# The `WithEndpoint` callback overload's add branch never reaches the real resource (#352)

**Date:** 2026-09-17
**Status:** Draft
**Resolves:** #352 (`WithEndpoint(name, Action<EndpointAnnotation>, createIfNotExists: true)` taking
its *add* branch on a **reachable** source — `local`, `container` — registers the new
`EndpointAnnotation` on the `ServiceResource` facade alone, never on the real resource DCP runs, so
the endpoint silently has no effect.)
**Relates to:**
[`2026-09-14-334-endpoint-skip-gate-design.md`](2026-09-14-334-endpoint-skip-gate-design.md) (#334)
and [`2026-09-15-335-raw-withendpoint-gate-design.md`](2026-09-15-335-raw-withendpoint-gate-design.md)
(#335, shipped as PR #357). #335 §2 named this bug, proved it was distinct from the gating problem
it was closing, and deliberately left it open: "choosing to delegate unmodified doesn't introduce or
worsen it." This document closes it. **This is not a resume of #335** — that design is complete and
merged; it is cited only for convention and for the scope boundary it drew.
**Does not resolve:** #353 (guest-language callbacks reaching Aspire's `internal`
`WithEndpointCallback`, which no C# shadow can intercept), #359.

---

## 1. The bug, re-verified against the real Aspire 13.5.2

Decompiled fresh for this document with `ilspycmd 11.0.0.9375` against
`~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll` — the floor `global.json`
and `Directory.Build.props` pin. The ticket's own transcription is accurate; the only correction is
that the second `if` uses the non-short-circuiting `&`, which changes nothing semantically:

```csharp
[AspireExportIgnore(Reason = "Polyglot app hosts use the internal withEndpointCallback export, ...")]
public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder,
    [EndpointName] string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true)
    where T : IResourceWithEndpoints
{
    ArgumentNullException.ThrowIfNull(builder, "builder");
    ArgumentNullException.ThrowIfNull(endpointName, "endpointName");
    ArgumentNullException.ThrowIfNull(callback, "callback");
    EndpointAnnotation endpointAnnotation = builder.Resource.Annotations.OfType<EndpointAnnotation>()
        .SingleOrDefault(ea => string.Equals(ea.Name, endpointName, StringComparisons.EndpointAnnotationName));
    if (endpointAnnotation != null)
    {
        callback(endpointAnnotation);                            // UPDATE: shared instance, already correct
    }
    if ((endpointAnnotation == null) & createIfNotExists)
    {
        endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, name: endpointName,
            networkId: KnownNetworkIdentifiers.LocalhostNetwork);
        callback(endpointAnnotation);
        builder.Resource.Annotations.Add(endpointAnnotation);    // ADD: direct, never WithAnnotation
    }
    else if (endpointAnnotation == null)
    {
        return builder;                                          // createIfNotExists: false -> no-op
    }
    return builder;
}
```

The reference behaviour it diverges from is the primary numeric overload, whose add branch ends
`return builder.WithAnnotation(endpointAnnotation2);` — through `ServiceResourceBuilder`, so it
dual-writes today. Every other shadowed overload (#335 §1's table #1–#4, #6, #7) forwards into that
primary one, so all eight numeric shadows share the correct add branch. The callback overload is the
only one in the family that adds by hand.

### 1.1 Probe evidence

Seven cases run as real compiled xunit tests against a `container`-sourced service on `net10.0`,
reading identity hashes off both `ServiceResource.Annotations` and
`((ServiceResourceBuilder)service).Real!.Resource.Annotations`. Probes were temporary and are not
part of the diff; their results are the load-bearing facts below.

| Probe | Call | Result |
|---|---|---|
| P1 | `WithEndpoint("admin", e => e.Port = 9200)` | **Bug reproduced.** Facade: `http`, `admin(p=9200, scheme=tcp)`. Real: `http` only. |
| P2 | `WithEndpoint("http", e => e.Port = 9300)` | Update branch correct — one instance, identical hash, `p=9300` on both. Acceptance item 2 holds. |
| P3 | `WithEndpoint("nope", …, createIfNotExists: false)` | Callback **not invoked**, nothing added either side. Acceptance item 3 holds. |
| P4 | `WithEndpoint(port: 9400, name: "metrics", scheme: "http")` | `metrics` on both, **same identity hash** — numeric add branch is the reference behaviour. Acceptance item 4 holds. |
| P5 | delegated call wrapped in the §3 identity-diff forward | facade 2 endpoints / real 2 endpoints, matching hashes — **fix works, no double-add**. |
| P6 | numeric overload under the *same* diff | 1 annotation new on the facade, **already on real** (`alreadyOnReal=1`). Without an identity guard this double-adds. |
| P7 | two same-named endpoints, then a callback call | `InvalidOperationException: Sequence contains more than one matching element` — from Aspire's `SingleOrDefault`, thrown before anything is added; `"dup"` matched `"DUP"`, confirming `OrdinalIgnoreCase`. |

### 1.2 The constraint that eliminates the most attractive option

`Aspire.Hosting.ApplicationModel.Resource.Annotations` is `virtual`, which reads at first glance
like an invitation to hand `ServiceResource` a collection that mirrors every `Add` onto `real`. It
is not: the property's type, `ResourceAnnotationCollection`, is **`sealed`** (decompiled above), and
its four mutation hooks — `InsertItem`, `RemoveItem`, `SetItem`, `ClearItems` — are `protected
override`s on a sealed type. There is no subclass to write, and the property's declared return type
admits no substitute. A universal write interceptor on the facade's annotation collection is not
available in Aspire 13.5.2 at any price, so no design below can assume one.

## 2. The decision this document exists to settle

Given that the delegated call has already mutated the facade by the time control returns, how does
the shadow learn that the *add* branch ran, and forward the new annotation to `real`? Four
mechanisms were considered; the ticket suggested the first and it is not ratified unexamined.

### 2.1 Option A — identity diff of the annotation collection across the delegated call **(chosen)**

Snapshot the facade's annotation instances by reference before delegating; afterwards, forward every
instance that is new *and* that `real` does not hold **at that moment** — a live read of
`real`'s collection after the call, never a second pre-call snapshot (§3 spells out why).

- Needs no knowledge of Aspire's branch structure, its constructor defaults, or its name matching.
- P5 proves it forwards exactly the added instance; P4/P6 prove the `real`-already-holds guard is
  what keeps it from double-adding on the eight numeric shadows.
- **Forward-compatible with an upstream fix.** If a later Aspire changes this add branch to
  `builder.WithAnnotation(...)`, `ServiceResourceBuilder` dual-writes first, the guard sees the
  instance already on `real`, and the forward becomes a silent no-op. Nothing has to be un-shipped,
  and the CI `latest` Aspire leg does not start failing. This property, not elegance, is what
  decides between A and B.
- Cost: one `HashSet` of reference-equal annotations per gated endpoint call, on a code path that
  runs once per AppHost composition line. Not measurable.

### 2.2 Option B — look the endpoint name up before and after

Check whether an `EndpointAnnotation` named `endpointName` exists before the call and after it; if
one appeared, forward it.

Rejected. It reproduces Aspire's matching rule rather than observing its effect: the comparison is
`StringComparisons.EndpointAnnotationName`, which is `internal` to `Aspire.Hosting.dll`
(`Aspire.StringComparisons`), so this package would have to hard-code `OrdinalIgnoreCase` — the
value P7 confirms today — with no compile-time link to the constant it is copying. It is also
narrower than A for no benefit: it can only ever recognise an `EndpointAnnotation`, and it still
needs A's "already on `real`" guard to stay idempotent. Strictly more assumptions, strictly less
coverage.

### 2.3 Option C — intercept `WithAnnotation`, or the collection itself

Not possible. `ServiceResourceBuilder.WithAnnotation` is already the interception point and Aspire's
add branch provably does not call it (§1); and per §1.2 the collection cannot be subclassed. The
existing shadow-and-gate layer exists precisely because this option is closed, and #334/#335 already
paid that price.

### 2.4 Option D — take the add branch ourselves instead of delegating

Replicate Aspire's add branch inside the shadow: on a name miss with `createIfNotExists: true`,
construct `new EndpointAnnotation(ProtocolType.Tcp, name: endpointName, networkId:
KnownNetworkIdentifiers.LocalhostNetwork)`, invoke the callback, and call `builder.WithAnnotation`.
Both the constructor overload and `KnownNetworkIdentifiers.LocalhostNetwork` are public, so this
compiles.

Rejected, and it is the option most worth rejecting explicitly, because it looks cleanest. It forks
Aspire's implementation into this package: every default the constructor call encodes (the
`ProtocolType.Tcp`, the localhost network id, the absent `uriScheme`/`transport` that make the new
endpoint's scheme come out as `tcp` — P1) becomes a copy that silently drifts the first time Aspire
changes one, with no compiler error and no test that would notice until a developer's endpoint lands
on the wrong network. It also duplicates the name lookup, inheriting §2.2's `StringComparisons`
problem on top. Every other shadow in this file delegates unmodified; this one would not.

### 2.5 Option E — reconcile facade-only annotations onto `real` at `BeforeStartEvent`

A late sweep over `RealToFacadeRegistry` copying anything the facade holds and `real` does not.
This is the only option that would also cover #353's guest-language path, since it does not depend
on a C# call site at all.

Rejected. Three reasons, in order of weight:

1. **It converts a fail-closed design into a fail-open one.** The whole point of #334/#335's gate
   and of `Reachability`'s denylist-not-allowlist remarks is that an unrecognised annotation is
   treated as configuration and refused, loudly. A blanket sweep does the opposite: it makes every
   *future* interception gap invisible by papering over it, so the next one is found by a user, not
   by a test.
2. **It fights code that deliberately un-mirrors.** `UrlSource.DropWaitsOnUrlServices`,
   `ServiceWaitRetargeting` and `RealToFacadeRegistry` all exist to make the two collections
   deliberately differ at specific moments. A sweep would have to know about each, and about each
   future one.
3. **Its timing is unproven.** Endpoint allocation, service-discovery environment composition and
   DCP model construction all read endpoints, and nothing here establishes that `BeforeStartEvent`
   is early enough for all of them. Option A runs at AppHost-composition time, which is exactly when
   the numeric overload's own `WithAnnotation` already runs — provably early enough, because that
   path works today.

#353 stays uncovered either way; it is `internal` API, and widening this ticket to chase it would
buy coverage of an unconfirmed gap at the cost of the properties above.

### 2.6 Does the mechanism generalise beyond `EndpointAnnotation`?

**The helper is written generically over `IResourceAnnotation`; its application is deliberately
scoped to one call site.** Those are separate answers and both matter.

"Generic" here means **not type-filtered**, not "takes a `<TAnnotation>` parameter": the diff yields
`IResourceAnnotation`-typed instances, so there is no type argument to infer and
`ForwardingAnnotationsAddedBy` is a plain non-generic method. The plan should not invent one.

Not type-filtered, because a filter buys nothing: the diff observes instances, not
types, so filtering to `EndpointAnnotation` would be an extra condition whose only effect is to drop
anything the delegated call adds that the author did not anticipate — silently, which is the
behaviour this repo rejects everywhere else. Today the delegated call adds exactly one annotation
and it is an `EndpointAnnotation` (§1); a caller's own callback that reached back through a captured
builder could in principle add more, and a generic diff carries that for free.

Scoped in the application, because the callback overload is the **only** method in
`Aspire.Hosting.ResourceBuilderExtensions` 13.5.2 that adds an annotation by direct
`Annotations.Add` at all — a grep of the full decompiled class finds exactly one such statement, in
this method's add branch; every other addition in the class goes through `builder.WithAnnotation`.
Wrapping the eight numeric shadows too would be
provably unnecessary (P4) and, minus the identity guard, actively harmful (P6). The guard makes the
wrap harmless rather than useful there — which is a reason to keep it available, not a reason to
apply it.

The version-drift risk this scoping creates is real and is handled by a test, not by prophylactic
wrapping: §4 adds an assertion that the numeric add branch still reaches `real`. If a future Aspire
regresses it the way the callback overload is regressed today, that test fails on the CI `latest`
Aspire leg and names the overload, instead of a developer finding it in a running app.

## 3. Fix shape

One change in `ServiceSourcesBuilderExtensions`'s callback-overload shadow, and one new internal
member on `ServiceResourceBuilder`. No change to `GateEndpointCall`, to `Reachability`, to
`WithAnnotation`, to any of the other eight shadows, or to any public signature.

The shadow becomes gate → snapshot → delegate → forward:

```csharp
// Attribute unchanged from ServiceSourcesBuilderExtensions.cs:391 -- copy it verbatim from there,
// not from this block. Only the body below changes.
public static IResourceBuilder<ServiceResource> WithEndpoint(
    this IResourceBuilder<ServiceResource> builder,
    [EndpointName] string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true)
{
    if (GateEndpointCall(builder))
    {
        return builder;
    }

    // Aspire's add branch adds straight to Resource.Annotations, never through
    // ServiceResourceBuilder.WithAnnotation, so `real` only learns about it from this diff (#352).
    return ServiceResourceBuilder.ForwardingAnnotationsAddedBy(
        builder,
        () => Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(builder, endpointName, callback, createIfNotExists));
}
```

`ForwardingAnnotationsAddedBy` lives on `ServiceResourceBuilder` — the file that already owns the
dual-write contract and the only file that knows what `real` is — and does:

1. If `builder` is not a `ServiceResourceBuilder`, or its `Real` is `null`, just run the delegate and
   return. (The non-`ServiceResourceBuilder` case is the same hypothetical `GateEndpointCall`'s own
   fallback already contemplates; the `null` case is `"url"`, which never gets here because the gate
   fires first.)
2. Snapshot the **facade's** `Resource.Annotations` into a `HashSet<IResourceAnnotation>` built with
   `ReferenceEqualityComparer.Instance`. Take no snapshot of `real`.
3. Run the delegate and keep its result.
4. Read `Real.Resource.Annotations` **now**, after the delegate, into a second reference-equal set.
   For each annotation on the facade that is in neither set, forward it to `real` (step 5) — then
   return the delegate's result.
5. Forwarding one annotation: if `Reachability.IsUnreachable(annotation.GetType(), Source)`, record
   `ServiceSourcesWarnings.For(ApplicationBuilder).AddSkip(Resource.Name, Source,
   Reachability.CapabilityLabel(annotation.GetType()))` and forward nothing; otherwise
   `real.WithAnnotation(annotation)`.

**Step 4's `real`-side read must be live, not a second pre-call snapshot.** This is the single
subtlety in the whole design. A pre-call snapshot of `real` cannot see what the delegate wrote to
`real` *during* the call — which is exactly what happens on every numeric overload, where the add
branch goes through `ServiceResourceBuilder.WithAnnotation` and dual-writes mid-delegate (P6). Read
before, and such an annotation looks new on the facade and absent from `real`, and gets added a
second time. Read after, and it is correctly recognised as already there. The same distinction is
what makes §2.1's forward-compatibility claim true: if a future Aspire changes this add branch to
`builder.WithAnnotation(...)`, the post-call read sees the instance on `real` and the forward becomes
a silent no-op. P5 forwarded from a post-call read and produced no double-add; that is the shape to
implement.

Three further mechanical points that are not obvious:

- **The forward goes to `real`, never through `builder.WithAnnotation`.** The facade already holds
  the instance — Aspire put it there — so routing through `ServiceResourceBuilder.WithAnnotation`
  would add it a second time. This is acceptance item 6's "must not double-add to the facade," and
  it is why the dual-write in `ServiceResourceBuilder.cs:128-163` is untouched.
- **`real.WithAnnotation<IResourceAnnotation>(annotation)` compiles without reflection.** An
  interface type argument satisfies `where TAnnotation : IResourceAnnotation` (there is no `new()`
  constraint), and Aspire's `DistributedApplicationResourceBuilder<T>.WithAnnotation` with the
  default `Append` behaviour is exactly `Resource.Annotations.Add(annotation)` — decompiled,
  identical to what the facade received. **`Append` is not merely preferable to `Replace`; `Replace`
  would throw.** With `TAnnotation` inferred as `IResourceAnnotation`, Aspire's `Replace` path runs
  `Resource.Annotations.OfType<IResourceAnnotation>().SingleOrDefault()`, which throws
  `InvalidOperationException` whenever `real` holds more than one annotation — always true after
  `Bridge`, which adds a `ServiceSourceAnnotation` alongside the source's own endpoint.
- **Reachability is re-checked per forwarded annotation, by runtime type (step 5).**
  `GateEndpointCall` already cleared `typeof(EndpointAnnotation)` for this source, so this check
  cannot fire today: the only annotation the delegated call adds is an `EndpointAnnotation`, and the
  gate is what let control reach here. It exists so the invariant "nothing reaches `real` without
  consulting `Reachability`" stays true of every path, including one that forwards an annotation
  type nobody predicted (§2.6). Note the deliberate difference from `WithAnnotation<TAnnotation>`,
  which keys on the *static* type parameter: here only `annotation.GetType()` is available, and for
  the sealed `EndpointAnnotation` the two agree.

  **What a fired re-check leaves behind is a named residue, not a silent one.** `WithAnnotation` can
  refuse cleanly because it runs before either collection is touched; here the annotation is already
  on the facade by the time the check runs, and this design does not remove it — deleting an object
  Aspire (or a caller's own callback) put there would be a second guess on top of a branch that
  cannot currently execute. The skip warning in step 5 is what makes the divergence visible instead
  of silent, which is the property §2.5 rejects Option E for lacking.

### 3.1 No try/finally, and no change to exception behaviour

The forward runs only on the delegate's normal return. Nothing is lost by that, and it is verified
rather than assumed: Aspire's add branch invokes the callback *before* `Annotations.Add`, so a
throwing callback leaves nothing added to forward; and P7's duplicate-name
`InvalidOperationException` is thrown by `SingleOrDefault` before either branch runs. A partially
applied *update*-branch mutation on a throwing callback survives exactly as it does today, on the
shared instance, unchanged by this design.

### 3.2 What this fix also repairs, as a consequence

Today a facade-only endpoint stays facade-only forever: a later `WithEndpoint("admin", …)` takes the
*update* branch against the orphaned instance and is equally invisible to `real`. Once the first
call forwards the instance, the two collections share it and every subsequent update is visible on
both — the same property `ResolvedService.Bridge`'s comment already claims for endpoints that
existed at bridge time. No extra code; worth a test (§4).

### 3.3 Bounded residue, named rather than guarded

If `real` ever carried an `EndpointAnnotation` with the same *name* as the forwarded one but a
different instance, the forward would produce two same-named endpoints on `real`. This design does
**not** guard against that, deliberately:

- No current code path produces it. `Bridge` copies every real annotation onto the facade at bridge
  time, and an audit of every `Annotations.Add` in `src/` finds no post-bridge real-only endpoint
  write — `DeferredCheckout.RestoreLaunchProfile` (the one genuinely late write) adds its
  `EnvironmentCallbackAnnotation` to both collections by hand, and `UrlSource`'s endpoint goes to a
  facade with no real resource at all.
- Guarding it means name matching, which means copying `StringComparisons.EndpointAnnotationName`
  (§2.2) — reintroducing the exact dependency Option A was chosen to avoid, to defend a state that
  cannot currently occur.

This is the one place where the design accepts a residue rather than failing closed, and it is
bounded: it is a single named shape, not an open input space.

## 4. Tests

The regression must assert against `real`, not the facade: today's failure mode is silent and a
facade-only assertion passes against the bug (acceptance item 8). `ServiceResourceBuilder.Real` is
`internal` and the test assembly already has `InternalsVisibleTo`
(`src/Aspire.Hosting.ServiceSources/AssemblyInfo.cs`); the one existing use of that route is
`test/.../Sources/ServiceResourceBuilderTests.cs:28`.

**One existing test must be reckoned with before anything new is written.**
`test/.../ServiceSourcesBuilderExtensionsTests.cs:182-204`
(`WithEndpoint_Callback_OnReachableSource_InvokesCallbackAndAppliesThroughToTheRealEndpoint`) is
today's reachable-source callback test. Its comment says it deliberately pre-registers through
`WithHttpsEndpoint` so it exercises the *update* branch, "not its separate, pre-existing add-branch
dual-write gap (#352, out of this task's scope)" — and despite its name, every assertion in it is
against the facade. Its construction (`ResolvedService.Bridge(builder.AddResource(new
ServiceContainerResource(...)).WithImage(...), "orders", "container")`) is also the cheapest way to
build a bridged service in a test. The plan **strengthens this test in place**: drop the now-stale
scope clause from its comment and add an `Assert.Same` against `Real.Resource`'s endpoint, so its
name becomes true. It then covers case 3 below, and cases 3 and 6 must not duplicate it.

Home for the new cases: a new fixture beside `EndpointSkipGapRepro.cs`, or a section of
`ServiceResourceTests.cs` — not `EndpointSkipGapRepro.cs` itself, whose stated subject is the *skip
gate*, except for case 8 which belongs there. Gated cases follow that file's existing convention of
routing the callback overload through `OverloadProbe.CallWithEndpointCallback`
(`OverloadResolutionProbe.cs:51`); the reachable cases call the extension directly, as
`ServiceSourcesBuilderExtensionsTests` already does. The plan decides placement; these are the cases.

1. **`container` add branch reaches `real`** — the P1 repro, inverted. Fails before the fix.
   Assert the same instance (`Assert.Same`) is on both collections and that each holds exactly one
   endpoint named `admin`.
2. **`local` add branch reaches `real`** — same assertion against a `local`-sourced service.
   `ResolvedService.Bridge(<project builder>, name, "local")` is the cheap construction
   (`ProjectResource` has a public `(string name)` constructor and implements
   `IResourceWithServiceDiscovery`, which is `Bridge`'s only constraint); `LocalProjectSourceTests`
   has the fuller route if the plan prefers it. Fails before the fix.
3. **Update branch unchanged** (item 2) — P2 as an assertion: one instance, mutation visible on both.
   Covered by strengthening the existing test named above; do not add a second one.
4. **`createIfNotExists: false` is still a no-op** (item 3) — P3 as an assertion, including a closure
   flag proving the callback was never invoked.
5. **Numeric add branch still reaches `real`** (item 4, and §2.6's drift detector) — P4 as an
   assertion, with a comment saying what its failure would mean.
6. **No double-add** — after a callback add, the facade holds exactly one endpoint of that name; the
   count on each side is asserted, not just membership.
7. **Follow-on update after a callback add** (§3.2) — add through the callback overload, then update
   through it again, and assert the new port is visible on `real`.
8. **Unreachable sources unchanged** (item 5) — one skip warning carrying the
   `WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint` label, the callback provably never invoked, and
   nothing added to `real`.
   - The **kubernetes** case already exists:
     `EndpointSkipGapRepro.DefaultNamedEndpoint_OnKubernetesSource_ViaCallback_DoesNotChangeThePortForwardsEndpoint`.
     It is not rewritten; it must simply still pass, and the plan names it as the gate's guard
     because this change edits that exact method body.
   - The **url** case is **new coverage, not a duplicate** — every url case in `EndpointSkipGapRepro.cs`
     and `ServiceSourcesBuilderExtensionsTests.cs` goes through `WithHttpEndpoint`/`WithHttpsEndpoint`
     or a numeric shim, none through the callback overload. It is worth adding here because `url` is
     the source where `Real` is `null`, so it is the one case that proves the gate, not the
     null-check in step 1, is what stops the new code.

## 5. Documentation

- `Sources/ResolvedService.cs`'s bridge comment already explains why the shadows exist (the
  *update* branch bypassing `WithAnnotation`). It gets one more clause: the callback overload's
  *add* branch bypasses it too, in the other direction, and the shadow forwards the instance so the
  claim "the same instance sits in both collections" stays true for endpoints created after bridge
  time.
- `Sources/ServiceResourceBuilder.cs` — the new member carries the WHY: Aspire adds this one
  annotation by hand, so the only way to see it is to diff.
- No `README.md` change. The endpoint vocabulary it documents is unchanged; this restores the
  behaviour the README already describes.

## 6. CHANGELOG disposition

**No `### Fixed` entry** — re-verified, not inherited from recon. `CHANGELOG.md`'s preamble limits
`Fixed` to bugs in already-*released* behaviour. The dual-write bridge this gap lives in arrived in
`243c227` (#313/#326, 2026-09-14); the latest tag is **v0.5.1** (2026-09-07),
`git merge-base --is-ancestor 243c227 v0.5.1` is false, and the file does not exist in the v0.5.1
tree. The bug is entirely inside the current `[Unreleased]` cycle, as were #334/#339 and #335/#357
before it. Whoever lands this re-confirms no tag was cut in the meantime. If anything is written at
all it is a clarifying clause inside the existing `[Unreleased]` `Breaking` entry for #313 — not a
new section.

Sibling **PR #364** also edits `CHANGELOG.md` and `README.md`. Writing neither file keeps this
branch's rebase against it empty; that is a consequence of the disposition above, not its reason.

## Attack surface

Nothing added. No new input is parsed; no shell, path, URL, query or deserializer is reached; no
credential, subprocess or network call is involved; no trust or process boundary is crossed. The
change moves an object reference this package already held from one in-memory collection to another
in the same AppHost process.

Two boundary-shaped properties are worth stating rather than implying:

- **The gate is strictly upstream of everything new.** The snapshot/forward is unreachable unless
  `GateEndpointCall` returned `false`, so no unreachable source's `real` resource — the
  `kubectl port-forward` process `Reachability` exists to protect — can be written by this path.
  §3's per-annotation re-check is a second, redundant consultation of the same table, never a
  different policy.
- **The forward grants the caller no capability they do not already have.** The object forwarded
  today is one Aspire itself constructed in a call this package delegated, and the caller's influence
  over it is exactly the influence the callback already has on a plain `AddProject`/`AddContainer` —
  this fix removes a privilege reduction that was an accident. The generic diff (§2.6) does mean a
  callback that reached back through a captured builder and added some other annotation to the
  public, mutable `ServiceResource.Annotations` would see it forwarded too; that is not new reach,
  because on a reachable source that same caller can already put any annotation on `real` by calling
  `WithAnnotation` directly, and step 5 re-checks `Reachability` on whatever the diff hands it.

## Open Questions

None blocking. Four things are decided rather than left open, and are flagged here so a reviewer
disagreeing with one knows where to push:

- The same-name-different-instance residue is accepted and named (§3.3) rather than guarded, because
  guarding it costs the `StringComparisons` dependency Option A was chosen to avoid.
- The identity guard is kept even though the callback overload does not need it today (§2.1), for
  idempotence under an upstream Aspire fix.
- A fired reachability re-check (§3 step 5) warns and leaves the annotation on the facade rather
  than removing it, so that one branch diverges instead of failing fully closed. It cannot execute
  today; the alternative is deleting an object this package did not add.
- #353 stays out of scope (§2.5), which means a guest-language AppHost's endpoint callback is still
  not covered by this or any other shadow.

---

*Reviewed once (spec round, per implement-ticket Phase 4): attack surface, false claims about the
code and about Aspire 13.5.2, contradictions, scope against the acceptance checklist, and
two-way-readable requirements. Two must-fix findings — the pre-call vs post-call `real` read in §3
step 4, and the unspecified behaviour of the reachability re-check — are applied above, along with
five clarifications. No finding changed the chosen mechanism.*
