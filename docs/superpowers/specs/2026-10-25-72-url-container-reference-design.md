# Letting a container consumer `WithReference` a `"url"`-sourced service (#72)

**Date:** 2026-10-25
**Status:** Draft
**Resolves:** #72 (a container consumer of a `"url"` service is refused up front by
`UnregisteredServiceStartupGuard`, because the facade has no resource for DCP to plumb
container-to-host networking through).
**Relates to:**
[`2026-08-22-apphost-configurable-real-resources-design.md`](2026-08-22-apphost-configurable-real-resources-design.md)
(where the limitation was first recorded) and
[`2026-09-10-313-service-resource-dualwrite-bridge-design.md`](2026-09-10-313-service-resource-dualwrite-bridge-design.md)
(why the facade is what the consumer references).
**Measured against:** Aspire AppHost `13.6.1` (the repo's floor, `Directory.Build.props`), Docker on
Linux, a throwaway AppHost with one `url`-sourced service (`https://httpbin.org`) and
`builder.AddContainer("storefront", "alpine")` as the consumer. Every row of the probe table below
is a real `dotnet run` followed by `docker inspect` / `docker exec wget`, not a reading of the
source. The probe project was deleted afterwards; the table is the record. Not reproducible from the repo as such; the reproducible form is the
live smoke test in section 5.

## 1. Problem and what was already known

Two routes were measured dead on the issue (2026-09-17):

- **Route A** (bridge to Aspire's `ExternalServiceResource`): `WithReference` picks its overload on
  the *static* type of the argument. A consumer holds `IResourceBuilder<ServiceResource>`, which is
  `IResourceWithServiceDiscovery`, so Aspire takes the endpoint overload and never reaches the
  external-service one. `ExternalServiceResource` is also `sealed` and has no endpoint annotation.
- **Route B** (rewrite the consumer's annotations at `BeforeStartEvent`): removing the
  `EndpointReferenceAnnotation` and `ResourceRelationshipAnnotation` and overwriting env values left
  the container going `Starting -> FailedToStart` with no reason logged. The issue guessed the
  residual cause was the `EnvironmentCallbackAnnotation` closing over a live `EndpointReference`.
- **Option 1** (register the facade): DCP cannot allocate a port; the container is never created.

This spec asks for a third route and tests candidates rather than reasoning about them.

## 2. Probe results

Control: identical AppHost with the `WithReference` line removed. The container is created within
~6 s, so the environment is sound.

| # | Route | Result |
|---|---|---|
| 0 | Baseline (`WithReference(inventory)`, today's code) | Refused by the pre-flight with the documented message. |
| 1 | **Overload**: package-supplied `WithReference<T>(this IResourceBuilder<T>, IResourceBuilder<ServiceResource>)` that injects the URL literally for a container consumer and otherwise delegates to Aspire's endpoint overload | **Works** for the call as written. C# overload resolution picked it over Aspire's (confirmed by a marker line); `services__inventory__https__0=...` injected; container Up; `wget` from inside it returned httpbin's JSON. Reaches only callers whose static type is `IResourceBuilder<ServiceResource>` in C#. |
| 2 | **Closure surgery**: at `BeforeStartEvent`, reflect into each `EnvironmentCallbackAnnotation`'s delegate closure and remove the one holding the facade's `EndpointReferenceAnnotation`, then add a literal env callback | **Works**, and it *confirms* the issue's guess about route B: with the endpoint annotations removed but the callback left, the container is never created and nothing is logged; with the callback removed too, it starts. But it depends on a compiler-generated closure two delegates deep (`DisplayClass34_0.endpointReferencesAnnotation`), which can change in any Aspire release, and it cannot see an opaque user callback. **Rejected.** |
| 3 | **Wrap and rewrite**: at `BeforeStartEvent`, replace each container consumer's `EnvironmentCallbackAnnotation` and `CommandLineArgsCallbackAnnotation` with a wrapper that runs the original and then rewrites any value that refers to the `"url"` facade's endpoint into a literal string; remove the shape annotations (`EndpointReferenceAnnotation`, `ResourceRelationshipAnnotation` of type `Reference`) | **Works for every form tried** (table below). Uses only public Aspire API. Nothing is executed early. |

Route 3 detail, one consumer carrying all of these at once, container Up, each value verified in
`docker inspect` and the URL fetched from inside the container:

| How the AppHost wrote the dependency | Value DCP would have resolved | After rewrite |
|---|---|---|
| `WithReference(inventory)` (`INVENTORY_HTTPS`, `services__inventory__https__0`) | `EndpointReference` | literal URL |
| `WithEnvironment("A", inventory.GetEndpoint("https"))` | `EndpointReference` | literal URL |
| `WithEnvironment("B", ReferenceExpression.Create($"{ep}/x"))` | `ReferenceExpression` (provider: `EndpointReference`) | `<url>/x` |
| `WithEnvironment("E", ep.Property(EndpointProperty.Url))` | `EndpointReferenceExpression` | literal URL |
| `WithEnvironment(ctx => ctx.EnvironmentVariables["C"] = ep)` (opaque lambda) | `EndpointReference` | literal URL |
| `WithArgs(ctx => ctx.Args.Add(ep))` | `EndpointReference` in `Args` | literal URL |

The opaque-lambda and expression rows are the cases the pre-flight documents as undetectable
(`UrlSource.cs`, "a container that consumes a url service in a form leaving neither [annotation]").
Route 3 closes that gap for env and args, because the rewrite inspects the *values the callback
produced* at the moment DCP asks for them rather than guessing at the delegate.

Other measurements that shaped the design:

- **Resolving through Aspire does not work.** Calling `GetValueAsync` on the facade's
  `EndpointReference` from inside the wrapper was cancelled and the container silently failed to
  start. The rewrite must compute the value from the facade's own `EndpointAnnotation`
  (`UriScheme`, `TargetHost`, `Port`), which the source already knows, not ask Aspire to resolve it.
- **A thrown exception inside a callback is a silent `FailedToStart`**, the same failure mode as
  the original bug. Anything the rewrite cannot handle must be reported by logging, not left to throw.
- **Loopback URLs do not work from a container**, and cannot be made to. `http://localhost:8765`
  was mapped to `host.docker.internal` and the container could not resolve it: Aspire's containers
  on Linux get no `extra_hosts` entry. Reaching the host from a container is what DCP's tunnel
  (`*.dev.internal`) does for a *registered* resource, which is exactly what a `"url"` service is not.
  This is the same as Aspire's own `ExternalServiceResource`, which also injects the URL verbatim.
- **`BeforeStartEvent` handlers run in subscription order**, and `UnregisteredServiceStartupGuard`
  is subscribed during `AddService`, so any rewrite must live in (or before) that handler, not in a
  later subscriber.

## 3. Decision

**Build route 3, inside the existing pre-flight, gated on the same annotation shape the pre-flight
matches today.** For a container that the shape match already identifies as consuming a `"url"`
service, the refusal is replaced by a rewrite; the rest of the pre-flight (dropping `WaitFor`,
flushing skips) is unchanged. Route 1 (the overload) is not built: it is narrower (C# only, static
type only), adds public API and an `ASPIREEXPORT008` warning to resolve under `-warnaserror`, and
covers nothing route 3 lacks.

Scope is deliberately the shape-matched consumers only. Wrapping every container's callbacks would
also close the opaque-lambda gap (route 3 handled it in the probe), but it touches containers that
reference no `url` service, and the ticket asks to keep the documented gap. That widening is left as
an explicit follow-up (Open Question 1), not built here.

### 3.1 Behaviour

At `BeforeStartEvent`, for each `ContainerResource` for which `ConsumedUrlService` (unchanged:
`EndpointReferenceAnnotation`, or `ResourceRelationshipAnnotation` of type `Reference`, targeting a
`"url"` facade) finds a facade:

1. Remove those `EndpointReferenceAnnotation`s and `Reference` relationships that target a `"url"`
   facade. They are what DCP reads to wire container-to-host networking; leaving them keeps the
   container from being created. (Cost: the dashboard shows no relationship line from the consumer
   to the facade. The facade is not in the model, so there was nothing for it to point at.)
2. Replace every `EnvironmentCallbackAnnotation` and `CommandLineArgsCallbackAnnotation` on that
   consumer with a wrapper that awaits the original and then rewrites each produced value that
   *refers to a `"url"` facade*: an `EndpointReference` on one; an `EndpointReferenceExpression` on
   one; or a `ReferenceExpression` any of whose `ValueProviders` does so (recursively).
3. A facade `EndpointReference` becomes the literal string `scheme://host[:port]`, port omitted when
   it is the scheme's default. A `ReferenceExpression` becomes a **new `ReferenceExpression` with
   only the facade providers replaced by literal text and every other provider left as a provider**.
   It is not flattened with `GetValueAsync`: that would resolve a container-network value on the host
   network, and would turn a `ParameterResource` secret into plaintext (in a manifest under
   `aspire publish`). If the public builder API cannot express this, the implementation falls back
   to rewriting only expressions whose providers are all facade endpoints and leaves a mixed one
   untouched, and says so in the log (below); it does not resolve foreign providers.
4. `EndpointProperty` values are derived from the facade's `EndpointAnnotation`:
   `Url` = the literal in 3; `Scheme` = `UriScheme`; `Host`/`IPV4Host` = `TargetHost`; `Port` and
   `TargetPort` = `Port`; `HostAndPort` = `host:port` with the port omitted only when default is
   *not* applied (a `host:port` pair keeps its port). The implementation's tests pin each result.
   Default port is derivable without a new type: `UrlSource` builds the annotation from `uri.Port`,
   which is already the scheme default (80/443) when the URL omitted it, so "port equals the scheme
   default" is a comparison against `UriScheme`. (`https://x:443` and `https://x` are
   indistinguishable and equivalent.)
5. Path, query and userinfo are **not** injected. They never reach the `EndpointAnnotation`
   (`UrlSource` takes only `uri.Scheme`, `uri.Host`, `uri.Port`), so the literal cannot carry a
   credential by construction, and this must not be "fixed" to match a fuller URL. Host-process
   consumers are unchanged and keep the value Aspire resolves for them; the spec does not claim
   host and container literals are byte-identical (the container literal omits a default port).
6. A value this cannot rewrite (an `EndpointProperty` added by a future Aspire, or the mixed
   expression above) is logged with the consumer, the key and the service name through
   `Name`/`Raw`, never with the URL, and left in place. Note that this leaves that container to the
   silent `FailedToStart`; the log line is the only signal, and that residual is stated in 3.3.

The wrap is idempotent: wrapped annotations are tracked (a marker, such as a
`ConditionalWeakTable` of wrappers, not a subclass of Aspire's annotation types, whose
subclassability and DCP's exact-type reads were not checked), and an already-wrapped annotation is
left alone, since the event can be raised more than once in a process. The implementation verifies
that replacing the annotation instance is accepted by DCP; the probe did exactly that.

### 3.2 Why wrap rather than inspect

The pre-flight asks "does this annotation look like a reference" and cannot answer for a delegate.
The wrapper does not need the answer for a consumer already known to reference the service: it runs
when DCP runs the callback and looks at the data, which also makes `WithEnvironment("X",
ep.Property(...))`, expressions, and `WithArgs` work. Closure reflection (route 2) was rejected for
depending on compiler-generated shapes; an eager pre-evaluation was rejected because it runs user
code twice.

### 3.3 What stays, and the gap that remains

- **The documented gap is kept, and narrowed in one place.** The shape match still decides which
  consumers are rewritten. A container that consumes a `"url"` service through a channel leaving
  neither shape annotation (an opaque `WithEnvironment(ctx => ...)` callback alone, a hand-written
  `EnvironmentCallbackAnnotation`) is **not** rewritten and still reaches DCP, failing with the raw
  `Host endpoint ... DCP Service` trace or a silent `FailedToStart`. So does a consumer reaching the
  service through an `IResourceWithConnectionString` built from its endpoint
  (`UrlConsumerWaitTests` records that case as unmet). Those two remain open; the pre-flight no
  longer throws for the shapes it does match.
- **"No silent hang" is therefore met for the shapes the pre-flight matches, and explicitly not met
  for the two above.** Closing them would mean either wrapping every container (Open Question 1)
  or executing callbacks early; neither is built here. The code comment that documents the gap
  (`ConsumedUrlService` remarks) stays, reworded to name exactly these remainders.
- **Host-process consumers**, and the `local`, `container`, `kubernetes` sources are untouched: the
  rewrite is gated on `ContainerResource` plus a `"url"` facade. The facade's `EndpointAnnotation`
  and `GetEndpoint` are not modified.
- The `WaitFor` drop and its skip warning are unchanged. `"disabled"` stays outside this.

### 3.4 Attack surface

The wrapper runs delegates already registered on a container the pre-flight matched, when Aspire
would have run them, and writes strings derived from the catalog `url` host/port/scheme. New
exposure to name: that host now appears in the container's environment and in `docker inspect`
(a boundary it did not cross before; scheme, host and port only, no userinfo/path/query, per 3.1.5).
The URL's source trust is unchanged (`servicesources.local.json` already decides where host
consumers connect). Log lines go through `Name`/`Raw` and never print the URL. Because the wrapper
decorates callbacks other packages added to the same container, it must call the original
unmodified and rewrite only values that refer to the facade; foreign values pass through untouched,
which is also what keeps secrets and container-network values intact (3.1.3). It must never throw
(a throw in a callback is the silent failure).

## 4. Consequences for the code

- `UnregisteredServiceStartupGuard`: the `throw` becomes the rewrite, in a new separately testable
  class called from the guard's handler. The guard is subscribed during `AddService`, so it is first
  among this package's `BeforeStartEvent` handlers for an AppHost that called `AddService` before
  subscribing its own; a handler subscribed earlier, or a later one that adds annotations after the
  rewrite, escapes it. State that in the class comment. The "Tracked as issue #72" text goes with
  the message.
- Comments: edit only those this change makes false or stale: `UrlSource` header (the
  "container consumer fails" and "option 1/option 2" paragraphs), `IServiceSource.cs` (#72 remark),
  `DisabledSource.cs` (#58/#72 remark), the guard's class and `ConsumedUrlService` remarks, and
  `DisabledConsumerWaitTests`. No tracker or upstream issue numbers in the replacement text. A repo
  wide sweep of the many other `#NNN` comments is not part of this.
- Docs that become false: `docs/sources/url.md` (the "refused" paragraph and container caveat; add
  one sentence that a loopback URL means the container itself, since Aspire gives these containers
  no route to the host), `docs/getting-started/samples.md`, `docs/sources/disabled.md` if it points
  at the refusal, and the "a container cannot reference it" comment in both sample `Program.cs`
  files.
- CHANGELOG `[Unreleased]`: new **Added** subsection (the refusal shipped in a release, so the
  limitation is released behaviour, but lifting it breaks nothing that worked, so it is neither
  Fixed nor Breaking). The old `0.x` line saying lifting it is "tracked as [#72]" stays as history.
- No public API change.

## 5. Tests (names first)

- Existing tests that assert the refusal are **inverted, not deleted**:
  `ContainerConsumerTests.UrlSourcedService_ReferencedByAContainer_FailsWithAServiceSourcesError`
  and `...ConsumedByAContainerViaWithEnvironment_FailsWithAServiceSourcesError` become "does not
  throw, and the consumer's callbacks produce the literal". `ReferencedByAHostProcess_IsLeftAlone`
  stays as the host-process regression.
- New class `UrlContainerReferenceTests`, carrying `[Trait("IO", "true")]` (it builds an AppHost
  through `AppHostDirectory`, which writes config files), no `Timing`/`Concurrency`. It drives
  `TestHelpers.PublishBeforeStartEventAsync`, then runs the consumer's callbacks. **Building an
  `EnvironmentCallbackContext` / `CommandLineArgsCallbackContext` is new test infrastructure** (no
  existing test does it). One test per row of the section 2 table (except the opaque-lambda row,
  which asserts the documented gap: not rewritten); default port omitted and non-default kept;
  `WithReference` yields both `INVENTORY_HTTPS` and `services__...__0` as literals; shape
  annotations gone; no throw; running the pre-flight twice leaves one wrapper; a mixed
  `ReferenceExpression` keeps its non-facade provider as a provider (assert it is not resolved); a
  callback value unrelated to the facade is untouched; a container referencing a `container`-sourced
  service is untouched; each `EndpointProperty` result.
- Live confirmation, because the in-memory tests cannot see DCP's silent failure (the thing that
  broke twice): extend a smoke script (`smoketest-container-source.sh` or a sibling) with a
  container referencing a `url`-sourced service, asserting the injected env and a fetch from inside.

## 6. Open Questions

1. **Close the opaque-callback gap by wrapping every container?** Probe route 3 did handle an
   opaque lambda. Doing it means touching containers that reference nothing and dropping the
   "pre-flight matches annotation shape only" gap the ticket asks to keep. Recommendation: not in
   this ticket; file it if the gap bites.
2. **Warn on a loopback `url` consumed by a container?** Verbatim injection makes a quiet
   misconfiguration reachable. Non-goal here; docs sentence only.
3. **Polyglot (TypeScript) consumers.** The generated `withReference` calls the same Aspire method,
   so the annotations should match, but this was not run. Verify via the TypeScript sample or CI;
   do not claim it in the CHANGELOG until seen.
4. **Aspire-version drift and publish mode.** Measured on 13.6.1 only (`aspire-matrix.yml` covers
   the rest). Under `aspire publish` the wrapper would emit the literal into the manifest; not run.
