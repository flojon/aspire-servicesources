# Per-Service Local-Source Checkouts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the milestone 1a `"local"`-source cache-collision and stale-ref bugs by giving each service its own full git clone under the AppHost directory, keyed by service name, with ref reconciliation on every resolve that never discards a developer's uncommitted edits.

**Architecture:** `LocalProjectSource.ResolveProjectPath` stops using a developer-configurable, repo-name-keyed shared cache directory and instead clones each service into `<AppHostDirectory>/.servicesources/checkouts/<serviceName>/`. Because that path can never be shared between two services, the old `GetOriginUrl`/`RepositoryUrlsMatch` collision guard is deleted outright. Because each checkout is now owned by exactly one service, `Checkout` can safely run on every resolve (not just first clone) to reconcile a changed `ref` — guarded by a dirty-working-tree check so a developer's uncommitted work is never silently checked out over. `IGitClient` gains `Fetch`, `HasUncommittedChanges`, and `IsRefCheckedOut`; `GetOriginUrl` is removed. The `cacheDirectory` developer-config key is removed entirely, since checkout location is no longer configurable.

**Tech Stack:** C# / .NET 10, LibGit2Sharp 0.32.0, xunit, Aspire.Hosting 13.4.6.

**Spec:** `docs/superpowers/specs/2026-08-16-servicesources-shared-repo-cache-design.md`

## Global Constraints

- Every managed-clone failure path throws `ServiceSourcesConfigurationException` naming the service (and repository/ref where applicable) — never a silent fallback or swallowed exception.
- No unconditional network call on every resolve: `Fetch` only runs as a one-shot retry when a configured `ref` can't be resolved from the local clone.
- Checkout location is fixed at `<AppHostDirectory>/.servicesources/checkouts/<serviceName>/` — no longer developer-configurable.
- `<AppHostDirectory>/.servicesources/.gitignore` is auto-written, idempotently (write-if-missing), with exact contents `*\n!.gitignore\n`.
- A developer's uncommitted edits inside a managed checkout must never be silently discarded by ref reconciliation.
- `config.Path` (fully developer-managed checkout, outside this flow) and its existing `path`+`ref` mutual-exclusion check are unchanged by this plan.

---

## Task 1: Extend `IGitClient` with `Fetch`, `HasUncommittedChanges`, `IsRefCheckedOut`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs` (fake stub only, to keep the solution compiling)

**Interfaces:**
- Produces: `IGitClient.Fetch(string repositoryPath)` — fetches all refs from the `origin` remote into the local clone; no-op if there is no `origin` remote.
- Produces: `IGitClient.HasUncommittedChanges(string repositoryPath) : bool` — true if the working tree has any uncommitted modification.
- Produces: `IGitClient.IsRefCheckedOut(string repositoryPath, string reference) : bool` — true if `reference` resolves (locally, no network) to the same commit currently at `HEAD`.
- `GetOriginUrl` is untouched in this task — it's removed in Task 2, once the logic that consumes it is deleted.

- [ ] **Step 1: Write the failing tests for the three new `LibGit2SharpGitClient` methods**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`, inside the `LibGit2SharpGitClientTests` class (after the existing `GetOriginUrl_ReturnsOriginRemoteUrlAfterClone` test, before the closing `}`):

```csharp
    [Fact]
    public void Fetch_PullsRefCreatedOnOriginAfterInitialClone()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        using (var originRepo = new Repository(origin))
        {
            var signature = new Signature("test", "test@test.com", DateTimeOffset.Now);
            var lateBranch = originRepo.CreateBranch("feature/late");
            Commands.Checkout(originRepo, lateBranch);
            File.WriteAllText(Path.Combine(origin, "file.txt"), "late content");
            Commands.Stage(originRepo, "file.txt");
            originRepo.Commit("late commit", signature, signature);
        }

        client.Fetch(destination);
        client.Checkout(destination, "feature/late");

        Assert.Equal("late content", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void HasUncommittedChanges_CleanCheckout_ReturnsFalse()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        Assert.False(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void HasUncommittedChanges_ModifiedFile_ReturnsTrue()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        File.WriteAllText(Path.Combine(destination, "file.txt"), "locally edited");

        Assert.True(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void IsRefCheckedOut_MatchingRef_ReturnsTrue()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);
        client.Checkout(destination, "v1.0.0");

        Assert.True(client.IsRefCheckedOut(destination, "v1.0.0"));
    }

    [Fact]
    public void IsRefCheckedOut_DifferentRef_ReturnsFalse()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);
        client.Checkout(destination, "v1.0.0");

        Assert.False(client.IsRefCheckedOut(destination, "feature/x"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LibGit2SharpGitClientTests"`
Expected: build error — `LibGit2SharpGitClient` does not contain a definition for `Fetch`/`HasUncommittedChanges`/`IsRefCheckedOut`.

- [ ] **Step 3: Add the three methods to `IGitClient`**

Replace the full contents of `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Git;

internal interface IGitClient
{
    void Clone(string repositoryUrl, string destinationPath);

    void Checkout(string repositoryPath, string reference);

    /// <summary>
    /// Fetches all refs from the "origin" remote into the local clone at
    /// <paramref name="repositoryPath"/>. A no-op if no "origin" remote is configured.
    /// </summary>
    void Fetch(string repositoryPath);

    /// <summary>
    /// Returns <see langword="true"/> if the working tree at <paramref name="repositoryPath"/>
    /// has any uncommitted modification (staged or unstaged).
    /// </summary>
    bool HasUncommittedChanges(string repositoryPath);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="reference"/> resolves, using only
    /// local data (no network), to the same commit currently checked out at HEAD.
    /// </summary>
    bool IsRefCheckedOut(string repositoryPath, string reference);

    /// <summary>
    /// Returns the URL of the "origin" remote for the repository already checked out at
    /// <paramref name="repositoryPath"/>, or <see langword="null"/> if it cannot be determined
    /// (e.g. no "origin" remote is configured). Never performs any network operation.
    /// </summary>
    string? GetOriginUrl(string repositoryPath);
}
```

- [ ] **Step 4: Implement the three methods in `LibGit2SharpGitClient`**

Replace the full contents of `src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs`:

```csharp
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Git;

internal sealed class LibGit2SharpGitClient : IGitClient
{
    public void Clone(string repositoryUrl, string destinationPath)
    {
        Repository.Clone(repositoryUrl, destinationPath);
    }

    public void Checkout(string repositoryPath, string reference)
    {
        using var repo = new Repository(repositoryPath);

        var branch = repo.Branches[reference] ?? repo.Branches[$"origin/{reference}"];
        if (branch is not null)
        {
            if (!branch.IsRemote)
            {
                Commands.Checkout(repo, branch);
                return;
            }

            var localBranch = repo.CreateBranch(reference, branch.Tip);
            repo.Branches.Update(localBranch, b => b.TrackedBranch = branch.CanonicalName);
            Commands.Checkout(repo, localBranch);
            return;
        }

        var tag = repo.Tags[reference];
        if (tag is not null)
        {
            Commands.Checkout(repo, tag.Target.Sha);
            return;
        }

        var commit = repo.Lookup<Commit>(reference);
        if (commit is not null)
        {
            Commands.Checkout(repo, commit.Sha);
            return;
        }

        throw new ServiceSourcesConfigurationException(
            $"Ref '{reference}' was not found in repository at '{repositoryPath}'.");
    }

    public void Fetch(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);

        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
        {
            return;
        }

        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
        Commands.Fetch(repo, remote.Name, refSpecs, null, null);
    }

    public bool HasUncommittedChanges(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.RetrieveStatus().IsDirty;
    }

    public bool IsRefCheckedOut(string repositoryPath, string reference)
    {
        using var repo = new Repository(repositoryPath);

        var headSha = repo.Head.Tip?.Sha;
        if (headSha is null)
        {
            return false;
        }

        var branch = repo.Branches[reference] ?? repo.Branches[$"origin/{reference}"];
        if (branch is not null)
        {
            return branch.Tip.Sha == headSha;
        }

        var tag = repo.Tags[reference];
        if (tag is not null)
        {
            return tag.Target.Sha == headSha;
        }

        var commit = repo.Lookup<Commit>(reference);
        return commit is not null && commit.Sha == headSha;
    }

    public string? GetOriginUrl(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Network.Remotes["origin"]?.Url;
    }
}
```

- [ ] **Step 5: Add stub implementations to `FakeGitClient` so the solution keeps compiling**

In `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`, inside the `FakeGitClient` class, add these members (after the existing `GetOriginUrl` method, before the closing `}` of `FakeGitClient`):

```csharp
        public List<string> FetchedRepos { get; } = [];

        public bool UncommittedChanges { get; set; }

        public string? CurrentlyCheckedOutRef { get; set; }

        public void Fetch(string repositoryPath) => FetchedRepos.Add(repositoryPath);

        public bool HasUncommittedChanges(string repositoryPath) => UncommittedChanges;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => CurrentlyCheckedOutRef == reference;
```

These fields aren't exercised by assertions yet (that starts in Task 3); this step only exists so `FakeGitClient` keeps satisfying `IGitClient` after Step 3.

- [ ] **Step 6: Run the new tests and confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LibGit2SharpGitClientTests"`
Expected: all tests pass (5 new + 5 existing = 10 passed).

- [ ] **Step 7: Run the full test suite to confirm nothing else broke**

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs
git commit -m "Add Fetch, HasUncommittedChanges, IsRefCheckedOut to IGitClient"
```

---

## Task 2: Per-service checkout path, drop the collision check

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`

**Interfaces:**
- Consumes: `IGitClient.Clone`, `IGitClient.Checkout` (unchanged, from existing code).
- Produces: `LocalProjectSource.ResolveProjectPath(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config, string appHostDirectory, IGitClient gitClient) : string` — note the signature drops the `cacheDirectory` parameter present before this task.
- Removes: `IGitClient.GetOriginUrl`, `LocalProjectSource.GetRepositoryName`, `LocalProjectSource.RepositoryUrlsMatch`, `LocalProjectSource.NormalizeRepositoryUrl`.

- [ ] **Step 1: Write the failing tests for the new checkout path and the collision-can't-happen guarantee**

In `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`, replace the two collision-check tests (`ResolveProjectPath_CacheDirectoryOriginMismatch_ThrowsNamingServiceAndBothUrls` and `ResolveProjectPath_CacheDirectoryOriginUnknown_DoesNotThrow`, currently at the bottom of the file) with:

```csharp
    [Fact]
    public void ResolveProjectPath_TwoServicesSameRepo_GetIndependentNonCollidingPaths()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var ordersPath = LocalProjectSource.ResolveProjectPath(
            "orders", Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), appHostDirectory, gitClient);
        var billingPath = LocalProjectSource.ResolveProjectPath(
            "billing", Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.NotEqual(ordersPath, billingPath);
        Assert.Equal(2, gitClient.ClonedRepos.Count);
    }

    [Fact]
    public void ResolveProjectPath_ManagedClone_WritesGitignoreUnderServiceSourcesDirectory()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        var gitignorePath = Path.Combine(appHostDirectory, ".servicesources", ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Equal("*\n!.gitignore\n", File.ReadAllText(gitignorePath));
    }

    [Fact]
    public void ResolveProjectPath_ManagedClone_DoesNotOverwriteExistingGitignore()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Directory.CreateDirectory(dir);
        var gitignorePath = Path.Combine(dir, ".gitignore");
        File.WriteAllText(gitignorePath, "custom content");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal("custom content", File.ReadAllText(gitignorePath));
    }
```

Then update the remaining tests in the same file to match the new checkout layout and the new `ResolveProjectPath` signature (four call sites lose the `"/unused/cache"` / `cacheDirectory` argument; two gain a new path assertion):

- `ResolveProjectPath_PathIsSet_UsesItDirectlyWithoutTouchingGit`: change the call to
  `LocalProjectSource.ResolveProjectPath(ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory, gitClient)`.
- `ResolveProjectPath_PathAndRefBothSet_ThrowsNamingServiceAndDoesNotTouchGit`: change the call to
  `LocalProjectSource.ResolveProjectPath(ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir, @ref: "feature/x"), UnusedAppHostDirectory, gitClient)`.
- `ResolveProjectPath_RelativePathOverride_AnchorsToAppHostDirectoryNotProcessCwd`: change the call to
  `LocalProjectSource.ResolveProjectPath(ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: relativePath), appHostDirectory, gitClient)`.
- `ResolveProjectPath_CacheMiss_ClonesIntoCacheDirectoryUnderRepoName`: rename to `ResolveProjectPath_CacheMiss_ClonesIntoAppHostDirectoryUnderServiceName` and replace its body:

```csharp
    [Fact]
    public void ResolveProjectPath_CacheMiss_ClonesIntoAppHostDirectoryUnderServiceName()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        var (repositoryUrl, destinationPath) = Assert.Single(gitClient.ClonedRepos);
        Assert.Equal("https://github.com/company/orders", repositoryUrl);
        Assert.Equal(Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName), destinationPath);
        Assert.Equal(Path.Combine(destinationPath, "Orders.csproj"), projectPath);
    }
```

- `ResolveProjectPath_CacheMiss_UsesDeveloperRefOverCatalogDefaultRef`, `ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset`, `ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout`: replace all three bodies:

```csharp
    [Fact]
    public void ResolveProjectPath_CacheMiss_UsesDeveloperRefOverCatalogDefaultRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: null), appHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("main", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: null), DevConfig(@ref: null), appHostDirectory, gitClient);

        Assert.Empty(gitClient.CheckedOutRefs);
    }
```

- `ResolveProjectPath_CacheHit_DoesNotCloneOrCheckout`: replace the body:

```csharp
    [Fact]
    public void ResolveProjectPath_CacheHit_DoesNotCloneOrCheckout()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: null), DevConfig(), appHostDirectory, gitClient);

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }
```

  (Note: `defaultRef` is now `null` here, not `"main"` — with the stale-ref fix, a configured ref *would* now trigger a reconciling `Checkout` even on cache-hit; that behavior gets its own test in Task 3. This test is specifically the "no ref configured at all" case.)
- `ResolveProjectPath_ProjectFileMissing_ThrowsNamingServiceProjectAndRoot`, `ResolveProjectPath_CloneFails_WrapsAsConfigurationExceptionNamingServiceAndRepository`, `ResolveProjectPath_CheckoutFails_WrapsAsConfigurationExceptionNamingServiceAndRef`: replace all three bodies:

```csharp
    [Fact]
    public void ResolveProjectPath_ProjectFileMissing_ThrowsNamingServiceProjectAndRoot()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(project: "src/Missing.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory, new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("src/Missing.csproj", ex.Message);
        Assert.Contains(repoDir, ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_CloneFails_WrapsAsConfigurationExceptionNamingServiceAndRepository()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CloneException = new InvalidOperationException("network unreachable") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/company/orders", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveProjectPath_CheckoutFails_WrapsAsConfigurationExceptionNamingServiceAndRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CheckoutException = new ServiceSourcesConfigurationException(
                "Ref 'missing-ref' was not found in repository at '/tmp/x'."),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "missing-ref"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("missing-ref", ex.Message);
        Assert.IsType<ServiceSourcesConfigurationException>(ex.InnerException);
    }
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: build errors — `ResolveProjectPath` overload resolution failures (wrong argument count), and `IGitClient` still requiring `GetOriginUrl` on `FakeGitClient` is fine since it's not removed yet in this step — the failures are purely about the changed `ResolveProjectPath` signature, which doesn't exist yet.

- [ ] **Step 3: Remove `GetOriginUrl` from `IGitClient` and `LibGit2SharpGitClient`**

In `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`, delete the `GetOriginUrl` method and its doc comment (the last member in the interface).

In `src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs`, delete the `GetOriginUrl` method (the last member in the class).

In `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`, delete the `GetOriginUrl_ReturnsOriginRemoteUrlAfterClone` test.

In `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`'s `FakeGitClient`, delete the `OriginUrlsByPath` dictionary property and the `GetOriginUrl` method, and delete the line `OriginUrlsByPath[destinationPath] = repositoryUrl;` from inside `Clone`.

- [ ] **Step 4: Rewrite `LocalProjectSource.ResolveProjectPath` and `Resolve` to use the new checkout path**

Replace the full contents of `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var projectPath = ResolveProjectPath(serviceName, metadata, config, builder.AppHostDirectory, gitClient);

        var projectBuilder = builder.AddProject(serviceName, projectPath);
        return ServiceResource.CreateFacade(builder, serviceName, projectBuilder);
    }

    internal static string ResolveProjectPath(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            if (config.Ref is not null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': 'ref' cannot be combined with 'path' — 'path' points directly at " +
                    "an existing checkout, and 'ref' only applies when this tool manages the clone.");
            }

            // Anchor a relative `path` override to the AppHost directory (matching Aspire's own
            // AddProject behavior), not to the process's current working directory.
            // Path.GetFullPath is a no-op when config.Path is already absolute.
            repoRoot = Path.GetFullPath(config.Path, appHostDirectory);
        }
        else
        {
            EnsureGitignore(appHostDirectory);
            repoRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);
            var reference = config.Ref ?? metadata.DefaultRef;

            if (!Directory.Exists(repoRoot))
            {
                try
                {
                    gitClient.Clone(metadata.Repository, repoRoot);
                }
                catch (Exception ex)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': failed to clone repository '{metadata.Repository}' into '{repoRoot}'.", ex);
                }

                if (reference is not null)
                {
                    try
                    {
                        gitClient.Checkout(repoRoot, reference);
                    }
                    catch (Exception ex)
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
                    }
                }
            }
            else if (reference is not null)
            {
                try
                {
                    gitClient.Checkout(repoRoot, reference);
                }
                catch (Exception ex)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
                }
            }
        }

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }

    private static void EnsureGitignore(string appHostDirectory)
    {
        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Directory.CreateDirectory(dir);

        var gitignorePath = Path.Combine(dir, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath, "*\n!.gitignore\n");
        }
    }
}
```

Note this intermediate version still unconditionally re-runs `Checkout` on a cache-hit whenever a `reference` is configured — with no dirty-tree guard yet. That guard is added in Task 3; don't skip ahead.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~LibGit2SharpGitClientTests"`
Expected: all tests pass.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: failures are limited to `AddServiceIntegrationTests` (still asserts the old cache path — fixed in Task 6) and any `servicesources.local.json` fixtures still setting `cacheDirectory` (fixed in Task 5). No other test should fail. If something else fails, stop and investigate before continuing.

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs
git commit -m "Key local-source checkouts by service name under the AppHost directory"
```

---

## Task 3: Dirty-checkout guard on ref reconciliation

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`

**Interfaces:**
- Consumes: `IGitClient.HasUncommittedChanges`, `IGitClient.IsRefCheckedOut` (added Task 1).
- `ResolveProjectPath`'s signature is unchanged from Task 2.

- [ ] **Step 1: Write the failing tests**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`, near `ResolveProjectPath_CacheHit_DoesNotCloneOrCheckout`:

```csharp
    [Fact]
    public void ResolveProjectPath_CacheHit_CleanTree_ReconcilesChangedRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        var (repositoryPath, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal(repoDir, repositoryPath);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DirtyTreeButAlreadyOnConfiguredRef_DoesNothing()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient
        {
            UncommittedChanges = true,
            CurrentlyCheckedOutRef = "feature/x",
        };

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DirtyTreeAndDifferentRef_ThrowsWithoutTouchingWorkingTree()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient
        {
            UncommittedChanges = true,
            CurrentlyCheckedOutRef = "main",
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("feature/x", ex.Message);
        Assert.Contains("uncommitted", ex.Message);
        Assert.Empty(gitClient.CheckedOutRefs);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: `ResolveProjectPath_CacheHit_DirtyTreeButAlreadyOnConfiguredRef_DoesNothing` and `ResolveProjectPath_CacheHit_DirtyTreeAndDifferentRef_ThrowsWithoutTouchingWorkingTree` FAIL (current code always calls `Checkout` regardless of dirty state); `ResolveProjectPath_CacheHit_CleanTree_ReconcilesChangedRef` should already PASS (no behavior change needed for the clean case, confirming the Task 2 baseline).

- [ ] **Step 3: Add the dirty-tree guard to `ResolveProjectPath`**

In `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`, replace the `else if (reference is not null)` branch (the cache-hit branch) with:

```csharp
            else if (reference is not null)
            {
                if (gitClient.HasUncommittedChanges(repoRoot))
                {
                    if (!gitClient.IsRefCheckedOut(repoRoot, reference))
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{serviceName}': checkout at '{repoRoot}' has uncommitted changes and is not " +
                            $"on the configured ref '{reference}'. Commit or stash your changes, then re-run.");
                    }
                }
                else
                {
                    try
                    {
                        gitClient.Checkout(repoRoot, reference);
                    }
                    catch (Exception ex)
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
                    }
                }
            }
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: all tests pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: same pre-existing failures as after Task 2 (`AddServiceIntegrationTests`, `cacheDirectory`-related tests), nothing new.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs
git commit -m "Guard ref reconciliation against discarding uncommitted checkout changes"
```

---

## Task 4: Fetch-and-retry when a configured ref can't be resolved locally

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`

**Interfaces:**
- Consumes: `IGitClient.Fetch` (added Task 1).
- Produces: `LocalProjectSource.CheckoutWithFetchRetry` — a new private static helper; not consumed outside this file.

- [ ] **Step 1: Write the failing tests**

Add to `FakeGitClient` in `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs` (this field controls a one-shot retry scenario: the first `Checkout` call for a run fails, every subsequent one succeeds):

```csharp
        private int _checkoutAttempts;

        public bool FailFirstCheckoutOnly { get; set; }

        public Exception? FetchException { get; set; }
```

Then replace the existing `Checkout` method body in `FakeGitClient` with:

```csharp
        public void Checkout(string repositoryPath, string reference)
        {
            _checkoutAttempts++;

            if (CheckoutException is not null)
            {
                throw CheckoutException;
            }

            if (FailFirstCheckoutOnly && _checkoutAttempts == 1)
            {
                throw new InvalidOperationException("ref not resolvable locally");
            }

            CheckedOutRefs.Add((repositoryPath, reference));
            CurrentlyCheckedOutRef = reference;
        }
```

And replace the existing `Fetch` method body:

```csharp
        public void Fetch(string repositoryPath)
        {
            if (FetchException is not null)
            {
                throw FetchException;
            }

            FetchedRepos.Add(repositoryPath);
        }
```

Then add the test cases (near `ResolveProjectPath_CheckoutFails_WrapsAsConfigurationExceptionNamingServiceAndRef`):

```csharp
    [Fact]
    public void ResolveProjectPath_CacheMiss_CheckoutFailsOnce_FetchesThenRetriesSuccessfully()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { FailFirstCheckoutOnly = true };

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "feature/late"), DevConfig(), appHostDirectory, gitClient);

        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Assert.Equal(new[] { repoDir }, gitClient.FetchedRepos);
        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/late", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_RefMissingEvenAfterFetch_WrapsAsConfigurationException()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CheckoutException = new ServiceSourcesConfigurationException(
                "Ref 'does-not-exist' was not found in repository at '/tmp/x'."),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "does-not-exist"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Single(gitClient.FetchedRepos);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FetchItselfFails_WrapsAsConfigurationExceptionNamingService()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            FailFirstCheckoutOnly = true,
            FetchException = new InvalidOperationException("network unreachable"),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "feature/late"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: the three new tests FAIL — `ResolveProjectPath_CacheMiss_CheckoutFailsOnce_FetchesThenRetriesSuccessfully` throws instead of succeeding (no retry exists yet); the other two currently pass on the first Checkout failure alone, but assert `Assert.Single(gitClient.FetchedRepos)`, which fails since nothing calls `Fetch` yet.

- [ ] **Step 3: Add `CheckoutWithFetchRetry` and use it from both checkout call sites**

In `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`, replace both inline `try { gitClient.Checkout(...) } catch (Exception ex) { throw new ServiceSourcesConfigurationException(...) }` blocks (the one inside `if (!Directory.Exists(repoRoot))` and the one inside the `else` of the dirty-tree guard) with a single call:

```csharp
CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
```

Then add the helper method (after `ResolveProjectPath`, before `EnsureGitignore`):

```csharp
    private static void CheckoutWithFetchRetry(
        string serviceName, ServiceMetadata metadata, string repoRoot, string reference, IGitClient gitClient)
    {
        try
        {
            gitClient.Checkout(repoRoot, reference);
            return;
        }
        catch
        {
            // Fall through to fetch-and-retry below.
        }

        try
        {
            gitClient.Fetch(repoRoot);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to fetch repository '{metadata.Repository}' at '{repoRoot}' " +
                $"while resolving ref '{reference}'.", ex);
        }

        try
        {
            gitClient.Checkout(repoRoot, reference);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
        }
    }
```

The full method after this edit (both call sites and the branch structure) should read:

```csharp
    internal static string ResolveProjectPath(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            if (config.Ref is not null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': 'ref' cannot be combined with 'path' — 'path' points directly at " +
                    "an existing checkout, and 'ref' only applies when this tool manages the clone.");
            }

            repoRoot = Path.GetFullPath(config.Path, appHostDirectory);
        }
        else
        {
            EnsureGitignore(appHostDirectory);
            repoRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);
            var reference = config.Ref ?? metadata.DefaultRef;

            if (!Directory.Exists(repoRoot))
            {
                try
                {
                    gitClient.Clone(metadata.Repository, repoRoot);
                }
                catch (Exception ex)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': failed to clone repository '{metadata.Repository}' into '{repoRoot}'.", ex);
                }

                if (reference is not null)
                {
                    CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
                }
            }
            else if (reference is not null)
            {
                if (gitClient.HasUncommittedChanges(repoRoot))
                {
                    if (!gitClient.IsRefCheckedOut(repoRoot, reference))
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{serviceName}': checkout at '{repoRoot}' has uncommitted changes and is not " +
                            $"on the configured ref '{reference}'. Commit or stash your changes, then re-run.");
                    }
                }
                else
                {
                    CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
                }
            }
        }

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: all tests pass, including the two existing tests `ResolveProjectPath_CloneFails_WrapsAsConfigurationExceptionNamingServiceAndRepository` and `ResolveProjectPath_CheckoutFails_WrapsAsConfigurationExceptionNamingServiceAndRef` (the latter now exercises the fetch-retry path internally, but its assertions — service name, ref, exception type — are unaffected).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: same pre-existing failures as after Task 3, nothing new.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs
git commit -m "Fetch and retry checkout when a configured ref isn't resolvable locally"
```

---

## Task 5: Remove the `cacheDirectory` developer-config key

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFile.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`

**Interfaces:**
- Removes: `ServiceSourcesConfigCache.GetCacheDirectory`, `DeveloperConfigFile.CacheDirectory`. Nothing in this codebase calls `GetCacheDirectory` anymore after Task 2.

- [ ] **Step 1: Confirm nothing still calls the method being removed**

Run: `grep -rn "GetCacheDirectory" src/ test/`
Expected: only the definition in `ServiceSourcesConfigCache.cs` and its three tests in `ServiceSourcesConfigCacheTests.cs` — no production call sites (Task 2 already removed the one in `LocalProjectSource.cs`).

- [ ] **Step 2: Delete the three now-obsolete tests**

In `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs`, delete `GetCacheDirectory_ExpandsTildeToHomeDirectory`, `GetCacheDirectory_DefaultsWhenNotConfigured`, and `GetCacheDirectory_RelativePath_AnchorsToAppHostDirectoryNotProcessCwd` in their entirety (the last three `[Fact]` methods in the file).

- [ ] **Step 3: Remove `GetCacheDirectory` and `ExpandHome` from `ServiceSourcesConfigCache`**

Replace the full contents of `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceSourcesConfigCache
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LoadedConfig> Cache = new();

    public static (ServiceMetadata Metadata, ServiceDeveloperConfig DeveloperConfig) ResolveService(
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var loaded = Cache.GetValue(builder, static b => LoadedConfig.Load(b.AppHostDirectory));

        if (!loaded.Catalog.Services.TryGetValue(serviceName, out var metadata))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in 'servicesources.yaml'.");
        }

        if (!loaded.DeveloperConfig.Services.TryGetValue(serviceName, out var developerConfig))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in 'servicesources.local.json'.");
        }

        return (metadata, developerConfig);
    }

    private sealed class LoadedConfig
    {
        public required ServiceCatalog Catalog { get; init; }

        public required DeveloperConfigFile DeveloperConfig { get; init; }

        public static LoadedConfig Load(string appHostDirectory)
        {
            var catalog = ServiceCatalogLoader.Load(Path.Combine(appHostDirectory, "servicesources.yaml"));
            var developerConfig = DeveloperConfigLoader.Load(Path.Combine(appHostDirectory, "servicesources.local.json"));
            return new LoadedConfig { Catalog = catalog, DeveloperConfig = developerConfig };
        }
    }
}
```

- [ ] **Step 4: Remove `CacheDirectory` from `DeveloperConfigFile`**

Replace the full contents of `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFile.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class DeveloperConfigFile
{
    public Dictionary<string, ServiceDeveloperConfig> Services { get; set; } = new();
}
```

- [ ] **Step 5: Update `DeveloperConfigLoaderTests` to stop asserting on the removed field**

In `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`, in `Load_ParsesServicesFromJson`, remove the `"cacheDirectory": "~/.servicesources/repos",` line from the JSON literal and remove the `Assert.Equal("~/.servicesources/repos", config.CacheDirectory);` line. The test becomes:

```csharp
    [Fact]
    public void Load_ParsesServicesFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "services": {
                "orders": { "source": "local" },
                "payments": { "source": "local", "path": "/home/dev/code/payments", "ref": "feature/new-checkout" }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal(2, config.Services.Count);
            Assert.Equal("local", config.Services["orders"].Source);
            Assert.Null(config.Services["orders"].Path);
            Assert.Equal("/home/dev/code/payments", config.Services["payments"].Path);
            Assert.Equal("feature/new-checkout", config.Services["payments"].Ref);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 6: Run the affected tests and confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~ServiceSourcesConfigCacheTests|FullyQualifiedName~DeveloperConfigLoaderTests"`
Expected: all tests pass.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: only `AddServiceIntegrationTests` still fails (its `servicesources.local.json` fixture still writes `cacheDirectory`, which `System.Text.Json` will now silently ignore rather than error on — but its path assertion is still against the old layout). Fixed next in Task 6.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFile.cs test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs
git commit -m "Remove the cacheDirectory developer-config key"
```

---

## Task 6: Update the end-to-end integration tests

**Files:**
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs`

**Interfaces:**
- Consumes: `LocalProjectSource` (via `builder.AddService`, unchanged public entry point), the real `sample-service.git` fixture at `test/Aspire.Hosting.ServiceSources.Tests/Fixtures/sample-service.git` (branches `main` → `SampleProj` port 5001, `feature/v2` → port 5002; tag `v1.0.0`).

- [ ] **Step 1: Write the failing tests**

Replace the full contents of `test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceIntegrationTests
{
    private static string FixtureRepoPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-service.git");

    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    private static int? PortOf(IDistributedApplicationBuilder builder, string serviceName)
    {
        var realResource = Assert.Single(builder.Resources, r => r.Name == serviceName);
        var endpointAnnotation = Assert.Single(
            ((IResource)realResource).Annotations.OfType<EndpointAnnotation>());
        return endpointAnnotation.Port;
    }

    [Fact]
    public void AddService_ManagedClone_ClonesRealRepoAndChecksOutFeatureRef()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
                defaultRef: main
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            {
              "services": { "orders": { "source": "local", "ref": "feature/v2" } }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        var clonedProjectPath = Path.Combine(appHostDir, ".servicesources", "checkouts", "orders", "SampleProj", "SampleProj.csproj");
        Assert.True(File.Exists(clonedProjectPath));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);
        Assert.Equal(5002, PortOf(builder, "orders"));
    }

    [Fact]
    public void AddService_TwoServicesSameRepoDifferentRefs_BothResolveIndependently()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders-main:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
              orders-v2:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            {
              "services": {
                "orders-main": { "source": "local", "ref": "main" },
                "orders-v2": { "source": "local", "ref": "feature/v2" }
              }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        builder.AddService("orders-main");
        builder.AddService("orders-v2");

        Assert.Equal(5001, PortOf(builder, "orders-main"));
        Assert.Equal(5002, PortOf(builder, "orders-v2"));
    }

    [Fact]
    public void AddService_TwoServicesSameRepoSameRef_BothResolveIndependently()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders-a:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
              orders-b:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            {
              "services": {
                "orders-a": { "source": "local", "ref": "main" },
                "orders-b": { "source": "local", "ref": "main" }
              }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        builder.AddService("orders-a");
        builder.AddService("orders-b");

        Assert.Equal(5001, PortOf(builder, "orders-a"));
        Assert.Equal(5001, PortOf(builder, "orders-b"));
    }
}
```

- [ ] **Step 2: Run the tests and confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~AddServiceIntegrationTests"`
Expected: all three tests pass. `AddService_TwoServicesSameRepoSameRef_BothResolveIndependently` is the concrete regression test for the worktree-branch-naming conflict identified during design review — with independent full clones per service, there's no shared `refs/heads` namespace left to conflict on.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass, no failures anywhere in the solution.

- [ ] **Step 4: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs
git commit -m "Update integration tests for per-service checkout layout"
```
