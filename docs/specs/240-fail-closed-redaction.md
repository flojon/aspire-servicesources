# Fail-closed credential redaction in the echoed connection string (#240)

Status: **Draft** (revision 2 — revision 1's algorithm was withdrawn; see "What changed and why")

## The problem

`KubernetesBackingServiceSource.NothingAddressesTheTunnel` is the one message in this package that
echoes a whole, valid connection string back to the developer. It reaches `~/.aspire/logs` and gets
pasted into issues, so it redacts what it echoes.

It redacts by **blocklist**: a regex naming the keywords a secret is usually written under
(`password`, `pwd`, `secret`, `token`, `accountkey`, `accesskey`, `apikey`, `signature`) plus a
URI's `user:pass@host` authority. A keyword the list does not name is printed whole.

The list has been corrected three times since it was written, each time by someone finding a shape
it missed. Every fix was correct; the pattern is that the list is never finished, and every gap is a
printed password.

## The invariant

Everything below exists to hold one property, and any change to it must be checked against the
property rather than against the procedure:

> **Nothing is printed unless it has been positively recognised as safe to print.** The recognised
> things are: a key name, a value under a key on the allowlist, a URI's scheme, its authority after
> the userinfo, and its path.

Revision 1 failed because it stated a procedure and not a property. Six shapes were found where the
procedure printed a password in full — each one a string that used a separator the procedure did not
tokenize on.

## What changed and why

Revision 1 split the string on `;` and dispatched on whether a `://` sat at position 0. A reviewer
implemented that pseudocode and ran it; these printed the secret verbatim, and the current blocklist
catches all but two of them:

| Input | Revision 1 printed |
|---|---|
| `host=db.internal port=5432 user=dev password=hunter2` (libpq conninfo) | the whole string |
| `mongodb://db.internal:27017;Password=hunter2` (no path, so the authority swallowed the tail) | the whole string |
| `tcp://db.internal:1433;UID=a@b.com;Password=hunter2` | `tcp://***@b.com;Password=hunter2` |
| `jdbc:postgresql://user:pw@h:5432/db?ssl=true` | the whole string, as a "key" |
| `redis://user:pa#ss@db.internal:6379` | the whole string |
| `postgresql://app:8Kx/2Qz+w7A=@db.internal:5432/orders` | the whole string |
| `Host==x=hunter2` (ADO.NET's `==` key escape) | the whole string |

They share one cause. Fail-closed-by-key is only fail-closed if the tokenizer that finds the keys is
at least as permissive as every tokenizer that could have written the string. Revision 1 knew two
separators; the real strings used a space, an end-of-string, a raw sub-delim inside a userinfo, and
a doubled `=`.

**Revision 2 tokenizes by keys rather than by delimiters.** It does not need to know which dialect
wrote the string, and it never has to decide whether a `;` is a separator or part of a password.

## The design

### 1. The blocklist stays, demoted to a backstop

The existing `Credentials` regex runs **first**, unchanged, on the original string. It can only
replace text with `***`, so it can only ever redact more.

This is not extending the blocklist — nothing is added to it, and it stops being the defence. It is
kept because it makes one guarantee that the new scanner cannot make on its own: **no shape that is
redacted today can be printed after this change.** A tokenizer can always be surprised by a dialect
nobody modelled; an unconditional `(?<=password\s*=)[^;]*` cannot.

Because the regex stays, so do its `TimeSpan.FromSeconds(1)` timeout, the
`RegexMatchTimeoutException` catch and the `Unscannable` sentinel. Revision 1 proposed deleting all
three on the grounds that removing the regex removed the last ReDoS surface. It did not: revision 1's
own mutual recursion measured **O(n²)** (1.6 s at 48 KB, 5.8 s at 96 KB) and died with an uncatchable
`Stack overflow.` at 300 KB, which would take the AppHost down instead of reporting the
configuration error it was building. Revision 2's scanner is a single non-recursive linear pass, and
the timeout still bounds the backstop.

### 2. The scanner

Over the string the backstop returned:

```
A key-start is:  a delimiter, then a key, then '=' not followed by '='
  delimiter  = start-of-string | ';' | '&' | '?' | ',' | ASCII whitespace
  key        = [A-Za-z_] [A-Za-z0-9_.-]* with single interior spaces allowed
A pair's value runs from after its '=' to the delimiter that introduces the next key-start,
or to end-of-string.
Text before the first key-start is the prefix.
```

Output is `RedactPrefix(prefix)` followed by, for each pair, its separator text verbatim, its key
verbatim, `=`, and:

```
RedactValue(key, value):
    if value is empty            -> value        # an empty string cannot be a secret, and an
                                                 # emptied ${port} is the diagnosis this message
                                                 # exists to deliver
    if value starts with '='     -> "***"        # ADO.NET's '==' escape: the key was not the key
    if key is not allowlisted    -> "***"
    return MaskAuthority(value)                  # an allowlisted key may still hold a URL
```

```
MaskAuthority(v):                                # linear, non-recursive
    find the last '@' in v
    if none                      -> v
    if no ':' occurs before it   -> v            # 'UID=a@b.com' is an address, not an authority
    return "***" + v[from that '@']              # 'Data Source=user:pw@h:1433' -> '***@h:1433'
```

```
RedactPrefix(p):
    if p is empty or only delimiters  -> p
    if p contains "://"
        mask the userinfo: the LAST '@' in p, everything before it back to the "://" becomes "***"
        of what remains, everything up to the first '?' is scheme + authority + path -> printed
        anything after that '?' other than delimiters is unvetted query text          -> "***"
    if p matches  host ':' 1-5 digits  (or '[' IPv6 ']' ':' digits)  -> p
    return "***"
```

The prefix is where a whole-string URI lands, because a URI has no `key=` in it. Taking the **last**
`@` rather than the first, and not stopping the authority at `/`, `?` or `#`, is what covers a
password containing any of those — the three shapes revision 1 leaked.

### 3. The allowlist

Compared with `OrdinalIgnoreCase` against the trimmed key — not `ToLower()`, which under `tr-TR`
maps the `I` of `Initial Catalog` to `ı` and breaks the lookup:

`host`, `server`, `data source`, `port`, `database`, `initial catalog`, `user`, `user id`, `userid`,
`username`, `uid`, `driver`, `provider`

This is the ticket's list plus `user id`/`userid`. The ticket's list carries `uid`, `user` and
`username` but not SqlClient's canonical spelling of the same concept, so `User ID=sa` would be
masked while `UID=sa` survived. That is a gap in assembling the list across dialects rather than a
judgement, and closing it adds no new judgement.

Deliberately left off:

- `endpoint` / `blobendpoint` — an endpoint URL is where an Azure SAS token is written
  (`https://acct.blob.core.windows.net/?sv=…&sig=…`). Allowlisting it fails open again.
- `integrated security`, `encrypt`, `sslmode`, `timeout` and friends — inert in fact, but nothing in
  this message needs them, and each addition is the first step of the same slide. They read as
  `***`.

Two honest caveats. `uid`/`user`/`username` print an identifier that is PII and, for some managed
providers, is itself a generated account key — the AC4 fixture prints `UID=a@b.com` into
`~/.aspire/logs` by design. And `data source`/`server` are safe as *values* in every dialect
checked (Oracle TNS descriptors, `tcp:host,1433`, named pipes, SQLite `file:` URIs); their danger
was always structural, which `MaskAuthority` and the key scanner now cover.

## The open question the ticket raises, answered

> Worth checking while doing it whether the message needs values at all, or whether keys plus
> "this one is empty" would diagnose both cases just as well.

**Keep the values.** Revision 1 argued this and got the reasons wrong; the conclusion survives on
different grounds.

What is *not* true: that the echo shows "no `${port}` is present". This exception is thrown only
once `ports == 0` is already established, and the message says so in prose — the reader knows the
placeholder is absent before reading the echo. Nor is keys-only "more machinery": it needs no
allowlist at all, so it is strictly smaller, and `Key=` versus `Key=***` distinguishes empty from
non-empty for free.

What is true, and decides it:

- The dominant case is a template with no credential at all, which today is echoed verbatim — the
  existing code goes out of its way not to append its note in that case, precisely so the developer
  is not left wondering which part was hidden. Keys-only puts `***` into *every* one of these
  messages.
- The message already hard-codes a worked example (`'Host=localhost;Port=${port};Database=orders'`).
  A keys-only echo adds almost nothing over that example, so keys-only is not really a middle option
  — it collapses into "do not echo at all", and the echo is what makes the shell-expansion case
  diagnosable.

So the real fork is echo-with-values or no echo, and the echo earns its place.

## Consequences for the message

`***` no longer implies a credential was found; it also appears for an unrecognised key. The note
must stop asserting one. Pinned wording, so the tests can assert it:

- now: `" (a credential in it shown as ***)"`
- becomes: `" (values not known to be safe to print are shown as ***)"`

Its guard keeps the same two arms — `shown == connectionString || shown == Unscannable` — so an
all-allowlisted template still quotes back exactly what the developer wrote, with no note. That
requires the reassembly to be **byte-identical** when nothing is replaced: keys and separator runs
are copied verbatim, and nothing is trimmed or normalised on the way out.

## Comments

The 35 lines of `<remarks>` on `Credentials` and `Redacted` document the blocklist's reasoning,
including "this narrows the blast radius rather than closing it" — the claim this change reverses.
They are replaced by remarks stating the invariant, why the backstop is kept, and the residues
below. Per this repo's convention they describe what the code does, not what this PR changed.

## Stated residues

Not closed, and said out loud rather than left to be discovered:

- A path or fragment containing `@` over-redacts: `redis://db.internal:6379/@notauser` becomes
  `redis://***@notauser`. Fail-closed, and the alternative reopens the password-with-`/` leak.
- A comma-delimited option list whose first token is not a pair — `localhost:6379,ssl=false` — has an
  unvettable prefix, so it reads `***,ssl=***`.
- A key that is itself secret text (`hunter2=x`) prints the key. Keys are printed by construction;
  that is the ticket's own prescription and what makes the message diagnostic.
- A value containing ` word=` is re-tokenized, so `Password=my token=x` reads `Password=***;token=***`
  rather than one masked value. Fail-closed, mildly odd.

## Tests

`AKeywordThatOnlyLooksLikeACredential_IsNotRedacted` is **deleted**, not rewritten: it asserts a
blocklist property (the lookbehind anchors on `=`, so `SharedAccessKeyName` is not caught) that
ceases to exist. Three of its four rows now redact. Its fourth row is rehomed as the no-corruption
test. `AConnectionStringWithNoCredential_IsEchoedUntouched` is left **untouched** — it is the best
regression guard for the ordinary case.

| Input | Expected |
|---|---|
| `Host=db.internal;Port=5432;Username=dev;Password=hunter2` | `Password=***`, rest intact |
| `Host=db.internal;Port=5432;Pwd=hunter2` | `Pwd=***` |
| `postgresql://orders_app:hunter2@db.internal:5432/orders` | `postgresql://***@db.internal:5432/orders` |
| `redis://:hunter2@db.internal:6379` | `redis://***@db.internal:6379` |
| `redis://user:pa;ss@db.internal:6379` | `redis://***@db.internal:6379` |
| `mongodb://user:p;w@db.internal:27017` | `mongodb://***@db.internal:27017` |
| `Endpoint=sb://ns…/;SharedAccessKeyName=root;SharedAccessKey=hunter2` | all three values `***`, all three keys shown |
| `BlobEndpoint=https://acct…/;SharedAccessSignature=sv=2021&sig=hunter2` | both values `***` |
| `Host=h;Rotation Key=hunter2` | `Rotation Key=***` — the fail-closed case no blocklist would name |
| `host=db.internal port=5432 user=dev password=hunter2` | `password=***`, the rest intact |
| `mongodb://db.internal:27017;Password=hunter2` | host printed, `Password=***` |
| `tcp://db.internal:1433;UID=a@b.com;Password=hunter2` | host and `UID` intact, `Password=***` |
| `jdbc:postgresql://user:pw@h:5432/db?ssl=true` | `jdbc:postgresql://***@h:5432/db?ssl=***` |
| `redis://user:pa#ss@db.internal:6379` | `redis://***@db.internal:6379` |
| `postgresql://app:8Kx/2Qz+w7A=@db.internal:5432/orders` | `postgresql://***@db.internal:5432/orders` |
| `Host==x=hunter2` | `***` — the `==` escape fails closed |
| `Data Source=user:hunter2@h:1433` | `Data Source=***@h:1433` |
| `redis://h:6379/0?password=hunter2` | `?password=***` — regression guard, the blocklist catches this today |
| `Data Source=tcp://db.internal:1433;UID=a@b.com;Database=orders` | intact — no corruption |
| `Host=h;Password='a;b';Database=orders` | `Password=***`, no phantom pair, `Database=orders` intact |
| `Host=db.internal;Port=5432;Database=orders` | untouched, no `***`, no note |
| `Host=localhost;Port=;Database=orders` | untouched — the shell-expansion diagnosis |
| `Host=h;Custom Port=` | untouched — an empty value is never masked |
| `localhost:6379` | untouched — Redis/Kafka's own shape must not collapse to `***` |
| `hunter2` | `***` |
| a 300 KB pathological string | returns, does not overflow the stack |
| the note wording when only an unknown key was masked | does not call it a credential |

## Decided, not open

- **CHANGELOG: no entry.** #144/#234 sits in `[Unreleased]`, so nothing that has shipped changes,
  and its `### Added` entry says nothing about redaction that becomes wrong.
- **Query parameters are in scope, and required.** The current blocklist already redacts
  `?password=hunter2`, so omitting them would make the fail-closed design leak something the
  fail-open one caught.
- **`GitUrl.Redact`/`GitUrl.RedactAll` are not reused.** They delete the userinfo rather than mask
  it, end the authority at `/` only, and answer a different question (a git remote URL, never a
  keyword-pair string). `GitCommand` does not use this code and does not have this problem.
