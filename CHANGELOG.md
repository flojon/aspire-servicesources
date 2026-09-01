# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
version is below `1.0.0`, a breaking change can ship in a minor release, so each one gets a
**Breaking** entry saying what breaks and how to migrate. A change that keeps compiling but
behaves differently is called out under **Changed** — read those before upgrading too, since
nothing will fail to build to warn you.

## [Unreleased]

### Added

- **`builder.UseDeferredCheckout()` moves a cold `"local"` checkout past AppHost startup**
  ([#130], [#159]). Opt-in, off by default. A `"local"` service whose managed checkout does not
  exist yet is registered against the path that checkout will have, held back with Aspire's
  explicit-start behaviour, cloned while the AppHost runs, and started once its checkout lands.
  The dashboard comes up immediately instead of after every clone, checkout progress and failure
  become visible resource state, and a clone that fails costs one service rather than the whole
  AppHost.

  All three `"local"` kinds that own a managed checkout are covered — `dotnet`, `java` and
  `javascript`. (The other *sources* — `url`, `kubernetes` and `container` — never clone a
  repository, so they have nothing to defer.) The launch-profile caveat below is the `dotnet` kind's alone: neither
  satellite kind has a launch profile, and both take their endpoints from the committed catalog —
  `java` requires `port` in its kind block, and a `javascript` service always gets an `http`
  endpoint whose port Aspire allocates when the block does not name one — so a deferred `java` or
  `javascript` service is identical to a warm one. Their checks against the working tree
  (`workingDirectory` and the `mvnw`/`gradlew` wrapper; `appDirectory`, `package.json` and
  `scriptPath`) run just after the clone, reported as that service's resource state. For
  `javascript`, the separate resource that runs `npm install` is held back with the app and
  started ahead of it.

  Aspire reads a project's launch profile during composition and turns it into endpoints,
  environment variables and command-line arguments there and then, so a deferred service — whose
  repository is not on disk yet — gets none of the three, and nothing re-runs the step.
  **Environment is restored**: once the clone lands, the profile's `environmentVariables` are
  applied before the resource starts, and only where the AppHost has not already set the same key.
  Without that a deferred service runs as `Production` while every warm run of it runs as
  `Development`, because `Host.CreateDefaultBuilder` takes the environment name from
  `DOTNET_ENVIRONMENT` and most repositories set it in the launch profile and nowhere else. Values
  are expanded and `DOTNET_LAUNCH_PROFILE` is set, both as Aspire does them on a warm run, and the
  profile is the one Aspire itself will select — including a profile named by
  `DOTNET_LAUNCH_PROFILE` or one whose `commandName` is `Executable` or absent — so the process
  never gets one profile's environment and another's arguments.

  **Endpoints cannot be restored**, since ports are allocated during composition, so declare any
  you need:

  ```csharp
  builder.UseDeferredCheckout();

  var orders = builder.AddService("orders").WithHttpEndpoint();
  ```

  A service that declares none is *not* refused — a run-to-completion worker has no
  `applicationUrl` on either path and would have to declare an endpoint it never listens on.
  Instead the landed launch profile is read after the clone and a shortfall is reported then,
  quoting the `applicationUrl` it actually found and what to add. The line above is correct on a
  warm checkout too, where it updates the endpoint the profile already created.

  Nothing else about a run changes. The clones still start on the first `AddService()` call, in
  parallel, at the same moment as before — only who waits for them moves. A checkout that already
  exists takes the eager path unchanged, as do `path` overrides. So does everything outside run
  mode: `aspire publish` and manifest generation clone first as they always have, because a
  manifest written from a repository that is not on disk would describe a project without its
  endpoints or its profile environment.

  A satellite package registering its own kind through `AddLocalKind()` opts in by implementing
  `ILocalResourceKind.ResolveDeferred()`, which is handed the path the clone will land in and
  returns the resource plus a `ValidateCheckout` callback for the checks that need the working
  tree. It defaults to returning `null`, meaning "resolve me eagerly", so an existing handler
  keeps its current behaviour without changing. Its companion
  `ILocalResourceKind.SupportsDeferredCheckout(rawConfig)` answers the same question without
  registering anything — `ResolveDeferred` adds resources to the app model, so it cannot be used to
  ask speculatively — and also defaults to declining.

  `appType: node` and `appType: bun` are deferred only when the catalog guarantees a `package.json`
  — `runScript` is set, or `packageManager` names one. Aspire's `AddNodeApp`/`AddBunApp` attach a
  package manager, and with it the `npm install` resource the app waits on, only when they can see
  a `package.json` in the app directory; what a warm run builds therefore depends on the
  repository's contents, which a checkout that has not landed cannot be read for. Rather than
  guess, those services resolve eagerly. Every other `appType` runs a `package.json` script by
  definition and is deferred unconditionally.

- **`GetServiceEndpoint()`, a portable way for a consumer to name a resolved service's endpoint**
  ([#160]). The endpoint *name* a service exposes was decided by whichever source resolved it — a
  `"local"` dotnet project takes its endpoints from its launch profile, a `"url"` service is named
  for the URL's scheme, and every other source produced `http` — so a consumer's
  `GetEndpoint("https")` resolved only while that service happened to sit on a source that produced
  an `https` endpoint. Switching one service from `"local"` to `"kubernetes"` therefore broke an
  unrelated consumer, and broke it late: composition succeeded and Aspire's `ExpressionResolver`
  threw while gathering the consumer's environment, surfacing as a `FailedToStart` on the
  **consumer**, naming a service the consumer never changed. `GetServiceEndpoint()` asks for *the*
  endpoint the service exposes — `https` if there is one, else `http`, else its only endpoint — and
  survives a source switch. It is exported to Aspire's Type System as `getServiceEndpoint()`, so a
  guest-language AppHost has the same spelling. `GetEndpoint("<scheme>")` keeps working and stays
  the right call for an endpoint you added yourself; the README says which to reach for.

- **`scheme` on the `kubernetes` and `container` config blocks** ([#160]). Both hardcoded `http`,
  which was not merely a naming choice: `kubectl port-forward` is a byte-transparent TCP tunnel, so
  a pod serving TLS is genuinely reachable at `https://localhost:<port>` and a consumer handed an
  `http://` URL for it cannot connect at all. Set `scheme: https` in `servicesources.yaml` and the
  service exposes an endpoint named `https` whose URL says so. It defaults to `http`, so nothing
  changes for a service that does not set it. For `"kubernetes"` a developer can override it in
  `servicesources.local.json` alongside a `port` override; for `"container"` it is catalog-only,
  exactly as `container.port` is, since the image decides what it serves. Certificate hostname
  validation is the one thing a tunnel cannot fix — the client connects to `localhost` while the
  certificate names the in-cluster service — and the README says so.

### Changed

- **`git` on `PATH` is now required for a managed `"local"` checkout** ([#85]), and
  `LibGit2Sharp` is gone from the package. Clone, fetch and checkout shell out to the `git`
  executable, the same "a tool you already have" trade the `"kubernetes"` source makes with
  `kubectl`. `git` 2.7 or newer; a service pointed at your own directory with `path` needs none.

  This removes the only source-specific external dependency the package had, and by far the
  heaviest thing it shipped. `LibGit2Sharp.NativeBinaries` carries a native libgit2 for all
  thirteen RIDs it supports in one package, and an Aspire AppHost is a portable build by default,
  so every consumer's build output got **all** of them — about **23 MB of `runtimes/`**, roughly
  half of a sample AppHost's `bin`, all but one of them for a platform that machine will never
  run. Plus 32 MB in `~/.nuget/packages`. There is no per-RID variant to reference instead and
  the native assets can't be pruned, so the dependency had to go rather than be trimmed.

  Nothing in `servicesources.yaml`, `servicesources.local.json` or the public API changes.

- **SSH repository URLs now work** ([#85]). `git@host:org/repo`, `host:org/repo` and `ssh://...`
  were previously refused at resolution time with a message pointing at the HTTPS equivalent,
  because LibGit2Sharp's bundled binaries had no SSH transport. They are now handed to `git` as
  written and resolved by your SSH agent and `~/.ssh/config`. Because nothing may block the
  AppHost on a prompt, SSH runs with `BatchMode=yes` unless you set your own `GIT_SSH_COMMAND`:
  an un-agented passphrase-protected key, or a host not yet in `known_hosts`, fails immediately
  rather than waiting. Connect once by hand to settle either.

- **Credential resolution is `git`'s own** ([#85]). Every `credential.helper` you have configured
  is consulted by git exactly as it is for a `git clone` you type yourself, rather than this
  package running `git credential fill` and passing the result to libgit2 — so helper ordering,
  per-URL config and `credential reject` on a refused credential all behave as git documents them.
  `SERVICESOURCES_GIT_USERNAME`/`SERVICESOURCES_GIT_TOKEN` still apply as a last resort, supplied
  as a helper of last resort so they never override one you configured; if a configured helper's
  credential is refused, the command is re-run once with those helpers cleared so the environment
  token still gets its turn. The per-process credential cache is gone with the plumbing that
  needed it, so a rotated token takes effect on the next resolution with nothing to clear.

- **`ServiceSourcesConfigurationException` prints as its message, not as a stack dump**
  ([#125]). These are raised from `AddService()` and normally end the AppHost unhandled, so the
  runtime's rendering of them *is* the error output — and it buried the sentence naming the fix
  under three nested inner-exception blocks and a stack trace per level, about thirty lines for a
  failed private clone. `ToString()` now prints the message plus one `caused by:` line per cause,
  and names `SERVICESOURCES_FULL_ERRORS=1` whenever it dropped anything; set that for the
  runtime's complete dump. `Message`, the `InnerException` chain and `StackTrace` are untouched,
  but anything logging one of these exceptions logs the summary unless that variable is set.

- **A clone or fetch that never resolved a credential says so** ([#125]), instead of reporting
  authentication as the likely cause. When no credential helper yields anything and neither
  `SERVICESOURCES_GIT_TOKEN` nor `SERVICESOURCES_GIT_USERNAME` is set, `git` falls through to
  asking a human and finds prompting disabled (`could not read Username for '<host>': terminal
  prompts disabled`) — a client-side dead end that never reached the host, so blaming a rejected
  token sent developers hunting for one they never had. The usual real cause is a credential
  helper that resolves in your shell but not in the environment the AppHost process inherits. A
  failure that did carry a credential keeps the old wording.

- CI type-checks the TypeScript export surface on every PR ([#88]). `samples/DemoAppHostTypeScript`
  regenerates its Aspire Type System SDK from the branch's own source tree — `aspire.config.json`
  resolves this package from `src/`, not from a published version — and compiles it under strict
  `tsc`, against a pinned Aspire CLI. Nothing else here compiles what these packages export to
  guest languages: the export test asserts the `[AspireExport]` attribute is *present*, and
  `aspire restore` exits 0 even when the TypeScript it just wrote does not compile.

### Fixed

- **Managed checkouts no longer inherit the AppHost repository's MSBuild and NuGet settings**
  ([#119]). A checkout is cloned into `<AppHostDirectory>/.servicesources/checkouts/<service>/`,
  and MSBuild, NuGet, the .NET SDK host and analyzer configuration all find their settings by
  walking *up* — so another team's repository was built under rules written for yours. Central
  package management failed a checkout's restore with `NU1008`; a `packageSourceMapping` could
  silently confine it to the wrong feeds; an `.editorconfig` could raise its analyzers to errors;
  a `global.json` applied your SDK pin. `.servicesources/` now carries six tool-managed files that
  stop those walks, refreshed on upgrade. A checkout bringing its own keeps its own.

  **Read before upgrading:** package source mapping is now *off* inside checkouts, including
  mappings from your user- and machine-level `nuget.config`, because NuGet's `<clear />` discards
  everything accumulated above it. Set `SERVICESOURCES_KEEP_PACKAGE_SOURCE_MAPPING=1` to keep it
  enforced. The README documents each barrier and where its coverage stops.

- **The TypeScript AppHost sample no longer needs an unreleased Aspire CLI** ([#88]). The README
  put the floor for `samples/DemoAppHostTypeScript` at CLI **13.6.0**, which is not released.
  Measured against released **13.5.3**: the generated SDK type-checks clean under strict `tsc` and
  the sample runs end-to-end. Nothing in the package changed — the requirement was stale. Aspire's
  codegen omits the `*Promise`/`*PromiseImpl` wrapper pair for a bare Aspire interface return
  ([microsoft/aspire#19507]) — the six `TS2552` errors the README warned about — but emits it when
  the interface appears as an extension-method *receiver*, which the eight `[AspireExport]` shims
  added in `0.3.0` declare, so they carry it for `addService` too. The real floor is **13.5.3**, for an unrelated reason: an older CLI pins
  its generated host project below this package's 13.5.2 Aspire floor and fails `aspire restore`
  with `NU1605`.

- **A service's `source` value is matched case-insensitively** ([#167]). `{ "source": "Local" }`
  failed the run with `Service 'orders' has source 'Local', which is not implemented yet.` — a
  message that sent the reader looking for an unbuilt feature when the only difference was the
  capital. Configuration keys have always been case-insensitive, so a value that is not reads as a
  bug rather than a rule, and it bites hardest through an environment variable, where capitalising
  `Source=Local` is the natural thing to write. All four source names now match in any casing.

  The parallel checkout prefetch had a quieter version of the same problem: a service spelled
  `"Local"` was dropped from the set of clones to start, so its checkout ran alone on the
  `AddService()` thread instead of alongside the others. No error, just a slower first start.

  The unknown-source message is reworded to name the sources that do exist, since with case folded
  it can now only fire for a name none of them has. `kind` names stay case-sensitive with their
  "did you mean" hint — those are an open registry satellite packages contribute to, where folding
  case could collide two packages' registrations, while the four source names are a closed set this
  package owns.

### Documentation

- **Who builds a `"local"` checkout, and when** ([#81]). Aspire does, on every start: a `dotnet`
  service is launched with `dotnet run` from inside the checkout, so a cold clone compiles on
  first use and a checkout whose `ref` moved is recompiled rather than served from the previous
  ref's binaries. Measured, along with the two things to know when it goes wrong — the compiler's
  output goes to the resource's console in the dashboard rather than the AppHost's, and two `path`
  services sharing a `ProjectReference` inside one repository can collide over that project's
  build output. Nothing in the package changed.

## [0.3.1] - 2026-08-27

Publishes the two satellite packages, which `0.3.0` could not. Core `0.3.0` is on nuget.org
and is unchanged by this release in everything but its version number — there is no reason to
move a core-only AppHost off it.

### Fixed

- **`KoalaSoft.Aspire.Hosting.ServiceSources.Java` and `.JavaScript` are published again**
  ([#117]). Both satellites were rejected by nuget.org during the `0.3.0` release and exist at
  no version on that feed; `0.3.1` is the first release either of them reaches it at. The
  `0.3.0` core package published normally in the same run, so a `0.3.0` AppHost using only
  core is unaffected.

  The cause was the prerelease upper bound `0.3.0` introduced to stop a satellite pairing with a
  next-minor core ([#79]). nuget.org's gallery refuses one at push time — `The package manifest
  contains an invalid Version: '0.4.0-0'`, HTTP 400 — while the NuGet client, `dotnet pack`,
  `restore` and GitHub Packages all accept it ([NuGetGallery#6948], open), and `pack` emits only
  NU5104, a warning. The bound was correct on every surface the repository could observe and wrong
  on the one a release touches.

  It is now chosen per build: `[0.3.1, 0.4.0)` on a stable build for nuget.org,
  `[0.3.1-alpha.0.7, 0.4.0-0)` on a prerelease build for GitHub Packages. Nothing is lost — every
  prerelease of these packages goes to GitHub Packages, so nuget.org has no `0.4.0-*` for the
  plain bound to admit, and `0.3.0`'s pairing guarantee holds on both feeds.

### Changed

- CI packs the release shape as well as the prerelease one, and fails on a stable package whose
  nuspec declares a prerelease version anywhere ([#117]). Every pack before this ran off a tag
  and so was always a prerelease, which is why `0.3.0` passed every check and then failed the
  push. `RELEASING.md` records the rest of the process.

## [0.3.0] - 2026-08-27

### Breaking

- **Removed the public `ServiceResource` type** ([#62]). `AddService()` used to return a builder
  over a `ServiceResource` facade that was never added to `builder.Resources`. It now returns a
  builder over the resource Aspire actually runs — a `ProjectResource` for `local`, a container or
  executable resource for `container` and `kubernetes`, or whatever an `ILocalResourceKind`
  returns. The declared return type is unchanged, `IResourceBuilder<IResourceWithServiceDiscovery>`
  before and after, so call sites that only pass the result to `WithReference(...)` or
  `GetEndpoint(...)` keep compiling. What breaks is code that *names* the type:

  ```csharp
  // No longer compiles - the type is gone:
  ServiceResource resource = builder.AddService("orders").Resource;
  if (builder.AddService("orders").Resource is ServiceResource) { /* ... */ }
  ```

  An assembly compiled against `0.2.0` that references `ServiceResource` throws
  `TypeLoadException` at runtime against this version. Recompile it.

  To configure the resolved service, use the new `Configure<T>()`, or `As<T>()` when you
  need the concrete resource type:

  ```csharp
  builder.AddService("orders")
      .Configure<IResourceWithEnvironment>(r => r.WithReference(ordersDb))
      .Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(migrations));

  IResourceBuilder<ProjectResource> web = builder.AddService("web").As<ProjectResource>();
  ```

  `Configure<T>` is how you reach an extension constrained to a capability interface: calling
  `service.WithEnvironment(...)` directly fails with `CS0311`, before this release and after.

  See **Changed** below for two things that keep compiling and change behavior: calls on the
  returned builder that used to no-op, and the `kubernetes` resource rename.

- **A satellite package now accepts only core versions within its own minor** ([#79]).
  `KoalaSoft.Aspire.Hosting.ServiceSources.Java` and `.JavaScript` used to declare a floor on
  `KoalaSoft.Aspire.Hosting.ServiceSources`, so NuGet was free to satisfy a satellite with any
  later core, including one whose interfaces had moved:

  ```xml
  <!-- before: any core at or above this version -->
  <dependency id="KoalaSoft.Aspire.Hosting.ServiceSources" version="0.3.0" />
  <!-- after: this minor only -->
  <dependency id="KoalaSoft.Aspire.Hosting.ServiceSources" version="[0.3.0, 0.4.0-0)" />
  ```

  A satellite implements core's `ILocalResourceKind`, so a mismatched pair failed at run time with
  `MissingMethodException`/`TypeLoadException` rather than at restore. The minor is the boundary
  because that is where a breaking change ships below `1.0.0`; a core patch still resolves, so core
  can be serviced without republishing every satellite.

  If your AppHost references core and a satellite separately, moving core alone to the next minor
  now fails restore with `NU1107` rather than building and throwing at startup. Move both
  together, or drop the core reference and let the satellite bring core in for you — which is what
  the README's install section now recommends.

### Added

- `Configure<T>()` and `As<T>()`, for configuring the resource a service resolved to from
  the AppHost that called `AddService()` ([#62], fixes [#53]). `Configure<T>` is skipped
  with a logged warning for the `url` and `kubernetes` sources, which run out of band;
  `As<T>` throws for them.
- A `kind` extension point for the `local` source: `ILocalResourceKind` and
  `AddLocalKind(...)` let a satellite package teach `local` to run a non-.NET service
  ([#55], closes [#41]).
- A `javascript` kind for the `local` source, in the satellite package
  `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` ([#59], closes [#44]). After
  `builder.UseJavaScript()`, a `local` service with `kind: javascript` is cloned like any other
  and run through `Aspire.Hosting.JavaScript`. Its `javascript:` block picks the integration —
  `javascript`, `vite`, `nextjs`, `node` or `bun` — plus the package manager, the app directory,
  and the HTTP endpoint a consumer's `WithReference(...)` resolves against.
- A `java` kind for the `local` source, in the satellite package
  `KoalaSoft.Aspire.Hosting.ServiceSources.Java` ([#60], closes [#45]). After
  `builder.UseJava()`, a `local` service with `kind: java` runs through the Aspire Community
  Toolkit's Java integration. Its `java:` block selects exactly one run mode — a Maven goal, a
  Gradle task, or a pre-built jar — and `mavenGoal`/`gradleTask` run the repository's own
  `mvnw`/`gradlew`, so a JDK is required but Maven or Gradle itself is not.
- Authentication for private git repositories, resolved through the developer's existing
  git credential helpers ([#56]).
- `AddService()` is exported to Aspire's Type System, so TypeScript AppHosts can call
  `builder.addService('orders')` ([#51]).
- Port bounds validation (1-65535) for the `kubernetes` source, matching the check the
  `container` source already had ([#40], fixes [#23]).

### Changed

- **The Java satellite accepts older Community Toolkit versions** ([#80]). Its floor was the
  version it happened to be developed against rather than the oldest carrying the API it
  calls, so consumers already on 13.3.0 were excluded for no reason. `WithMavenGoal`,
  `WithGradleTask` and `WithWrapperPath` all first ship in
  `CommunityToolkit.Aspire.Hosting.Java` **13.3.0**, so the floor moves from 13.4.0 to that.

  Nothing changes for consumers on a newer version: NuGet takes the lowest version satisfying
  every constraint, so a floor only decides how far *back* a consumer may go. The JavaScript
  satellite's floor was left alone here, since it moves in step with core's `Aspire.Hosting` —
  see **The Aspire floor moves from 13.4.6 to 13.5.2** ([#112]) below.

- **Calls on an `AddService()` result that used to do nothing now take effect** ([#62]).
  `IResourceWithServiceDiscovery` extends `IResourceWithEndpoints` and `IResource`, so every
  Aspire extension constrained to those already compiled against `AddService()`'s return type:

  ```csharp
  IResourceBuilder<IResourceWithServiceDiscovery> service = builder.AddService("orders");

  service.WithHttpEndpoint(targetPort: 1234);   // compiles - before and after
  service.WithExplicitStart();
  service.ExcludeFromManifest();
  service.WithAnnotation(annotation);
  ```

  Against the old facade these silently did nothing, because it was never in
  `builder.Resources`; they now land on the real, registered resource. Nothing stops compiling,
  so an AppHost carrying one changes behavior on upgrade with no diagnostic — re-read any such
  call as live rather than inert.
- **`kubernetes`-sourced resources are renamed from `{service}-portforward` to `{service}`**
  ([#62]). Aspire keys service discovery off the resource name, so the old name published the
  endpoint as `services__orders-portforward__…` and a consumer resolving `orders` never found
  it. The name is user-visible, though: it is what the dashboard shows, so anything keying off
  it — a `WithReference` by string, saved dashboard state, external log or trace queries
  filtering on resource name — sees a rename.
- Developer-config fields that a service's source does not use — `port` under a `local`
  source, `context` under a `container` source — now fail fast with a
  `ServiceSourcesConfigurationException` naming every offending field, instead of being
  silently ignored ([#43], fixes [#24]).
- `local` services resolve eagerly during `AddService()` rather than at `BeforeStartEvent`,
  because a real resource has to exist by the time `AddService()` returns ([#62]). Wall-clock is
  unchanged — the first `AddService()` prefetches every `local` service in the catalog in
  parallel. But a checkout failure now throws from the failing `AddService()` rather than being
  aggregated, and `AddLocalKind(...)` must be called before the first `AddService()`.
- Superseded preview packages are pruned from the GitHub Packages feed after each release,
  keeping the five most recent ([#68]).
- **The Aspire floor moves from 13.4.6 to 13.5.2** ([#112], fixes [#89]). `Aspire.Hosting` and
  `Aspire.Hosting.JavaScript` move together as one matched set, because the latter at 13.4.6 is
  the half that breaks once `Aspire.Hosting` reaches 13.5.x.

  **Move your AppHost's own Aspire version with it.** In an AppHost still on 13.4.x this lifts
  `Aspire.Hosting` alone, leaving `Aspire.AppHost.Sdk`, `Aspire.Hosting.AppHost` and the DCP and
  dashboard packages behind — a mixed Aspire family that restore reports nothing about. Raise
  `Aspire.AppHost.Sdk` and `Aspire.Hosting.AppHost` to 13.5.2 or later at the same time.

### Fixed

- **The published packages now carry the `polyglot` NuGet tag** ([#112]). It is what `aspire add`
  reads to surface a package to non-C# AppHosts — the audience this release's Type System exports
  exist for. Aspire's own targets append it only in the per-framework inner builds, while `pack`
  generates the nuspec from the cross-targeting outer pass, so all three packages packed without
  it. Appended in `Directory.Build.targets` instead, under Aspire's own condition so the two
  cannot both add it.

- **A `kind: javascript` service no longer throws `MethodAccessException` when Aspire resolves
  above the JavaScript integration** ([#112], fixes [#89]). An AppHost on Aspire 13.5.x pulled
  `Aspire.Hosting` up with it while nothing lifted `Aspire.Hosting.JavaScript` above the floor
  this package declared. 13.4.6 of it reaches into `Aspire.Hosting` internals across a
  friend-assembly boundary that 13.5.x revokes, so the pair restored and compiled clean and then
  threw the first time a `javascript` service resolved. Raising the floor settles it, and from
  Aspire 13.5.0 on `Aspire.Hosting.JavaScript` references no `Aspire.Hosting` internals at all.
  The Java satellite was never affected.

- A service consumed by a *container* now works for every source except `url` ([#62], fixes
  [#58]). The resource returned by `AddService()` is registered with the app model, so DCP
  creates the Service object a container-to-container reference needs. For `url`, a
  `BeforeStartEvent` pre-flight now reports the unsupported combination clearly instead of
  letting DCP fail with `Host endpoint ... should have an associated DCP Service resource`;
  lifting that limitation is tracked as [#72].

### Documentation

- nuget.org version and download badges, and a Preview builds section explaining that the
  GitHub Packages feed needs a `read:packages` token ([#67]).
- How to run several services out of one repository ([#64]).
- The install section now says to add a satellite package *instead of* the core package
  rather than alongside it, so an AppHost that uses one carries a single reference and has no
  second version to keep in step ([#79]).
- The guest-language section and the TypeScript sample now name the Aspire CLI version they
  need, 13.6.0 or newer - the codegen fix they depend on is in no released CLI yet ([#57]).

## [0.2.0] - 2026-08-18

### Added

- `net8.0` and `net9.0` targets alongside `net10.0` ([#39]).
- README documentation for the `url` and `container` sources, and a sample AppHost that
  exercises every source ([#39]).
- NuGet discoverability tags on the package ([#38]).

## [0.1.0] - 2026-08-18

Initial release, published to nuget.org as `KoalaSoft.Aspire.Hosting.ServiceSources`.
Targets `net10.0`.

### Added

- `builder.AddService("name")`, resolving a service from a `servicesources.yaml` catalog
  plus a per-developer `servicesources.local.json` override, so where a service comes from
  is a developer's choice rather than something baked into the AppHost.
- Four sources: `local` (a managed git checkout, or a self-managed working copy via
  `path`), `kubernetes` (a `kubectl port-forward` against a dev cluster), `url` (a fixed,
  already-known URL), and `container` (a published image run locally).
- Git checkout management over LibGit2Sharp: clone, fetch-and-retry for a ref that is not
  resolvable locally, and a guard against discarding uncommitted changes in a checkout.
- Local checkouts keyed per service under the AppHost directory, resolved in parallel at
  `BeforeStartEvent`.
- Fail-fast configuration validation with `ServiceSourcesConfigurationException`.
- MIT license, README, symbol packages, and Trusted Publishing (OIDC) to nuget.org.

[Unreleased]: https://github.com/flojon/aspire-servicesources/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/flojon/aspire-servicesources/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/flojon/aspire-servicesources/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/flojon/aspire-servicesources/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/flojon/aspire-servicesources/releases/tag/v0.1.0

[#23]: https://github.com/flojon/aspire-servicesources/issues/23
[#24]: https://github.com/flojon/aspire-servicesources/issues/24
[#38]: https://github.com/flojon/aspire-servicesources/pull/38
[#39]: https://github.com/flojon/aspire-servicesources/pull/39
[#40]: https://github.com/flojon/aspire-servicesources/pull/40
[#41]: https://github.com/flojon/aspire-servicesources/issues/41
[#43]: https://github.com/flojon/aspire-servicesources/pull/43
[#44]: https://github.com/flojon/aspire-servicesources/issues/44
[#45]: https://github.com/flojon/aspire-servicesources/issues/45
[#51]: https://github.com/flojon/aspire-servicesources/pull/51
[#53]: https://github.com/flojon/aspire-servicesources/issues/53
[#55]: https://github.com/flojon/aspire-servicesources/pull/55
[#56]: https://github.com/flojon/aspire-servicesources/pull/56
[#57]: https://github.com/flojon/aspire-servicesources/pull/57
[#58]: https://github.com/flojon/aspire-servicesources/issues/58
[#59]: https://github.com/flojon/aspire-servicesources/pull/59
[#60]: https://github.com/flojon/aspire-servicesources/pull/60
[#62]: https://github.com/flojon/aspire-servicesources/pull/62
[#64]: https://github.com/flojon/aspire-servicesources/pull/64
[#67]: https://github.com/flojon/aspire-servicesources/pull/67
[#68]: https://github.com/flojon/aspire-servicesources/pull/68
[#72]: https://github.com/flojon/aspire-servicesources/issues/72
[#79]: https://github.com/flojon/aspire-servicesources/issues/79
[#80]: https://github.com/flojon/aspire-servicesources/issues/80
[#81]: https://github.com/flojon/aspire-servicesources/issues/81
[#85]: https://github.com/flojon/aspire-servicesources/issues/85
[#88]: https://github.com/flojon/aspire-servicesources/issues/88
[#89]: https://github.com/flojon/aspire-servicesources/issues/89
[#112]: https://github.com/flojon/aspire-servicesources/pull/112
[#117]: https://github.com/flojon/aspire-servicesources/pull/117
[#119]: https://github.com/flojon/aspire-servicesources/issues/119
[#125]: https://github.com/flojon/aspire-servicesources/issues/125
[#130]: https://github.com/flojon/aspire-servicesources/issues/130
[#159]: https://github.com/flojon/aspire-servicesources/issues/159
[#160]: https://github.com/flojon/aspire-servicesources/issues/160
[#167]: https://github.com/flojon/aspire-servicesources/issues/167
[microsoft/aspire#19507]: https://github.com/microsoft/aspire/issues/19507
[NuGetGallery#6948]: https://github.com/NuGet/NuGetGallery/issues/6948
