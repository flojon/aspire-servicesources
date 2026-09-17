# Dual-write the callback overload's add branch onto the real resource (#352) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #352 by making the `WithEndpoint(name, Action<EndpointAnnotation>, createIfNotExists: true)`
shadow forward the brand-new `EndpointAnnotation` Aspire's *add* branch puts on the `ServiceResource`
facade through to the real resource DCP actually runs, so an endpoint created that way on a
`local`/`container` service stops silently having no effect.

**Architecture:** One new internal static member on `ServiceResourceBuilder` —
`ForwardingAnnotationsAddedBy(builder, call)` — snapshots the facade's annotation instances by
reference, runs the delegated Aspire call, then reads `Real.Resource.Annotations` **live, after the
call**, and forwards to `real` every facade instance in neither set (design §2.1, Option A: identity
diff). The callback shadow in `ServiceSourcesBuilderExtensions` becomes
gate → snapshot → delegate → forward. Nothing else changes: not `GateEndpointCall`, not
`Reachability`, not `ServiceResourceBuilder.WithAnnotation`'s dual-write, not the other eight
`WithEndpoint` shadows, not any public signature.

**Tech Stack:** C# / .NET (net8.0, net9.0, net10.0 multi-target), xUnit, Aspire.Hosting 13.5.2
(pinned floor, `Directory.Build.props`).

**Spec:** [docs/superpowers/specs/2026-09-17-352-callback-overload-dualwrite-design.md](../specs/2026-09-17-352-callback-overload-dualwrite-design.md)

## Global Constraints

- **Pinned Aspire floor: `Aspire.Hosting` 13.5.2.** Every claim this plan makes about Aspire's
  behaviour comes from the spec's §1 decompilation of
  `~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll`. Do not re-derive it from
  memory; if something contradicts the spec, stop and report rather than adjusting the code to fit.
- **The `real`-side read is taken AFTER the delegated call, never before.** This is the single
  load-bearing subtlety of the whole design (spec §3, step 4). A pre-call snapshot of `real` cannot
  see what the delegate wrote to `real` *during* the call — which is what every numeric overload
  does, dual-writing mid-delegate through `ServiceResourceBuilder.WithAnnotation` — so a pre-call
  read makes such an instance look new-on-facade-and-absent-from-real and adds it a **second time**.
- **The forward goes to `real` directly (`real.WithAnnotation(annotation)`), never through
  `builder.WithAnnotation`.** The facade already holds the instance — Aspire put it there — so
  routing through `ServiceResourceBuilder.WithAnnotation` would add it to the facade twice.
- **`Append` behaviour only — never `Replace`.** With `TAnnotation` inferred as `IResourceAnnotation`,
  Aspire's `Replace` path runs `Resource.Annotations.OfType<IResourceAnnotation>().SingleOrDefault()`,
  which throws `InvalidOperationException` whenever `real` holds more than one annotation — always
  true after `Bridge`. Take the parameterless default.
- **The diff is not type-filtered.** `ForwardingAnnotationsAddedBy` is a plain non-generic method
  yielding `IResourceAnnotation`-typed instances. Do not add a `<TAnnotation>` parameter and do not
  filter to `EndpointAnnotation` (spec §2.6).
- **Only the callback shadow is wrapped.** The eight numeric shadows are left exactly as they are;
  wrapping them is provably unnecessary (spec probe P4) and their drift risk is covered by a test
  (Task 2), not by prophylactic wrapping.
- **No `CHANGELOG.md` entry and no `README.md` change** (spec §6). The bug lives entirely inside the
  current `[Unreleased]` cycle: the dual-write bridge arrived in `243c227` (#313/#326, 2026-09-14),
  the latest tag is `v0.5.1` (2026-09-07), and `CHANGELOG.md`'s preamble restricts `Fixed` to bugs in
  already-*released* behaviour. Task 4 re-confirms no tag was cut in the meantime. Writing neither
  file also keeps the rebase against sibling PR #364 (which edits both) empty.
- **Comment style:** short, WHY-only, no implementation narrative, and **no issue/PR numbers in
  shipped comment text** — issue numbers belong in commit messages and in this plan. The spec's §3
  code block shows `(#352)` inline; that is the spec illustrating provenance, not the shipped text.
  Neighbouring comments in these files carry older issue refs; do not add new ones, and do not strip
  the existing ones (out of scope).
- **Verify legs** (from the ticket notes file). Cheap, run every task: `dotnet restore`,
  `dotnet build -c Release --no-restore -warnaserror`, `dotnet test -c Release --no-build`. `-warnaserror`
  is the flag that decides green from red — a build without it is a false green. Run
  `dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package`
  once, in Task 4. Legs 5–11 (smoke tests, TypeScript typecheck, repo invariants, Aspire version
  matrix, .NET 11 preview) **cannot run on this machine** and must be named as not run, never implied
  to have passed. This diff touches only `src/` and `test/` C# files, so it does not trigger the
  Aspire version matrix or affect the repo-invariant scripts.
- All commands run from the worktree root
  `C:\Source\aspire-servicesources\.claude\worktrees\ticket-352-517a7c`, on branch
  `claude/ticket-352-517a7c`.

---

## File Structure

- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs` — add one
  `internal static` member, `ForwardingAnnotationsAddedBy`, to the `ServiceResourceBuilder` class,
  after `WithAnnotation<TAnnotation>`. This file already owns the dual-write contract and is the only
  file that knows what `real` is, which is why the member lives here rather than in the extensions
  file (spec §3).
- **Modify** `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:382-402` — the
  callback-overload shadow's body (the gate stays; the bare delegation is wrapped) and its `<summary>`.
- **Create** `test/Aspire.Hosting.ServiceSources.Tests/EndpointCallbackDualWriteTests.cs` — the new
  fixture for every reachable-source case. The spec (§4) rules out putting these in
  `EndpointSkipGapRepro.cs`, whose stated subject is the *skip gate*, and `ServiceResourceTests.cs`
  pins `ServiceResource`'s interface shape and has nothing to do with dual-writing. A new fixture
  beside `EndpointSkipGapRepro.cs` is the spec's first-named option and is what this plan picks.
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs:182-204`
  — strengthen the existing
  `WithEndpoint_Callback_OnReachableSource_InvokesCallbackAndAppliesThroughToTheRealEndpoint` so its
  name becomes true (spec §4, case 3).
- **Modify** `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs` — one new `url`-source
  callback case (spec §4, case 8). The existing kubernetes callback case is **not** rewritten; it is
  the guard proving this change did not weaken the gate, and it must simply still pass.
- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs:20-34` — one added clause
  to the bridge comment (spec §5).

No change to `OverloadResolutionProbe.cs`: the one probe method the new unreachable test needs,
`OverloadProbe.CallWithEndpointCallback`, already exists.

---

## Task 1: Forward the callback add branch's new annotation to `real`

The core fix. Everything else in this plan is coverage around it.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs` (add a member to the
  `ServiceResourceBuilder` class, after `WithAnnotation<TAnnotation>`, before the class's closing brace)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:382-402`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointCallbackDualWriteTests.cs` (create)

**Interfaces:**
- Consumes: `ServiceResourceBuilder.Real` (`internal IResourceBuilder<IResource>?`),
  `ServiceResourceBuilder.Source` (`internal string`), `Reachability.IsUnreachable(Type, string)`,
  `Reachability.CapabilityLabel(Type)`, `ServiceSourcesWarnings.For(IDistributedApplicationBuilder).AddSkip(string, string, string)`,
  the existing private `GateEndpointCall(IResourceBuilder<ServiceResource>) : bool` (unchanged).
- Produces:
  `internal static IResourceBuilder<ServiceResource> ServiceResourceBuilder.ForwardingAnnotationsAddedBy(IResourceBuilder<ServiceResource> builder, Func<IResourceBuilder<ServiceResource>> call)`
  — called only from the callback shadow. Tasks 2 and 3 assert against its effects; neither adds
  another call site.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/EndpointCallbackDualWriteTests.cs` with exactly this
content:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers the callback <c>WithEndpoint</c> overload's <em>add</em> branch on reachable sources:
/// Aspire adds the brand-new <see cref="EndpointAnnotation"/> straight to the facade's collection
/// rather than through <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>, so without
/// the identity-diff forward it never reaches the resource DCP actually runs. Every assertion here
/// reads the real resource's own collection, not the facade's — the failure mode is silent, and a
/// facade-only assertion passes against the bug.
/// </summary>
public class EndpointCallbackDualWriteTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static IResourceBuilder<ServiceResource> ContainerService(
        IDistributedApplicationBuilder builder, string name) =>
        ResolvedService.Bridge(
            builder.AddResource(new ServiceContainerResource(name)).WithImage("nginx"), name, "container");

    // ServiceResourceBuilder.Real is internal; the test assembly reaches it through InternalsVisibleTo
    // (src/Aspire.Hosting.ServiceSources/AssemblyInfo.cs), exactly as ServiceResourceBuilderTests does.
    private static ResourceAnnotationCollection RealAnnotations(IResourceBuilder<ServiceResource> service) =>
        ((ServiceResourceBuilder)service).Real!.Resource.Annotations;

    [Fact]
    public void CallbackAddBranch_OnContainerSource_RegistersTheNewEndpointOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");
        // Pre-registered through the numeric overload, which dual-writes correctly today: the
        // pre-call snapshot must recognise this instance and leave it alone rather than forwarding
        // it a second time.
        service.WithHttpEndpoint(port: 8080, name: "http");

        service.WithEndpoint("admin", endpoint => endpoint.Port = 9200);

        var onFacade = Assert.Single(
            service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "admin");
        var onReal = Assert.Single(
            RealAnnotations(service).OfType<EndpointAnnotation>(), e => e.Name == "admin");

        Assert.Same(onFacade, onReal);
        Assert.Equal(9200, onFacade.Port);
        // Exactly two endpoints per side: Assert.Single above already rules out a duplicate "admin",
        // and these counts rule out a duplicated "http" from a re-forwarded pre-existing instance.
        Assert.Equal(2, service.Resource.Annotations.OfType<EndpointAnnotation>().Count());
        Assert.Equal(2, RealAnnotations(service).OfType<EndpointAnnotation>().Count());
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void CallbackAddBranch_OnLocalSource_RegistersTheNewEndpointOnTheRealResource()
    {
        var builder = Builder();
        var real = builder.AddResource(new ProjectResource("orders"));
        var service = ResolvedService.Bridge(real, "orders", "local");

        service.WithEndpoint("admin", endpoint => endpoint.Port = 9200);

        var onFacade = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var onReal = Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>());

        Assert.Same(onFacade, onReal);
        Assert.Equal("admin", onReal.Name);
        Assert.Equal(9200, onReal.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void CallbackUpdateAfterACallbackAdd_OnContainerSource_IsVisibleOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        service.WithEndpoint("admin", endpoint => endpoint.Port = 9200);
        service.WithEndpoint("admin", endpoint => endpoint.Port = 9500);

        var onFacade = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var onReal = Assert.Single(RealAnnotations(service).OfType<EndpointAnnotation>());

        // Once the first call forwards the instance, the two collections share it, so the second
        // call's in-place update needs no forwarding of its own to be visible on both.
        Assert.Same(onFacade, onReal);
        Assert.Equal(9500, onReal.Port);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointCallbackDualWriteTests"
```

Expected: **all three FAIL.** Today the delegated Aspire call adds the `admin` `EndpointAnnotation` to
the facade alone, so `Assert.Single(RealAnnotations(service).OfType<EndpointAnnotation>(), e => e.Name == "admin")`
finds no match ("The collection did not contain any matching elements") in the container case, and
`Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>())` finds none in the local case.
The follow-on-update case fails for the same reason: before the fix the first call's instance never
reaches `real`, so the second call's in-place update has nothing on the real resource to be visible
through (spec §3.2 — a facade-only endpoint stays facade-only forever). The first two are the P1
repro from spec §1.1, inverted.

- [ ] **Step 3: Add `ForwardingAnnotationsAddedBy`**

In `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs`, insert this member inside
the `ServiceResourceBuilder` class, immediately after the closing brace of
`WithAnnotation<TAnnotation>` and before the class's own closing brace:

```csharp

    /// <summary>
    /// Runs <paramref name="call"/> and forwards to <c>real</c> every annotation the call left on the
    /// facade alone. Aspire's callback <c>WithEndpoint</c> overload adds its brand-new
    /// <see cref="EndpointAnnotation"/> straight to <c>Resource.Annotations</c> rather than through
    /// <see cref="WithAnnotation{TAnnotation}"/> — the one method in its whole extension class that
    /// does — so diffing the collection across the call is the only way this bridge can learn about it.
    /// </summary>
    /// <remarks>
    /// The <c>real</c>-side read is deliberately taken <em>after</em> <paramref name="call"/> returns,
    /// never as a second pre-call snapshot: every numeric overload dual-writes through
    /// <see cref="WithAnnotation{TAnnotation}"/> <em>during</em> the call, and a pre-call read would
    /// miss that write, so the instance would look new on the facade, absent from <c>real</c>, and be
    /// added a second time. Reading after is also what makes this a silent no-op if a future Aspire
    /// routes this add branch through <c>WithAnnotation</c> itself.
    /// <para>
    /// The forward goes to <c>real</c> directly rather than back through
    /// <see cref="WithAnnotation{TAnnotation}"/>, because the facade already holds the instance.
    /// </para>
    /// <para>
    /// Deliberately not filtered to <see cref="EndpointAnnotation"/>: the diff observes instances, so
    /// a type filter could only drop something the delegated call added that nobody anticipated —
    /// silently, which is what <see cref="Reachability"/>'s fail-closed denylist exists to avoid.
    /// The reachability re-check below cannot fire today by construction, whatever type the diff
    /// yields: an unreachable source never gets past the caller's gate, and on a reachable one
    /// <see cref="Reachability.IsUnreachable"/> is false for every annotation type. It keeps the
    /// invariant "nothing reaches <c>real</c> without consulting <see cref="Reachability"/>" true of
    /// this path too. A fired re-check warns and leaves the annotation on the facade rather than
    /// removing an object this package did not add, so the divergence is named rather than silent.
    /// </para>
    /// </remarks>
    internal static IResourceBuilder<ServiceResource> ForwardingAnnotationsAddedBy(
        IResourceBuilder<ServiceResource> builder, Func<IResourceBuilder<ServiceResource>> call)
    {
        if (builder is not ServiceResourceBuilder serviceBuilder || serviceBuilder.Real is not { } realBuilder)
        {
            return call();
        }

        var beforeOnFacade = new HashSet<IResourceAnnotation>(
            serviceBuilder.Resource.Annotations, ReferenceEqualityComparer.Instance);

        var result = call();

        var nowOnReal = new HashSet<IResourceAnnotation>(
            realBuilder.Resource.Annotations, ReferenceEqualityComparer.Instance);

        foreach (var annotation in serviceBuilder.Resource.Annotations.ToArray())
        {
            if (beforeOnFacade.Contains(annotation) || nowOnReal.Contains(annotation))
            {
                continue;
            }

            if (Reachability.IsUnreachable(annotation.GetType(), serviceBuilder.Source))
            {
                ServiceSourcesWarnings.For(serviceBuilder.ApplicationBuilder).AddSkip(
                    serviceBuilder.Resource.Name,
                    serviceBuilder.Source,
                    Reachability.CapabilityLabel(annotation.GetType()));
                continue;
            }

            realBuilder.WithAnnotation(annotation);
        }

        return result;
    }
```

Three compile-level facts worth knowing before reading a red squiggle as a design problem:

- `ReferenceEqualityComparer.Instance` is an `IEqualityComparer<object?>`, and `IEqualityComparer<in T>`
  is contravariant, so it satisfies `HashSet<IResourceAnnotation>`'s comparer parameter directly. No
  cast, no custom comparer class.
- `realBuilder.WithAnnotation(annotation)` infers `TAnnotation` as `IResourceAnnotation`. That
  satisfies `where TAnnotation : IResourceAnnotation` (there is no `new()` constraint), and the
  default `Append` behaviour is exactly `Resource.Annotations.Add(annotation)`.
- `ReferenceEqualityComparer`, `HashSet<>` and `ToArray()` all come in via `ImplicitUsings` plus the
  file's existing `System.Linq` usage; the file needs no new `using`.

- [ ] **Step 4: Wrap the callback shadow's delegation**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, replace the callback
shadow's `<summary>` and body. Find:

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
```

and replace it with:

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
    /// <remarks>
    /// It is also the only overload whose delegation needs wrapping: its add branch is the one place
    /// in Aspire's whole extension class that adds an annotation by direct <c>Annotations.Add</c>
    /// instead of <c>builder.WithAnnotation</c>, so the new endpoint would otherwise sit on the
    /// facade alone and never reach the resource DCP runs.
    /// </remarks>
```

Then, in the same method, find:

```csharp
        return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(builder, endpointName, callback, createIfNotExists);
```

and replace it with:

```csharp
        return ServiceResourceBuilder.ForwardingAnnotationsAddedBy(
            builder,
            () => Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(builder, endpointName, callback, createIfNotExists));
```

The `if (GateEndpointCall(builder)) { return builder; }` block above it is untouched — the gate stays
strictly upstream of everything new, so no unreachable source's `real` resource can be written by
this path.

- [ ] **Step 5: Run the tests to verify they pass**

Run:

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointCallbackDualWriteTests"
```

Expected: **all three PASS.**

- [ ] **Step 6: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every existing test still green across net8.0/net9.0/net10.0. Pay
particular attention to the 20 existing tests in `EndpointSkipGapRepro.cs` and
`ServiceSourcesBuilderExtensionsTests.cs` — a regression there would mean the wrap disturbed the gate.

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs \
        src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointCallbackDualWriteTests.cs
git commit -m "$(cat <<'EOF'
Forward the callback overload's added endpoint to the real resource (#352)

Aspire's WithEndpoint(name, callback, createIfNotExists) add branch
adds the new EndpointAnnotation straight to Resource.Annotations
instead of through builder.WithAnnotation -- the only method in its
extension class that does -- so on a reachable source the endpoint
landed on the ServiceResource facade alone and never reached the
resource DCP runs. The shadow now snapshots the facade's annotation
instances by reference, delegates, and forwards anything new that
`real` does not already hold, read live after the call so a numeric
overload's mid-delegate dual-write is never added twice.

A follow-on update through the same overload is repaired as a
consequence: once the first call forwards the instance, both
collections share it and every later in-place mutation is visible on
both.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Pin the reachable-source invariants the fix must not disturb

Three cases the spec's acceptance checklist names (items 2, 3 and 4). Unlike Task 1's, all three pass
before the fix as well as after — they are regression guards and a version-drift detector, not
repros, which is why they are a separate task with no red leg rather than more TDD cycles inside
Task 1.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointCallbackDualWriteTests.cs` (add two tests)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs:182-204`
  (strengthen one existing test)

**Interfaces:**
- Consumes: `ContainerService` and `RealAnnotations` from Task 1's fixture; no production code changes.
- Produces: nothing new.

- [ ] **Step 1: Add the two new tests**

Append these inside the `EndpointCallbackDualWriteTests` class from Task 1, after
`CallbackUpdateAfterACallbackAdd_OnContainerSource_IsVisibleOnTheRealResource`:

```csharp
    [Fact]
    public void CallbackWithCreateIfNotExistsFalse_OnContainerSource_AddsNothingAndNeverInvokesTheCallback()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");
        var callbackInvoked = false;

        service.WithEndpoint("nope", _ => callbackInvoked = true, createIfNotExists: false);

        Assert.False(callbackInvoked);
        Assert.Empty(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Empty(RealAnnotations(service).OfType<EndpointAnnotation>());
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    // The drift detector for scoping the forward to one call site: the numeric overloads are left
    // unwrapped because their add branch ends in builder.WithAnnotation and so dual-writes already.
    // If this fails against a newer Aspire, that stopped being true and the numeric shadows need the
    // same wrap -- which is safe, since the identity guard makes it a no-op when it is not needed.
    [Fact]
    public void NumericAddBranch_OnContainerSource_StillReachesTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        service.WithEndpoint(port: 9400, name: "metrics", scheme: "http");

        var onFacade = Assert.Single(
            service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "metrics");
        var onReal = Assert.Single(
            RealAnnotations(service).OfType<EndpointAnnotation>(), e => e.Name == "metrics");

        Assert.Same(onFacade, onReal);
        Assert.Equal(9400, onReal.Port);
    }
```

- [ ] **Step 2: Strengthen the existing reachable-source callback test**

In `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`, inside
`WithEndpoint_Callback_OnReachableSource_InvokesCallbackAndAppliesThroughToTheRealEndpoint`, find:

```csharp
        // Pre-registered through the already-correct WithHttpsEndpoint add branch, so this test
        // exercises the callback overload's update branch against a real shared instance -- not its
        // separate, pre-existing add-branch dual-write gap (#352, out of this task's scope).
        service.WithHttpsEndpoint(port: 443, name: "probe");
```

and replace it with:

```csharp
        // Pre-registered through the already-correct WithHttpsEndpoint add branch, so this test
        // exercises the callback overload's update branch against an instance both collections
        // already share, rather than its add branch.
        service.WithHttpsEndpoint(port: 443, name: "probe");
```

Then find the following **four** lines together — the two-line `Assert.Equal(9999, …)` /
`Assert.Empty(…)` pair on its own occurs three times in this file (lines 63, 96 and 202), and two of
those methods also declare a local named `real`, so a shorter anchor would compile and silently add
these assertions to the wrong test:

```csharp
        Assert.True(callbackInvoked);
        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
```

and replace them with:

```csharp
        Assert.True(callbackInvoked);
        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
        Assert.Equal(9999, endpoint.Port);
        // The update branch mutates the shared instance in place, so `real` sees the new port with
        // no forwarding at all -- the through-to-the-real-endpoint half this test's name has always
        // claimed and never actually checked.
        Assert.Same(
            endpoint,
            Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe"));
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
```

(`real` is already a local in that test — `var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");`
— so no cast through `ServiceResourceBuilder.Real` is needed here.)

- [ ] **Step 3: Run the three tests**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointCallbackDualWriteTests|FullyQualifiedName~WithEndpoint_Callback_OnReachableSource"
```

Expected: **all six tests in that filter PASS** (Task 1's three plus these three). All three added
here would also have passed before Task 1 — they are guards, not repros, which is why this task has
no red leg. If any of them fails, the fix disturbed an invariant it was supposed to leave alone; that
is a stop-and-report, not a test to adjust.

- [ ] **Step 4: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings, every test green across all three TFMs.

- [ ] **Step 5: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/EndpointCallbackDualWriteTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs
git commit -m "$(cat <<'EOF'
Pin the reachable-source endpoint invariants around the forward (#352)

createIfNotExists: false stays a no-op with the callback never invoked,
and the numeric overload's add branch still reaches the real resource
-- the drift detector that justifies wrapping only the callback shadow
rather than all nine. The existing reachable-source callback test
asserted only against the facade despite its name; it now asserts the
shared instance on the real resource too, and its stale scope clause
is gone.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Prove the gate, not the null check, stops unreachable sources

Acceptance item 5. `url` is the source where `Real` is `null`, which makes it the one case that can
tell `GateEndpointCall` apart from `ForwardingAnnotationsAddedBy`'s own null check: a null check alone
would still let the delegated call run, invoke the callback, and add the endpoint to the facade.
Every existing `url` case in the suite goes through `WithHttpEndpoint`/`WithHttpsEndpoint` or a
numeric shim — none through the callback overload — so this is new coverage, not a duplicate.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs` (add one test)

**Interfaces:**
- Consumes: that file's existing `Url(IDistributedApplicationBuilder)` helper and
  `OverloadProbe.CallWithEndpointCallback` from `OverloadResolutionProbe.cs:51` (both already exist,
  unchanged); no production code changes.
- Produces: nothing new.

- [ ] **Step 1: Add the url-source callback test**

In `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`, insert this test immediately
after `NewEndpoint_OnUrlSource_IsSkippedAndReported` and before the
`// PASSES before and after — the update branch mutates nothing for a no-argument call` comment block:

```csharp
    // The callback overload against a "url" facade, which is the one source whose ServiceResourceBuilder
    // has no real resource at all. That makes it the case that distinguishes the gate from the
    // annotation-diff forward's own null check: the null check would still have let the delegated
    // call run, invoke the callback, and add "admin" to the facade. Nothing is added and the callback
    // never runs, so the gate is what stopped it.
    [Fact]
    public void NewEndpoint_OnUrlSource_ViaCallback_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);
        var before = service.Resource.Annotations.OfType<EndpointAnnotation>().Count();
        var callbackInvoked = false;

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithEndpointCallback(service, "admin", endpoint =>
        {
            callbackInvoked = true;
            endpoint.Port = 9999;
        });

        var after = service.Resource.Annotations.OfType<EndpointAnnotation>().Count();
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.False(callbackInvoked);
        Assert.Equal(before, after);
        Assert.DoesNotContain(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "admin");
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoints before={before}, after={after}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }
```

- [ ] **Step 2: Run the new test and the whole skip-gate fixture**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointSkipGapRepro"
```

Expected: **every test in the fixture PASSES**, including the new one and — this is the point of
running the whole fixture rather than just the new test —
`DefaultNamedEndpoint_OnKubernetesSource_ViaCallback_DoesNotChangeThePortForwardsEndpoint`, which
exercises the exact method body Task 1 edited and is the gate's guard for the `kubernetes` source.
The new test passes before and after Task 1 as well; it is coverage of a property the change must not
break, not a repro.

- [ ] **Step 3: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings, every test green across all three TFMs.

- [ ] **Step 4: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs
git commit -m "$(cat <<'EOF'
Cover the callback overload against a url source (#352)

Every existing url case in the suite reaches the gate through
WithHttpEndpoint/WithHttpsEndpoint or a numeric shim; none through the
callback overload. url is the source whose builder has no real
resource, so it is the one case that proves the reachability gate --
not the annotation-diff forward's own null check -- is what stops the
call: the callback never runs and nothing lands on the facade.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Documentation, CHANGELOG disposition, and full verification

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs:20-34` (the bridge comment)

**Interfaces:**
- Consumes: nothing. This task edits one comment and re-verifies everything Tasks 1–3 built.
- Produces: nothing.

- [ ] **Step 1: Extend the bridge comment**

In `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs`, find the end of the second comment
paragraph in `Bridge`:

```csharp
        // branch, which never calls WithAnnotation at all (#334, #335). Nine shadow overloads across
        // three method names are all second interception points reading the identical Reachability
        // table, not workarounds duplicating its logic.
```

and replace it with:

```csharp
        // branch, which never calls WithAnnotation at all (#334, #335). Nine shadow overloads across
        // three method names are all second interception points reading the identical Reachability
        // table, not workarounds duplicating its logic.
        //
        // The callback overload's shadow carries one extra job, because that overload bypasses
        // WithAnnotation in the other direction too: its *add* branch puts a brand-new
        // EndpointAnnotation straight onto the facade's collection, so the shadow diffs that
        // collection across the delegated call and forwards the new instance to the real resource.
        // That is what keeps the same-instance-in-both-collections claim above true for endpoints
        // created after bridge time, not only for the ones copied here.
```

- [ ] **Step 2: Re-confirm the CHANGELOG disposition**

```bash
git fetch origin --tags
git tag --sort=-v:refname | head -3
# rev-parse first: `git merge-base --is-ancestor` also exits non-zero on a bad rev, so without it a
# mistyped SHA prints the reassuring answer.
git rev-parse --verify 243c227^{commit}
git merge-base --is-ancestor 243c227 v0.5.1 && echo "TAGGED-BEFORE-RELEASE" || echo "STILL-UNRELEASED"
```

Expected: `git rev-parse` resolves the SHA, `v0.5.1` is still the newest tag, and the last command
prints `STILL-UNRELEASED` — so the
bug remains inside the current `[Unreleased]` cycle and `CHANGELOG.md`'s preamble means **no `Fixed`
entry**, exactly as spec §6 concluded. If a newer tag has appeared and `243c227` is now an ancestor of
it, **stop and report it** rather than writing an entry: the disposition changes, and which
`[Unreleased]`/released section it belongs in is a call for the human, not for this task. Do not edit
`CHANGELOG.md` or `README.md` under any other circumstance — sibling PR #364 edits both, and leaving
them alone keeps the rebase against it empty.

- [ ] **Step 3: Run every leg that can run on this machine**

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results
dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package
```

Expected: restore clean; build 0 errors / 0 warnings (`-warnaserror` is the flag that decides green
from red — a build without it is a false green); every test green across net8.0/net9.0/net10.0; pack
produces a `.nupkg` with no warnings.

Paste the real output of each into the notes file. Per superpowers:verification-before-completion, no
green claim without the output that proves it.

**Name these as NOT RUN, with the reason — never imply they passed:** the container-source, config-layers
and local-source smoke tests (legs 5–7: Linux-targeted bash scripts, and the config-layers one needs
the `aspire` CLI, which is not installed here); the TypeScript export-surface typecheck (leg 8:
neither Node nor the `aspire` CLI is installed); the repo-invariant scripts (leg 9: no real Python
interpreter here); the Aspire floor/latest version matrix (leg 10: CI only, and a source/test-only
diff does not trigger it); and the .NET 11 preview build (leg 11: scheduled/manual only). Leg 8 is
not path-filtered and so reports on every PR regardless; this change adds no exported surface (the
only touched public method keeps its signature and its `[AspireExportIgnore]`), so no TypeScript
projection changes.

- [ ] **Step 4: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs
git commit -m "$(cat <<'EOF'
Note the callback shadow's add-branch forward in the bridge comment (#352)

ResolvedService.Bridge's comment explains why the endpoint shadows
exist, in terms of the update branch bypassing WithAnnotation. The
callback overload's add branch bypasses it in the other direction, and
the shadow forwards the instance -- which is what keeps the comment's
own same-instance-in-both-collections claim true for endpoints created
after bridge time.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Notes

**Spec coverage.** §1 / §1.1 (the bug and probe P1) → Task 1's two repro tests. §1.2 (no collection
subclass available) → nothing to build; the design below it is what Task 1 implements. §2.1 (Option A,
identity diff, the `real`-already-holds guard) → Task 1, Step 3. §2.2–§2.5 (rejected options) →
nothing built, by construction. §2.6 (generic, non-type-filtered helper; application scoped to one
call site; drift covered by a test) → Task 1, Step 3 (no type parameter, no filter) and Task 2's
`NumericAddBranch_OnContainerSource_StillReachesTheRealResource`. §3 (fix shape, steps 1–5) → Task 1,
Steps 3–4; step 4's live post-call read is called out in Global Constraints, in the member's own
`<remarks>`, and in the commit message. §3.1 (no try/finally) → the plan adds none, and the code runs
the forward only on normal return. §3.2 (follow-on updates repaired as a consequence) → Task 1's
`CallbackUpdateAfterACallbackAdd_…`, which sits in Task 1 rather than Task 2 precisely because it
fails before the fix and so belongs on a red leg. §3.3 (accepted same-name residue) → deliberately
unguarded; no task adds name matching. §4 cases 1–8 → case 1 Task 1, case 2 Task 1, case 3 Task 2
Step 2 (the strengthened existing test; no duplicate added), case 4 Task 2, case 5 Task 2, case 6
Task 1 (the `Assert.Single`-with-predicate plus the per-side count of 2, which together rule out both
a duplicated `admin` and a re-forwarded `http`), case 7 Task 1, case 8 Task 3 (new `url` case) plus Task 3 Step 2's
whole-fixture run covering the existing kubernetes case. §5 (documentation) → Task 4 Step 1 for
`ResolvedService.cs`, Task 1 Step 3 for the new member's WHY, and no `README.md` change. §6 (no
CHANGELOG entry) → Global Constraints plus Task 4 Step 2's re-confirmation. Attack surface section →
the gate is left strictly upstream (Task 1 Step 4 touches nothing above it) and the per-annotation
reachability re-check is in Task 1 Step 3.

**Task boundaries.** Task 1 holds every case that is red before the fix (the two add-branch repros
and the follow-on update), so every red→green transition this plan claims is one a step actually
runs. Task 2 holds only cases that are green before and after — guards and the version-drift
detector — and therefore deliberately has no "verify it fails" step. Task 3 is the same shape for the
unreachable path. Task 4 is comment-only plus the full verification sweep.

**Acceptance checklist coverage** (from the ticket notes): item 1 → Task 1; item 2 → Task 2 Step 2;
item 3 → Task 2; item 4 → Task 2; item 5 → Task 3; item 6 → Task 1 (the diff mechanism, with the
no-double-add counts and `ServiceResourceBuilder.WithAnnotation` untouched); item 7 → Global
Constraints (every Aspire claim traced to the spec's decompilation); item 8 → Tasks 1–2, every
assertion reading the real resource's own collection; item 9 → Task 4 Step 2.

**Placeholder scan.** No TBD/TODO, no "add appropriate error handling", no "similar to Task N". Every
code step carries literal code; every run step carries a literal command and a stated expectation.
The one conditional in the plan (Task 4 Step 2's "if a newer tag has appeared") names a concrete
check, a concrete expected output, and a concrete action — stop and report — rather than leaving a
decision open.

**Type consistency.** `ForwardingAnnotationsAddedBy(IResourceBuilder<ServiceResource>, Func<IResourceBuilder<ServiceResource>>)`
is spelled identically in Task 1's Interfaces block, its definition, and its one call site.
`RealAnnotations`/`ContainerService` are defined once in Task 1's fixture and used unchanged in
Task 2. The warning label asserted in Task 3 (`"WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"`) is
the literal already in `Reachability.CapabilityLabel`, which this plan does not change.
