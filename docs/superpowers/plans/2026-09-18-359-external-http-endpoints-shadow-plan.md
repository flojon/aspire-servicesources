# Gating `WithExternalHttpEndpoints` at the call site (#359) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the tenth gated endpoint shadow — a non-generic `WithExternalHttpEndpoints` on
`IResourceBuilder<ServiceResource>` that returns early on a `url` or `kubernetes` service with its own
skip warning, so `IsExternal` is never written rather than written and put back.

**Architecture:** One new method in `ServiceSourcesBuilderExtensions`, built from the in-file
`WithHttpEndpoint`/`WithHttpsEndpoint` template, plus one optional parameter on the private
`GateEndpointCall` so this surface can name itself in the warning instead of inheriting a label for
three methods the developer did not call. Nothing else in production changes except two prose
corrections: the `EndpointMutationDetector` class remark, and the CHANGELOG's stale claim that #372
is what covers this. The detector's behaviour, `Reachability.CapabilityLabel`, and the nine existing
gated surfaces are all untouched.

**Tech Stack:** C# / .NET (net8.0, net9.0, net10.0 multi-target), xUnit, `Aspire.Hosting` 13.5.2
(pinned floor, `Directory.Build.props`).

**Spec:** [docs/superpowers/specs/2026-09-18-359-external-http-endpoints-shadow-design.md](../specs/2026-09-18-359-external-http-endpoints-shadow-design.md)

## Global Constraints

- **Pinned Aspire floor: `Aspire.Hosting` 13.5.2.** Every claim this plan makes about Aspire's
  `WithExternalHttpEndpoints<T>` comes from #359's own decompilation, carried through the spec. Do
  not re-derive it from memory, and do not re-decompile. If something contradicts the spec, stop and
  report rather than adjusting the code to fit.
- **Skip and warn. Never throw, never mutate-then-revert** (spec §3.4). The whole point of this
  ticket over #372 is that the write never happens — not transiently, not at all. Any test that
  asserts the outcome only after `BeforeStartEvent` is asserting the weaker #372 property; the
  call-time assertion is the one that distinguishes them.
- **Fully-qualified static delegate, never extension syntax on `builder`** (spec §1.1). The return is
  `Aspire.Hosting.ResourceBuilderExtensions.WithExternalHttpEndpoints(builder)`. Written as
  `builder.WithExternalHttpEndpoints()` it resolves back into the shadow and recurses.
- **No `[OverloadResolutionPriority]`** (spec §4.1). It exists on `WithCommand` to break a CS0121
  between *two sibling shadows in this package*; `WithExternalHttpEndpoints` has no sibling, and the
  non-generic-over-generic tie-break is what the other nine already rely on. If the probe in Task 2
  does not compile, that is a stop-and-report — not a cue to reach for the attribute.
- **`Reachability.CapabilityLabel` is not changed** (spec §2.2, §5). The literal
  `"WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"` at `ServiceResourceBuilder.cs:92` stays exactly
  as it is; nine `EndpointSkipGapRepro` / `ServiceSourcesBuilderExtensionsTests` assertions are
  `Contains` substrings of it. The new label is passed from the one call site that knows it.
- **`GateEndpointCall`'s nine existing call sites are not touched** (spec §2.3). The new parameter is
  `string? capability = null` with a null default, so lines 271/293/313/335/355/374/401/422/438 keep
  compiling and keep emitting exactly the text they emit today. A diff that edits any of them is
  wrong.
- **`EndpointMutationDetector`'s behaviour is not changed** (spec §5). Only its class remark. It is
  not made aware of the shadow, not given a suppression list, and not told which surfaces are gated.
  The "no second warning" property is structural (spec §3.1), not something the detector is
  configured into.
- **Comment style:** short, WHY-only, under roughly fifteen words, no changelog, history or
  narrative. **No third-party issue numbers or links in shipped comment text** — this repo's own
  issue numbers are fine.
- **CHANGELOG entry is required**, under `## [Unreleased]` → `### Fixed` (spec §4.4), citing
  `([#359])`. **`[#359]` is already defined at `CHANGELOG.md:1732`** — do not add a second link
  definition, which would be a duplicate-reference footgun.
- **Verify legs** (from the ticket notes file). Cheap, run every task: `dotnet restore`,
  `dotnet build -c Release --no-restore -warnaserror`, `dotnet test -c Release --no-build`.
  `-warnaserror` and `-c Release` are what decide green from red — a Debug build, or one without
  that flag, is a false green. All three are intrinsic over net8.0/net9.0/net10.0: one invocation
  covers the framework matrix. `dotnet pack` runs once, in Task 4.
- **The TypeScript export-surface leg cannot run on this machine** (spec §4.3) — no `node`, no `npm`,
  no pinned Aspire CLI 13.5.3. It is the leg most likely to catch a mistake in Task 3's
  `[AspireExport]`, and it must be named as CI-verified-only in the PR body, never implied to have
  passed. The same goes for the three smoke-test scripts and the `verify-invariants` python checks.
- All commands run from the worktree root
  `C:\Source\aspire-servicesources\.claude\worktrees\ticket-359-82c6f5`, on branch
  `claude/ticket-359-82c6f5`.

---

## File Structure

- **Modify** `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` — one new
  `WithExternalHttpEndpoints` method after `WithHttpsEndpoint`, and one optional parameter plus one
  XML-doc clause on `GateEndpointCall` (line 510). The shadow goes in this file because every other
  gated shadow is here and the gate itself is private to it.
- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` — the class
  remark at lines 12–17 only. No code.
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/OverloadResolutionProbe.cs` — one new
  `OverloadProbe` entry. This file exists precisely because a call written inside
  `Aspire.Hosting.ServiceSources.Tests` proves nothing about overload resolution: extension lookup
  stops at the shadow's own namespace before Aspire's is consulted. `ServiceSourcesOverloadProbe` is
  a descendant of neither, so both candidates compete as equals.
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs` — two new tests, in
  that file's established style. **The `kubernetes` fixture this file needs already exists** —
  `FakePortAllocator` (`:104-111`), `KubernetesDefinition()` (`:113-119`) and `KubernetesDevConfig()`
  (`:121-122`) — so the spec's §7 "share-or-duplicate" question is settled by neither: **reuse the
  in-file fixture that is already there.** Spec §4.2/§7 assumed it was absent; it is not. Do not copy
  `EndpointMutationDetectorTests.cs:35-55`, do not extract a shared fixture into a third file, and do
  not refactor the three existing tests that inline `new KubernetesSource(...).Resolve(...)` — that
  would be unrequested churn in tests this ticket is not about.
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` — one test
  retargeted and renamed, two added, one comment replaced.
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs` — one string
  added to `ExportedIds_MatchTheKnownSurface`'s hand-maintained `expected` array (`:90-122`).
- **Modify** `CHANGELOG.md` — one new `### Fixed` entry, and an in-place edit of #372's `### Added`
  entry at lines 159–161. No new link definitions.

No change to `Reachability`, `ServiceResourceBuilder`, `ServiceSourcesWarnings`, `UrlSource`,
`KubernetesSource`, `ResolvedService`, `README.md`, the samples, or any existing public signature.

---

## Task 1: Repair the detector suite's `IsExternal` coverage before the shadow lands

`EndpointMutationDetectorTests.ExternalHttpEndpoints_OnKubernetesSource_IsRevertedAndReported`
(`:293`) drives its mutation with `service.WithExternalHttpEndpoints()`. The moment Task 2's shadow
exists that call binds to the shadow, is skipped, and every assertion in the test becomes false — so
it has to move to the path that stays unshadowed **first**, or Task 2 lands on a red suite and its own
red→green claim is unreadable.

Spec §3.2 decides retarget-and-split rather than delete: this is the detector suite's only
`IsExternal` case, `EndpointUpdateContext` exposes `IsExternal` as settable, and a guest-language
AppHost can still write it — so the detector is still the only thing that catches that, permanently
(spec §3.4). The `WithExternalHttpEndpoints`-specific half of the old test becomes Task 2's own
assertion.

Checklist item 8's missing `url` leg lands here too, for the same reason and in the same shape.

**This task has no red leg, and that is by construction** — both tests exercise the detector through
the callback path, which Task 2 does not touch, so both are green before and after. Its red→green
value is negative: it is what keeps Task 2's red→green honest.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (modify)

**Interfaces:**
- Consumes: `GuestLanguageEndpointCallbacks.EndpointCallback(IResourceBuilder<ServiceResource>, string, params (string Property, object? Value)[])`,
  the in-file fixture helpers `Builder()`, `Url(IDistributedApplicationBuilder)`,
  `Kubernetes(IDistributedApplicationBuilder)`, `Endpoints(IResourceBuilder<ServiceResource>)`, and
  `TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(IDistributedApplicationBuilder)`. No
  production code changes.
- Produces: nothing new. Two renamed/added `[Fact]`s only.

- [ ] **Step 1: Retarget and rename the existing test**

In `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs`, find the comment and
test at lines 290–311:

```csharp
    // The argument for a state-keyed detector over another per-method shadow: WithExternalHttpEndpoints
    // is a public Aspire method that sets IsExternal directly on existing annotations, so no gate
    // ever sees it. Caught here with no WithExternalHttpEndpoints-specific code at all.
    [Fact]
    public async Task ExternalHttpEndpoints_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        service.WithExternalHttpEndpoints();

        Assert.True(Assert.Single(Endpoints(service)).IsExternal);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);

        // The field the developer's call actually wrote, named in the line they have to act on.
        Assert.Contains(
            "endpoint 'https' was changed after this service resolved (IsExternal) and has been put back",
            Assert.Single(warnings));
    }
```

and replace it with:

```csharp
    // Driven through the callback rather than through C#'s WithExternalHttpEndpoints: that method is
    // gated, so a test written through it would assert nothing about the detector (#359).
    [Fact]
    public async Task ChangedIsExternal_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("IsExternal", true));

        Assert.True(Assert.Single(Endpoints(service)).IsExternal);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);

        // The field the developer's call actually wrote, named in the line they have to act on.
        Assert.Contains(
            "endpoint 'https' was changed after this service resolved (IsExternal) and has been put back",
            Assert.Single(warnings));
    }
```

The name matches its siblings `ChangedTargetHost_OnUrlSource_…` and `ChangedProtocol_OnKubernetesSource_…`,
which is what it always was underneath. The revert-warning assertion is unchanged verbatim: the
message names the *field*, not the method that wrote it, so retargeting does not move it.

- [ ] **Step 2: Add the missing `url` leg**

Immediately after the test from Step 1, add:

```csharp
    // The url counterpart the shipped suite lacked. UrlSource registers a single 'https' endpoint,
    // so the callback name resolves to the update branch.
    [Fact]
    public async Task ChangedIsExternal_OnUrlSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Url(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("IsExternal", true));

        Assert.True(Assert.Single(Endpoints(service)).IsExternal);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);
        Assert.Contains(warnings, warning =>
            warning.Contains("Service 'inventory'")
            && warning.Contains(
                "endpoint 'https' was changed after this service resolved (IsExternal) and has been put back"));
    }
```

`Assert.Contains(warnings, …)` rather than `Assert.Single(warnings)` here, matching the sibling
`ChangedTargetHost_OnUrlSource_IsRevertedAndReported` at `:255`: a `url` service can accumulate other
warnings of its own, and this test's subject is the revert line, not the tally.

- [ ] **Step 3: Run both tests**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~ChangedIsExternal"
```

Expected: **both PASS.** Both are green against today's tree — the detector already covers
`IsExternal`, and the callback path is exactly how #372's other field tests reach it. If
`ChangedIsExternal_OnUrlSource_IsRevertedAndReported` fails with an `InvalidOperationException` from
the harness naming `'https'`, the `url` fixture's endpoint name is not what `UrlSource.cs:53-61`
registers — stop and report, because Task 2's `url` skip test rests on the same fixture.

- [ ] **Step 4: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across net8.0/net9.0/net10.0. Nothing else in the
suite references the old test name.

- [ ] **Step 5: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Move the detector's IsExternal coverage onto the callback path (#359)

The IsExternal case drove its mutation with C#'s
WithExternalHttpEndpoints, which the next commit gates -- after which
the call is skipped and every assertion in that test is false. Retargeted
onto the guest-language endpoint callback, which no shadow can reach and
which is therefore the path the detector permanently owns, and renamed to
match its ChangedTargetHost/ChangedProtocol siblings.

The url leg the suite never had is added alongside it: IsExternal was
covered on kubernetes only, and the two sources take different branches
through Restore.

The revert assertion is unchanged -- the message names the field, not the
method that wrote it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: The gated shadow

The ticket itself: acceptance criteria 1, 2, 3, 4, 6 and the second half of 7. The shadow is the
in-file `WithHttpEndpoint`/`WithHttpsEndpoint` template with no arguments to forward (spec §1.1), and
`GateEndpointCall` grows one optional parameter so this surface names itself rather than inheriting
a label for three methods the developer did not call (spec §2.3).

`[AspireExport]` is deliberately **not** added here — it is Task 3, where it has its own red leg
against `CatalogExportsTests`.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/OverloadResolutionProbe.cs` (add one entry)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs` (add three tests)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (add one test)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` (one new method; one
  parameter and one XML-doc clause on `GateEndpointCall` at `:498-523`)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` (class remark,
  `:11-25`)

**Interfaces:**
- Consumes: `GateEndpointCall(IResourceBuilder<ServiceResource>)` as it stands today,
  `Reachability.CapabilityLabel(Type)`, `ServiceSourcesWarnings.For(IDistributedApplicationBuilder).AddSkip(string, string, string)`,
  `Aspire.Hosting.ResourceBuilderExtensions.WithExternalHttpEndpoints<T>(IResourceBuilder<T>)`, the
  `EndpointSkipGapRepro` in-file fixtures `Url(IDistributedApplicationBuilder)`,
  `FakePortAllocator`, `KubernetesDefinition()`, `KubernetesDevConfig()`, and the
  `EndpointMutationDetectorTests` helpers from Task 1.
- Produces, used by Task 3:
  - `public static IResourceBuilder<ServiceResource> ServiceSourcesBuilderExtensions.WithExternalHttpEndpoints(this IResourceBuilder<ServiceResource> builder)`
  - `private static bool GateEndpointCall(IResourceBuilder<ServiceResource> builder, string? capability = null)`
  - `public static IResourceBuilder<ServiceResource> OverloadProbe.CallWithExternalHttpEndpoints(IResourceBuilder<ServiceResource> service)`

- [ ] **Step 1: Add the probe entry**

In `test/Aspire.Hosting.ServiceSources.Tests/OverloadResolutionProbe.cs`, find the end of
`CallWithEndpointCallback`:

```csharp
    public static IResourceBuilder<ServiceResource> CallWithEndpointCallback(
        IResourceBuilder<ServiceResource> service, string endpointName, Action<EndpointAnnotation> callback) =>
        service.WithEndpoint(endpointName, callback);
```

and add immediately after it:

```csharp
    // No sibling shadow to disambiguate, so no [OverloadResolutionPriority] on the target: this
    // compiling and binding the non-generic shadow is the whole assertion (#359).
    public static IResourceBuilder<ServiceResource> CallWithExternalHttpEndpoints(
        IResourceBuilder<ServiceResource> service) =>
        service.WithExternalHttpEndpoints();
```

This compiles *today*, against Aspire's generic — `ServiceResource` implements
`IResourceWithEndpoints` (`ServiceResource.cs:35`), so the generic's constraint is satisfied and
there is exactly one candidate. That is what makes Step 3's failure a behavioural one rather than a
compile error, and it is why the probe is written before the shadow.

- [ ] **Step 2: Write the two failing skip tests**

In `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`, append these two tests at the
end of the class, after `DefaultNamedEndpoint_OnUrlSource_StaysGated_EvenIfServiceSourceAnnotationIsStripped`:

```csharp
    // #359: Aspire's WithExternalHttpEndpoints writes IsExternal onto the annotations that already
    // exist, so WithAnnotation never sees it and only a shadow can stop it. Asserted at call time,
    // not after BeforeStartEvent -- "never written" and "written and put back" are different
    // guarantees, and this file's subject is the first one.
    [Fact]
    public void ExternalHttpEndpoints_OnUrlSource_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithExternalHttpEndpoints(service);

        var endpoints = service.Resource.Annotations.OfType<EndpointAnnotation>().ToArray();
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint => Assert.False(endpoint.IsExternal));
        Assert.Equal(
            "Service 'inventory': skipped WithExternalHttpEndpoints because its source is 'url' — it "
            + "resolves to a fixed, already-running URL with no local process to configure. The service "
            + "is expected to be configured wherever it actually runs. To make this AppHost's "
            + "configuration and start ordering apply instead, give it a 'local' or 'container' source "
            + "in servicesources.local.json — which works only where its 'servicesources.yaml' entry "
            + "already declares that source: a 'repository' or 'repositoryRef' for 'local', a "
            + "'container' block for 'container'.",
            Assert.Single(warnings));
    }

    // The same against a real kubectl port-forward, whose EndpointAnnotation instance the facade
    // shares -- so an IsExternal write here lands on the running forward, not on a copy.
    [Fact]
    public void ExternalHttpEndpoints_OnKubernetesSource_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithExternalHttpEndpoints(service);

        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.False(endpoint.IsExternal);
        Assert.Equal(
            "Service 'orders': skipped WithExternalHttpEndpoints because its source is 'kubernetes' — "
            + "it resolves to a 'kubectl port-forward' in front of an already-running service, so the "
            + "configuration would reach kubectl rather than the service. The service is expected to be "
            + "configured wherever it actually runs. To make this AppHost's configuration and start "
            + "ordering apply instead, give it a 'local' or 'container' source in "
            + "servicesources.local.json — which works only where its 'servicesources.yaml' entry "
            + "already declares that source: a 'repository' or 'repositoryRef' for 'local', a "
            + "'container' block for 'container'.",
            Assert.Single(warnings));
    }
```

And a third, immediately after them:

```csharp
    // Two capabilities on one service, which is the only place the new label meets the shared one.
    // ServiceSourcesWarnings groups skips by (service, source), so this must stay one message.
    [Fact]
    public void ExternalHttpEndpointsAlongsideAnotherSkip_OnUrlSource_AreReportedInOneGroupedMessage()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        service.WithHttpsEndpoint(port: 9999);
        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithExternalHttpEndpoints(service);

        Assert.Contains(
            "skipped 2 calls (WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint, WithExternalHttpEndpoints)",
            Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }
```

This is the one assertion behind the spec's §2.3 claim that `ServiceSourcesWarnings` already handles
two distinct capabilities on one service "without further work" — `Describe` groups skips by
`(ServiceName, Source)` (`ServiceSourcesWarnings.cs:94-98`) and `DescribeCalls` renders several as a
count plus a tally. The order inside the parentheses is insertion order, which is why
`WithHttpsEndpoint` is called first. Without it the CHANGELOG's "still gets one grouped message"
would be an unasserted claim.

Three things these deliberately do, each of which a reviewer will otherwise ask about:

- **`Assert.Equal` on the whole sentence, not `Contains` on the method name** (spec §4.2). This is
  the "use the output" evidence for the review loop: a `Contains` passes on a label that is right
  inside a sentence that reads wrong. The em-dashes are U+2014, matching
  `ServiceSourcesWarnings.cs:303` and `OutOfBandSourceAdvice.cs:31` — copy them, do not retype them
  as hyphens.
- **The kubernetes fixture is the one already in this file** (`:104-122`), inlined exactly as the
  three existing kubernetes tests inline it. No new fixture, no extraction, no copy from
  `EndpointMutationDetectorTests`.
- **`TestHelpers.CreateBuilder`, not `CreateBuilderThatCanStart`** — this file never publishes
  `BeforeStartEvent`, and its existing tests all read `ServiceSourcesWarnings.For(builder).Messages`
  directly.

- [ ] **Step 3: Write the failing interaction test**

In `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs`, append this test at
the end of the class:

```csharp
    // Checklist item 7: one warning, not two. The shadow returns before Aspire's method runs, so the
    // detector finds nothing to revert -- structural, but the thing a reader most needs pinned.
    // Assert.Single is what fails if a second warning ever appears; a Contains would pass with two.
    [Fact]
    public async Task ExternalHttpEndpoints_OnKubernetesSource_IsSkippedAndTheDetectorAddsNothing()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithExternalHttpEndpoints(service);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);

        var warning = Assert.Single(warnings);
        Assert.Contains("skipped WithExternalHttpEndpoints", warning);
        Assert.DoesNotContain("has been put back", warning);
    }
```

This file has no `using ServiceSourcesOverloadProbe;` yet. Add it to the existing using block at the
top of `EndpointMutationDetectorTests.cs`, after `using Aspire.Hosting.ServiceSources.Sources;`:

```csharp
using ServiceSourcesOverloadProbe;
```

- [ ] **Step 4: Run all four to verify they fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~ExternalHttpEndpoints"
```

Expected: **all four FAIL on an assertion, not on a compile error.**
`OverloadProbe.CallWithExternalHttpEndpoints` currently binds Aspire's generic, so the call runs for
real. Each fails at a different point, and each is worth recognising:

- `ExternalHttpEndpoints_OnUrlSource_IsSkippedAndReported` — `Assert.NotEmpty` passes, then the
  `Assert.All(…, Assert.False)` fails with `IsExternal` observed as `true`;
- `ExternalHttpEndpoints_OnKubernetesSource_IsSkippedAndReported` — `Assert.False(endpoint.IsExternal)`
  fails the same way;
- `ExternalHttpEndpointsAlongsideAnotherSkip_OnUrlSource_AreReportedInOneGroupedMessage` — the
  ungated call records no skip at all, so the single message reads `skipped
  WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint` with no tally and the `Assert.Contains` fails;
- `ExternalHttpEndpoints_OnKubernetesSource_IsSkippedAndTheDetectorAddsNothing` — the call-time
  `Assert.False(...)` fails, before `BeforeStartEvent` is ever published.

If instead any of them fails to **compile** with CS0121, stop and report — that means a second
applicable `WithExternalHttpEndpoints` candidate exists that spec §4.1 did not account for, and the
no-attribute decision has to be revisited by a human rather than patched around with
`[OverloadResolutionPriority]`.

- [ ] **Step 5: Give `GateEndpointCall` an optional capability label**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, find the closing lines of
`GateEndpointCall`'s XML doc and its signature and body (`:506-523`):

```csharp
    /// scan survives only as a fallback for a hypothetical non-<see cref="ServiceResourceBuilder"/>
    /// <see cref="IResourceBuilder{ServiceResource}"/> — no such builder reaches <see cref="AddService"/>
    /// today.
    /// </summary>
    private static bool GateEndpointCall(IResourceBuilder<ServiceResource> builder)
    {
        var source = builder is ServiceResourceBuilder serviceBuilder
            ? serviceBuilder.Source
            : builder.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault()?.Source;
        if (source is null || !Reachability.IsUnreachable(typeof(EndpointAnnotation), source))
        {
            return false;
        }

        ServiceSourcesWarnings.For(builder.ApplicationBuilder)
            .AddSkip(builder.Resource.Name, source, Reachability.CapabilityLabel(typeof(EndpointAnnotation)));
        return true;
    }
```

and replace with:

```csharp
    /// scan survives only as a fallback for a hypothetical non-<see cref="ServiceResourceBuilder"/>
    /// <see cref="IResourceBuilder{ServiceResource}"/> — no such builder reaches <see cref="AddService"/>
    /// today.
    /// </summary>
    /// <param name="builder">The service builder the endpoint call was written against.</param>
    /// <param name="capability">
    /// What to name in the skip warning, for a surface the shared label would misdescribe. Defaults
    /// to <see cref="Reachability.CapabilityLabel"/>'s three-method label, which the overloads that
    /// forward to one another are all genuinely covered by.
    /// </param>
    private static bool GateEndpointCall(IResourceBuilder<ServiceResource> builder, string? capability = null)
    {
        var source = builder is ServiceResourceBuilder serviceBuilder
            ? serviceBuilder.Source
            : builder.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault()?.Source;
        if (source is null || !Reachability.IsUnreachable(typeof(EndpointAnnotation), source))
        {
            return false;
        }

        ServiceSourcesWarnings.For(builder.ApplicationBuilder)
            .AddSkip(
                builder.Resource.Name,
                source,
                capability ?? Reachability.CapabilityLabel(typeof(EndpointAnnotation)));
        return true;
    }
```

**Do not touch any of the nine existing `GateEndpointCall(builder)` call sites** (lines
271/293/313/335/355/374/401/422/438). The default is what keeps them emitting today's text.

- [ ] **Step 6: Add the shadow**

In the same file, find the end of `WithHttpsEndpoint` (`:298-299`):

```csharp
        return Aspire.Hosting.ResourceBuilderExtensions.WithHttpsEndpoint(builder, port, targetPort, name, env, isProxied);
    }
```

and add immediately after it:

```csharp
    /// <summary>
    /// Shadows Aspire's own <c>WithExternalHttpEndpoints&lt;T&gt;</c>. Unlike every other endpoint
    /// shadow in this file it is scoped to no endpoint name: Aspire's method walks the resource's
    /// <em>existing</em> <see cref="EndpointAnnotation"/> instances and writes
    /// <c>IsExternal = true</c> on each <c>http</c>/<c>https</c> one, never calling
    /// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/> — so no gate saw it, and on a
    /// <c>kubernetes</c> service the write landed on the real <c>kubectl port-forward</c>'s own
    /// endpoint (#359). The gate is unaffected by the shape difference: it reads only the builder's
    /// source, and <see cref="Reachability.IsUnreachable"/> for an <see cref="EndpointAnnotation"/>
    /// is unconditionally true for both out-of-band sources, so "skip all of them" and "skip this
    /// one" are the same answer. Non-generic on the concrete <see cref="ServiceResource"/> receiver,
    /// which C# prefers over Aspire's generic — no AppHost-visible signature change.
    /// </summary>
    public static IResourceBuilder<ServiceResource> WithExternalHttpEndpoints(
        this IResourceBuilder<ServiceResource> builder)
    {
        // Its own label: the shared one names three methods this call forwards to none of.
        if (GateEndpointCall(builder, "WithExternalHttpEndpoints"))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithExternalHttpEndpoints(builder);
    }
```

The `[AspireExport]` attribute is intentionally absent — Task 3.

- [ ] **Step 7: Run the four tests to verify they pass**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~ExternalHttpEndpoints"
```

Expected: **all four PASS.**

If either `EndpointSkipGapRepro` test fails on the `Assert.Equal` while its `Assert.False` passes,
the gate is working and only the reconstructed sentence in spec §2.4 is off. **Paste the real
assertion-failure output into the ticket notes file**
(`C:\Source\aspire-servicesources\.git\worktrees\ticket-359-82c6f5\ticket-notes.md`, under "Build /
test output log"), correct the expected string in the test to the observed one, and note the
correction — spec §7 anticipates exactly this and authorises it. Do **not** weaken the assertion to a
`Contains` to make it pass.

- [ ] **Step 8: Correct the production XML doc**

The class remark on `EndpointMutationDetector` now ships a claim that is false. In
`src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs`, find lines 11–17:

```csharp
/// <remarks>
/// The call-site gate (<see cref="Reachability"/>, <c>GateEndpointCall</c>,
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>) can only stop calls that reach
/// this package. A guest-language AppHost invokes Aspire's own endpoint-callback capabilities
/// directly, and Aspire's <c>WithExternalHttpEndpoints</c> writes existing annotations in place, so
/// neither is interceptable at all. This watches the state instead of the call, which is why it
/// covers both without naming either.
```

and replace with:

```csharp
/// <remarks>
/// The call-site gate (<see cref="Reachability"/>, <c>GateEndpointCall</c>,
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>) owns every mutation surface
/// this package can name and bind from C#, and stops those before they happen. A guest-language
/// AppHost invokes Aspire's own endpoint-callback capabilities directly, and their shape is
/// <c>internal</c> to Aspire, so no C# shadow can ever project them. This watches the state instead
/// of the call, which is why it covers them — and any in-place writer nobody has enumerated yet —
/// without naming any of them. A surface the gate covers leaves no state change here to find, so the
/// two cannot double-report.
```

- [ ] **Step 9: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across net8.0/net9.0/net10.0 **except**
`CatalogExportsTests.ExportedIds_MatchTheKnownSurface`, which must still be **green** here — the
shadow carries no `[AspireExport]` yet, so the exported set has not moved. If it is red at this
point, `[AspireExport]` was added early; remove it and leave it to Task 3.

Pay attention to `EndpointSkipGapRepro` and `ServiceSourcesBuilderExtensionsTests` as a whole: every
existing assertion on the literal `WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint` must still pass.
A failure there means a call site was disturbed, which is a stop-and-report, not a test to adjust.

- [ ] **Step 10: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs \
        test/Aspire.Hosting.ServiceSources.Tests/OverloadResolutionProbe.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Gate WithExternalHttpEndpoints at the call site (#359)

Aspire's method walks the annotations that already exist and writes
IsExternal on each http/https one, so WithAnnotation never sees it and no
gate did either. On a kubernetes service the facade shares those
instances with the real kubectl port-forward, so the write reached the
running forward.

A non-generic shadow on the concrete receiver routes the call through the
existing gate, which returns before Aspire's method runs -- so the field
is never written, not even transiently, which is what the two new tests
assert at call time rather than after BeforeStartEvent.

GateEndpointCall takes an optional capability label, defaulted, so the
nine call sites that share the three-method label keep emitting it
unchanged. This surface names itself instead: it forwards to none of
those three, takes no endpoint name, and writes one field across every
endpoint, so the shared label would have named three methods the
developer did not call.

No [OverloadResolutionPriority]: unlike WithCommand this has no sibling
shadow to be ambiguous with, and the probe in a neutral namespace is what
proves the non-generic overload wins -- a call inside the test namespace
stops the lookup one level too early to prove anything.

The detector's class remark said this method was uninterceptable. It was
not; the audit that concluded otherwise was reading a method-name sweep
rather than the binding rules. Corrected to state the division of labour
instead: the gate owns what C# can bind, the detector owns the
guest-language callbacks and whatever has not been enumerated yet, and a
gated call leaves no state change for the detector to find.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `[AspireExport]` and the catalog surface

Acceptance criterion 5. Aspire's own `WithExternalHttpEndpoints` is projected to guest languages;
leaving the shadow unexported would mean a TypeScript AppHost's `withExternalHttpEndpoints` kept
binding Aspire's ungated method while the C# one was gated — the exact split the other nine shadows
avoid.

`CatalogExportsTests.ExportedIds_MatchTheKnownSurface` asserts set equality against a hand-maintained
list, which is the repo's intended mechanism for recording a deliberate surface addition: adding the
id first is the red leg, and the attribute is what turns it green.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs` (`:90-122`)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` (one attribute)

**Interfaces:**
- Consumes: Task 2's `WithExternalHttpEndpoints`. No signature change.
- Produces: nothing new.

- [ ] **Step 1: Add the expected id**

In `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`, find the second line of
the `expected` array (`:93`):

```csharp
            "addServiceCatalog", "withEndpoint", "withHttpEndpoint", "withHttpsEndpoint", "withCommand",
```

and replace with:

```csharp
            "addServiceCatalog", "withEndpoint", "withHttpEndpoint", "withHttpsEndpoint", "withCommand",
            "withExternalHttpEndpoints",
```

Flat and camelCased with no type prefix, the same derivation as the `withEndpoint` /
`withHttpEndpoint` / `withHttpsEndpoint` entries beside it: the shadow is a static extension method
with no explicit `Id`.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~ExportedIds_MatchTheKnownSurface"
```

Expected: **FAIL.** The assertion is `Assert.Equal` over two ordered sequences, so the failure shows
`withExternalHttpEndpoints` present in `expected` and absent from `ids` — the shadow carries no
`[AspireExport]` yet.

- [ ] **Step 3: Add the attribute**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, find the declaration added
in Task 2:

```csharp
    public static IResourceBuilder<ServiceResource> WithExternalHttpEndpoints(
        this IResourceBuilder<ServiceResource> builder)
```

and replace with:

```csharp
    [AspireExport]
    public static IResourceBuilder<ServiceResource> WithExternalHttpEndpoints(
        this IResourceBuilder<ServiceResource> builder)
```

The attribute goes between the XML doc and the signature, as on `WithHttpEndpoint` (`:265`) and
`WithHttpsEndpoint` (`:287`).

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~CatalogExportsTests"
```

Expected: **every test in the fixture PASSES**, not just the one — the file's other guards
(duplicate-id, naming-shape) run over the widened set too, and a failure in one of those means the
derived id is not what Step 1 predicted. If one does fail, report the id it actually derived rather
than editing the guard.

- [ ] **Step 5: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across net8.0/net9.0/net10.0.

**This is the point where the TypeScript export-surface leg would earn its keep, and it cannot run
here** — no `node`, no `npm`, no pinned Aspire CLI 13.5.3. Do not imply it passed. Its risk is
bounded (spec §4.3): whether a guest-language `withExternalHttpEndpoints` binds this package's export
or Aspire's is the identical mechanism already governing the three endpoint exports that ship today,
and if it somehow bound Aspire's, the outcome would be #372's detector reverting and reporting the
mutation — today's shipped behaviour, not a regression.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs
git commit -m "$(cat <<'EOF'
Export the WithExternalHttpEndpoints shadow to guest languages (#359)

Aspire's own method is projected, so an unexported shadow would leave a
TypeScript AppHost binding the ungated one while C# was gated -- the
split the other endpoint shadows exist to avoid.

The exported-id list is hand-maintained by design, so a new
[AspireExport] fails it until the addition is recorded deliberately;
recording it is that mechanism working, not a test being adjusted.

The typecheck over the TypeScript samples is the leg that would catch a
mistake here and it cannot run on this machine -- left to CI, and named
as not run.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: CHANGELOG reconciliation and full verification

Acceptance criterion 9, plus the sweep that has to happen before the PR.

Two edits, both in `## [Unreleased]` and both required by spec §4.4. The new entry goes under
`### Fixed`, because the section is reserved for bugs in already-*released* behaviour and this
qualifies: `WithExternalHttpEndpoints` silently mutating a `url`/`kubernetes` service shipped in
v0.5.1 and earlier. #372's revert behaviour is itself unreleased, so what a v0.5.2 reader experiences
is not "reverted became skipped" but "a call that used to apply silently now skips with a warning".

The stale claim at `:159-161` is **edited, not deleted**. The "covers" half stays true for anything
that reaches the detector, and it is the sentence carrying §3.4's whole point — that a state-keyed
detector catches an unenumerated surface. What goes is "that issue stays open and separately owned"
and the implication that the detector is the only thing covering this method. Editing an
already-merged entry is correct here because both land inside `## [Unreleased]`, above the last tag
`v0.5.1`, so no reader of a released changelog ever sees the intermediate state.

**Files:**
- Modify: `CHANGELOG.md` (one new `### Fixed` entry; one in-place edit at `:159-161`)

**Interfaces:**
- Consumes: nothing. This task edits one file and re-verifies everything Tasks 1–3 built.
- Produces: nothing.

- [ ] **Step 1: Confirm the section choice still holds**

```bash
git fetch origin --tags
git tag --sort=-v:refname | head -3
```

Expected: `v0.5.1` is still the newest tag. If a newer tag has appeared, **stop and report** rather
than writing the entry — whether #372's revert behaviour has now shipped changes what a reader
experiences and therefore which section this belongs in, and that is a call for the human.

- [ ] **Step 2: Edit #372's stale claim**

In `CHANGELOG.md`, find lines 159–161:

```markdown
  This measures the state rather than the call, so it also **covers**
  `WithExternalHttpEndpoints` ([#359]), which sets `IsExternal` on existing endpoints directly from
  C# and was likewise unreported; that issue stays open and separately owned. Nothing changes for a
```

and replace with:

```markdown
  This measures the state rather than the call, so it also caught `WithExternalHttpEndpoints`
  ([#359]), which sets `IsExternal` on existing endpoints directly from C# and was likewise
  unreported — an in-place writer no method-name audit had found. That call is now gated at the call
  site instead, and is the entry below; the detector stays the backstop for the guest-language
  callbacks and for whatever is found next. Nothing changes for a
```

- [ ] **Step 3: Add the `### Fixed` entry**

In the same file, find the `### Fixed` heading under `## [Unreleased]` and the first line of the
entry that follows it (`:193-195`):

```markdown
### Fixed

- **A service name can no longer forge a second log entry in four out-of-band messages** ([#372]).
```

and replace with:

```markdown
### Fixed

- **`WithExternalHttpEndpoints` on a `url` or `kubernetes` service is now skipped and reported
  instead of applied silently** ([#359]). Aspire's method does not add an annotation — it walks the
  ones the service already has and writes `IsExternal` on each `http`/`https` one — so it went
  straight past the check every other endpoint call in this package goes through, and neither warned
  nor appeared in the skipped-calls tally. For a `kubernetes` service that write landed on the real
  `kubectl port-forward`'s own endpoint, publishing a port-forward to the world on the strength of a
  line the AppHost author wrote about the service behind it. The call is now intercepted the same way
  `WithHttpEndpoint` and `WithHttpsEndpoint` already are: nothing is written, and the service's
  existing skip message names this call by name — `Service 'orders': skipped
  WithExternalHttpEndpoints because its source is 'kubernetes' — …` — rather than naming the three
  endpoint methods the shared label covers, which this one forwards to none of. A service that calls
  both surfaces still gets one grouped message. Nothing changes for a `local` or `container` service,
  where the call applies exactly as before. The detector added in this release ([#372]) reported this
  after the fact; with the call skipped there is no longer any change for it to find, so there is one
  warning rather than two, and the detector remains the backstop for the guest-language endpoint
  callbacks, which no C# shadow can reach.

- **A service name can no longer forge a second log entry in four out-of-band messages** ([#372]).
```

- [ ] **Step 4: Check the references resolve and nothing was duplicated**

```bash
grep -c '^\[#359\]: ' CHANGELOG.md
grep -n 'stays open and separately owned' CHANGELOG.md
grep -n 'WithExternalHttpEndpoints' CHANGELOG.md
```

Expected: `1` for the first — the definition at `:1732` already existed, so a `2` means a duplicate
was added and must be removed (an undefined reference renders as literal `([#359])` text; a
duplicate one is a lint failure in some markdown tooling). **No output** for the second, which is the
stale claim gone. For the third, hits in exactly two places: the edited #372 sentence, and the new
`### Fixed` entry. A hit anywhere else means an earlier entry also makes a claim about this method
that Step 2 did not reconcile.

- [ ] **Step 5: Run every leg that can run on this machine**

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results
dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package
```

Expected: restore clean; build 0 errors / 0 warnings; every test green across net8.0/net9.0/net10.0;
pack produces a `.nupkg` with no warnings.

Paste the real output of each into the ticket notes file
(`C:\Source\aspire-servicesources\.git\worktrees\ticket-359-82c6f5\ticket-notes.md`, under "Build /
test output log"). Per superpowers:verification-before-completion, no green claim without the output
that proves it. **The Write tool refuses paths under `.git/worktrees/<name>/`** — write the text to a
scratchpad file and `cp` it into place, or append with a heredoc.

**Name these as NOT RUN, with the reason — never imply they passed:**

- the **TypeScript export-surface typecheck** over `samples/DemoAppHostTypeScript` and
  `samples/DemoAppHostTypeScriptCodeCatalog` — no `node`, no `npm`, no pinned Aspire CLI 13.5.3.
  Worth naming twice on this ticket: Task 3 adds an `[AspireExport]`, so this is the one leg that
  actually exercises the change's guest-language half;
- the container-source, config-layers and local-source smoke tests — Linux-targeted bash scripts
  needing Docker and a live AppHost;
- the `verify-invariants` python checks — no `python`/`python3` on PATH;
- the Aspire floor/latest version matrix and the .NET 11 preview build — CI-scheduled only, and the
  matrix is path-filtered to version-declaring files, which this diff does not touch.

- [ ] **Step 6: Commit**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
Record the WithExternalHttpEndpoints gate in the changelog (#359)

Fixed, not Added: the preamble reserves Fixed for bugs in released
behaviour, and this method mutating an out-of-band service unreported
shipped in v0.5.1 and earlier. The detector's revert is itself
unreleased, so what a reader of the next release experiences is a call
that used to apply silently now skipping with a warning.

The earlier entry's claim that this issue stays open and separately owned
is edited rather than deleted -- the detector really did catch an
in-place writer no method-name audit had found, which is the point of
measuring state, and only the ownership half is now wrong.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Notes

**Spec coverage.** §1.1 (the method, stated once) → Task 2 Step 6, verbatim including the
fully-qualified delegate and the shape-difference argument, which becomes the XML doc. §2.1/§2.2 (the
label problem and the rejected widening) → nothing is built for the rejected option; the reasoning
becomes Task 2 Step 5's `<param>` doc and Step 6's one-line WHY comment, and the constraint "do not
touch `CapabilityLabel`" is a Global Constraint. §2.3 (the optional parameter, blast radius zero, and the
grouped two-capability message) → Task 2 Step 5, with the nine untouched call sites named by line
number, and Task 2 Step 2's third test for the grouping. §2.4 (the exact literals) →
Task 2 Step 2, asserted with `Assert.Equal` on the whole sentence, with Step 7's correction procedure
for the case where the reconstruction is off. §2.5 (adds nothing to the attack surface) → not code;
its one operative consequence — that the label is a compile-time literal and needs no escaping — is
why no sanitiser call appears anywhere in Task 2. §3.1 (exactly one warning, structurally) → Task 2
Step 3's `Assert.Single` plus `Assert.DoesNotContain("has been put back")`. §3.2 (retarget, do not
delete; split in two) → Task 1 Steps 1–2 and Task 2 Step 3. §3.3 (both false claims) → Task 1 Step 1
replaces the test comment, Task 2 Step 8 the production XML doc. §3.4 (the division of labour) →
Task 2 Step 8's replacement text is that paragraph in the doc's own words. §4.1 (no attribute) → a
Global Constraint plus Task 2 Steps 1 and 4, where CS0121 is a stop-and-report rather than a cue to
add it. §4.2 (the test plan) → Tasks 1, 2 and 3, one file per bullet. §4.3 (the leg that cannot run)
→ a Global Constraint, Task 3 Step 5 and Task 4 Step 5's not-run list. §4.4 (CHANGELOG) → Task 4
Steps 2–3, `### Fixed`, no new link definition. §5 (deliberately not done) → nothing in any task
changes `CapabilityLabel`, the detector's behaviour, the other nine labels, the samples, or files any
upstream ask. §6's nine criteria → 1/4 Task 2 Step 6, 2 Task 2 Step 2's call-time assertions, 3 Task
2 Steps 2 and 5, 5 Task 3, 6 Task 2 Step 2, 7 Task 1 and Task 2 Steps 3 and 8, 8 Task 1 Step 2, 9
Task 4.

**One correction to the spec, made rather than inherited.** Spec §4.2 and §7 both state that
`EndpointSkipGapRepro` "has no `kubernetes` fixture today" and leave share-or-duplicate as an
implementation call. It does have one: `FakePortAllocator` at `:104-111`, `KubernetesDefinition()` at
`:113-119` and `KubernetesDevConfig()` at `:121-122`, used by three existing tests in that file. The
question the spec left open therefore does not arise, and the answer is neither of its two options —
reuse what is there. The File Structure section says so explicitly, and Task 2 Step 2's test inlines
`new KubernetesSource(new FakePortAllocator(54321)).Resolve(...)` exactly as its three neighbours do
rather than introducing a helper, because extracting one would mean rewriting tests this ticket is
not about.

**What the plan's own review round changed.** One gap and one unverified assumption, both closed
before this plan was committed.

- **The grouped two-capability message was claimed and untested.** Spec §2.3 states that a service
  calling both surfaces gets one message reading `skipped 2 calls (…, WithExternalHttpEndpoints)`,
  and Task 4's CHANGELOG entry repeats it to the reader — but §4.2's test plan listed no test for it,
  so the claim would have shipped unasserted. It is also the one place the new label meets the shared
  one, which is exactly where a per-surface label could go wrong. Added as Task 2 Step 2's third
  test, with a real red leg (before the shadow the call records no skip, so there is no tally).
- **The Task 2/Task 3 split rests on `ServiceSourcesBuilderExtensions` not exporting its methods
  wholesale, and that was checked rather than assumed.** The class carries no attribute at all
  (`:13`), and `CatalogExportsTests.ExportedMethods` (`:133-168`) yields a static method only when it
  carries `[AspireExport]` directly — the `ExposeMethods` branch is instance-only. So a shadow
  without the attribute genuinely leaves `ExportedIds_MatchTheKnownSurface` green in Task 2 and
  genuinely turns it red in Task 3 Step 2. Had the class been `[AspireExport(ExposeMethods = true)]`
  like `ServiceCatalogBuilder`, the split would have been unworkable and both tasks would have had to
  merge. Task 2 Step 9 states the expectation explicitly so a wrong split fails loudly.

**Task boundaries, and why the order is what it is.** Task 1 must precede Task 2: the shadow makes
`ExternalHttpEndpoints_OnKubernetesSource_IsRevertedAndReported` fail outright, so retargeting it
afterwards would mean Task 2 handing over a red suite and its own red→green claim being unreadable.
Task 1 is therefore the one task with no red leg, which is stated in its preamble rather than
disguised. Task 3 is separate from Task 2 because the export is a genuinely separate decision — a
reviewer could accept the C# gate and question the guest-language projection, whose verifying leg
cannot run here — and because adding the id before the attribute is a real red→green pair that would
be lost if the attribute rode along with the shadow. Task 4 is documentation plus the full sweep.
Every task ends on a green suite; no task leaves a known-red test for a later one.

**Placeholder scan.** No TBD/TODO, no "add appropriate error handling", no "similar to Task N". Every
code step carries literal content; every run step carries a literal command and a stated expectation.
The plan's four conditionals — Task 1 Step 3's harness failure, Task 2 Step 4's CS0121, Task 2 Step
7's string mismatch, Task 3 Step 4's derived-id mismatch, Task 4 Step 1's newer tag — each name a
concrete check, a concrete expected output, and a concrete action, which is stop and report in four
of the five and a bounded, spec-authorised correction in the fifth.

**Type consistency.** `GateEndpointCall(IResourceBuilder<ServiceResource> builder, string? capability = null)`
is spelled identically in Task 2's Interfaces block, its definition, and the one new call site;
`WithExternalHttpEndpoints(this IResourceBuilder<ServiceResource>)` is spelled identically in Task
2's Interfaces block, Step 6's definition, and Task 3's attribute edit. `OverloadProbe.CallWithExternalHttpEndpoints(IResourceBuilder<ServiceResource>)`
takes one parameter at its definition and at all three call sites.
`GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("IsExternal", true))` matches the
harness's `(IResourceBuilder<ServiceResource>, string, params (string Property, object? Value)[])`
signature and the `("Property", value)` tuple shape its existing callers use. The
`EndpointMutationDetectorTests` helpers `Builder`/`Url`/`Kubernetes`/`Endpoints` and the
`EndpointSkipGapRepro` helpers `Url`/`FakePortAllocator`/`KubernetesDefinition`/`KubernetesDevConfig`
are all pre-existing and used with their current signatures; nothing in this plan redefines one. The
capability string `"WithExternalHttpEndpoints"` is written once in production and appears in the
three test assertions and the CHANGELOG entry in exactly that spelling.
