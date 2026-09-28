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
  `dotnet`. That test (it uses `runScript: dev`) keeps passing unchanged; only its comment, which
  says a `dotnet:` block is always stray, goes stale and is updated.
- `DotnetMetadata` must live in namespace `Aspire.Hosting.ServiceSources.Config` (the loader's
  `IsNestedBlock` requires the same namespace as `ServiceMetadata`); `bool?` and `string?` members
  are scalars and are not treated as nested blocks.
- `kindBlockKey` for `dotnet` stays null (there is still no opaque kind block).
  `IsReservedKindName("dotnet")` becoming true is harmless: `LocalKindRegistry.Register` already
  refuses that name. The loader comment above `kindBlockKey` ("a `dotnet:` block is always stray or
  misspelled") becomes false and is rewritten. A scalar or list under `dotnet:`, or a non-bool
  `excludeLaunchProfile`, fails in the typed YamlDotNet pass like every other typed block.

Rejected alternative: route the block through `KindConfig` and `LocalKindConfig.Parse<T>` like
`java`/`javascript`. It works, but it moves typo detection from catalog load to resolution time,
reverses the loader's `dotnet` exemption and its explanatory comment, and gives a code-declared
service two competing stores (`WithKind("dotnet", obj)` next to `WithProject`).

### Cross-field checks (yaml in `ServiceCatalogLoader.Load`, code in `ServiceDefinitionBuilder.Build`)

Both checks run at exactly one place per origin, share one implementation, and are not re-run at
resolution:

- `dotnet:` on a service whose `kind` is not `dotnet` is an error naming the service and the kind:
  the block would otherwise be silently ignored. For code, `Build()` is the only place that sees
  both calls, so `AsDotnet` before or after `AsJava`/`WithKind` gives the same answer.
- Contradiction: `excludeLaunchProfile: true` together with a non-blank `launchProfileName` is an
  error (section 4). `excludeLaunchProfile: false` with a name is fine.

They apply to the catalog entry whatever source is eventually selected: a `dotnet:` block on an
entry a developer currently runs through `url` or `container` is validated but otherwise unread
(it takes effect the day they switch that service to `repository` or `path`), the same way a
`java:` block survives under other sources today. The profile-must-exist check (section 4) needs a
working tree and therefore only runs for the sources that have one.

## 4. Precedence and validation

Resolved once by a shared static, `DotnetMetadata.Resolve(...)` returning `{ Name?, Exclude }`,
used by the loader, `Build()` and both `AddProject` call sites:

1. `excludeLaunchProfile: true` with a name: **configuration error** naming both fields, remedy
   "drop one". Rejected rather than "exclude wins" because Aspire's own precedence would make the
   name silently dead, which is the failure mode this design removes.
2. `excludeLaunchProfile: true` alone: exclude.
3. A non-blank `launchProfileName` alone: select it. This deliberately outranks the AppHost-level
   `AppHost:DefaultLaunchProfileName`/`DOTNET_LAUNCH_PROFILE` default, matching Aspire (an explicit
   `LaunchProfileAnnotation` beats `DefaultLaunchProfileAnnotation`): a catalog author who names a
   profile means it.
4. Neither: today's behavior, unchanged.

Check 1 runs at catalog load for yaml and in `Build()` for code (section 3), so it is settled before
any clone and holds on the eager, `path` and deferred paths alike.

Blank handling is deliberately asymmetric. A blank yaml scalar (`launchProfileName:`) means "unset",
the repo-wide rule for blank catalog scalars (`ServiceCatalogLoader` does the same for
`defaultSource`). A code call `WithLaunchProfileName(null or blank)` throws immediately, because an
explicit call with nothing to say is a mistake in the AppHost, not a cleared line in a file. A
padded name such as `" http "` is non-blank, is used verbatim, and therefore fails the must-exist
check below with the profiles the file does contain.

### Profile-must-exist check

When a name is configured, the named profile must exist in
`<project dir>/Properties/launchSettings.json`. Otherwise a `ServiceSourcesConfigurationException`
names the service, the configured value, the file, and the profiles it does contain (or says the
file is absent). This also covers the case Aspire is silent about (no file at all).

One shared reader owns the file read: `LandedLaunchProfile` gains
`ProfileNames(string projectFile)` returning the profile names, or a distinct "absent" / "unreadable"
result, using the tolerant parse it already has (comments and trailing commas allowed). The check
consumes it from the eager path, `PathSource` and the deferred restore, and runs **before**
`LandedLaunchProfile.Read`, which returns `Empty` for a missing named profile and would otherwise
hide the problem. An unreadable (unparseable) file skips the check and leaves the outcome to Aspire,
as `Read` treats unreadable as absent. The check needs the working tree, so it runs after the project
file is resolved on the eager and `path` paths, and after the clone lands on the deferred path
(section 6): the placeholder in section 6 is what lets composition pass, so on the deferred path a
wrong name surfaces post-clone rather than at composition.

## 5. Eager path (`LocalProjectSource`, `PathSource`)

After `ResolveProjectFile` and the profile-must-exist check, call
`builder.AddProject(serviceName, projectPath, options => { options.LaunchProfileName = ..;
options.ExcludeLaunchProfile = ..; })` when either option is set, and the unchanged two-argument
call otherwise, so an unconfigured service is exactly what it is today. The options overload is used
rather than `AddProject(name, path, launchProfileName)` because it expresses name and exclude
independently and cannot turn a null name into an exclusion by accident.

`PathSource` gets the same wiring through one shared internal helper,
`LocalProjectSource.AddDotnetProject(builder, serviceName, projectPath, definition.Dotnet)`, which
owns the "options overload only when something is set" rule for both call sites: a developer
flipping a service from `repository` to `path` must not silently change which profile runs. The `path` source has no
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
  again and Aspire reads the real file at start time, as today. A side effect worth knowing: with a
  non-null effective profile at composition, Aspire's own `WithProjectDefaults` registers its
  `DOTNET_LAUNCH_PROFILE` environment callback (`TryAdd`) on the deferred path too, using the
  catalog's name before any file check. The existing `RestoreLaunchProfileEnvironment` only writes
  that key when absent, so for a named profile it becomes a no-op for that variable and still
  restores the profile's other variables. Today's null profile takes the early-return branch of
  `WithProjectDefaults`; the placeholder takes the continuing one, which adds no endpoints because
  the placeholder has no `applicationUrl`.
- After the clone lands, `RestoreLaunchProfile` runs the section 4 profile-must-exist check first. A
  configured profile that the landed repository does not have fails that service (`FailedToStart`
  on every resource `AllResources` withholds) before it starts, rather than letting Aspire fail
  later at executable creation. That failure goes through `ReportFailureAsync`, whose text is
  written for a clone that did not complete ("its checkout was deferred past startup and did not
  complete"); that is wrong for a checkout that did land, and equally wrong today for the existing
  `ResolveProjectFile` throw in the same method. The prefix is reworded to cover a failed
  post-clone check, and the profile message stays self-explanatory.
- `excludeLaunchProfile: true`: annotation only. Cold and warm runs are identical (no endpoints, no
  profile environment, no `DOTNET_LAUNCH_PROFILE`) and no endpoint warning is issued, because
  `LandedLaunchProfile.Read` returns empty for an excluded resource. This is the trade
  `UseDeferredCheckout()` already documents, now chosen explicitly.
- Named profile, cold run: the profile's environment variables are restored after the clone, and
  the endpoint warning fires if that profile has an `applicationUrl` the AppHost did not declare,
  exactly as for the default profile today.
- `SupportsDeferredCheckout` and the prefetch are unaffected: the `dotnet` branch of
  `LocalProjectSource.Resolve` never consults `SupportsDeferredCheckout` (only non-dotnet kinds do),
  and `Register` still calls `prefetch.StartCheckout` as before. Deferral itself remains opt-in
  (`UseDeferredCheckout()`, run mode, cold managed checkout).
- The stale `DeferredProjectMetadata` remark that `ExcludeLaunchProfile` is "worse" for the cold case
  is rewritten: it is now the explicit, chosen behavior when the catalog asks for it.

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
  throws immediately, in the repo's usual message shape (section 4, blank handling).
- `ServiceDefinition.Dotnet` is an optional `init` property, so no existing construction site
  changes; only `ServiceMetadata.ToDefinition` and `ServiceDefinitionBuilder.Build` populate it.
- Both new public types are ATS-exported; the Aspire CLI TypeScript SDK typecheck job (CI-only)
  covers them. The TypeScript sample (`samples/DemoAppHostTypeScriptCodeCatalog`) is not extended.

## 8. Attack surface and trust

- The catalog is shared team configuration and the checkout is repository content. Selecting a
  profile *name* chooses among profiles the repository already carries; it cannot introduce one.
  The trust boundary is the same as running that repository's default profile (its
  `commandLineArgs` and `environmentVariables` are what runs), but the choice of *which* profile is
  now the catalog author's, so a catalog can point at a non-default profile (a "Docker" or
  "Debug-Seed" profile, say) that the default selection would never run. That is the feature, and
  it is why the name is matched exactly and a nonexistent name is an error rather than a silent
  fall-back to the default.
- `excludeLaunchProfile` strictly reduces what is taken from the repository (no profile
  environment, arguments or endpoints).
- The name is used as a key compared against the checkout's JSON and as the value of
  `DOTNET_LAUNCH_PROFILE` (which Aspire itself sets from the selected profile name on the warm path
  and, with the section 6 placeholder, at composition on the deferred path). This package never
  puts it in a path or command line. On the deferred path that variable therefore briefly carries
  the catalog string before the post-clone check has confirmed it exists; the check fails the start
  before the process runs, so no process ever sees an unconfirmed name.
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

- Loader: `dotnet:` fields bind; an unknown key inside `dotnet:` is rejected at load
  (`Load_StrayDotnetBlock_...` keeps passing; update its comment); `dotnet:` on `kind: java` is
  rejected naming both; blank `launchProfileName:` is absent; exclude plus name is rejected; a
  scalar under `dotnet:` and a non-bool `excludeLaunchProfile` fail.
- Builder: `AsDotnet` sets the definition; repeated call throws; `AsDotnet` with `AsJava` throws in
  either order; blank name throws.
- Resolution (`LocalProjectSourceTests`, `PathSourceTests`): name selected (assert the
  `LaunchProfileAnnotation` value, and that it beats a configured `AppHost:DefaultLaunchProfileName`);
  exclude yields `ExcludeLaunchProfileAnnotation` and no endpoints; both set throws; a missing named
  profile throws the escaped message listing available profiles; no `launchSettings.json` at all
  throws; an unconfigured service is unchanged.
- Deferred (`DeferredCheckoutTests`): a named profile survives composition with the checkout absent,
  restores the profile's env after landing, and fails the start (all `AllResources` marked
  `FailedToStart`, message not claiming the clone failed) if the landed repo lacks it; an exclude
  cold run restores nothing and warns nothing.
- Escaping: a profile key containing control characters is not reproduced raw in an error.

## Open Questions

1. **Reading of the ticket.** The ticket says "developer-config yaml"; this spec reads that as the
   yaml *catalog* (`servicesources.yaml`), since per-kind blocks live there and the developer-config
   layer has none. If a per-machine setting was meant, see item 2.
2. **Per-developer override.** Should `servicesources.local.json` be able to override
   `launchProfileName`/`excludeLaunchProfile` for one machine? Proposed: no (mirrors java/javascript
   being catalog-only); revisit on demand. It is a larger change (new developer-config block,
   validator, precedence).
3. **No-`launchSettings.json` strictness.** Proposed: a named profile with no file at all is an
   error (the name can never resolve), which is stricter than Aspire's silent no-op. The alternative
   is to match Aspire and error only when the file exists but lacks the name.
4. **Naming.** `AsDotnet`/`WithLaunchProfileName`/`ExcludeLaunchProfile()` follow the sibling
   `AsJava`/`With*` conventions; no strong preference if the maintainer wants a single
   `WithLaunchProfile(string?)` instead.
