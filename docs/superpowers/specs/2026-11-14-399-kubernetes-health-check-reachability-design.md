# Letting a health check reach a `"kubernetes"` service's port-forward (#399)

**Date:** 2026-11-14
**Status:** Approved (the four open questions were answered by the human; recorded in section 7)
**Resolves:** #399 (`WithHttpHealthCheck` is skipped for a `"kubernetes"`-sourced service although the
port-forward has a real local endpoint).
**Relates to:** #490 / PR #512 (the built-in TCP check and the port-forward supervisor that this
design has to stay compatible with);
[`2026-09-10-313-service-resource-dualwrite-bridge-design.md`](2026-09-10-313-service-resource-dualwrite-bridge-design.md)
(why the facade dual-writes, and the fail-closed rule);
[`2026-09-17-372-endpoint-mutation-detector-design.md`](2026-09-17-372-endpoint-mutation-detector-design.md).
**Measured against:** reading the repo at `de0736c`, and a decompile of `Aspire.Hosting` **13.5.3**
(the newest copy in the local NuGet cache; the repo's `AspireVersion` is 13.6.5). Every statement
below about Aspire's own behaviour comes from that decompile, not from a run. Nothing here was
exercised against a live host or cluster; the plan's tests re-check the Aspire-facing claims (marked
**[verify]**).

## 1. The decision

Can `WithHttpHealthCheck`, and endpoint-targeting annotations like it, become a second exception
beside `WaitAnnotation` in `Reachability.IsUnreachable` for `"kubernetes"`, while `DecorationAnnotationTypes`
stays a denylist and `"url"` is untouched?

**Yes, for exactly one annotation type, `HealthCheckAnnotation`, and not for "endpoint-targeting
annotations" as a class.** The exception is one more exact-`Type` entry in the kubernetes entry of a
per-source allowance map. Everything else that is not already decoration still gates, so an annotation type nobody
anticipated still fails closed. It needs one companion change to keep the #490 supervisor correct
(section 4.3); without that change the answer would be no.

## 2. What the code does today

- `Reachability.IsUnreachable(annotationType, source)` is true when the source is in
  `OutOfBandSources` (`url`, `kubernetes`, `disabled`, the unconfigured placeholder), the type is not in
  `DecorationAnnotationTypes` (`ResourceRelationshipAnnotation`, `EndpointReferenceAnnotation`), and
  the pair is not `("kubernetes", WaitAnnotation)`. The `WaitAnnotation` clause is inline in the
  expression (`ServiceResourceBuilder.cs`). The twin predicate `ServiceConfigurationExtensions.IsUnreachable<T>`
  carves out `IResourceWithWaitSupport` for `Unwrap<T>`.
- Both are reached through `ServiceResourceBuilder.WithAnnotation` (gate, then add the same instance to
  the facade and `real`) and `ForwardingAnnotationsAddedBy` (re-checks, for annotations added to the facade
  directly). `MirroringCommandRemovalsBy` consults `IsUnreachable(typeof(ResourceCommandAnnotation), ...)`
  to stay out of kubernetes services, and its remarks rely on that being true.
- `Aspire.Hosting`'s `WithHttpHealthCheck` ends in `builder.WithHealthCheck(key)`, which adds a
  `HealthCheckAnnotation(key)` through `builder.WithAnnotation`. On a `ServiceResourceBuilder` that call
  therefore reaches the gate with `typeof(HealthCheckAnnotation)`, which is not exempt, so it is skipped with
  the label `HealthCheckAnnotation` (`CapabilityLabel` has no case for it and falls back to `Type.Name`).
  The check's DI registration (`Services.AddHealthChecks().Add(...)`) still happens, so today an orphan
  registration is left behind. **[verify]**
- `KubernetesSource.Resolve` builds the `ServiceExecutableResource` named for the service with one
  endpoint (`name: scheme`, `port == targetPort == localPort`, `isProxied: false`), registers
  `"{service}-port-forward-tcp"` (`LocalPortHealthCheck`, a bare TCP connect to 127.0.0.1) and attaches it
  to the real resource with `WithHealthCheck`, all **before** `ResolvedService.Bridge` copies the
  annotation list to the facade. The facade and the real resource share the same `EndpointAnnotation`
  and `HealthCheckAnnotation` instances.
- `scheme` is validated to be `http` or `https` (`EndpointScheme.Resolve`), so the kubernetes service always
  has exactly one endpoint and it is always one `WithHttpHealthCheck`'s default selector accepts.
- `PortForwardSupervisor.OnSnapshotAsync` decides "the tunnel is up" with
  `snapshot.HealthReports.Length > 0 && snapshot.HealthStatus == HealthStatus.Healthy`, i.e. the **aggregate**
  over every check on the resource, not the tunnel's own report.

## 3. Why `HealthCheckAnnotation` is not the `WithEnvironment` problem

The skip policy exists because configuration dual-written onto a `"kubernetes"` service lands on the
**kubectl process**, not the service (`Unwrap<T>` remarks, issue #53). For `WithEnvironment`,
`WithArgs`, `WithEndpoint` that is the whole story and it stays.

A health check differs in what it targets: its subject is the **local port**, and the port-forward's whole
job is that a connection to that port reaches the service. `EndpointUriHealthCheck` resolves its URL
from an `EndpointReference` (`endpoint.GetValueAsync`) and issues a GET; for the facade-bound reference that
resolves through the shared `EndpointAnnotation`, whose `AllocatedEndpoint` is set on the real resource's
instance (`WithAnnotation` remarks: "The SAME instance goes into both collections"). The request travels
the tunnel and is answered by the service behind it. That is the opposite of the wrong-process failure:
it measures the thing the developer named. This is also what #490 already decided for `WaitFor`: a
health report on the real executable is the honest signal for "the service is reachable".

The annotation itself is also pure data (a key naming a check in DI); it configures nothing on kubectl.

## 4. Design

### 4.1 The allowance

Replace the inline `WaitAnnotation` clause with a map keyed by source, matched by exact `Type`, so a type
reviewed for one source cannot be admitted for another by accident:

```csharp
private static readonly Dictionary<string, HashSet<Type>> ReachableBySource = new(StringComparer.Ordinal)
{
    ["kubernetes"] = [typeof(WaitAnnotation), typeof(HealthCheckAnnotation)],
};
```

`IsUnreachable` becomes: out-of-band, not decoration, and not (the source has an entry and the type is in it). The table is the only widening. `DecorationAnnotationTypes` is unchanged and remains a
denylist; adding to the kubernetes allowance is a deliberate, tested edit, never a default.

- **Exact `Type` identity**, as for decoration: `HealthCheckAnnotation` is a public, non-sealed class, so a
  subclass does not match and is skipped. That is intentional fail-closed behaviour, not an oversight.
- `"url"`, `"disabled"` and the unconfigured placeholder are unchanged: `IsUnreachable` stays true for
  `HealthCheckAnnotation` there (no local process; `url`'s `real` is null, the placeholder registers
  nothing). The new test pins this per source.

### 4.2 Labels, skip text and docs

- `CapabilityLabel` gains `HealthCheckAnnotation` -> `WithHealthCheck/WithHttpHealthCheck`, so the surviving skip on
  `"url"`/`"disabled"` names a method the author called instead of an annotation type.
- The `Reachability.IsUnreachable` summary, the `DecorationAnnotationTypes` remarks (which currently list
  `WithHttpHealthCheck` among the things that must fail closed on kubernetes), the
  `ServiceConfigurationExtensions` remarks, `docs/guides/configuration.md` ("Configuring a resolved
  service", including the paragraph that says `WithHttpHealthCheck` is skipped and the "one exception"
  paragraph) and `docs/sources/kubernetes.md` change to match; `CHANGELOG.md` gets a **Changed** entry
  (behaviour change: a call that used to be skipped now applies).
- `ServiceConfigurationExtensions.IsUnreachable<T>` is not changed in behaviour (its visibility becomes `internal`, only so the agreement test can reach it): there is no capability interface for
  health checks to key on, so `Unwrap<T>` has nothing to carve out. It keeps its own hard-coded
  `IResourceWithWaitSupport` carve-out, so the allowance lives in two places (annotation type here,
  capability type there); a test asserts the two agree on `WaitFor` for every source.

### 4.3 The supervisor must read the tunnel's own report (required)

Today any extra check on the real resource changes how the supervisor reads the tunnel. A user check
that fails (a 503, a 404 on the wrong path, a TLS error) makes the aggregate `Unhealthy` while the tunnel is
fine, and:

1. `_ready` never becomes true. If kubectl later exits, `firstFailure = !_ready && _restarts == 0` classifies
   a tunnel that held for an hour as "failed before ready": it publishes `FailedToStart` and does **not** restart.
2. After 60s unhealthy, `WarnUnhealthyAsync` tells the developer the port-forward "has not accepted a
   connection ... a Service with no ready pods behind it looks like this", which is false for an HTTP-level failure.

So the supervisor's readiness test changes from the aggregate to the report whose `Name` equals
`KubernetesSource.HealthCheckKey(serviceName)` (`HealthReportSnapshot.Name` is the check key, confirmed in the decompile of
`ResourceHealthCheckService`). Defined states, replacing the `HealthReports.Length > 0` tests at
`PortForwardSupervisor.cs:237` and `:244`:

- Tunnel report present with `Status == Healthy`: ready; sets `_ready`, `_healthySince ??=`, clears
  `_unhealthySince` and `_warnedUnhealthy`.
- Present with Unhealthy/Degraded: starts the 60s unhealthy clock and warning, as today.
- Absent, or present with `Status == null` (Aspire: not yet received): no report, so neither ready nor
  unhealthy; no state changes. A snapshot where only a user check has reported is this case.
- `_healthySince` (the `StableUptime` restart-counter reset) follows the tunnel report only.
- A user check's status has no effect on any of the above.
`PortForwardSupervisor.Install` already takes `serviceName`, so no new plumbing is needed. A user-authored
check that fails is then visible where it belongs (the dashboard's health badge, and `WaitFor` still holds
on the aggregate), but never mistaken for a dead tunnel.

`WaitFor` itself needs no change: Aspire evaluates the aggregate, so a consumer waits for the tunnel **and**
the user's check, which is what the author asked for.

### 4.4 What a user gets

`service.WithHttpHealthCheck("/healthz")` on a `"kubernetes"` service now applies: a
`HealthCheckAnnotation` with key `"{service}_{scheme}_/healthz_200_check"` is on the real resource, and
`WaitFor(service)` holds until both the TCP check and the HTTP check pass. `WithHealthCheck(key)` with a
user-registered check applies too (section 7, decision 2).

## 5. Attack surface this design creates

Who is the actor: the AppHost author (trusted code) and the developer's checked-in or local config
(`servicesources.yaml`, `servicesources.local.json`, which choose the source, context, namespace, service,
port, scheme). Nothing here accepts input from the network or from the cluster before a probe runs.

1. **A new thing is trusted: the cluster's responses.** A health check now sends requests to a service
   in a (shared, possibly dev) cluster and acts on the answer by holding or releasing consumers. A
   response can only keep a consumer waiting (unhealthy) or release it (healthy); it cannot change what is
   configured. Worst case is a held or prematurely released consumer, the same trust as `WaitFor` on the TCP
   check from #490, plus the HTTP status.
2. **Redirects and absolute paths** (see section 7, decision 4). `EndpointUriHealthCheck` builds `new Uri(baseUri, path)`, so an absolute
   or scheme-relative `path` argument retargets the probe to another host, and an HTTP redirect from the
   cluster service goes wherever the handler follows it (default `HttpClient` handler; **[verify]** redirect
   behaviour in 13.6.x). The first is the AppHost author's own argument (same as any Aspire resource); the
   second is new for this package because the response comes from a remote system: a compromised or
   hostile workload could redirect the unauthenticated GET from the developer's machine to an internal or
   metadata address and have the resulting 200 release dependents (blind SSRF). A mitigation exists
   (configure the named client with `AllowAutoRedirect = false`) but touches Aspire's client; it was declined (section 7, decision 4).
3. **TLS.** For `scheme: https` the tunnel is byte-transparent, so the certificate is the pod's and the
   host name is `localhost` (`EndpointScheme` remarks). The default probe will reject it unless the
   cluster certificate is trusted for `localhost`, leaving the service unhealthy and every `WaitFor`
   holding. We must **not** relax certificate validation to make this work: that would turn a probe of a
   remote system into an unauthenticated one with no way for the developer to see it. See section 7, decision 1.
4. **Probe traffic against a remote service, in whichever cluster is selected.** The context comes from
   `servicesources.local.json`, so a developer switching a service to `"kubernetes"` turns a checked-in,
   previously skipped `WithHttpHealthCheck` into a live GET against whatever context they named, possibly a
   production one. The TCP check never reached application code; this does. Periodic GETs to a real service from every developer's
   machine, with no credentials and no headers beyond Aspire's defaults. Same shape as the TCP probe, whose
   connections `kubectl` already logs as `Handling connection for <port>` (`docs/sources/kubernetes.md`),
   but it now reaches application code and its logs. Documented, not mitigated.
5. **Widening the allowance is the actual risk to future code.** A per-source type map is easy to extend
   and each entry silently stops a warning. Controls: exact `Type` match (no name matching, no subclass
   match), a per-source map so an entry cannot leak to another source, a test per reachable type and per source asserting the full matrix, and
   an explicit remark naming what a new entry must prove (section 6). `DecorationAnnotationTypes`
   remains a denylist, so an unrecognised type still gates by default.
6. **Do not leak into neighbouring types.** `ResourceCommandAnnotation` (`WithHttpCommand`, which also
   issues requests to the endpoint) stays gated: `MirroringCommandRemovalsBy`'s reasoning ("no kubernetes
   service can reach the removal") depends on it. `EndpointProbeAnnotation`/`ProbeAnnotation` stay gated; and
   `WithHttpProbe` cannot compile against the facade at all, because `ServiceResource` deliberately does
   not implement `IResourceWithProbes`. `ResourceUrlAnnotation` and friends stay gated. Each is a separate
   decision with its own wrong-process analysis.
7. **Supervisor confusion (availability).** Covered by 4.3; without it a failing user check converts a
   recoverable tunnel death into a permanent `FailedToStart`.
8. **The probe target is a loopback port, not an identity.** If `kubectl` failed to bind because another
   local process owns the port, or during a restart gap, the GET and its 200 come from that process. The TCP
   check has the same flaw; the HTTP check makes "healthy" look more meaningful and sends a request to a
   foreign local service. Accepted, documented.
9. **Slow or hanging user checks.** A custom `WithHealthCheck` that never returns holds the aggregate
   unhealthy and every `WaitFor` with it; the tunnel report is unaffected (4.3).
10. **Key collisions.** `WithHealthCheck` throws if the resource already has that key. The built-in
   `"{service}-port-forward-tcp"` is on the facade (copied by `Bridge`), so a caller reusing it gets Aspire's
   own `DistributedApplicationException` rather than silently replacing the tunnel check (Aspire registers a
   `WithHttpHealthCheck`'s DI check before attaching it, so a repeated identical call leaves a duplicate
   registration and then throws; same on any resource). Generated
   `WithHttpHealthCheck` keys embed the service name, endpoint name, path and status, so they cannot equal it.

## 6. What does not change

- `"url"`: `IsUnreachable(HealthCheckAnnotation, "url")` stays true; no health check is applied; the skip is
  still reported (with the better label).
- `"disabled"` and the unconfigured placeholder: unchanged.
- `DecorationAnnotationTypes` and its denylist semantics; fail-closed for every other annotation type
  on `"kubernetes"`, including subclasses of `HealthCheckAnnotation`.
- The EndpointMutationDetector, the endpoint shadow overloads and `Unwrap<T>`.
- No public API: `Reachability` is `internal`, so `PublicAPI.Unshipped.txt` does not change.
  (Confirm at build: the analyzer fails the build otherwise.)

A rule for the remark above the table, so the next entry has to earn it: a type may join the
kubernetes allowance only if (a) it is data the real resource or Aspire consumes rather than something applied
to the kubectl process, (b) acting on the real resource measures or orders the service behind the tunnel,
and (c) no invariant elsewhere in this package assumes it is unreachable.

## 7. Decisions (the former open questions, answered by the human)

1. **`scheme: https`: document the limitation only.** No startup notice, no refusal. Certificate
   validation is never relaxed. `docs/sources/kubernetes.md` says that a default `WithHttpHealthCheck`
   against an `https` tunnel is `Unhealthy` unless the cluster certificate is trusted for `localhost`,
   and that `WaitFor` then holds.
2. **Breadth of the type: accept the wider type.** Any `HealthCheckAnnotation` of exact `Type` is
   admitted, including `WithHealthCheck(key)` with a user-registered check. No shadow
   `WithHttpHealthCheck` overload, no new public API.
3. **Scope: only `HealthCheckAnnotation`.** `WithHttpCommand`, probe and URL annotations stay gated; each
   would need its own spec.
4. **Redirects: document the residual risk, no code.** No `AllowAutoRedirect = false`. The docs state that
   the probe follows Aspire's default redirect handling and that a hostile workload behind a tunnel could
   redirect it (section 5, item 2).

## 8. Test plan (outline, for the Plan step)

Traits: `ReachabilityTests` and the supervisor tests keep their existing class traits. `KubernetesHealthCheckDualWriteTests`
builds a real host under a temporary directory, so it carries `[Trait("IO", "true")]` per the CLAUDE.md trait table.

- `ReachabilityTests`: matrix of {`url`, `kubernetes`, `disabled`, unconfigured} x {`WaitAnnotation`,
  `HealthCheckAnnotation`, a subclass of `HealthCheckAnnotation`, `EnvironmentCallbackAnnotation`,
  `ResourceCommandAnnotation`, an unknown test annotation}.
- Dual-write: a `"kubernetes"` service's `WithHttpHealthCheck()` puts one `HealthCheckAnnotation` on the
  real resource and the facade, with no skip recorded; the same call on `"url"` records a skip labelled
  `WithHealthCheck/WithHttpHealthCheck`. **[verify]** that `OnResourceEndpointsAllocated` (subscribed against the unregistered
  facade) is a harmless no-op.
- A probe through the facade: after the real endpoint's `AllocatedEndpoint` is set, the facade-bound
  `EndpointReference` resolves `GetValueAsync` to the allocated URL (otherwise every other test could pass
  and nothing be probed). The `OnResourceEndpointsAllocated` existence check never runs for the unregistered
  facade; accepted, since a kubernetes service's endpoint always exists.
- The two predicates agree: `Reachability.IsUnreachable(WaitAnnotation, s)` equals
  `IsUnreachable<IResourceWithWaitSupport>(s)` for every source.
- `PortForwardSupervisor` tests (existing ones build snapshots with `new HealthReportSnapshot("tcp", ...)`,
  `Tests/Sources/PortForwardSupervisorTests.cs:150`, and are migrated to the real
  `HealthCheckKey(serviceName)` name): a snapshot with the tunnel check healthy and a user check unhealthy
  counts as ready; a tunnel that exits then is restarted, not `FailedToStart`; the 60s warning is not
  raised for it; a snapshot with the tunnel check unhealthy still raises it.
- The tunnel report absent / `Status == null` / user-only-reported states of 4.3 each get a case.

## 9. Out of scope

Backing services (`KubernetesBackingServiceSource`) attach their own checks and are untouched. A future
`IResourceWithProbes` on the real resource is not reachable through the facade. New public API; changing how Aspire builds or sends the HTTP probe; certificate handling; any annotation type
besides `HealthCheckAnnotation`; the `"url"` source.


## Review round 2 amendments

- `WithAnnotation<TAnnotation>` gates on both the static type argument and the annotation's runtime type, so a subclass passed under `HealthCheckAnnotation` is skipped like the facade-diff path.
- On `"kubernetes"`, a `Replace` of a `HealthCheckAnnotation` is treated as `Append`: a resource carries many checks, and replacing would drop the tunnel's own.
- `ReachableBySource` is a `FrozenDictionary` of `FrozenSet`.
