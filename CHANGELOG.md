# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
version is below `1.0.0`, a breaking change can ship in a minor release, so each one gets a
**Breaking** entry saying what breaks and how to migrate. A change that keeps compiling but
behaves differently is called out under **Changed** — read those before upgrading too, since
nothing will fail to build to warn you.

## [Unreleased]

### Breaking

- **Removed the public `ServiceResource` type** ([#62]). `AddService()` used to return a
  builder over a `ServiceResource` facade that was deliberately never added to
  `builder.Resources`. It now returns a builder over the resource Aspire actually runs — a
  `ProjectResource` for `local`, an Aspire container or executable resource for `container`
  and `kubernetes`, or whatever an `ILocalResourceKind` returns.

  `AddService()`'s declared return type is unchanged,
  `IResourceBuilder<IResourceWithServiceDiscovery>` before and after, so call sites that
  only pass the result to `WithReference(...)` or `GetEndpoint(...)` keep compiling. What
  breaks is code that *names* the type:

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

  These exist because most of Aspire's configuration extensions cannot bind to
  `IResourceBuilder<IResourceWithServiceDiscovery>` at all, facade or not: `WithEnvironment`
  is constrained to `IResourceWithEnvironment`, which `IResourceWithServiceDiscovery` does
  not extend, so `service.WithEnvironment(...)` has never compiled — before or after — and
  fails with `CS0311`. Reaching that API is what `Configure<T>` is for.

  See **Changed** below for two things that keep compiling and change behavior: calls on the
  returned builder that used to no-op, and the `kubernetes` resource rename.

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
  `builder.UseJavaScript()`, a `local` service with `kind: javascript` is cloned like any
  other and then run through `Aspire.Hosting.JavaScript`. Its `javascript:` block picks the
  integration — `javascript`, `vite`, `nextjs`, `node` or `bun` — plus the package manager,
  the app directory within the checkout, and the HTTP endpoint a consumer's
  `WithReference(...)` resolves against.
- A `java` kind for the `local` source, in the satellite package
  `KoalaSoft.Aspire.Hosting.ServiceSources.Java` ([#60], closes [#45]). After
  `builder.UseJava()`, a `local` service with `kind: java` runs through the Aspire Community
  Toolkit's Java integration. Its `java:` block selects exactly one run mode — a Maven goal,
  a Gradle task, or a pre-built jar — and `mavenGoal`/`gradleTask` execute the repository's
  own `mvnw`/`gradlew` wrapper, so a JDK is required on the machine but Maven or Gradle
  itself is not.
- Authentication for private git repositories, resolved through the developer's existing
  git credential helpers ([#56]).
- `AddService()` is exported to Aspire's Type System, so TypeScript AppHosts can call
  `builder.addService('orders')` ([#51]).
- Port bounds validation (1-65535) for the `kubernetes` source, matching the check the
  `container` source already had ([#40], fixes [#23]).

### Changed

- **A satellite package now requires the exact core version it was built against** ([#79]).
  `KoalaSoft.Aspire.Hosting.ServiceSources.Java` and `.JavaScript` used to declare a floor on
  `KoalaSoft.Aspire.Hosting.ServiceSources`, so NuGet was free to satisfy the satellite with
  any newer core:

  ```xml
  <!-- before: any core at or above this version -->
  <dependency id="KoalaSoft.Aspire.Hosting.ServiceSources" version="0.2.0" />
  <!-- after: this core exactly -->
  <dependency id="KoalaSoft.Aspire.Hosting.ServiceSources" version="[0.2.0]" />
  ```

  A satellite implements core's `ILocalResourceKind`, so a mismatched pair failed at run time
  with `MissingMethodException`/`TypeLoadException` rather than at restore. Every package in
  the repo is versioned in lockstep from one release tag, so the matching core always exists.

  Upgrade both together — a satellite pinned to `0.2.x` will not accept core `0.3.0`. If you
  reference only the satellite, restore pulls the right core for you.

- **Calls on an `AddService()` result that used to do nothing now take effect** ([#62]).
  `IResourceWithServiceDiscovery` extends `IResourceWithEndpoints` and `IResource`, so every
  Aspire extension constrained to those already bound to `AddService()`'s return type and
  compiled:

  ```csharp
  IResourceBuilder<IResourceWithServiceDiscovery> service = builder.AddService("orders");

  service.WithHttpEndpoint(targetPort: 1234);   // compiles - before and after
  service.WithExplicitStart();
  service.ExcludeFromManifest();
  service.WithAnnotation(annotation);
  ```

  Against the old facade these silently did nothing, because it was never in
  `builder.Resources`. They now land on the real, registered resource and actually happen.
  Nothing here stops compiling, so an AppHost carrying one of these calls changes behavior on
  upgrade with no diagnostic: re-read any such call on an `AddService()` result as live
  rather than inert.
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
  because a real resource has to exist by the time `AddService()` returns ([#62]). Cold
  checkouts still run in parallel — the first `AddService()` starts a prefetch for every
  `local` service in the catalog — so wall-clock is unchanged. Consequences: a checkout
  failure now throws from the failing `AddService()` call rather than being aggregated
  across services, `ILocalResourceKind.Validate` no longer runs before any service has
  touched the app model, and `AddLocalKind(...)` must be called before the first
  `AddService()`.
- Superseded preview packages are pruned from the GitHub Packages feed after each release,
  keeping the five most recent ([#68]).

### Fixed

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

[Unreleased]: https://github.com/flojon/aspire-servicesources/compare/v0.2.0...HEAD
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
[#58]: https://github.com/flojon/aspire-servicesources/issues/58
[#59]: https://github.com/flojon/aspire-servicesources/pull/59
[#60]: https://github.com/flojon/aspire-servicesources/pull/60
[#62]: https://github.com/flojon/aspire-servicesources/pull/62
[#64]: https://github.com/flojon/aspire-servicesources/pull/64
[#67]: https://github.com/flojon/aspire-servicesources/pull/67
[#68]: https://github.com/flojon/aspire-servicesources/pull/68
[#72]: https://github.com/flojon/aspire-servicesources/issues/72
[#79]: https://github.com/flojon/aspire-servicesources/issues/79
