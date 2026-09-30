# Repository-level `path` for a `repositoryRef` group Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `repositories.<name>.path` in `servicesources.local.json` (or its env/user-secrets spelling) points every member of a `repositoryRef` group at one already-checked-out directory, with a member's own `repository.path` still winning (#416).

**Architecture:** One seam, `LocalGitCheckout.EffectivePath(definition, serviceName, config, repositoryConfig)`, answers "which directory, if any, does the developer supply for this member" (own path, else the group's when grouped). `IsManagedCheckout`, `IsColdManagedCheckout`, `PrepareRepoRoot`, `DeferredCheckout.ShouldDefer`, `RequireRepositoryToCheckOut` and `PreparePlan.For` all read through it so prefetch, deferral, prepare and the "no repository to clone" exemption cannot disagree. No config-shape change: the reserved rejection is removed and the field gets its meaning. A separate, small audit addition reports `repositories` entries that name no declared repository.

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), xUnit, Aspire.Hosting.

**Spec:** `docs/superpowers/specs/2026-09-30-416-repository-level-path-design.md`. Read it first; "decision N" below refers to its section 2.

## Global Constraints

- Source in `src/Aspire.Hosting.ServiceSources`, tests in `test/Aspire.Hosting.ServiceSources.Tests` (one module).
- No new config key and no change to `DeveloperConfigShape`/`DeveloperConfigValidator`: the file key is `repositories.<name>.path`, config key `ServiceSources:Repositories:<name>:path`, env `ServiceSources__Repositories__<name>__Path`.
- Precedence, first set wins: member `repository.path` (alias `local.path`) > group `path` > managed checkout (group `ref` > catalog `defaultRef`).
- Group `path` applies only to a grouped service (`IsGrouped`); an ungrouped service of the same name ignores it.
- Relative values resolve against the AppHost directory (not the json file); the directory must exist; a missing one throws `ServiceSourcesConfigurationException` naming the repository and `ServiceSources:Repositories:<name>:path`.
- Group `path` plus group `ref` is an error naming the repository and both keys, raised whether or not the resolving member has its own path. Catalog `defaultRef` is ignored silently. A member's own `repository.ref` stays refused.
- No git calls, no clone, no fetch, no deferral for a member that resolves to a path. Builds stay unserialized.
- `LocalPathDeprecationNotice` is not emitted for a group-sourced path; it still fires for a member's own `repository.path`.
- All catalog/config-derived strings in error text go through `Name`, `Raw.Escaped`, `Raw.Join`, `Raw.Literal`/`Raw.Compose` like neighbouring messages.
- Build must pass `dotnet build -c Release --no-restore -warnaserror` (XML docs as neighbours have). Cheap test leg every task: `dotnet test -f net10.0`; while iterating `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~<Name>"`.
- Code comments: only the non-obvious WHY, one short sentence, no ticket/PR references.

## Review Focus

Failure modes the spec implies that no headline test covers, most likely first. Each has a test in the owning task.

1. A typo'd repository name in `repositories` silently leaves members cloning into the managed checkout (Task 4: audit reports it with a did-you-mean; a case-variant of a declared name is NOT reported).
2. A mixed group, where one member has its own path and its siblings do not: siblings must get the group path, and prefetch must not start a clone for a member that resolves to a path (Tasks 1, 2).
3. A group `path` with group `ref` where the resolving member also has its own `repository.path`: still an error (Task 1).
4. A group path written as an environment variable with different casing (`ServiceSources__Repositories__MONOREPO__Path`) binds to the declared name (Task 5).
5. A group path pointing at a missing directory reports the repository and key, not a member service (Task 1).
6. The catalog `prepare` ignored-notice must not tell a group-path developer to set a `repository.path` they did not write (Task 3).

---

## File Structure

- Modify `Git/LocalGitCheckout.cs`: `EffectivePath`, `PathComesFromGroup`, `GroupPathKey`, subject-aware `ResolveDeveloperDirectory`, `PrepareRepoRoot` branch, predicate signatures.
- Modify `Sources/LocalProjectSource.cs`, `Sources/DeferredCheckout.cs`, `Sources/LocalCheckoutPrefetch.cs`, `Sources/PathSource.cs` (one call site): thread `repositoryConfig` through.
- Modify `Prepare/PreparePlan.cs`: optional `groupPathKey`.
- Modify `Config/DeveloperConfiguration.cs`, `Config/ServiceConfigAudit.cs`: undeclared repository entries.
- Modify `Config/RepositoryDeveloperConfig.cs` (doc comment only).
- Docs: `docs/sources/repository.md`, `docs/guides/configuration.md`, `docs/sources/path.md`, `CHANGELOG.md`.
- Tests: `Git/LocalGitCheckoutTests.cs`, `Sources/LocalProjectSourceTests.cs`, `Sources/DeferredCheckoutTests.cs`, `Sources/LocalCheckoutPrefetchTests.cs`, `Prepare/PreparePlanTests.cs`, `Config/DeveloperConfigurationTests.cs`, `Config/ServiceConfigAuditTests.cs`, `Config/ServiceSourcesConfigCacheTests.cs` (all under `test/Aspire.Hosting.ServiceSources.Tests`).

Task order is real: 1 makes `PrepareRepoRoot` honour the group path (additive, no signature changes); 2 makes every predicate and caller agree with it (signature change, fixed at all call sites in the same task); 3 fixes notices and prepare wording, which need 2's `managedCheckout`; 4 and 5 are config-level and independent of 1-3; 6 is docs.

---

### Task 1: `PrepareRepoRoot` resolves the group path

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs` (`ResolveDeveloperDirectory` ~190, `PrepareRepoRoot` ~311-360, its doc comment ~298-310)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/PathSource.cs:145` (call site)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/RepositoryDeveloperConfig.cs` (drop "reserved" wording)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs` (it already holds `GroupedDefinition`, `DevConfig`, `FakeGitClient`, `ResolveProjectPath` and the reserved-error test being replaced)

**Interfaces:**
- Consumes: existing `LocalGitCheckout.IsGrouped(definition, serviceName)`, `PreparePlan.ServiceLabel(string)` / `PreparePlan.RepositoryLabel(string)` (both return `Raw`).
- Produces (later tasks rely on these exact names):
  - `internal static string GroupPathKey(string checkoutName)` returns `$"{DeveloperConfiguration.RepositoriesKey}:{checkoutName}:path"`.
  - `internal static string? EffectivePath(ServiceDefinition definition, string serviceName, ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig)` returns `config.Repository.Path ?? (IsGrouped(definition, serviceName) ? repositoryConfig?.Path : null)`.
  - `internal static bool PathComesFromGroup(ServiceDefinition definition, string serviceName, ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig)` true iff `config.Repository.Path is null && IsGrouped(...) && repositoryConfig?.Path is not null`.
  - `ResolveDeveloperDirectory(Raw subject, string key, string path, string appHostDirectory)` (was `string serviceName`); the message begins with `subject`.

- [ ] **Step 1: Delete `PrepareRepoRoot_RepositoryPathOnGroupedService_ThrowsReservedError` and write the failing tests**

```csharp
[Fact]
public void PrepareRepoRoot_GroupPathOnGroupedService_UsesItWithoutTouchingGit()
{
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var gitClient = new FakeGitClient();

    var prepared = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, GroupedDefinition(), DevConfig(), new RepositoryDeveloperConfig { Path = groupDir },
        UnusedManagedAppHostDirectory, gitClient);

    Assert.Equal(groupDir, prepared.RepoRoot);
    Assert.False(prepared.NeedsReconciliation);
    Assert.Empty(gitClient.ClonedRepos);
    Assert.Empty(gitClient.CheckedOutRefs);
}

[Fact]
public void PrepareRepoRoot_RelativeGroupPath_AnchorsToAppHostDirectory()
{
    var appHostDirectory = TempDirectories.CreateSubdirectory().FullName;
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var relative = Path.GetRelativePath(appHostDirectory, groupDir);

    var prepared = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, GroupedDefinition(), DevConfig(), new RepositoryDeveloperConfig { Path = relative },
        appHostDirectory, new FakeGitClient());

    Assert.Equal(groupDir, prepared.RepoRoot);
}

[Fact]
public void PrepareRepoRoot_GroupPathMissing_NamesTheRepositoryAndTheKeyNotTheMember()
{
    var missing = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "nope");

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
        LocalGitCheckout.PrepareRepoRoot(
            ServiceName, GroupedDefinition(), DevConfig(), new RepositoryDeveloperConfig { Path = missing },
            UnusedManagedAppHostDirectory, new FakeGitClient()));

    Assert.Contains("Repository 'monorepo'", ex.Message);
    Assert.Contains("ServiceSources:Repositories:monorepo:path", ex.Message);
    Assert.DoesNotContain($"Service '{ServiceName}'", ex.Message);
}

[Fact]
public void PrepareRepoRoot_MemberPathBeatsGroupPath_AndSiblingStillGetsGroupPath()
{
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var ownDir = TempDirectories.CreateSubdirectory().FullName;
    var repositoryConfig = new RepositoryDeveloperConfig { Path = groupDir };

    var own = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, GroupedDefinition(), DevConfig(path: ownDir), repositoryConfig,
        UnusedManagedAppHostDirectory, new FakeGitClient());
    var sibling = LocalGitCheckout.PrepareRepoRoot(
        "basket", GroupedDefinition(serviceName: "basket"), DevConfig(), repositoryConfig,
        UnusedManagedAppHostDirectory, new FakeGitClient());

    Assert.Equal(ownDir, own.RepoRoot);
    Assert.Equal(groupDir, sibling.RepoRoot);
}

[Fact]
public void PrepareRepoRoot_LocalAliasPathBeatsGroupPath()
{
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var ownDir = TempDirectories.CreateSubdirectory().FullName;
    // 'local' is folded into 'repository' by ReconcileRepositoryAlias; go through that real fold.
    var config = new ServiceDeveloperConfig { Source = "repository", Local = new() { Path = ownDir } };
    Assert.Null(config.ReconcileRepositoryAlias(ServiceName));

    var prepared = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, GroupedDefinition(), config, new RepositoryDeveloperConfig { Path = groupDir },
        UnusedManagedAppHostDirectory, new FakeGitClient());

    Assert.Equal(ownDir, prepared.RepoRoot);
}

[Theory]
[InlineData(false)]
[InlineData(true)]
public void PrepareRepoRoot_GroupPathWithGroupRef_ThrowsNamingRepositoryAndBothKeys(bool memberHasOwnPath)
{
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var config = memberHasOwnPath ? DevConfig(path: TempDirectories.CreateSubdirectory().FullName) : DevConfig();

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
        LocalGitCheckout.PrepareRepoRoot(
            ServiceName, GroupedDefinition(), config,
            new RepositoryDeveloperConfig { Path = groupDir, Ref = "feature/x" },
            UnusedManagedAppHostDirectory, new FakeGitClient()));

    Assert.Contains("Repository 'monorepo'", ex.Message);
    Assert.Contains("ServiceSources:Repositories:monorepo:path", ex.Message);
    Assert.Contains("ServiceSources:Repositories:monorepo:ref", ex.Message);
}

[Fact]
public void PrepareRepoRoot_GroupPathWithCatalogDefaultRef_IsNotAnError()
{
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var gitClient = new FakeGitClient();

    var prepared = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, GroupedDefinition(defaultRef: "main"), DevConfig(), new RepositoryDeveloperConfig { Path = groupDir },
        UnusedManagedAppHostDirectory, gitClient);

    Assert.Equal(groupDir, prepared.RepoRoot);
    Assert.Empty(gitClient.CheckedOutRefs);
}

[Fact]
public void PrepareRepoRoot_GroupRefAlone_StillWorks()
{
    var appHostDirectory = TempDirectories.CreateSubdirectory().FullName;
    var repoRoot = LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "monorepo");
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

    var prepared = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, GroupedDefinition(), DevConfig(), new RepositoryDeveloperConfig { Ref = "feature/x" },
        appHostDirectory, new FakeGitClient());

    Assert.Equal(repoRoot, prepared.RepoRoot);
}

[Fact]
public void PrepareRepoRoot_GroupPathIgnoredForUngroupedServiceOfTheSameName()
{
    var appHostDirectory = TempDirectories.CreateSubdirectory().FullName;
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    var repoRoot = LocalGitCheckout.ManagedRepoRoot(appHostDirectory, ServiceName);
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

    var prepared = LocalGitCheckout.PrepareRepoRoot(
        ServiceName, Definition(), DevConfig(), new RepositoryDeveloperConfig { Path = groupDir },
        appHostDirectory, new FakeGitClient());

    Assert.Equal(repoRoot, prepared.RepoRoot);
}

[Fact]
public void GroupPath_DotnetMemberProjectResolvesAgainstGroupDirectoryAndStaysConfined()
{
    var groupDir = TempDirectories.CreateSubdirectory().FullName;
    Directory.CreateDirectory(Path.Combine(groupDir, "src", "Orders"));
    File.WriteAllText(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), "<Project />");
    var definition = GroupedDefinition(project: "src/Orders/Orders.csproj");

    var project = ResolveProjectPath(
        ServiceName, definition, DevConfig(), UnusedAppHostDirectory, new FakeGitClient(),
        new RepositoryDeveloperConfig { Path = groupDir });

    Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), project);
    Assert.Throws<ServiceSourcesConfigurationException>(() =>
        LocalProjectSource.ResolveProjectFile(ServiceName, groupDir, "../escape/Orders.csproj"));
}
```

Also update the doc comment on `UnusedManagedAppHostDirectory` (drop "and the reserved `path` field").

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~LocalProjectSourceTests&FullyQualifiedName~Group"`
Expected: FAIL (group-path cases hit the old "reserved" error).

- [ ] **Step 3: Implement in `LocalGitCheckout.cs`**

Add the three helpers from the Interfaces block. Change `ResolveDeveloperDirectory` to take `Raw subject` and format `$"{subject}: the '{Raw.Escaped(key)}' override points at ..."`; update its two callers (`PrepareRepoRoot` passes `PreparePlan.ServiceLabel(serviceName)`, `PathSource.cs:145` likewise). Confirm `ServiceLabel` renders `Service 'x'` so existing message tests stay green.

Replace the "reserved" block in `PrepareRepoRoot` with the ref-conflict check, and add the group branch after the member-path branch:

```csharp
if (grouped && repositoryConfig?.Path is not null && repositoryConfig.Ref is not null)
{
    throw ServiceSourcesConfigurationException.For(
        $"Repository '{new Name(definition.Repository.CheckoutName)}': '{Raw.Escaped(GroupPathKey(definition.Repository.CheckoutName))}' cannot be combined with '{Raw.Literal(DeveloperConfiguration.RepositoriesKey, default)}:{new Name(definition.Repository.CheckoutName)}:ref' — the path points directly at an existing checkout, and the ref only applies when this tool manages the clone.");
}

// after the existing `if (config.Repository.Path is not null) { ... }` block:
if (PathComesFromGroup(definition, serviceName, config, repositoryConfig))
{
    return new PreparedCheckout(
        ResolveDeveloperDirectory(
            label, GroupPathKey(definition.Repository.CheckoutName), repositoryConfig!.Path!, appHostDirectory),
        NeedsReconciliation: false);
}
```

`label` is already the repository label when grouped. Rewrite the `<param name="repositoryConfig">` and `<exception>` doc text and `RepositoryDeveloperConfig.Path`'s doc to say the group path redirects the whole group.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~LocalGitCheckoutTests|FullyQualifiedName~PathSourceTests"`
Expected: PASS (existing `ResolveDeveloperDirectory` message tests still green).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Give repositories.<name>.path its meaning in PrepareRepoRoot (#416)"
```

---

### Task 2: Every managed-vs-path predicate reads through `EffectivePath`

Without this, prefetch would still clone speculatively, `ShouldDefer` would still defer, and `RequireRepositoryToCheckOut` would still refuse a repositoryless member whose only path is the group's.

**Files:**
- Modify: `Git/LocalGitCheckout.cs` (`IsManagedCheckout` ~175, `IsColdManagedCheckout` ~256)
- Modify: `Sources/DeferredCheckout.cs` (`ShouldDefer` ~195, its `IsColdManagedCheckout` call ~236)
- Modify: `Sources/LocalCheckoutPrefetch.cs` (candidate filter ~711, `WouldBeDeferredIfAdded` ~772-780)
- Modify: `Sources/LocalProjectSource.cs` (`Resolve` ~28, ~58, ~126; `RequireRepositoryToCheckOut` ~315)
- Test: `Git/LocalGitCheckoutTests.cs` (14 existing call sites to update), `Sources/DeferredCheckoutTests.cs` (3), `Sources/LocalCheckoutPrefetchTests.cs`, `Sources/LocalProjectSourceTests.cs`

**Interfaces:**
- Consumes: Task 1's `EffectivePath`, `PathComesFromGroup`.
- Produces:
  - `bool IsManagedCheckout(ServiceDefinition definition, string serviceName, ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig)`
  - `bool IsColdManagedCheckout(string appHostDirectory, string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig)` (still tests `ManagedRepoRoot(appHostDirectory, definition.Repository.CheckoutName)` for existence)
  - `bool DeferredCheckout.ShouldDefer(IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig)`
  - `RequireRepositoryToCheckOut(string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig)`

- [ ] **Step 1: Write the failing tests**

In `Git/LocalGitCheckoutTests.cs` (follow the file's existing `IsColdManagedCheckout` tests for setup; copy `GroupedDefinition`/`DevConfig`/`Definition` helpers into the file if absent, never reach into another test class's privates):

```csharp
[Fact]
public void IsColdManagedCheckout_GroupedMemberWithGroupPath_IsNotCold()
{
    var appHost = TempDirectories.CreateSubdirectory().FullName; // nothing at the managed root
    var group = new RepositoryDeveloperConfig { Path = TempDirectories.CreateSubdirectory().FullName };

    Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHost, "orders", GroupedDefinition(), DevConfig(), group));
    Assert.True(LocalGitCheckout.IsColdManagedCheckout(appHost, "orders", GroupedDefinition(), DevConfig(), repositoryConfig: null));
}

[Fact]
public void IsManagedCheckout_FollowsEffectivePath()
{
    var group = new RepositoryDeveloperConfig { Path = "/x" };
    Assert.False(LocalGitCheckout.IsManagedCheckout(GroupedDefinition(), "orders", DevConfig(), group));
    Assert.False(LocalGitCheckout.IsManagedCheckout(GroupedDefinition(), "orders", DevConfig(path: "/own"), null));
    Assert.True(LocalGitCheckout.IsManagedCheckout(GroupedDefinition(), "orders", DevConfig(), null));
    // an ungrouped service of the same name never picks the group entry up
    Assert.True(LocalGitCheckout.IsManagedCheckout(Definition(), "orders", DevConfig(), group));
}
```

In `Sources/DeferredCheckoutTests.cs` (mirror the existing `ShouldDefer` test's run-mode builder setup): `ShouldDefer_GroupedMemberWithGroupPath_DoesNotDefer` (false with `repositoryConfig.Path` set, true with `null`), and `ShouldDefer_MixedGroup_OnlyMembersWithoutAPathDefer` (member with own path false, sibling inheriting the group path false, a member of a second group with no path true).

In `Sources/LocalCheckoutPrefetchTests.cs` (mirror the existing "no speculative clone for a path service" test): a `repositories` group path on a declared group makes `Run` start no clone (`FakeGitClient.ClonedRepos` empty, no `.servicesources/checkouts/<name>` created); a second pathless repository in the same run still clones as before.

In `Sources/LocalProjectSourceTests.cs`: `Resolve_GroupedMemberWithGroupPathAndNoRepositoryUrl_IsNotRefused` (definition with blank `Repository.Url`: resolves with the group path; without it throws the existing "gives it no repository to clone" error), using the existing `RequireRepositoryToCheckOut` tests as the template.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~LocalGitCheckoutTests|FullyQualifiedName~DeferredCheckoutTests|FullyQualifiedName~LocalCheckoutPrefetchTests|FullyQualifiedName~LocalProjectSourceTests"`
Expected: build FAIL (the new signatures do not exist yet); that is the failing state.

- [ ] **Step 3: Implement**

`LocalGitCheckout`:

```csharp
public static bool IsManagedCheckout(
    ServiceDefinition definition, string serviceName, ServiceDeveloperConfig config,
    RepositoryDeveloperConfig? repositoryConfig) =>
    EffectivePath(definition, serviceName, config, repositoryConfig) is null;

public static bool IsColdManagedCheckout(
    string appHostDirectory, string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config,
    RepositoryDeveloperConfig? repositoryConfig) =>
    IsManagedCheckout(definition, serviceName, config, repositoryConfig)
    && !Directory.Exists(ManagedRepoRoot(appHostDirectory, definition.Repository.CheckoutName));
```

Update their doc comments (they say "No `repository.path` means it does"). `DeferredCheckout.ShouldDefer` gains `repositoryConfig` and passes everything to `IsColdManagedCheckout`. `LocalCheckoutPrefetch`: the candidate filter already carries `candidate.RepositoryConfig`, so pass `candidate.Name`, `candidate.Definition`, `candidate.Config`, `candidate.RepositoryConfig`; give `WouldBeDeferredIfAdded` a `repositoryConfig` parameter and pass it to `ShouldDefer`. `LocalProjectSource.Resolve`: `RequireRepositoryToCheckOut(serviceName, definition, config, repositoryConfig)`, `var managedCheckout = LocalGitCheckout.IsManagedCheckout(definition, serviceName, config, repositoryConfig);`, `deferred.ShouldDefer(builder, serviceName, definition, config, repositoryConfig)`. Fix the remaining compile errors in existing tests mechanically (pass `null`, or the right definition/name).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build -c Release --no-restore -warnaserror` then `dotnet test -f net10.0`
Expected: build clean, all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Read managed-vs-path decisions through EffectivePath (#416)"
```

---

### Task 3: Notices and prepare wording for a group-sourced path

**Files:**
- Modify: `Sources/LocalProjectSource.cs` (`Resolve` ~68-80, the `!managedCheckout` block; the `PreparePlan.For` call ~100)
- Modify: `Prepare/PreparePlan.cs` (`For` ~104, `DirectoryOverride` ~123, `IgnoredCatalogStepNotice` ~428)
- Test: `Prepare/PreparePlanTests.cs` (notice tests near lines 348-415), `Sources/LocalProjectSourceTests.cs` (mirror the existing `LocalPathDeprecationNotice` test)

**Interfaces:**
- Consumes: Task 2's `managedCheckout`; Task 1's `PathComesFromGroup`, `GroupPathKey`.
- Produces: `PreparePlan.For(serviceName, label, catalog, developer, managedCheckout, windows, string? groupPathKey = null)`; when `groupPathKey` is non-null the ignored-step notice names that key instead of `repository.path`.

- [ ] **Step 1: Write the failing tests**

`PreparePlanTests.cs`:

```csharp
[Fact]
public void For_UnmanagedFromGroupPath_NoticeNamesTheGroupKeyNotRepositoryPath()
{
    var catalog = new PrepareMetadata { Command = ["npm", "ci"] };
    var key = "ServiceSources:Repositories:monorepo:path";

    var plan = PreparePlan.For(
        "orders", PreparePlan.RepositoryLabel("monorepo"), catalog, developer: null,
        managedCheckout: false, windows: false, groupPathKey: key);

    var notice = plan.IgnoredCatalogNotice!.Value.ToString();
    Assert.Contains(key, notice);
    Assert.DoesNotContain("repository.path", notice);
    // the remedy is still the per-service block, which is what applies under a group path
    Assert.Contains("\"repository\": { \"prepare\"", notice);
}

[Fact]
public void For_UnmanagedFromMemberPath_NoticeStillNamesRepositoryPath()
{
    var plan = PreparePlan.For(
        "orders", PreparePlan.ServiceLabel("orders"), new PrepareMetadata { Command = ["npm", "ci"] }, null,
        managedCheckout: false, windows: false);

    Assert.Contains("repository.path", plan.IgnoredCatalogNotice!.Value.ToString());
}
```

`LocalProjectSourceTests.cs` (through `LocalProjectSource.Resolve` on a test builder, as the existing deprecation-notice test does): `Resolve_GroupPathOnly_EmitsNoLocalPathDeprecationNotice` (no notice about `repository.path` being deprecated), `Resolve_MemberOwnPathInsideGroupWithGroupPath_StillEmitsDeprecationNotice`, `Resolve_GroupPathAndCatalogPrepare_IgnoredNoticeNamesGroupKey`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~PreparePlanTests|FullyQualifiedName~LocalProjectSourceTests"`
Expected: FAIL (`groupPathKey` does not exist; the deprecation notice fires for any unmanaged checkout).

- [ ] **Step 3: Implement**

In `PreparePlan.For` add the optional parameter and choose the override:

```csharp
var directoryOverride = groupPathKey is null
    ? RepositoryPathOverride
    : new DirectoryOverride(groupPathKey, "repository");
```
and pass `directoryOverride` to `ForPathCheckout` in place of `RepositoryPathOverride`. `DirectoryOverride.Key` is rendered through `Raw.Escaped`, so the colon-separated config key needs no extra handling. Read the rest of `ForPathCheckout` and `IgnoredCatalogStepNotice` for any other wording that assumes `repository.path` (the service-keyed snippet is correct as is) and adjust only what the tests show.

In `LocalProjectSource.Resolve`, gate the deprecation notice on the member's own path, and pass the key to the plan:

```csharp
if (config.Repository.Path is not null)
{
    ServiceSourcesWarnings.For(builder).AddNotice(LocalPathDeprecationNotice(serviceName, config));
}

var groupPathKey = LocalGitCheckout.PathComesFromGroup(definition, serviceName, config, repositoryConfig)
    ? LocalGitCheckout.GroupPathKey(definition.Repository.CheckoutName)
    : null;
// ... PreparePlan.For(..., managedCheckout, OperatingSystem.IsWindows(), groupPathKey);
```
Reword the nearby comment so it says the notice is about the service's own `repository.path`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -f net10.0`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Word the path notices for a group-sourced checkout (#416)"
```

---

### Task 4: Report `repositories` entries that name no declared repository

**Files:**
- Modify: `Config/DeveloperConfiguration.cs` (new properties, `ReadFrom` ~189-207)
- Modify: `Config/ServiceConfigAudit.cs` (`Report` ~90, new reason method, class XML doc)
- Test: `Config/ServiceConfigAuditTests.cs`, `Config/DeveloperConfigurationTests.cs`

**Interfaces:**
- Consumes: `CanonicalizeToCatalog<T>` (already returns the undeclared names as its second tuple element), `NearMiss.Nearest(orphan, candidates, spelling: ...)` as `OrphanedEntriesReason` uses it.
- Produces: `DeveloperConfiguration.UndeclaredRepositoryNames` and `DeveloperConfiguration.RepositoryNames` (both `required IReadOnlyList<string>`); `ServiceConfigAudit.Report` appends one reason for unknown repository names.

- [ ] **Step 1: Write the failing tests**

`ServiceConfigAuditTests.cs` (same harness: `CreateBuilder(catalogYaml, localJson)` + `TestHelpers.PublishBeforeStartEventCapturingWarningsAsync`):

```csharp
private const string MonorepoCatalog = """
    repositories:
      monorepo:
        url: https://github.com/company/monorepo
    services:
      orders:
        repositoryRef: monorepo
        project: src/Orders/Orders.csproj
        container:
          image: ghcr.io/company/orders
          port: 8080
    """;

[Fact]
public async Task RepositoryEntryMatchingNoDeclaredRepository_IsReportedWithDidYouMean()
{
    var builder = CreateBuilder(MonorepoCatalog, """
        { "services": { "orders": { "source": "container" } },
          "repositories": { "monorpeo": { "path": "/src/monorepo" } } }
        """);
    builder.AddService("orders");

    var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

    Assert.Contains("monorpeo", warning);
    Assert.Contains("(did you mean 'monorepo'?)", warning);
    Assert.Contains("repositories", warning);
    Assert.DoesNotContain("Service configuration that nothing read", warning);
}

[Fact]
public async Task RepositoryEntryInAnotherCasing_IsNotReported()
{
    var builder = CreateBuilder(MonorepoCatalog, """
        { "services": { "orders": { "source": "container" } },
          "repositories": { "MonoRepo": { "path": "/src/monorepo" } } }
        """);
    builder.AddService("orders");

    Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
}
```

The first test's `path` is nonexistent on purpose: the service is `container`-sourced, so nothing resolves it, which proves the audit does not depend on resolution. If the yaml catalog key spelling differs (`repositoryRef`, `repositories:`), copy it from `docs/sources/repository.md` or an existing grouped-catalog test. `DeveloperConfigurationTests.cs`: `ReadFrom_UndeclaredRepositoryEntry_IsExposedAndDeclaredOneIsNot` asserting `UndeclaredRepositoryNames == ["monorpeo"]` and `RepositoryNames` holds the declared names (build via the file's `CreateAppHostDirectory` helper, like `ReadFrom_ConfigurationSpellsTheServiceDifferently_KeysTheEntryByTheCatalogSpelling`).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~ServiceConfigAuditTests|FullyQualifiedName~DeveloperConfigurationTests"`
Expected: FAIL (no warning produced / properties missing).

- [ ] **Step 3: Implement**

In `ReadFrom` replace the discarded tuple half and its comment:

```csharp
var (repositories, undeclaredRepositoryNames) = CanonicalizeToCatalog(boundRepositories, declaredRepositoryNames);
```
and set `UndeclaredRepositoryNames = undeclaredRepositoryNames, RepositoryNames = declaredRepositoryNames` in the initializer, with XML docs on both. In `ServiceConfigAudit.Report`, after the service orphans:

```csharp
var repositoryOrphans = config.UndeclaredRepositoryNames.OrderBy(k => k, StringComparer.Ordinal).ToArray();
if (repositoryOrphans.Length > 0)
{
    reasons.Add(OrphanedRepositoryEntriesReason(repositoryOrphans, config.RepositoryNames));
}
```
`OrphanedRepositoryEntriesReason` mirrors `OrphanedEntriesReason`: "Repository configuration that nothing read", "No repository in this AppHost's catalog is named ...", says the group's members use their managed checkout instead, lists the declared repository names, and names the key `"{DeveloperConfigFileSource.FileRepositoriesKey}"` in `servicesources.local.json`. Every interpolated name goes through `Name`/`Raw.Join`. Update the class XML doc to cover repositories.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -f net10.0`
Expected: PASS (existing audit tests asserting zero or one warning stay green).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Report repositories entries that name no declared repository (#416)"
```

---

### Task 5: Pin the binding and the no-warning acceptance case

These are characterization tests: the behaviour should already hold once Tasks 1-2 land. Write them first and run them as a check; they are the ticket's acceptance tests.

**Files:**
- Test: `Config/DeveloperConfigurationTests.cs`, `Config/ServiceSourcesConfigCacheTests.cs`

- [ ] **Step 1: Write the tests**

`DeveloperConfigurationTests.cs`:
- `ReadFrom_RepositoryPathInJsonFile_BindsToTheDeclaredName`: `{ "repositories": { "Monorepo": { "path": "/src/mono" } } }` against a catalog declaring `monorepo` gives `Repositories["monorepo"].Path == "/src/mono"`.
- `ReadFrom_RepositoryPathFromEnvironmentVariable_BindsCaseInsensitively`: supply `ServiceSources:Repositories:MONOREPO:Path` the way neighbouring layered-config tests in this file do (in-memory collection on `builder.Configuration` if there is no env-var helper) and assert the same binding.

`ServiceSourcesConfigCacheTests.cs` (use the file's existing pattern for capturing startup notices, `TestHelpers.PublishBeforeStartEventCapturingWarningsAsync`): `GroupedMembersWithGroupPath_EmitNoStartupNotices` for a yaml catalog and again for a code catalog (`AddRepository`/`WithSharedRepository`, as in `RepositoryBuilderTests`): two services sharing the group, `repositories.monorepo.path` set to an existing temp directory, both `repository`-sourced and added through `AddService`; assert the captured warnings are empty, in particular none containing `none of them are grouped`. Add a negative control in the same file: the same two services declared ungrouped (same URL, no path) DO yield the `none of them are grouped` notice, proving the assertion can fail.

- [ ] **Step 2: Run them**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~DeveloperConfigurationTests|FullyQualifiedName~ServiceSourcesConfigCacheTests"`
Expected: PASS. If the no-notice test fails, the spec's "false-claim check" is wrong: stop and report it rather than changing the notice.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "Pin group path binding and the no-notice case (#416)"
```

---

### Task 6: Documentation and changelog

**Files:**
- Modify: `docs/sources/repository.md` (~684-700, "The per-service escape from a group" and the "no repository-level equivalent" sentence)
- Modify: `docs/guides/configuration.md` (~150-155, the "reserved" paragraph; add the env spelling)
- Modify: `docs/sources/path.md` (one line)
- Modify: `CHANGELOG.md` (`[Unreleased]` / `### Added`)

- [ ] **Step 1: Write the docs**

`repository.md`: replace "There is no repository-level equivalent" with a section on `repositories.<name>.path`: the whole group uses that directory; `project:` resolves against it and stays confined to it; nothing cloned, fetched, reconciled or deferred; relative values resolve against the AppHost directory, not the json file; it must exist; precedence (member `repository.path` > group `path` > managed checkout); it cannot be combined with the group `ref` (catalog `defaultRef` is ignored); it applies only to grouped services; catalog `prepare` is ignored with the existing notice and a service's `repository.prepare` applies; builds sharing it are not serialized; an unknown repository name is reported at startup. Include an eShop-shaped example:

```json
{
  "repositories": {
    "eshop": { "path": "../eShop" }
  }
}
```
with the catalog's `repositoryRef: eshop` and `project: src/Basket.API/Basket.API.csproj`, and the environment spelling `ServiceSources__Repositories__eshop__Path`.

`configuration.md`: replace "`path` exists on the shape but is reserved" with the real description and the env spelling. `path.md`: one sentence: for several grouped services pointing at one tree, set `repositories.<name>.path` once instead. `CHANGELOG.md` under `[Unreleased]` `### Added`, in the register of neighbouring entries: "`repositories.<name>.path` points a whole `repositoryRef` group at an existing checkout in one setting; a service's own `repository.path` still wins, and it cannot be combined with the group's `ref`. A `repositories` entry naming no declared repository is now reported at startup. (#416)".

- [ ] **Step 2: Verify**

Run: `dotnet build -c Release --no-restore -warnaserror`, then `grep -rn "no repository-level\|does not redirect\|is reserved and" README.md docs/sources docs/guides src`
Expected: build clean; grep returns nothing (leave `docs/superpowers/**` history alone). If `mkdocs` is available locally, `mkdocs build --strict` passes; otherwise name it as not run.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "Document the repository-level path and add the changelog entry (#416)"
```

---

## Self-review (spec coverage)

- Decisions 1-2: Task 1 (resolution, missing-directory message), Task 6 (docs). Decision 3: Tasks 1-2 (`EffectivePath` seam, mixed-group tests). Decision 4: Task 1 (ref-conflict theory, `defaultRef` case, group ref alone). Decision 5: Tasks 1, 2 (ungrouped same-name tests). Decision 6: Task 4 (in scope per owner). Decision 7: Tasks 3, 5. Decision 8: Task 3. Decision 9: no code (Global Constraints). Decision 10: Task 1 doc comments, Task 6.
- Spec section 4 test list is fully mapped; "absolute value used as-is" is covered by every test using an absolute temp directory, the relative case has its own test.
- Names used across tasks are defined once and spelled identically: `EffectivePath`, `PathComesFromGroup`, `GroupPathKey`, `IsManagedCheckout(definition, serviceName, config, repositoryConfig)`, `IsColdManagedCheckout(appHostDirectory, serviceName, definition, config, repositoryConfig)`, `ShouldDefer(..., repositoryConfig)`, `PreparePlan.For(..., groupPathKey)`, `UndeclaredRepositoryNames`, `RepositoryNames`.
- Task 5 is characterization (green on first run) and says so.

## Plan review (one round)

Axes: implements the spec; each step independently verifiable; ordering real.
- Fixed: `RequireRepositoryToCheckOut` originally took no `repositoryConfig` though spec decision 3 lists it; added to Task 2.
- Fixed: the `local.path` alias test goes through `ReconcileRepositoryAlias` so it exercises the real fold.
- Fixed: Task 1 keeps predicate signatures untouched so its tests are green in isolation; all compile-breaking signature changes live in Task 2.
- Fixed: Task 4's first audit test uses a nonexistent `path` on a `container`-sourced service, proving the audit does not depend on resolution.
