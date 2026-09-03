# Multi-language local source (#41)

> **Superseded in part by #187, 2026-09-03.** The `ILocalResourceKind`/`AddLocalKind`
> extension point below is unchanged and still how a kind is written. The *packaging* is not:
> there are no satellite packages. A kind lives in
> `KoalaSoft.Aspire.Hosting.ServiceSources` and the language's hosting package is referenced
> with `PrivateAssets="all"`, so it reaches no consumer who has not asked for it — the
> AppHost references it directly. Read the packaging sections here as history; #187 and the
> per-language issues (#46-#50) carry the current shape.

## Problem

`AddService()`'s `"local"` source only knows how to clone a repository and run
a `.csproj`/`.fsproj` via Aspire's `AddProject`. Services written in
JavaScript/TypeScript, Java, or other languages can already be reached via
the `url`, `kubernetes`, and `container` sources, but they can't be cloned
and run locally the way a .NET service can. This tracks GitHub issue #41,
"Import TypeScript and other language projects."

The Aspire ecosystem already has proper integrations for running these
languages locally — `Aspire.Hosting.JavaScript` (`AddViteApp`, `AddNodeApp`,
`AddNextJsApp`, `AddBunApp`, with `.WithNpm()`/`.WithYarn()`/`.WithPnpm()`/
`.WithBun()` modifiers) and `CommunityToolkit.Aspire.Hosting.Java`
(`AddJavaApp`, `AddSpringApp`, with `.WithMavenGoal()` etc., running the
local `java`/Maven-wrapper/Gradle-wrapper command directly). The goal is to
delegate to these rather than reinventing "how to start a Node or Java app."

## Non-goals

- Reimplementing JS/Java process-launch behavior ourselves — we delegate to
  the existing Aspire integrations.
- Changing the `url`/`kubernetes`/`container` sources, which are already
  language-agnostic.
- Forcing every consumer of the core package to take on Node/Java hosting
  dependencies they don't use.

## Architecture

- `ServiceMetadata` gains an optional `Kind` field (string), defaulting to
  `"dotnet"` when omitted. Every existing `servicesources.yaml` keeps
  working unchanged, and the `project` field / `AddProject` behavior is
  untouched for the default case.
- The git clone/checkout logic currently inline in `LocalProjectSource` is
  extracted into a shared, language-agnostic helper (e.g.
  `LocalGitCheckout.ResolveRepoRoot(...)`) that resolves/clones/checks out a
  repository to a directory on disk. This part is identical regardless of
  language and stays fully inside the core package, reused by every kind.
- Core defines a small extension point:
  ```csharp
  public interface ILocalResourceKind
  {
      IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
          IDistributedApplicationBuilder builder,
          string serviceName,
          string repoRoot,
          object? rawConfig); // opaque per-kind yaml block
  }
  ```
  and a registry populated via
  `builder.AddLocalKind(string kind, ILocalResourceKind handler)`.
  Core registers `"dotnet"` itself, backed by today's `AddProject` logic
  reading the existing `project` field — no separate package needed for
  this, since `AddProject` lives in `Aspire.Hosting`, which the core package
  already depends on unconditionally for `IDistributedApplicationBuilder`/
  `IResourceBuilder` themselves. (Considered splitting `dotnet` out into its
  own satellite package for symmetry with JS/Java; rejected — it would add
  an extra package and an extra registration call for the library's most
  common case, for zero dependency savings.)
- The catalog loader captures each service's kind-specific yaml block (e.g.
  everything under a `javascript:` key) as an opaque node — it does not
  attempt to understand JS/Java field names. Each kind's handler
  deserializes its own block into its own options type.
- Two new satellite packages, built in this repo/solution alongside core:
  - `Aspire.Hosting.ServiceSources.JavaScript` (NuGet id
    `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript`), depending on core
    + `Aspire.Hosting.JavaScript`. Exposes `builder.UseJavaScript()`, which
    registers a handler parsing `appDirectory`/`runScript`/`packageManager`
    and calling `AddViteApp`/`AddNpmApp`/etc with the matching `.WithNpm()`/
    `.WithYarn()`/`.WithPnpm()`/`.WithBun()` modifier.
  - `Aspire.Hosting.ServiceSources.Java` (NuGet id
    `KoalaSoft.Aspire.Hosting.ServiceSources.Java`), depending on core +
    `CommunityToolkit.Aspire.Hosting.Java`. Exposes `builder.UseJava()`,
    which registers a handler parsing `workingDirectory`/`mavenGoal`/`port`
    and calling `AddJavaApp`/`AddSpringApp` with `.WithMavenGoal()`/
    `.WithHttpEndpoint()`.
- Referencing an unregistered `kind` in yaml throws
  `ServiceSourcesConfigurationException` naming the service, the unknown
  kind, and which package/call to add — e.g. "Service 'frontend': kind
  'javascript' is not registered. Add the
  `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` package and call
  `builder.UseJavaScript()`."

## Config schema example

```yaml
services:
  orders:                       # unchanged — kind implicit "dotnet"
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj

  frontend:
    repository: https://github.com/example/frontend
    kind: javascript
    javascript:
      appDirectory: .
      runScript: dev
      packageManager: npm       # npm (default) | yarn | pnpm | bun

  java-api:
    repository: https://github.com/example/java-api
    kind: java
    java:
      workingDirectory: .
      mavenGoal: spring-boot:run
      port: 8080
```

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

builder.UseJavaScript();
builder.UseJava();

var orders   = builder.AddService("orders");
var frontend = builder.AddService("frontend");
var javaApi  = builder.AddService("java-api");
```

## Error handling

- Unregistered `kind` → `ServiceSourcesConfigurationException` as shown
  above.
- Malformed per-kind config (e.g. an invalid `packageManager` value) is the
  registered handler's own responsibility to validate and throw
  `ServiceSourcesConfigurationException` for. Core does not pre-validate
  blocks it doesn't understand.
- Git clone/checkout failures are unaffected — that logic doesn't move, so
  existing error paths (`LocalProjectSource`'s clone/checkout exceptions,
  now on the shared `LocalGitCheckout` helper) stay the same for every kind.

## Testing

- Core: existing dotnet-path tests are unaffected (no yaml/behavior change
  for them). Add tests for the registry itself — unknown-kind error, and a
  fake test `ILocalResourceKind` to verify registration/dispatch — with no
  real JS/Java dependency in core's test project.
- Each satellite package carries its own test suite for its own config
  parsing and resource-creation call, independent of core and of each
  other.

## Backward compatibility

Fully additive: `kind` is optional and defaults to `"dotnet"`; existing
`project`/`path`/`ref` fields and `AddProject` behavior are untouched. No
existing `servicesources.yaml` needs any change.

## TypeScript/guest-language AppHost compatibility

Following up from #42 (exporting `AddService()` to Aspire's Type System so it
can be called from a TypeScript AppHost): `builder.UseJavaScript()` and
`builder.UseJava()` need the same `[AspireExport]` treatment `AddService` got
in #42 — no `[ResourceName]` needed since neither takes a name parameter.

This is a one-line addition per method, not a design change: the part of ATS
that's genuinely hard — exporting delegates/callback objects across the
guest/host JSON-RPC boundary — never comes into play here. `ILocalResourceKind`
and `builder.AddLocalKind(kind, handler)` are internal registry machinery;
each satellite package constructs its own handler and registers it *inside*
its own extension method body, entirely on the .NET host-process side. A
TypeScript AppHost author would never see or implement `ILocalResourceKind`
— they'd just call `useJavaScript()` then `addService("frontend")`, same
shape as the plain `AddService`-only case.

The `kind`/`javascript:`/`java:` yaml config in `servicesources.yaml` is read
host-side regardless of AppHost language, so no config-schema changes are
needed for TS compatibility either.

## Repo layout

New `src/Aspire.Hosting.ServiceSources.JavaScript` and
`src/Aspire.Hosting.ServiceSources.Java` projects are added to
`ServiceSources.slnx` alongside the core project, each with their own
`.csproj`, own NuGet package, and own `test/` folder — built, tested, and
released from this repo's existing CI pipeline.

## Adding a satellite package

Each language is one package, and the pattern below is what #44 (JavaScript)
and #45 (Java) both follow. The first six steps are the package itself; the
last three are release wiring that is easy to forget, because each is a
hard-coded list that a new package has to be added to by hand.

1. `src/Aspire.Hosting.ServiceSources.<Lang>/` with a `.csproj` copied from an
   existing satellite: same `TargetFrameworks` (`net8.0;net9.0;net10.0`),
   `PackageId` `KoalaSoft.Aspire.Hosting.ServiceSources.<Lang>`, MinVer with
   `MinVerTagPrefix` `v`, symbols on, and `LICENSE`/`README.md` packed in. It
   references core by project reference and the language's hosting integration
   by package reference — never the other way round, so a consumer of core
   never takes on a hosting dependency it doesn't use.
2. `AssemblyInfo.cs` with `InternalsVisibleTo` for the package's own test
   project, so the handler and its options type can stay `internal`.
3. An options record for the kind's yaml block, parsed with
   `LocalKindConfig.Parse<T>` so an unknown property is rejected by name.
4. An `ILocalResourceKind` implementation. `Validate` takes the options block
   alone and runs immediately before that service's `Resolve` and ahead of its
   checkout, so a typo is caught without paying for a clone; anything needing
   the repository on disk (path existence, containment in the checkout) has to
   run from `Resolve`, which is the only one given `repoRoot`. Any path the
   block can name — an app directory, a script or project file — must be
   resolved against the checkout and rejected if it lands outside it, or a
   catalog entry can run something the service doesn't own.
5. A `Use<Lang>()` extension calling `builder.AddLocalKind(...)`, carrying
   `[AspireExport]` so a TypeScript AppHost can call it too. If it ever takes
   parameters, they need the same ATS treatment `AddService` got in #42.
6. `test/Aspire.Hosting.ServiceSources.<Lang>.Tests/` — its own suite, run
   independently of core's, plus an entry for both new projects in
   `ServiceSources.slnx`, and README coverage of the new `kind` and its block.
7. `.github/workflows/preview.yml` → the `Pack` step. Packing is project by
   project rather than solution-wide, because the solution also holds the
   sample AppHost and the test projects, which must never reach a feed. A
   package missing from this list is simply never published.
8. `.github/workflows/release.yml` → the same `Pack` step, separately. The two
   workflows do not share it.
9. `.github/workflows/release.yml` → the `prune-previews` matrix. `preview.yml`
   publishes a preview of every packed package on every push to `main`, and
   this job is the only thing that trims them; a package packed in step 7 but
   missing here accumulates preview versions without bound. The matrix exists
   because `actions/delete-package-versions` takes a single `package-name`, and
   it runs `fail-fast: false` because a package with no previews yet — every
   satellite's first release — fails its own prune, and must not cancel the
   jobs pruning the packages that do have previews.
