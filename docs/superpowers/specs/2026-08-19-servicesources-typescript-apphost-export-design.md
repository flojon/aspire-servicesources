# Aspire.Hosting.ServiceSources — TypeScript AppHost Export Design

**Status:** Proposed
**Date:** 2026-08-19
**Scope:** Export `AddService()` through Aspire's Type System (ATS) so it can be called from a
TypeScript (or any other guest-language) AppHost, closing out
[issue #42](https://github.com/flojon/aspire-servicesources/issues/42).

## Problem

`AddService()` (`ServiceSourcesBuilderExtensions.cs`) is a plain C# extension method on
`IDistributedApplicationBuilder`. It's consumed purely at C# AppHost compile/run time — there
is no manifest entry or language-neutral surface for it. Aspire 13.2 (GA in 13.4) added a
TypeScript AppHost: `apphost.mts` runs on Node.js as a "guest process" and talks to a
background .NET orchestration "host process" over JSON-RPC. Integrations reach guest languages
via **ATS (Aspire Type System)** attributes, which the Aspire CLI scans for in a hosting
assembly to code-generate a typed TypeScript SDK (`.aspire/modules/aspire.mts`) — the same
mechanism official integrations (Redis, Postgres, ...) use, open to third-party packages. This
project already targets `Aspire.Hosting` 13.4.6, so ATS is already available; no dependency
bump is needed.

## Architecture

- Annotate the existing `AddService(this IDistributedApplicationBuilder builder, string name)`
  extension method with `[Aspire.Hosting.AspireExport]`, and its `name` parameter with
  `[Aspire.Hosting.ResourceName]` — confirmed present in `Aspire.Hosting.dll` 13.4.6 (verified
  by inspecting the shipped assembly's type table: `AspireExportAttribute`,
  `ResourceNameAttribute`, alongside `AspireDtoAttribute`, `AspireUnionAttribute`,
  `AspireValueAttribute`, `AspireExportIgnoreAttribute`).
- No new export surface needed for the return type: `IResourceBuilder<IResourceWithServiceDiscovery>`
  is a built-in Aspire "handle" type (`IResourceBuilder<T>` over a core `Aspire.Hosting.ApplicationModel`
  interface) that ATS already understands, because it's exactly what the generated
  `withReference(...)` binding on every other exported resource already consumes. The internal
  `ServiceResource` facade class (`ServiceResource.cs`) is never named in the exported
  signature, so it needs no `[AspireExport]` annotation of its own and stays purely an
  implementation detail — consistent with its existing "deliberately unregistered facade,
  configure the underlying resource instead" design (see its class-level `<remarks>`).
- The build-time validation analyzer, `Aspire.Hosting.Integration.Analyzers`, already ships
  transitively via `buildTransitive/net8.0/Aspire.Hosting.targets` in the `Aspire.Hosting`
  13.4.6 package this project already references (confirmed present in the local NuGet cache)
  — no new `PackageReference` is required to get export-mistake diagnostics at build time.
- `servicesources.yaml` / `servicesources.local.json` continue to be read directly by
  `ServiceCatalogLoader` / `DeveloperConfigLoader` at AppHost run time. That resolution is
  entirely on the .NET host-process side of the JSON-RPC boundary, so it is completely
  unaffected by which language authored the AppHost — no config-schema or loader changes are
  needed for this pass.
- Add a `samples/DemoAppHostTypeScript` project alongside the existing C# `samples/DemoAppHost`,
  built the same way `aspire new` would scaffold a TypeScript AppHost (`apphost.mts`,
  `aspire.config.json` referencing this repo's `Aspire.Hosting.ServiceSources.csproj` by path —
  not a NuGet version — per the multi-language integration authoring docs' testing pattern),
  reusing the same `"url"` source (httpbin.org) demoed in the C# sample since it needs no local
  git checkout or container runtime to smoke-test.

## Why the return type needs no `[AspireDto]`/custom export

`IResourceBuilder<TResource>` handles are opaque references on the guest side — TypeScript code
never inspects their shape, it only threads them into other generated calls
(`withReference(orders)`, `orders.getEndpoint(...)`). ATS's job for a handle type is to give the
guest SDK an identifier it can round-trip over JSON-RPC, not to project its C# members into
TypeScript. Since `IResourceWithServiceDiscovery` is already a first-class exported type
(everything using `WithReference` already relies on it), `AddService` returning it needs no
additional export declaration beyond the method annotation itself.

## Verification Plan

- **Generated SDK inspection**: run `aspire add` (referencing this project by local path) in
  the new TypeScript sample, then inspect `.aspire/modules/aspire.d.ts` to confirm an
  `addService(name: string): Promise<ResourceBuilder<ResourceWithServiceDiscovery>>` (or
  equivalent generated name/shape) appears, and that the build-time analyzer raises no
  diagnostics for `ServiceSourcesBuilderExtensions.cs`.
- **End-to-end smoke test**: `aspire run` the TypeScript sample against the `"url"` source and
  confirm the dashboard shows the resolved service exactly as the C# sample does, and that a
  dependent resource's `WithReference(orders)` equivalent (`builder.addContainer(...).withReference(orders)`
  or similar TS-side call) wires up service discovery identically.
- No changes to existing C# unit tests are required for functional correctness — this pass is
  additive (attributes only). Existing `AddServiceTests.cs` continues to validate the same
  runtime behavior unchanged.

## Outcome

The ATS-level export reasoning above held: `aspire restore` registers `AddService` cleanly,
producing a correctly-shaped `addService(name: string)` binding in the generated TypeScript SDK
with no diagnostics. But that's incomplete regarding guest-language *usability* — Task 2
discovered a separate, upstream gap in the Aspire CLI's TypeScript codegen for methods returning
a bare Aspire interface (`IResourceBuilder<IResourceWithServiceDiscovery>`) rather than a
concrete resource class: the codegen never emits the `*Promise`/`*PromiseImpl` wrapper pair that
every other exported method gets, so the generated SDK fails to compile. Reproduced on both
Aspire CLI 13.4.6 and 13.5.0. This currently blocks the guest-language call path from compiling,
even though the export itself registers correctly. Filed upstream as
[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507); see the README's
"Known issue" section for live status.

Changing `AddService`'s return type to the concrete `ServiceResource` class to route around this
was considered and rejected: it would change the public C# API and contradict `ServiceResource`'s
deliberate "reference-only facade, don't configure me directly" design (see `ServiceResource.cs`'s
class-level `<remarks>`). The right move is waiting for the upstream fix, not a workaround here.

## Explicitly Out of Scope for This Pass

- Exporting anything from the `IServiceSource` implementations (`LocalProjectSource`,
  `KubernetesSource`, `UrlSource`, `ContainerSource`) individually — they're already reached
  through the single `AddService` entry point, so no separate ATS surface is needed for them.
- A Python or Go sample — the ATS mechanism is language-neutral once `AddService` is exported,
  but this pass only proves it out for TypeScript, the only guest language with a shipped
  Aspire CLI codegen path as of 13.4.
- Any change to `servicesources.yaml`/`servicesources.local.json` schema or loaders.
- Publishing/deployment-time (`aspire publish`) behavior — this pass only covers local `aspire run`.
