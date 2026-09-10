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
can be `Resource`-derived and still work for `container`, `kubernetes`, and `url`. It cannot also be
the resource registered for `local` without giving up genuine `ProjectResource` semantics — launch
profiles, debugger attach, hot reload, project metadata — which is exactly the machinery
`ExecutableCreator` gates behind `is ProjectResource` (`LaunchConfigurationType == "project"`,
`SupportsDebuggingAnnotation`, `TryGetProjectMetadata`).

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

- **A facade that does not need to be the registered DCP resource for `local`.** For instance:
  `AddService` keeps returning `IResourceBuilder<ServiceResource>` for `container`/`kubernetes`/`url`
  (three sources where `ServiceResource : Resource` is the literal registered object), while `local`
  is special-cased — either a distinct return path, or `ServiceResource` for `local` is a thin
  `ServiceResource`-shaped view that is **not** what DCP executes, with `local`'s real `ProjectResource`
  registered separately. The second option reopens the wiring question #319 also left open
  ("whether `WithReference` still resolves at runtime") in a harder form: Aspire's own `WithEnvironment`
  / `WithReference` extensions mutate `IResourceBuilder<T>.Resource.Annotations` directly, so unless
  the facade object *is* the registered resource, configuration applied through it would never reach
  DCP. That was not probed here and would need its own `aspire run` spike before being trusted.
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

## Recommendation

Do not implement a single-class facade across all four sources — it cannot preserve `local`'s
current DCP-native behaviour. Before writing an implementation plan for #313, decide between:

1. **Split return type by source-compatibility, not by source name**: `ServiceResource` unifies
   `container` + `kubernetes` + `url` (three internal types collapse into subtypes of one public
   class); `local` keeps returning `IResourceBuilder<ProjectResource>`. This is a real API split
   (`AddService` cannot have one static return type doing this — it would need to be two methods, or
   accept that the declared return type stays the covariant common ground, which is back to needing a
   shared base "wide enough" for `ProjectResource` too, i.e. `Resource` itself, at which point ATS
   codegen sees only the `Resource`-level vocabulary for `local` again).
2. **Confirm whether `ServiceResource : Resource` can be the registered object for `local` too**,
   abandoning `ProjectResource`'s launch-profile/debugging integration for `local` services. This is a
   real feature regression for what is likely the most-used source, and should be a deliberate,
   named trade-off if chosen — not a side effect.
3. **Keep today's architecture** (bare capability-interface return, `Configure<T>`/`As<T>`/shims) as
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
