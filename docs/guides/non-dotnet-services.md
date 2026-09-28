# Non-.NET services (`kind`)

A `"repository"` or `"path"` service is resolved as a .NET project by default. Set `kind` in the catalog to run
the checkout some other way — the git clone/checkout is identical, only what gets built out of
the resulting directory changes:

=== "C#"

    ```csharp
    catalog.AddService("frontend")
        .WithRepository("https://github.com/example/frontend")
        .AsJavaScript(o => o.WithAppDirectory(".").WithRunScript("dev"));
    ```

=== "YAML"

    ```yaml
    services:
      frontend:
        repository: https://github.com/example/frontend
        kind: javascript          # optional; defaults to "dotnet"
        javascript:               # per-kind options block, named after the kind
          appDirectory: .
          runScript: dev
    ```

`kind: dotnet` (the default) uses the entry's `project` property and needs no options block; an optional `dotnet:` block
chooses its launch profile ([details](yaml-catalog.md#dotnet-choosing-a-launch-profile)).
`project` is required for that kind, and is a path relative to the service's checkout that must
stay inside it — the rule `java.jarPath` and `java.workingDirectory` follow below, and for the same
reason: the catalog is shared configuration you clone rather than write, so it does not get to name
a project elsewhere on a developer's machine. An absolute path (`/srv/Api.csproj`,
`C:\repos\Api.csproj`) or one that climbs out (`../../shared/Api.csproj`) is refused at that
service's `AddService()` call, before its checkout is used. A project in another repository gets an
entry of its own, naming that repository. The `javascript` keys are confined to the checkout too,
by a check of their own that judges the resolved path rather than the written one.
Any other kind is resolved by a registered handler, and its options live in a block named after
the kind. Kind names are matched case-sensitively, and a kind with no registered handler fails
at that service's `AddService()` call, before its checkout is used.

## JavaScript: `kind: javascript`

Runs the checkout through
[`Aspire.Hosting.JavaScript`](https://www.nuget.org/packages/Aspire.Hosting.JavaScript), which
your AppHost references itself (13.5.2 or newer — see [Installation](../getting-started/installation.md#non-net-services)). `javascript` is a
built-in kind, resolved the same way `dotnet` always has been — reference the package and declare
the kind, no registration call needed:

=== "C#"

    ```csharp
    builder.AddServiceCatalog(catalog =>
        catalog.AddService("frontend")
            .WithRepository("https://github.com/example/frontend")
            .AsJavaScript(o => o
                .WithAppType("vite")          // javascript (default) | vite | nextjs | node | bun
                .WithAppDirectory("web")      // directory holding package.json, relative to the repo root
                .WithRunScript("dev")         // package.json script to run
                .WithPackageManager("pnpm")   // npm | yarn | pnpm | bun
                .WithPort(4321)));            // the port consumers reach the service on

    var frontend = builder.AddService("frontend");
    ```

=== "YAML"

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
([naming a service's endpoint](configuration.md#naming-a-services-endpoint)). Node and Bun must be on `PATH` for the app types
that use them.

## Java: `kind: java`

Runs the checkout through the Aspire Community Toolkit's
[Java integration](https://github.com/CommunityToolkit/Aspire), which your AppHost references
itself as `CommunityToolkit.Aspire.Hosting.Java` (13.3.0 or newer — see
[Installation](../getting-started/installation.md#non-net-services)). `java` is a built-in kind, resolved the same way `dotnet` always has
been — reference the package and declare the kind, no registration call needed:

=== "C#"

    ```csharp
    builder.AddServiceCatalog(services =>
        services.AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .AsJava(o => o.WithMavenGoal("spring-boot:run").WithPort(8080)));

    var catalog = builder.AddService("catalog");
    ```

=== "YAML"

    ```yaml
    services:
      catalog:
        repository: https://github.com/example/catalog
        kind: java
        java:
          mavenGoal: spring-boot:run
          port: 8080
    ```

The checkout is cloned exactly as for any other `"repository"` service (`path`, `ref`, and
`defaultRef` all behave identically), then handed to that integration to run.

**`java:` block options**

| Field | Required | Description |
| --- | --- | --- |
| `mavenGoal` | one of these three | Run via the Maven wrapper, e.g. `spring-boot:run`. |
| `gradleTask` | one of these three | Run via the Gradle wrapper, e.g. `bootRun`. |
| `jarPath` | one of these three | Run a pre-built jar with `java -jar`, relative to `workingDirectory`. May climb out of it — a monorepo's shared build output directory — but must stay inside the checkout. |
| `port` | yes | The port the app listens on. Becomes the service's endpoint (see `scheme` below), so consumers can `WithReference(...)` or `GetServiceEndpoint()` it. |
| `scheme` | no (defaults to `http`) | The scheme the app serves on `port` — `http` or `https`. |
| `workingDirectory` | no (defaults to the repository root) | Where in the checkout the project lives — the directory holding `pom.xml` / `build.gradle`, and by default the `mvnw`/`gradlew` wrapper too. Must stay inside the checkout. |
| `wrapperPath` | no (defaults to the wrapper in `workingDirectory`) | Where the `mvnw`/`gradlew` wrapper script lives, relative to the **repository root** — for the monorepo that commits a single wrapper at its root while the service itself sits further down. Name it without an extension (`gradlew`, not `gradlew.bat`) and it works for the whole team: on Windows the `.cmd`/`.bat` wrapper beside it is the one run. Only meaningful with `mavenGoal` or `gradleTask`. |
| `args` | no | Extra arguments for whichever run mode is configured — passed to the Maven wrapper, the Gradle wrapper, or the jar. |

`mavenGoal`, `gradleTask`, and `jarPath` are mutually exclusive: exactly one must be set. A
monorepo service, running a Gradle task with an extra argument:

=== "C#"

    ```csharp
    catalog.AddService("catalog")
        .WithRepository("https://github.com/example/monorepo")
        .AsJava(o => o
            .WithWorkingDirectory("services/catalog")
            .WithGradleTask("bootRun")
            .WithWrapperPath("gradlew")
            .WithArgs(["--args=--spring.profiles.active=dev"])
            .WithPort(8080));
    ```

=== "YAML"

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

Every problem with the block — unknown properties, a missing or out-of-range `port`, an
unsupported `scheme`, no run mode or more than one, a `workingDirectory`, `wrapperPath` or
`jarPath` escaping the repository, a `wrapperPath` set alongside `jarPath`, a `workingDirectory`
that isn't in the checkout, a wrapper script that isn't there — is reported by the
`AddService("catalog")` call itself, before the service has added anything to the app model. The
last two are read against the checkout, so under
[deferred checkout](../sources/repository.md#first-run-deferred-checkout), where there isn't one yet, they are
reported after the clone lands as this service's resource state instead — the same two checks
saying the same two things.

Add `scheme: https` if the app serves TLS on `port`:

=== "C#"

    ```csharp
    catalog.AddService("catalog")
        .WithRepository("https://github.com/example/catalog")
        .AsJava(o => o.WithMavenGoal("quarkus:run").WithPort(8443).WithScheme("https"));
    ```

=== "YAML"

    ```yaml
    services:
      catalog:
        repository: https://github.com/example/catalog
        kind: java
        java:
          mavenGoal: quarkus:run
          port: 8443
          scheme: https
    ```

Like `port`, it's catalog-only — the app decides what it serves, so there's nothing
per-developer to override — and it defaults to `http`. With `scheme: https` the service exposes
an endpoint named `https` instead of `http`, so `catalog.GetServiceEndpoint()` resolves to it and
`catalog.GetEndpoint("https")` works directly; see
[naming a service's endpoint](configuration.md#naming-a-services-endpoint). Getting the app itself to actually
serve TLS is a framework concern the `java:` block deliberately stays out of — reach it from the
AppHost with `Unwrap<JavaAppExecutableResource>(...)`, most often paired with Aspire's own
`WithHttpsCertificateConfiguration` to hand the service the developer certificate without
hardcoding a path.

**Reaching the rest of the Java integration.** The `java:` block covers how to start the app; it
deliberately doesn't mirror every modifier the Community Toolkit offers. Anything else is reachable
from the AppHost with `Unwrap<JavaAppExecutableResource>(...)`, which hands the delegate the real
resource builder:

```csharp
builder.AddService("catalog")
    .Unwrap<JavaAppExecutableResource>(java => java
        .WithMavenBuild()                  // compile before starting
        .WithJvmArgs(["-Xmx512m"])
        .WithOtelAgent("/path/to/opentelemetry-javaagent.jar"))
    .WithEnvironment("CATALOG_MODE", "dev");
```

This survives a developer switching `catalog` to any other source in their own
`servicesources.local.json`: when it no longer resolves to a Java resource, the delegate is skipped
and the skip is logged at startup. The parameterless `Unwrap<JavaAppExecutableResource>()` returns
the builder directly but throws in that case — use it only where the AppHost genuinely requires a
Java resource. See [configuring a resolved service](configuration.md#configuring-a-resolved-service).

`UseJava()`/`useJava()` (also exported to Aspire's Type System for a TypeScript AppHost) is
**obsolete** — `java` resolves without it, and calling it registers an instance of the same
built-in handler the fallback already uses, so it changes nothing about how `kind: java` resolves.
Delete the call — don't just add an `AddLocalKind` call next to it, since `UseJava()` still occupies
the `"java"` registration slot the same way any `AddLocalKind` call does, and a second registration
for the same name throws. To substitute a *different* `ILocalResourceKind` for `"java"` (e.g. a test
double), call `AddLocalKind("java", yourKind)` directly — `UseJava()` cannot take a handler
argument, so it was never able to do that.

## Implementing a kind

A kind implements `ILocalResourceKind` and registers it from an extension
method:

```csharp
public sealed class JavaScriptKind : ILocalResourceKind
{
    private sealed class Options
    {
        public string? AppDirectory { get; set; }
        public string? RunScript { get; set; }
    }

    // Optional, and worth implementing whenever Resolve parses rawConfig or reads the checkout:
    // this runs immediately before Resolve, against the same repoRoot, and before this service has
    // added anything to the app model — so a typo'd options block, or one naming a directory the
    // repository doesn't have, is reported without a half-created resource behind it. Not the only
    // place to put these checks if your kind supports deferred checkouts — see below.
    public void Validate(string serviceName, string repoRoot, object? rawConfig)
    {
        var options = LocalKindConfig.Parse<Options>(rawConfig, serviceName);

        if (options?.AppDirectory is { } appDirectory
            && !Directory.Exists(Path.Combine(repoRoot, appDirectory)))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': appDirectory '{appDirectory}' is not in the checkout.");
        }
    }

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        // repoRoot is the already-cloned, already-checked-out directory.
        var options = LocalKindConfig.Parse<Options>(rawConfig, serviceName);
        ...
    }

    // Optional: the type every resource Resolve/ResolveDeferred returns is, or derives from.
    // Lets Unwrap<T>(configure) reject a T this kind could never produce instead of only
    // warning; core fails startup if a returned resource isn't one.
    public Type ResourceType => typeof(JavaScriptAppResource);
}

public static IDistributedApplicationBuilder UseCustomJavaScriptKind(this IDistributedApplicationBuilder builder) =>
    builder.AddLocalKind("javascript", new JavaScriptKind());
```

`LocalKindConfig.Parse<T>` turns the opaque options block into a typed object, and rejects an
unknown property or a block that isn't a mapping with a `ServiceSourcesConfigurationException`
naming the service. `AddLocalKind` must be called before the `AddService()` call for a service of
that kind — resolution is eager, so registering later is too late — accepts each kind name at most
once, and cannot re-register `"dotnet"`.

A name that collides with a well-known service property (`repository`, `path`, `project`,
`defaultRef`, `defaultSource`, `repositoryRef`, `kind`, `kubernetes`, `url`, `container`,
`prepare`) can still be registered with `AddLocalKind` — the collision only matters for a **yaml**
service that actually names this kind: its `<kind>:` block sits at the same nesting level as those
properties, so it would be read as the matching property instead of the kind's options. That check
happens per-service, in the yaml loader, not at registration time — a code-declared catalog never
hits it.

It also refuses a handler that declares a public `Validate` taking a service name first and an
options block somewhere, which doesn't match the interface member — the pre-`repoRoot`
`Validate(string, object?)`, the parameter added in the wrong position, the wrong return type —
naming the kind and the method it found. `Validate` is a defaulted interface member, so any of
those compile clean and simply stop implementing it, and everything they rejected would be silently
accepted instead. Registration is the only place left to say so; the build won't. A `Validate` of
your own is left alone unless it looks like that attempt: a private helper, one taking your own
options type, and one like `Validate(string message)` that carries no options block at all all
register exactly as they did before.

**Supporting [deferred checkout](../sources/repository.md#first-run-deferred-checkout).** Two more members, both
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

> **Validate your options block here too.** `Validate` is paired with `Resolve`, and core calls
> neither for a service it defers — there is no checkout for `Validate` to judge the service
> against, so `ResolveDeferred` runs in their place. A kind that can answer `true` from
> `SupportsDeferredCheckout` and rejects a bad block only in `Validate` has arranged for that block
> never to be checked at all under deferred checkout. Parse it here as well and throw
> `ServiceSourcesConfigurationException`. Nothing warns you: implementing both `Validate` and
> `ResolveDeferred` is the ordinary, correct arrangement — the built-in `java` kind does — so
> there is no signal to refuse the way a mismatched `Validate` signature is refused.

Returning `null` from `ResolveDeferred` after `SupportsDeferredCheckout` said `true` is honoured —
legitimate for a kind that can only tell once it has looked at everything — but it isn't free. The
checkout prefetch acts on `SupportsDeferredCheckout`, so a service that answered `true` is left out
of the clones started ahead of demand, and declining here drops it onto the eager path with no clone
already running: it is cloned inline, alone, on the `AddService()` thread rather than alongside the
others. Decide in `SupportsDeferredCheckout` wherever you can, where the answer is free. A block too
malformed to answer for is `false`, which routes it to the eager path where `Validate` reports it
properly.

## Private repositories

Clone and fetch for a managed checkout (no `path` override) authenticate the same way, in order:

1. **Whatever your `git` already does.** Clone and fetch run the `git` on your `PATH`, so every
   `credential.helper` you have configured — Git Credential Manager, `osxkeychain`, `libsecret`, a
   cached PAT, a `.netrc`-backed helper — is consulted exactly as it is for a `git clone` you type
   yourself. For an SSH remote that means your SSH agent and `~/.ssh/config`. Nothing to configure
   here: if `git clone <repository>` works in the environment the AppHost runs in, so does this.
2. **`SERVICESOURCES_GIT_USERNAME`/`SERVICESOURCES_GIT_TOKEN`/`SERVICESOURCES_GIT_HOST`
   environment variables**, if the helpers above yield nothing (e.g. no helper configured) — or if
   what they yielded was refused, see below. **`SERVICESOURCES_GIT_HOST` is required** — the host
   (and port, if the URL has one, e.g. `git.internal.example:8443`), matched case-insensitively,
   that the token is for. Without it the token is offered to no host at all, since a catalog can
   list services from more than one host and this token belongs to only one of them.
   `SERVICESOURCES_GIT_TOKEN` alone (alongside `SERVICESOURCES_GIT_HOST`) is enough for hosts that
   accept any username alongside a personal access token (GitHub, GitLab, Azure DevOps); set
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

When the ladder resolves *nothing* — no helper yields a credential, and the environment rung has
nothing to offer either because `SERVICESOURCES_GIT_TOKEN` is unset or because
`SERVICESOURCES_GIT_HOST` doesn't name this host — the error says so specifically instead of
blaming authentication, because nothing was ever offered for the host to refuse. Watch for this
when the helper works in your shell but not under the AppHost: helpers run in whatever
environment the AppHost process inherits, which is not necessarily your interactive one.

**SSH works.** A `repository` written as `git@host:org/repo`, `host:org/repo` or `ssh://...` is
handed to `git` as written and resolved by your SSH agent and `~/.ssh/config`, the same as any
other clone. Because nothing may block on a prompt, SSH runs with `BatchMode=yes`: a key whose
passphrase isn't already held by an agent, and a host that isn't in `known_hosts` yet, fail
immediately rather than waiting. Connect to the host once by hand to settle either, or set your own
`GIT_SSH_COMMAND`, which is left untouched if you do.

