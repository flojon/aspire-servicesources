# Endpoint calls that update an existing endpoint bypass the skip-and-warn gate (#334)

**Date:** 2026-09-14
**Status:** Draft
**Resolves:** #334 (`WithHttpEndpoint()`/`WithHttpsEndpoint()` called with the default endpoint
name on an out-of-band `ServiceResource` hits Aspire's in-place-update branch instead of the add
branch, so the call never reaches `ServiceResourceBuilder.WithAnnotation` and is silently
unreported in the skip-and-warn tally).
**Relates to:** [`2026-09-10-313-service-resource-dualwrite-bridge-design.md`](2026-09-10-313-service-resource-dualwrite-bridge-design.md)
(#313/#326) — this bug lives entirely inside the mechanism that design introduced, and its own §3
"Residual risk" note on the update branch already named half of what this document resolves.

---

## 1. The mechanism, verified against the actual pinned floor

Everything below was checked directly against this repo's pinned Aspire version
(`Directory.Build.props:74`, `AspireVersion = 13.5.2`) by decompiling
`~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll` with `ilspycmd 11.0.0.9375`
— not assumed from the issue's paraphrase of it.

`WithHttpsEndpoint<T>`/`WithHttpEndpoint<T>` (`ResourceBuilderExtensions`) both forward to the
generic `WithEndpoint<T>(builder, port, targetPort, scheme, name, env, isProxied, isExternal,
protocol)` overload. Its body, decompiled in full:

```csharp
public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder, int? port = null,
    int? targetPort = null, string? scheme = null, [EndpointName] string? name = null,
    string? env = null, bool? isProxied = null, bool? isExternal = null, ProtocolType? protocol = null)
    where T : IResourceWithEndpoints
{
    string text = scheme ?? (protocol ?? ProtocolType.Tcp).ToString().ToLowerInvariant();
    string resolvedName = name ?? text;
    EndpointAnnotation endpointAnnotation = builder.Resource.Annotations.OfType<EndpointAnnotation>()
        .FirstOrDefault(sb => string.Equals(sb.Name, resolvedName, StringComparisons.EndpointAnnotationName));
    if (endpointAnnotation != null)
    {
        if (port.HasValue) endpointAnnotation.Port = port;
        if (targetPort.HasValue) endpointAnnotation.TargetPort = targetPort;
        if (isExternal.HasValue) endpointAnnotation.IsExternal = isExternal.Value;
        if (isProxied.HasValue) endpointAnnotation.IsExplicitlyProxied = isProxied;
        ConfigureEndpointEnvironmentVariable(builder, endpointAnnotation, env);
        return builder;                                    // <-- UPDATE BRANCH: no WithAnnotation call
    }
    EndpointAnnotation endpointAnnotation2 = new EndpointAnnotation(...);
    ConfigureEndpointEnvironmentVariable(builder, endpointAnnotation2, env);
    return builder.WithAnnotation(endpointAnnotation2);     // <-- ADD BRANCH: goes through WithAnnotation
}
```

The branch selector is a name lookup on `builder.Resource.Annotations` — under this repo's design
(`ServiceResource facade`), that is the **facade's own collection**, which `ResolvedService.Bridge`
(`Sources/ResolvedService.cs:26-29`) pre-populates by copying every annotation instance `real`
already carries, *before* the AppHost author's own call runs:

- `UrlSource.Resolve` (`Sources/UrlSource.cs:52-61`) builds an `EndpointAnnotation` named
  `uri.Scheme` (`"http"` or `"https"`) directly on the facade — `url` has no `real`, so
  `BridgeUnregistered` is what puts it there, but the effect on the branch check is identical.
- `KubernetesSource.Resolve` (`Sources/KubernetesSource.cs:37`) calls
  `.WithEndpoint(..., name: scheme, ...)` on the real `ServiceExecutableResource` builder, and
  `Bridge` copies that same `EndpointAnnotation` instance onto the facade.
- `ContainerSource.Resolve` (`Sources/ContainerSource.cs:25`) does the identical thing for
  `container`, which is **not** an out-of-band source (`Reachability.OutOfBandSources` names only
  `"url"` and `"kubernetes"`), so this branch selection happens there too but has no gating
  consequence — see §4 for why `container` is out of scope.

So `service.WithHttpsEndpoint()` with no explicit `name:` resolves `resolvedName = "https"`, finds
the annotation the source itself already registered under that exact name, and takes the update
branch — which never calls `builder.WithAnnotation(...)`, the one point
`ServiceResourceBuilder` (`Sources/ServiceResourceBuilder.cs:118-153`) overrides to run
`Reachability.IsUnreachable` and either warn-and-skip or dual-write. The call is invisible to that
override entirely, for both the add-branch's gate and the add-branch's dual-write.

**Root-cause claim from the issue, verified true:** "for a new annotation" is exactly the qualifier
that matters, and it is Aspire's own code drawing that line — not something this package's design
overlooked so much as a case its dispatch model (`WithAnnotation` as the sole interception point)
structurally cannot see, because the update branch is a different code path inside Aspire's
extension method, not a different call into ours.

## 2. Confirming this predates nothing — it was introduced by #326

Before #326 (`243c227`, merged today, still entirely inside `[Unreleased]`), `AddService` returned
`IResourceBuilder<IResourceWithServiceDiscovery>`, and the **only** way to reach `WithHttpsEndpoint`
was through `Configure<IResourceWithEndpoints>(r => r.WithHttpsEndpoint())`
(`ServiceConfigurationExtensions.cs`, decompiled from `243c227~1`). `Configure<T>`'s gate
(`IsUnreachable<T>(annotation.Source)`) ran **before** `configure(...)` was ever invoked — i.e.
before Aspire's `WithEndpoint<T>` had a chance to look at the annotation collection at all. Whether
that inner call would itself have taken the add or update branch was irrelevant: the whole lambda
was skipped or run as one unit. `IResourceBuilder<IResourceWithServiceDiscovery>` also could not
bind Aspire's own `WithHttpsEndpoint<T>` extension directly (`T` would have to satisfy
`IResourceWithEndpoints`, which `IResourceWithServiceDiscovery` does not implement), so no native
call bypassing `Configure<T>` was even possible to write.

This gap is new in #326: it is a direct consequence of `ServiceResourceBuilder.WithAnnotation`
replacing `Configure<T>`'s per-call gate with a per-`WithAnnotation`-invocation gate, which
(correctly, for every other native method) intercepts less than the whole call and (here,
incorrectly) intercepts *nothing* for the update branch.

**CHANGELOG consequence:** per `CHANGELOG.md`'s own stated rule ("A bug introduced and fixed within
the same `[Unreleased]` cycle never shipped... nothing for a consumer to have hit"), and since #326
has not been released, **no `### Fixed` entry is warranted regardless of what this ticket ships** —
the dual-write bridge feature simply ships with this gap closed (or documented) the first time,
with no separate changelog line calling out a "fix."

## 3. Fix shape: a non-generic shadow overload, verified by real compiled probes

**Revision note (2026-09-14):** this section originally chose "accept and document" (issue option
3). The human rejected that resolution — a known, permanent reporting gap is not acceptable — and
directed investigation of a fourth option: shadow `WithHttpEndpoint`/`WithHttpsEndpoint` with
non-generic overloads on `IResourceBuilder<ServiceResource>` that C# overload resolution prefers
over Aspire's generic ones, gate inside the shadow before ever touching Aspire's own branch logic,
and delegate to Aspire's implementation by fully-qualified static call when reachable. §3.1 and §3.2
below (options 1 and 2) are unchanged from the original revision — both still verified infeasible/
out of scope on the same grounds. What follows is new.

### 3.1 Why option 1 (report at `BeforeStartEvent` by diffing) does not work

Option 1 needs some observable difference between "the source's own registration" and "state after
the AppHost's call" to detect that a call happened at all. But look again at the update branch: every
mutation is gated on the corresponding parameter's `.HasValue` — `if (port.HasValue) ... `. The
ticket's own primary repro is `service.WithHttpsEndpoint()` with **no arguments** — every one of
`port`, `targetPort`, `isExternal`, `isProxied` is `null`, so **the update branch mutates nothing**.
The `EndpointAnnotation` after the call is reference- and value-identical to the one the source
registered. There is no diff to find, at `BeforeStartEvent` or anywhere else that only inspects
end state: the call and its absence are indistinguishable from outside Aspire's own code, because
Aspire's own code is the only thing that ever saw the call happen. This rules the option out for
the exact case the ticket exists to fix, not just makes it awkward — it is verified infeasible, not
merely costly.

(A version of option 1 restricted to "differs after a call that *did* pass arguments" would catch
part of §4's mutation-with-args case, but not the no-arg case the ticket's acceptance criterion 1
requires, so it does not clear the bar on its own either.)

### 3.2 Why option 2 (sources don't pre-register the default name) is out of scope for a bug fix

The `"https"`/`"http"` pre-registration on `url`/`kubernetes` (and `container`, which shares the
mechanism though it is not gated) is exactly what lets a consumer read
`service.GetEndpoint("https")` or `WithReference(service)` **without the AppHost author calling
`WithHttpsEndpoint()` at all** — the ordinary case for these sources, where the endpoint is data the
source already knows (the configured URL; the port-forward's local port), not something an AppHost
author configures. Moving that registration off the name Aspire's own helpers default to would mean
every such consumer breaks unless the AppHost author now explicitly calls `WithHttpsEndpoint()`
first — a behavior change to the wire contract between a source and its consumers, felt by every
existing AppHost using `url`/`kubernetes`/`container`, not a targeted fix to a reporting gap. It
would also very likely trip this repo's own size-gate predicates (config/wire-shape change,
multiple sources touched) were it scoped as its own ticket — which is the right way to scope it, if
it is ever pursued, not folded into #334.

### 3.3 Why the shadow can work at all: `AddService` returns a concrete, unwidened type

The idea only has a chance because `AddService` (`ServiceSourcesBuilderExtensions.cs:77-78`) returns
`IResourceBuilder<ServiceResource>` **concretely** — not an interface `ServiceResource` merely
satisfies. Nothing between `AddService`'s return and the AppHost author's `.WithHttpsEndpoint()`
call widens that static type: `ServiceResourceBuilder` (the internal type actually returned, per
§1/§2's design) declares `IResourceBuilder<ServiceResource> WithAnnotation<TAnnotation>(...)` and
returns `this`, so a fluent chain of native calls stays typed as `IResourceBuilder<ServiceResource>`
throughout. That is exactly the receiver type a non-generic extension method can be written against
verbatim, with no generic parameter of its own to infer — the condition C# overload resolution needs
to prefer it over Aspire's `WithHttpsEndpoint<T>(this IResourceBuilder<T> builder, ...)`.

### 3.4 Proof, not argument: a real compiled and executed probe

Reasoning about C# overload-resolution rules is not enough on its own — this was verified with a
throwaway xUnit test added to `test/Aspire.Hosting.ServiceSources.Tests/`, compiled and run against
all three target frameworks (`net8.0`/`net9.0`/`net10.0`) this repo builds, then deleted (not
committed; nothing about this section depends on a file that still exists in the tree).

**Probe 1 — does the shadow bind at all, with the exact call shape an AppHost author writes?**

```csharp
public static class ShadowOverloadProbeExtensions
{
    public static bool ShadowWasCalled;

    // Non-generic, exact receiver type match — the candidate shadow.
    public static IResourceBuilder<ServiceResource> WithHttpsEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port = null, int? targetPort = null, string? name = null, string? env = null, bool? isProxied = null)
    {
        ShadowWasCalled = true;
        return Aspire.Hosting.ResourceBuilderExtensions.WithHttpsEndpoint(builder, port, targetPort, name, env, isProxied);
    }
}

[Fact]
public void ProbeWhichOverloadBinds()
{
    IResourceBuilder<ServiceResource> serviceBuilder = builder.AddResource(new ServiceResource("probe-svc"));
    serviceBuilder.WithHttpsEndpoint();   // no explicit generic argument, default args — the real call shape
    Assert.True(ShadowOverloadProbeExtensions.ShadowWasCalled);
}
```

Both `Aspire.Hosting` (for the generic method) and the probe's own namespace were in scope via
`using` at the call site — the ambiguity condition the human asked to be ruled out. Result: **the
project compiled with zero errors** (no `CS0121` ambiguous-call error — had both candidates been
equally applicable, this would have failed to build, not merely picked one arbitrarily), and the
test **passed on net8.0, net9.0, and net10.0**, proving `ShadowWasCalled` was `true` — the shadow
was selected, not Aspire's generic method. The build did emit a pre-existing repo analyzer warning,
`ASPIREEXPORT008` ("Extension method 'WithHttpsEndpoint' on exported type is missing `[AspireExport]`
or `[AspireExportIgnore]`") — a real, separately-tracked implementation requirement, not a reason to
doubt the binding result; see §3.6.

**Probe 2 — does gating inside the shadow actually close the gap, end to end, against the real
production types?** Built against `ServiceResourceBuilder`/`Reachability`/`ServiceSourceAnnotation`
directly (internal, reachable via this repo's existing `InternalsVisibleTo`), reproducing the exact
#334 scenario: a facade carrying a pre-existing `"https"`-named `EndpointAnnotation` (as
`UrlSource`/`KubernetesSource` register), wrapped exactly as `ResolvedService.BridgeUnregistered`
wraps a `"url"` source (`real: null`), then the shadow gates on `Reachability.IsUnreachable` before
ever calling into Aspire's implementation:

```csharp
public static IResourceBuilder<ServiceResource> WithHttpsEndpoint(
    this IResourceBuilder<ServiceResource> builder,
    int? port = null, int? targetPort = null, string? name = null, string? env = null, bool? isProxied = null)
{
    var source = builder.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault()?.Source;
    if (source is not null && Reachability.IsUnreachable(typeof(EndpointAnnotation), source))
    {
        ServiceSourcesWarnings.For(builder.ApplicationBuilder)
            .AddSkip(builder.Resource.Name, source, Reachability.CapabilityLabel(typeof(EndpointAnnotation)));
        return builder;
    }
    return Aspire.Hosting.ResourceBuilderExtensions.WithHttpsEndpoint(builder, port, targetPort, name, env, isProxied);
}
```

Calling `serviceBuilder.WithHttpsEndpoint(port: 9999)` against this fixture and asserting
afterward: `preexisting.Port` stayed `null` (the mutation §4 describes did **not** happen — the
in-place property assignment this ticket's severity claim didn't originally cover is also closed by
this fix shape, not just the reporting gap), and exactly one warning was recorded, containing
`"WithHttpEndpoint/WithHttpsEndpoint"` — the same label `Reachability.CapabilityLabel` already
produces for the add-branch case today. **Passed on net8.0/net9.0/net10.0.** This is the strongest
evidence available short of shipping the real implementation: the gate the ticket asks for actually
fires, for the actual scenario, using the actual production `Reachability`/`ServiceSourceAnnotation`
types, with a real `Assert` on the real mutable state, not a description of what should happen.

### 3.5 Decision

**Shadow `WithHttpEndpoint`/`WithHttpsEndpoint` with non-generic overloads on
`IResourceBuilder<ServiceResource>`**, each gating via `Reachability.IsUnreachable` exactly as
`ServiceResourceBuilder.WithAnnotation` does today, then delegating to Aspire's own
`Aspire.Hosting.ResourceBuilderExtensions.WithHttpEndpoint`/`WithHttpsEndpoint` by fully-qualified
static call when reachable — never through extension-method syntax on `builder`, which would recurse
into the shadow itself. Concretely, for the Plan phase:

1. Add the two shadow methods (each mirroring Aspire's current public signature exactly, so no
   AppHost-visible signature changes) as **`public static`** methods — extension-method binding at
   an AppHost's own call site is resolved against that external assembly's `using` directives, so an
   `internal` shadow would compile inside this package but be invisible to any AppHost project
   referencing it, and never bind at all. They must live in the `Aspire.Hosting.ServiceSources`
   namespace specifically (not merely any public namespace this package owns): verified —
   `AddService` itself is declared in that exact namespace
   (`ServiceSourcesBuilderExtensions.cs:9`), and `samples/DemoAppHost/Program.cs:2` carries an
   explicit `using Aspire.Hosting.ServiceSources;` that must already be present for `AddService` to
   bind at all. Placing the shadows in the same namespace — most simply, as two more methods on
   `ServiceSourcesBuilderExtensions` itself — means every AppHost that can already call
   `AddService` automatically has the shadow in scope too, with no new `using` for anyone to add and
   nothing for this ticket to document as a migration step.
2. Reuse `Reachability.IsUnreachable`/`Reachability.CapabilityLabel` unchanged — this is the same
   table `ServiceResourceBuilder.WithAnnotation` already consults, keyed the same way
   (`typeof(EndpointAnnotation)`), so the two interception points report identically and neither can
   drift from the other.
3. `ResolvedService.Bridge`'s existing comment (`Sources/ResolvedService.cs:20-25`) still needs the
   addendum acceptance item 5 asks for, but the framing changes from "documenting a permanent gap"
   to "documenting why a second interception point exists alongside `WithAnnotation`" — the Plan
   phase writes the actual comment edit once the shadow's real location is chosen.
4. README's skip section (`README.md:1761-1792`) needs no new "known gap" paragraph under this
   decision — the behavior described there (native calls skipped and logged for `url`/`kubernetes`)
   becomes true for the default-named case too, so no doc change is needed beyond what the shipped
   fix already makes true. (If the Plan phase decides to add a brief note that the fix reaches
   `WithHttpEndpoint`/`WithHttpsEndpoint` specifically — not the raw `WithEndpoint` overloads, see
   §3.6 — that is a discretionary clarity addition, not a requirement.)

### 3.6 Residual risks and scope edges the Plan phase must verify or explicitly accept

None of these were disproven by the probes above — they are exactly the kind of "still unverified
end-to-end" items the #313 spec itself flagged for its own design (§8 there), named here with the
same rigor rather than left implicit:

- **`ASPIREEXPORT008` (verified, real):** the probe's build emitted this analyzer warning for the
  shadow method, unprompted. This repo's CI build runs `-warnaserror`
  (`ticket-notes.md`'s own recorded verify-leg finding), so an un-annotated shadow **fails CI**, not
  merely warns locally. The shadow needs `[AspireExport]` (to keep projecting into TypeScript/guest-
  language AppHosts exactly as Aspire's own method already does — the whole point of #326 was that
  native vocabulary "just works" for guest languages) rather than `[AspireExportIgnore]`, which would
  silently remove `WithHttpsEndpoint`/`WithHttpEndpoint` from the generated handle for every source,
  a regression `NewEndpoint_OnUrlSource_IsSkippedAndReported`'s C# behavior wouldn't catch but the
  `typecheck-typescript` CI leg would. The Plan phase must add `[AspireExport]` and treat the
  `typecheck-typescript` leg (already named in `ticket-notes.md` as "run before PR if feasible") as
  load-bearing for this specific risk — **not optional** for this ticket the way it is for most
  ATS-unrelated bug fixes, because this fix's mechanism (a second extension method on an exported
  type) is exactly the shape that analyzer exists to catch, and there are now two candidate
  `WithHttpsEndpoint`s in scope for ATS's own codegen to reconcile (Aspire's and this package's),
  which needs the `typecheck-typescript` leg to actually prove out rather than assume.
- **Binary-compatibility `bool` (non-nullable) `isProxied` overload, not shadowed (verified edge
  case, narrow):** Aspire ships a second `WithHttpEndpoint`/`WithHttpsEndpoint` overload taking
  `bool isProxied` rather than `bool?`, for source compiled against an older signature
  (`[AspireExportIgnore(Reason = "Binary compatibility shim...")]`, decompiled). An AppHost author
  who explicitly passes a literal `bool` (not the ordinary `bool?`-accepting call) binds to that
  compat-shim overload — which this design does not shadow — and its internal delegation calls
  Aspire's own nullable overload using `T` as *its own* generic parameter, inside Aspire's assembly,
  which cannot see this package's shadow at all. This is a real, narrow, unclosed edge: an explicit
  non-nullable `isProxied: true`/`isProxied: false` argument on the default name still takes the
  unguarded update branch. In practice this signature is rarely if ever called explicitly (the
  public, IntelliSense-visible parameter type is `bool?`), and closing it would mean also shadowing
  the compat overload — cheap to add if the Plan phase chooses to, not required by the ticket's
  acceptance criteria, which name `WithHttpEndpoint`/`WithHttpsEndpoint` without specifying which
  overload. Flagged rather than silently left.
- **The raw `WithEndpoint`/`WithEndpoint` callback overloads are not in this ticket's scope, and are
  at least as broken (verified, out of scope):** decompiling
  `WithEndpoint<T>(builder, endpointName, Action<EndpointAnnotation> callback, createIfNotExists)`
  shows its **add branch also never calls `WithAnnotation`** — it does
  `builder.Resource.Annotations.Add(endpointAnnotation)` directly. That overload is unguarded in
  both branches, not just the update one, for the identical reason (a different Aspire code path
  the `WithAnnotation` interception point cannot see). It is not named in #334's acceptance
  checklist or repro tests, sharing only the root cause, not the ticket — flagged here as a sibling
  gap for a possible future issue, not built, not filed, consistent with this document's scope
  discipline. Do not read its existence as a reason to widen this fix's surface without a human
  decision to do so.

### 3.7 Repro-test disposition

The repro test in the ticket asserts `DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported` should
pass (i.e. the call *is* reported) — under this decision that assertion is exactly what the fix
makes true, unmodified. Recommend the Plan phase commit `EndpointSkipGapRepro.cs` from the issue
**as written, with all three cases passing as-is**: no inversion, since the gap is closed rather
than accepted.

**Do not add a fourth case for the with-arguments mutation finding.** That is §4's finding, tracked
separately as #335, not part of #334's acceptance criteria — `EndpointSkipGapRepro.cs`'s scope stays
exactly the three cases the issue described. Whoever picks up #335 adds its own repro coverage
there, in that issue's own branch.

## 4. Widened finding, tracked separately as #335

Re-reading the decompiled body in §1 with an eye on the issue's own severity claim ("The resolved
URL is not corrupted... I went looking for a worse consequence and did not find one"): that claim
was checked only against the no-argument repro (`service.WithHttpsEndpoint()`). The update branch's
four `if (x.HasValue) endpointAnnotation.X = x` lines are **not** gated by source or reachability at
all — they run whenever the AppHost author passes an explicit `port`, `targetPort`, `isExternal`, or
`isProxied` argument to a call that resolves to an already-registered name, regardless of source.

For `kubernetes`, that annotation instance is the **same object** `KubernetesSource.Resolve`
registered on the real, DCP-registered `ServiceExecutableResource` (shared by `ResolvedService.Bridge`,
§2's comment). So `service.WithHttpsEndpoint(port: 9999)` on a `kubernetes`-sourced service whose
declared name matches (the default, unnamed case) does not just go unreported — it silently repoints
the real `kubectl port-forward` process's configured port, in place, with no gate and no warning.
This is the exact "configuration reaching the wrong process" failure mode
`Reachability`/`ServiceConfigurationExtensions.IsUnreachable` exists to prevent (see
`ServiceResourceBuilder.cs:5-11`'s own framing), happening through a path neither this design nor
the original `Configure<T>` gate ever covered, because — like the reporting gap — it is entirely
inside Aspire's own update branch.

**This is a distinct, more severe defect from #334's reporting gap**, not a rephrasing of it: #334
is "a developer isn't told their call was ignored"; this is "a developer's call was not ignored, and
landed somewhere unintended, still with no warning." It shares the exact same root cause and would
very likely be closed by the same investigation, but fixing it is not part of #334's acceptance
checklist (which frames the bug as reporting-only, following the issue). **Filed and tracked
separately as [#335](https://github.com/flojon/aspire-servicesources/issues/335)** — out of this
ticket's scope; #334's Plan phase must not build a fix for it and must not add repro coverage for it
to `EndpointSkipGapRepro.cs` (see §5).

Note for whoever picks up #335: §3's shadow-overload fix, once shipped for #334, closes this finding
too as a side effect for the `WithHttpsEndpoint`/`WithHttpEndpoint` default-name case specifically —
Probe 2 in §3.4 asserts exactly that (`preexisting.Port` stayed `null` after a `port: 9999` call).
What #335 would still need to cover is the raw `WithEndpoint` overloads (§3.6's last bullet), which
#334's fix does not touch.

`container` is excluded from this finding's scope even though `ContainerSource` shares the same
default-name pre-registration mechanism: `container` is not in `Reachability.OutOfBandSources`, so
native calls against it are never gated or skipped in the first place — a mutation reaching its real
resource is the intended, reachable behavior, not a bypass of anything.

## 5. Test and acceptance-criteria disposition

| Acceptance item | Disposition |
|---|---|
| 1. Default-named endpoint call on `url` reported in tally | **Fixed** — the §3 shadow overload gates before Aspire's update branch runs; Probe 2 (§3.4) proves a warning is recorded end-to-end against the real production types. |
| 2. No functional regression (resolved URL/env correct; kubernetes tuples unmutated) | Preserved and, for the with-arguments case, *newly* closed too (Probe 2: `preexisting.Port` stays `null`) — a strictly stronger guarantee than before, not merely a non-regression. |
| 3. `EndpointSkipGapRepro.cs` committed | Recommended for the Plan phase, committed **as written in the issue, all three assertions unmodified** — per §3.7, the fix makes them true rather than requiring inversion. |
| 4. Pick one of three fix shapes | **A fourth shape**, directed by the human after §3.1/§3.2 ruled the issue's own three options out/infeasible: a non-generic shadow overload on `IResourceBuilder<ServiceResource>`, verified viable by two real compiled-and-run probes (§3.3-§3.4). |
| 5. Revisit `ResolvedService.Bridge`'s comment | Framing changed per §3.5 item 3 — documents why a second interception point exists, not a permanent gap; Plan phase writes the exact wording once the shadow's file location is chosen. |

## Attack surface

None added. The shadow overloads add no new input parsing, no new shell/URL/path/query
construction, and cross no new trust or process boundary — they are ordinary C# extension methods
running in the same AppHost process as everything else in this package, gating on the same
`Reachability` table and reusing Aspire's own `WithHttpEndpoint`/`WithHttpsEndpoint` implementation
by direct call rather than reimplementing it. The one thing worth naming precisely: `Reachability`'s
gate is now consulted from **two** interception points (`ServiceResourceBuilder.WithAnnotation` and
these shadows) instead of one, both reading the identical `Reachability.IsUnreachable`/
`CapabilityLabel` — reused, not duplicated logic — so the two cannot silently drift into disagreeing
about what gets skipped. §3.6 names the residual scope edges (the binary-compat `bool`-`isProxied`
overload, the raw `WithEndpoint` overloads) precisely so they are not mistaken for closed by this
change.

## Open Questions

None. The human's three prior open questions are resolved: (1) a fourth fix shape was investigated
and verified viable by two real compiled-and-run probes rather than defaulting back to
accept-and-document — §3.3-§3.5; (2) the widened with-arguments mutation finding is filed and
referenced as [#335](https://github.com/flojon/aspire-servicesources/issues/335) — §4; (3) the repro
test's scope stays the three cases from the issue, with #335's own coverage left to that issue —
§3.7. §3.6 names residual scope edges (the `bool`-`isProxied` compat overload; the raw `WithEndpoint`
overloads) as things the Plan phase must consciously accept or close, not as open product questions —
none of them changes the fix shape decided here.
