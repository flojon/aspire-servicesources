# Gate the raw WithEndpoint overloads (#335) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #335 by shadowing the seven Aspire `WithEndpoint`/`WithHttpEndpoint`/`WithHttpsEndpoint`
overloads left unshadowed by #334's fix (PR #339), so an explicit argument to any of them can no
longer silently mutate a shared `EndpointAnnotation` on an out-of-band (`url`/`kubernetes`)
`ServiceResource` with no `Reachability` gate and no warning.

**Architecture:** Seven new non-generic extension-method shadows on `IResourceBuilder<ServiceResource>`
in `ServiceSourcesBuilderExtensions`, each reusing the existing private `GateEndpointCall` helper
unchanged and delegating to Aspire's matching overload by fully-qualified static call — the exact
mechanism PR #339 already established for `WithHttpEndpoint`/`WithHttpsEndpoint`'s primary overload.
`Reachability.CapabilityLabel`'s `EndpointAnnotation` case widens from a two-method to a three-method
label so a raw-`WithEndpoint` skip names itself accurately.

**Tech Stack:** C# / .NET (net8.0, net9.0, net10.0 multi-target), xUnit, Aspire.Hosting 13.5.2 (pinned
floor, `Directory.Build.props:74`).

**Spec:** [docs/superpowers/specs/2026-09-15-335-raw-withendpoint-gate-design.md](../specs/2026-09-15-335-raw-withendpoint-gate-design.md)

## Global Constraints

- Pinned Aspire floor: `Aspire.Hosting` 13.5.2 (`Directory.Build.props:74`). Every claim about Aspire's
  decompiled behavior in this plan was verified against
  `~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll` with `ilspycmd 11.0.0.9375`.
- Every new shadow delegates to Aspire's implementation by **fully-qualified static call**
  (`Aspire.Hosting.ResourceBuilderExtensions.XXX(...)`) — **never** extension-method syntax on
  `builder` (e.g. `builder.WithEndpoint(...)`), which would recurse into the shadow itself.
- `GateEndpointCall` (already in `ServiceSourcesBuilderExtensions.cs`) is reused **unchanged** by every
  new shadow. Do not add parameters to it or branch its logic by method name.
- The one new `[AspireExport]`-attributed shadow (the primary `WithEndpoint`, Task 1) must build with
  zero `ASPIREEXPORT008` warnings under `dotnet build -c Release -warnaserror`, and the
  `typecheck-typescript` CI leg (both matrix samples) must pass locally before that task is considered
  done — this is the one piece of new exported surface in the whole plan, so it is the one piece this
  plan cannot skip verifying end-to-end. All six remaining shadows carry `[AspireExportIgnore]`
  (matching Aspire's own attribute on each), so this requirement applies to Task 1 only.
- No `CHANGELOG.md` entry: the gap predates no release (introduced by #326, still `[Unreleased]`;
  #334/#339's own fix is also still `[Unreleased]`).
- Comment style per this repo's conventions: short, WHY-only, no implementation narrative, no PR/issue
  references inside code comments (issue numbers belong in commit messages and this plan, not in the
  shipped comment text) — matching the existing style already in `ServiceSourcesBuilderExtensions.cs`
  and `ServiceResourceBuilder.cs`.
- Run tests with `dotnet test -c Release` (all three target frameworks); run the full build with
  `dotnet build -c Release -warnaserror` before any task's final commit.

---

## File Structure

- **Modify** `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` — add a
  `using System.Net.Sockets;` (for `ProtocolType`) and seven new shadow methods, inserted directly
  after the existing `WithHttpsEndpoint` shadow and before the `GateEndpointCall` private helper's
  doc comment.
- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs` — widen
  `Reachability.CapabilityLabel`'s `EndpointAnnotation` case string (Task 1); polish its remarks and
  `ResolvedService.cs`'s bridge comment once every shadow exists (Task 5).
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs` — add
  one test per shadow proving the url-source skip-and-report behavior, plus reachable-source sanity
  checks for the two overloads an AppHost author would plausibly write by hand (the primary
  `WithEndpoint` and the callback overload).
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs` — add two
  production-shaped kubernetes-source repro tests (raw numeric `WithEndpoint` with an explicit `port`;
  the callback overload), mirroring the file's existing
  `DefaultNamedEndpoint_OnKubernetesSource_WithArguments_DoesNotChangeThePortForwardsEndpoint` pattern.

No new files. The design doc's test-plan wording said "extend `EndpointSkipGapRepro.cs`" for
everything; this plan follows the split the test suite already uses instead — cheap facade-level unit
tests (one per shadow, proving the mechanism) live in `ServiceSourcesBuilderExtensionsTests.cs`
exactly like the two existing `WithHttpsEndpoint`/`WithHttpEndpoint` tests there, while the
heavier, real-`KubernetesSource`-backed "does this actually protect a live kubectl port-forward"
scenarios live in `EndpointSkipGapRepro.cs` alongside the ones #334 already put there. This achieves
the same coverage the spec asks for without duplicating either file's existing organizational logic.

---

## Task 1: Shadow the primary raw `WithEndpoint` overload, widen `CapabilityLabel`

This is the one task in this plan touching new `[AspireExport]` surface — read the Global Constraints
section above before starting.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs:88` (the
  `CapabilityLabel` switch expression)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`

**Interfaces:**
- Consumes: the existing private `GateEndpointCall(IResourceBuilder<ServiceResource> builder) : bool`
  in `ServiceSourcesBuilderExtensions.cs` (unchanged); `Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint<T>`
  (Aspire's own primary overload).
- Produces: `public static IResourceBuilder<ServiceResource> WithEndpoint(this IResourceBuilder<ServiceResource> builder, int? port = null, int? targetPort = null, string? scheme = null, string? name = null, string? env = null, bool? isProxied = null, bool? isExternal = null, ProtocolType? protocol = null)`
  — later tasks in this plan add sibling overloads of the same name, never call this one directly.

- [ ] **Step 1: Write the failing tests**

In `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`, add these two
tests inside the `ServiceSourcesBuilderExtensionsTests` class, after the existing
`WithHttpsEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint` test and before the
`FakeNonServiceResourceBuilder` class:

```csharp
[Fact]
public void WithEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

    // A partial call omitting every optional argument but port/scheme — the shape that reaches
    // the primary WithEndpoint<T> overload rather than any of its binary-compat shims, all of
    // which require every parameter and so are inapplicable to a call this short.
    var result = service.WithEndpoint(port: 9999, scheme: "https");

    Assert.Same(service, result);
    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}

[Fact]
public void WithEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint()
{
    var builder = Builder();
    var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
    var service = ResolvedService.Bridge(real, "orders", "container");

    var result = service.WithEndpoint(port: 9999, scheme: "https", name: "probe");

    var endpoint = Assert.Single(
        result.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
    Assert.Equal(9999, endpoint.Port);
    Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
}
```

In `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`, add this test inside the
`EndpointSkipGapRepro` class, immediately after
`DefaultNamedEndpoint_OnKubernetesSource_WithArguments_DoesNotChangeThePortForwardsEndpoint`:

```csharp
// #335's scenario reached through the raw overload directly, rather than through
// WithHttpsEndpoint (already proven gated by #334's own fix) — the primary WithEndpoint<T>
// overload is what WithHttpsEndpoint forwards to internally, but an AppHost author can call it
// directly too, and #334's shadow never touched this call surface.
[Fact]
public void DefaultNamedEndpoint_OnKubernetesSource_ViaRawWithEndpoint_DoesNotChangeThePortForwardsEndpoint()
{
    var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
    var service = new KubernetesSource(new FakePortAllocator(54321))
        .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

    var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
    var beforePort = before.Port;

    service.WithEndpoint(port: 9999, scheme: "https");

    var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
    var warnings = ServiceSourcesWarnings.For(builder).Messages;

    Assert.Same(before, after);
    Assert.NotEqual(9999, beforePort);
    Assert.Equal(beforePort, after.Port);
    Assert.True(
        warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
        $"endpoint port after={after.Port}; warnings={warnings.Count}; "
        + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported|FullyQualifiedName~WithEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint|FullyQualifiedName~DefaultNamedEndpoint_OnKubernetesSource_ViaRawWithEndpoint_DoesNotChangeThePortForwardsEndpoint" -f net8.0`

Expected: the two url/kubernetes-source tests **FAIL** — today, with no shadow, `service.WithEndpoint(port: 9999, scheme: "https")` binds straight to Aspire's own generic `WithEndpoint<T>` (since `ServiceResource` already implements `IResourceWithEndpoints`), takes the ungated update branch, and actually changes the port to `9999` with zero warnings recorded — the exact bug #335 reports. The reachable-source test should already **PASS** (nothing is gating a `container` source; this test exists to catch a future regression, not today's bug).

- [ ] **Step 3: Add the `using` and the shadow method**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, add the missing `using` at
the top of the file (after the existing `using System.Reflection;`):

```csharp
using System.Reflection;
using System.Net.Sockets;
using Aspire.Hosting;
```

Then insert this method immediately after the closing brace of the existing `WithHttpsEndpoint`
shadow (the one ending `return Aspire.Hosting.ResourceBuilderExtensions.WithHttpsEndpoint(builder, port, targetPort, name, env, isProxied);` then `}`), and before the `GateEndpointCall` doc comment
(`/// <summary>\n/// Whether an endpoint call against...`):

```csharp
    /// <summary>
    /// Shadows Aspire's own generic <c>WithEndpoint&lt;T&gt;</c> — the overload
    /// <see cref="WithHttpEndpoint"/>/<see cref="WithHttpsEndpoint"/> themselves forward to. Same
    /// update-branch gap as those two (#334), same shadow-and-gate fix, for the call surface an
    /// AppHost author reaches by calling <c>WithEndpoint</c> directly instead.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<ServiceResource> WithEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port = null, int? targetPort = null, string? scheme = null, [EndpointName] string? name = null,
        string? env = null, bool? isProxied = null, bool? isExternal = null, ProtocolType? protocol = null)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(
            builder, port, targetPort, scheme, name, env, isProxied, isExternal, protocol);
    }

```

- [ ] **Step 4: Widen `Reachability.CapabilityLabel`**

In `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs`, change:

```csharp
        nameof(EndpointAnnotation) => "WithHttpEndpoint/WithHttpsEndpoint",
```

to:

```csharp
        nameof(EndpointAnnotation) => "WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint",
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported|FullyQualifiedName~WithEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint|FullyQualifiedName~DefaultNamedEndpoint_OnKubernetesSource_ViaRawWithEndpoint_DoesNotChangeThePortForwardsEndpoint" -f net8.0`

Expected: all three **PASS**.

- [ ] **Step 6: Run the full test suite and the warnaserror build**

Run: `dotnet build -c Release -warnaserror`
Expected: 0 errors, 0 warnings — in particular, no `ASPIREEXPORT008` for the new `[AspireExport]`
shadow just added.

Run: `dotnet test -c Release`
Expected: every existing test still passes across net8.0/net9.0/net10.0, plus the three new ones.

- [ ] **Step 7: Verify guest-language projection (`typecheck-typescript` reproduction)**

This is the one new `[AspireExport]` surface in this whole plan — reproduce the CI leg locally rather
than assuming it's fine:

```bash
dotnet tool install --tool-path /tmp/aspire-cli aspire.cli --version 13.5.3 2>/dev/null || true
export PATH="/tmp/aspire-cli:$PATH"
cd samples/DemoAppHostTypeScript && rm -rf .aspire && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json && cd -
cd samples/DemoAppHostTypeScriptCodeCatalog && rm -rf .aspire && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json && cd -
```

Expected: both `tsc` invocations exit 0. If either fails, the new `WithEndpoint` overload is emitting
TypeScript the generator or `tsc` rejects — stop and fix the signature/attribute before proceeding to
Task 2 (every later task adds more `[AspireExportIgnore]`-only overloads, which cannot regress this,
so this is the one point in the plan where this check matters).

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs \
        test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs
git commit -m "$(cat <<'EOF'
Shadow the primary raw WithEndpoint overload (#335)

WithHttpEndpoint/WithHttpsEndpoint forward to Aspire's generic
WithEndpoint<T>, which #334 (PR #339) never shadowed directly -- an
AppHost author calling WithEndpoint itself still hit the ungated
update branch. Widen Reachability.CapabilityLabel's EndpointAnnotation
case to name all three methods now that a skip can come from any of
them.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Shadow the three `WithEndpoint<T>` binary-compat shims

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`

**Interfaces:**
- Consumes: `GateEndpointCall` (unchanged); the primary `WithEndpoint` shadow from Task 1 is a sibling
  overload, never called directly by these three.
- Produces: three more `WithEndpoint` overloads on `IResourceBuilder<ServiceResource>`:
  - `WithEndpoint(builder, int? port, int? targetPort, string? scheme, string? name, string? env, bool isProxied, bool? isExternal, ProtocolType? protocol)`
  - `WithEndpoint(builder, int? port, int? targetPort, string? scheme, string? name, string? env, bool? isProxied, bool? isExternal)`
  - `WithEndpoint(builder, int? port, int? targetPort, string? scheme, string? name, string? env, bool isProxied, bool? isExternal)`

Each of these three is a distinct, currently-shipping Aspire overload (verified by decompiling
`Aspire.Hosting.dll` 13.5.2 — see the design doc §1 table, rows #2–#4) with **no default parameter
values at all**: every argument must be supplied to reach one. This is what makes them individually
reachable by an AppHost author's call — omitting even one argument disqualifies all three and falls
through to the Task 1 primary overload instead.

- [ ] **Step 1: Write the failing tests**

Add these three tests to `ServiceSourcesBuilderExtensionsTests.cs`, after the two tests Task 1 added:

```csharp
[Fact]
public void WithEndpoint_CompatShimWithProtocol_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

    // `protocol:` excludes the two no-protocol shims outright (they have no such parameter); of
    // the two candidates left, a plain bool literal is an exact match for this shim's non-nullable
    // isProxied and only an implicit conversion for the nullable primary's -- exact match wins.
    service.WithEndpoint(
        port: 9999, targetPort: 9999, scheme: "https", name: "https", env: null,
        isProxied: true, isExternal: null, protocol: ProtocolType.Tcp);

    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}

[Fact]
public void WithEndpoint_CompatShimNullableIsProxiedNoProtocol_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

    // Every argument but `protocol` supplied, with isProxied typed bool? explicitly -- the shape
    // that reaches this shim rather than its bool-isProxied sibling (not applicable to a bool?
    // argument without an explicit, non-implicit conversion) or the primary overload (which needs
    // its `protocol` default, so loses to any shim applicable without one).
    bool? isProxied = null;
    service.WithEndpoint(
        port: 9999, targetPort: 9999, scheme: "https", name: "https", env: null,
        isProxied: isProxied, isExternal: null);

    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}

[Fact]
public void WithEndpoint_CompatShimBoolIsProxiedNoProtocol_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

    // Every argument but `protocol` supplied, with a plain bool literal for isProxied -- an exact
    // type match beats the bool?-typed sibling shim's implicit-conversion match, so this is the
    // shape that reaches this specific overload.
    service.WithEndpoint(
        port: 9999, targetPort: 9999, scheme: "https", name: "https", env: null,
        isProxied: true, isExternal: null);

    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithEndpoint_CompatShim" -f net8.0`

Expected: all three **FAIL** — each call shape currently binds to one of Aspire's own compat-shim
overloads directly (no shadow exists yet for any of them), takes the ungated update branch, and
mutates the port to `9999` with no warning.

- [ ] **Step 3: Add the three shadow methods**

Insert these three methods in `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`,
immediately after the primary `WithEndpoint` shadow Task 1 added:

```csharp
    /// <summary>
    /// Binary-compatibility shim for <see cref="WithEndpoint"/>'s primary overload — same shape
    /// Aspire itself keeps for callers compiled against the pre-nullable-<c>isProxied</c> signature.
    /// Same gate, same reason (#335): this is real, currently-shipping Aspire API surface, not
    /// speculative scaffolding, and an AppHost author who writes a literal <c>isProxied: true</c>
    /// alongside an explicit <c>protocol</c> reaches this overload, not the nullable one.
    /// </summary>
    [AspireExportIgnore(Reason = "Binary compatibility shim for the nullable isProxied overload.")]
    public static IResourceBuilder<ServiceResource> WithEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port, int? targetPort, string? scheme, [EndpointName] string? name, string? env,
        bool isProxied, bool? isExternal, ProtocolType? protocol)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(
            builder, port, targetPort, scheme, name, env, (bool?)isProxied, isExternal, protocol);
    }

    /// <summary>
    /// Subset overload of <see cref="WithEndpoint"/>'s primary signature, omitting <c>protocol</c> --
    /// Aspire's own pre-protocol-parameter shape, kept for source compatibility. Same gate, same
    /// reason (#335) as every other shim in this file.
    /// </summary>
    [AspireExportIgnore(Reason = "Subset of the full WithEndpoint overload which is already exported.")]
    public static IResourceBuilder<ServiceResource> WithEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port, int? targetPort, string? scheme, [EndpointName] string? name, string? env,
        bool? isProxied, bool? isExternal)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(
            builder, port, targetPort, scheme, name, env, isProxied, isExternal, null);
    }

    /// <summary>
    /// Binary-compatibility shim combining both of <see cref="WithEndpoint"/>'s other two shims'
    /// omissions — no <c>protocol</c>, non-nullable <c>isProxied</c>. Same gate, same reason (#335).
    /// </summary>
    [AspireExportIgnore(Reason = "Binary compatibility shim for the nullable isProxied overload.")]
    public static IResourceBuilder<ServiceResource> WithEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port, int? targetPort, string? scheme, [EndpointName] string? name, string? env,
        bool isProxied, bool? isExternal)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(
            builder, port, targetPort, scheme, name, env, (bool?)isProxied, isExternal, (ProtocolType?)null);
    }

```

Note the last two shims each delegate straight to Aspire's **primary** `WithEndpoint` (passing
`protocol: null` explicitly), not to each other or through the middle shim — this mirrors Aspire's
own decompiled bodies exactly (verified in the design doc §1 table, row #4: "not via #3, despite the
identical parameter list").

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithEndpoint_CompatShim" -f net8.0`

Expected: all three **PASS**.

- [ ] **Step 5: Run the full test suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings (all three new overloads carry
`[AspireExportIgnore]`, matching Aspire's own attribute on each, so no `ASPIREEXPORT008` risk here).

Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs
git commit -m "$(cat <<'EOF'
Shadow WithEndpoint's three binary-compat shims (#335)

Aspire ships three older WithEndpoint<T> signatures (non-nullable
isProxied, and/or no protocol parameter) alongside the primary one
Task 1 already shadowed. Each is real, reachable API surface with the
identical ungated update-branch bug, so each gets its own shadow
overload rather than being left open the way #339 left the analogous
WithHttpEndpoint/WithHttpsEndpoint shims.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Shadow the callback overload

The callback overload is qualitatively different from every overload in Tasks 1-2: it can mutate any
field on the `EndpointAnnotation` (not just `Port`/`TargetPort`/`IsExternal`/`IsExplicitlyProxied`),
and when unreachable, the gate must prevent the callback from running **at all** — there is no
per-property `.HasValue` check to lean on the way the numeric overloads have.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`

**Interfaces:**
- Consumes: `GateEndpointCall` (unchanged).
- Produces: `public static IResourceBuilder<ServiceResource> WithEndpoint(this IResourceBuilder<ServiceResource> builder, string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true)`

- [ ] **Step 1: Write the failing tests**

Add to `ServiceSourcesBuilderExtensionsTests.cs`, after the three compat-shim tests from Task 2:

```csharp
[Fact]
public void WithEndpoint_Callback_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "https");
    var callbackInvoked = false;

    var result = service.WithEndpoint("https", endpoint =>
    {
        callbackInvoked = true;
        endpoint.Port = 9999;
    });

    Assert.Same(service, result);
    Assert.False(callbackInvoked, "the callback must never run when the source is unreachable");
    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}

[Fact]
public void WithEndpoint_Callback_OnReachableSource_InvokesCallbackAndAppliesThroughToTheRealEndpoint()
{
    var builder = Builder();
    var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
    var service = ResolvedService.Bridge(real, "orders", "container");
    // Pre-registered through the already-correct WithHttpsEndpoint add branch, so this test
    // exercises the callback overload's update branch against a real shared instance -- not its
    // separate, pre-existing add-branch dual-write gap (#352, out of this task's scope).
    service.WithHttpsEndpoint(port: 443, name: "probe");
    var callbackInvoked = false;

    service.WithEndpoint("probe", endpoint =>
    {
        callbackInvoked = true;
        endpoint.Port = 9999;
    });

    Assert.True(callbackInvoked);
    var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
    Assert.Equal(9999, endpoint.Port);
    Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
}
```

Add to `EndpointSkipGapRepro.cs`, immediately after the
`DefaultNamedEndpoint_OnKubernetesSource_ViaRawWithEndpoint_DoesNotChangeThePortForwardsEndpoint`
test Task 1 added:

```csharp
// #335's most severe scenario: an arbitrary-mutation callback against the real kubectl
// port-forward's endpoint must never run at all when unreachable -- not run-then-reverted, since
// this overload can mutate fields (scheme, protocol, target) no numeric overload exposes, so
// "gated before it ran" and "ran but its effect was reverted" are not equivalent guarantees here.
[Fact]
public void DefaultNamedEndpoint_OnKubernetesSource_ViaCallback_DoesNotChangeThePortForwardsEndpoint()
{
    var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
    var service = new KubernetesSource(new FakePortAllocator(54321))
        .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

    var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
    var beforePort = before.Port;
    var callbackInvoked = false;

    service.WithEndpoint("https", endpoint =>
    {
        callbackInvoked = true;
        endpoint.Port = 9999;
    });

    var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
    var warnings = ServiceSourcesWarnings.For(builder).Messages;

    Assert.False(callbackInvoked);
    Assert.Same(before, after);
    Assert.Equal(beforePort, after.Port);
    Assert.True(
        warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
        $"endpoint port after={after.Port}; warnings={warnings.Count}; "
        + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithEndpoint_Callback|FullyQualifiedName~ViaCallback" -f net8.0`

Expected: the url-source and kubernetes-source tests **FAIL** — today the callback runs unconditionally
(`callbackInvoked` becomes `true`, `Port` becomes `9999`), since no shadow exists yet for this
overload. The reachable-source test should already **PASS**.

- [ ] **Step 3: Add the callback shadow method**

Insert immediately after the third compat shim Task 2 added, in
`src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`:

```csharp
    /// <summary>
    /// Shadows Aspire's own callback-based <c>WithEndpoint&lt;T&gt;</c>. Unlike every numeric
    /// overload above, this one hands the AppHost author the live <see cref="EndpointAnnotation"/>
    /// itself with no constraint on what it mutates, and Aspire's own implementation gates neither
    /// its update branch nor its add branch. When unreachable, the callback is never invoked at all
    /// -- there is no partial-mutation state to reason about the way there is for a property that
    /// simply keeps its old value. Mirrors Aspire's own <c>[AspireExportIgnore]</c> exactly: this
    /// overload was never projected to guest languages in the first place.
    /// </summary>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withEndpointCallback export, which exposes EndpointUpdateContext instead of EndpointAnnotation.")]
    public static IResourceBuilder<ServiceResource> WithEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        [EndpointName] string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(builder, endpointName, callback, createIfNotExists);
    }

```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithEndpoint_Callback|FullyQualifiedName~ViaCallback" -f net8.0`

Expected: all three **PASS**.

- [ ] **Step 5: Run the full test suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings.

Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs
git commit -m "$(cat <<'EOF'
Shadow the WithEndpoint callback overload (#335)

The most severe of #335's unguarded call surfaces: Aspire's callback
overload hands an AppHost author the live EndpointAnnotation with no
constraint on what it mutates, and gates neither its update branch nor
its add branch. The shadow skips the call entirely when unreachable --
the callback never runs -- rather than running it and trying to
revert its effect afterward. The add branch's separate dual-write gap
on reachable sources (#352) is out of scope here.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Shadow `WithHttpEndpoint`/`WithHttpsEndpoint`'s leftover `bool isProxied` shims

Closes the gap #339 explicitly left open (#334's design doc §3.6): an AppHost author who writes a
literal `isProxied: true`/`isProxied: false` (not `isProxied: (bool?)true`) on `WithHttpEndpoint` or
`WithHttpsEndpoint` binds to Aspire's non-nullable-`isProxied` compat overload, which the existing
`WithHttpEndpoint`/`WithHttpsEndpoint` shadows (already in this file, added by #339) do not cover.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`

**Interfaces:**
- Consumes: `GateEndpointCall` (unchanged).
- Produces:
  - `WithHttpEndpoint(this IResourceBuilder<ServiceResource> builder, int? port, int? targetPort, string? name, string? env, bool isProxied)`
  - `WithHttpsEndpoint(this IResourceBuilder<ServiceResource> builder, int? port, int? targetPort, string? name, string? env, bool isProxied)`

- [ ] **Step 1: Write the failing tests**

Add to `ServiceSourcesBuilderExtensionsTests.cs`, after the callback-overload tests from Task 3:

```csharp
[Fact]
public void WithHttpEndpoint_CompatShim_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "http");

    // Every argument supplied, with a plain bool isProxied literal -- an exact match for this
    // shim's non-nullable isProxied, and only an implicit conversion for the nullable primary's,
    // so this is the shape that binds here rather than to the overload already shadowed above.
    service.WithHttpEndpoint(port: 9999, targetPort: 9999, name: "http", env: null, isProxied: true);

    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}

[Fact]
public void WithHttpsEndpoint_CompatShim_DefaultNameOnUrlSource_IsSkippedAndReported()
{
    var builder = Builder();
    var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

    service.WithHttpsEndpoint(port: 9999, targetPort: 9999, name: "https", env: null, isProxied: true);

    Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
    var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithHttpEndpoint_CompatShim|FullyQualifiedName~WithHttpsEndpoint_CompatShim" -f net8.0`

Expected: both **FAIL** — each call binds today to Aspire's own `bool`-`isProxied` compat overload
directly, mutates the port, and records no warning.

- [ ] **Step 3: Add the two shadow methods**

Insert immediately after the callback overload shadow Task 3 added, in
`src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`:

```csharp
    /// <summary>
    /// Binary-compatibility shim for <see cref="WithHttpEndpoint"/> -- the leftover gap #339 (#334)
    /// explicitly left open pending this ticket. An AppHost author who writes a literal
    /// <c>isProxied: true</c>/<c>isProxied: false</c> binds here rather than to the nullable
    /// overload already shadowed above.
    /// </summary>
    [AspireExportIgnore(Reason = "Binary compatibility shim for the nullable isProxied overload.")]
    public static IResourceBuilder<ServiceResource> WithHttpEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port, int? targetPort, [EndpointName] string? name, string? env, bool isProxied)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithHttpEndpoint(builder, port, targetPort, name, env, (bool?)isProxied);
    }

    /// <summary>
    /// The <c>https</c> counterpart of the shim above -- same shadowing, same gate, same reason.
    /// </summary>
    [AspireExportIgnore(Reason = "Binary compatibility shim for the nullable isProxied overload.")]
    public static IResourceBuilder<ServiceResource> WithHttpsEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port, int? targetPort, [EndpointName] string? name, string? env, bool isProxied)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithHttpsEndpoint(builder, port, targetPort, name, env, (bool?)isProxied);
    }

```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithHttpEndpoint_CompatShim|FullyQualifiedName~WithHttpsEndpoint_CompatShim" -f net8.0`

Expected: both **PASS**.

- [ ] **Step 5: Run the full test suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings.

Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs
git commit -m "$(cat <<'EOF'
Shadow WithHttpEndpoint/WithHttpsEndpoint's bool-isProxied shims (#335)

#339 shadowed only the nullable-isProxied WithHttpEndpoint/
WithHttpsEndpoint overloads and explicitly left their non-nullable
bool isProxied binary-compat shims open pending this ticket. Same
gate, same fix shape, closing the last of #335's seven call surfaces.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Documentation touch-ups and final verification

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs:27-33` (the bridge comment)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs` (`CapabilityLabel`'s
  remarks doc comment)

**Interfaces:**
- Consumes: nothing new — this task only edits comments and re-verifies everything Tasks 1-4 built.
- Produces: nothing new.

- [ ] **Step 1: Update the `ResolvedService.cs` bridge comment**

In `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs`, change:

```csharp
        // That same update branch is also why WithHttpEndpoint/WithHttpsEndpoint are shadowed
        // (ServiceSourcesBuilderExtensions) rather than gated solely through WithAnnotation: naming
        // an endpoint that already exists here — the default name on a "url"/"kubernetes" service,
        // which is exactly the name this method pre-registers — takes Aspire's in-place-update
        // branch, which never calls WithAnnotation at all (#334). The shadow is a second
        // interception point reading the identical Reachability table, not a workaround duplicating
        // its logic.
```

to:

```csharp
        // That same update branch is also why WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint (and
        // their binary-compat shims, and the callback overload) are all shadowed
        // (ServiceSourcesBuilderExtensions) rather than gated solely through WithAnnotation: naming
        // an endpoint that already exists here — the default name on a "url"/"kubernetes" service,
        // which is exactly the name this method pre-registers — takes Aspire's in-place-update
        // branch, which never calls WithAnnotation at all (#334, #335). Nine shadow overloads across
        // three method names are all second interception points reading the identical Reachability
        // table, not workarounds duplicating its logic.
```

- [ ] **Step 2: Update `CapabilityLabel`'s remarks**

In `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs`, find the doc comment
directly above `CapabilityLabel` (starts `/// The capability name a skip warning should show for
<paramref name="annotationType"/>...`). Its `<remarks>` block currently explains only the
`EnvironmentAnnotation` literal-vs-`nameof` choice. Add one sentence after that existing remarks
paragraph:

```csharp
    /// <remarks>
    /// <c>"EnvironmentAnnotation"</c> is a literal, not <c>nameof</c>: that type
    /// (<c>Aspire.Hosting.ApplicationModel.EnvironmentAnnotation</c>) is <c>internal</c> to
    /// <c>Aspire.Hosting.dll</c>, so this package cannot name it at compile time — but Aspire's own
    /// public <c>WithEnvironment(builder, name, string value)</c> overload constructs one and passes
    /// it straight to <c>WithAnnotation&lt;EnvironmentAnnotation&gt;</c>, so <paramref
    /// name="annotationType"/> is genuinely this type at runtime for the AppHost's most common
    /// <c>WithEnvironment</c> call, and the label must still recognize it. The
    /// <see cref="EndpointAnnotation"/> label names three methods, not one: <c>WithEndpoint</c>,
    /// <c>WithHttpEndpoint</c> and <c>WithHttpsEndpoint</c> (and each one's own binary-compat shims)
    /// all resolve to a lookup against this same annotation type, so a skip triggered by any of them
    /// is the identical capability, reported once under one label rather than three.
    /// </remarks>
```

- [ ] **Step 3: Run the full test suite and build one final time**

Run: `dotnet build -c Release -warnaserror`
Expected: 0 errors, 0 warnings, 0 `ASPIREEXPORT008`.

Run: `dotnet test -c Release`
Expected: every test passes across net8.0/net9.0/net10.0, including all nine tests Tasks 1-4 added.

- [ ] **Step 4: Re-verify the `typecheck-typescript` leg one more time**

```bash
export PATH="/tmp/aspire-cli:$PATH"
cd samples/DemoAppHostTypeScript && rm -rf .aspire && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json && cd -
cd samples/DemoAppHostTypeScriptCodeCatalog && rm -rf .aspire && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json && cd -
```

Expected: both exit 0 — confirming Tasks 2-4's `[AspireExportIgnore]`-only additions didn't disturb
what Task 1 already verified clean.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs \
        src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs
git commit -m "$(cat <<'EOF'
Update shadow-mechanism comments for #335's nine total interception points

ResolvedService's bridge comment and Reachability.CapabilityLabel's
remarks both described two shadow overloads across two methods; update
both now that #335 brings the total to nine across three methods.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Notes

- **Spec coverage:** §1 (all 7 overloads) → Tasks 1, 2, 3, 4. §2 (callback semantics) → Task 3.
  §3.1 (label widening) → Task 1, Step 4. §3.1a (`ASPIREEXPORT008`/`typecheck-typescript`) → Task 1,
  Steps 6-7, re-verified in Task 5. §3.2 (compiled-probe verification for the near-ambiguous shim
  pair) → Task 2's tests are themselves the compiled-and-run probe, engineered to hit each specific
  overload by argument shape. §3.3 (out of scope: #352, #353, `container`) → not built anywhere in
  this plan, consistent with the design. §4 (tests) → covered across all four implementation tasks.
  §5 (doc touch-ups) → Task 5. §6 (no CHANGELOG entry) → Global Constraints, never touched by any task.
- **Placeholder scan:** no TBD/TODO; every step shows literal code or literal shell commands.
- **Type consistency:** every shadow's delegation call matches the exact Aspire overload signature and
  cast pattern verified in the design doc's §1 table and §2 code listing; every test's expected label
  string matches Task 1 Step 4's exact widened literal.
