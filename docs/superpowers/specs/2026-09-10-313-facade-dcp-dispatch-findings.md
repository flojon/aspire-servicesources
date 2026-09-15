# Can one `ServiceResource` sit above all four sources? — measured findings

**Status:** Finding, for [issue #313](https://github.com/flojon/aspire-servicesources/issues/313)
**Date:** 2026-09-10
**Base commit:** `e1496f8`
**Answers open question 1** left by `2026-09-09-313-concrete-resource-ats-probe-findings.md` (PR
#319): *"How one `ServiceResource` sits above four different real resources. … Neither was probed
here."*
**Measured against:** `Aspire.Hosting` **13.5.3** (`~/.nuget/packages/aspire.hosting/13.5.3/lib/net8.0/Aspire.Hosting.dll`),
decompiled with `ilspycmd` 11.0.0.9375. Reading the framework's own dispatch code, not assuming it.

## Verdict

**No — not for all four sources with one class.** DCP decides how to run a resource two different
ways, and the two ways cannot be satisfied by the same concrete type at once:

- **Container dispatch is annotation-based.** `ContainerResourceExtensions.IsContainer(this
  IResource)` is `resource.Annotations.OfType<ContainerImageAnnotation>().Any()` — no type check at
  all. `GetContainerResources()` walks `model.Resources` the same way. A `Resource`-derived facade
  carrying a `ContainerImageAnnotation` (added directly via `WithAnnotation`, not through the
  `WithImage<T>() where T : ContainerResource` helper) is picked up and run as a container. **This
  source is compatible with a bare `ServiceResource : Resource`.**
- **Executable dispatch is nominal-type-based.** `ExecutableResourceExtensions.GetExecutableResources()`
  is `model.Resources.OfType<ExecutableResource>()`. There is no annotation escape hatch — the object
  in `model.Resources` must literally *be* an `ExecutableResource` (or subtype) for DCP to spawn and
  monitor it as a process. **The `kubernetes` source's `kubectl port-forward` needs this.**
- **Project dispatch is also nominal-type-based**, the same way: `ProjectResourceExtensions.GetProjectResources()`
  is `model.Resources.OfType<ProjectResource>()`. And in this Aspire version,
  `ProjectResource : Resource` directly — it does **not** derive from `ExecutableResource`. They are
  siblings under `Resource`, not a chain. **The `local` source needs this**, today satisfied for free
  because it calls Aspire's own `AddProject(name, path)` and gets a genuine `ProjectResource` back.

A single concrete class cannot be assignable to both `ExecutableResource` and `ProjectResource` —
C# has one base class per type, and neither Aspire type derives from the other. So `ServiceResource`
declared as `: ExecutableResource` (not merely `: Resource` — `kubernetes` specifically needs the
`ExecutableResource` ancestry, container's own requirement is looser) can be the literal registered
resource for `container`, `kubernetes`, and `url`. It cannot also be the resource registered for
`local` without giving up genuine `ProjectResource` semantics — launch profiles, debugger attach, hot
reload, project metadata — which is exactly the machinery `ExecutableCreator` gates behind
`is ProjectResource` (`LaunchConfigurationType == "project"`, `SupportsDebuggingAnnotation`,
`TryGetProjectMetadata`).

This is a harder wall than #319 could see, because #319 measured `tsc` only. The conflict is a plain
C#-language fact once you know what DCP actually keys on, and it holds regardless of what the
TypeScript side does.

## What was read, and where

| Dispatch | Decompiled site | Check |
|---|---|---|
| Container | `Aspire.Hosting.ContainerResourceExtensions.IsContainer` | `resource.Annotations.OfType<ContainerImageAnnotation>().Any()` |
| Container | `Aspire.Hosting.ContainerResourceExtensions.GetContainerResources` | same annotation walk over `model.Resources` |
| Executable | `Aspire.Hosting.ExecutableResourceExtensions.GetExecutableResources` | `model.Resources.OfType<ExecutableResource>()` |
| Project | `Aspire.Hosting.ApplicationModel.ProjectResourceExtensions.GetProjectResources` | `model.Resources.OfType<ProjectResource>()` |
| Project (deeper) | `Aspire.Hosting.Dcp.ExecutableCreator` | repeated `er.ModelResource is ProjectResource` / `modelResource is ProjectResource` gates around launch-profile handling, debugging support, project metadata |
| Class hierarchy | `Aspire.Hosting.ApplicationModel.ProjectResource` | `public class ProjectResource : Resource, IResourceWithEnvironment, IResource, IResourceWithArgs, IResourceWithServiceDiscovery, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithProbes, IComputeResource, IContainerFilesDestinationResource` |
| Class hierarchy | `Aspire.Hosting.ApplicationModel.ExecutableResource` | `public class ExecutableResource : Resource, IResourceWithEnvironment, IResource, IResourceWithArgs, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithProbes, IComputeResource` |
| Class hierarchy | `Aspire.Hosting.ApplicationModel.ContainerResource` | `public class ContainerResource(string name, ...) : Resource(name), ...` |
| `WithImage` constraint | `Aspire.Hosting.ContainerResourceBuilderExtensions.WithImage<T>` | `where T : ContainerResource` — a compile-time convenience only; DCP's own dispatch (row 1 above) does not require it |
| `WithAnnotation` | `Aspire.Hosting.ApplicationModel.IResourceBuilder<T>.WithAnnotation` | interface member, constrained only by `T : IResource` — so a `ContainerImageAnnotation` can be added to any resource builder without going through `WithImage<T>` |

None of `ProjectResource`, `ExecutableResource`, or `ContainerResource` is `sealed`. That is necessary
but not sufficient: today's internal types (`ServiceContainerResource : ContainerResource`,
`ServiceExecutableResource : ExecutableResource`) already exploit it. The wall is not sealing, it is
that `ProjectResource` and `ExecutableResource` are unrelated siblings, so nothing can derive from
both.

## What this rules in and out for #313's design

**Ruled out:** "One public `ServiceResource` class, and every source's `AddService(...)` returns
`IResourceBuilder<ServiceResource>` where the registered resource literally is one." Works for three
sources, not the fourth, and the fourth (`local`) is the one the package exists primarily to serve —
an in-repo checkout run with full dotnet tooling fidelity.

**Still open, not ruled out by this finding:**

- **A facade that does not need to be the registered DCP resource for `local` — measured, partially
  positive.** A follow-up probe (throwaway xUnit test, reverted after running, same convention as the
  #319 spike) built a hand-rolled `IResourceBuilder<FacadeResource>` wrapping a real
  `IResourceBuilder<ContainerResource>`, where `WithAnnotation` dual-writes: the *same* annotation
  instance goes into both the facade's and the real resource's `Annotations` collections, rather than
  copying values. Two things were confirmed this way, without needing a live `aspire run`:
  - **Read path**: a third resource's `.WithReference(facadeBuilder)` — Aspire's own extension,
    unlocked purely by `FacadeResource : IResourceWithServiceDiscovery` — produced a
    `services__orders__http__0` environment-variable *key* on the consumer, meaning
    `ApplyEndpoints`'s endpoint lookup (`endpointReferenceAnnotation.Resource.GetEndpoints(...)`) found
    the endpoint through the facade object, because the same `EndpointAnnotation` instance also lives
    in `real.Resource.Annotations`.
  - **Write path**: `facadeBuilder.WithEnvironment("FOO", "bar")` — again Aspire's own extension —
    landed the resulting `EnvironmentCallbackAnnotation` on `real.Resource.Annotations` too (the *only*
    object DCP ever sees, since the facade is never added via `builder.AddResource`), and invoking that
    real resource's callback directly produced `FOO=bar`.

  **Not covered by this probe, still open:** resolving an `EndpointReference`'s actual *value* (the URL
  string, via `IValueProvider.GetValueAsync`) hangs against a bare test builder with no live app —
  `EndpointReferenceAnnotation`'s deferred callback populates the environment-variable *key*
  synchronously (confirmed above) but the *value* needs a real `AllocatedEndpoint`, which only DCP sets
  during an actual run. So "the key resolves" is verified; "the URL is correct end-to-end" is not.
  Neither is `WaitFor`/wait-ordering — whether Aspire's wait machinery, which tracks state through
  `ResourceNotificationService`, correctly follows a wait recorded against the facade object through to
  the real resource's published state. Both would need an actual `aspire run` (with a container runtime
  or a real `kubectl`) rather than a bare `DistributedApplicationBuilder`, which is a materially heavier
  probe than this one.
- **Accepting `local` keeps returning `IResourceBuilder<ProjectResource>` directly**, rather than
  `ServiceResource`. `ProjectResource` already carries every capability interface `ServiceResource`
  would declare (`IResourceWithEnvironment`, `IResourceWithArgs`, `IResourceWithEndpoints`,
  `IResourceWithWaitSupport`) plus `IResourceWithServiceDiscovery` natively — so for `local`
  specifically, `Configure<T>`/`As<T>`/the `WithService*` shims are *already* unnecessary today, not
  because of this ticket but because Aspire's own type already gives an AppHost author full
  vocabulary. The only remaining problem for `local` is that `AddService`'s declared C# return type
  has to be *one* type across all four branches for ATS to generate one method/handle — so this path
  means `AddService` cannot uniformly return `ServiceResource` unless `ServiceResource` is also what
  `local` hands back, which is exactly the option above.
- **Whether `kubernetes`'s `ExecutableResource` requirement can be relaxed.** Not investigated: e.g.
  whether a resource can carry both an `ExecutableAnnotation`-equivalent and satisfy `OfType<ExecutableResource>()`
  through some other mechanism. Given `GetExecutableResources()` is a bare `OfType<T>()` with no
  annotation fallback (unlike containers), this looks like a hard requirement, not a soft one — but it
  was not exhaustively probed against every DCP code path that touches executables.

## The upstream fix already has a draft PR

The wall in this doc is specifically that container dispatch is annotation-based while executable and
project dispatch are nominal-type-based. Both `ExecutableAnnotation` (Command, WorkingDirectory) and
`IProjectMetadata` (: `IResourceAnnotation`; ProjectPath, LaunchSettings, ...) already carry the data
DCP would need to dispatch the same way containers do — the annotations exist, only the dispatch check
(`OfType<ExecutableResource>()` / `OfType<ProjectResource>()`) still reads the concrete type instead.

That exact change is in flight upstream: **microsoft/aspire#18052**, *"WIP: Use annotations to
determine project & executable resources"* — an open draft PR (filed 2026-06-09, still active as of
2026-09-06), whose description states the intent plainly: *"This brings the executable and project
behaviour in line with containers which are already annotation based."* It updates
`ResourceSnapshotBuilder`, `ApplicationOrchestrator`, `ManifestPublishingContext`, and
`ExecutableResourceExtensions` to check annotations rather than type, and includes a playground project
demonstrating a `ProjectResource` ↔ `ContainerResource` "bait and switch" using exactly this mechanism.
Its own description names the residual awkwardness: `GetProjectResources()`/`GetExecutableResources()`
still return "all resources of *type* X", which may no longer match what DCP actually executes as X
once annotations can diverge from type — a naming/documentation gap, not a blocker for #313's use.

If #18052 landed, the wall in this doc would go away: a `ServiceResource : Resource` facade carrying
the right annotation (`ContainerImageAnnotation`, `ExecutableAnnotation`, or an `IProjectMetadata`)
would get real DCP execution for every source, including `local`, with no feature regression and no
wrapper object.

**Correction — #18052 is not expected to merge as-is.** The PR's own thread has one substantive,
non-CI-retry comment, from `davidfowl` (an Aspire maintainer), in reply to a request to mark it
draft: *"Thats OK I dont think we would merge this anyways. This is a big enough change that it
should just be in the drafts."* Everything after that (a "hitting a few quirks" note from the author,
and several rounds of `github-actions` CI-retry messages) is the PR author continuing to poke at it
for their own understanding, not sustained movement toward merge. So this is not a reliable "wait for
it to land" dependency — it is evidence the *mechanism* is technically sound (annotation-based
dispatch works, per its own playground project demonstrating a `ProjectResource`↔`ContainerResource`
bait-and-switch) with no committed path for *this* PR to ship it. A differently-scoped, more formal
follow-on (plausibly through the #19836 proposal process, which is exactly the kind of "big enough
change" process davidfowl's comment gestures at) might still get there, but there is nothing to defer
to today.

Related, narrower proposal seen while searching: **microsoft/aspire#19836**, *"Resource projections:
typed, target-scoped container views instead of implicit shape conversion"* (filed 2026-09-09). It
solves a different problem — typed `RunAsContainer`/`PublishAsContainer`-style shape changes without
losing resource identity — and its own text calls out #18052 as "the direction of" annotation-based
classification, complementary rather than competing. Not a substitute for #18052 here.

## Recommendation

Do not implement a single-class facade across all four sources *without deciding one of the paths
below first*. The wrapper/dual-write mechanism now has a positive, if partial, empirical result —
it is no longer purely hypothetical — but it is a second real object per logical service, with its
own risks (see below). Before writing an implementation plan for #313, decide between:

1. **Track microsoft/aspire#18052 and defer #313's full unification — downgraded, not recommended
   as the primary path.** A maintainer (`davidfowl`) said directly on the PR thread that it isn't
   expected to merge as-is ("I dont think we would merge this anyways. This is a big enough change
   that it should just be in the drafts"). Unlike #72 ↔ microsoft/aspire#9965 — an accepted issue with
   ordinary review left to do — there is no committed path for *this* PR to land. Treat it as evidence
   the mechanism works, not as a dependency to wait on. Revisit if a differently-scoped follow-on
   appears (possibly through #19836's formal proposal process).
2. **Build the dual-write wrapper now, as the practical way to get #313's benefit without an upstream
   dependency that may never resolve.** The read and write paths
   both checked out in this doc's probe (endpoint discovery through `WithReference`, environment
   configuration through `WithEnvironment`, both reaching the real registered resource). What is
   *not* yet checked — full endpoint URL resolution end-to-end, and `WaitFor`/wait-ordering through
   `ResourceNotificationService` — needs an `aspire run`-grade probe (a real container runtime or
   `kubectl`) before this path can be trusted for `local`. Two objects per logical service is also a
   standing risk independent of what's tested: anything in Aspire's own code that reads
   `.Resource.Annotations` directly (not through an overridable `WithAnnotation`) has to be
   individually verified to see the shared instances, and a future Aspire release could add such a
   read path without warning. microsoft/aspire#19836 documents this exact risk class for its own,
   different projection mechanism ("two objects now exist per logical resource... identity
   canonicalization must be complete") — worth reading before committing to hand-rolling it here.
3. **Correction — "split the return type, `local` keeps `Configure<T>`/`As<T>`, the others don't"
   does not work as a single `AddService` method.** The source is picked at runtime from developer
   config, so one call site must handle whichever of the four sources gets selected — the declared
   return type is one fixed type, and by `IResourceBuilder<out T>` covariance every source's actual
   object must be assignable to it. `local`'s real object is Aspire's own `ProjectResource`, which
   cannot be made to inherit from anything this package declares. So either the declared type is rich
   (`ServiceResource : ExecutableResource`, unlocking `container`/`kubernetes`/`url`) and a real
   `ProjectResource` is never assignable to it — a compile error for a method that might resolve to
   `local` — or the declared type is forced down to whatever `ProjectResource` already satisfies,
   which (interfaces breaking TS codegen, per #319) means plain `Resource`, and *no* source gains
   anything through the return type; every source is back to `Configure<T>`/`As<T>`. **The only way to
   give the three sources the rich type while one `AddService` still covers `local` is option 2's
   wrapper** — at which point `local` also stops needing `Configure<T>`/`As<T>`, so this isn't a
   cheaper alternative to the bridge, it's the bridge with the container/kubernetes/url path spelled
   out. A genuinely different shape exists — **two separate methods**, e.g. `AddService` unchanged
   plus something like `AddContainerBackedService` with the rich return type, that an AppHost author
   opts into *per call site*, asserting that particular service will never resolve to `local` (and
   throwing at runtime if it does) — but that moves the decision from the developer's local config to
   the AppHost author's method choice, breaking the "any service transparently switches source without
   touching Program.cs" property for whichever services use it. Not equivalent to what "split" was
   gesturing at, and its own trade-off needs weighing separately if pursued.
4. **Confirm whether `ServiceResource : Resource` can be the registered object for `local` too**,
   abandoning `ProjectResource`'s launch-profile/debugging integration for `local` services. This is a
   real feature regression for what is likely the most-used source, and should be a deliberate,
   named trade-off if chosen — not a side effect.
5. **Keep today's architecture** (bare capability-interface return, `Configure<T>`/`As<T>`/shims) as
   the documented, permanent design, closing #313 on the grounds that the "yes" from PR #319 answered
   the codegen question but not the DCP-dispatch question, and the latter is where the real cost
   lives.

None of these is a code change by itself — each is a scope decision for #313 that determines whether
there is an implementation plan to write at all, and if so, what shape.

## Reproducing

```bash
DLL=~/.nuget/packages/aspire.hosting/13.5.3/lib/net8.0/Aspire.Hosting.dll
ilspycmd -t Aspire.Hosting.ContainerResourceExtensions "$DLL"          # IsContainer, GetContainerResources
ilspycmd -t Aspire.Hosting.ExecutableResourceExtensions "$DLL"         # GetExecutableResources
ilspycmd -t Aspire.Hosting.ApplicationModel.ProjectResourceExtensions "$DLL"  # GetProjectResources
ilspycmd -t Aspire.Hosting.ApplicationModel.ProjectResource "$DLL" | head -30      # class declaration
ilspycmd -t Aspire.Hosting.ApplicationModel.ExecutableResource "$DLL" | head -30   # class declaration
ilspycmd -t Aspire.Hosting.Dcp.ExecutableCreator "$DLL" | grep -n "is ProjectResource"
```

The dual-write wrapper probe was a throwaway xUnit test (`Spike313FacadeWrapperProbe.cs`, reverted after
running, same convention as #319): a `FacadeResource : Resource` plus a hand-rolled
`IResourceBuilder<FacadeResource>` whose `WithAnnotation` adds the same annotation instance to both the
facade's and a real `IResourceBuilder<ContainerResource>`'s `Annotations`. Two `[Fact]`s, both green:
`.WithReference(facadeBuilder)` from a third resource produces a `services__orders__http__0`
environment-variable key (checked by invoking `EnvironmentCallbackAnnotation.Callback` directly, since
`GetEnvironmentVariableValuesAsync` and resolving an `EndpointReference`'s value both hang without a
live app); `facadeBuilder.WithEnvironment("FOO", "bar")` lands an `EnvironmentCallbackAnnotation` on the
*real* resource, resolving to `FOO=bar` when that resource's own callback runs.
