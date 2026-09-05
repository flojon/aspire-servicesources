# Fail-closed credential redaction in the echoed connection string (#240)

Status: **Draft**

## The problem

`KubernetesBackingServiceSource.NothingAddressesTheTunnel` is the one message in this package that
echoes a whole, valid connection string back to the developer. It reaches `~/.aspire/logs` and gets
pasted into issues, so it redacts what it echoes.

It redacts by **blocklist**: a regex naming the keywords a secret is usually written under
(`password`, `pwd`, `secret`, `token`, `accountkey`, `accesskey`, `apikey`, `signature`) plus a
URI's `user:pass@host` authority. A keyword the list does not name is printed whole.

The list has been corrected three times since it was written, each time by someone finding a shape
it missed — `redis://:pass@host`, `SharedAccessKey=`, then `SharedAccessSignature=`, then a `;`
inside a URI password. Every fix was correct; the pattern is that the list is never finished, and
every gap is a printed password.

## The change

Invert the default. Redact the value of every `key=value` pair **unless** its key is on an
allowlist of keys known to carry no secret, and redact a URI's userinfo **wholesale** rather than
trying to find the password inside it.

An unknown key then reads as `Key=***` — mildly annoying, never dangerous — instead of being
printed.

## The open question the ticket raises, answered

> Worth checking while doing it whether the message needs values at all, or whether keys plus
> "this one is empty" would diagnose both cases just as well.

**Keep the values.** The message has two jobs, and the allowlist already serves both with the
values it keeps:

1. *Show that no `${port}` is present.* The developer reads the string back and sees there is no
   placeholder in it. Keys alone would show `Port` exists but not what is in it, which is the whole
   question.
2. *Show what a shell left behind when it expanded a `${port}` away.* This arrives as
   `Host=localhost;Port=;Database=orders` — an **empty value under an allowlisted key**. Printing
   the value shows this directly. A keys-only message would need a bolt-on "this one is empty"
   annotation to say the same thing: strictly more machinery, conveying strictly less.

So values-under-allowlisted-keys is both the safer and the smaller design. A keys-only message is
rejected.

## The algorithm

Two syntaxes reach this method, and each ends an authority differently. That difference is the one
thing the previous three corrections kept colliding with, so it is stated once and used
consistently:

- **Keyword syntax** (`Host=h;Port=5432`) — an unquoted `;` is a pair separator. A value containing
  a literal `;` must be quoted, so splitting on `;` is safe and a `;` never continues a value.
- **URI syntax** (`redis://u:p@h:6379`) — RFC 3986 puts `;` in `sub-delims`, which `userinfo`
  admits raw. A `;` does **not** end an authority; only `/`, `?`, `#` or end-of-string do.

Which syntax applies is decided by where the `://` sits: at the very start of the string (a bare
URI) or after a `key=` (a URI-valued keyword).

```
Redacted(s):
    if s starts with <scheme>"://"   (after leading whitespace; scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ))
        return RedactUri(s)
    return join(';', s.Split(';').Select(RedactPair))

RedactPair(token):                      # token is ';'-free by construction
    if token is blank                   -> token           # a trailing ';' stays a trailing ';'
    i = token.IndexOf('=')
    if i < 0                            -> "***"           # fail closed: an unquoted ';' inside a
                                                           # value lands its tail here
    key, value = token[..i], token[(i+1)..]
    if key.Trim() is not allowlisted    -> key + "=***"
    return key + "=" + RedactUri(value)                    # an allowlisted key may still hold a URI

RedactUri(s):
    if s has no "://" at a scheme position -> s
    authority = from after "://" to the first '/', '?' or '#', else end of string
    if authority contains '@'
        authority = "***" + authority[from its LAST '@']    # userinfo wholesale, username included
    rest = whatever followed the authority
    # A bare URI may carry a ';'-separated keyword tail (Azure writes 'Endpoint=sb://...' the other
    # way round, but nothing stops the reverse). The first piece of the rest is the URI's own
    # path/query; the pieces after it are keyword pairs and go through RedactPair.
    pieces = rest.Split(';')
    pieces[0] = RedactQuery(pieces[0])
    pieces[1..] = RedactPair(each)
    return prefix + authority + join(';', pieces)

RedactQuery(p):                          # p is the path, optionally '?' and a query
    if p has no '?'                     -> p
    return path + '?' + join('&', query.Split('&').Select(RedactPair))
```

`RedactPair` is reused for query parameters deliberately: `?password=x` is the same fail-open gap in
a different separator, and giving it the same rule means there is one allowlist to reason about.

### The allowlist

Exactly the ticket's, compared case-insensitively against the trimmed key:

`host`, `server`, `data source`, `port`, `database`, `initial catalog`, `user`, `username`, `uid`,
`driver`, `provider`

Deliberately minimal. Every addition is a fresh judgement that a key can never carry a secret, and
the accumulation of such judgements is what this change exists to stop. Two keys that might look
like candidates are left off on purpose:

- `endpoint` / `blobendpoint` — an endpoint URL is where an Azure SAS token is written
  (`https://acct.blob.core.windows.net/?sv=…&sig=…`). Allowlisting it would fail open again.
- `integrated security` — inert in fact, but nothing about this message needs it, and it is the
  first step of the same slide. It reads as `Integrated Security=***`.

Note the asymmetry with URIs: `UID=dev` keeps its value, while `postgresql://dev:p@h` loses the
username too. That is intended — inside a userinfo you cannot reliably tell a username from a
password (`redis://:pass@h` has only the latter), and guessing is what the wholesale rule replaces.

## Consequences for the message

`***` no longer implies a credential was found; it also appears for an unrecognised key. The note
the message appends must stop asserting one:

- now: `(a credential in it shown as ***)`
- becomes: something that says a value was hidden, not that it was secret — e.g.
  `(values this package cannot vouch for are shown as ***)`.

The condition guarding the note is unchanged: append it only when something was actually replaced,
so an ordinary all-allowlisted template still quotes back exactly what the developer wrote.

## What is removed

`Redacted` becomes plain string scanning: no regex, so no `RegexMatchTimeoutException`, so the
`Unscannable` sentinel and the `catch` that returns it are dead and go with it. The caller's
`shown == Unscannable` arm goes too. This also removes the last ReDoS surface in this file — the
motivation for the 1-second timeout in the first place.

## Tests

Rewriting the three existing redaction theories, since the contract they encode is the one being
replaced. Every shape the prior corrections found stays covered:

| Input | Expected |
|---|---|
| `Host=db.internal;Port=5432;Username=dev;Password=hunter2` | `Password=***`, rest intact |
| `Host=db.internal;Port=5432;Pwd=hunter2` | `Pwd=***` |
| `postgresql://orders_app:hunter2@db.internal:5432/orders` | `postgresql://***@db.internal:5432/orders` — username gone too |
| `redis://:hunter2@db.internal:6379` | `redis://***@db.internal:6379` |
| `redis://user:pa;ss@db.internal:6379` | `redis://***@db.internal:6379` |
| `mongodb://user:p;w@db.internal:27017` | `mongodb://***@db.internal:27017` |
| `Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=hunter2` | all three values `***`, all three keys shown |
| `BlobEndpoint=https://acct…/;SharedAccessSignature=sv=2021&sig=hunter2` | both values `***` |
| **new** `Host=h;Rotation Key=hunter2` (a key no blocklist would name) | `Rotation Key=***` — the fail-closed case |
| **new** `redis://h:6379/0?password=hunter2` | query value `***` |
| `Data Source=tcp://db.internal:1433;UID=a@b.com;Database=orders` | intact — no corruption |
| `Host=db.internal;Port=5432;Database=orders` | untouched, no `***` in the message |
| **new** `Host=localhost;Port=;Database=orders` | untouched — the shell-expansion diagnosis |
| **new** the note wording when only an unknown key was masked | does not call it a credential |

## Open questions

1. Should the CHANGELOG say anything? #144/#234 sits in `[Unreleased]`, so nothing that has shipped
   changes. Proposal: no new entry; add one sentence to the existing #144 `### Added` entry noting
   that the echoed connection string shows values under unrecognised keys as `***`. (A reader
   pasting a failure into an issue benefits from knowing what was masked.)
2. Is `RedactPair`-on-query-parameters beyond the ticket's letter? It is not in the ticket's
   prescription, but `?sig=` is the identical fail-open gap. Included; flag it in the PR.
