# Fail-closed credential redaction in the echoed connection string (#240)

Status: **Implemented** (revision 3 — this describes what shipped; revisions 1 and 2 were
withdrawn under review, and "What changed and why" keeps the reasons, because they are the reasons
the final shape looks the way it does)

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
> things are: a key name; a value under a key on the allowlist; a URI's scheme, its authority after
> the userinfo, and its path; a bare `host:port`; an empty value; and the runs of separators
> between pairs.

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

Revision 2 tokenized by keys and was right about that, but got the levels wrong in two ways that
review caught in the code rather than on paper. It read whitespace as a separator everywhere, so the
tail of an unrecognised value was re-read as pairs and any of them under an allowlisted name was
printed — `Rotation Key=abc user=def` printed `def`. And it read a key as ending at the `=` with no
space allowed in between, so `RotationKey = hunter2` was not a pair at all: it was swallowed into the
previous value, printed in full, and — because the result equalled the input — printed with no note
saying anything had been hidden. Revision 3 confines nested pairs to values already recognised, lets
space sit on either side of the `=`, halves the backstop, and answers "may a pair begin here?" in
constant time rather than by walking backwards over the whitespace (which was quadratic: 2.1 s at
80 KB of it).

**The design that shipped tokenizes by keys rather than by delimiters.** It does not need to know which dialect
wrote the string, and it never has to decide whether a `;` is a separator or part of a password.

## The design

### 1. The keyword list stays, halved, and demoted to a backstop

```
(?<=(?:password|pwd|secret|token|accountkey|accesskey|apikey|signature)\s*=)[^;]*
```

It runs **first**, on the original string, and can only replace text with `***`, so it can only ever
redact more. That is what makes it impossible for this change to print something the previous
version hid.

It is not extending the blocklist — nothing is added to it, and it stops being the defence. It is
also **half the size it was**: the alternative that matched a URI's `user:pass@host`, and with it the
`(?:[^@/\s;]*|[^@/\s=]*)` alternation that three separate corrections went into, is deleted. The
scanner covers every shape that alternation caught and several it did not, so keeping it would have
pinned its non-obvious justification in the file forever for no coverage.

What the keyword half still earns: a conventional keyword sitting inside an **allowlisted** value
with nothing to mark it off. `Data Source=file:pwd=hunter2` has no separator before `pwd`, so the
scanner has no reason to read it as a pair and would print it. An unconditional `pwd=` finds it.

A timeout on the regex still returns `Unscannable`, and that path **short-circuits** — the sentinel
is returned without being scanned, since the scanner would recognise nothing in it and reduce it to
`***`.

### 2. The scanner

Over the string the backstop returned:

```
Level 1 — a pair may begin at the start of the string, or after one of  ;  &  ?  ,
          optionally followed by whitespace ("Host=h; Port=5432" is two pairs).
Level 2 — inside a value whose key is allowlisted, a pair begins after whitespace, and only
          there. Not a union with level 1: those separators have already split the string before
          any value was formed.
A key is  [A-Za-z_] then [A-Za-z0-9_.-], with single interior spaces where a key character
          follows, and then '=' not followed by '='.
A value runs from after its '=' to the start of the next key, less the separators between them.
Text before the first key is the prefix.
```

Keys and separator runs are copied verbatim, so a string with nothing hidden comes back
byte-identical and the caller can tell the two cases apart by comparing.

**The longest key wins.** In `Host=x Custom Port=5432` the short read finds the allowlisted `Port`
and prints `5432`; the long read finds `Custom Port`, which is recognised as nothing. Leftmost-longest
is the fail-closed reading, not a tidiness preference.

**Whitespace introduces a pair only at level 2**, and that is load-bearing. Reading a space as a
separator everywhere lets `Rotation Key=abc user=def` print `def` — not a username, but the tail of
a value nothing recognised. Confining it to values already recognised as safe is what makes libpq's
`host=h port=5432 password=hunter2` work without opening that hole. Level 2 does not treat the
value's own head as a key, or the key would run across the space in `host=db.internal port=5432`.

Level 2 is the last point at which anything is printed, so there is no recursion to bound. The whole
scan is one linear pass — which also answers the measurement that killed revision 1, whose mutual
recursion ran at O(n²) and died with an uncatchable `Stack overflow.` at 300 KB.

```
RedactValue(key, value):
    if value is empty            -> value        # an empty string cannot be a secret, and an
                                                 # emptied ${port} is the diagnosis this message
                                                 # exists to deliver
    if key is not allowlisted    -> "***"
    return the level-2 scan of value, with MaskAuthority over its head and each nested value

MaskAuthority(v):                                # an allowlisted key may still hold a URL
    at = the LAST '@' in v;  if none, or at index 0     -> v
    if no ':' occurs before it                          -> v   # 'UID=a@b.com' is an address
    keep a leading "scheme://" if one begins at or before that '@', else keep nothing,
    and replace everything up to the '@' with "***"

RedactPrefix(p):
    trim a trailing run of separators off p and put it back at the end
    if the core contains "://"                  -> return MaskUri(core)
    if the core is host ':' 1-5 digits, or '[' IPv6 ']' ':' digits  -> return p
    return "***"

MaskUri(u):
    masked = MaskAuthority(u)                    # the same rule; a "://" guarantees its ':'
    if a '?' remains in masked with anything after it  -> replace what follows it with "***"
```

ADO.NET's `==` escape is handled in exactly one place: the key rule declines `Host==x=hunter2`
because the `=` is doubled, so nothing in it is recognised and it reads as `***`. (Note this is the
one case where a *key* is hidden too — the real key there is `host=x`. Worth knowing, since keys are
otherwise always shown.)

Taking the **last** `@` and not stopping the authority at `/`, `?` or `#` is what covers a password
containing any of those. All three are legal unencoded in passwords people actually write, and a
rule that stopped at them printed the password whole in revision 1.

### 3. The allowlist

Compared with `OrdinalIgnoreCase` against the trimmed key — not `ToLower()`, which under `tr-TR`
maps the `I` of `Initial Catalog` to a dotless `ı` and breaks the lookup:

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
providers, is itself a generated account key — the no-corruption fixture prints `UID=a@b.com` into
`~/.aspire/logs` by design. And `data source`/`server` are safe as *values* in every dialect
checked (Oracle TNS descriptors, `tcp:host,1433`, named pipes, SQLite `file:` URIs); their danger
was always structural, which `MaskAuthority` and the key scan now cover.

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
- becomes: `" (a value is shown only under a key known to hold no secret; the rest read as ***, which
  does not mean they were secret)"`

The second clause is not padding. Reading the real messages showed that a template of ordinary,
unrecognised keys — `Host=localhost;Custom Port=5432;Encrypt=True` — gets `***` over a port number
and a boolean, and a note saying values were "not known to be safe" reads as an accusation about
them. Saying what the rule is, and that it is not a claim about the value, is the honest version.

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
- A bare hostname with no port — `localhost`, `orders-pg` — is not distinguishable from a token that
  merely looks like a word, so it reads `***`. The port is what makes `localhost:6379` recognisable,
  and requiring it is what stops an API key being printed on the grounds that it is shaped like a
  host.
- A key that is itself secret text (`hunter2=x`) prints the key. Keys are printed by construction;
  that is the ticket's own prescription and what makes the message diagnostic.
- The note now fires for pure over-redaction. A template of nothing but unrecognised keys gets
  `***` and the sentence explaining it, even though it held no credential at all — the developer is
  told something was hidden when the honest answer is "nothing here was recognised".
- A value under an allowlisted key that contains ` word=` is read as nested pairs, so
  `Data Source=a b=c` reads `Data Source=a b=***`. Fail-closed, mildly odd, and the price of libpq.
- Space around a `=` is layout and is preserved, but a key is still only ASCII letters, digits and
  `_ . -` with single interior spaces. A dialect writing a key outside that charset would not be
  read as a pair, and its value would be swallowed by the pair before it — the shape that leaked
  `RotationKey = hunter2` before the space rule landed. Nothing known writes such a key, and the
  keyword backstop covers the conventional names regardless.

## Tests

`AKeywordThatOnlyLooksLikeACredential_IsNotRedacted` does not survive as it was: it asserts a
blocklist property (the lookbehind anchors on `=`, so `SharedAccessKeyName` is not caught) that
ceases to exist. Three of its four rows now redact, so it is rehomed to the unit tests as
`AKeywordThatOnlyLooksLikeACredential_IsMaskedAnyway` with those three rows and their expectations
inverted — the knowing cost of the inversion, stated as a test. Its fourth row is rehomed as the no-corruption
test. `AConnectionStringWithNoCredential_IsEchoedUntouched` is left **untouched** — it is the best
regression guard for the ordinary case.

| Input | Expected |
|---|---|
| `Host=db.internal;Port=5432;Username=dev;Password=hunter2` | `Password=***`, rest intact |
| `Host=db.internal;Port=5432;Pwd=hunter2` | `Pwd=***` |
| `postgresql://orders_app:hunter2@db.internal:5432/orders` | `postgresql://***@db.internal:5432/orders` |
| `x:y@a://b` | `***@a://b` — an `@` before the scheme resolves no authority, so none of it prints |
| `redis://:hunter2@db.internal:6379` | `redis://***@db.internal:6379` |
| `redis://user:pa;ss@db.internal:6379` | `redis://***@db.internal:6379` |
| `mongodb://user:p;w@db.internal:27017` | `mongodb://***@db.internal:27017` |
| `Endpoint=sb://ns…/;SharedAccessKeyName=root;SharedAccessKey=hunter2` | all three values `***`, all three keys shown |
| `BlobEndpoint=https://acct…/;SharedAccessSignature=sv=2021&sig=hunter2` | both values `***` (the keyword backstop takes the whole signature before `&sig=` is ever read as a pair) |
| `Host=h;Rotation Key=hunter2` | `Rotation Key=***` — the fail-closed case no blocklist would name |
| `host=db.internal port=5432 user=dev password=hunter2` | `password=***`, the rest intact |
| `mongodb://db.internal:27017;Password=hunter2` | host printed, `Password=***` |
| `tcp://db.internal:1433;UID=a@b.com;Password=hunter2` | host and `UID` intact, `Password=***` |
| `jdbc:postgresql://user:pw@h:5432/db?ssl=true` | `jdbc:postgresql://***@h:5432/db?ssl=***` |
| `redis://user:pa#ss@db.internal:6379` | `redis://***@db.internal:6379` |
| `postgresql://app:8Kx/2Qz+w7A=@db.internal:5432/orders` | `postgresql://***@db.internal:5432/orders` |
| `Host==x=hunter2` | `***` — the `==` escape fails closed, key included |
| `Rotation Key=abc user=def` | `Rotation Key=***` — a nested pair does not escape an unrecognised value |
| `Host=x Custom Port=5432` | `Host=x Custom Port=***` — longest key wins |
| `Host=localhost;RotationKey = hunter2;Database=orders` | `RotationKey = ***` — space around `=` hides nothing |
| `Host = localhost;Port = 5432;Database = orders` | untouched, spacing and all |
| 30 000 copies of `Host=h; ` | returns in well under a second — the scan is linear in whitespace too |
| `Data Source=file:pwd=hunter2` | `Data Source=file:pwd=***` — the row the backstop exists for |
| `localhost:6379,ssl=false` | `localhost:6379,ssl=***` |
| `localhost` | `***` |
| `Host=db.internal;Integrated Security=SSPI;Database=orders` | `Integrated Security=***` — the knowing cost of the inversion |
| `Data Source=user:hunter2@h:1433` | `Data Source=***@h:1433` |
| `redis://h:6379/0?password=hunter2` | `?password=***` — regression guard, the blocklist catches this today |
| `Data Source=tcp://db.internal:1433;UID=a@b.com;Database=orders` | intact — no corruption |
| `Host=h;Password='a;b';Database=orders` | `Password=***`, no phantom pair, `Database=orders` intact |
| `Host=db.internal;Port=5432;Database=orders` | untouched, no `***`, no note |
| `Host=localhost;Port=;Database=orders` | untouched — the shell-expansion diagnosis |
| `Host=h;Custom Port=` | untouched — an empty value is never masked |
| `localhost:6379` | untouched — Redis/Kafka's own shape must not collapse to `***` |
| `hunter2` | `***` |
| a 300 KB pathological string | returns; a regression here would take the test host down rather than redden one test, so this row guards the suite |
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
