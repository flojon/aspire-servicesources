# A post-hoc endpoint-mutation detector for out-of-band sources (#372)

**Date:** 2026-09-17
**Status:** Draft
**Resolves:** #372 (design the detector #353 deferred).
**Covers, without closing:** #359 (`WithExternalHttpEndpoints` mutating `IsExternal` on an
out-of-band service, ungated from C# today). Being keyed on state rather than on a method name, this
detector catches it as a consequence — that is the central argument for the design (§6, §7 test 7).
#359 is separately owned and open; the relationship belongs in the PR body, and closing it is that
issue's owner's call, not this ticket's.
**Starts from:**
[`2026-09-17-353-guest-language-endpoint-callback-findings.md`](2026-09-17-353-guest-language-endpoint-callback-findings.md)
§4 (the prototype) and §4.6 (the objection this document has to answer), and
[`2026-09-17-352-callback-overload-dualwrite-design.md`](2026-09-17-352-callback-overload-dualwrite-design.md)
§2.5 (which rejected a `BeforeStartEvent` reconciliation sweep). §4 of this document answers §2.5
reason by reason.
**Extends:** [`2026-09-14-334-endpoint-skip-gate-design.md`](2026-09-14-334-endpoint-skip-gate-design.md)
(#334) and [`2026-09-15-335-raw-withendpoint-gate-design.md`](2026-09-15-335-raw-withendpoint-gate-design.md)
(#335) — the C# gate. This document does not change either.

**Measured against** `Aspire.Hosting` **13.5.2**, the floor `global.json` and `Directory.Build.props`
pin, decompiled fresh for this document with `ilspycmd 11.0.0.9375` from
`~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll`. Every Aspire line number
below is from that decompilation. Repo claims are against `main` @ `73254a9`.

---

## 1. What is being closed, and what it is not

A guest-language AppHost reaches `Aspire.Hosting/withEndpointCallback`,
`Aspire.Hosting/withHttpEndpointCallback` and `Aspire.Hosting/withHttpsEndpointCallback` directly on
the handle `addService()` returns. Those capabilities never touch this package, so #335's shadows
cannot see them, and no shadow ever will: the shape a shadow must project names
`EndpointUpdateContext`, which is `internal sealed` to `Aspire.Hosting.dll`. Findings §1–§3 measured
all of that; none of it is re-argued here.

What the callback lands on is the *same* `EndpointAnnotation` instance the real resource holds
(findings §2.1, `same=True`), so a `kubernetes` service's repointed `Port` repoints the actual
`kubectl port-forward`. That is the wrong-process failure mode `Reachability` exists to prevent.

Two scoping facts carried in from the findings and the ticket, stated once so nothing below
overstates:

- The behaviour was measured **in TypeScript only**, through `samples/DemoAppHostTypeScript`'s
  generated SDK. ATS capability ids are language-independent, so it should generalise, but no Java
  or Python bindings were produced. Read "guest language" throughout as *measured in TypeScript,
  inferred elsewhere*.
- The **upstream ask** — have Aspire route the callback path's update branch through
  `WithAnnotation`, or make `EndpointUpdateContext` public — is not superseded by this design and
  remains the cheaper long-term fix. It is acknowledged here and deliberately filed nowhere; see
  §9.

## 2. Decision 1 — revert, warn-only, or fail

**Decision: revert and warn.** Never throw.

### 2.1 Why not fail

This package's whole promise is that a shared `Program.cs` keeps working when one developer flips a
service to `"kubernetes"` in their own `servicesources.local.json`. Every unreachable capability
today *skips with a warning* rather than throwing —
`ServiceResourceBuilder.WithAnnotation`, `ServiceSourcesBuilderExtensions.GateEndpointCall`, and
`UrlSource.DropWaitsOnUrlServices` all do. A detector that threw would turn one developer's local
source choice into a build break for a call another developer wrote. Failing is off the table on
policy, not on cost.

### 2.2 Why not warn-only

Warn-without-revert leaves the mutation in place. For `kubernetes` that means the real
`kubectl port-forward` stays repointed and the warning is a note next to a service that is quietly
talking to the wrong thing. The bar findings §4.1 set — *the observable outcome matches gating at
the call site* — is the right bar, and warn-only does not clear it.

### 2.3 The cost of reverting, measured rather than assumed

The ticket names the cost: a guest AppHost that calls `withEndpointCallback('probe', …)` and then
`getEndpoint('probe')` loses the endpoint under a reference already taken. Reading the real
`EndpointReference` makes that cost **more specific than the ticket states**, and the difference
changes what the warning has to say.

`ResourceExtensions.GetEndpoint(resource, name)` (`ResourceExtensions.cs:725`) returns
`new EndpointReference(resource, endpointAnnotation)` when the annotation exists, and that
constructor (`EndpointReference.cs:311`) assigns `_endpointAnnotation = endpoint` **eagerly**. So:

| When the reference was taken | What removing the annotation does |
|---|---|
| After the callback created `probe` (the ticket's case) | The reference keeps a live pointer to the orphaned instance. It does **not** throw "not defined". `GetAllocatedEndpoint()` returns `null` because DCP never saw the annotation, so resolution fails at environment-gathering with `The endpoint `probe` is not allocated for the resource `orders`.` (`EndpointReference.cs:170`) |
| Before the callback ran (name miss) | `_endpointAnnotation` is never cached; `EndpointAnnotation` throws `BuildMissingEndpointMessage()` — `The endpoint `probe` is not defined for the resource `orders`. Available endpoints: `https`.` |

Neither message names the cause. That is the whole cost of reverting, and it is bounded: it can only
arise for the *added-endpoint* shape, never for the in-place-change shape, where the instance keeps
its identity and its place in both collections and no reference can dangle.

The design pays that cost down rather than avoiding it: **the warning names the endpoint**, so the
log carries `endpoint 'probe' was added after this service resolved` before Aspire's own unexplained failure. See §5.3.

### 2.4 The two shapes get different treatment, and why that is not two decisions

- **Changed in place** — restore the recorded values on the same instance. The gated C# equivalent
  leaves the endpoint exactly as the source registered it, so this *is* the gate's outcome. This is
  the shape that reaches the real resource: `ResolvedService.Bridge` copies the *same* annotation
  instances onto the facade, so a repointed `Port` is repointed on the `kubectl port-forward` too.
- **Added** — remove the instance from the facade. Aspire's create branch is
  `builder.Resource.Annotations.Add(endpointAnnotation)`
  (`ResourceBuilderExtensions.cs:1313`), and `builder.Resource` is the facade, so a smuggled endpoint
  lands on the facade **alone**: the guest-language call never reaches this package, so
  `ForwardingAnnotationsAddedBy` — which is what would otherwise forward it — never runs
  (`GateEndpointCall` returned first, §3.3). The harm is therefore *not* that DCP would create a
  service for it: the facade is never registered in the application model at all
  (`Bridge`/`BridgeUnregistered` call no `AddResource`; `RealToFacadeRegistry` exists precisely
  because model walkers see `real` and never the facade). The harm is that the facade **is** what
  consumers reference — a `WithReference(service)` resolves the facade's endpoints at
  environment-gathering, so a smuggled `probe` would be published to every consumer as a
  service-discovery entry for an endpoint nothing allocates. `GateEndpointCall` returns before
  Aspire's create branch runs, so a gated C# call leaves no such endpoint at all; removing it is the
  gate's outcome.
- **Removed** — re-add the recorded instance, unless an endpoint of that name is already present
  (Aspire resolves endpoints by name with `SingleOrDefault`, which throws on a duplicate, so a
  re-add must never introduce one). Not measured by the prototype, which enumerated only the first
  two. It is included because "changed, added, removed" is the complete set of differences between
  two collections, and a detector that handles two of three is shape-keyed, not state-keyed — which
  is the property §6 and §4 both lean on. No reachable path removes an `EndpointAnnotation` from an
  out-of-band facade today (§3.3), so it costs one branch and no false positives.

Both the added and removed branches also reconcile `real`'s collection when a real resource exists
and the instance is present there — a no-op for every path enumerated above, kept so that the two
collections cannot silently diverge if a future Aspire routes the create branch through
`WithAnnotation`. It is deliberately not the load-bearing part of either branch.

## 3. Decision 2 — is `BeforeStartEvent` early enough? The enumeration

This is #352 §2.5's third objection and the one deliverable the findings explicitly owed. It is
answered by enumerating the readers in the pinned Aspire, not by restating §4.2.

### 3.1 The fixed points

`DistributedApplication.ExecuteBeforeStartHooksAsync` (`DistributedApplication.cs:631`) runs, in
order:

1. `IDistributedApplicationEventingSubscriber.SubscribeAsync` for every registered subscriber —
   **registration only**, and it happens *before* the publish, so anything a subscriber subscribes
   lands behind every composition-time subscriber in the list.
2. `eventing.PublishAsync(beforeStartEvent, …)` — the detector runs here.
3. `IDistributedApplicationLifecycleHook.BeforeStartAsync` for every hook.
4. `EnsureComputeEnvironmentAnnotationsApplied(appModel)`.
5. the `before-start` pipeline step.

It is awaited from both `RunAsync` (`:586`) and `StartAsync` (`:521`), in each case under
`IsPublishMode || IsRunMode` and strictly before `_host.StartAsync` — so publish mode is covered, and
**every `IHostedService` in the application starts after the detector has run.**

`DistributedApplicationEventing.PublishAsync` defaults to `EventDispatchBehavior.BlockingSequential`
and walks the subscription list in order, so "subscription order" is dispatch order.

### 3.2 Every reader of an `EndpointAnnotation`, and when it runs

Derived from the full set of files in `Aspire.Hosting.dll` that name `EndpointAnnotation`.

| Reader | Where | Reached from | Verdict |
|---|---|---|---|
| **DCP model construction** — builds DCP `Service` objects from `r.Annotations.OfType<EndpointAnnotation>()` | `DcpExecutor.PrepareServices` (`DcpExecutor.cs:626`, the read at `:630`) | `DcpExecutor.RunApplicationAsync` ← `ApplicationOrchestrator.RunApplicationAsync` ← `OrchestratorHostService.StartAsync`, an `IHostedService` | after |
| **Endpoint allocation** — effective proxy mode, fixed public port, proxyless port assignment | `DcpExecutor.GetEffectiveIsProxied` / `TryGetEffectiveFixedPublicPort` / `EnsureProxylessEndpointPort` (`:707`, `:738`, `:756`), `ProxylessEndpointPortAllocator` | same | after |
| **Service-discovery environment composition** — where an `EndpointReference` finally resolves | `DcpModelUtilities.cs:200` and `:254`, materialising each resource's `EnvironmentCallbackAnnotation`s | same | after |
| **Dashboard allowed-origins / dashboard env** | `DashboardEventHandlers.GetAllowedOriginsFromResourceEndpoints` (`:699`), called from `ConfigureEnvironmentVariables` (`:488`) | an `EnvironmentCallbackAnnotation`, so the row above | after |
| **Publish-mode manifest bindings** | `ManifestPublishingContext` | `ManifestPublishingCallbackAnnotation` callbacks in the `publish` pipeline step, driven by `PipelineExecutor`, an `IHostedService` | after |
| **Devcontainer port forwarding** | `DevcontainerPortForwardingEventingSubscriber` (`:32`) | subscribes `ResourceEndpointsAllocatedEvent` (`:56`) | after allocation, so after |
| **CLI backchannel** | `AppHostRpcTarget`, `AuxiliaryBackchannelRpcTarget` | RPC against a running app host | after |
| **Resource snapshots for the dashboard** | `ResourceSnapshotBuilder`, `DcpModelUtilities`, `DcpNameGenerator`, `ContainerCreator`, `HostResourceWithEndpoints`, `ServiceWithModelResource` | all inside the DCP executor | after |
| **Dashboard resource shaping** | `DashboardEventHandlers.OnBeforeStartAsync` (`:55`) → `ConfigureAspireDashboardResource` (`:311`) | subscribed in `SubscribeAsync` (`:829`), i.e. in step 1 above → appended behind every composition-time subscriber | same event, **after the detector**. It both reads *and writes*: it clears the dashboard resource's `EndpointAnnotation`s (`:315–318`) and adds fresh ones. Scoped by name to `KnownResourceNames.AspireDashboard`, so it cannot touch a `ServiceResource` facade |
| **Lifecycle hooks** | `IDistributedApplicationLifecycleHook.BeforeStartAsync` | step 3 | after; `Aspire.Hosting` registers none that reads endpoints |
| **`before-start` pipeline step** | `DistributedApplicationPipeline` (`:272`) is an aggregation no-op; its only built-in dependent, `validate-compute-environments` (`:279`), reads compute-environment bindings | step 5 | after, and endpoint-blind |

**Answer: yes**, with the claim stated precisely, because the loose version is false. Endpoints are
of course read *during* AppHost composition — `ResourceExtensions.GetEndpoint` (`:725`) is the case
§2.3 itself turns on, and `WithEndpoint`'s own name lookup is another. Those all run before the
snapshot is taken, so they are not at issue. The claim that matters is:

> **No reader of an `EndpointAnnotation` runs between `builder.Build()` and the `BeforeStartEvent`
> publish**, and every reader of final endpoint state runs after it.

Checked rather than assumed: `DistributedApplicationBuilder.Build` (`:644–667`), the
`DistributedApplication` constructor, the `ApplicationOrchestrator` constructor and all five
`IDistributedApplicationEventingSubscriber.SubscribeAsync` implementations
(`DashboardEventHandlers:825`, `DevcontainerPortForwardingEventingSubscriber:50`,
`RequiredCommandValidationEventingSubscriber:23`, `TerminalHostEventingSubscriber:46`,
`EventingExports.CallbackEventingSubscriber:18`) are all endpoint-free. Everything in the table is
either behind `_host.StartAsync` or — for the two that share the event — appended behind any
composition-time subscriber by construction.

### 3.3 The writers that run *before* the detector — the part the objection did not ask for

An enumeration of readers is only half the answer: a writer that runs before the detector and
changes a fingerprinted field is a **false positive**, which is worse than a missed detection because
it reverts something legitimate.

Five handlers are subscribed to `BeforeStartEvent` in the `DistributedApplicationBuilder`
**constructor** (`DistributedApplicationBuilder.cs:429–491`) — before any AppHost line runs, so ahead
of every subscription this package can make:

| Handler | Touches an `EndpointAnnotation`? |
|---|---|
| `InitializeDcpAnnotations` | No — populates DCP instance annotations |
| `WarnPersistentContainersWithoutUserSecrets` | No |
| `ExcludeDashboardFromManifestAsync` | No |
| `UpdateContainerRegistryAsync` | No — `ContainerImageAnnotation` only |
| **`MutateHttp2TransportAsync`** | **Yes** — `item.Transport = (flag ? "http2" : item.Transport)` over every `http`/`https` endpoint |

`MutateHttp2TransportAsync` writes `Transport`, which is in the fingerprint. It only *changes*
anything when `flag` is true — when the resource carries `Http2ServiceAnnotation`. The single writer
of that annotation is `AsHttp2Service`, whose body is
`builder.WithAnnotation(new Http2ServiceAnnotation())` (`ResourceBuilderExtensions.cs:1724`). On a
facade that is `ServiceResourceBuilder.WithAnnotation`, and
`Reachability.IsUnreachable(typeof(Http2ServiceAnnotation), "kubernetes"|"url")` is **true** —
`Http2ServiceAnnotation` is not in `DecorationAnnotationTypes` and is not `WaitAnnotation`. The
annotation never lands, `flag` is false, and the statement is a self-assignment.

So there is no false positive — but it is a *conditional* absence resting on
`Reachability`'s denylist staying a denylist. §7 pins it with a test rather than leaving it as a
paragraph. (Incidentally, that self-assignment materialises `Transport`'s lazily-derived backing
field on every run; Aspire doing this itself is why §5.2's restore rule is not novel.)

Four further `BeforeStartEvent` subscribers exist in 13.5.2 outside the builder constructor —
`TerminalHostEventingSubscriber.ResolveTerminalHostsAsync` (`:49`),
`TerminalResourceBuilderExtensions.MaterializeTerminalHosts` (`:90`),
`ContainerRegistryResourceBuilderExtensions` (`:166`), and
**`ResourceBuilderExtensions.SubscribeHttpsEndpointsUpdate`** (`:3922`, subscribing at `:3928`).
The first three touch no `EndpointAnnotation`. The fourth does — its own doc comment says it exists
"to conditionally update endpoint URI schemes" — and it is *public*, composition-time, and therefore
able to land ahead of the detector. Nothing in `Aspire.Hosting` calls it, so it cannot fire in an
AppHost that does not opt in; §3.4 names it as a residual rather than letting it pass unrecorded.

On this package's side, the only post-resolve writes to an endpoint tuple in `src/` are:

- `UrlSource` setting `AllocatedEndpoint` — at resolve, **before** the snapshot is taken, and not a
  fingerprinted field.
- `ServiceResourceBuilder.ForwardingAnnotationsAddedBy`, added by #366 — strictly it writes no tuple
  *field*, it forwards annotation *instances* to `real` (`ServiceResourceBuilder.cs:227`), and it is
  double-gated for out-of-band sources anyway: `GateEndpointCall` returns before reaching it, and the
  helper re-checks `Reachability.IsUnreachable` itself. (Listing it is what supersedes the stale claim
  that `UrlSource` was the only post-resolve annotation write in `src/`; the conclusion is unchanged,
  re-verified on `main` rather than inherited.)

One path in `src/` *does* remove an `EndpointAnnotation`: `ServiceResourceBuilder.WithAnnotation`'s
`ResourceAnnotationMutationBehavior.Replace` branch (`ServiceResourceBuilder.cs:149`), where
`TAnnotation` can be `EndpointAnnotation`. It cannot fire for an out-of-band source — the
reachability gate at `:137–142` returns first — so the detector still sees no removal it did not
cause. The absolute form of this sentence ("nothing removes one") was wrong and is corrected here
rather than dropped, because a designer re-running the grep it implies would find that line.
`UrlSource.DropWaitsOnUrlServices`, `RealToFacadeRegistry.MirrorRemoval` and `ServiceWaitRetargeting`
remove `WaitAnnotation`s only.

### 3.4 What remains uncovered

Six residuals, none a known gap, all bounded. The last three are reachable only by code that already
holds the annotation or the real resource — no surface this design polices reaches either — so all
three are named here rather than built against. The first two of those three were found by following
the built output; the third by reading:

1. **A handler subscribed *after* the detector that mutates an endpoint during `BeforeStartEvent`.**
   Nothing in Aspire 13.5.2 or in this package does this — the only in-event endpoint writer is
   `DashboardEventHandlers`, which is name-scoped to the dashboard resource (§3.2). This is the
   residual the findings named, with the same disposition.
2. **A handler subscribed *before* the detector that mutates an endpoint.** §3.3 enumerates every
   such subscriber in `Aspire.Hosting` and finds one that could —
   `ResourceBuilderExtensions.SubscribeHttpsEndpointsUpdate`, which nothing in the box calls. An
   AppHost that opts into it before `AddService()` would have its `UriScheme` change read as an
   author mutation and reverted. Out of reach by construction, and reachable only by explicit
   opt-in.

   Aspire's four built-in `BeforeStartEvent` handlers are *always* ahead of the detector —
   `InitializeDcpAnnotations`, `WarnPersistentContainersWithoutUserSecrets`, `MutateHttp2TransportAsync`
   and `ExcludeDashboardFromManifestAsync`, measured in that order off the subscription list. Only the
   third writes a fingerprinted field (`Transport`), and only for an `AsHttp2Service` annotation that
   `Reachability` stops landing on an out-of-band service, which
   `AsHttp2Service_OnKubernetesSource_NeitherLandsNorRevertsTheTransport` pins. A future Aspire adding
   an endpoint-mutating built-in is therefore a known exposure rather than a surprise.
3. **Endpoint readers and writers in packages other than `Aspire.Hosting.dll`.** §3.2's enumeration
   is of that assembly, which is what `Directory.Build.props` pins and what the callbacks live in.
   An integration package (`Aspire.Hosting.Azure`, community hosting packages) can subscribe
   `BeforeStartEvent` at composition time and be dispatched before the detector. Not enumerable
   here, because the set is open; named so that a future report of a reverted-legitimate-change is
   diagnosed against this list rather than from scratch.
4. **`AllocatedEndpoint` is outside the fingerprint.** For a `url` service `UrlSource` sets it eagerly,
   and it is the value consumers actually resolve — so code that rewrote it *and* a watched field
   would see the watched field restored under a message saying the endpoint "has been put back" while
   the allocated value still pointed elsewhere. Deliberately not added: `EndpointUpdateContext` does
   not expose it (reflected, not assumed), the C# overload that would hand it over is shadowed and
   gated, and it is a reference-compared object whose re-materialisation by Aspire would cost the
   no-false-positives guarantee of §3.3 — which is load-bearing in a way this residual is not.
5. **The re-add's `real`-side guard can decline.** If `real` already holds a *different*
   `EndpointAnnotation` under the recorded name, the original is restored onto the facade and not onto
   `real`, under the same "has been put back" line. The guard is there because Aspire resolves an
   endpoint by name with `SingleOrDefault`, which throws on a duplicate, so declining is the safe
   half of the trade. Unreachable through the policed surfaces — the guest holds only the facade,
   Aspire's create branch adds to the facade, and the removal loop mirrors onto `real` — so it needs
   code that already has `real` in hand.
6. **A mutation made on `real` alone.** Add-detection walks the facade's annotations, so an endpoint
   added to or removed from the real resource without touching the facade is neither reverted nor
   reported. Reaching `real` means resolving the registered resource out of the model by name and
   editing it directly, which is an author who already has full control of what the AppHost composes —
   outside the threat model of §2, where the mutating party is a guest-language script holding only
   the facade. Named so that it is a stated residual rather than an unexamined one.

## 4. Decision 6 — answering #352's design §2.5 head-on

§2.5 rejected "reconcile facade-only annotations onto `real` at `BeforeStartEvent`" (its option E) and
noted that it was the only option that would also cover #353. Anyone designing here meets that
rejection first, so it is answered rather than worked around.

| §2.5's reason | Does it carry over? |
|---|---|
| **1. "Converts a fail-closed design into a fail-open one."** | **No — inverted.** Option E copies facade annotations *onto* `real`, carrying a mutation through. This design restores the resolve-time values, drops what was smuggled in, and reports a skip. That is the fail-closed direction §2.5 is itself defending, applied at a later moment. |
| **2. "Fights code that deliberately un-mirrors"** (`DropWaitsOnUrlServices`, `ServiceWaitRetargeting`, `RealToFacadeRegistry`) | **No, for the endpoint-only scope — and this is now measured, not asserted.** Option E compares facade against `real`; this design never does. It compares the facade against *its own resolve-time snapshot*, for `EndpointAnnotation` alone. §3.3 records the check: the endpoint tuple is the one piece of state nothing in `src/` writes, removes or un-mirrors after resolve. Every named un-mirroring path operates on `WaitAnnotation`. **The reason does bite the moment the scope widens**, which is exactly why §6 says endpoints only. |
| **3. "Its timing is unproven."** | **It carried over, and §3 discharges it.** §2.5 was right that findings §4.2 established only that `BeforeStartEvent` precedes DCP and fires in both modes, without enumerating the readers. §3.2 enumerates them from the pinned assembly; §3.3 goes further and enumerates the *writers* that precede the detector, which is where the only real hazard turned out to be. §7 turns both into tests, so the answer stays true as Aspire moves. |

One line of §2.5 is simply overtaken — "#353 stays uncovered either way; it is `internal` API, and
widening this ticket to chase it would buy coverage of an **unconfirmed** gap". The gap was confirmed
by measurement in #353. The scoping decision that line justified was right regardless: #366 was not
the ticket to widen, and this is the ticket that widens it.

On the other side of the ledger, #366 shipped `ServiceResourceBuilder.ForwardingAnnotationsAddedBy` —
a reference-identity diff of a resource's annotations across a call. The mechanism is therefore
already accepted in-tree; what this document argues is its placement (§5.1) and its direction (§2).

## 5. Fix shape

Two new files, two one-line call sites, no change to `Reachability`, to `GateEndpointCall`, to
`ServiceResourceBuilder.WithAnnotation`, or to any public signature. The second new file is
`OutOfBandSourceAdvice`, which holds the clauses the messages share (§5.4). Four existing files
change, all of them message text or the call sites above: `ServiceSourcesWarnings` gains the revert
sentence this design needs and the immediate reporter that carries it (§5.4);
`ServiceConfigurationExtensions`, `UrlSource` and `LocalProjectSource` take the shared clauses in
place of their own drifted copies; `ResolvedService` carries the two call sites — five in all.

### 5.1 Decision 3 — where the snapshot lives

**`ResolvedService.Bridge` and `ResolvedService.BridgeUnregistered`.** The ticket calls this
placement unmeasured; here is the measurement.

- **It is a genuine chokepoint.** Every `ServiceResourceBuilder` in the package is constructed in one
  of those two methods — seven call sites, and no others: `ContainerSource.cs:32`,
  `DeferredCheckout.cs:275` and `:393`, `KubernetesSource.cs:39`, `LocalProjectSource.cs:223` and
  `:415`, `UrlSource.cs:65`.
- **The snapshot point is correct for both out-of-band sources**, read rather than assumed.
  `UrlSource.Resolve` builds the `EndpointAnnotation`, sets `AllocatedEndpoint`, adds it to the
  facade, and *then* calls `BridgeUnregistered` (`UrlSource.cs:53–65`). `KubernetesSource.Resolve`
  calls `.WithEndpoint(port, targetPort, scheme, name, isProxied: false)` on the executable builder
  and *then* calls `Bridge` (`KubernetesSource.cs:31–39`, the `WithEndpoint` at `:37`). In both, every endpoint the source
  registers exists before the bridge runs.
- **It is where the package already does per-service one-time wiring** — `Bridge` already calls
  `ServiceStartupFailureNotices.For` and `ServiceWaitRetargeting.EnsureSubscribed`. This is one more
  of the same shape, not a new kind of thing in a new place.
- **It is the only place that has all four inputs at once**: the facade, the real resource (whose
  collection holds the same instances), the source string, and the builder.

Rejected alternatives:

- **`ServiceResourceBuilder`'s constructor.** The same instant, but that type's job is call-gating;
  putting lifecycle subscriptions in it separates them from the two methods that already own them.
- **`ServiceSources.AddService`.** Above source resolution, so it cannot see the endpoints the source
  registered, and with `DeferredCheckout` in the picture "after `AddService` returns" is not one
  moment.

The detector is installed **only for out-of-band sources** —
`Reachability.OutOfBandSources.Contains(source)`. That filter is what makes the no-false-positives
argument true: for those sources `Reachability.IsUnreachable(typeof(EndpointAnnotation), source)` is
unconditionally true, so any post-resolve endpoint change is by definition one that should have been
skipped. On `local` and `container` an endpoint change after resolve is legitimate and nothing is
installed.

### 5.2 The detector

New file `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs`, `internal static`.

**Fingerprint.** A `readonly record struct` over the ten measured fields — `Name`, `Port`,
`TargetPort`, `UriScheme`, `TargetHost`, `Transport`, `IsExternal`, `IsExplicitlyProxied`,
`ExcludeReferenceEndpoint`, `TlsEnabled` — plus `Protocol`. All eleven are public get/set on
`EndpointAnnotation`, so every one can be written back.

`Protocol` is an addition to the measured list, not a revision of it: none of the ten is dropped.
`EndpointUpdateContext` exposes exactly ten **settable** properties — `Protocol`, `Port`,
`TargetPort`, `UriScheme`, `TargetHost`, `Transport`, `IsExternal`, `IsProxied`,
`ExcludeReferenceEndpoint`, `TlsEnabled` — and one read-only one, `Name`
(`EndpointUpdateContext.cs:17`). Nine of those ten settable ones are on the measured list; `Protocol`
is the tenth (`EndpointUpdateContext.cs:22`), and leaving it out would leave one writable field of the
very context this design exists to police undetected. A Tcp→Udp flip on a `kubectl port-forward`
endpoint is precisely the wrong-process outcome being prevented. With `Protocol` added, the
fingerprint covers **every** field the callback surface can write, plus `Name`.

`IsExplicitlyProxied` rather than `IsProxied` is what the measured list already chose, and reading the
assembly confirms it: `EndpointUpdateContext.IsProxied` reads and writes
`EndpointAnnotation.IsExplicitlyProxied` (`EndpointUpdateContext.cs:127`), so that one field catches
every change the callback surface can make to the proxy setting.

**Keyed by reference identity, never by name.** The snapshot is a
`Dictionary<EndpointAnnotation, Fingerprint>` built with `ReferenceEqualityComparer.Instance`
(contravariance on `IEqualityComparer<in T>` makes the `IEqualityComparer<object?>` singleton bind,
exactly as `ForwardingAnnotationsAddedBy` already relies on at `ServiceResourceBuilder.cs:203`).
`EndpointAnnotation.Name` is a public settable property, so the annotation's identity and its name are
independent: keyed by name, a rename would read as one endpoint disappearing and another appearing,
and the restore would be wrong in both directions. Keyed by instance it is a changed field on a known
instance. **No surface this design polices can perform that rename today** —
`EndpointUpdateContext.Name` is get-only and `WithExternalHttpEndpoints` writes only `IsExternal` —
so this is defence against a field the model leaves open rather than against a measured attack, and
§7 test 5 is labelled accordingly.

**Restore rule: two phases, and write back only what still differs.** Not a blanket rewrite of all
eleven, and not a naive one-pass diff either. Three fields are derived from others:

- `Transport` falls back to `"http"`/`Protocol.ToString()` off `UriScheme` when `_transport` is null
  (`EndpointAnnotation.cs:112–131`); `TlsEnabled` falls back to `UriScheme == "https"` (`:203–208`).
- `Port` and `TargetPort` fall back to each other when `!IsProxied` — which is the case for **both**
  out-of-band sources — and their setters unconditionally overwrite the `_portSetToNull` /
  `_targetPortSetToNull` flags the getters consult (`:54`, `:65`, `:84`, `:95`).

So a callback that writes only `UriScheme` makes `Transport` and `TlsEnabled` *read* as changed, and
writing them back would materialise backing state that was deliberately unset. The rule is therefore:

1. **Read once, compare once.** Compute the whole current fingerprint into a local before writing
   anything, and diff it against the recorded one. Never interleave reads with writes; with
   interdependent getters the two orders give different answers.
2. **Write independent fields first, then re-evaluate the dependent ones.** Restore the differing
   fields among `Name`, `Protocol`, `UriScheme`, `TargetHost`, `IsExternal`, `IsExplicitlyProxied`
   and `ExcludeReferenceEndpoint`. Then, for each of `Transport`, `TlsEnabled`, `Port` and
   `TargetPort`, **re-read the getter** and write the recorded value only if it *still* differs. A
   dependent field that came back into line on its own is left untouched, and its backing field stays
   null.

The result is that the common case writes exactly the mutated field, and no case materialises a
lazily-derived value that was not already materialised.

`IsExplicitlyProxied` is restored, never `IsProxied` — the two public setters keep the backing fields
in lockstep (`:153–157`, `:172–176`), so one restore covers both, and `_isProxied`'s `true` default
makes `IsExplicitlyProxied = null` reconstruct the untouched state exactly. Aspire's own
`SetResolvedIsProxied` (`:364`) writes `_isProxied` alone, but it is DCP-only and therefore runs after
the detector.

**Handler.** Subscribed to `BeforeStartEvent` at install time. It walks
`facade.Annotations.OfType<EndpointAnnotation>()` and the snapshot:

| Case | Action | Entry recorded |
|---|---|---|
| Known instance, fingerprint differs | restore per the two-phase rule above | `endpoint '<recorded name>' was changed after this service resolved (<fields>) and has been put back` |
| Unknown instance | remove from the facade; also from `real.Annotations` if present there (§2.4 — it is not, today) | `endpoint '<name>' was added after this service resolved and has been removed, …` |
| Known instance no longer on the facade | **restore it first**, then re-add, unless an endpoint of that name is already present; likewise on `real` if it is absent there too | `endpoint '<recorded name>' was removed after this service resolved and has been put back` |

Materialise both collections before mutating them; `Annotations` is the live collection. The entry's
endpoint name is emitted through the sanitiser §5.4 specifies, never interpolated raw.

**The third case restores before it re-adds, and the two cases are not disjoint.** An endpoint can be
changed *and then* removed, and re-adding the instance without restoring it puts the mutation back on
both collections under a line that reports only a removal — the revert this design exists for would
not have happened. Restoring first also settles the name the duplicate guard then asks about: a
renamed-and-removed endpoint is otherwise looked for under a name it no longer carries, and `real`,
which never lost the instance, ends up holding it twice. The entry names the fields in that case too.

**The guard matches names case-insensitively**, because that is how Aspire's own lookup matches them
(`StringComparisons.EndpointAnnotationName`). A guard that agreed with that lookup only on casing
would wave through the duplicate it exists to stop.

### 5.3 Decision 4 — subscription order is a requirement

Carried as stated, for the reason the prototype measured: with `ServiceSourcesWarnings`' own flush
handler subscribed *first* — which an earlier, unrelated service's skip already causes, since
`EnsureSubscribed` fires on the first `ServiceSourcesWarnings.For(builder)` call — the prototype
reverted the mutation and logged **nothing**, which is strictly worse than not detecting at all.

Two measures, both required:

1. **The detector reports from inside its own handler**, and only when it actually detected
   something. Reporting is report-once, so this cannot double-log whichever way the order falls.
2. **`ServiceSourcesWarnings.ReporterFor(builder)`, not `For(builder)`, inside the handler.** `For`
   subscribes during the event's own dispatch, which that class's remarks document as *inert* —
   Aspire snapshots the subscription list before dispatching (`DistributedApplicationEventing.cs:64`).
   `ReporterFor` exists for exactly this caller.

**Measure 1 is what carries this, not an ordering arrangement.** `UrlSource.RegisterContainerConsumerCheck`
ends with `_ = ServiceSourcesWarnings.For(builder)` (`UrlSource.cs:142`) to put the flush handler
behind its own, and an earlier draft of this section claimed the detector could do the same. It
cannot: `RegisterContainerConsumerCheck` runs at `UrlSource.cs:63`, *before* the `BridgeUnregistered`
call at `:65` that installs the detector, so for every `url` service the flush handler is **always**
subscribed ahead of it. It is commonly ahead for `kubernetes` too —
`ServiceSourcesConfigCache.cs:536` calls `For(builder)` during config load. The failing order is
therefore the normal order, not the unlucky one, which makes the in-handler `Flush` a requirement
rather than a belt-and-braces measure. Do not substitute a subscription-ordering trick for it.

**Reported through `ReportNow`, not through `Flush`.** `ServiceSourcesWarnings.ReportNow`'s remarks
warn that flushing everything early in `BeforeStartEvent` splits a later skip off into a second
message, and an earlier draft of this section argued the split could not arise here, because
`UrlSource` subscribes its handler at `:63` before the detector is installed at `:65`. That reasoning
holds only when the `url` service resolves first. It is wrong whenever an out-of-band service resolves
*ahead* of a `url` one — an ordinary AppHost ordering — and the split is then real and reproducible: a
`url` service's `WithEnvironment` skip and the `WaitFor` that `DropWaitsOnUrlServices` drops later in
the same event come out as two messages instead of one. Measured, and pinned by a test.

`ReportNow` takes a ready-made sentence, which the same draft counted against it: the detection cannot
join the service's existing skip group. That is the right trade rather than a cost to absorb — per
§5.4 a revert is not a skipped call, so it does not belong in that group in the first place, and the
grouping #53 and #206 exist to preserve is the one that was being broken. Both measures the failing
order demands are kept: the line is written from inside the handler, so it reaches the log whichever
way the subscription order falls, and nobody else's pending entries are drained to write it.

### 5.4 Wording of the warning

The detector reports through a sibling of `SkipReason` rather than through `AddSkip`, because a
revert is not a skip:

```
Service 'orders': endpoint 'https' was changed after this service resolved (Port, TargetPort) and has
been put back; endpoint 'probe' was added after this service resolved and has been removed, so a
reference taken to it will not resolve. Its source is 'kubernetes' — it resolves to a 'kubectl
port-forward' in front of an already-running service, so the configuration would reach kubectl rather
than the service. An out-of-band service's endpoints are fixed by its source, so configure the service
where it actually runs. To change what this endpoint points at, set 'kubernetes.port' for this
service in servicesources.local.json. 'kubernetes.scheme' changes only the scheme consumers address
it with — the forward reaches the same service — and renames the endpoint, which is named for it. To
make this AppHost's
configuration and start ordering apply instead, give it a 'local' or 'container' source in
servicesources.local.json — which works only where its 'servicesources.yaml' entry already declares
that source: a 'repository' or 'repositoryRef' for 'local', a 'container' block for 'container'.
```

The sample above is the string the code emitted, read back rather than transcribed — a sample kept by
hand is a fifth copy of a sentence that has already drifted twice.

**Two things the skip frame got wrong, each measured by reading the built output rather than the
code.** *Skipped* says the call never landed; these calls landed and were undone, which is the one
fact the reader most needs and the only one that explains why their endpoint is not what they set it
to. *`N calls`* counts entries, and the entries here count endpoints: a single
`WithExternalHttpEndpoints()` over a two-endpoint service reported "2 calls", a number the developer
never wrote. Those two are why a revert gets its own sentence rather than reusing `SkipReason`.

A third defect turned up the same way but did **not** distinguish the two sentences, because it was
the skip's own: its remedy — "set its source to `local` or `container`" — threw for a service whose
catalog entry declares neither, which is the common case for a `url` service, so following it
literally on the very service that emitted it produced `ServiceSourcesConfigurationException`. That
sentence is pre-existing, and four messages offered it in four spellings, so it was hoisted out of all
of them rather than corrected in one.

**`OutOfBandSourceAdvice` is where it now lives** (`src/Aspire.Hosting.ServiceSources/`,
`internal static`), holding the four clauses an out-of-band message is built from: `SourceDetail`
(why the source runs out of band), `SwitchSource` (the conditional way back under this AppHost's
control), and — for the revert message only — `RedirectTheEndpoint` (per source) and
`TheEndpointsItHas`, which stand in for one another by what the reader was doing. Four sites lead
into `SwitchSource` in their own grammar: `SkipReason`, `RevertReason`,
`ServiceConfigurationExtensions.Explain<T>`'s `IsUnreachable<T>` arm, and `UrlSource`'s refusal of a
container's reference to a `url` service. A clause rather than a whole sentence, because the lead-ins
genuinely differ — two of the four embed it as the last item of an `or` list.

**Why a revert names a lever the other three do not.** The reader of a revert wanted the endpoint
somewhere else, and the switch `SwitchSource` offers is exactly what a url-only entry cannot take. The
developer-config settings that *do* redirect it — `url.url`, or `kubernetes.port` — work on that
shape, and live in the file the next sentence already names. They are phrased as
"what this endpoint points at" rather than as moving the endpoint: on `kubernetes` they choose what
`kubectl port-forward` forwards to while the local port stays allocated, and promising the endpoint's
own port would be the next dead end. Measured both ways round before the sentence was written.

The clause also **names the rename**, because both sources name an endpoint after its scheme: a
reader who follows the scheme half loses the name their `GetEndpoint` matches, which is the failure
this detector exists to report, reached by following its own advice. Measured on both sources —
`kubernetes.scheme: http` against a catalog declaring `https` renames the endpoint `https` to `http`,
and a scheme change inside `url.url` does the same.

The two sources differ on what the scheme half *buys*, and the clause says so rather than treating
them alike. For `url` the scheme is genuinely part of the target, so changing it inside `url.url`
redirects and renames. For `kubernetes` it is not: the scheme never reaches `BuildPortForwardArgs`,
so the forward keeps its service and remote port and only the name and the scheme consumers address
move. Measured — `kubernetes.scheme: http` leaves the `kubectl port-forward` argv byte-identical, so
offering it as a second redirect would cost the rename and buy nothing.

**And it is offered only to a reader whose endpoint survives.** No setting adds an endpoint to an
out-of-band service — the C# overloads are gated and these levers only redirect the one the source
registers — so for a revert whose entries are *all* additions the lever is advice that cannot be
followed. That shape gets `TheEndpointsItHas` instead, naming what the service does have, which is
the fact the reader lacks. Measured: following `url.url` after an added `probe` was removed produces
one endpoint named for the URL's scheme and never a `probe`.

**The service name is escaped and capped in all four**, not only in the two warnings. The name is a
catalog key, so it is caller-controlled; escaping it in the warnings while interpolating it raw into
the exceptions left the same forged log line one sentence away.

**The fields that changed are named.** The fingerprint carries eleven, and without naming them the
reader is told an endpoint changed and left to bisect. `Restore` already compares field by field, so
the names are free; `IsExplicitlyProxied` is reported as `IsProxied`, the spelling the callback
context exposes.

This is a **deliberate departure** from the prototype, which reused
`Reachability.CapabilityLabel(typeof(EndpointAnnotation))` and so logged
`WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint` verbatim. That label names three C# methods for a
mutation no C# call made, and — per §2.3 — the endpoint *name* is the one piece of information that
makes Aspire's later unexplained `is not allocated` failure traceable. The measured property the
prototype was demonstrating is that the observable outcome matches gating at the call site; that
property is preserved exactly. Only the capability label changes.

**The endpoint name is caller-controlled, and this is the first warning in the package to carry one.**
Existing skips interpolate `Reachability.CapabilityLabel(...)`, a value from a fixed table. For the
*added* shape the name comes verbatim from a guest-language script — an arbitrary string that reaches
a log line. A name containing a newline forges log lines; an unbounded one floods them.

The label therefore goes through `ConfiguredValue.Bare`, which this package already uses for
developer-written text echoed back into a message, plus two things that helper does not cover: the
64-character cap with an ellipsis, and the single quote. Reusing it rather than testing for control
characters is what catches the hostile characters that are not control characters — `U+2028`, which is
a line terminator to anything that splits on Unicode line separators, and the bidi overrides, which
reverse everything printed after them. The quote has to be escaped because the message *delimits* the
name with it: a name spelling `a' was removed after this service resolved; endpoint 'b` otherwise
reads as two entries for things the developer did not do. Cheap, and it is the only untrusted string
in the whole design (§10).

### 5.5 Call sites

```csharp
// ResolvedService.Bridge — last statement before the `return`, after
// ServiceStartupFailureNotices.For / ServiceWaitRetargeting.EnsureSubscribed
EndpointMutationDetector.Install(real.ApplicationBuilder, facade, real.Resource, source);

// ResolvedService.BridgeUnregistered — last statement before the `return`, after
// ServiceStartupFailureNotices.For(builder)
EndpointMutationDetector.Install(builder, facade, real: null, source);
```

**Last statement in both, deliberately symmetric.** `ServiceStartupFailureNotices.For` subscribes its
own `BeforeStartEvent` handler (`ServiceStartupFailureNotices.cs:145`), so placing the install before
it in one method and after it in the other would give the two sources opposite dispatch orders — in a
design whose §5.3 calls subscription order a requirement, that asymmetry is a defect rather than a
detail. Installing last means the detector's handler is dispatched after every other handler this
package subscribes per service, in both methods.

`Install` takes `IResource? real` and returns immediately when
`!Reachability.OutOfBandSources.Contains(source)`, so both call sites are unconditional and the filter
lives in one place.

## 6. Decision 5 — scope: endpoints only

**Endpoints only in this ticket**, with the widening left additive rather than forbidden.

The mechanism is state-keyed and would generalise mechanically. What does **not** generalise is the
argument that makes it safe. Constraint by constraint:

- The no-false-positives claim has two halves. The first —
  `Reachability.IsUnreachable(typeof(T), out-of-band)` is true — holds for almost every annotation
  type. The second — *nothing in `src/` changes this state between resolve and `BeforeStartEvent`* —
  is established in §3.3 **for `EndpointAnnotation` specifically**, by reading every write in `src/`.
- For `WaitAnnotation` the second half is provably false. `UrlSource.DropWaitsOnUrlServices` removes
  `WaitAnnotation`s at `BeforeStartEvent` on purpose, `RealToFacadeRegistry.MirrorRemoval` mirrors
  that removal onto the facade, and `ServiceWaitRetargeting` rewrites wait targets at the same
  moment. A blanket "no annotation may change after resolve" detector would fire on this package's
  own code, on its first run.
- `WaitAnnotation` is also one of the three types `Reachability` treats as *reachable* for
  `kubernetes` — the other two being `DecorationAnnotationTypes`'
  `ResourceRelationshipAnnotation` and `EndpointReferenceAnnotation`, which are reachable for both
  out-of-band sources (`ServiceResourceBuilder.cs:46–50`, `:65–68`). So the first half fails for
  `WaitAnnotation` too, and for any type a future entry adds to that denylist.

That is #352 §2.5's reason 2 landing squarely — on the widened design, and not on this one. Narrowing
to `EndpointAnnotation` is what makes §4's answer to that reason honest instead of convenient.

The findings noted the same shape may exist across the handle's other ~60 `Aspire.Hosting/*`
capabilities; only the endpoint ones were enumerated. Widening is left to a later ticket, and the
design keeps it cheap: the fingerprint is one record and one comparison, so a second annotation type
is an addition. The **requirement** on that ticket is the part worth writing down — each new type
must bring its own enumeration of what in `src/` writes it after resolve, exactly as §3.3 does here.
Reusing this one would be reusing the wrong argument.

## 7. Tests

All in `test/Aspire.Hosting.ServiceSources.Tests/`, reusing `EndpointSkipGapRepro`'s
`UrlSource`/`KubernetesSource` fixtures. New file `EndpointMutationDetectorTests.cs` unless noted.

**Detection and revert** — driving Aspire's internal generic reflectively, the way ATS capability
dispatch does (findings §5 records the technique):

1. `kubernetes` + `withHttpsEndpointCallback` setting `Port`/`TargetPort` → both restored, one revert
   message naming `endpoint 'https' was changed after this service resolved`, and it reaches the log.
2. `kubernetes` + `withEndpointCallback('probe', …)` → `probe` absent from the facade afterwards, one
   revert message naming `endpoint 'probe' was added after this service resolved`. Assert on the facade only: per §2.4 the added
   instance never reaches `real`, so asserting its absence there would pass whether or not the
   detector ran. Assert instead that the facade's remaining endpoints are exactly the one the source
   registered.
3. `url` + `withHttpsEndpointCallback` setting `TargetHost` → restored, revert reported. (`url` has
   no real resource; this pins the `real is null` path.)
4. Both mutations on one service → one grouped message, with **no** `N calls` tally: per §5.4 the
   entries count endpoints rather than calls, so the tally is what the revert sentence drops.
5. A rename — `Name` written directly on an existing `EndpointAnnotation` → detected and restored as
   a *change*, not as an add plus a remove, which is what pins the reference-identity key. Written
   against the annotation rather than through a callback, and labelled in the test name as such,
   because `EndpointUpdateContext.Name` is get-only: no surface this design polices can rename an
   endpoint today (§5.2).
6. `Protocol` flipped Tcp→Udp through `withEndpointCallback` → detected and restored.
6a. An endpoint name containing `\r\n` and 300 characters → the logged message is one line and the
   name is truncated (§5.4).

**#359, from C#, which is the argument for this over another shadow:**

7. `service.WithExternalHttpEndpoints()` on a `kubernetes` service → `IsExternal` restored to `false`,
   one skip. This is today's reproduction on `main` (`IsExternal False -> True`, `warnings=0`)
   turning green without a line of `WithExternalHttpEndpoints`-specific code.

**Subscription order (§5.3), both ways round:**

8. An unrelated earlier service records a skip — so `ServiceSourcesWarnings`' flush handler is
   already subscribed ahead of the detector — then a mutation is detected. Assert the message
   **reached the log**. This is the case the prototype measured as `LOGGED=0`.
9. The detector installed first, no earlier skip. Assert the message reached the log exactly once.

   Both assert through `TestHelpers.PublishBeforeStartEventCapturingWarningsAsync`
   (`test/Aspire.Hosting.ServiceSources.Tests/TestHelpers.cs:87`), which returns what was written.
   `ServiceSourcesWarnings.Messages` must **not** be used here: it returns the buffer, so it would
   pass in exactly the `LOGGED=0` case these two tests exist to catch.

**No false positives (§3.3, §5.1):**

10. `kubernetes` and `url` services with no post-resolve mutation at all → zero warnings after
    `BeforeStartEvent`.
11. A `container`-sourced service whose endpoint *is* changed after resolve → zero warnings and no
    revert; nothing is installed for a reachable source.
12. **The `MutateHttp2TransportAsync` pin.** A `kubernetes` service on which `AsHttp2Service()` is
    called: assert the facade carries no annotation named `Http2ServiceAnnotation`, and that running
    `BeforeStartEvent` produces no `Transport` revert. The assertion is by **type name**, not
    `typeof(...)` — `Http2ServiceAnnotation` is `internal sealed` to `Aspire.Hosting.dll`
    (`Http2ServiceAnnotation.cs:6`), the same accessibility constraint
    `Reachability.CapabilityLabel` already documents for `EnvironmentAnnotation`. If a future
    `Reachability` change ever lets that annotation through, this fails and names the reason rather
    than a developer finding a reverted transport at runtime.

**Timing (§3.2), kept true as Aspire moves:**

13. A `BeforeStartEvent`-ordering assertion that the detector's handler runs before a handler
    subscribed later. Mechanism: after resolving an out-of-band service, subscribe a probe handler
    that appends to a shared list and reads the facade's endpoint state; the detector appends first,
    and the probe sees the *restored* values. That is the structural fact §3.2's last two rows rest
    on — Aspire dispatches `BeforeStartEvent` sequentially in subscription order, and everything
    `SubscribeAsync` registers is subscribed after composition — expressed with the one seam a unit
    test has.

The deeper claim — that DCP and the publish pipeline read endpoints only from an `IHostedService` —
is not unit-testable from here without standing up DCP. It is established by reading the pinned
assembly (§3.2) and is re-checked by the CI `latest` Aspire leg through the behavioural tests above:
if a future Aspire moved endpoint reading earlier, tests 1–3 would stop restoring in time and fail.

## 8. CHANGELOG

One entry under `## [Unreleased]` → **`### Added`**. The fork the repo's own rule opens is closed
here rather than left to the implementer: `Fixed` is only for bugs in already-*released* behaviour,
the last release tag is `v0.5.1` (2026-09-07), and the whole `ServiceResource` endpoint surface this
detector guards — #313, and #334/#335/#366 on top of it — still sits under `## [Unreleased]`. None of
it has shipped, so there is no released behaviour to have been broken.

> **Endpoint changes made after a `url` or `kubernetes` service resolves are now detected, reverted
> and reported** ([#372]). Aspire's own `withEndpointCallback`, `withHttpEndpointCallback` and
> `withHttpsEndpointCallback` — reachable from a guest-language AppHost, and `internal` to Aspire, so
> no C# shadow can intercept them — mutated an out-of-band service's endpoint with no warning, which
> for a `kubernetes` service repointed the real `kubectl port-forward`. …

Cite `([#372])` inline and add the matching `[#372]: …/issues/372` definition to the sorted link
block. The entry should also say that `WithExternalHttpEndpoints` (#359) is **covered** as a
consequence, citing `([#359])` — covered, not fixed or closed: #359 is open and separately owned, and
this ticket does not close it.

**Two `### Fixed` entries sit beside it**, and the reasoning above does not contradict them: they
belong to the two pre-existing defects this PR absorbed by human decision rather than to the detector.
The unescaped service name and all four unconditional switch remedies are present verbatim at `v0.5.1`,
so both are bugs in released behaviour and `Fixed` is the section the repo's rule points at. The
detector's own entry stays `### Added` for the reason given.

## 9. The upstream ask

Having Aspire route the callback path's update branch through `WithAnnotation`, or make
`EndpointUpdateContext` public, would let #335's shadows close this at the call site and make this
whole detector unnecessary. It is the cheaper long-term fix, it is **not** superseded by this design,
and this design is a workaround for its absence rather than a replacement for it.

Per the decision taken on this ticket, it is recorded here and **filed nowhere** — no upstream issue,
no issue in this repo, and no comment asking anyone else to file one. This paragraph is the whole of
its disposition.

## 10. Attack surface

- **What it trusts:** only the facade's own `Annotations` collection and the snapshot this package
  took of it. Nothing crosses a process, a socket, a filesystem path or a deserializer. No
  configuration value reaches it — the only external input is the `source` string, already validated
  upstream and used solely for a set membership test.
- **What it executes:** nothing. No subprocess, no shell, no reflection over caller-supplied names.
- **What it writes:** property values it previously read off the same objects, and collection
  add/remove on two in-memory annotation collections.
- **What it logs:** the service name, the source, and the endpoint name. No port, host, URL or
  connection string is emitted, so the message cannot leak a target's address into a shared log.
  **The endpoint name is the one untrusted string in the design** — for the *added* shape it comes
  verbatim from a guest-language script, and it is the first caller-controlled value this package
  interpolates into a warning (existing skips use a fixed capability label). Unbounded, it forges log
  lines with an embedded newline. §5.4 requires it to pass through `ConfiguredValue.Bare` — which
  also catches the hostile characters a control-character test misses — plus the single quote it
  does not cover and a 64-character truncation, and §7 test 6a pins that.
- **Denial of service:** one dictionary per out-of-band service, one pass over its endpoints at
  `BeforeStartEvent`. Bounded by the number of services in the AppHost.
- **The one real hazard is a false revert** — reverting something legitimate. §3.3 and §5.1 bound it
  by construction, test 12 pins the single conditional case inside `Aspire.Hosting`, and §3.4's
  residuals 2 and 3 name the two ways a false revert could still arrive from outside it.

## 11. Code-comment discipline

Comments in the new file explain only the non-obvious WHY, under roughly fifteen words, and carry no
changelog, history or narrative. The ones that earn their place are the reasons the code cannot state
itself: why the snapshot is keyed by reference identity rather than by name; why the restore re-reads
the derived fields in a second pass; why a removed endpoint is restored before it is put back; why the
duplicate guard matches case-insensitively; why the report is written from inside the handler; and why
the endpoint name is escaped and capped before it reaches a log line. **No third-party issue numbers
or links appear in code** — this repo's own issue numbers are fine, and the Aspire-side reasoning
stays in this document.

## 12. Disposition

The implementation lands in the **same pull request** as this document, not in a follow-up: §5 is
written to be built from, and §7 is the test list that build has to satisfy.

## Open Questions

None. The six decisions the ticket named are settled in §2, §3, §4, §5.1, §5.3 and §6. The two
questions already answered by a human are honoured and not reopened: design plus implementation in
one PR (§12), and the upstream ask acknowledged here and filed nowhere (§9).
