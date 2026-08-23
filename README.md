# Aspire.Hosting.ServiceSources

[![NuGet](https://img.shields.io/nuget/v/KoalaSoft.Aspire.Hosting.ServiceSources?logo=nuget&label=nuget)](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources)
[![Downloads](https://img.shields.io/nuget/dt/KoalaSoft.Aspire.Hosting.ServiceSources?logo=nuget&label=downloads)](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/flojon/aspire-servicesources/blob/main/LICENSE)

A .NET Aspire AppHost extension that lets `builder.AddService("orders")` resolve to a real,
running resource whose *source* is chosen per developer, not baked into the AppHost.

## Why

`AddProject<T>()` assumes a service lives in the AppHost's own solution. In a real
microservice environment, services live in separate repositories, and different developers
want different things for the same service: clone it locally to edit, run it from an
already-checked-out working copy, reach an instance already running in a shared Kubernetes
dev cluster, hit a fixed URL, or just run a published container image. The AppHost should
only describe *what* it depends on; where that dependency actually comes from is a
per-developer choice, made without ever touching the AppHost's `.csproj`/`.sln`.

`AddService()` is the seam: the AppHost calls it once per service, and a developer-local
config file decides how it's actually resolved — a managed or self-managed local git
checkout (`"local"`), a `kubectl port-forward` against a dev cluster (`"kubernetes"`), a
fixed, already-known URL (`"url"`), or a published container image run locally
(`"container"`) — behind one stable return type, so the AppHost code never has to change
when a developer switches sources.

## Install

Published on nuget.org as [`KoalaSoft.Aspire.Hosting.ServiceSources`](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources).
If every service your AppHost declares is a .NET project, this is the only package you need:

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

Services that aren't .NET projects need the satellite package for their language, so an
AppHost only takes on the hosting dependencies it actually uses — see
[Non-.NET local services](#non-net-local-services-kind):

| Language | Package |
| --- | --- |
| Java | `KoalaSoft.Aspire.Hosting.ServiceSources.Java` |
| JavaScript | `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` |

A satellite already depends on the core package, so add it *instead of* the core package
rather than alongside it — restore brings the matching core in for you:

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript
```

Two direct references mean two versions to move in step, because a satellite accepts core
only within its own minor: bump one and not the other and restore fails with `NU1107`. A
single reference has nothing to keep in step. Add a satellite per language you use; core
still arrives once, transitively.

Or reference the project directly from your AppHost instead:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj" />
</ItemGroup>
```

Requires .NET 8 or later (net8.0, net9.0, and net10.0 are all supported) and an AppHost
project using the `Aspire.AppHost.Sdk` (`aspire new` / `aspire restore` sets this up).

Every release is listed in the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md), which
is where breaking changes and their migrations are recorded. Check it before upgrading —
while the version is below `1.0.0`, a breaking change can ship in a minor release.

### Preview builds

Every push to `main` publishes a prerelease build (`0.x.y-alpha.0.N`) to GitHub Packages.
Stable releases go to nuget.org only — use those unless you specifically need an unreleased
fix. Previews are pruned after each release — only the five most recent are kept — so treat
them as disposable and never pin one in a long-lived project.

GitHub's NuGet registry requires authentication for every download, even for public
packages — unlike the container registry, it has no anonymous access. This is *not* a grant
on this repository: any authenticated GitHub user can download a public package, so all you
need is a token on your own account. It must be a **classic** personal access token with the
`read:packages` scope; fine-grained tokens are not supported by GitHub Packages.

```bash
dotnet nuget add source https://nuget.pkg.github.com/flojon/index.json \
  --name servicesources-preview --username <your-github-username> --password <your-pat>
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources --prerelease
```

## Getting started

**1. Declare the service in `Program.cs`:**

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(orders);

builder.Build().Run();
```

**2. Add the shared catalog, `servicesources.yaml`, next to the AppHost project (commit this
file):**

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

(A service that isn't a .NET project also takes a `kind` — see
[Non-.NET local services](#non-net-local-services-kind).)

**3. Add your own `servicesources.local.json` next to it (gitignore this file — it's
per-developer):**

```json
{
  "services": {
    "orders": { "source": "local" }
  }
}
```

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

### `"local"` source options

```json
{
  "services": {
    "orders": { "source": "local" },
    "payments": {
      "source": "local",
      "path": "/home/dev/code/payments",
      "ref": "feature/new-checkout"
    }
  }
}
```

- Omit `path` for a managed checkout: cloned once into
  `<AppHostDirectory>/.servicesources/checkouts/<serviceName>/`, and reconciled to the
  configured `ref` (or the catalog's `defaultRef`) on every run. Uncommitted edits are never
  discarded — if the checkout is dirty and the ref changed, resolution fails loudly instead of
  overwriting your work. Anything you put at that path yourself that isn't a plain clone — a linked
  `git worktree`, or a clone made with `--separate-git-dir` — is refused with an explanation rather
  than replaced; point at it with `path` instead. A directory there with no `.git` entry at all is
  treated as debris from an interrupted clone and **deleted**, so don't hand-place a plain directory
  as a quick override — use `path` for that too. The `.servicesources/` directory gitignores itself
  on first use — no need to add it to your own `.gitignore`.
- Set `path` to point at a checkout you manage yourself (e.g. an existing local clone). It's
  used as-is — no clone, no checkout, no fetch, ever. A relative `path` is anchored to the
  AppHost directory, and must name a directory that already exists. `ref` cannot be combined
  with `path`.
- Keep the file to the services you actually add. `AddService()` has to hand back the real
  resource, so it can't wait until the AppHost has finished composing to find out which services
  it wants — the first call clones the checkouts for *every* `"local"` entry, in parallel. Only the
  services you actually add are then reconciled to their configured `ref`: a checkout that already
  exists is never touched on behalf of an entry you don't `AddService()`, so work in progress on a
  branch there is safe. Entries you never add still cost network and disk for that first clone. The
  AppHost logs which ones those were at startup — and warns if one of them failed, since nothing
  else would ever tell you — so you know what to drop.

#### Several services from one repository

A catalog entry maps one service to one thing to run, so a repository holding several services
gets one entry per service — each naming the same `repository`, and each selecting its own part
of the tree (`project` for the default `dotnet` kind, or the kind's own options block, such as
`appDirectory`, for the kinds below):

```yaml
services:
  orders:
    repository: https://github.com/example/monorepo
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main
  payments:
    repository: https://github.com/example/monorepo
    project: src/Payments.Api/Payments.Api.csproj
    defaultRef: main
```

The catalog is the same either way; what differs is how many checkouts of that repository end
up on your machine, which each developer chooses in `servicesources.local.json`:

- **One managed checkout per service** — omit `path`. Managed checkouts are keyed by service
  name, so `orders` and `payments` each get their own independent clone of the repository, at
  `.servicesources/checkouts/orders/` and `.servicesources/checkouts/payments/`. Each can sit
  on its own `ref` and neither can disturb the other, but the repository is cloned once per
  service, and an edit to shared code in one checkout is invisible to the other.
- **One checkout shared by every service** — set `path`. Clone the repository yourself, then
  point each service at that same directory; the entry's `project` (or `appDirectory`) is
  resolved relative to it:

  ```json
  {
    "services": {
      "orders":   { "source": "local", "path": "/home/dev/code/monorepo" },
      "payments": { "source": "local", "path": "/home/dev/code/monorepo" }
    }
  }
  ```

  This is usually what you want when the services share code: one clone, one branch, and an
  edit to a shared project is picked up by every service at once. The trade-off is that the
  clone is yours to manage — nothing is ever cloned, fetched or checked out on your behalf —
  and `ref` cannot be combined with `path`.

Mixing the two is fine: services you're actively editing can share one `path` checkout while
the rest stay on managed clones.

### Non-.NET local services: `kind`

A `"local"` service is resolved as a .NET project by default. Set `kind` in the catalog to run
the checkout some other way — the git clone/checkout is identical, only what gets built out of
the resulting directory changes:

```yaml
services:
  frontend:
    repository: https://github.com/example/frontend
    kind: javascript          # optional; defaults to "dotnet"
    javascript:               # per-kind options block, named after the kind
      appDirectory: .
      runScript: dev
```

`kind: dotnet` (the default) uses the entry's `project` property and needs no options block.
Any other kind is resolved by a handler that a satellite package registers, and its options
live in a block named after the kind. Kind names are matched case-sensitively, and a kind with
no registered handler fails at that service's `AddService()` call, before its checkout is used.

#### JavaScript: `kind: javascript`

Provided by the `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` package, which runs the
checkout through [`Aspire.Hosting.JavaScript`](https://www.nuget.org/packages/Aspire.Hosting.JavaScript).
Install it, then call `UseJavaScript()` once, before the first `AddService()` call:

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

builder.UseJavaScript();

var frontend = builder.AddService("frontend");
```

```yaml
services:
  frontend:
    repository: https://github.com/example/frontend
    kind: javascript
    javascript:
      appType: vite         # javascript (default) | vite | nextjs | node | bun
      appDirectory: web     # directory holding package.json, relative to the repo root
      runScript: dev        # package.json script to run
      packageManager: pnpm  # npm | yarn | pnpm | bun
      port: 4321            # the port consumers reach the service on
```

Every option is optional:

- **`appType`** — which integration runs the app: `javascript` (the default, `AddJavaScriptApp`),
  `vite`, `nextjs`, `node`, or `bun`. `node` and `bun` execute a file directly rather than a
  `package.json` script, so they require `scriptPath`; the other three run a script and reject it.
- **`appDirectory`** — the directory holding the app's `package.json`, relative to the repository
  root, which is also the default. It must stay inside the checkout, and — for every app type that
  runs a `package.json` script — it is checked to actually hold one, so pointing it at the wrong
  directory of a monorepo is reported against the service rather than surfacing later as an npm
  `could not read package.json`.
- **`runScript`** — the `package.json` script to run; the integrations default this to `dev`. For
  `node`/`bun` it overrides the `scriptPath` they would otherwise execute directly, which needs a
  `package.json` in `appDirectory` — without one those two app types run `scriptPath` and nothing
  else, so a `runScript` set there is rejected rather than silently ignored.
- **`scriptPath`** — the entry-point file (e.g. `server.js`) relative to `appDirectory`. Required
  by `appType: node` and `appType: bun`, and rejected for the others. Like `appDirectory` it must
  stay inside the checkout, and it is checked to exist so a typo is reported against the service
  rather than surfacing later as a `cannot find module` crash.
- **`packageManager`** — `npm`, `yarn`, `pnpm`, or `bun`, used to install dependencies before the
  app starts (a fresh clone has no `node_modules`). Left unset, the integration's own default
  applies: npm for most app types, Bun for `appType: bun`.
- **`port`** / **`targetPort`** — the port consumers reach the service on, and the port the app
  itself listens on. Both are allocated by Aspire when unset.
- **`portEnv`** — the environment variable the app reads its listen port from; defaults to `PORT`.
  Rejected for `vite`/`nextjs`, whose integrations bind the dev server's port themselves.

The service always gets an `http` endpoint, so the builder `AddService()` returns can be passed to
a consumer's `WithReference(...)` like any other. Node and Bun must be on `PATH` for the app types
that use them.

#### Java: `kind: java`

Provided by the `KoalaSoft.Aspire.Hosting.ServiceSources.Java` package, which runs the checkout
through the .NET Aspire Community Toolkit's
[Java integration](https://github.com/CommunityToolkit/Aspire). Install it, then call `UseJava()`
once, before the first `AddService()` call — `AddService()` resolves eagerly, so a `kind: java`
service registered after it has already run has nowhere to look up its handler:

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

builder.UseJava();

var catalog = builder.AddService("catalog");
```

`servicesources.yaml`:
```yaml
services:
  catalog:
    repository: https://github.com/example/catalog
    kind: java
    java:
      mavenGoal: spring-boot:run
      port: 8080
```

The checkout is cloned exactly as for any other `"local"` service (`path`, `ref`, and
`defaultRef` all behave identically), then handed to that integration to run.

**`java:` block options**

| Field | Required | Description |
| --- | --- | --- |
| `mavenGoal` | one of these three | Run via the Maven wrapper, e.g. `spring-boot:run`. |
| `gradleTask` | one of these three | Run via the Gradle wrapper, e.g. `bootRun`. |
| `jarPath` | one of these three | Run a pre-built jar with `java -jar`, relative to `workingDirectory`. May climb out of it — a monorepo's shared build output directory — but must stay inside the checkout. |
| `port` | yes | The port the app listens on. Becomes the service's HTTP endpoint, so consumers can `WithReference(...)` it. |
| `workingDirectory` | no (defaults to the repository root) | Where in the checkout the project lives — the directory holding `pom.xml` / `build.gradle`, and by default the `mvnw`/`gradlew` wrapper too. Must stay inside the checkout. |
| `wrapperPath` | no (defaults to the wrapper in `workingDirectory`) | Where the `mvnw`/`gradlew` wrapper script lives, relative to the **repository root** — for the monorepo that commits a single wrapper at its root while the service itself sits further down. Name it without an extension (`gradlew`, not `gradlew.bat`) and it works for the whole team: on Windows the `.cmd`/`.bat` wrapper beside it is the one run. Only meaningful with `mavenGoal` or `gradleTask`. |
| `args` | no | Extra arguments for whichever run mode is configured — passed to the Maven wrapper, the Gradle wrapper, or the jar. |

`mavenGoal`, `gradleTask`, and `jarPath` are mutually exclusive: exactly one must be set. A
monorepo service, running a Gradle task with an extra argument:

```yaml
services:
  catalog:
    repository: https://github.com/example/monorepo
    kind: java
    java:
      workingDirectory: services/catalog
      gradleTask: bootRun
      wrapperPath: gradlew
      args: ["--args=--spring.profiles.active=dev"]
      port: 8080
```

A multi-project Gradle repository (like a multi-module Maven one) commits a single wrapper at its
root rather than one per project, which is what `wrapperPath: gradlew` names here — without it the
wrapper is looked for in `services/catalog`, beside the project.

`mavenGoal` and `gradleTask` run the repository's own `mvnw`/`gradlew` wrapper, so a JDK must be
on the developer's machine but Maven/Gradle itself need not be. That wrapper has to be in the
checkout — there is no fallback to a system-wide `mvn`/`gradle` — so a checkout without one is
reported as such, rather than left to surface as a failure to start the app. On Windows the wrapper
run is `mvnw.cmd`/`gradlew.bat`, whether it was found by default or named by `wrapperPath`: the
extensionless scripts beside them are POSIX shell scripts that Windows cannot exec.

Every problem with the block bar two — unknown properties, a missing or out-of-range `port`, no run
mode or more than one, a `workingDirectory`, `wrapperPath` or `jarPath` escaping the repository, a
`wrapperPath` set alongside `jarPath` — is reported by the `AddService("catalog")` call itself,
before the service has added anything to the app model. The two exceptions are a `workingDirectory`
that doesn't exist in the checkout and a wrapper script that isn't there: both need the checkout on
disk, which isn't cloned until the block itself has been checked, so they are reported a moment
later, once the resource is being created.

**Reaching the rest of the Java integration.** The `java:` block covers how to start the app; it
deliberately doesn't mirror every modifier the Community Toolkit offers. Anything else is reachable
from the AppHost with `As<JavaAppExecutableResource>()`, which hands back the real resource builder:

```csharp
builder.AddService("catalog")
    .As<JavaAppExecutableResource>()
    .WithMavenBuild()                      // compile before starting
    .WithJvmArgs(["-Xmx512m"])
    .WithOtelAgent("/path/to/opentelemetry-javaagent.jar");
```

Use `Configure<T>(...)` instead for anything that should survive a developer switching that service
to a non-`local` source — `As<T>()` throws if the service no longer resolves to a Java resource,
which is the point when the AppHost genuinely requires one.

`UseJava()` is exported to Aspire's Type System, so a TypeScript AppHost can call `useJava()`
before `addService(...)` the same way.

#### Implementing a kind

A satellite package implements `ILocalResourceKind` and registers it from its own extension
method:

```csharp
public sealed class JavaScriptKind : ILocalResourceKind
{
    private sealed class Options
    {
        public string? AppDirectory { get; set; }
        public string? RunScript { get; set; }
    }

    // Optional, and worth implementing whenever Resolve parses rawConfig: this runs immediately
    // before Resolve, and before this service's checkout, so a typo'd options block is reported
    // without a half-created resource behind it and without paying for a clone first.
    public void Validate(string serviceName, object? rawConfig) =>
        LocalKindConfig.Parse<Options>(rawConfig, serviceName);

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        // repoRoot is the already-cloned, already-checked-out directory.
        var options = LocalKindConfig.Parse<Options>(rawConfig, serviceName);
        ...
    }
}

public static IDistributedApplicationBuilder UseJavaScript(this IDistributedApplicationBuilder builder) =>
    builder.AddLocalKind("javascript", new JavaScriptKind());
```

`LocalKindConfig.Parse<T>` turns the opaque options block into a typed object, and rejects an
unknown property or a block that isn't a mapping with a `ServiceSourcesConfigurationException`
naming the service. `AddLocalKind` must be called before the `AddService()` call for a service of
that kind — resolution is eager, so registering later is too late — accepts each kind name at most
once, and cannot re-register `"dotnet"` or use a
name that collides with a well-known service property (`repository`, `project`, `defaultRef`,
`kind`, `kubernetes`, `url`, `container`) — a block by one of those names would be read as that
property rather than as the kind's options.

#### Private repositories

Clone and fetch for a managed checkout (no `path` override) authenticate the same way, in order:

1. **Your `git` credential helper.** The managed checkout shells out to `git credential fill` for
   the repository's host, so whatever you already have configured — Git Credential Manager,
   `osxkeychain`, `libsecret`, a cached PAT, a `.netrc`-backed helper — is reused automatically.
   Nothing to configure here beyond having `git` on `PATH` with a working credential helper (run
   `git credential fill` yourself against the same host to confirm it resolves before wiring it
   up here).
2. **`SERVICESOURCES_GIT_USERNAME`/`SERVICESOURCES_GIT_TOKEN` environment variables**, if the
   helper above yields nothing (e.g. no helper configured, or `git` isn't on `PATH`) — or if what
   it yielded was refused, see below. `SERVICESOURCES_GIT_TOKEN` alone is enough for hosts that
   accept any username alongside a personal access token (GitHub, GitLab, Azure DevOps); set
   `SERVICESOURCES_GIT_USERNAME` too if your host requires a specific one.

The order is a ladder, not a one-shot choice: if the host refuses the credential your helper
supplied, the environment variables are tried next, and only then the request is left
unauthenticated. Each credential is offered once per clone or fetch — a refused one is never
replayed.

A credential the host actually refuses is also reported back to your helper with
`git credential reject`, exactly as `git` itself does, so Git Credential Manager, `osxkeychain`,
`libsecret` and friends erase their stored copy and resolve afresh next time instead of serving the
same dead token on every run. That only happens on an outright rejection of the credential
(HTTP 401); a "not found" answer never erases anything, since a repository your credential simply
can't see is at least as likely an explanation as a bad credential. Rotating a token therefore
takes effect on the next resolution — there's no need to restart the AppHost to clear a cached one.

Credentials are never read from `servicesources.yaml` (committed) or `servicesources.local.json`
— there's no field for them in either file, by design, so a secret can't accidentally end up in
the committed catalog. The one way to get one in there anyway is to embed it in the `repository`
URL itself (`https://user:token@host/org/repo`); git accepts that form, but it commits the token
along with the catalog, so use one of the two mechanisms above instead. Should such a URL be
configured regardless, every message this tool prints strips the userinfo from it first, so the
token doesn't spread from the catalog into your console and logs.

A clone or fetch that fails for what looks like an authentication reason raises an error naming
the service, the repository, and authentication as the likely cause, rather than a generic
"failed to clone" message. This includes a "not found" response: GitHub, GitLab and Azure DevOps
all answer an unauthenticated request for a private repository with 404 rather than 401, so as not
to leak whether it exists, so the error covers both readings — bad credentials, or a repository
the credentials in use can't see. A rate-limited response is deliberately left out, even though
hosts answer it with the same `403` as a token that's missing a scope: there the credential is
fine and the fix is to wait, so it's reported as the transport failure it is.

**SSH is not supported.** LibGit2Sharp's bundled native binaries don't include an SSH transport,
so a `repository` written as `git@host:org/repo`, `host:org/repo` or `ssh://...` fails fast at
resolution time with a message pointing at the HTTPS equivalent — use `https://host/org/repo`
instead. The same check covers an existing checkout whose `origin` is an SSH remote, before any
fetch is attempted against it.

### `"kubernetes"` source

Point a service at an already-running instance in a Kubernetes dev cluster via
`kubectl port-forward`, instead of running it locally at all.

`servicesources.yaml`:
```yaml
services:
  orders:
    kubernetes:
      service: orders-svc
      port: 8080
```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": {
      "source": "kubernetes",
      "context": "dev-west",
      "namespace": "orders",
      "port": 8080
    }
  }
}
```

Requires `kubectl` on `PATH`, authenticated against the named `context`.

### `"url"` source

Point a service at a fixed, already-known URL — e.g. a Kubernetes ingress, a staging
deployment, or any other reachable HTTP(S) endpoint. There's no underlying resource for
Aspire to run; the endpoint resolves straight to the configured URL.

Two consequences follow from the service running out of band: the AppHost's
[`Configure` calls are skipped and logged](#configuring-a-resolved-service), and a **container**
can't `WithReference` it — a project or executable can — which fails with a clear error rather than
a DCP stack trace. See [#58](https://github.com/flojon/aspire-servicesources/issues/58).

`servicesources.yaml`:
```yaml
services:
  orders:
    url:
      url: https://orders.example.com
```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": { "source": "url" }
  }
}
```

Set `url` in the developer config instead to override the catalog's URL for just that
developer (e.g. pointing at a personal tunnel or local proxy):

```json
{
  "services": {
    "orders": { "source": "url", "url": "https://orders.dev.internal" }
  }
}
```

### `"container"` source

Run a published container image locally via Aspire's own container-runtime integration —
image pull and lifecycle are managed entirely by Aspire.

`servicesources.yaml`:
```yaml
services:
  orders:
    container:
      image: ghcr.io/company/orders
      port: 8080
      defaultTag: latest
```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": { "source": "container" }
  }
}
```

Set `tag` in the developer config to override the catalog's `defaultTag` for just that
developer:

```json
{
  "services": {
    "orders": { "source": "container", "tag": "v1.4.2" }
  }
}
```

### Combining sources on one catalog entry

A single `servicesources.yaml` entry can carry blocks for every source at once — the catalog
just describes *how* each source would resolve the service; each developer's
`servicesources.local.json` picks which one actually applies to them:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    kubernetes:
      service: orders-svc
      port: 8080
    url:
      url: https://orders.example.com
    container:
      image: ghcr.io/example/orders
      port: 8080
      defaultTag: latest
```

A developer editing the service picks `"local"`; one debugging against a shared dev cluster
picks `"kubernetes"`; one who just needs it reachable picks `"url"` or `"container"` — same
catalog entry, same `AddService("orders")` call in the AppHost, no code changes either way.
Each developer's own `servicesources.local.json` just names which source applies to them —
editing `orders` locally:

```json
{ "services": { "orders": { "source": "local" } } }
```

debugging against a shared dev cluster:

```json
{ "services": { "orders": { "source": "kubernetes", "context": "dev-west", "namespace": "orders", "port": 8080 } } }
```

or just needing it reachable, not caring how:

```json
{ "services": { "orders": { "source": "url" } } }
```

## Configuring a resolved service

`AddService()` returns a builder over the **real** resource Aspire runs, so the AppHost can inject
its own configuration — connection strings, generated secrets, a sibling's endpoint, wait ordering.
Values like these come from the AppHost's own graph and can't be written into
`servicesources.yaml`/`servicesources.local.json`.

The resolved resource's type depends on the source, which each developer chooses, so name the
capability you need and it is checked at composition time:

```csharp
var backend = builder.AddService("backend")
    .Configure<IResourceWithEnvironment>(r => r
        .WithReference(planningDb)
        .WithEnvironment("DBPASSWORD", postgres.Resource.PasswordParameter)
        .WithEnvironment("ENCRYPTIONKEY", builder.AddParameter("EncryptionKey", new GenerateParameterDefault(), secret: true))
        .WithEnvironment("Services__CommonAuth", commonAuth.GetEndpoint("https")))
    .Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(migrationService));
```

`As<T>()` is the same cast without the callback, and reaches anything `Configure` would — including
a satellite kind's own extension methods:

```csharp
backend.As<JavaScriptAppResource>().WithRunScript("dev");
```

**`Configure` is skipped for the `"url"` and `"kubernetes"` sources**, and the skip is logged at
startup. Both resolve to something already running elsewhere — a `"url"` service has no local
process at all, and a `"kubernetes"` service is a `kubectl port-forward` in front of a remote one,
so environment variables applied here would configure `kubectl` rather than the service. Those
services are expected to be configured wherever they actually run.

The one exception is **wait ordering on a `"kubernetes"` service**, which still applies:
`Configure<IResourceWithWaitSupport>` (and `WaitForService` / `WaitForServiceCompletion`) reach a
real, registered `kubectl port-forward` executable, and holding *that* back until a migration
finishes is exactly what the AppHost asked for. Only configuration that would land on the wrong
process is dropped. A `"url"` service skips wait ordering too, since it has no registered resource
for Aspire to hold back.

Skipping rather than failing is deliberate: a developer switching a service to a remote source in
their own `servicesources.local.json` must not break a `Program.cs` they don't own. You'll see:

```
warn: Aspire.Hosting.ServiceSources
      Service 'backend': skipped Configure<IResourceWithEnvironment> because its source is
      'kubernetes' — it resolves to a 'kubectl port-forward' in front of an already-running
      service, so the configuration would reach kubectl rather than the service. ...
```

`As<T>()` **throws** for those sources instead of skipping — it has to return a builder, and handing
back the `kubectl` executable would silently configure the wrong process. Prefer `Configure` for
anything that should survive a source switch. It follows the same wait-ordering exception:
`As<IResourceWithWaitSupport>()` on a `"kubernetes"` service returns the port-forward's builder
rather than throwing.

### From a guest-language AppHost

`Configure<T>` is generic, and Aspire's Type System projects a generic method with its type
parameter erased — so guest languages get a set of non-generic equivalents instead, one per shape
(overloads don't survive codegen either):

```typescript
const payments = await builder
  .addService('payments')
  .withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withServiceReference(inventory);
```

| TypeScript | C# equivalent |
|---|---|
| `withServiceEnvironment(name, value)` | `.Configure<IResourceWithEnvironment>(r => r.WithEnvironment(name, value))` |
| `withServiceEnvironmentFromParameter(name, parameter)` | `…WithEnvironment(name, parameter)` |
| `withServiceEnvironmentFromEndpoint(name, endpoint)` | `…WithEnvironment(name, endpoint)` |
| `withServiceReference(other)` | `…WithReference(other)` |
| `withServiceConnectionString(source)` | `…WithReference(source)` |
| `waitForService(dependency)` | `.Configure<IResourceWithWaitSupport>(r => r.WaitFor(dependency))` |
| `waitForServiceCompletion(dependency, { exitCode })` | `…WaitForCompletion(dependency, exitCode)` |
| `withServiceArg(arg)` | `.Configure<IResourceWithArgs>(r => r.WithArgs(arg))` |

They delegate to `Configure<T>`, so out-of-band sources are skipped and logged exactly as above —
including the wait-ordering exception, which `waitForService` and `waitForServiceCompletion` inherit.
In C# they're hidden from IntelliSense — use `Configure<T>`, which reaches every Aspire extension
method rather than just these.

## Sample

`samples/DemoAppHost` is a minimal working AppHost demonstrating all three easily-runnable
sources: `orders` via a real managed `"local"` git checkout (a small project cloned from
[`dotnet/aspire-samples`](https://github.com/dotnet/aspire-samples)), `inventory` via the
`"url"` source (pointing at [httpbin.org](https://httpbin.org), a live public test API), and
`payments` via the `"container"` source (the `nginxdemos/hello` hello-world image) — run it to
see the whole flow end to end. (`"kubernetes"` isn't demoed here since it needs a real cluster
and `kubectl`; see its section above.)

It also carries a `catalog` service showing `kind: java` — a `"local"` checkout of
[Spring PetClinic](https://github.com/spring-projects/spring-petclinic) run with its own Maven
wrapper. `builder.UseJava()` is wired up, but `AddService("catalog")` is commented out and the
service is left out of `servicesources.local.json.example`, since unlike the three above it needs a
JDK. To run it, do both: uncomment the call and add `"catalog": { "source": "local" }` to your
`servicesources.local.json`. Leaving it out of that file by default is what keeps the sample from
cloning PetClinic on every run — the first `AddService` prefetches every `"local"` entry there,
whether or not you add it.

```bash
cd samples/DemoAppHost
cp servicesources.local.json.example servicesources.local.json
aspire run
```

A TypeScript AppHost equivalent — proving `AddService()` is correctly exported and registers with
Aspire's Type System from a guest language, and that a resolved service can be
[configured from TypeScript](#from-a-guest-language-apphost) — lives in
`samples/DemoAppHostTypeScript`. Both of its services use the `"container"` source so that
`payments` can `withServiceReference(inventory)`: a `"url"` service runs out of band, and a
container consumer of one is [rejected up front](#url-source). (**Note:** this sample does not
currently run end-to-end — see the known issue below the code block for why.)

```bash
cd samples/DemoAppHostTypeScript
npm install
cp servicesources.local.json.example servicesources.local.json
aspire restore
aspire run
```

**Known issue:** as of Aspire CLI 13.4.6/13.5.0, `aspire restore`/`aspire add` correctly
registers `addService(name: string)` in the generated TypeScript SDK (`.aspire/modules/aspire.mts`)
with no diagnostics — confirming Task 1's `[AspireExport]` on `AddService` works — but the
generated SDK fails to compile (`TS2552: Cannot find name 'ResourceWithServiceDiscoveryPromise'`)
because the Aspire CLI's TypeScript codegen doesn't emit a `*Promise`/`*PromiseImpl` wrapper pair
for extension methods that return a bare Aspire interface type
(`IResourceBuilder<IResourceWithServiceDiscovery>`) rather than a concrete resource class. This
appears to affect any integration whose exported method returns a bare Aspire interface rather
than a concrete resource class, though we've only confirmed it for this one. Tracked upstream at
[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507); until that's fixed,
`aspire run` on this sample fails at its TypeScript build step even though the export itself is
correctly registered.

## Status

Early stage, evolving fast. `"local"`, `"kubernetes"`, `"url"`, and `"container"` sources are
all implemented — see [`docs/superpowers/`](docs/superpowers/) for design and implementation
history, including the phase 2 backlog (repo auto-update, config discovery walk-up,
dependency/infrastructure resolution, and more).
