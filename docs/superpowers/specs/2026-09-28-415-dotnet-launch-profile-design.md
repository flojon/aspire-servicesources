# Selecting a launch profile for the built-in `dotnet` kind (#415)

**Date:** 2026-09-28
**Status:** Draft
**Resolves:** #415 (`LocalProjectSource.Resolve` always calls the two-argument
`builder.AddProject(name, path)`, so a `dotnet` service cannot select a launch profile other than
the one Aspire picks by default, nor opt out of launch profiles).
**Relates to:** open PR #404 (checkout timing / deferred checkout) overlaps this change on
`ILocalResourceKind.cs`, `ServiceSourcesBuilderExtensions.cs`, `Sources/DeferredCheckout.cs`,
`LocalProjectSourceTests.cs`, `docs/sources/repository.md` and `CHANGELOG.md`. Not chased here;
whichever lands second rebases.

---

## 1. Facts this design rests on

Checked against the code at `bea1a02` and, for Aspire, against the pinned floor
(`Directory.Build.props`, `AspireVersion = 13.5.2`) by decompiling
`~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll`.

**In this repository**

- `Sources/LocalProjectSource.cs` (dotnet branch of `Resolve`) and `Sources/PathSource.cs` (same
  branch) both end in `builder.AddProject(serviceName, projectPath)`. Those are the only two
  `AddProject` call sites. The deferred path builds the resource by hand in
  `DeferredCheckout.Register`: `AddResource(new ProjectResource(..))` +
  `WithAnnotation<IProjectMetadata>(new DeferredProjectMetadata(projectPath))` +
  `WithProjectDefaults(new ProjectResourceOptions())` + `WithExplicitStart()`.
- The built-in `dotnet` kind never reads `ServiceDefinition.KindOptions`. The yaml loader
  (`Config/ServiceCatalogLoader.cs`, `kindBlockKey = metadata.Kind == LocalKinds.Dotnet ? null : ...`)
  deliberately treats a `dotnet:` block as an unknown property, and
  `ServiceCatalogLoaderTests.Load_StrayDotnetBlock_ThrowsNamingServiceAndProperty` pins that.
  `LocalKindRegistry.Register` refuses to register `"dotnet"`. So the `java:`/`javascript:` mechanism
  (opaque `KindConfig` parsed by an `ILocalResourceKind`) does not exist for `dotnet`; see section 3
  for why it is not reintroduced.
- `Sources/LandedLaunchProfile.cs` already reproduces Aspire's profile selection from the resource's
  annotations (`ExcludeLaunchProfileAnnotation`, `LaunchProfileAnnotation`,
  `DefaultLaunchProfileAnnotation`), and `DeferredCheckout.RestoreLaunchProfile` uses it after the
  clone to restore profile environment variables and to warn about un-mirrored `applicationUrl`
  endpoints. It therefore already does the right thing for both new options, provided the
  annotations are on the resource.
- `DeferredProjectMetadata.LaunchSettings` returns an **empty** `LaunchSettings` while the `.csproj`
  is missing, so no profile is found and none is required.
- Per-kind options live in the catalog (`servicesources.yaml` / the code catalog); the
  developer-config layer (`servicesources.local.json`) has no per-kind blocks. The ticket's phrase
  "developer-config yaml" is therefore read as "the yaml catalog".

**In Aspire 13.5.2**

- `AddProject(name, path, string? launchProfileName)` sets
  `ExcludeLaunchProfile = launchProfileName == null` and `LaunchProfileName = launchProfileName`.
  The `Action<ProjectResourceOptions>` overload exposes both properties independently.
- `WithProjectDefaults`: `ExcludeLaunchProfile` **wins outright** and `LaunchProfileName` is then
  never read (`if (options.ExcludeLaunchProfile) ... else if (!IsNullOrEmpty(LaunchProfileName))`).
  Selector order when neither is set: a `LaunchProfileAnnotation` (explicit name), then
  `DefaultLaunchProfileAnnotation` (from `AppHost:DefaultLaunchProfileName` /
  `DOTNET_LAUNCH_PROFILE`), then the first launchable profile in file order.
- The explicit-name selector returns the name **unconditionally**. `WithProjectDefaults` then calls
  `GetEffectiveLaunchProfile(throwIfNotFound: true)`, which throws `DistributedApplicationException`
  when a `launchSettings.json` exists but does not contain the name, **and returns null silently
  when no `launchSettings.json` exists at all** (`GetLaunchSettings` returns null, the dictionary is
  null, and the not-found branch is never reached).
- `LaunchProfile` and `LaunchSettings` are public, constructible types.

## 2. Consequences of those facts

1. A named profile on the **deferred** path would crash composition with today's placeholder:
   `DeferredProjectMetadata` returns an empty, non-null `Profiles` dictionary, so
   `GetLaunchProfile(name, throwIfNotFound: true)` throws for any explicit name. The placeholder
   must contain the requested name while the checkout is missing (section 6).
2. `excludeLaunchProfile` needs nothing special on the deferred path: with the annotation present,
   `GetLaunchSettings` short-circuits to null before `IProjectMetadata` is consulted.
3. Aspire's silent "named profile, no `launchSettings.json`" behavior would make a typo'd or stale
   catalog value do nothing. This design closes that gap with its own check (section 4).
4. Exclude plus a name is a contradiction Aspire resolves by silently dropping the name. This design
   rejects it (section 4).

## 3. Design: where the option lives

### yaml

A `dotnet:` block on a service whose `kind` is `dotnet` (the default). The shape mirrors `java:` and
`javascript:` (a block named after the kind):

```yaml
services:
  orders:
    repository: https://github.com/company/orders
    project: src/Orders.Api/Orders.Api.csproj
    dotnet:
      launchProfileName: http        # select this launchSettings.json profile
  worker:
    repository: https://github.com/company/worker
    project: src/Worker/Worker.csproj
    dotnet:
      excludeLaunchProfile: true     # ignore launchSettings.json entirely
```

| Field | Type | Meaning |
|---|---|---|
| `launchProfileName` | string | The `launchSettings.json` profile to use, matched exactly (no trimming). A blank or whitespace-only value is treated as absent. |
| `excludeLaunchProfile` | bool | `true`: Aspire ignores `launchSettings.json` (equivalent to `AddProject(..., launchProfileName: null)`). `false` or absent: no effect. |

### How it is modelled

The `dotnet` kind is resolved directly from top-level service metadata rather than through an
`ILocalResourceKind` (`LocalKinds.Dotnet` documents why), so the block is a **typed nested block on
`ServiceMetadata`**, like `kubernetes:`/`url:`/`container:`, not an opaque `KindConfig`:

- `Config/DotnetMetadata.cs` (internal, yaml-bound): `LaunchProfileName` (`string?`),
  `ExcludeLaunchProfile` (`bool?`).
- `ServiceMetadata.Dotnet` (`DotnetMetadata?`) and `ServiceDefinition.Dotnet`; `ToDefinition`
  copies it across.
- `KnownTopLevelProperties`/`KnownNestedProperties` in `ServiceCatalogLoader` are derived by
  reflection from the metadata types, so `dotnet:` becomes a known top-level property and a typo
  inside it (`launchProfile:`, `runScript:`) is rejected **at catalog load** with the existing
  "unknown property 'x' inside 'dotnet'" message. That keeps the guarantee the existing stray-block
  test pins: an unknown key under `dotnet:` is still a load-time error naming the service and
  `dotnet`. The test needs reworking only because a *valid* `dotnet:` block is no longer stray.
- `kindBlockKey` for `dotnet` stays null (there is still no opaque kind block).
  `IsReservedKindName("dotnet")` becoming true is harmless: `LocalKindRegistry.Register` already
  refuses that name.

Rejected alternative: route the block through `KindConfig` and `LocalKindConfig.Parse<T>` like
`java`/`javascript`. It works, but it moves typo detection from catalog load to resolution time,
reverses the loader's `dotnet` exemption and its explanatory comment, and gives a code-declared
service two competing stores (`WithKind("dotnet", obj)` next to `WithProject`).

### Cross-field checks (yaml at catalog load, code at `Build()`)

- `dotnet:` on a service whose `kind` is not `dotnet` is an error naming the service and the kind:
  the block would otherwise be silently ignored. For code this is checked in
  `ServiceDefinitionBuilder.Build`, so `AsDotnet` before or after `AsJava`/`WithKind` gives the same
  answer.
- Contradiction: `excludeLaunchProfile: true` together with a non-blank `launchProfileName` is an
  error (section 4). `excludeLaunchProfile: false` with a name is fine.

## 4. Precedence and validation

Resolved once by a shared static (`DotnetMetadata` to `{ Name?, Exclude }`), used by yaml and code
alike:

1. `excludeLaunchProfile: true` with a name: **configuration error** naming both fields, remedy
   "drop one". Rejected rather than "exclude wins" because Aspire's own precedence would make the
   name silently dead, which is the failure mode this design removes.
2. `excludeLaunchProfile: true` alone: exclude.
3. A non-blank `launchProfileName` alone: select it. This deliberately outranks the AppHost-level
   `AppHost:DefaultLaunchProfileName`/`DOTNET_LAUNCH_PROFILE` default, matching Aspire (an explicit
   `LaunchProfileAnnotation` beats `DefaultLaunchProfileAnnotation`): a catalog author who names a
   profile means it.
4. Neither: today's behavior, unchanged.

Check 1 runs at catalog load for yaml, and for a code-declared service at resolution before any
clone (next to `ValidateProject`), so it holds on the eager and deferred paths alike.

### Profile-must-exist check

When a name is configured, the named profile must exist in
`<project dir>/Properties/launchSettings.json`. Otherwise a `ServiceSourcesConfigurationException`
names the service, the configured value, the file, and the profiles it does contain (or says the
file is absent). This also covers the case Aspire is silent about (no file at all). The JSON read is
the tolerant one `LandedLaunchProfile` already does (comments and trailing commas allowed); a file
that cannot be parsed skips this check and leaves the outcome to Aspire, as `LandedLaunchProfile.Read`
treats unreadable as absent. It needs the working tree, so it runs after the project file is
resolved on the eager and `path` paths, and after the clone lands on the deferred path (section 6).

## 5. Eager path (`LocalProjectSource`, `PathSource`)

After `ResolveProjectFile` and the profile-must-exist check, call
`builder.AddProject(serviceName, projectPath, options => { options.LaunchProfileName = ..;
options.ExcludeLaunchProfile = ..; })` when either option is set, and the unchanged two-argument
call otherwise, so an unconfigured service is exactly what it is today. The options overload is used
rather than `AddProject(name, path, launchProfileName)` because it expresses name and exclude
independently and cannot turn a null name into an exclusion by accident.

`PathSource` gets the same wiring through a shared helper: a developer flipping a service from
`repository` to `path` must not silently change which profile runs. The `path` source has no
deferral. The other sources (`url`, `container`, `kubernetes`, `disabled`) never read the block, like
a `java:` block under those sources today.

## 6. Deferred-checkout path (`DeferredCheckout.Register`)

The deferred `dotnet` service is assembled by hand, so the option is applied by hand:

- `WithProjectDefaults(new ProjectResourceOptions { LaunchProfileName = .., ExcludeLaunchProfile = .. })`
  puts on the resource the same `LaunchProfileAnnotation` / `ExcludeLaunchProfileAnnotation` Aspire
  would add on the warm path. `LandedLaunchProfile.Read` and the endpoint warning then already
  follow the requested profile (its selector honors the annotation first) with no change.
- `DeferredProjectMetadata` takes the configured name and, **only while the project file is
  missing**, returns a `LaunchSettings` whose `Profiles` maps that name to an empty
  `LaunchProfile { CommandName = "Project" }`. That gets `throwIfNotFound` past composition. The
  profile carries no `applicationUrl`, so no endpoints are synthesised, which is exactly the
  existing cold-start cost `UseDeferredCheckout()` documents. Once the file exists it returns null
  again and Aspire reads the real file at start time, as today.
- After the clone lands, `RestoreLaunchProfile` runs the section 4 profile-must-exist check first. A
  configured profile that the landed repository does not have fails that service's start with the
  same message as the eager path (the existing post-clone failure channel), rather than letting
  Aspire fail later at executable creation.
- `excludeLaunchProfile: true`: annotation only. Cold and warm runs are identical (no endpoints, no
  profile environment, no `DOTNET_LAUNCH_PROFILE`) and no endpoint warning is issued, because
  `LandedLaunchProfile.Read` returns empty for an excluded resource. This is the trade
  `UseDeferredCheckout()` already documents, now chosen explicitly.
- Named profile, cold run: environment and `DOTNET_LAUNCH_PROFILE=<name>` are restored after the
  clone, and the endpoint warning fires if that profile has an `applicationUrl` the AppHost did not
  declare, exactly as for the default profile today.
- `SupportsDeferredCheckout` and the prefetch are unaffected: the `dotnet` kind already always defers.

## 7. Code catalog API

Mirrors `AsJava`/`AsJavaScript` (a fluent options handle, so the internal yaml-bound type stays
non-public):

```csharp
catalog.AddService("orders")
    .WithRepository("https://github.com/company/orders")
    .WithProject("src/Orders.Api/Orders.Api.csproj")
    .AsDotnet(o => o.WithLaunchProfileName("http"));

catalog.AddService("worker")
    .WithRepository("https://github.com/company/worker")
    .WithProject("src/Worker/Worker.csproj")
    .AsDotnet(o => o.ExcludeLaunchProfile());
```

- `Dotnet/DotnetKindOptionsBuilder` (`[AspireExport(ExposeMethods = true)]`, internal ctor):
  `WithLaunchProfileName(string)` and `ExcludeLaunchProfile()`, writing into an internal
  `DotnetMetadata`.
- `Dotnet/DotnetServiceSourcesBuilderExtensions.AsDotnet(this ServiceDefinitionBuilder,
  Action<DotnetKindOptionsBuilder>)` (`[AspireExport]`). Unlike `AsJava` it does **not** call
  `WithKind` (`dotnet` is the default kind and takes no options object); it stores the metadata,
  guarded by `RequireUnset` so a second `AsDotnet` throws the same "already called" error as every
  other block.
- The section 3 cross-field checks run in `Build()`. `WithLaunchProfileName` given null or blank
  throws immediately, in the repo's usual message shape.
- Both new public types are ATS-exported; the Aspire CLI TypeScript SDK typecheck job (CI-only)
  covers them.

## 8. Attack surface and trust

- The catalog is shared team configuration and the checkout is repository content. Selecting a
  profile *name* adds no capability: the profile bodies (`commandLineArgs`, `environmentVariables`,
  and `executablePath` for `commandName: Executable`, which Aspire allows) already come from the
  checkout, and Aspire's default selection already picks one of them. The option only chooses
  which; it cannot introduce a value the repository did not carry. A catalog author who can name a
  profile could already commit a `launchSettings.json` whose first profile does the same.
- `excludeLaunchProfile` strictly reduces what is taken from the repository (no profile
  environment, arguments or endpoints).
- The name is only compared with keys of the checkout's JSON. This package never uses it in a path
  or command line; `DOTNET_LAUNCH_PROFILE` is set only in the deferred restore, and only to the name
  of a profile that was found in the file.
- Error text embeds catalog- and repository-derived strings (the configured name, the profile names
  in the file, file paths). All go through the structural escaping (`Name`, `Raw.Escaped`,
  `Raw.Join`) so a hostile profile key cannot inject terminal control sequences or forge lines in an
  exception message.
- The `launchSettings.json` read is the file `LandedLaunchProfile` already reads and fails closed to
  "skip our check", never to running anything.

## 9. Non-goals

- No per-developer override in `servicesources.local.json` (java/javascript options are catalog-only
  too). See Open Questions.
- No `ExcludeKestrelEndpoints` or other `ProjectResourceOptions` members.
- No launch-profile option for other kinds (`java`, `javascript` have no launch profiles).
- No change to default profile selection when neither option is set.
- No attempt to synthesise endpoints from the named profile on a cold deferred run (impossible for
  the reason `DeferredCheckout`'s class comment gives).

## 10. Documentation and changelog

- `docs/guides/yaml-catalog.md`: the `dotnet:` block in the catalog schema, with an example.
- `docs/guides/catalog-in-code.md`: `AsDotnet` next to the other per-kind helpers.
- `docs/sources/repository.md` (the cold-checkout section on lost profile endpoints) and
  `docs/sources/path.md`: a paragraph each on the option, its precedence over the AppHost default,
  and the deferred-run behavior above.
- `CHANGELOG.md`, `## [Unreleased]`, `### Added`: the `dotnet:` block, `AsDotnet`, and the
  contradiction and missing-profile errors. Not Breaking (no valid catalog changes meaning), not
  Changed or Fixed (0.7.0 is the last tag and nothing shipped behaves differently).

## 11. Testing (pointer for the plan)

Each behavior is a test written first:

- Loader: `dotnet:` fields bind; an unknown key inside `dotnet:` is rejected at load (rework
  `Load_StrayDotnetBlock_...`); `dotnet:` on `kind: java` is rejected naming both; blank
  `launchProfileName:` is absent.
- Builder: `AsDotnet` sets the definition; repeated call throws; `AsDotnet` with `AsJava` throws in
  either order; blank name throws.
- Resolution (`LocalProjectSourceTests`, `PathSourceTests`): name selected (assert the
  `LaunchProfileAnnotation` value, and that it beats a configured `AppHost:DefaultLaunchProfileName`);
  exclude yields `ExcludeLaunchProfileAnnotation` and no endpoints; both set throws; a missing named
  profile throws the escaped message listing available profiles; no `launchSettings.json` at all
  throws; an unconfigured service is unchanged.
- Deferred (`DeferredCheckoutTests`): a named profile survives composition with the checkout absent,
  restores `DOTNET_LAUNCH_PROFILE`/env after landing, and fails the start if the landed repo lacks
  it; an exclude cold run restores nothing and warns nothing.
- Escaping: a profile key containing control characters is not reproduced raw in an error.

## Open Questions

1. **Per-developer override.** Should `servicesources.local.json` be able to override
   `launchProfileName`/`excludeLaunchProfile` for one machine? Proposed: no (mirrors java/javascript
   being catalog-only); revisit on demand. It is a larger change (new developer-config block,
   validator, precedence).
2. **No-`launchSettings.json` strictness.** Proposed: a named profile with no file at all is an
   error (the name can never resolve), which is stricter than Aspire's silent no-op. The alternative
   is to match Aspire and error only when the file exists but lacks the name.
3. **Naming.** `AsDotnet`/`WithLaunchProfileName`/`ExcludeLaunchProfile()` follow the sibling
   `AsJava`/`With*` conventions; no strong preference if the maintainer wants a single
   `WithLaunchProfile(string?)` instead.
