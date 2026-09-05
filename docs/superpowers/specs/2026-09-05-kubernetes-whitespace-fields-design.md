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
answer decides more than the priority — it decides which of the two fields is the one that matters.

## What kubectl actually does

Measured against `kubectl` v1.24.3, with a stub API server serving real discovery so the request
path could be read off the wire.

**A context with surrounding whitespace is reported, and reported well.** `kubectl` resolves the
context against the kubeconfig before it does anything else, and says:

```
error: context " dev-west" does not exist
```

The value is quoted, so the space is visible to a reader who looks at it.

**A namespace with surrounding whitespace is not reported at all.** `kubectl` does not validate a
namespace locally — it URL-encodes it and asks the API server for the object underneath it:

```
PATH: /api/v1/namespaces/%20orders/services/orders-pg
```

and the server answers with a message about the object, not about the namespace:

```
Error from server (NotFound): services "orders-pg" not found
```

So the developer is told their **Service** name is wrong, and it is not. They go and check the one
field that is correct. This is the failure that makes #236 a trap rather than a polish item: not
that the value reaches `kubectl` as written, but that the resulting diagnosis points at the wrong
field.

Both messages land in the tunnel resource's log in the dashboard rather than in the terminal, which
raises the cost of a misdiagnosis further: a developer has to go looking for the message before they
can be misled by it.

## The ticket's premise is half wrong, and it changes the wording

The ticket justifies the fix with:

> A kubectl context and a namespace are Kubernetes object names; neither can contain a space, so a
> surrounding one is always a typo or a copy-paste artifact.

That is true of a namespace and false of a context.

A **namespace** is an RFC 1123 label — lowercase alphanumerics and `-`. A space is not a legal
character anywhere in one, so ` orders` names nothing that can exist.

A **context** is not a Kubernetes object at all. It is a key in the developer's own kubeconfig, and
kubectl accepts whatever is written there:

```
$ kubectl config set-context "my dev ctx" --cluster=dev
Context "my dev ctx" created.
$ kubectl config set-context " padded " --cluster=dev
Context " padded " created.
```

Both are usable contexts. A context name with a space in it — including one with a space at either
end — is legal, creatable, and selectable.

This matters twice.

It rules out **interior** whitespace as part of the rule. `"my dev ctx"` is a context somebody may
really have, so a rule about whitespace anywhere in the value would refuse a working configuration.
The rule is about surrounding whitespace only, which is what the ticket scopes it to; the reason is
now recorded rather than assumed.

And it rules out the sentence the message wanted to say. "A context cannot contain a space" would be
a false claim printed at a developer, and false in the one direction that matters: the developer it
would be printed at is the one who actually has such a context.

## Decision: refuse, in the validator, per-field

**(2), refusing in `DeveloperConfigValidator`, with the opt-in declared on the property itself.**

Three reasons, in the order they weigh.

**Trimming is silently wrong in exactly the case the ticket cannot rule out.** Given a context
genuinely named `" padded "`, trimming does not fail — it connects to a *different* context, or to
none, and says nothing about having rewritten what the developer wrote. Refusing is never silently
wrong: the pathological case gets a message naming the value and the spelling, and one
`kubectl config rename-context` if they want to keep using this package with that context. A rule
that is right 999 times in 1000 and silently wrong once should be a diagnostic rather than a
rewrite, and this package already made that choice for whitespace-only values.

**The knowledge belongs in one place.** Trimming at the point of use puts the same rule in two
sources, and the ticket exists because those two sources are the thing most likely to drift apart.
The validator is walked once per entry for both shapes, so one opt-in covers a service and a backing
service by construction rather than by remembering.

**The diagnostics are already there.** `DeveloperConfigValidator` names the entry, its kind, the
block, the key and the configuration path the value arrived at — and escapes the value so a tab or a
U+00A0 is distinguishable from a space, which is precisely the class of value this is about. A trim
at the point of use says nothing at all; a bespoke message in each source would be a third and
fourth copy of what this file already writes.

### What the message says instead

Not "a context cannot contain a space", which is false, but what is true of both fields and is the
actual defect:

> `'namespace' in the 'kubernetes' block is set to `' orders'`, which has whitespace around it —
> kubectl takes the value as written, so `' orders'` is a different namespace from `'orders'`. Write
> `'orders'`.`

Plus the `SetAt` suffix every other complaint in this file carries, naming the configuration key the
value came from — because it need not have come from the file.

## Design

### The opt-in travels on the property

```csharp
[NoSurroundingWhitespace("a namespace kubectl was given as written")]
public string? Namespace { get; set; }
```

Declared on the property rather than in a table inside the validator, for the reason
`DeveloperConfigShape` gives for deriving its own keys from the entry type: *"read off the entry
type itself rather than declared a second time beside it. Deriving it means a field added to a block
type is immediately a valid key, with nothing to keep in step."* A `(block, field)` table in the
validator is a second declaration of a field that already exists, keyed by two strings, and a field
renamed on one side of it goes quiet rather than failing.

The attribute carries a phrase rather than being a bare marker. The rule is not obvious — this file
deliberately passes values through as written, and a field that is stricter than the file's general
rule should say on its face why it is. The phrase is what makes the next field's opt-in a decision
rather than a copied attribute.

### The dictionary carries the property, not the type

`DeveloperConfigField.BlockFieldsOf` returns `IReadOnlyDictionary<string, Type>` today. It becomes
`IReadOnlyDictionary<string, PropertyInfo>`: the value already comes from `GetProperties()`, a
`PropertyInfo` carries `PropertyType` and its attributes both, and `DeveloperConfigShape` already
exposes `IReadOnlyList<PropertyInfo>` for its blocks — so this adds no reflection to the surface,
it stops discarding what was there.

Every consumer that wants the type reads `.PropertyType`. The keys are untouched, so
`HomeBlocksOf`, `NearMissFieldsOf` and every "valid keys there are …" list are unaffected.

### Where the check sits in the walk

In `CollectBlock`, **after** the existing `Blank` check and **before** `BindsTo`:

- After `Blank`, so a value that is *entirely* whitespace keeps the complaint it has today. `"   "`
  satisfies both rules, and the existing one is the better message for it: it names the empty
  spelling that unsets a key, which is what that developer is reaching for.
- Before `BindsTo`, so the field's own type never enters the sentence. It is `string?` for both
  fields today, but a field whose value is surrounded by space is wrong in the same way whatever it
  binds to, and the pairing reads as a contradiction for a string — the same reason `Blank` is kept
  apart from `NotBindable`.

The rule fires when `value != value.Trim()` and the value is not entirely whitespace, which the
preceding branch has already taken.

## Scope

**In:** `context` and `namespace`, on both `KubernetesDeveloperConfig` (a service) and
`KubernetesBackingServiceDeveloperConfig` (a backing service). Both sources, symmetrically, which is
the reason the ticket is one issue rather than two.

**Out — `connectionString`.** The ticket is explicit, and right: a connection string may carry
trailing whitespace inside a quoted value.

**Out — `service`, and the reason is the symmetry the ticket is about.** A backing service's
`kubernetes.service` is developer config and would take the attribute for free. A *service's* is
not: it comes from `servicesources.yaml` via `KubernetesMetadata`, which this validator does not
walk at all. Opting in the backing-service half alone would produce exactly the one-sided fix the
ticket refuses to ship — a Service name refused in one section of one file and passed through in
another. Whether the catalog should get whitespace diagnostics of its own is a separate question
about a separate file, and is recorded on the issue rather than answered here.

**Out — trimming anything.** No value is rewritten anywhere by this change.

## Testing

Against `DeveloperConfigValidator` through its existing test seam, plus the two sources for the
symmetry claim:

- `context` and `namespace` with a leading space, a trailing space, and both — refused, for a
  service and for a backing service. Four fields' worth, because the symmetry is the ticket.
- The message names the block, the key, the escaped value and the trimmed spelling.
- A tab and a U+00A0 around a value are refused and are visible in the message, via the existing
  `Escaped`.
- A value that is entirely whitespace still gets `Blank`'s message and not this one.
- An empty value is still the unset gesture and is not refused.
- Interior whitespace — `"my dev ctx"` — is accepted, which is the measured kubeconfig behaviour
  above and would otherwise be a regression nobody noticed until someone had such a context.
- A field that did not opt in is unaffected: `local.path` with a trailing space, a
  `prepare.command` element with surrounding space, and `direct.connectionString` with trailing
  space all still bind.
- The attribute is actually reachable through the shape — a test that asserts the opted-in fields
  are the ones expected, so that moving a field between blocks cannot silently drop the rule.

## Open questions

1. **Should the catalog's `kubernetes.service` get the same treatment?** Out of scope here; the
   catalog has no equivalent validator. Recorded on #236.
2. **Is the attribute's phrase worth its weight, versus a bare marker attribute?** The phrase is
   what stops the next opt-in being a copy. Reviewers should push back if the messages read better
   without it.

## Delivery

One commit on `236-ws-trim-45b0`, off `origin/main`. `CHANGELOG.md` gets a `### Changed` entry
under `## [Unreleased]`: this is a new refusal of a file that used to start, which is behaviour a
reader upgrading needs to know about.
