# Backing-service diagnostics: naming, orphaned config, and the placeholder syntax

Design for #200, #206 and #207 — the three findings left open by stage 1 of #144 (#199).

## Why the three are one change

They were filed separately and they are separate defects, but they share a deadline and, in two
cases, a mechanism.

The deadline is that **none of this has shipped**. The last tag is `v0.4.0`; `AddBackingService`,
the `"direct"` source and the `{port}` / `{secret:…}` placeholder syntax are all still under
`## [Unreleased]`. Every option below that would be a breaking change after 0.5.0 is free today, and
two of the three issues weigh their options explicitly against that cost. Settling them together,
before the release, is what makes the cheap answers available at all.

The mechanism is shared between #200 and #206: both reach for a place to put a message that is not
an exception. #200 turns out not to need one — see the decision below — but #206 does, and the
existing `ServiceConfigurationWarnings` is shaped around `(service, source, capability)` skip
records rather than free text.

## Decisions

### #200 — fail fast when the `"local"` factory misnames its resource

`AddBackingService` throws when the resource the factory returns is not named after the backing
service.

The defect is that Aspire's `WithReference(db)` keys the connection string on the *referenced
resource's* own name. Under `"direct"` that resource is the one this package adds, named after the
backing service; under `"local"` it is whatever the AppHost's factory built. So a factory returning
`AddDatabase("orders")` gives the consumer `ConnectionStrings__orders`, while every other source
gives it `ConnectionStrings__orders-db`, and switching source moves the key the app reads. The
AppHost is happy either way. The app reports it, by starting and finding nothing.

Documenting it — the state before this change — is enough for a reader who finds the guidance and
nothing at all for one who does not. Warning about it is worse than it looks: the remedy a warning
would name has to be one the reader can act on, and in a guest language there is only one. C# can
settle it from the consumer's side with `WithReference(source, connectionName)`; the generated
TypeScript shim takes no such argument, because ATS erases overloads and
`ServiceConfigurationExports.WithServiceConnectionString` exports the one-argument shape (#209).
Renaming the factory's resource is therefore not the *recommended* remedy for a guest-language
AppHost, it is the only one — and a rule with exactly one remedy is a rule, not advice.

Making the key stable ourselves, by surfacing the factory's resource under the backing service's
name, was rejected: it needs a wrapper resource or a rename, which reintroduces the facade #62
deliberately removed.

The comparison is `Ordinal`. It was written as `OrdinalIgnoreCase` on the reasoning that
configuration keys fold case, which is true of .NET's `IConfiguration` and of nothing else in reach:
this package runs JavaScript and Java services, and `process.env` and `System.getenv` are both
case-sensitive. A folded comparison would admit exactly the key move this check exists to prevent,
narrowed to casing and therefore harder to see. Both names are literals in the AppHost's own code,
so requiring them to agree exactly costs the author nothing.

The check runs only in the `"local"` branch, because only that branch invokes the factory. That is
the right coverage rather than a gap: `"local"` is what an AppHost nobody has configured resolves
to, so the check fires on the default path and a misnamed factory cannot reach a user undetected.

### #206 — warn on backing-service config that nothing reads

Two spellings produce the same silence, and both end with a database container the developer was
trying to avoid:

- an entry key that matches no `AddBackingService()` call — `orders_db` against
  `AddBackingService("orders-db", …)`. It binds, it validates, nothing looks it up. `orders-db` has
  no entry, no entry means `"local"`, and `"local"` runs the factory.
- a misspelled `backingServices` root key, which does that to every backing service at once. Filed
  as #201, closed in favour of #206 because the fix is the same.

Neither can be an error. A shared `servicesources.local.json` may legitimately carry entries for
backing services only some configurations add, which is the same reason the service side validates
every entry without requiring each to be used. So both are warnings, and both need a channel.

Scope is backing services only. The service side has the same hole — an entry naming no catalog
service is equally unread — but that section has shipped and developers have files for it, so
widening this change onto it is a separate decision with a larger blast radius.

No opt-out. Whether deliberately-unused entries are common is exactly what the warning will find
out, and designing for it first is designing without the answer.

No publish-mode branch. An orphaned entry is as wrong under `aspire publish` as under `aspire run`,
and nothing in the implementation needs to know which one it is in.

### #207 — placeholders become `${port}` and `${secret:<name>:<key>}`

The defect is that `{…}` reserves a shape a connection string already uses. A token whose word up to
the first colon is `port` or `secret`, in any casing, is always read as a placeholder, so
`Driver={SQL Server};UID=sa;PWD={secret}` — ODBC, where braces quote a value, and the password
happens to be the word — cannot be written at all. It fails loudly, which is why #207 is not urgent,
but the developer is stuck.

Three answers were weighed.

**Doubling the brace** was tried in #199 and withdrawn, and the reasoning holds: ODBC has its own
doubling rule, so `PWD={pa}}ss}` is the password `pa}ss`, and collapsing that `}}` yields a string
the driver reads as ending at the brace — the app connects with `pa` and trailing rubbish. `{{`
fares no better, since ODBC does not require doubling `{`, so `PWD={{abc}` is the password `{abc`
and collapsing drops a character. Rewriting a working connection string silently is worse than being
unable to write a rare one.

**Scoped doubling** — collapsing `{{` only where the token it opens would otherwise be a placeholder
— repairs both of those cases, since neither `pa}}ss` nor `{{abc}` is followed by a keyword. It is a
real option and it carries no deadline. It was rejected for a narrower case it still gets wrong:
`PWD={{port}}}` is ODBC for the password `{port}`, and under a scoped rule it would be rewritten to
`PWD={port}}` — a password of `port}` — silently. Today that string fails loudly. Trading a loud
failure for a silent rewrite is the exact trade the withdrawal above refused.

**Changing the syntax** avoids all of it, because braces stop being reserved. `${` is not a sequence
any connection-string dialect uses, so `Driver={SQL Server};UID=sa;PWD={secret}` parses as plain
text with no escape anywhere, and the motivating case needs no remedy because it is no longer a
problem. This is the option that is only cheap before 0.5.0 ships.

The reserved shape shrinks rather than vanishing: a literal `${port}` is still unwritable. That
stays documented, along with the fact that `$` is not otherwise special, so `$${port}` remains
available the day something wants it — which is a cheaper sentence to write than the escape it
describes.

**Unknown `${…}` stays literal.** Only a token whose first word is exactly `port` or `secret` is
claimed; `${DB_PASS}` passes through as text. Rejecting unknown `${…}` as a misspelled placeholder
would read well and would give near-miss suggestions a home, but it breaks an AppHost whose own
tooling expands `${…}` in a connection string, for no gain this package needs.

## Design

### Placeholder syntax (#207)

`ConnectionStringTemplate.Parse` scans for `$` followed by `{` in place of a bare `{`. The rest of
the rule is unchanged: read the body up to the first `}`, split on `:`, claim the token only when
`parts[0]` equals `port` or `secret` under `OrdinalIgnoreCase`, and fail on a token that is claimed
but unreadable. A `$` not followed by `{`, and a `${` whose first word is not a keyword, are literal
text; the scan resumes one character past the `$` so that `${a${port}` still finds the placeholder
inside it, mirroring the existing brace behaviour.

`Segment.AsWritten` and the `Token` properties keep quoting the spelling the developer wrote, for
the reason already recorded on them: the keyword is matched case-insensitively, so a token rebuilt
from the constants would quote a spelling that appears nowhere in the developer's file.

`ConnectionStringTemplate.AppendLiteral` is untouched. Its `{`→`{{` doubling is about
`string.Format` inside `ReferenceExpression`, not about this package's syntax, and the reasoning on
it stands unchanged.

Three error messages lose their "there is no escape" paragraph — `ConnectionStringTemplate.Malformed`
and the two in `DirectBackingServiceSource` that reject an unresolvable `Port` or `Secret`. A
malformed `${…}` is now unambiguously an attempt at a placeholder, so the message says what is wrong
with it and stops. The remarks on `ConnectionStringTemplate` keep the reasoning, rewritten: what the
syntax reserves, why braces are no longer part of it, and why doubling was not the answer.

### Fail fast on a misnamed local resource (#200)

In `AddBackingService`'s `"local"` branch, after the existing null check:

```csharp
if (!string.Equals(resource.Resource.Name, name, StringComparison.OrdinalIgnoreCase))
{
    throw MisnamedLocalResource(name, resource.Resource.Name);
}
```

The message names the backing service, the resource the factory actually returned, and one remedy:
rename the resource to the backing service's name, with `AddDatabase("orders-db", "orders")` shown
for the case where the Aspire resource and the database itself want different names. It does not
offer `WithReference(source, connectionName)`. That overload is real and remains a legitimate way to
choose a different key deliberately, but it does not exist in a guest language, and an error whose
fix is unavailable to the reader is worse than the silence it replaced.

The XML doc on the `local` parameter changes from guidance to a statement of the rule, and its
second paragraph — the consumer-side remedy — is reframed as what it now is: a way to choose a
different connection name on purpose, not a way out of this constraint.

### The warnings channel and the orphan audit (#206)

**The channel.** `ServiceConfigurationWarnings` buffers `Entry` records instead of `Skip` records:

```csharp
private abstract record Entry;
private sealed record Skip(string ServiceName, string Source, string Capability) : Entry;
private sealed record Message(string Text) : Entry;
```

Each entry carries a `Reported` flag, which is what makes reporting exactly-once however many
handlers ask for it. A single "how far have we got" index would do for `Flush` alone, and does not
survive `ReportNow` below, which has to report one entry while leaving the entries around it
outstanding. `Describe` groups the `Skip`s by `(service, source)` as it does today and passes
`Message` texts through in the order they were added; skips are described first, so the grouping is
unaffected by interleaving.

`ReportNow(services, messages)` reports just its caller's messages and leaves everything else
buffered. The audit needs it rather than `Flush`: its handler is subscribed at the first
`AddBackingService`, usually one of an AppHost's first lines, so it runs before `UrlSource` records
the dropped wait that belongs in the same grouped message as that service's earlier skipped
`Configure` calls. A `Flush` there empties the buffer in between and splits one message into two,
undoing the subscription ordering `UrlSource` arranges on purpose.

The class is renamed `ServiceSourcesWarnings`. It is internal, the rename is mechanical across the
eight files that name it — five in `src`, three in `test` — and "service configuration" stops being
true once it carries a message about a backing service.

**Recording what was declared.** A `ConditionalWeakTable<IDistributedApplicationBuilder, …>` records
the names `AddBackingService()` was called with, following the shape the other per-builder caches in
this package use — a side-effect-free factory, with the work done behind a lock on the instance the
table actually kept.

**The audit.** `AddBackingService` subscribes once per builder to `BeforeStartEvent`. The handler
diffs the configured entry keys, from `ServiceSourcesConfigCache.BackingServicesFor(builder)`,
against the declared names under `OrdinalIgnoreCase` — the entry key is a configuration key, which
does fold case, unlike the environment variable in #200 — and reports a message for anything left
over through `ReportNow`.

The root-key check is asked unconditionally rather than gated on the bound section being empty. That
section is the *merged* view across every configuration layer, so gating on it meant a single
environment variable setting one entry suppressed the report that the developer's whole file was
going unread. The two questions are independent, and the near-miss lookup already answers with
nothing when the file has the key, has nothing resembling it, or is not there.

One message for all orphans rather than one each, which is the anti-noise rule the class already
follows for skips. It names each orphaned key, says what happened as a consequence — the backing
service fell back to `"local"` and started an instance of its own — and, for each key, offers the
declared name it resembles via `NearMiss.Closest`, so `orders_db` is told it looks like `orders-db`.

**The root key.** `DeveloperConfigFileSource.ReadFileSource` already parses the file, so it captures
the root keys it saw and keeps them on the per-builder `Registration`. `NearMissForServicesKey`
becomes a lookup over those captured keys, parameterised by which file key is being asked about, and
`NothingConfiguredError`'s existing use of it stops paying for a second parse of the file.

The audit asks for a `backingServices` near miss whenever at least one `AddBackingService()` call was
made — with no calls there is nothing the key would have fed, and an AppHost that uses no backing
services should never hear about the section. Cross-contamination between the two root keys is not
possible: `services` and `backingServices` are seven edits apart against a `NearMiss.MaxEdits`
tolerance of two.

## Testing

Every part is covered by unit tests in the existing suites, following the arrangement already there.

- **#207.** `ConnectionStringTemplateTests` moves to the new spelling throughout, and gains the
  acceptance case: `Driver={SQL Server};UID=sa;PWD={secret}` parses as a single literal. The rows
  pinning `{portal}`, `{secretariat}` and `{secrets:a}` as text keep their point under `${…}`, and
  the ODBC rows — `PWD={pa}}ss}`, `PWD={{abc}`, `Server={host}\instance` — become straightforward
  literals rather than cases that survive a rule.
- **#200.** `BackingServiceConsumerTests.LocalFactoryNamingItsResourceDifferently_MovesTheVariable`
  pins the behaviour being removed and is replaced by a test that the call throws, naming both
  names, plus one that a factory whose resource differs only by *case* is refused too — the row that
  would have passed under the folded comparison this design first specified. The acceptance
  criterion itself — the same key under `"local"` and `"direct"` — is already pinned by
  `SwitchingTheBackingServiceToDirect_ChangesOnlyTheValue` and its sibling, so it needs no new test;
  what it lacked was the rule that keeps it true.
- **#206.** An orphaned entry warns and suggests the declared name it resembles; an entry matching a
  declared name in any casing does not; a misspelled root key warns, including when another
  configuration layer has contributed an entry of its own; the same file warns nothing when no
  `AddBackingService()` call exists; and a healthy all-local AppHost produces no warning at all,
  which is the regression that would matter most.
- **The channel.** `UrlConsumerWaitTests.DroppedWait_StillGroupsWhenTheAppHostAlsoAddsABackingService`
  pins that adding a backing service does not split the url service's one grouped warning into two.
  Verified by reverting the audit to `Flush` and watching it fail with exactly two messages.

## Delivery

One PR closing all three, on `worktree-backing-service-diagnostics-200-206-207`. They touch the same
README and changelog region, and separating them would mean resolving that three times for no
reviewer benefit.

Two docs beyond the README and changelog carry the old placeholder spelling and are updated with it:
`2026-08-15-servicesources-database-source-design.md`, which is forward-looking rather than
historical — it specifies the `"kubernetes"` stage, so leaving `{port}` there would misdirect
whoever implements it — and `2026-08-30-ats-callback-spike-findings.md`, whose TypeScript example
names a resource the new rule refuses. Both carry a note saying what changed rather than being
silently rewritten.

The changelog entries for `AddBackingService` and the placeholder syntax are **edited in place**
under `## [Unreleased]`. Neither gets a **Breaking** entry: nothing has been released that could
break, and adding one would tell a reader to migrate code that cannot exist.

The README's backing-services section changes in three places — the naming rule, which becomes
enforced rather than advised; the placeholder syntax; and "One gap worth knowing about", which
described the silence #206 removes and now describes what is reported instead. The design doc
`docs/superpowers/specs/2026-08-15-servicesources-database-source-design.md` carries the old
placeholder spelling and is updated with it.

#209 is filed and out of scope: the generated shim's missing `connectionName` argument outlives all
three of these, and #200's decision does not depend on it.
