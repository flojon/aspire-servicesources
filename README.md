# Aspire.Hosting.ServiceSources

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

Published on [nuget.org](https://www.nuget.org/profiles/flojon) as `KoalaSoft.Aspire.Hosting.ServiceSources`:

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

Or reference the project directly from your AppHost instead:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj" />
</ItemGroup>
```

Requires .NET 8 or later (net8.0, net9.0, and net10.0 are all supported) and an AppHost
project using the `Aspire.AppHost.Sdk` (`aspire new` / `aspire restore` sets this up).

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
  overwriting your work. The `.servicesources/` directory gitignores itself on first use — no
  need to add it to your own `.gitignore`.
- Set `path` to point at a checkout you manage yourself (e.g. an existing local clone). It's
  used as-is — no clone, no checkout, no fetch, ever. A relative `path` is anchored to the
  AppHost directory, and must name a directory that already exists. `ref` cannot be combined
  with `path`.

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
no registered handler fails at startup before anything is cloned.

**Implementing a kind.** A satellite package implements `ILocalResourceKind` and registers it
from its own extension method:

```csharp
public sealed class JavaScriptKind : ILocalResourceKind
{
    private sealed class Options
    {
        public string? AppDirectory { get; set; }
        public string? RunScript { get; set; }
    }

    // Optional, and worth implementing whenever Resolve parses rawConfig: this runs for every
    // service before any of them has added a resource, so a typo'd options block is reported
    // alongside every other service's failure instead of aborting a half-built app model.
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
naming the service. `AddLocalKind` must be called before the service resolves (i.e. before the
app host starts), accepts each kind name at most once, and cannot re-register `"dotnet"` or use a
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
anything that should survive a source switch.

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

They delegate to `Configure<T>`, so out-of-band sources are skipped and logged exactly as above.
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
