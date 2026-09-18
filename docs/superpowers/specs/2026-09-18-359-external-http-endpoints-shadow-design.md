# Gating `WithExternalHttpEndpoints` at the call site (#359)

**Date:** 2026-09-18
**Status:** Draft
**Resolves:** #359 (`WithExternalHttpEndpoints` mutating `IsExternal` on an out-of-band service with
no gate and no warning, from C#).
**Builds on:** [`2026-09-17-372-endpoint-mutation-detector-design.md`](2026-09-17-372-endpoint-mutation-detector-design.md)
(#372), which shipped the post-hoc detector that reverts this mutation at `BeforeStartEvent`. That
detector stays; §3 states what each of the two mechanisms owns once this lands, and retires the
claims — in production code and in a test comment — that this shadow could never exist.
**Extends:** [`2026-09-14-334-endpoint-skip-gate-design.md`](2026-09-14-334-endpoint-skip-gate-design.md)
(#334) and [`2026-09-15-335-raw-withendpoint-gate-design.md`](2026-09-15-335-raw-withendpoint-gate-design.md)
(#335) — the C# call-site gate. This is the tenth gated endpoint surface, built from the same
template. Neither document changes.
**Out of scope:** #371 (`WithCommand`'s remove-then-add mirror). The `WithCommand` shadows are read
here only as evidence about `[OverloadResolutionPriority]` (§4.1), and nothing about them changes.

**Measured against** the repo at `c9301b7` (`origin/main`), and `Aspire.Hosting` **13.5.2**, the
floor `global.json`/`Directory.Build.props` pin. Aspire's `WithExternalHttpEndpoints<T>` body is
quoted from #359's own decompilation (`ilspycmd 11.0.0.9375`) and is not re-decompiled here.

---

## 1. What this document settles

The shadow itself is not the uncertain part. `WithHttpEndpoint`
(`src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:266-279`) and
`WithHttpsEndpoint` (`:288-300`) are a working in-file template — `[AspireExport]`, a non-generic
receiver of `IResourceBuilder<ServiceResource>`, `if (GateEndpointCall(builder)) return builder;`,
then a fully-qualified static delegate call — and #359's shadow is that template with no arguments to
forward. Nine call sites already use it.

Two questions are genuinely open, and they are what this document decides:

1. **What the skip warning says** (§2). The gate's current label names three methods the developer
   may not have called.
2. **How this reconciles with #372's detector** (§3). One mechanism must own the reporting for this
   call, and two artefacts currently assert in writing that a shadow here is impossible.

§4 covers the lower-uncertainty items: overload resolution, the test plan, the missing `url`-leg
detector coverage, and the CHANGELOG.

### 1.1 What the change is, stated once

Aspire's method (#359, decompiled) walks the resource's **existing** `EndpointAnnotation` instances
and writes `item.IsExternal = true` on each one whose `UriScheme` is `http` or `https`. It never
calls `WithAnnotation`, so `ServiceResourceBuilder.WithAnnotation`
(`src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs:128-156`) — this package's one
interception point for new annotations — never sees it. On a `url` or `kubernetes` service the
annotation it writes is the same instance the real resource holds, so for `kubernetes` the write
lands on the real `kubectl port-forward`'s endpoint.

The fix adds one method:

```csharp
[AspireExport]
public static IResourceBuilder<ServiceResource> WithExternalHttpEndpoints(
    this IResourceBuilder<ServiceResource> builder)
{
    if (GateEndpointCall(builder, "WithExternalHttpEndpoints"))
    {
        return builder;
    }

    return Aspire.Hosting.ResourceBuilderExtensions.WithExternalHttpEndpoints(builder);
}
```

The second argument is §2's decision; everything else is the template.

**Note the shape difference #359 asked about.** Every overload #335 shadowed is scoped to one
endpoint name; this one loops over all of them. `GateEndpointCall` is unaffected by that: it reads
only the builder's `Source` and asks `Reachability.IsUnreachable(typeof(EndpointAnnotation), source)`
(`ServiceSourcesBuilderExtensions.cs:510-523`) — there is no name lookup in it to adjust. The
single-name shape lives in Aspire's method, on the far side of the gate, and is never reached when
the gate fires. `IsUnreachable` for `EndpointAnnotation` is unconditionally true for both out-of-band
sources (`ServiceResourceBuilder.cs:65-68`), so "skip all of them" and "skip this one" reduce to the
same answer.

---

## 2. Decision 1 — what the skip warning says

### 2.1 The problem

`GateEndpointCall` records its skip as:

```csharp
ServiceSourcesWarnings.For(builder.ApplicationBuilder)
    .AddSkip(builder.Resource.Name, source, Reachability.CapabilityLabel(typeof(EndpointAnnotation)));
```

`CapabilityLabel` maps `EndpointAnnotation` to the literal
`"WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"` (`ServiceResourceBuilder.cs:92`). Routed through
the gate unchanged, a developer who wrote `WithExternalHttpEndpoints()` would read that three
methods they never called were skipped, and would not find their own call named anywhere.

(The notes file records this label as `WithHttpEndpoint/WithHttpsEndpoint`. That was the #334 value;
#335 widened it, and `EndpointSkipGapRepro.cs:45` and `:64` assert the old two-method text as a
`Contains` **substring** of the current three-method one. Both readings of the file are consistent;
the current literal is the three-method one.)

### 2.2 Options considered

**Widen the shared label** to `"WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint/WithExternalHttpEndpoints"`.
Cheap — the existing assertions are `Contains` on substrings of that string, so appending at the end
keeps every one of them green — and it has precedent: #335 widened this same label from two names to
three.

Rejected. The precedent is narrower than it looks. The three names #335 grouped are genuinely one
call the developer might have written any of: `WithHttpEndpoint` and `WithHttpsEndpoint` *forward to*
`WithEndpoint`, all three resolve to the same named-endpoint lookup, and the method's own remark says
so — "a skip triggered by any of them is the identical capability, reported once under one label
rather than three" (`ServiceResourceBuilder.cs:71-88`). `WithExternalHttpEndpoints` is not that. It
forwards to none of them, takes no endpoint name, and writes one field across every endpoint. Adding
it to the group would make the label name four methods of which at least three are always wrong,
across all ten gated surfaces — the existing problem made worse for the nine that do not have it.

**Give this surface its own reason string.** Chosen.

### 2.3 The decision

`GateEndpointCall` takes an optional capability label:

```csharp
private static bool GateEndpointCall(IResourceBuilder<ServiceResource> builder, string? capability = null)
```

and passes `capability ?? Reachability.CapabilityLabel(typeof(EndpointAnnotation))` to `AddSkip`.
The nine existing call sites are unchanged and keep the shared label; the new shadow passes
`"WithExternalHttpEndpoints"`.

Blast radius is zero: `CapabilityLabel` is not touched, so the `EndpointSkipGapRepro`,
`EndpointMutationDetectorTests` and design-doc assertions on the three-method literal all still hold,
and the other nine surfaces emit exactly the text they emit today.

`ServiceSourcesWarnings` already handles two distinct capabilities on one service without further
work. `Describe` groups skips by `(ServiceName, Source)` and `DescribeCalls`
(`ServiceSourcesWarnings.cs:415-425`) renders a single capability as itself and several as a count
plus a tally, so a service that calls both surfaces gets one message reading
`skipped 2 calls (WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint, WithExternalHttpEndpoints)`. That
is the behaviour the grouping was built for, not a new case.

**Why not a `CapabilityLabel` overload keyed on something other than the annotation type?**
`CapabilityLabel` answers "what did the developer call, given this annotation type", and the
annotation type does not distinguish these two callers — that is the whole reason the question
arises. Passing the answer from the one call site that knows it is the smaller change and keeps the
type-keyed table honest for the paths that have nothing else to go on
(`ServiceResourceBuilder.cs:140`, `:223`).

### 2.4 The exact literal text a developer sees

Assembled from `SkipReason` (`ServiceSourcesWarnings.cs:301-305`), `OutOfBandSourceAdvice.SourceDetail`
and `SwitchSourceRemedy`, with the service names the test fixtures use. These are the strings the
tests in §4.2 assert.

**`url` source, service `inventory`:**

> Service 'inventory': skipped WithExternalHttpEndpoints because its source is 'url' — it resolves to a fixed, already-running URL with no local process to configure. The service is expected to be configured wherever it actually runs. To make this AppHost's configuration and start ordering apply instead, give it a 'local' or 'container' source in servicesources.local.json — which works only where its 'servicesources.yaml' entry already declares that source: a 'repository' or 'repositoryRef' for 'local', a 'container' block for 'container'.

**`kubernetes` source, service `orders`:**

> Service 'orders': skipped WithExternalHttpEndpoints because its source is 'kubernetes' — it resolves to a 'kubectl port-forward' in front of an already-running service, so the configuration would reach kubectl rather than the service. The service is expected to be configured wherever it actually runs. To make this AppHost's configuration and start ordering apply instead, give it a 'local' or 'container' source in servicesources.local.json — which works only where its 'servicesources.yaml' entry already declares that source: a 'repository' or 'repositoryRef' for 'local', a 'container' block for 'container'.

**Following the advice literally.** A reader of either message is told to give the service a `local`
or `container` source, and told in the same clause what the catalog entry must already carry for that
to work. For a `kubernetes` service that came from a `repository` + `project` + `kubernetes:` entry
the `local` half is available and works. For a url-only entry the message says, in the clause itself,
that neither option is theirs to make — which is what #372's own "remedy no longer dead-ends" fix
(`CHANGELOG.md:208-219`) put there, and this surface inherits it unchanged. That is the right outcome
for a skip: there is no `servicesources.local.json` setting that makes an out-of-band endpoint
external, so offering one would be the dead end that fix removed.

`WithExternalHttpEndpoints` reads as a single call in the message, with no count and no tally, because
`DescribeCalls` renders one capability as itself.

### 2.5 What this adds to the attack surface: nothing

Worth stating explicitly, because #372's own `### Fixed` entries record a log-forging bug in these
exact messages — a service name carrying a newline or an apostrophe could close the quote and write a
sentence of its own into a log a reader trusts (`CHANGELOG.md:195-206`).

The new capability label is a **compile-time string literal in this package's own source**. It is
never derived from a catalog key, a yaml value, a guest-language argument or any other caller-controlled
input, so it cannot forge a line and needs no escaping. The two names in the same sentence that *are*
caller-controlled — the service name, and the endpoint name in the detector's message — already go
through `ServiceSourcesWarnings.Label` and are untouched here.

The shadow itself takes **no parameters at all**, so there is no argument to validate, nothing to
forward, and no new value crossing into Aspire. It either returns early or calls a method with a
single receiver. `GateEndpointCall`'s new parameter is likewise only ever passed a literal from
within this file; making it `string?` with a null default means a future call site that forgets it
falls back to the existing shared label rather than to an empty or absent capability name.

---

## 3. Decision 2 — the division of labour with #372's detector

### 3.1 How many warnings, and which one

**Exactly one: the skip warning from §2.4. The detector emits nothing for this call.**

This is structural, not a coincidence to be tested for and hoped about. The shadow returns before
Aspire's method runs, so no `EndpointAnnotation` field is written — not transiently, not at all. At
`BeforeStartEvent` the detector's `Reconcile` finds the endpoint in its snapshot, `Restore` compares
the captured `Fingerprint` against the live annotation, `current == recorded` short-circuits to an
empty `changed` list (`EndpointMutationDetector.cs:180-183`), nothing is added to `reverts`, and
`ReportRevertsNow` returns early on `reverts.Count == 0` (`ServiceSourcesWarnings.cs:265-276`, the
guard at `:273`).

**No change to `EndpointMutationDetector.cs` is required for this**, beyond the remark correction in
§3.3. The detector is already silent when it finds nothing.

The one warning is delivered at `BeforeStartEvent` either way — skips are buffered until there is a
logger (`ServiceSourcesWarnings` class remarks) — but it is *recorded* at call time, which is what
makes it a skip rather than a revert, and what lets it group with the service's other skipped calls
into one message instead of standing alone as a second.

### 3.2 The existing test at `EndpointMutationDetectorTests.cs:293`

`ExternalHttpEndpoints_OnKubernetesSource_IsRevertedAndReported` calls
`service.WithExternalHttpEndpoints()` from namespace `Aspire.Hosting.ServiceSources.Tests`. C#
extension lookup walks namespace declarations innermost-outward and stops at the first level with an
applicable candidate, so that call binds to this package's shadow (in `Aspire.Hosting.ServiceSources`,
one level nearer than `Aspire.Hosting`) the moment the shadow exists — the mechanism
`OverloadResolutionProbe.cs:7-14` documents and `OverloadResolutionProbeGuardTests` protects. The
call is then skipped, nothing mutates, and every assertion in the test is false: `IsExternal` is not
`true` after the call, and no revert warning is produced.

**Decision: retarget, do not delete.** Split it into two, because it was carrying two different
things at once.

- **The detector's `IsExternal` coverage is real and must survive.** `IsExternal` is one of the
  eleven fields in the detector's `Fingerprint`, and `EndpointUpdateContext` exposes it as settable
  (#372 design §5.2), so a guest-language AppHost can still write it and the detector is still the only
  thing that catches that. This test is the detector suite's only `IsExternal` case. Retarget it to
  the path that stays unshadowed:
  `GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("IsExternal", true))`, renamed
  `ChangedIsExternal_OnKubernetesSource_IsRevertedAndReported` to match its siblings
  (`ChangedTargetHost_…`, `ChangedProtocol_…`). Its revert-warning assertion is unchanged — the
  message names the field, not the method that wrote it, so
  `"endpoint 'https' was changed after this service resolved (IsExternal) and has been put back"`
  still holds verbatim.
- **The `WithExternalHttpEndpoints`-specific claim becomes this ticket's own assertion**, in a new
  test in the same file: `WithExternalHttpEndpoints()` on a `kubernetes` service leaves `IsExternal`
  false at call time, and publishing `BeforeStartEvent` yields **exactly one** warning, which is the
  §2.4 skip and not a revert. That is checklist item 7's "one, not two", asserted directly. It stays
  in the detector's file rather than in `EndpointSkipGapRepro` because it is the interaction between
  the two mechanisms, and because only this file's fixtures
  (`CreateBuilderThatCanStart`, `PublishBeforeStartEventCapturingWarningsAsync`) can publish the
  event.

Deleting outright was rejected: it would drop the detector's only `IsExternal` case and leave the
"not two warnings" claim untested. Rewriting in place as one test was rejected: one test cannot
assert both that the detector reverts an `IsExternal` change and that it does not.

### 3.3 The comment at `:290-292` — and a second, worse one in production code

The test comment reads:

> The argument for a state-keyed detector over another per-method shadow: WithExternalHttpEndpoints
> is a public Aspire method that sets IsExternal directly on existing annotations, so no gate ever
> sees it. Caught here with no WithExternalHttpEndpoints-specific code at all.

Both sentences become false: a gate does see it, and there is now
`WithExternalHttpEndpoints`-specific code.

**The same claim is also in shipped production XML docs**, which the task brief did not name and
which matters more because it is on the public-facing type:
`src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs:12-17` says the call-site gate
"can only stop calls that reach this package. A guest-language AppHost invokes Aspire's own
endpoint-callback capabilities directly, and Aspire's `WithExternalHttpEndpoints` writes existing
annotations in place, so **neither is interceptable at all**." The second half of that sentence is
wrong after this lands and must be corrected in the same change.

Both are replaced with the statement below, phrased for each site.

### 3.4 The post-#359 division of labour

This is the paragraph the two corrected comments say in their own words, and the thing this document
exists to put on the record.

**The shadow is the primary defence for any mutation surface this package can name and bind from
C#.** That means a public Aspire extension method, applicable to `IResourceBuilder<ServiceResource>`,
whose whole signature is expressible in types this package can reference. For such a surface the
shadow is strictly better than the detector on three counts: the mutation never happens at all rather
than happening and being undone; the warning is a *skip*, recorded at the call site and grouped with
the service's other skipped calls, rather than a *revert* standing alone; and no reference taken
between the call and `BeforeStartEvent` ever observes the mutated value. That is what acceptance
criterion 2 means by "stricter than #372's mutate-then-revert". #359 is the tenth such surface.

**The detector is the backstop for every mutation surface that cannot be shadowed, and for the ones
nobody has enumerated yet.** Two populations, both permanent:

- The guest-language endpoint-callback capabilities (`withEndpointCallback`,
  `withHttpEndpointCallback`, `withHttpsEndpointCallback`). Their shape, `EndpointUpdateContext`, is
  `internal sealed` to `Aspire.Hosting.dll`, so no C# shadow can project them — measured in #353 and
  not re-argued. This population is closed under "no shadow will ever exist".
- Anything that writes an `EndpointAnnotation` in place and has not been found yet. #359 *is* the
  proof that this population is non-empty: it was an eighth call surface that #335's own decompiled,
  name-based audit missed, found only by a reviewer reading #357. Being keyed on state rather than on
  method names, the detector needs no entry per surface and covers the next miss before anyone files
  it.

**The two cannot double-report**, for the reason in §3.1: a skipped call leaves no state change to
find. They also cannot disagree — both restore the same outcome, the endpoint as its source
registered it.

**What #372's argument actually was, and what #359 does to it.** #372 argued that a detector was
needed *as well as* shadows, because enumerating methods cannot be complete. That argument is intact
and this ticket is its best evidence. What is retired is the narrower claim the two comments make —
that `WithExternalHttpEndpoints` in particular is uninterceptable, and that the detector is therefore
the *only* thing that can cover it. It was interceptable; the audit that concluded otherwise was
reading a method-name sweep, not the binding rules. Where both mechanisms can cover a surface, the
shadow owns it and the detector goes quiet on it by construction.

---

## 4. The lower-uncertainty items

### 4.1 `[OverloadResolutionPriority(1)]` is not needed

`WithCommand` carries it (`ServiceSourcesBuilderExtensions.cs:455`) for a reason that does not apply
here, and the in-file comment says so: without it, *this package's own two* `WithCommand` shadows —
the `CommandOptions` one and the `[Obsolete]` legacy one — are equally applicable to the three-argument
call an AppHost writes, and that call stops compiling with CS0121. It disambiguates a collision
between two sibling shadows, not between a shadow and Aspire.

`WithExternalHttpEndpoints` has no sibling. Aspire declares exactly one
(`WithExternalHttpEndpoints<T>(this IResourceBuilder<T>) where T : IResourceWithEndpoints`) and this
package will declare exactly one. Both are applicable to an `IResourceBuilder<ServiceResource>`
receiver with identical parameter types after inference, and C#'s better-function-member tie-break
prefers the non-generic member over the generic one — the same rule the nine existing endpoint
shadows already rely on, argued in the `WithHttpEndpoint` XML doc
(`ServiceSourcesBuilderExtensions.cs:259-263`) and in #335. No attribute.

**This is asserted by compilation, not by reasoning.** A new `OverloadProbe.CallWithExternalHttpEndpoints`
entry in `test/Aspire.Hosting.ServiceSources.Tests/OverloadResolutionProbe.cs` puts the call in
namespace `ServiceSourcesOverloadProbe`, which is a descendant of neither `Aspire.Hosting` nor
`Aspire.Hosting.ServiceSources`, so both extension classes compete as genuine equals and ordinary
betterness decides. If the non-generic shadow did not win there, the probe would either not compile
(CS0121) or would bind Aspire's method and the behavioural test through it would fail. A test written
directly in `Aspire.Hosting.ServiceSources.Tests` proves nothing about resolution — the lookup stops
at the shadow's own namespace before Aspire's is ever consulted — which is why the probe exists.

**Inherited precondition, not a new one:** the shadow is only in scope where
`Aspire.Hosting.ServiceSources` is imported. Every AppHost reaching this code already calls
`AddService`, which lives in the same static class, so the import is already there. This is the same
precondition all nine existing shadows carry and it is not re-argued.

### 4.2 Test plan

**`test/Aspire.Hosting.ServiceSources.Tests/OverloadResolutionProbe.cs`** — one new entry:

- `CallWithExternalHttpEndpoints(IResourceBuilder<ServiceResource> service) => service.WithExternalHttpEndpoints();`
  Compiling at all is half the assertion (§4.1); the tests below route through it for the other half.

**`test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`** — two new tests, in that
file's established style (existing `url` fixture; a `kubernetes` fixture added, matching
`EndpointMutationDetectorTests.cs:44-55`):

- `ExternalHttpEndpoints_OnUrlSource_IsSkippedAndReported` — snapshot every endpoint's `IsExternal`,
  call through `OverloadProbe`, assert every one is still `false` **at call time** (criterion 2: not
  mutated, not even transiently), and assert exactly one warning whose text equals the `url` string
  in §2.4. Asserting the full literal, not a `Contains` on the method name, is what makes this the
  "use the output" evidence — it catches a label that is right but a sentence that reads wrong.
- `ExternalHttpEndpoints_OnKubernetesSource_IsSkippedAndReported` — the same against the
  `kubernetes` fixture and the `kubernetes` string in §2.4.

**`test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs`** — one test retargeted,
two added:

- `ChangedIsExternal_OnKubernetesSource_IsRevertedAndReported` — the retarget of the existing
  `:293` test onto `GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("IsExternal", true))`
  (§3.2). Assertions otherwise unchanged.
- `ChangedIsExternal_OnUrlSource_IsRevertedAndReported` — **checklist item 8**, the `url` leg the
  shipped suite lacks. Same shape against the `url` fixture, whose source registers a single `https`
  endpoint (`UrlSource.cs:53-61`) so the `"https"` callback name resolves. Written through the
  callback rather than through `WithExternalHttpEndpoints` for the same reason as its `kubernetes`
  sibling: after this lands, a `WithExternalHttpEndpoints`-driven url test would exercise the shadow
  and assert nothing about the detector.
- `ExternalHttpEndpoints_OnKubernetesSource_IsSkippedAndTheDetectorAddsNothing` — **checklist item 7**.
  Call through `OverloadProbe`, assert `IsExternal` is false immediately, publish `BeforeStartEvent`,
  assert `Assert.Single(warnings)` and that the single warning is the §2.4 `kubernetes` skip and
  contains no `"has been put back"`. `Assert.Single` is the assertion that fails if a second warning
  ever appears; a `Contains`-only test would pass with two.

**`test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`** — add
`"withExternalHttpEndpoints"` to `ExportedIds_MatchTheKnownSurface`'s `expected` array
(`:90-122`). That test asserts set equality against a hand-maintained list, so a new `[AspireExport]`
fails it until the list is updated; updating it is the intended way to record a deliberate surface
addition. The id is flat and camelCased because the shadow is a static extension method with no
explicit `Id` — the same derivation as the `withHttpEndpoint`/`withHttpsEndpoint`/`withEndpoint`
entries already in that list.

**Not written:** a sample AppHost change. The existing samples do not call this method, and adding a
call to make a point is scaffolding the review loop would rightly flag.

### 4.3 The verify leg that cannot run here

Checklist item 5 puts a new `[AspireExport]` on the guest-language surface. The TypeScript export-surface
leg (`aspire restore` + `npx tsc --noEmit` over `samples/DemoAppHostTypeScript` and
`samples/DemoAppHostTypeScriptCodeCatalog`) is the leg most likely to catch a mistake in it, and it
**cannot run locally** — `node`, `npm` and the pinned Aspire CLI `13.5.3` are all absent from PATH
(re-confirmed 2026-09-18). It must be named as CI-verified-only in the PR body, never implied to have
passed.

**The risk it carries is bounded**, and does not change any decision above. Whether a guest-language
`withExternalHttpEndpoints` call binds to this package's export or to Aspire's is the identical
mechanism that already governs `withHttpEndpoint`, `withHttpsEndpoint` and `withEndpoint`, all three
of which ship working today. And if it somehow did not bind here, the outcome would be #372's
detector reverting the mutation and reporting it — the behaviour that ships on `main` right now.
There is no failure mode in which the developer is worse off than before this change.

### 4.4 CHANGELOG reconciliation

The stale text is `CHANGELOG.md:159-161`, inside #372's `### Added` entry:

> This measures the state rather than the call, so it also **covers** `WithExternalHttpEndpoints`
> ([#359]), which sets `IsExternal` on existing endpoints directly from C# and was likewise
> unreported; that issue stays open and separately owned.

**Both halves are edited, not deleted.** The "covers" claim stays true for anything that reaches the
detector, and deleting it would lose the fact that the detector is what catches an unenumerated
surface — §3.4's whole point. What goes is "that issue stays open and separately owned", and the
implication that the detector is the only thing covering this method. The sentence becomes a
cross-reference to #359's own entry.

**Editing an already-merged entry is correct here**, not a liberty: #372 and #359 both land inside
`## [Unreleased]`, above the last tag `v0.5.1` (`8d981e9`, 2026-09-07). No reader of a released
changelog ever sees the intermediate state, so the release notes should describe where the code
ended up, not the order two PRs arrived in.

**#359's own entry goes under `### Fixed`.** The repo reserves that section for bugs in already-*released*
behaviour, and this qualifies: `WithExternalHttpEndpoints` silently mutating a `url`/`kubernetes`
service's endpoint shipped in v0.5.1 and earlier. #372's revert behaviour is itself unreleased, so
"this used to be reverted and is now skipped" is not the change a v0.5.2 reader experiences — what
they experience is that a call which used to apply silently now skips with a warning. The entry
should say that, mention that the detector remains the backstop for the guest-language callbacks, and
cite `([#359])`. **`[#359]` is already defined at `CHANGELOG.md:1732`**; no new link definition.

No `### Breaking` entry. The behaviour change is confined to `url` and `kubernetes` services, where
this package's documented contract has always been that configuration is skipped with a warning, and
it matches what the other nine endpoint surfaces already do. `local` and `container` are untouched.

---

## 5. What is deliberately not done

- **`Reachability.CapabilityLabel` is not changed.** §2.2.
- **`EndpointMutationDetector`'s behaviour is not changed.** Only its class remark (§3.3). It is not
  made aware of the shadow, not given a suppression list, and not told which surfaces are gated —
  all of which would reintroduce the per-method coupling it was built to avoid, to solve a problem
  (§3.1) that does not exist.
- **The other nine gated surfaces keep the shared label.** Per-surface reason strings for all ten
  would be a larger, separately arguable change; nothing in #359 requires it, and the three names the
  shared label groups are defensible for the calls that produce it (§2.2).
- **The upstream ask is not filed.** Having Aspire route this method's write through `WithAnnotation`
  would make the shadow unnecessary, and is the cheaper long-term fix. It is acknowledged and
  deliberately filed nowhere, on the same footing as #372 §9.
- **#371 is untouched.** Out of scope.

---

## 6. Acceptance criteria mapped

| # | Criterion | Where |
|---|---|---|
| 1 | Non-generic overload routed through `GateEndpointCall` | §1.1, §4.1 |
| 2 | Skipped on `url`/`kubernetes`, never mutated, not transiently | §1.1, §4.2 (call-time assertion) |
| 3 | Exactly one skip warning, `SkipReason` shape not `RevertReason` | §2.3, §2.4, §3.1 |
| 4 | Fully-qualified static delegate on a reachable source | §1.1 |
| 5 | `[AspireExport]`, `CatalogExportsTests` updated | §4.2, §4.3 |
| 6 | `EndpointSkipGapRepro`-style coverage over both fixtures | §4.2 |
| 7 | Detector interaction sound; one warning not two; `:293` reconciled | §3.1, §3.2, §3.3 |
| 8 | Missing `url`-leg detector test added | §4.2 |
| 9 | `CHANGELOG.md:159-161` stale claim does not survive | §4.4 |

---

## 7. Open Questions

None blocking. The two decisions this document was written to settle are settled (§2.3, §3.2/§3.3),
and every remaining item has a decision with its reasoning stated.

Recorded for the implementer rather than for the human, because none of them changes the design:

- **§4.3's TypeScript leg cannot be run locally** and must be named as CI-only in the PR body. Its
  risk is bounded and its worst case is today's behaviour; it is not a reason to hold the plan.
- **The exact strings in §2.4 are assembled from source, not observed from a run.** The implementer
  should paste the real assertion failure output into the notes file on first run rather than
  trusting this document's reconstruction, and correct §2.4 if they differ.
- **`EndpointSkipGapRepro` has no `kubernetes` fixture today** and needs one added (§4.2). Copying
  `EndpointMutationDetectorTests.cs:35-55` — `FakePortAllocator`, `KubernetesDefinition`,
  `Kubernetes` — is the obvious route; whether to share it between the two files or duplicate it is
  an implementation call, not a design one.
