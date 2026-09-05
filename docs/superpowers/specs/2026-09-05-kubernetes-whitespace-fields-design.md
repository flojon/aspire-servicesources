# Surrounding whitespace on a field that names something across a CLI

Design for #236 — a `context` or `namespace` written with a leading or trailing space reaching
`kubectl` with the space in it, from both sources that build a port-forward.

## What the ticket asks

Two candidates, and the ticket says the choice between them is its substance:

1. **Trim at the point of use**, in `KubernetesSource` and `KubernetesBackingServiceSource`.
2. **Refuse in `DeveloperConfigValidator`**, which already owns the "value that lost its text"
   complaint and knows which block a field belongs to — with a per-field opt-in so the rule cannot
   reach a field where a space may be real.

It also asks a question ahead of both: whether `kubectl` reports a space-prefixed value clearly
enough that this is polish rather than a trap. That question is answerable by measurement, and the
answer decides more than the priority — it decides what the fix is *for*.

## What kubectl actually does

Measured against `kubectl` v1.24.3, with a stub API server serving real discovery so the request
path could be read off the wire.

**A context with surrounding whitespace is reported, and reported well.** `kubectl` resolves the
context against the kubeconfig before it does anything else, and says:

```
error: context " dev-west" does not exist
```

The value is quoted, so the space is visible to a reader who looks at it.

That quoting is contingent on something this package happens to do. Without `--namespace`, the same
kubeconfig and the same bad context give `Error in configuration: context was not found for
specified context:  dev-west` — unquoted, where a leading space reads as an ordinary gap after the
colon and is invisible. `KubectlPortForward.Args` always emits `--namespace`, defaulting to
`default`, so the good message is the one this package gets; but that default is documented in
`KubectlPortForward` as a deliberate departure from kubectl's own behaviour, which makes it exactly
the kind of thing a later change might revisit. Recorded so that revisiting it is a decision rather
than an accident.

**A namespace with surrounding whitespace is reported well or misdiagnosed, and which one depends
on the developer's RBAC.** `kubectl` does not validate a namespace locally. It percent-encodes it
and asks for the object underneath it; when that 404s it asks a *second* question, about the
namespace itself:

```
PATH: /api/v1/namespaces/%20orders/services/orders-pg
PATH: /api/v1/namespaces/%20orders
```

If the developer may read that second object, its answer is what surfaces, and it is a good message —
the right field, quoted, so the space is visible:

```
Error from server (NotFound): namespaces " orders" not found
```

If they may not — a `Role`/`RoleBinding` scoped to their own namespace, which is the ordinary
posture in a shared dev cluster and grants nothing at cluster scope — the second question is
answered `403 Forbidden`, `kubectl` discards it, and what surfaces is the first answer:

```
Error from server (NotFound): services "orders-pg" not found
```

Now the developer is told their **Service** name is wrong, and it is not. They go and check the one
field that is correct.

**An earlier draft of this document claimed the misdiagnosis unconditionally, and that was a
measurement error of mine.** The first stub API server answered every path with the same canned
`services "orders-pg" not found`, including the namespace re-query — so it fabricated the fallback
and hid the good message. A namespace-aware stub shows both branches. The conclusion survives in a
narrower form: the misdiagnosis is real and reachable, but it is the RBAC-restricted case rather
than the general one, and a developer with cluster-scoped read gets told exactly what is wrong.

Two limits of this measurement, recorded rather than papered over: the stub returns `404` for a
syntactically invalid namespace name because that is what an etcd key miss produces, and a real API
server rejecting the name at admission with `400` was not tested; and the RBAC branch was produced
by making the stub answer `403`, not by exercising a real `RoleBinding`.

Both messages land in the tunnel resource's log in the dashboard rather than in the terminal, which
raises the cost of either outcome: a developer has to go looking for the message before it can help
or mislead them.

## The ticket's premise is half wrong, and it changes the wording

The ticket justifies the fix with:

> A kubectl context and a namespace are Kubernetes object names; neither can contain a space, so a
> surrounding one is always a typo or a copy-paste artifact.

That is true of a namespace and false of a context.

A **namespace** is a DNS-1123 label — lowercase alphanumerics and `-`. A space is not a legal
character anywhere in one, so ` orders` names nothing that can exist. (kubectl does not enforce that
client-side either: `kubectl create namespace " orders" --dry-run=client` reports success, which
only sharpens the point that nothing local catches this.)

A **context** is not a Kubernetes object at all. It is a key in the developer's own kubeconfig, and
kubectl accepts whatever is written there:

```
$ kubectl config set-context " padded " --cluster=dev
Context " padded " created.
$ kubectl config use-context " padded "
Switched to context " padded ".
```

Creatable, selectable, usable. A context name with a space in it — including one at either end — is
legal.

This matters twice.

It rules out **interior** whitespace as part of the rule. `"my dev ctx"` is a context somebody may
really have, so a rule about whitespace anywhere in the value would refuse a working configuration.
The rule is about surrounding whitespace only, which is what the ticket scopes it to; the reason is
now recorded rather than assumed.

And it rules out the sentence the message wanted to say. "A context cannot contain a space" would be
a false claim printed at a developer, and false in the one direction that matters: the developer it
would be printed at is the one who actually has such a context. What that developer needs is not an
assertion but a way out, which the message now carries — see *The message*.

## Decision: refuse, in the validator, per-field

**(2), refusing in `DeveloperConfigValidator`, with the opt-in declared on the property itself.**

### The reason that carries it

**Trimming is silently wrong in exactly the case the ticket cannot rule out.** Given a context
genuinely named `" padded "`, trimming does not fail — it selects a *different* context, or none,
and says nothing about having rewritten what the developer wrote. A kubectl context names a cluster
**and** a user entry, and a user entry may carry an `exec:` credential plugin, so the silent case is
not merely "the wrong namespace": it is different credentials, a different cluster, and a different
helper binary invoked to mint them. Refusing is never silently wrong. The pathological case gets a
message naming the value, the spelling, and the one command that resolves it.

That argument stands alone, and it is the whole of the decision. The two supporting arguments in the
first draft of this document were both false, and are corrected below rather than quietly dropped —
a design that keeps a dead argument is a design a later reader re-derives from.

### Two arguments this document previously made, and why they were wrong

**"Trimming puts the same rule in two sources."** It does not. `KubectlPortForward.Args` is a single
shared method, called from `KubernetesSource` and `KubernetesBackingServiceSource` alike, and its own
doc comment says it exists precisely so the two cannot drift. Candidate (1) is **two `.Trim()` calls
in one method**. The ticket makes this assumption too, and it is wrong there as well.

**"One opt-in covers both shapes by construction."** It does not. `KubernetesDeveloperConfig` and
`KubernetesBackingServiceDeveloperConfig` are unrelated sealed types, so the attribute is written
**five times, by remembering** (see *Scope*). What candidate (2) genuinely buys over candidate (1)
is not fewer edits — it is more of them — but that the rule and its wording live in one place, so
the two sources cannot drift in what they *say*, and that there is something to say at all.

### The cost, stated rather than implied

Candidate (1) is two `.Trim()` calls in `KubectlPortForward.Args`, plus tests. Candidate (2) is:

- a new attribute type — the first `Attribute` anywhere in `src/`;
- five attribute applications across two config types;
- `DeveloperConfigField.BlockFieldsOf` changing its value type from `Type` to `PropertyInfo`, which
  moves five declarations: `BlockFieldsOf` itself, `DeveloperConfigShape.BlockFields`,
  `CollectBlock`'s `fields` parameter, `NotValidInBlock` and `BlockExpected` — three of which use
  only `.Keys` and are mechanical;
- one new branch and one message builder in the validator;
- an arm added to `Escaped` (see *Zero-width characters*);
- tests, including a new backing-service seam in `DeveloperConfigValidatorTests`.

That is materially more work than candidate (1), and it is worth it only because of the silent-
rewrite argument above. If that argument is rejected, candidate (1) is the right answer and this
whole design should be replaced by two `.Trim()` calls — which is a real option and is offered as
one on the pull request rather than buried here.

### Why the validator rather than a bespoke message in each source

`DeveloperConfigValidator` already names the entry, its kind, the block, the key and the
configuration path the value arrived at, and it collects an entry's problems so a developer is not
told about them one startup at a time. A refusal written in the two sources would be a third and
fourth copy of what this file already writes, and it would arrive after the validator had already
passed the entry — reporting a config problem from a place that is no longer doing config
validation.

## The message

One sentence shape for every opted-in field, because what is true of all of them is the same thing:

> `'namespace' in the 'kubernetes' block is set to ' orders', and the whitespace is part of the
> value: it is passed to kubectl exactly as written, so what kubectl looks for is ' orders' and not
> 'orders'. Set it to 'orders'.`

plus the `SetAt` suffix every other complaint in this file carries, naming the configuration key the
value came from — because it need not have come from the file.

It claims nothing about what can or cannot exist, so it is true for a context as well as for a
namespace; it does not say "whitespace around it", which would be false for a value padded on one
side only; and `Set it to …` is the remedy verb this file already uses. The two spellings sit side
by side in quotes, which is what makes a plain space visible — `Escaped` deliberately leaves a plain
space as itself, and the ticket's own example is a plain space, so the quoting rather than the
escaping is what does the work in the common case. The escaping earns its place on the second-
commonest case: a tab, a non-breaking space, or a character with no glyph at all.

**One field appends a sentence, and it is the field that can legitimately be padded.** A context is
the only opted-in field where the developer receiving this message may have meant it, and a message
that told *that* developer to write `'padded'` would be sending them to a context that may not
exist. So `Context` — on both shapes — carries a second sentence naming the way out:

> `A context name may legitimately carry surrounding whitespace, and this package cannot tell one
> from a typo — rename it with 'kubectl config rename-context' if you meant it.`

This is what the attribute's optional payload is *for*, which settles what it does: **the payload is
message text, appended to the shared sentence.** A field with nothing extra to say carries the bare
attribute.

## Design

### The opt-in travels on the property

```csharp
/// <remarks>A namespace is a DNS-1123 label; a space is not legal anywhere in one.</remarks>
[NoSurroundingWhitespace]
public string? Namespace { get; set; }

[NoSurroundingWhitespace(
    "A context name may legitimately carry surrounding whitespace, and this package cannot tell "
    + "one from a typo — rename it with 'kubectl config rename-context' if you meant it.")]
public string? Context { get; set; }
```

Declared on the property rather than in a table inside the validator, for the reason
`DeveloperConfigShape` gives for deriving its own keys from the entry type: *"read off the entry
type itself rather than declared a second time beside it."* A `(block, field)` table in the
validator is a second declaration of a field that already exists, keyed by two strings, and a field
renamed on one side of it goes quiet rather than failing.

### The dictionary carries the property, not the type

`DeveloperConfigField.BlockFieldsOf` returns `IReadOnlyDictionary<string, Type>` today. It becomes
`IReadOnlyDictionary<string, PropertyInfo>`: the value already comes from `GetProperties()`, a
`PropertyInfo` carries `PropertyType` and its attributes both, and `DeveloperConfigShape` already
exposes `IReadOnlyList<PropertyInfo>` for its blocks — so this adds no reflection to the surface, it
stops discarding what was there. Consumers that want the type read `.PropertyType`; the three that
use only `.Keys` change their declared type and nothing else.

### Where the check sits in the walk

In `CollectBlock`, **after** the existing `Blank` check and **before** `BindsTo`:

- After `Blank`, so a value that is *entirely* whitespace keeps the complaint it has today. `"   "`
  satisfies both rules, and the existing one is the better message for it: it names the empty
  spelling that unsets a key, which is what that developer is reaching for.
- Before `BindsTo`, so the field's own type never enters the sentence — the same reason `Blank` is
  kept apart from `NotBindable`.

The rule fires when `field.Value is { } value && value != value.Trim()`. The null guard is not
decoration: `Blank` takes only `{ Length: > 0 }` values, so a null reaches this line.

Only block fields are walked here. A future opt-in placed on `source` at the entry root, or on a
list field, would be inert — which is what the guard test below exists to catch.

### Zero-width characters are the boundary, and the message must not hide them

`char.IsWhiteSpace` is false for `U+FEFF`, `U+200B`, `U+200C` and `U+200D`, so `string.Trim` does
not remove them (verified on .NET 8). Two consequences:

- `"\uFEFForders"` is **not** refused by this rule and reaches kubectl as written. That is a
  separate trap with the same shape, and it is left open here rather than answered badly: a rule
  about invisible characters is a different rule from a rule about whitespace, and it needs its own
  decision about which code points and which fields. Recorded on the issue.
- `" \uFEFForders"` **is** refused — and the remedy the message computes is `\uFEFForders`, which
  renders as `orders` and is not `orders`. A developer following that advice writes a value that
  fails again, with no whitespace left to trigger a message. **The fix would have re-created the
  exact misdiagnosis it exists to close.**

So `Escaped` gains one arm: a character in Unicode category `Format` — which covers all four above,
and the soft hyphen — renders as its code point. It is the same rule `Escaped` already applies to
whitespace that cannot be told from a space by looking, extended to characters that cannot be seen
at all. Every message in this file benefits, and none changes for a value that does not contain one.

### Nothing is trimmed anywhere

No value is rewritten by this change. In particular `KubectlPortForward.Args` does **not** also trim
defensively: a second mechanism enforcing the same rule would make the refusal untestable at the
point it is claimed to happen, and would put back the duplication candidate (2) was chosen to avoid.

### Option injection is already foreclosed, and stays that way

Worth recording for a design about developer-supplied values reaching an external process.
`KubectlPortForward.Args` returns a `string[]`; the values are separate argv elements, never
concatenated and never split on whitespace, and nothing on this path uses a shell.
`--context`/`--namespace` are value-taking flags that consume the following element unconditionally,
so a `context` of `--kubeconfig=/tmp/evil` is taken as a literal context name rather than as a flag.
`svc/{service}` is positional and prefixed, so it cannot begin with `-` either. This design changes
none of that; it refuses a narrower class of value than the argv already handles safely.

The value is echoed back in the message, which is deliberate — seeing what arrived is the point.
That is not a new disclosure surface: `NotBindable` already echoes any value that fails to bind, so
a secret pasted into `kubernetes.port` is echoed verbatim today. The values this rule newly echoes
are single tokens naming a cluster object, so `KubernetesBackingServiceSource`'s reason for
redacting only the connection string — that it is the one echoed value which is a whole, valid
connection string — is unaffected.

## Scope

**In — five fields, on both shapes:**

| Shape | Block | Fields |
|---|---|---|
| `KubernetesDeveloperConfig` (a service) | `kubernetes` | `context`, `namespace` |
| `KubernetesBackingServiceDeveloperConfig` (a backing service) | `kubernetes` | `context`, `namespace`, `service` |

`context` and `namespace` are the ticket's own list, and both sources carry them — that symmetry is
why the ticket is one issue rather than two.

The measurement does not make either of them urgent on the strength of the resulting message alone.
`kubectl` names and quotes the offending value for a bad context always, and for a bad namespace
whenever the developer may read namespaces; the misdiagnosis is the RBAC-restricted branch. What
the rule buys in every branch is *when* and *where* the developer hears about it: at AppHost
startup, from the file they wrote, naming the key and the spelling — rather than from a `kubectl`
process whose output they have to go and find in a dashboard log, after the tunnel has been built
around a value that cannot work. The refusal is worth having for that; it is not worth overselling
as the only thing standing between the developer and a mystery.

**`service` on the backing-service block is in, and the ticket's "arguably the same case" is why.**
The line this draws is *a developer-config key whose value is handed to kubectl as the name of a
cluster object*. `service` is exactly that: `svc/{service}` in the argv, a DNS-1123 label that
cannot contain a space. Excluding it while including `context` would draw the line by how legible
the resulting failure is, and by that test `context` would be out too. One attribute and one test
row.

**A service's own Service name is out, because it is not a developer-config key at all.** It comes
from `servicesources.yaml` via `KubernetesMetadata`, a different file with a different author, which
this validator does not walk. Whether the catalog should get whitespace diagnostics of its own is a
real question about a different file, and is recorded on the issue rather than answered here.

**`connectionString` is out.** The ticket is explicit, and right: a connection string may carry
trailing whitespace inside a quoted value.

**`scheme` is out, and it is the interesting exclusion.** `EndpointScheme.Resolve` already does
`configured.Trim().ToLowerInvariant()`, so the `kubernetes` block ships today with one field silently
trimming. That is not an inconsistency to fix: a scheme is a closed set of two values, so trimming
cannot resolve to the *wrong* one — the failure mode that rules trimming out for a context does not
exist for a scheme. It does mean this document may not claim the package "passes every value through
as written", and does not.

**`port` is out.** `Int32Converter` accepts surrounding space, so `" 8080"` binds to `8080` with no
ambiguity about what was meant.

## Testing

Two seams, because the shapes are validated through different entry points, and the design's
headline claim is that both are covered:

- **Service side:** `DeveloperConfigValidatorTests.Load`, which writes a `servicesources.local.json`
  and resolves through `ServiceSourcesConfigCache.ResolveService`.
- **Backing-service side:** a seam this file does not have yet. `DeveloperConfigValidatorTests`
  contains no backing-service test at all, and `KubernetesBackingServiceTests` constructs its config
  in code and bypasses the validator entirely. A `LoadBackingService` helper is added alongside
  `Load`, modelled on `AddBackingServiceTests.CreateAppHostDirectory` — no catalog file, JSON under
  `backingServices`. **This helper is part of the work, not an existing asset.**

Cases:

- Each opted-in field refused with a leading space, with a trailing space, and with both — for the
  fields on both shapes. Driven from a `[Theory]` over `(shape, block, field)` rather than written
  out, so the symmetry claim is a table a reader can check against the *Scope* table above.
- The message names the block, the key, the escaped value and the trimmed spelling, and ends with
  the `SetAt` key.
- `context` — and only `context` — carries the `rename-context` sentence, on both shapes.
- A tab, a non-breaking space and a `U+FEFF` around a value are refused and are **visible** in the
  message, including in the spelling the remedy proposes. The `U+FEFF` case is the regression test
  for the trap the fix would otherwise have re-created.
- A value that is entirely whitespace still gets `Blank`'s message and not this one.
- An empty value is still the unset gesture and is not refused.
- Interior whitespace — `"my dev ctx"` — is accepted, which is the measured kubeconfig behaviour and
  would otherwise be a regression nobody noticed until someone had such a context.
- Fields that did **not** opt in are unaffected: `local.path` with a trailing space, a
  `prepare.command` element with surrounding space, and both `connectionString` fields with trailing
  space all still bind. These pin the exclusions, which are decisions this document made rather than
  properties of the mechanism.
- **Guard test:** every property in either shape carrying the attribute is a block-field property
  that the validator actually walks, and the set of carriers is exactly the *Scope* table. This
  guards two real failure modes — an attribute placed somewhere the walk never reaches, and a field
  quietly losing the rule — and not a third it cannot: moving a property between block types carries
  its attributes with it, so relocation was never the hazard.

## Open questions

None blocking. Two things are deliberately recorded on #236 rather than answered here: whether the
catalog's `kubernetes.service` should get whitespace diagnostics of its own, and the zero-width
character gap above.

## Delivery

One commit on `236-ws-trim-45b0`, off `origin/main`. `CHANGELOG.md` gets a `### Changed` entry under
`## [Unreleased]`: this is a new refusal of a file that used to start, which is behaviour a reader
upgrading needs to know about.
