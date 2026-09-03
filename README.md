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

> **These packages floor Aspire at 13.5.2, so an AppHost still on 13.4.x gets a mixed Aspire
> family.** NuGet takes the highest floor, so `Aspire.Hosting` is lifted to 13.5.2 while your
> `Aspire.AppHost.Sdk`, `Aspire.Hosting.AppHost` and the DCP and dashboard packages the SDK
> pins to it stay where they are. Nothing warns about it at restore. Move your AppHost's own
> Aspire version to 13.5.2 or later at the same time:
>
> ```xml
> <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.2" />
> ```

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
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript --prerelease
```

Add the satellite here too, not core alongside it — the feed carries a prerelease of all
three packages per commit, so two direct references are two prereleases to keep in step.
If you use no satellite at all, `dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
--prerelease` is the single reference to add.

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

That file is the base layer of the AppHost's own configuration — an environment variable or an
`appsettings.json` entry can override any of it for a single run, without an edit. See
[Overriding `servicesources.local.json`](#overriding-servicesourceslocaljson).

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

### `"local"` source options

Requires `git` (2.7 or newer) on `PATH` for a managed checkout — the same "a tool you already
have" trade the `"kubernetes"` source makes with `kubectl`. Every git operation runs under your own
git, so your credential helper, SSH agent, `~/.gitconfig` and proxy settings apply unchanged. A
service pointed at your own directory with `path` needs no git at all.

```json
{
  "services": {
    "orders": { "source": "local" },
    "payments": {
      "source": "local",
      "local": { "path": "/home/dev/code/payments", "ref": "feature/new-checkout" }
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
  on first use — no need to add it to your own `.gitignore` — and shields what it holds from your
  AppHost repository's build settings (see below).
- Set `path` to point at a checkout you manage yourself (e.g. an existing local clone). It's
  used as-is — no clone, no checkout, no fetch, ever. A relative `path` is anchored to the
  AppHost directory, and must name a directory that already exists. `ref` cannot be combined
  with `path`.
- Keep the file to the services you actually add — unless you use
  [`UseDeferredCheckout()`](#first-run-usedeferredcheckout), which removes the reason to.
  `AddService()` has to hand back the real resource, so it can't wait until the AppHost has finished
  composing to find out which services it wants: an entry whose *first* checkout an `AddService()`
  call would have to block on is cloned on the first call, in parallel with the others, before the
  AppHost has said which ones it wants. Entries you never add cost network and disk for that first
  clone. The AppHost logs which ones those were at startup — and warns if one of them failed, since
  nothing else would ever tell you — so you know what to drop.

  Nothing else is speculated over. A checkout that already exists — every service on every run
  after the first — is resolved only for the services you add, and so is a `path` override. And a
  service whose first checkout is *deferred* is cloned only when you add it: a deferred
  registration blocks on nothing, so its clone no longer has to be started ahead of demand to run
  alongside the others. With `UseDeferredCheckout()` on, a config listing ten `"local"` services in
  front of an AppHost that adds two downloads two (#76).

  Either way, only the services you actually add are reconciled to their configured `ref`: a
  checkout that already exists is never touched on behalf of an entry you don't `AddService()`, so
  work in progress on a branch there is safe.

#### Aspire builds a checkout, on every start

Nothing in this package compiles a checkout, and nothing needs to. A `dotnet` service is
registered with Aspire's own `AddProject`, and Aspire launches that resource with `dotnet run`,
whose working directory is the checkout itself. The build you would otherwise have to arrange is
that command's own implicit incremental build.

So a checkout cloned for the first time compiles when the resource starts — a cold clone with no
`bin/` needs nothing done to it first — and a checkout whose `ref` you change is recompiled on the
next run rather than served from the previous ref's binaries. That last one is worth stating
outright, because the failure it *doesn't* have would be a quiet one: a service answering with
code you moved away from.

Two things to know when it goes wrong:

- **The compiler's output isn't in the AppHost's console.** It goes to that resource's console in
  the dashboard, like any other project resource. A checkout that fails to compile therefore looks
  like a resource that never starts, and the reason is one click away rather than in the terminal
  you launched from.
- **Two `path` services in one repository can collide.** If both point into the same repository
  and their projects share a `ProjectReference`, Aspire starts both at once, and two builds write
  that shared project's `bin/`/`obj/` simultaneously — which fails intermittently, with an
  `MSB4018` or `CS2012` naming a file "being used by another process"
  ([microsoft/aspire#15190](https://github.com/microsoft/aspire/issues/15190)). Managed checkouts
  can't hit this: each service gets its own clone under `.servicesources/checkouts/<serviceName>/`,
  so there is no shared output directory even when two services come from one repository.

Launching the AppHost from an IDE is the one case this doesn't cover. An IDE that starts project
resources itself, to attach a debugger, builds them the way it builds anything else — and a
project reached by a path isn't in your solution, so it may not be built at all
([microsoft/aspire#2154](https://github.com/microsoft/aspire/issues/2154), open upstream).

#### First run: `UseDeferredCheckout()`

On a cold clone, `AddService()` blocks until the checkout it needs is on disk. Composition
hasn't finished, so the AppHost hasn't started, so there is no dashboard to look at while
several repositories clone — and a checkout that fails throws out of composition and takes the
whole AppHost down with it, including the services that were fine.

`builder.UseDeferredCheckout()` moves that wait past startup for the case where it hurts: a
`"local"` service whose *managed* checkout doesn't exist yet. The resource is registered against
the path its checkout will have, held back with Aspire's own explicit-start behaviour, cloned
while the AppHost runs, and started when its checkout lands:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.UseDeferredCheckout();

var orders = builder.AddService("orders").WithHttpEndpoint();
```

The dashboard comes up immediately, checkout progress and failure become resource state you can
see, and one bad clone costs one service instead of the run. The clones stay parallel: a deferred
service's clone starts at its own `AddService()` call and blocks nobody, so several of them still
run at once — the wall-clock is the slowest clone, not the sum. The one thing that still clones in
turn is a third-party `kind` handler that declares deferral support and then declines it for a
particular service; the built-in `dotnet`, `java` and `javascript` kinds never do. If you maintain
a kind of your own, [Implementing a kind](#implementing-a-kind) covers the two members that opt into
this and what declining late costs.

It also stops the AppHost downloading repositories it doesn't use. Without deferral the clones have
to start before the AppHost has said which services it wants, so every `"local"` entry with no
checkout yet is cloned; a deferred one is cloned only when it is added (#76).

The wait is one you can watch. git's own progress becomes the service's state — the phase it is
in, that phase's percentage, and the bytes transferred while a pack is arriving
(`Receiving objects 48% · 18.54 MiB`) — with every line git writes going to the service's console
logs as it arrives. A failure lands in the same two places. Nothing appears for a repository small
enough that git reports nothing, which is normal rather than a sign of a stall.

**What a cold checkout costs, and what it doesn't.** This part is about the `dotnet` kind. The
`java` and `javascript` kinds have no launch profile and read nothing out of the repository while
composing, so deferral costs them nothing at all — skip to *Scoped deliberately narrowly* below.
(One `javascript` exception, covered there: `appType: node` and `appType: bun` are deferred only
when the catalog guarantees a `package.json`.)

Aspire reads a project's launch profile
while composing the AppHost and turns it into endpoints, environment variables and command-line
arguments there and then. A deferred service has no repository on disk at that point, so all
three come out empty — and nothing re-runs the step.

Environment is put back for you. Once the clone lands, the profile's `environmentVariables` are
applied to the resource before it starts, and only where the AppHost hasn't already set the same
key, so `WithEnvironment` and `WithReference` still win. That matters more than it sounds:
`Host.CreateDefaultBuilder` takes the environment name from `DOTNET_ENVIRONMENT`, which most
repositories set in the launch profile and nowhere else, so without this a deferred service runs
as `Production` while every warm run of it runs as `Development`.

Values are expanded, and the service's own `DOTNET_LAUNCH_PROFILE` is set to the profile it was
started under — both as Aspire does them on a warm run. The profile read is whichever one Aspire
itself will select, which is the same selection it makes for the service's command-line arguments
once the checkout has landed: the profile your AppHost was launched under, when the service has
one by that name, and otherwise the first launchable profile in the file. So the process never
ends up with one profile's environment and another's arguments.

Endpoints can't be, because ports are allocated during composition and the spec is frozen. So a
deferred service carries only the endpoints you declare:

```csharp
var orders = builder.AddService("orders").WithHttpEndpoint();
```

You are not asked for that line up front, and a service that doesn't need it isn't refused — a
run-to-completion worker has no `applicationUrl` on either path, so demanding one would mean
declaring an endpoint it never listens on. Instead the real launch profile is read once the
checkout lands and the shortfall is reported then, quoting the `applicationUrl` it actually
found: the project still binds that URL itself and runs, but Aspire allocated no endpoint, so
the port isn't moved off a collision, nothing proxies it, service discovery can't resolve the
service and the dashboard won't link it. Add the line and the next run is whole; it is correct
on a warm checkout too, where it updates the endpoint the profile already created rather than
adding one.

Scoped deliberately narrowly, so the blast radius is first-run-only:

- Only a checkout that doesn't exist yet. A warm checkout — every run after the first — takes
  the existing eager path unchanged, with full launch-profile fidelity.
- Only managed checkouts. A `path` override is your own directory; there is nothing to clone.
- Only the `"local"` source, and within it only the kinds that own a managed checkout: `dotnet`,
  `java` and `javascript`. The other sources — `url`, `kubernetes` and `container` — never clone a
  repository, so they have nothing to defer.
- Only run mode. `aspire publish` and manifest generation clone first as they always have; a
  manifest written from a repository that isn't on disk would describe a project without its
  endpoints or its profile environment.

The `java` and `javascript` kinds get the same treatment for free, and without the endpoint
caveat above: `java` requires `port` in its kind block, and a `javascript` service always gets an
`http` endpoint with a port Aspire allocates when the block doesn't name one. Both come from the
committed catalog, so a deferred `java` or `javascript` service is identical to a warm one. The
checks that do need the working tree — `workingDirectory` and the `mvnw`/`gradlew` wrapper for
`java`, `appDirectory`/`package.json`/`scriptPath` for `javascript` — simply move to just after
the clone, which is where the docs already said they happened. For `javascript`, the separate
resource that runs `npm install` is held back with the app and started ahead of it.

`appType: node` and `appType: bun` are the one exception, and they opt out rather than guess.
Aspire's `AddNodeApp`/`AddBunApp` attach a package manager — and with it the `npm install`
resource the app waits on — only if they can see a `package.json` in the app directory, so what a
warm run builds depends on what the repository holds, and a checkout that hasn't landed can't be
looked at. They are deferred only where the answer is already known: `runScript` is set (which
requires a `package.json` anyway), or `packageManager` names one. Otherwise that one service
resolves eagerly, exactly as it does without `UseDeferredCheckout()`. Every other `appType` runs a
`package.json` script by definition and is deferred unconditionally.

Off by default: a service that used to be running by the time `Build()` returned is started
after it instead, which is visible to anything in your AppHost that assumed otherwise. Call it
before your first `AddService()`, which is where the decision is made.

#### Managed checkouts don't inherit your AppHost repository's build settings

A managed checkout is cloned *inside* your AppHost's repository, and MSBuild, NuGet, the .NET SDK
host and the compiler's analyzer configuration all find their settings by walking **up** from each
project or source file. Left alone, that means another team's repository gets built under rules
written for yours — most visibly as `NU1008` on every pinned `PackageReference` when your
repository turns on central package management, and least visibly as your `packageSourceMapping`
confining that repository's restores to your feeds (a leak that hides behind a warm
`~/.nuget/packages` and only surfaces on a clean machine or in CI).

So alongside the `.gitignore`, `.servicesources/` gets six tool-managed files that end those walks
there:

| File | Content | Stops |
| --- | --- | --- |
| `Directory.Build.props`, `Directory.Build.targets` | `<Project />` | your repository's build customisation |
| `Directory.Packages.props` | `ManagePackageVersionsCentrally=false` | your repository's central package management |
| `nuget.config` | `<packageSourceMapping><clear /></packageSourceMapping>` | your repository's package source mapping (see the note below) |
| `.editorconfig` | `root = true` | your repository's code style and analyzer severities |
| `global.json` | `{}` | your repository's SDK pin and `msbuild-sdks` versions |

Each is written with a comment saying what it is and why it's there, since you'll find them on disk
with no git history to explain them, and all are rewritten whenever their content is out of date,
so upgrading the package updates them.

Each barrier drops a constraint the checkout never opted into, and only supplies what a checkout
lacks. A checkout carrying its own `Directory.Build.props`, `Directory.Packages.props`,
`.editorconfig` or `global.json` is found first and keeps its own settings, including central
package management if that's how that repository builds.

Four of these are worth a note:

- **`.editorconfig` needs its own barrier** rather than riding on the `Directory.Build.props` one,
  because analyzer severity written as `dotnet_diagnostic.<id>.severity = error` comes from the
  `.editorconfig` itself — not from `EnforceCodeStyleInBuild` or `TreatWarningsAsErrors`. Without
  it, your repository's code style raises the checkout's own analyzers to errors.
- **`global.json` has two halves that resolve from different anchors**, so the barrier covers one
  of them completely and the other conditionally. `msbuild-sdks` resolves by walking up from the
  *project*, so it is stopped outright. `sdk.version` resolves by walking up from the *current
  working directory*, so it is stopped only for a build or run launched from inside the checkout —
  which is the working directory Aspire gives a project resource. A build you launch with the
  AppHost directory as its working directory still sees your repository's SDK pin.
- **`nuget.config` is the one barrier that isn't purely permissive, and the one that doesn't stop
  the walk.** NuGet merges every config from the drive root down rather than stopping at the
  nearest, so this file can only override the section it names. It names `packageSourceMapping`,
  and NuGet's `<clear />` discards *every* mapping accumulated before it — your user-level
  `~/.nuget/NuGet.Config` and machine-level ones included, not just your repository's. Inside a
  checkout, package source mapping is therefore off unless the checkout brings its own, while every
  inherited source stays reachable; a package that reaches your global packages folder that way is
  then served from it to restores that *do* have mapping in force, including your AppHost's own,
  because that folder isn't itself subject to mapping.

  The default is this way round because a mapping the checkout was never written against fails its
  restore outright, naming a source rather than the inherited rule behind it. If you'd rather keep
  the mapping enforced inside checkouts and deal with those failures, set
  `SERVICESOURCES_KEEP_PACKAGE_SOURCE_MAPPING=1`: the file isn't written, an existing one is
  removed, and the other five barriers are unaffected.
- **The rest of your `nuget.config` still reaches checkouts** for the same merging reason — your
  `packageSources`, `disabledPackageSources`, `packageSourceCredentials` and `config` sections
  among them. A repository that clears `packageSources` and adds only its own feed — the most
  common customisation there is — therefore still restricts what a checkout can restore, and like
  the mapping leak it hides behind a warm `~/.nuget/packages`. Clearing `packageSources` from here
  isn't the answer: a checkout that legitimately needs your private feed would stop building.

Two upward searches are deliberately left alone, because neither has a neutral value that isn't
also a decision: `Directory.Build.rsp` (MSBuild takes the first one found walking up from the
project, so your repository's response-file arguments still apply) and `.config/dotnet-tools.json`
(which affects `dotnet tool` run inside a checkout). Open an issue if either bites you.

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
      "orders":   { "source": "local", "local": { "path": "/home/dev/code/monorepo" } },
      "payments": { "source": "local", "local": { "path": "/home/dev/code/monorepo" } }
    }
  }
  ```

  This is usually what you want when the services share code: one clone, one branch, and an
  edit to a shared project is picked up by every service at once. The trade-off is that the
  clone is yours to manage — nothing is ever cloned, fetched or checked out on your behalf —
  and `local.ref` cannot be combined with `local.path`.

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

> **Keep `Aspire.Hosting.JavaScript` on the same version as `Aspire.Hosting`.** Aspire releases
> the two together and tests them that way. They were also coupled across a friend-assembly
> boundary until 13.5.0: `Aspire.Hosting.JavaScript` 13.4.6 against `Aspire.Hosting` 13.5.x
> restores and compiles clean, then throws `MethodAccessException` the first time a
> `kind: javascript` service resolves. This package floors both at 13.5.2, so you get a matched
> pair by default. If you raise `Aspire.Hosting` past that on its own, add a reference at
> whatever version your AppHost resolves for it — the version below is an example, not a
> version to copy:
>
> ```xml
> <PackageReference Include="Aspire.Hosting.JavaScript" Version="13.5.3" />
> ```

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
a consumer's `WithReference(...)` like any other — or to `GetServiceEndpoint()`, which is how a
consumer names that endpoint without knowing which source produced it
([naming a service's endpoint](#naming-a-services-endpoint)). Node and Bun must be on `PATH` for the app types
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
| `port` | yes | The port the app listens on. Becomes the service's HTTP endpoint, so consumers can `WithReference(...)` or `GetServiceEndpoint()` it. |
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
later, once the resource is being created. Under
[`UseDeferredCheckout()`](#first-run-usedeferredcheckout) that moment is later still — after the
clone lands, as this service's resource state — but it is the same two checks saying the same two
things.

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

**Supporting [`UseDeferredCheckout()`](#first-run-usedeferredcheckout).** Two more members, both
optional and both defaulting to "no", decide whether a service of your kind can start before its
checkout lands. Leave them alone and your kind keeps working exactly as it does now, always on the
eager path:

```csharp
// Answered before anything is registered, so core can decide which services to clone ahead of
// demand. Must touch no filesystem, add nothing to the app model, and never throw - it is called
// for services that may never be added. Answer from the options block alone.
public bool SupportsDeferredCheckout(object? rawConfig) => true;

// Resolve for a checkout that hasn't happened yet: repoRoot is the directory the clone *will*
// land in, and nothing is there yet.
public DeferredLocalResource? ResolveDeferred(
    IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
{
    // The same resource Resolve would build, but from the options block alone.
    var options = LocalKindConfig.Parse<Options>(rawConfig, serviceName);
    IResourceBuilder<IResourceWithServiceDiscovery> app = ...;

    return new DeferredLocalResource
    {
        Service = app,
        // Your checks that need the working tree. Core runs this after the clone and reports a
        // failure as that service's resource state.
        ValidateCheckout = () => ValidateWrapperScript(repoRoot),
    };
}
```

Build the resource exactly as `Resolve` would, but read no file under `repoRoot` — hand those checks
back as `ValidateCheckout`. Endpoints are the one thing that can't be added later, so a kind that
can only learn its endpoints by reading the repository should return `null`. Holding the resource
back and starting it once the checkout lands is core's job, and it covers every resource the call
adds to the app model, not just the one returned as `Service`.

Returning `null` from `ResolveDeferred` after `SupportsDeferredCheckout` said `true` is honoured —
legitimate for a kind that can only tell once it has looked at everything — but it isn't free. The
checkout prefetch acts on `SupportsDeferredCheckout`, so a service that answered `true` is left out
of the clones started ahead of demand, and declining here drops it onto the eager path with no clone
already running: it is cloned inline, alone, on the `AddService()` thread rather than alongside the
others. Decide in `SupportsDeferredCheckout` wherever you can, where the answer is free. A block too
malformed to answer for is `false`, which routes it to the eager path where `Validate` reports it
properly.

#### Private repositories

Clone and fetch for a managed checkout (no `path` override) authenticate the same way, in order:

1. **Whatever your `git` already does.** Clone and fetch run the `git` on your `PATH`, so every
   `credential.helper` you have configured — Git Credential Manager, `osxkeychain`, `libsecret`, a
   cached PAT, a `.netrc`-backed helper — is consulted exactly as it is for a `git clone` you type
   yourself. For an SSH remote that means your SSH agent and `~/.ssh/config`. Nothing to configure
   here: if `git clone <repository>` works in the environment the AppHost runs in, so does this.
2. **`SERVICESOURCES_GIT_USERNAME`/`SERVICESOURCES_GIT_TOKEN` environment variables**, if the
   helpers above yield nothing (e.g. no helper configured) — or if what they yielded was refused,
   see below. `SERVICESOURCES_GIT_TOKEN` alone is enough for hosts that accept any username
   alongside a personal access token (GitHub, GitLab, Azure DevOps); set
   `SERVICESOURCES_GIT_USERNAME` too if your host requires a specific one. Supplied to git as a
   credential helper of last resort, so it never overrides a helper you configured yourself, and
   the token is read from the environment rather than passed on a command line where other users
   on the machine could read it.

The order is a ladder, not a one-shot choice. `git` stops at the first helper that answers, so if
the host refuses that credential the clone would normally fail there — with the environment token
never offered. It is therefore re-run once with the configured helpers cleared, giving
`SERVICESOURCES_GIT_TOKEN` its turn. Only after that does the failure stand.

A credential the host actually refuses is reported back to your helper with `git credential
reject` — by `git` itself, as part of failing — so Git Credential Manager, `osxkeychain`,
`libsecret` and friends erase their stored copy and resolve afresh next time instead of serving the
same dead token on every run. Rotating a token therefore takes effect on the next resolution;
nothing is cached inside the AppHost process for a restart to clear.

Nothing ever prompts. `GIT_TERMINAL_PROMPT=0` is set on every invocation, and SSH runs with
`BatchMode=yes` unless you've set your own `GIT_SSH_COMMAND`, so a repository whose credentials
don't resolve fails immediately instead of hanging `builder.AddService()` on a prompt nobody is
there to answer.

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

When the ladder resolves *nothing* — no helper yields a credential and neither environment
variable is set — the error says so specifically instead of blaming authentication, because
nothing was ever offered for the host to refuse. Watch for this when the helper works in your
shell but not under the AppHost: helpers run in whatever environment the AppHost process
inherits, which is not necessarily your interactive one.

**SSH works.** A `repository` written as `git@host:org/repo`, `host:org/repo` or `ssh://...` is
handed to `git` as written and resolved by your SSH agent and `~/.ssh/config`, the same as any
other clone. Because nothing may block on a prompt, SSH runs with `BatchMode=yes`: a key whose
passphrase isn't already held by an agent, and a host that isn't in `known_hosts` yet, fail
immediately rather than waiting. Connect to the host once by hand to settle either, or set your own
`GIT_SSH_COMMAND`, which is left untouched if you do.

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
      "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 }
    }
  }
}
```

Requires `kubectl` on `PATH`, authenticated against the named `context`.

Add `scheme: https` if the pod behind that port serves TLS:

```yaml
services:
  orders:
    kubernetes:
      service: orders-svc
      port: 8443
      scheme: https
```

`kubectl port-forward` is a byte-transparent TCP tunnel, so the TLS handshake terminates at the
pod and `https://localhost:<port>` is the URL that actually works — the scheme is what the service
speaks, not a claim about the tunnel. It defaults to `http`, and names the endpoint consumers
reference: with `scheme: https` the service exposes an endpoint named `https`, so
`orders.GetEndpoint("https")` resolves. See
[naming a service's endpoint](#naming-a-services-endpoint).

What the tunnel can't fix is certificate hostname validation — the client connects to `localhost`
while the certificate names the in-cluster service — so a consumer that validates certificates
needs the usual dev-certificate handling for that.

Set `scheme` in the developer config to override the catalog for just that developer, alongside a
`port` override:

```json
{
  "services": {
    "orders": {
      "source": "kubernetes",
      "kubernetes": { "context": "dev-west", "port": 8443, "scheme": "https" }
    }
  }
}
```

### `"url"` source

Point a service at a fixed, already-known URL — e.g. a Kubernetes ingress, a staging
deployment, or any other reachable HTTP(S) endpoint. There's no underlying resource for
Aspire to run; the endpoint resolves straight to the configured URL.

Three consequences follow from the service running out of band. The AppHost's
[`Configure` calls are skipped and logged](#configuring-a-resolved-service). A **container**
can't `WithReference` it — a project or executable can — which fails with a clear error rather than
a DCP stack trace; see [#58](https://github.com/flojon/aspire-servicesources/issues/58). And a
consumer's `WaitFor` on it resolves immediately instead of waiting:

```csharp
var orders = builder.AddService("orders");

builder.AddProject<Projects.Storefront>("storefront")
    .WaitFor(orders);   // no-op while 'orders' is "url"
```

There is no lifetime to order against — the URL is already up, or it isn't, and nothing this
AppHost starts will change that — so the wait is dropped rather than satisfied. `WaitForCompletion`
goes the same way, since a service running out of band is never going to exit. Every consumer is
covered, containers included: a container that *references* a url service is still refused as
above, but one that only waits on it starts normally.

The drop is **logged**, alongside any `Configure` calls the same service skipped:

```
warn: Aspire.Hosting.ServiceSources
      Service 'orders': skipped WaitFor from 'storefront' because its source is 'url' — it
      resolves to a fixed, already-running URL with no local process to configure. ...
```

The one wait not reported is the `WaitForStart` Aspire adds itself for each resource an
`AddConnectionString` expression references — nobody wrote it, so there is no line to point at.

Note what this does **not** promise: the URL is not fetched, so the consumer starts whether or not
anything is listening. A `WaitFor` written against a `"local"` service keeps its full meaning the
moment the service is switched back, which is the point — a developer choosing `"url"` in their own
`servicesources.local.json` must not hang an AppHost they don't own. See
[#170](https://github.com/flojon/aspire-servicesources/issues/170).

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
    "orders": { "source": "url", "url": { "url": "https://orders.dev.internal" } }
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
    "orders": { "source": "container", "container": { "tag": "v1.4.2" } }
  }
}
```

Add `scheme: https` if the image serves TLS on `port`. Like `port`, it's catalog-only — the image
decides what it serves, so there's nothing per-developer to override — and it defaults to `http`:

```yaml
services:
  orders:
    container:
      image: ghcr.io/company/orders
      port: 8443
      scheme: https
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
{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 } } } }
```

or just needing it reachable, not caring how:

```json
{ "services": { "orders": { "source": "url" } } }
```

The `source` value is matched without regard to case, so `"local"`, `"Local"` and `"LOCAL"` all name
the same source. A name none of the four has is refused at composition time, naming the ones that
exist. (The `kind` names in `servicesources.yaml` are the exception — those *are* case-sensitive,
because satellite packages register them and two packages must not be able to collide by spelling.)

### Overriding `servicesources.local.json`

The file is read through the AppHost's own `IConfiguration`, as the **lowest**-precedence source in
the standard provider chain, under the key `ServiceSources:Services:<service>`. It is still the
place a developer normally writes a source selection, and a `.NET` or TypeScript AppHost authors it
identically — but every provider above it can override an entry without the file being touched:

| Layer | Overrides the file? |
| --- | --- |
| `servicesources.local.json` | — (the base) |
| `appsettings.json` | yes |
| `appsettings.{Environment}.json` | yes |
| User secrets | yes (requires a `UserSecretsId` in the AppHost csproj; without one the layer is simply absent) |
| Environment variables | yes |
| Command-line arguments | yes |

> **The `appsettings` layers need the file in the AppHost's output directory.** An AppHost project
> ships no `appsettings.json`, so unlike a web project it has no item copying that pattern to
> `bin/`, and a file placed beside the `.csproj` is silently never found — there is no error,
> the layer is simply absent. Add it explicitly:
>
> ```xml
> <ItemGroup>
>   <Content Include="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
> </ItemGroup>
> ```

> **The `ServiceSources:*` keys reach the AppHost's own `IConfiguration` on its first ServiceSources
> call, not before.** `servicesources.local.json` is a file of ours, read from the AppHost directory
> and re-keyed into the chain by whichever ServiceSources method the AppHost calls first — a
> `UseX()` registration, or the first `AddService()`. A read placed *above* all of them sees the
> chain without that layer, so a selection written only in the file comes back `null`, silently,
> since a missing key is not an error:
>
> ```csharp
> // null — nothing of ours has been called yet, so the file is not in the chain.
> var source = builder.Configuration["ServiceSources:Services:orders:source"];
>
> builder.UseJavaScript();
>
> // "local" — the file joined the chain on the line above.
> source = builder.Configuration["ServiceSources:Services:orders:source"];
> ```
>
> Reading these keys from an AppHost should be rare. Scoping a declaration to one source is what
> sends an AppHost looking for them, and
> [`Configure<T>`](#configuring-a-resolved-service) already does that scoping for you.

The immediate payoff is a **single run** with a different source and no edit to a file you'd have to
remember to change back. `source` itself isn't nested under a block, so this still works verbatim:

```bash
ServiceSources__Services__orders__Source=url dotnet run
```

Overriding a *field* works the same way, but gains its source's block segment —
`ServiceSources__Services__orders__Local__Ref`, `ServiceSources__Services__orders__Container__Tag`,
and so on. (`__` is the .NET configuration separator for `:`, and is what you want on every
platform.) Setting one of these to a blank value *unsets* the field, rather than setting it to an
empty string — `ServiceSources__Services__orders__Local__Path=` leaves the service with no `path`
at all, even one `servicesources.local.json` (or a layer in between) configured. It does not fall
back to that lower layer's value: configuration merges the layers *before* this package sees them,
so the blank is what arrives and the field ends up absent — which for `path` means the service gets
its managed checkout, exactly as if no layer had ever named one.

Blank means *empty*, exactly, whatever the field's type. A value of one or more spaces is refused
rather than read as either an unset field or a value of its own:
`ServiceSources__Services__orders__Kubernetes__Port=` drops the port, and
`ServiceSources__Services__orders__Local__Path=" "` is an error naming the spelling that works
rather than an override silently discarded — which is what a stray space surviving a CI variable
used to cost, leaving the service on its managed checkout with nothing said.

A service whose name contains a hyphen — `order-service`, say — makes a variable name a shell won't
accept as an inline assignment, so pass it through `env` instead:

```bash
env 'ServiceSources__Services__order-service__Source=url' dotnet run
```

The key itself is fine either way; it's only the one-line `NAME=value command` form that needs this.

The nesting is what makes this override story work at all: switching `source` from a higher layer
leaves the previous source's block sitting in the file, unread rather than removed. Nothing has to
be deleted from `servicesources.local.json` to switch a service away from the source it names there
— the fields for every other source can sit in the file unused, ready for the next switch back.

CI is the other case. A build agent has no developer to pick sources for it, and cloning every
service to run one test is waste, so pin them from the environment and ship no file at all:

```yaml
env:
  ServiceSources__Services__orders__Source: container
  ServiceSources__Services__payments__Source: container
```

**Named profiles** fall out of the same mechanism. Put the cluster-facing selection in
`appsettings.Cluster.json` next to the AppHost:

```json
{
  "ServiceSources": {
    "Services": {
      "orders": {
        "source": "kubernetes",
        "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 }
      }
    }
  }
}
```

and choose it per run by passing the environment as an argument to the AppHost:

```bash
aspire run -- --environment Cluster     # everything after -- goes to the AppHost
dotnet run -- --environment Cluster     # or launching the AppHost directly
```

**`DOTNET_ENVIRONMENT=Cluster` does not work under `aspire run`.** The CLI sets
`ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` to `Development` itself when it launches the
AppHost, so a value exported in your shell is overwritten and the profile is silently not
selected — you get the base file's selection with no indication that the profile was ignored.
The variable route only works when you run the AppHost yourself with `dotnet run`. The
command-line form above works in both.

Note the extra `ServiceSources` root: inside the AppHost's shared configuration the entries are namespaced, while `servicesources.local.json` keeps its bare
`services` root because it is a file of ours, read from the AppHost directory and re-keyed as it
joins the chain.

Two failures are reported differently on purpose, because a typo in a configuration key produces an
empty section rather than an error:

- **Nothing configured anywhere** — `ServiceSources:Services` is empty in every source. The message
  says so, names the `servicesources.local.json` path it looked for and whether it was found, and
  lists every source consulted.
- **This one service isn't configured** — other services resolved, this one has no entry. The
  message names `ServiceSources:Services:<service>:source` and the environment variable that would
  set it.

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
        .WithEnvironment("Services__CommonAuth", commonAuth.GetServiceEndpoint()))
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

That is this service waiting for something else. The other direction — something else waiting for
*this* service, `consumer.WaitFor(service)`, which is ordinary Aspire rather than a `Configure`
call — is dropped for `"url"` and honoured for every other source, including `"kubernetes"`. That
drop is reported in the same message as the service's skipped `Configure` calls. See
[the `"url"` source](#url-source).

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

`Configure<T>` is generic, and Aspire's Type System erases a generic method's type parameter to its
constraint — which for `Configure<T>` erases the capability being requested, since that is all `T`
says. So guest languages get a set of non-generic equivalents instead, one per shape, each with its
own name (two exports that project to the same generated name collide, and only one survives):

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

## Naming a service's endpoint

A consumer that wants the service's URL asks for an endpoint. Aspire names endpoints, and
`GetEndpoint("https")` looks like the obvious spelling — but the endpoint *name* a resolved service
exposes is decided by whichever source resolved it:

| Source | Endpoint name |
|---|---|
| `"local"`, `kind: dotnet` | whatever the launch profile's `applicationUrl` declares (`http`, `https`, or both) |
| `"local"`, satellite kinds (`javascript`, `java`) | `http` |
| `"url"` | the configured URL's scheme |
| `"kubernetes"`, `"container"` | the configured `scheme`, `http` unless set |

So naming a scheme resolves only while the service happens to be on a source that produces it.
Switch that service and the consumer breaks — and it breaks *late*: composition succeeds, and the
throw comes from Aspire's `ExpressionResolver` when the consumer's environment is gathered, so it
surfaces as a `FailedToStart` on the **consumer** naming a service the consumer never changed:

```
System.InvalidOperationException: The endpoint `https` is not defined for the resource
`common-auth`. Available endpoints: `http`.
```

`GetServiceEndpoint()` is the portable spelling. It asks for *the* endpoint the service exposes and
survives a source switch:

```csharp
var commonAuth = builder.AddService("common-auth");

builder.AddProject<Projects.Core>("planning-core")
    .WithEnvironment("Services__CommonAuth", commonAuth.GetServiceEndpoint());
```

It resolves to the endpoint named `https` if there is one, else `http`, else the service's only
endpoint whatever it's named — the same order Aspire's own service discovery resolves
`"https+http://"` in, so a service exposing both hands back the endpoint Aspire would have picked
itself. It throws at composition time, naming the service and its source, if the service exposes no
endpoint at all or exposes several with none named `http` or `https`; in that last case there's no
single endpoint to mean, so name the one you want with `GetEndpoint("<name>")`.

The endpoint is chosen when you call it, so call it after any `Configure` that adds one. The
`EndpointReference` it returns is lazy in the usual way — the URL resolves once Aspire has allocated
the port.

`WithReference(service)` plus service discovery is portable too, and is the better fit when the
consumer speaks service discovery: it injects every endpoint the service has under
`services__<name>__<scheme>__<index>`, and a client resolving `https+http://common-auth` picks
whichever is there. `GetServiceEndpoint()` is for the case a plain URL in a plain environment
variable is what the consumer reads.

`GetEndpoint("<scheme>")` still has its place — a service you know will never move off `"local"`,
or an endpoint you added yourself through `Configure<IResourceWithEndpoints>`. Just don't reach for
it across a service whose source a developer chooses.

From a guest-language AppHost it's `getServiceEndpoint()`, and the value flows into Aspire's own
`withEnvironment`:

```typescript
await builder
  .addExecutable('probe', process.execPath, '.', ['-e', probeScript])
  .withEnvironment('INVENTORY_URL', inventory.getServiceEndpoint());
```

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
cloning PetClinic on its first run: the sample does not call `UseDeferredCheckout()`, so the first
`AddService` clones every `"local"` entry there that has no checkout yet, whether or not you add it.

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
container consumer of one is [rejected up front](#url-source). A third resource, the `probe`
executable, hands the same `inventory` handle to Aspire's *own* `withReference()` and to
`getServiceEndpoint()`, and prints what each injected — so it shows as *Exited*, not Running, and
those two log lines are where you see both the native service-discovery path and the
[portable endpoint accessor](#naming-a-services-endpoint) working. (**Note:** this
sample needs Aspire CLI 13.5.3 or newer — see the compatibility note below the code block.)

```bash
cd samples/DemoAppHostTypeScript
npm install
cp servicesources.local.json.example servicesources.local.json
aspire restore
aspire run
```

**Requires Aspire CLI 13.5.3+:** the CLI pins its own Aspire version for the host project it
generates, so a CLI older than this package's Aspire floor (13.5.2) fails `aspire restore` with
`NU1605: Detected package downgrade: Aspire.Hosting from 13.5.2 to 13.5.1` before codegen even runs.
13.5.3 is the first release that pins high enough. On it, the generated SDK type-checks clean under
strict `tsc` and the sample runs end-to-end — `withReference()` on the `addService()` result injects
the resolved service's discovery variables into the consuming resource, e.g.
`services__inventory__http__0=http://inventory.dev.internal:80` pointing at the running `inventory`
container.

This sample used to require an unreleased 13.6.0, and that requirement is gone. Aspire's TypeScript
codegen does not emit a `*Promise`/`*PromiseImpl` wrapper pair for a bare Aspire interface
(`IResourceBuilder<IResourceWithServiceDiscovery>`, which is what `AddService` returns), so the
generated SDK referenced an undeclared `ResourceWithServiceDiscoveryPromise` and failed with six
`TS2552` errors — reported as
[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507) and fixed upstream by
[microsoft/aspire#19577](https://github.com/microsoft/aspire/pull/19577) under the 13.6 milestone.

That upstream fix is no longer what makes this work. The generator emits the wrapper pair when the
bare interface appears as an extension-method **receiver** rather than only as a return type, and
the eight `[AspireExport]` configuration shims above declare exactly that receiver — so they carry
the wrapper pair for `addService` too. Removing `[AspireExport]` from those eight shims brings all
six errors back on a current CLI, which is how the cause was isolated; the measurement is in
[`docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md`](docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md).

Switching between CLI builds can leave a stale code generator under `.aspire/`, so remove that
directory before regenerating:
[microsoft/aspire#19603](https://github.com/microsoft/aspire/issues/19603).

## When configuration is wrong

Every problem this package detects — a missing project file, an unregistered `kind`, a checkout it
won't overwrite, a clone it can't authenticate — is raised as a `ServiceSourcesConfigurationException`
whose message names the service, what failed, and what to do about it. Because these are raised
from `AddService()`, they usually reach you as an unhandled exception that takes the AppHost down
before Aspire starts, so that message *is* the error output. It prints as the message plus one
line per underlying cause:

```
Unhandled exception. Service 'reportdata': failed to clone repository 'https://github.com/acme/planning' into
'/src/report-service/src/Report.AppHost/.servicesources/checkouts/reportdata' — authentication failed, or the
repository is not visible to the credentials in use. Configure credentials via a git credential helper (`git
credential fill` must resolve them for this host) or the SERVICESOURCES_GIT_USERNAME/SERVICESOURCES_GIT_TOKEN
environment variables.
  caused by: unexpected http status code: 404
  (set SERVICESOURCES_FULL_ERRORS=1 for the full exception detail, including stack traces)
```

The stack frames behind it are this package's own plumbing and don't help with a misconfiguration,
so they're left out — and the last line says how to get them back, because for a failure this
package didn't anticipate they are the diagnosis. When you need them — you suspect a bug in this package rather than in your
configuration, and want to file it — set `SERVICESOURCES_FULL_ERRORS=1` to get the runtime's
complete dump, type names, inner-exception blocks, stack traces and all.

## Status

Early stage, evolving fast. `"local"`, `"kubernetes"`, `"url"`, and `"container"` sources are
all implemented — see [`docs/superpowers/`](docs/superpowers/) for design and implementation
history, including the phase 2 backlog (repo auto-update, config discovery walk-up,
dependency/infrastructure resolution, and more).

Changes are recorded in [`CHANGELOG.md`](CHANGELOG.md); how a release is cut is in
[`RELEASING.md`](RELEASING.md).
