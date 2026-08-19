# Multi-language local source (#41)

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
  `builder.AddLocalResourceKind(string kind, ILocalResourceKind handler)`.
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
    + `Aspire.Hosting.JavaScript`. Exposes
    `builder.AddJavaScriptServiceSourceKind()`, which registers a handler
    parsing `appDirectory`/`runScript`/`packageManager` and calling
    `AddViteApp`/`AddNpmApp`/etc with the matching `.WithNpm()`/
    `.WithYarn()`/`.WithPnpm()`/`.WithBun()` modifier.
  - `Aspire.Hosting.ServiceSources.Java` (NuGet id
    `KoalaSoft.Aspire.Hosting.ServiceSources.Java`), depending on core +
    `CommunityToolkit.Aspire.Hosting.Java`. Exposes
    `builder.AddJavaServiceSourceKind()`, which registers a handler parsing
    `workingDirectory`/`mavenGoal`/`port` and calling `AddJavaApp`/
    `AddSpringApp` with `.WithMavenGoal()`/`.WithHttpEndpoint()`.
- Referencing an unregistered `kind` in yaml throws
  `ServiceSourcesConfigurationException` naming the service, the unknown
  kind, and which package/call to add — e.g. "Service 'frontend': kind
  'javascript' is not registered. Add the
  `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` package and call
  `builder.AddJavaScriptServiceSourceKind()`."

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

builder.AddJavaScriptServiceSourceKind();
builder.AddJavaServiceSourceKind();

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

## Repo layout

New `src/Aspire.Hosting.ServiceSources.JavaScript` and
`src/Aspire.Hosting.ServiceSources.Java` projects are added to
`ServiceSources.slnx` alongside the core project, each with their own
`.csproj`, own NuGet package, and own `test/` folder — built, tested, and
released from this repo's existing CI pipeline.
