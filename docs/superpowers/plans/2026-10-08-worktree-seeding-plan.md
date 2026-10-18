# Seeding a Linked Worktree from the Main Worktree — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An AppHost run from a linked git worktree of its own repository starts its first run with the main worktree's developer config, source picks, and cheaply-obtained checkouts, then stays independent.

**Architecture:** A new `Worktrees/` folder holds three units: `WorktreeHome` (finds the main worktree's copy of the AppHost directory, a filesystem pre-check first and git only for a `.git` *file*), `WorktreeSeed` (copies `servicesources.local.json` and `.servicesources/selection.json` once, writes a marker, reports through `ServiceSourcesWarnings`), and `PathOverrideCheck` (warns about copied `path` overrides that resolve differently from the worktree). Checkouts are seeded at clone time: `IGitClient` gains a default `CloneWithReference` that `GitCliClient` implements with `--reference-if-able … --dissociate`, and `LocalGitCheckout.CloneIntoPlace` borrows from the home checkout when its origin matches, retrying as a plain clone only on `GitReferenceFailedException`.

**Tech Stack:** C# (net8.0/net9.0/net10.0), .NET Aspire hosting, git CLI (2.31+ for worktree detection), xUnit, `System.Text.Json`, `Microsoft.Extensions.Configuration.Json`.

**Spec:** `docs/superpowers/specs/2026-10-08-servicesources-worktree-seeding-design.md`

## Global Constraints

- No new configuration surface: no option, no environment variable, no file key.
- Seeding never fails a run. Config seeding catches `IOException`/`UnauthorizedAccessException`, reports a notice, writes no marker. Clone seeding retries a plain clone only on `GitReferenceFailedException`.
- Never overwrite an existing worktree file; copy with create-new semantics only.
- Only regular files are copied (not reparse points / symlinks).
- Prepare markers (`.servicesources/prepare/*`) are never seeded.
- Marker path: `.servicesources/worktree-seed.json`. Written even when nothing was copied; not written when home is not resolved or a copy failed.
- Build-time notices go through `ServiceSourcesWarnings.For(builder).AddNotice(Raw)`. The clone fallback line goes to the clone's `IGitProgressSink` (and nowhere when it is `null`).
- Every message is composed through the `Raw`/`Name` seam (`Raw.Compose($"…")`, `new Name(…)`, `Raw.Escaped(…)`, `Raw.Cause(ex)`); a raw `string` hole does not compile.
- New exception types get a `For(ServiceTextHandler, …)` factory and a `BannedSymbols.txt` entry for their constructor.
- `IGitClient.CloneWithReference` is a default interface method delegating to `Clone`, so the twelve existing private test fakes (ten core, one Java, one JavaScript) compile unchanged.
- The managed checkout path (`LocalGitCheckout.ManagedRepoRoot`) stays a pure function of the worktree's AppHost directory.
- Once the marker exists, a run spawns no git process to find home. The prefetch resolves home lazily, only when a checkout is actually cloned cold.
- Comments: only the non-obvious WHY, usually one short sentence. No history, no ticket references.
- Tests: during implementation run `dotnet test -f net10.0`; run the full matrix (`dotnet test`, no `-f`) once in the final task.
- Branch: per the user's git rules, start from an up-to-date `main` (preflight: `git status --short --branch`, `git fetch origin`, switch to `main`, `git pull --ff-only origin main`, confirm `HEAD == origin/main`), create the implementation branch `worktree-seeding`, then bring the spec and this plan along with `git cherry-pick` of their commits from `worktree-seeding-spec`. The plan must be committed on `worktree-seeding-spec` before that (it starts out untracked there); check with `git log --oneline -1 -- docs/superpowers/plans/2026-10-08-worktree-seeding-plan.md` on that branch.

## Deviations from the spec (deliberate, small)

1. **`WorktreeHome.TryResolve` returns a `HomeWorktree` record** (`AppHostDirectory`, `MainTop`, `WorktreeTop`, `WorktreeAppHostDirectory`), not only the path. The path-override check needs both roots and should not re-run git.
2. **Home is located with `git rev-parse --show-prefix`**, not `Path.GetRelativePath(worktreeTop, appHostDirectory)`. Git reports real paths, while the AppHost directory may be spelled through a symlink (`/var` vs `/private/var`) or a Windows 8.3 short temp path; the git-relative prefix sidesteps comparing the two spellings. `IWorktreeProbe` therefore returns `(GitDir, CommonDir, Top, Prefix, MainTop, MainIsBare)`. Every path in `HomeWorktree` is in git's spelling, including the worktree's own AppHost directory, so the path-override check compares like with like.
3. **`local.path`** is checked alongside `path.path` and `repository.path`. It is the deprecated alias of `repository.path` that `ServiceDeveloperConfig.ReconcileRepositoryAlias` still merges in, so the spec's "checked as long as it is read" rule covers it. Each block is checked only when the service's `source` makes the runtime read it (`path.path` for `"path"`, the other two for `"repository"`).
4. **Row 3 of the path-override table requires `Rh == Rw`.** The spec's row-3 condition ("`Rw` inside `mainTop` but not inside `worktreeTop`") also matches its own row-4 example: `../../other-repo` from `<main>/.worktrees/x/app` resolves to `<main>/.worktrees/other-repo`, which is another worktree's directory, not main's files. Row 3 is therefore "the same directory from both, inside `mainTop` and outside `worktreeTop`", and everything else with `Rh != Rw` is row 4. Both rows still warn; only the wording differs.
5. **`PrepareRepoRoot` takes `Func<string?>? homeAppHostDirectory`**, not a resolved path. The prefetch exists on every run with a `repository` service, so resolving there eagerly would spawn git on every run of a linked worktree and undo what the marker buys.
6. **Real-git tests use `TestRepository.CreateOrigin()`** (the helper every git test already uses) rather than the `sample-service.git` fixture. It provides `main` and `feature/x` branches, which the ref assertions need.

## File Structure

| File | Responsibility |
| --- | --- |
| Create `src/Aspire.Hosting.ServiceSources/Worktrees/IWorktreeProbe.cs` | `WorktreeLayout` record, `IWorktreeProbe`, production `GitWorktreeProbe` (two git calls, porcelain parsing). |
| Create `src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeHome.cs` | `HomeWorktree` record; pre-check + resolution; per-builder cache; `UseProbe`; path helpers. |
| Create `src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeSeed.cs` | Once-per-worktree config file copy, marker, notices. |
| Create `src/Aspire.Hosting.ServiceSources/Worktrees/PathOverrideCheck.cs` | Classify copied `path` overrides; build warning notices. |
| Modify `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFileSource.cs` | Call `WorktreeSeed.EnsureSeeded` before reading the file. |
| Create `src/Aspire.Hosting.ServiceSources/Git/GitReferenceFailedException.cs` | Exception for a clone that failed because of its reference. |
| Modify `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt` | Ban the new exception's constructor. |
| Modify `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs` | Default `CloneWithReference`. |
| Modify `src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs` | `CloneWithReference` override, failure classifier hook, `LooksLikeReferenceFailure`. |
| Modify `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs` | `homeAppHostDirectory` parameter, reference selection, narrow fallback with fresh scratch. |
| Modify `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs` | Resolve home once per builder, pass it to both `PrepareRepoRoot` calls. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/FakeWorktree.cs` | Main + linked worktree as plain directories, with a probe describing them. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/WorktreeHomeTests.cs` | Real-git resolution, pre-check, probe installation. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/WorktreeSeedTests.cs` | Config seeding, marker, failure, notices. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/PathOverrideCheckTests.cs` | One test per table row, plus classifier theory. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Git/GitCliCloneWithReferenceTests.cs` | Real-git reference clone, vanished reference, classifier. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/ScriptedCloneGitClient.cs` | Fake recording plain vs reference clones. |
| Create `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/CheckoutSeedingTests.cs` | Reference selection, fallback, propagation, real-git end-to-end, prefetch wiring. |
| Modify `docs/sources/repository.md` | "Working in git worktrees" section. |
| Modify `CHANGELOG.md` | `[Unreleased]` → `### Added` entry. |

---

### Task 1: Find the home worktree

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Worktrees/IWorktreeProbe.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeHome.cs`
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/FakeWorktree.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/WorktreeHomeTests.cs`

**Interfaces:**
- Consumes: `PathSource.ConfinementRootOf(string, Func<string?>)`, `PathSource.ConfinementRootKind`, `PathSource.IsOutside(string)` (all `internal static` in `Aspire.Hosting.ServiceSources.Sources`); `GitCommand.Run(IReadOnlyList<string>, IReadOnlyDictionary<string,string?>?, IGitProgressSink?)` and `GitUnavailableException` (`Aspire.Hosting.ServiceSources.Git`).
- Produces (namespace `Aspire.Hosting.ServiceSources.Worktrees`):
  - `internal readonly record struct WorktreeLayout(string GitDir, string CommonDir, string Top, string Prefix, string MainTop, bool MainIsBare)`
  - `internal interface IWorktreeProbe { WorktreeLayout? Probe(string directory); }`
  - `internal sealed class GitWorktreeProbe(IReadOnlyDictionary<string, string?>? environmentOverrides = null) : IWorktreeProbe`, with `internal static (string Top, bool IsBare)? ParseMainWorktree(string porcelain)`
  - `internal sealed record HomeWorktree(string AppHostDirectory, string MainTop, string WorktreeTop, string WorktreeAppHostDirectory)`
  - `internal static class WorktreeHome` with `HomeWorktree? TryResolve(IDistributedApplicationBuilder)`, `void UseProbe(IDistributedApplicationBuilder, IWorktreeProbe)`, `HomeWorktree? Resolve(string appHostDirectory, IWorktreeProbe probe)`, `StringComparison PathComparison`, `string Normalize(string)`, `bool SamePath(string, string)`, `bool IsInside(string path, string root)`
  - Test helper `FakeWorktree : IWorktreeProbe` (namespace `Aspire.Hosting.ServiceSources.Tests.Worktrees`): `static Siblings(bool createMainAppHost = true)`, `static Nested()`, properties `MainTop`, `WorktreeTop`, `MainAppHost`, `WorktreeAppHost`, `Calls`, methods `CreateWorktreeBuilder()`, `WriteHome(string relativePath, string content)`.

- [ ] **Step 1: Write the fake worktree helper**

`test/Aspire.Hosting.ServiceSources.Tests/Worktrees/FakeWorktree.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

/// <summary>
/// A main worktree and a linked one as plain directories, with a probe answering the way git would.
/// </summary>
internal sealed class FakeWorktree : IWorktreeProbe
{
    private int _calls;

    private FakeWorktree(string mainTop, string worktreeTop, bool createMainAppHost)
    {
        MainTop = mainTop;
        WorktreeTop = worktreeTop;

        Directory.CreateDirectory(Path.Combine(mainTop, ".git"));
        if (createMainAppHost)
        {
            Directory.CreateDirectory(MainAppHost);
        }

        Directory.CreateDirectory(WorktreeAppHost);
        File.WriteAllText(Path.Combine(worktreeTop, ".git"), $"gitdir: {Path.Combine(mainTop, ".git", "worktrees", "wt")}");
    }

    public string MainTop { get; }

    public string WorktreeTop { get; }

    public string MainAppHost => Path.Combine(MainTop, "app");

    public string WorktreeAppHost => Path.Combine(WorktreeTop, "app");

    public int Calls => Volatile.Read(ref _calls);

    public static FakeWorktree Siblings(bool createMainAppHost = true)
    {
        var root = TempDirectories.CreateSubdirectory().FullName;
        return new FakeWorktree(Path.Combine(root, "main"), Path.Combine(root, "wt"), createMainAppHost);
    }

    /// <summary>The layout <c>git worktree add .worktrees/x</c> makes.</summary>
    public static FakeWorktree Nested()
    {
        var main = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "main");
        return new FakeWorktree(main, Path.Combine(main, ".worktrees", "x"), createMainAppHost: true);
    }

    public WorktreeLayout? Probe(string directory)
    {
        Interlocked.Increment(ref _calls);

        return new WorktreeLayout(
            GitDir: Path.Combine(MainTop, ".git", "worktrees", "wt"),
            CommonDir: Path.Combine(MainTop, ".git"),
            Top: WorktreeTop,
            Prefix: "app/",
            MainTop: MainTop,
            MainIsBare: false);
    }

    public IDistributedApplicationBuilder CreateWorktreeBuilder()
    {
        var builder = TestHelpers.CreateBuilder(WorktreeAppHost);
        WorktreeHome.UseProbe(builder, this);
        return builder;
    }

    public void WriteHome(string relativePath, string content)
    {
        var path = Path.Combine(MainAppHost, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Worktrees/WorktreeHomeTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Tests.Git;
using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class WorktreeHomeTests
{
    private static GitWorktreeProbe RealProbe() => new(TestRepository.IsolatedEnvironment());

    /// <summary>A repository with a committed AppHost directory at <c>app</c>.</summary>
    private static TestRepository CreateMain()
    {
        var main = TestRepository.CreateOrigin();
        Directory.CreateDirectory(Path.Combine(main.Path, "app"));
        main.Commit("app/AppHost.txt", "apphost", "add apphost");

        // Untracked, so only the main worktree has it: proves which directory resolved.
        main.Write("app/main-only.txt", "main");
        return main;
    }

    private static string AddWorktree(TestRepository from, string path, string branch)
    {
        from.Git("worktree", "add", "--quiet", "-b", branch, path);
        return path;
    }

    private static void AssertIsMainAppHost(HomeWorktree? home)
    {
        Assert.NotNull(home);
        Assert.Equal("main", File.ReadAllText(Path.Combine(home.AppHostDirectory, "main-only.txt")));
    }

    private sealed class RecordingProbe : IWorktreeProbe
    {
        public int Calls { get; private set; }

        public WorktreeLayout? Probe(string directory)
        {
            Calls++;
            return null;
        }
    }

    [Fact]
    public void LinkedWorktree_ResolvesToTheMainWorktreesAppHost()
    {
        var main = CreateMain();
        var worktree = AddWorktree(main, Path.Combine(TempDirectories.CreateSubdirectory().FullName, "wt"), "wt");

        AssertIsMainAppHost(WorktreeHome.Resolve(Path.Combine(worktree, "app"), RealProbe()));
    }

    [Fact]
    public void WorktreeOfAWorktree_ResolvesToTheMainWorktree()
    {
        var main = CreateMain();
        var first = AddWorktree(main, Path.Combine(TempDirectories.CreateSubdirectory().FullName, "wt1"), "wt1");
        var second = AddWorktree(TestRepository.At(first), Path.Combine(TempDirectories.CreateSubdirectory().FullName, "wt2"), "wt2");

        AssertIsMainAppHost(WorktreeHome.Resolve(Path.Combine(second, "app"), RealProbe()));
    }

    [Fact]
    public void WorktreeNestedInsideTheMainWorktree_ResolvesToTheMainWorktree()
    {
        var main = CreateMain();
        var nested = AddWorktree(main, Path.Combine(main.Path, ".worktrees", "x"), "nested");

        AssertIsMainAppHost(WorktreeHome.Resolve(Path.Combine(nested, "app"), RealProbe()));
    }

    [Fact]
    public void BareRepositoryLayout_ResolvesToNull()
    {
        var main = CreateMain();
        var parent = TempDirectories.CreateSubdirectory().FullName;
        TestRepository.At(parent).Git("clone", "--bare", "--quiet", main.Path, "bare.git");
        var worktree = Path.Combine(parent, "wt");
        TestRepository.At(Path.Combine(parent, "bare.git")).Git("worktree", "add", "--quiet", "-b", "wt", worktree);

        Assert.Null(WorktreeHome.Resolve(Path.Combine(worktree, "app"), RealProbe()));
    }

    [Fact]
    public void MainWorktree_ResolvesToNullWithoutAskingGit()
    {
        var main = CreateMain();
        var probe = new RecordingProbe();

        Assert.Null(WorktreeHome.Resolve(Path.Combine(main.Path, "app"), probe));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void DirectoryOutsideAnyRepository_ResolvesToNullWithoutAskingGit()
    {
        var probe = new RecordingProbe();

        Assert.Null(WorktreeHome.Resolve(TempDirectories.CreateSubdirectory().FullName, probe));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void HomeWithoutTheAppHostDirectory_ResolvesToNull()
    {
        var fake = FakeWorktree.Siblings(createMainAppHost: false);

        Assert.Null(WorktreeHome.Resolve(fake.WorktreeAppHost, fake));
    }

    [Fact]
    public void ParseMainWorktree_ReadsTheFirstRecordAndItsBareFlag()
    {
        Assert.Equal<(string, bool)?>(("/r/main", false), GitWorktreeProbe.ParseMainWorktree(
            "worktree /r/main\nHEAD abc\nbranch refs/heads/main\n\nworktree /r/wt\nHEAD def\nbranch refs/heads/wt\n"));
        Assert.Equal<(string, bool)?>(("/r/bare.git", true), GitWorktreeProbe.ParseMainWorktree(
            "worktree /r/bare.git\nbare\n\nworktree /r/wt\nHEAD def\nbranch refs/heads/wt\n"));
        Assert.Null(GitWorktreeProbe.ParseMainWorktree(""));
    }

    [Fact]
    public void TryResolve_IsCachedPerBuilder()
    {
        var fake = FakeWorktree.Siblings();
        var builder = fake.CreateWorktreeBuilder();

        var first = WorktreeHome.TryResolve(builder);
        var second = WorktreeHome.TryResolve(builder);

        Assert.Same(first, second);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void UseProbe_AfterResolution_Throws()
    {
        var fake = FakeWorktree.Siblings();
        var builder = fake.CreateWorktreeBuilder();
        WorktreeHome.TryResolve(builder);

        Assert.Throws<InvalidOperationException>(() => WorktreeHome.UseProbe(builder, fake));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~WorktreeHomeTests"`
Expected: build FAILS — `Aspire.Hosting.ServiceSources.Worktrees` does not exist.

- [ ] **Step 4: Write the probe**

`src/Aspire.Hosting.ServiceSources/Worktrees/IWorktreeProbe.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Worktrees;

/// <summary>What git says about the worktree a directory is in.</summary>
/// <param name="Top">The directory's worktree root, in git's spelling.</param>
/// <param name="Prefix">The directory relative to its worktree's root, '/'-separated, as <c>--show-prefix</c> prints it.</param>
internal readonly record struct WorktreeLayout(
    string GitDir, string CommonDir, string Top, string Prefix, string MainTop, bool MainIsBare);

/// <summary>
/// Asks git about a directory's worktree.
/// </summary>
/// <remarks>
/// Separate from <see cref="IGitClient"/> because config seeding runs before the source registry,
/// where the git clients live, exists.
/// </remarks>
internal interface IWorktreeProbe
{
    /// <summary>The layout, or <see langword="null"/> when git cannot say.</summary>
    WorktreeLayout? Probe(string directory);
}

internal sealed class GitWorktreeProbe(IReadOnlyDictionary<string, string?>? environmentOverrides = null) : IWorktreeProbe
{
    public WorktreeLayout? Probe(string directory)
    {
        try
        {
            var revParse = GitCommand.Run(
                ["-C", directory, "rev-parse", "--path-format=absolute",
                    "--git-dir", "--git-common-dir", "--show-toplevel", "--show-prefix"],
                environmentOverrides);
            if (!revParse.Succeeded)
            {
                return null;
            }

            // Not trimmed as a whole: at the worktree root the prefix is an empty last line.
            var lines = revParse.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

            // Git older than 2.31 echoes the unknown --path-format back as output rather than failing.
            if (lines.Length < 4 || lines[0].StartsWith('-'))
            {
                return null;
            }

            var list = GitCommand.Run(["-C", directory, "worktree", "list", "--porcelain"], environmentOverrides);
            if (!list.Succeeded || ParseMainWorktree(list.StandardOutput) is not { } main)
            {
                return null;
            }

            return new WorktreeLayout(lines[0], lines[1], lines[2], lines[3], main.Top, main.IsBare);
        }
        catch (GitUnavailableException)
        {
            return null;
        }
    }

    /// <summary>The first record of <c>git worktree list --porcelain</c>, which is always the main worktree.</summary>
    internal static (string Top, bool IsBare)? ParseMainWorktree(string porcelain)
    {
        const string WorktreePrefix = "worktree ";
        string? top = null;
        var bare = false;

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (top is null)
            {
                if (!line.StartsWith(WorktreePrefix, StringComparison.Ordinal))
                {
                    return null;
                }

                top = line[WorktreePrefix.Length..];
            }
            else if (line.Length == 0)
            {
                break;
            }
            else if (line == "bare")
            {
                bare = true;
            }
        }

        return top is null ? null : (top, bare);
    }
}
```

- [ ] **Step 5: Write the resolver**

`src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeHome.cs`:

```csharp
using System.Runtime.CompilerServices;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Worktrees;

/// <summary>The main worktree's copy of an AppHost directory that runs from a linked worktree.</summary>
/// <param name="AppHostDirectory">The AppHost directory inside the main worktree.</param>
/// <param name="MainTop">The main worktree's root.</param>
/// <param name="WorktreeTop">The linked worktree's root.</param>
/// <param name="WorktreeAppHostDirectory">This AppHost directory, spelled the way git spells the other three.</param>
internal sealed record HomeWorktree(
    string AppHostDirectory, string MainTop, string WorktreeTop, string WorktreeAppHostDirectory);

/// <summary>
/// Finds the main worktree's copy of this AppHost directory, once per builder.
/// </summary>
internal static class WorktreeHome
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, Slot> Slots = new();

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static HomeWorktree? TryResolve(IDistributedApplicationBuilder builder) =>
        SlotFor(builder).Resolve(builder.AppHostDirectory);

    /// <summary>Replaces git for one builder; must happen before the builder's first entry point.</summary>
    public static void UseProbe(IDistributedApplicationBuilder builder, IWorktreeProbe probe) =>
        SlotFor(builder).UseProbe(probe);

    internal static HomeWorktree? Resolve(string appHostDirectory, IWorktreeProbe probe)
    {
        var appHost = Normalize(appHostDirectory);

        // Only a '.git' file can be a linked worktree, so the main worktree and non-git AppHosts never spawn git.
        var root = PathSource.ConfinementRootOf(appHost, static () => null);
        if (root.Kind != PathSource.ConfinementRootKind.Git || !File.Exists(Path.Combine(root.Directory, ".git")))
        {
            return null;
        }

        // A submodule or --separate-git-dir clone also has a '.git' file, but its git dir is its common dir.
        if (probe.Probe(appHost) is not { } layout || layout.MainIsBare || SamePath(layout.GitDir, layout.CommonDir))
        {
            return null;
        }

        // Located through git's prefix rather than a relative path, because git reports real paths and
        // the AppHost directory may be spelled through a symlink or a short name.
        var segments = layout.Prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var worktreeTop = Normalize(layout.Top);
        var worktreeAppHost = Normalize(Path.Combine([worktreeTop, .. segments]));
        var mainTop = Normalize(layout.MainTop);
        var home = Normalize(Path.Combine([mainTop, .. segments]));

        return !SamePath(home, worktreeAppHost) && Directory.Exists(home)
            ? new HomeWorktree(home, mainTop, worktreeTop, worktreeAppHost)
            : null;
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), PathComparison);

    internal static bool IsInside(string path, string root) => !PathSource.IsOutside(Path.GetRelativePath(root, path));

    private static Slot SlotFor(IDistributedApplicationBuilder builder) =>
        Slots.GetValue(builder, static _ => new Slot());

    private sealed class Slot
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();
        private IWorktreeProbe _probe = new GitWorktreeProbe();
        private bool _resolved;
        private HomeWorktree? _home;

        public void UseProbe(IWorktreeProbe probe)
        {
            lock (_gate)
            {
                if (_resolved)
                {
                    throw new InvalidOperationException(
                        "A worktree probe must be installed before the builder's first ServiceSources call.");
                }

                _probe = probe;
            }
        }

        public HomeWorktree? Resolve(string appHostDirectory)
        {
            lock (_gate)
            {
                if (!_resolved)
                {
                    _home = WorktreeHome.Resolve(appHostDirectory, _probe);
                    _resolved = true;
                }

                return _home;
            }
        }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~WorktreeHomeTests"`
Expected: PASS (10 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Worktrees test/Aspire.Hosting.ServiceSources.Tests/Worktrees
git commit -m "Find the main worktree's copy of an AppHost run from a linked worktree

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Seed config files once per worktree

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeSeed.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFileSource.cs` (inside `Registration.Register`, right after the `if (_registered) { return; }` block, before `ReadFileSource`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/WorktreeSeedTests.cs`

**Interfaces:**
- Consumes: `WorktreeHome.TryResolve`, `HomeWorktree` (Task 1); `FakeWorktree` (Task 1); `ToolDirectory.Ensure/PathIn`; `SourceSelectionStore.PathIn(string)` and `SourceSelectionStore.FileName`; `DeveloperConfiguration.FileName`; `ServiceSourcesWarnings.For(builder).AddNotice(Raw)` / `.Messages`; `Raw`, `Name`.
- Produces:
  - `internal static class WorktreeSeed` with `const string MarkerFileName = "worktree-seed.json"`, `static string MarkerPath(string appHostDirectory)`, `static void EnsureSeeded(IDistributedApplicationBuilder builder)`.
  - Inside the private `Seed`, a `List<string> copied` holding the display names of copied files (`DeveloperConfiguration.FileName` for the config file). Task 3 inserts its call just before the "copied" notice and keys on that list.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Worktrees/WorktreeSeedTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class WorktreeSeedTests
{
    private const string HomeConfig = """{ "services": { "orders": { "source": "repository" } } }""";
    private const string HomeSelection = """{ "orders": "repository" }""";

    private static string SelectionRelative => Path.Combine(ToolDirectory.Name, SourceSelectionStore.FileName);

    private static IDistributedApplicationBuilder Run(FakeWorktree fake)
    {
        var builder = fake.CreateWorktreeBuilder();
        DeveloperConfigFileSource.EnsureRegistered(builder);
        return builder;
    }

    private static IReadOnlyList<string> Notices(IDistributedApplicationBuilder builder) =>
        ServiceSourcesWarnings.For(builder).Messages;

    [Fact]
    public void AbsentFiles_AreCopiedFromHome_AndReported()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        fake.WriteHome(SelectionRelative, HomeSelection);

        var builder = Run(fake);

        Assert.Equal(HomeConfig, File.ReadAllText(Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName)));
        Assert.Equal(HomeSelection, File.ReadAllText(SourceSelectionStore.PathIn(fake.WorktreeAppHost)));
        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));

        var notice = Assert.Single(Notices(builder));
        Assert.Contains(DeveloperConfiguration.FileName, notice);
        Assert.Contains("selection.json", notice);
        Assert.Contains(Name.Escape(fake.MainAppHost), notice);
    }

    [Fact]
    public void SeededConfig_IsWhatTheFirstRunReads()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);

        var builder = Run(fake);

        Assert.Equal("repository", builder.Configuration["ServiceSources:Services:orders:source"]);
    }

    [Fact]
    public void ExistingWorktreeFiles_AreNeverOverwritten()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        var own = """{ "services": { "orders": { "source": "disabled" } } }""";
        File.WriteAllText(Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName), own);

        var builder = Run(fake);

        Assert.Equal(own, File.ReadAllText(Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName)));
        Assert.Empty(Notices(builder));
        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
    }

    [Fact]
    public void Marker_StopsASecondSeed_SoADeletedCopyStaysDeleted()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        Run(fake);
        var copy = Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName);
        File.Delete(copy);

        var second = Run(fake);

        Assert.False(File.Exists(copy));
        Assert.Empty(Notices(second));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void HomeWithNothingToCopy_StillWritesTheMarker()
    {
        var fake = FakeWorktree.Siblings();

        var builder = Run(fake);

        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
        Assert.Empty(Notices(builder));
    }

    [Fact]
    public void UnresolvedHome_WritesNoMarker_AndCreatesNoToolDirectory()
    {
        var fake = FakeWorktree.Siblings(createMainAppHost: false);

        Run(fake);

        Assert.False(Directory.Exists(ToolDirectory.PathIn(fake.WorktreeAppHost)));
    }

    [Fact]
    public void UnreadableHomeFile_IsReported_LeavesNoMarker_AndIsRetriedNextRun()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        var homeConfig = Path.Combine(fake.MainAppHost, DeveloperConfiguration.FileName);
        var copy = Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName);

        using (new FileStream(homeConfig, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var builder = Run(fake);

            Assert.False(File.Exists(copy));
            Assert.False(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
            Assert.Contains(Notices(builder), notice => notice.Contains("next run", StringComparison.Ordinal));
        }

        Run(fake);

        Assert.Equal(HomeConfig, File.ReadAllText(copy));
        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
    }

    [Fact]
    public void MainWorktree_IsLeftAlone()
    {
        var fake = FakeWorktree.Siblings();
        var builder = TestHelpers.CreateBuilder(fake.MainAppHost);
        WorktreeHome.UseProbe(builder, fake);

        DeveloperConfigFileSource.EnsureRegistered(builder);

        Assert.Equal(0, fake.Calls);
        Assert.False(Directory.Exists(ToolDirectory.PathIn(fake.MainAppHost)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~WorktreeSeedTests"`
Expected: build FAILS — `WorktreeSeed` does not exist.

- [ ] **Step 3: Write the seeder**

`src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeSeed.cs`:

```csharp
using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Worktrees;

/// <summary>
/// Copies the main worktree's git-ignored ServiceSources files into a linked worktree, once.
/// </summary>
internal static class WorktreeSeed
{
    public const string MarkerFileName = "worktree-seed.json";

    public static string MarkerPath(string appHostDirectory) =>
        Path.Combine(ToolDirectory.PathIn(appHostDirectory), MarkerFileName);

    /// <summary>Never throws: a seeding problem is a notice, and the next run tries again.</summary>
    public static void EnsureSeeded(IDistributedApplicationBuilder builder)
    {
        try
        {
            Seed(builder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceSourcesWarnings.For(builder).AddNotice(Raw.Compose(
                $"Could not finish seeding this linked worktree's ServiceSources files from the main worktree ({Raw.Cause(ex)}). It will be tried again on the next run."));
        }
    }

    private static void Seed(IDistributedApplicationBuilder builder)
    {
        var worktree = builder.AppHostDirectory;
        var marker = MarkerPath(worktree);

        // The marker, not the files, records that seeding happened, so a copy the developer deletes stays deleted.
        if (File.Exists(marker) || WorktreeHome.TryResolve(builder) is not { } home)
        {
            return;
        }

        ToolDirectory.Ensure(worktree);

        var warnings = ServiceSourcesWarnings.For(builder);
        var copied = new List<string>();
        var failed = false;

        (string Source, string Destination, string Display)[] files =
        [
            (Path.Combine(home.AppHostDirectory, DeveloperConfiguration.FileName),
                Path.Combine(worktree, DeveloperConfiguration.FileName),
                DeveloperConfiguration.FileName),
            (SourceSelectionStore.PathIn(home.AppHostDirectory),
                SourceSelectionStore.PathIn(worktree),
                $"{ToolDirectory.Name}/{SourceSelectionStore.FileName}"),
        ];

        foreach (var (source, destination, display) in files)
        {
            try
            {
                if (CopyIfAbsent(source, destination))
                {
                    copied.Add(display);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
                warnings.AddNotice(Raw.Compose(
                    $"Could not copy '{Raw.Escaped(source)}' into this linked worktree ({Raw.Cause(ex)}). Seeding from the main worktree will be tried again on the next run."));
            }
        }

        if (copied.Count > 0)
        {
            warnings.AddNotice(Raw.Compose(
                $"This AppHost runs from a linked git worktree, so its ServiceSources files were seeded from the main worktree's AppHost at '{Raw.Escaped(home.AppHostDirectory)}': copied {Raw.Join(", ", copied.Select(name => Raw.Escaped(name)))}. They are this worktree's own from now on."));
        }

        if (!failed)
        {
            WriteMarker(marker, home.AppHostDirectory, copied);
        }
    }

    /// <summary>Copies a regular file to a destination that must not exist yet.</summary>
    /// <returns>Whether this call created the destination.</returns>
    private static bool CopyIfAbsent(string source, string destination)
    {
        if (File.Exists(destination)
            || !File.Exists(source)
            || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        FileStream output;
        try
        {
            output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // A concurrent AppHost in the same worktree copied it first.
            return false;
        }

        var complete = false;
        try
        {
            using (output)
            {
                input.CopyTo(output);
            }

            complete = true;
        }
        finally
        {
            // A half-written copy would block every later seed, since existing files are never overwritten.
            if (!complete)
            {
                TryDelete(destination);
            }
        }

        return true;
    }

    private static void WriteMarker(string marker, string home, IReadOnlyList<string> copied)
    {
        var json = new JsonObject
        {
            ["home"] = home,
            ["copied"] = new JsonArray([.. copied.Select(name => (JsonNode?)JsonValue.Create(name))]),
        }.ToJsonString();

        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
        catch (IOException) when (File.Exists(marker))
        {
            // A concurrent AppHost wrote it first.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
```

- [ ] **Step 4: Hook it into registration**

In `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFileSource.cs`, add `using Aspire.Hosting.ServiceSources.Worktrees;` at the top, and in `Registration.Register` change:

```csharp
                if (_registered)
                {
                    return;
                }

                // Reading the file is the part that can fail on the file's own account, and it
```

to:

```csharp
                if (_registered)
                {
                    return;
                }

                // Before the read, so a linked worktree's first run reads what was seeded from the main worktree.
                WorktreeSeed.EnsureSeeded(builder);

                // Reading the file is the part that can fail on the file's own account, and it
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~Worktrees"`
Expected: PASS.

- [ ] **Step 6: Audit tests whose AppHost directory sits inside this repository**

A test whose AppHost directory is inside the repository would, when run from a linked worktree of this repository, seed from the developer's real main worktree. Find candidates:

```bash
grep -rn "AppContext.BaseDirectory\|ProjectDirectory =\|samples/" test --include=*.cs
```

For each hit that passes an in-repository directory to a builder (not a path under `TempDirectories` or a `TestRepository`), add `WorktreeHome.UseProbe(builder, NullWorktreeProbe.Instance);` immediately after the builder is created, using this helper added to `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/FakeWorktree.cs`:

```csharp
internal sealed class NullWorktreeProbe : IWorktreeProbe
{
    public static readonly NullWorktreeProbe Instance = new();

    public WorktreeLayout? Probe(string directory) => null;
}
```

If no test builds a builder over an in-repository directory, record that in the commit message and add nothing.

- [ ] **Step 7: Run the whole core test project**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources test/Aspire.Hosting.ServiceSources.Tests
git commit -m "Seed a linked worktree's developer config and source picks from the main worktree

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Warn about copied path overrides that resolve differently

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Worktrees/PathOverrideCheck.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeSeed.cs` (in `Seed`, after the copy loop)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/PathOverrideCheckTests.cs`

**Interfaces:**
- Consumes: `HomeWorktree`, `WorktreeHome.SamePath/IsInside/PathComparison` (Task 1); `WorktreeSeed` (Task 2); `DeveloperConfigFileSource.FileServicesKey` (`"services"`, internal const); `DeveloperConfiguration.FileName`.
- Produces:
  - `internal enum PathOverrideVerdict { Unchanged, MirrorsMainWorktree, IntoMainWorktree, ResolvesDifferently }`
  - `internal static class PathOverrideCheck` with `static PathOverrideVerdict Classify(string fromHome, string fromWorktree, string mainTop, string worktreeTop)` and `static IReadOnlyList<Raw> Inspect(string configFile, HomeWorktree home)`. `Inspect` resolves the worktree side from `home.WorktreeAppHostDirectory`, so both sides are in git's spelling.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Worktrees/PathOverrideCheckTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class PathOverrideCheckTests
{
    private static void WriteHomeOverride(FakeWorktree fake, string section, string value)
    {
        var json = new JsonObject
        {
            ["services"] = new JsonObject
            {
                ["orders"] = new JsonObject
                {
                    ["source"] = section == "path" ? "path" : "repository",
                    [section] = new JsonObject { ["path"] = value },
                },
            },
        };

        fake.WriteHome(DeveloperConfiguration.FileName, json.ToJsonString());
    }

    private static IReadOnlyList<string> PathNotices(FakeWorktree fake)
    {
        var builder = fake.CreateWorktreeBuilder();
        DeveloperConfigFileSource.EnsureRegistered(builder);
        return [.. ServiceSourcesWarnings.For(builder).Messages.Where(message => message.Contains("Service 'orders'", StringComparison.Ordinal))];
    }

    [Fact]
    public void AbsoluteExternalPath_IsSilent()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomeOverride(fake, "path", TempDirectories.CreateSubdirectory().FullName);

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void InRepositoryRelativePath_IsSilent()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomeOverride(fake, "path", "../services/orders");

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void AbsolutePathIntoTheMainWorktree_Warns()
    {
        var fake = FakeWorktree.Siblings();
        var intoMain = Path.Combine(fake.MainAppHost, ".servicesources", "checkouts", "orders");
        WriteHomeOverride(fake, "path", intoMain);

        var notice = Assert.Single(PathNotices(fake));
        Assert.Contains("'path.path'", notice);
        Assert.Contains("main worktree", notice);
        Assert.Contains(DeveloperConfiguration.FileName, notice);
    }

    [Fact]
    public void EscapingRelativePathFromANestedWorktree_Warns()
    {
        var fake = FakeWorktree.Nested();
        var parent = Path.GetDirectoryName(fake.MainTop)!;
        Directory.CreateDirectory(Path.Combine(parent, "other-repo"));
        Directory.CreateDirectory(Path.Combine(fake.MainTop, ".worktrees", "other-repo"));
        WriteHomeOverride(fake, "path", "../../other-repo");

        var notice = Assert.Single(PathNotices(fake));
        Assert.Contains("different directory", notice);
        Assert.DoesNotContain("build and edit the main worktree's files", notice);
    }

    [Fact]
    public void BlockTheSourceDoesNotRead_IsSilent()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(
            DeveloperConfiguration.FileName,
            new JsonObject
            {
                ["services"] = new JsonObject
                {
                    ["orders"] = new JsonObject
                    {
                        ["source"] = "Repository",
                        ["path"] = new JsonObject { ["path"] = fake.MainTop },
                    },
                },
            }.ToJsonString());

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void InRepositoryRelativePathFromANestedWorktree_IsSilent()
    {
        var fake = FakeWorktree.Nested();
        WriteHomeOverride(fake, "path", "../services/orders");

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void DeprecatedRepositoryPathIntoTheMainWorktree_WarnsTheSameWay()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomeOverride(fake, "repository", Path.Combine(fake.MainTop, "orders"));

        var notice = Assert.Single(PathNotices(fake));
        Assert.Contains("'repository.path'", notice);
    }

    [Fact]
    public void AnEntryWrittenAfterSeeding_IsNotInspected()
    {
        var fake = FakeWorktree.Siblings();
        DeveloperConfigFileSource.EnsureRegistered(fake.CreateWorktreeBuilder());
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName),
            new JsonObject
            {
                ["services"] = new JsonObject
                {
                    ["orders"] = new JsonObject { ["source"] = "path", ["path"] = new JsonObject { ["path"] = fake.MainTop } },
                },
            }.ToJsonString());

        Assert.Empty(PathNotices(fake));
    }

    // The verdict travels as a string: PathOverrideVerdict is internal, and a public theory cannot take it (CS0051).
    [Theory]
    // Row 1: the same external directory from both.
    [InlineData("/x/elsewhere", "/x/elsewhere", nameof(PathOverrideVerdict.Unchanged))]
    // Row 2: each worktree's own copy at the same position.
    [InlineData("/x/main/services/orders", "/x/wt/services/orders", nameof(PathOverrideVerdict.MirrorsMainWorktree))]
    // Row 3: the worktree would use main's files.
    [InlineData("/x/main/orders", "/x/main/orders", nameof(PathOverrideVerdict.IntoMainWorktree))]
    // Row 4: a sibling at a different depth.
    [InlineData("/x/other", "/other", nameof(PathOverrideVerdict.ResolvesDifferently))]
    // Row 4, nested: another worktree beside this one under main's .worktrees is not main's own files.
    [InlineData("/x/other-repo", "/x/main/.worktrees/other-repo", nameof(PathOverrideVerdict.ResolvesDifferently), "/x/main/.worktrees/x")]
    public void Classify_FollowsTheTable(string fromHome, string fromWorktree, string expected, string worktreeTop = "/x/wt")
    {
        static string Full(string path) => Path.GetFullPath(path);

        Assert.Equal(
            Enum.Parse<PathOverrideVerdict>(expected),
            PathOverrideCheck.Classify(Full(fromHome), Full(fromWorktree), Full("/x/main"), Full(worktreeTop)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~PathOverrideCheckTests"`
Expected: build FAILS — `PathOverrideCheck` and `PathOverrideVerdict` do not exist.

- [ ] **Step 3: Write the check**

`src/Aspire.Hosting.ServiceSources/Worktrees/PathOverrideCheck.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Worktrees;

internal enum PathOverrideVerdict
{
    /// <summary>The same directory outside the main worktree, from both: the developer's own external checkout.</summary>
    Unchanged,

    /// <summary>Each worktree's own directory at the same position: the monorepo case.</summary>
    MirrorsMainWorktree,

    /// <summary>The worktree would build and edit the main worktree's files.</summary>
    IntoMainWorktree,

    /// <summary>A missing directory or, worse, a different real one.</summary>
    ResolvesDifferently,
}

/// <summary>
/// Inspects directory overrides in a just-copied <c>servicesources.local.json</c>, once.
/// </summary>
/// <remarks>
/// Reports rather than rewrites: the worktree's file stays the truth, and a rewrite would lose its comments.
/// </remarks>
internal static class PathOverrideCheck
{
    // repository.path and its deprecated local.path alias still resolve a directory the same way.
    // Each is read only under the source that uses it, so a leftover block for another source is inert.
    private static readonly (string Section, string Key, string Source)[] Overrides =
    [
        ("path", "path.path", "path"),
        ("repository", "repository.path", "repository"),
        ("local", "local.path", "repository"),
    ];

    public static PathOverrideVerdict Classify(string fromHome, string fromWorktree, string mainTop, string worktreeTop)
    {
        var same = WorktreeHome.SamePath(fromHome, fromWorktree);

        // "Outside worktreeTop" matters because a worktree nested in main has its own files inside mainTop too.
        if (same && WorktreeHome.IsInside(fromWorktree, mainTop) && !WorktreeHome.IsInside(fromWorktree, worktreeTop))
        {
            return PathOverrideVerdict.IntoMainWorktree;
        }

        if (same && !WorktreeHome.IsInside(fromHome, mainTop))
        {
            return PathOverrideVerdict.Unchanged;
        }

        if (WorktreeHome.IsInside(fromHome, mainTop)
            && WorktreeHome.IsInside(fromWorktree, worktreeTop)
            && string.Equals(
                Path.GetRelativePath(mainTop, fromHome),
                Path.GetRelativePath(worktreeTop, fromWorktree),
                WorktreeHome.PathComparison))
        {
            return PathOverrideVerdict.MirrorsMainWorktree;
        }

        return PathOverrideVerdict.ResolvesDifferently;
    }

    /// <summary>One notice per override that does not resolve the way it did in the main worktree.</summary>
    public static IReadOnlyList<Raw> Inspect(string configFile, HomeWorktree home)
    {
        try
        {
            var file = new ConfigurationBuilder().AddJsonFile(configFile, optional: true).Build();
            using (file as IDisposable)
            {
                var notices = new List<Raw>();

                foreach (var service in file.GetSection(DeveloperConfigFileSource.FileServicesKey).GetChildren())
                {
                    foreach (var (section, key, source) in Overrides)
                    {
                        if (!string.Equals(service["source"], source, StringComparison.OrdinalIgnoreCase)
                            || service[$"{section}:path"] is not { } value
                            || string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        var fromHome = Path.GetFullPath(value, home.AppHostDirectory);
                        var fromWorktree = Path.GetFullPath(value, home.WorktreeAppHostDirectory);
                        var verdict = Classify(fromHome, fromWorktree, home.MainTop, home.WorktreeTop);

                        if (verdict is PathOverrideVerdict.IntoMainWorktree or PathOverrideVerdict.ResolvesDifferently)
                        {
                            notices.Add(Notice(service.Key, key, value, fromHome, fromWorktree, configFile, verdict));
                        }
                    }
                }

                return notices;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
        {
            // A malformed file is reported by registration, which reads it next.
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy already happened; escaping here would lose the "copied" notice and the marker for good.
            return [];
        }
    }

    private static Raw Notice(
        string serviceName, string key, string value, string fromHome, string fromWorktree, string configFile,
        PathOverrideVerdict verdict)
    {
        var consequence = verdict == PathOverrideVerdict.IntoMainWorktree
            ? Raw.Literal("which is inside the main worktree, so this worktree would build and edit the main worktree's files")
            : Raw.Literal("which is a different directory");

        return Raw.Compose(
            $"Service '{new Name(serviceName)}': '{Raw.Escaped(key)}' is '{Raw.Escaped(value)}' in the {Raw.Literal(DeveloperConfiguration.FileName)} copied from the main worktree. " +
            $"From the main worktree's AppHost it resolves to '{Raw.Escaped(fromHome)}', but from this worktree's it resolves to '{Raw.Escaped(fromWorktree)}', {consequence}. " +
            $"If that is not what you want, edit '{Raw.Escaped(configFile)}'.");
    }
}
```

- [ ] **Step 4: Call it from the seeder**

In `src/Aspire.Hosting.ServiceSources/Worktrees/WorktreeSeed.cs`, inside `Seed`, change:

```csharp
        if (copied.Count > 0)
        {
            warnings.AddNotice(Raw.Compose(
```

to:

```csharp
        // Only entries seeding just copied: one the developer writes afterwards is deliberate.
        if (copied.Contains(DeveloperConfiguration.FileName))
        {
            foreach (var notice in PathOverrideCheck.Inspect(
                Path.Combine(worktree, DeveloperConfiguration.FileName), home))
            {
                warnings.AddNotice(notice);
            }
        }

        if (copied.Count > 0)
        {
            warnings.AddNotice(Raw.Compose(
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~Worktrees"`
Expected: PASS. (`WorktreeSeedTests.AbsentFiles_AreCopiedFromHome_AndReported` still sees a single notice: its config has no path override.)

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Worktrees test/Aspire.Hosting.ServiceSources.Tests/Worktrees
git commit -m "Warn when a seeded path override resolves differently from the worktree

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Clone with a reference repository

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/GitReferenceFailedException.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt` (append one line)
- Modify: `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs` (after `Clone`)
- Modify: `src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs` (`Clone` neighbourhood, `RunRemoteCommand`, new `LooksLikeReferenceFailure`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/GitCliCloneWithReferenceTests.cs`

**Interfaces:**
- Consumes: `GitCommandResult`, `GitCommandFailedException.For`, `Describe(GitCommandResult)` (private in `GitCliClient`, returns `Raw`), `TestRepository`.
- Produces:
  - `internal sealed class GitReferenceFailedException : Exception` with `static GitReferenceFailedException For(ServiceTextHandler message, Exception innerException)`.
  - `IGitClient.CloneWithReference(string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null)` — default delegates to `Clone`.
  - `GitCliClient.LooksLikeReferenceFailure(string standardError, string referenceRepository)` — `internal static bool`.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Git/GitCliCloneWithReferenceTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class GitCliCloneWithReferenceTests
{
    // A URL rather than a path: git hardlinks from a plain path, which would not exercise borrowing.
    private static string CloneUrl(string path) => new Uri(path).AbsoluteUri;

    private static GitCliClient Client() => new(TestRepository.IsolatedEnvironment());

    private static string[] RemoteRefs(string checkout) =>
        TestRepository.At(checkout).Git("for-each-ref", "--format=%(refname)", "refs/remotes")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void CloneWithReference_LeavesAnIndependentCloneOfTheRealRemote()
    {
        var origin = TestRepository.CreateOrigin();
        var reference = TestRepository.EmptyDestination("reference");
        Client().Clone(CloneUrl(origin.Path), reference);
        TestRepository.At(reference).Git("branch", "home-only");
        var destination = TestRepository.EmptyDestination();

        Client().CloneWithReference(CloneUrl(origin.Path), destination, reference);

        Assert.False(File.Exists(Path.Combine(destination, ".git", "objects", "info", "alternates")));
        Assert.Equal(CloneUrl(origin.Path), Client().GetOriginUrl(destination));
        Assert.Contains("refs/remotes/origin/main", RemoteRefs(destination));
        Assert.Contains("refs/remotes/origin/feature/x", RemoteRefs(destination));
        Assert.DoesNotContain("refs/remotes/origin/home-only", RemoteRefs(destination));
        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void CloneWithReference_ReferenceGone_ClonesNormally()
    {
        var origin = TestRepository.CreateOrigin();
        var missing = TestRepository.EmptyDestination("gone");
        var destination = TestRepository.EmptyDestination();

        Client().CloneWithReference(CloneUrl(origin.Path), destination, missing);

        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void CloneWithReference_UnreachableRemote_IsNotAReferenceFailure()
    {
        var missingRemote = CloneUrl(TestRepository.EmptyDestination("no-such-remote.git"));
        var missingReference = TestRepository.EmptyDestination("gone");

        var failure = Record.Exception(() =>
            Client().CloneWithReference(missingRemote, TestRepository.EmptyDestination(), missingReference));

        Assert.NotNull(failure);
        Assert.IsNotType<GitReferenceFailedException>(failure);
    }

    [Theory]
    [InlineData("fatal: reference repository '/home/x/orders' is shallow", true)]
    [InlineData("error: unable to normalize alternate object path: /tmp/objects", true)]
    [InlineData("fatal: cannot repack to clean up", true)]
    [InlineData("error: object file /home/x/orders/.git/objects/ab/cdef is empty", true)]
    [InlineData("fatal: unable to access 'https://example.com/orders.git/': Could not resolve host: example.com", false)]
    [InlineData("info: Could not add alternate for '/home/x/orders': reference repository '/home/x/orders' does not exist\nfatal: unable to access 'https://example.com/orders.git/': Could not resolve host", false)]
    public void LooksLikeReferenceFailure_BlamesTheReferenceOnlyWhenGitDoes(string standardError, bool expected) =>
        Assert.Equal(expected, GitCliClient.LooksLikeReferenceFailure(standardError, "/home/x/orders"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~GitCliCloneWithReferenceTests"`
Expected: build FAILS — `CloneWithReference`, `GitReferenceFailedException`, `LooksLikeReferenceFailure` do not exist.

- [ ] **Step 3: Add the exception and ban its constructor**

`src/Aspire.Hosting.ServiceSources/Git/GitReferenceFailedException.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// A clone that failed because of the reference repository it borrowed objects from, so the caller
/// can retry without one instead of retrying every failure.
/// </summary>
internal sealed class GitReferenceFailedException(string message, Exception innerException)
    : Exception(message, innerException)
{
#pragma warning disable RS0030
    internal static GitReferenceFailedException For(ServiceTextHandler message, Exception innerException) =>
        new(message.Text, innerException);
#pragma warning restore RS0030
}
```

Append to `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt`:

```
M:Aspire.Hosting.ServiceSources.Git.GitReferenceFailedException.#ctor(System.String,System.Exception);compose the message through GitReferenceFailedException.For($"...", inner) so caller-controlled names cannot reach a reader unescaped
```

- [ ] **Step 4: Add the interface method**

In `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`, after the `Clone` declaration:

```csharp
    /// <summary>
    /// <see cref="Clone"/>, borrowing objects from <paramref name="referenceRepository"/> to save
    /// network transfer. The result must not depend on the reference afterwards.
    /// </summary>
    /// <remarks>
    /// A failure caused by the reference is reported as <see cref="GitReferenceFailedException"/>;
    /// every other failure surfaces exactly as <see cref="Clone"/> would surface it. Defaulted to a
    /// plain clone so test doubles need not implement it.
    /// </remarks>
    void CloneWithReference(
        string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null) =>
        Clone(repositoryUrl, destinationPath, progress);
```

- [ ] **Step 5: Implement it in `GitCliClient`**

In `src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs`, after the `Clone` method add:

```csharp
    public void CloneWithReference(
        string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null)
    {
        string[] progressOption = progress is null ? [] : ["--progress"];

        // -if-able: a reference that vanished since it was chosen clones normally instead of failing.
        // --dissociate: copies borrowed objects in, so a gc or deletion of the reference cannot corrupt this clone.
        RunRemoteCommand(
            ["clone", .. progressOption, $"--reference-if-able={referenceRepository}", "--dissociate",
                "--", repositoryUrl, destinationPath],
            GitUrl.Parse(repositoryUrl).Host,
            progress,
            result => LooksLikeReferenceFailure(result.StandardError, referenceRepository)
                ? GitReferenceFailedException.For($"{Describe(result)}", GitCommandFailedException.For($"{Describe(result)}"))
                : null);
    }

    /// <summary>Whether git blamed the reference repository for a failed clone.</summary>
    internal static bool LooksLikeReferenceFailure(string standardError, string referenceRepository)
    {
        // -if-able reports a missing reference on an "info:" line and carries on, so that line never explains a failure.
        var diagnosis = string.Join('\n', standardError.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("info:", StringComparison.Ordinal)));

        return diagnosis.Contains(referenceRepository, StringComparison.OrdinalIgnoreCase)
            || diagnosis.Contains(referenceRepository.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || diagnosis.Contains("reference repository", StringComparison.OrdinalIgnoreCase)
            || diagnosis.Contains("alternate", StringComparison.OrdinalIgnoreCase)
            || diagnosis.Contains("repack to clean up", StringComparison.OrdinalIgnoreCase);
    }
```

Change `RunRemoteCommand`'s signature from:

```csharp
    private void RunRemoteCommand(
        IReadOnlyList<string> arguments, string? targetHost, IGitProgressSink? progress = null)
```

to:

```csharp
    private void RunRemoteCommand(
        IReadOnlyList<string> arguments,
        string? targetHost,
        IGitProgressSink? progress = null,
        Func<GitCommandResult, Exception?>? classifyFailure = null)
```

and its final line from:

```csharp
        throw GitCommandFailedException.For($"{Describe(result)}");
    }
```

to:

```csharp
        if (classifyFailure?.Invoke(result) is { } classified)
        {
            throw classified;
        }

        throw GitCommandFailedException.For($"{Describe(result)}");
    }
```

Add a `<param name="classifyFailure">` line to its doc comment: `A more specific exception for a non-authentication failure, or <see langword="null"/> for the generic one.`

The test `LooksLikeReferenceFailure` row `object file /home/x/orders/.git/objects/ab/cdef is empty` passes through the path match.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~Git"`
Expected: PASS (new tests and the existing git tests).

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources test/Aspire.Hosting.ServiceSources.Tests/Git
git commit -m "Clone with a dissociated reference repository when one is offered

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Borrow from the home checkout at clone time

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs` (`PrepareRepoRoot`, `CloneIntoPlace`, new `ReferenceCheckout`, new `NewScratchDirectory`, new `TryDeleteScratch`)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs` (`EnsureStarted`, `ResolveRequestedRepoRoot`, `StartCheckoutTask`)
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/ScriptedCloneGitClient.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/CheckoutSeedingTests.cs`

**Interfaces:**
- Consumes: `IGitClient.CloneWithReference`, `GitReferenceFailedException` (Task 4); `WorktreeHome.TryResolve`, `FakeWorktree` (Task 1); `RecordingProgressSink`, `TestRepository` (existing, `Aspire.Hosting.ServiceSources.Tests.Git`).
- Produces:
  - `LocalGitCheckout.PrepareRepoRoot(…, IGitClient gitClient, IGitProgressSink? progress = null, Func<string?>? homeAppHostDirectory = null)` — invoked only on the cold-clone path.
  - `LocalGitCheckout.ReferenceCheckout(string? homeAppHostDirectory, ServiceDefinition definition, IGitClient gitClient)` — `internal static string?`

- [ ] **Step 1: Write the fake**

`test/Aspire.Hosting.ServiceSources.Tests/Worktrees/ScriptedCloneGitClient.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

/// <summary>Records which clones borrowed from a reference, and can make the borrowing fail.</summary>
internal sealed class ScriptedCloneGitClient : IGitClient
{
    private readonly object _gate = new();
    private readonly List<string> _plainClones = [];
    private readonly List<(string Destination, string Reference)> _referenceClones = [];

    /// <summary>What <see cref="GetOriginUrl"/> answers for any checkout, the home one included.</summary>
    public string? HomeOrigin { get; init; }

    public Exception? ReferenceFailure { get; init; }

    public IReadOnlyList<string> PlainClones { get { lock (_gate) { return [.. _plainClones]; } } }

    public IReadOnlyList<(string Destination, string Reference)> ReferenceClones { get { lock (_gate) { return [.. _referenceClones]; } } }

    public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
    {
        lock (_gate)
        {
            _plainClones.Add(destinationPath);
        }

        Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
    }

    public void CloneWithReference(
        string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null)
    {
        lock (_gate)
        {
            _referenceClones.Add((destinationPath, referenceRepository));
        }

        // What a failed attempt can leave behind, so the fallback has debris to avoid.
        Directory.CreateDirectory(destinationPath);

        if (ReferenceFailure is not null)
        {
            throw ReferenceFailure;
        }

        Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
    }

    public void Checkout(string repositoryPath, string reference)
    {
    }

    public void Fetch(string repositoryPath)
    {
    }

    public bool HasUncommittedChanges(string repositoryPath) => false;

    public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

    public string? GetOriginUrl(string repositoryPath) => HomeOrigin;
}
```

- [ ] **Step 2: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Worktrees/CheckoutSeedingTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class CheckoutSeedingTests
{
    private const string Url = "https://example.com/orders.git";

    private static ServiceDefinition Definition(string url = Url) => new()
    {
        Repository = new RepositoryDefinition { Url = url, CheckoutName = "orders" },
        Project = "Orders.csproj",
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
    };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "repository" };

    private static string NewAppHost() => TempDirectories.CreateSubdirectory().FullName;

    private static string HomeCheckout(string homeAppHost)
    {
        var root = LocalGitCheckout.ManagedRepoRoot(homeAppHost, "orders");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static LocalGitCheckout.PreparedCheckout Prepare(
        string appHost, string? home, IGitClient git, IGitProgressSink? progress = null, string url = Url) =>
        LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(url), DevConfig(), repositoryConfig: null, appHost, git, progress, () => home);

    private static IEnumerable<string> Scratch(string appHost) =>
        Directory.EnumerateDirectories(
            Path.GetDirectoryName(LocalGitCheckout.ManagedRepoRoot(appHost, "orders"))!, ".incoming-*");

    [Fact]
    public void MatchingHomeCheckout_IsBorrowedFrom()
    {
        var home = NewAppHost();
        var homeCheckout = HomeCheckout(home);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Prepare(NewAppHost(), home, git);

        Assert.Equal(homeCheckout, Assert.Single(git.ReferenceClones).Reference);
        Assert.Empty(git.PlainClones);
    }

    [Fact]
    public void HomeCheckoutOfAnotherRepository_IsNotBorrowedFrom()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient { HomeOrigin = "https://example.com/billing.git" };

        Prepare(NewAppHost(), home, git);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
    }

    [Fact]
    public void HomeCheckoutWithAGitFile_IsNotBorrowedFrom()
    {
        var home = NewAppHost();
        var root = LocalGitCheckout.ManagedRepoRoot(home, "orders");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Prepare(NewAppHost(), home, git);

        Assert.Empty(git.ReferenceClones);
    }

    [Fact]
    public void NoHome_ClonesPlainly()
    {
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Prepare(NewAppHost(), home: null, git);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
    }

    [Fact]
    public void ReferenceFailure_RetriesPlainlyInAFreshScratchDirectory_AndSaysSo()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var appHost = NewAppHost();
        var sink = new RecordingProgressSink();
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitReferenceFailedException.For($"fatal: cannot repack to clean up", new InvalidOperationException()),
        };

        var prepared = Prepare(appHost, home, git, sink);

        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
        Assert.NotEqual(Assert.Single(git.ReferenceClones).Destination, Assert.Single(git.PlainClones));
        Assert.Empty(Scratch(appHost));
        Assert.Contains(sink.Lines, line => line.Contains("cloning without it", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceFailure_WithNobodyWatching_StillClones()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitReferenceFailedException.For($"fatal: cannot repack to clean up", new InvalidOperationException()),
        };

        var prepared = Prepare(NewAppHost(), home, git, progress: null);

        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
    }

    [Fact]
    public void PlainCloneFailure_IsAttemptedOnce()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitCommandFailedException.For($"fatal: unable to access: Could not resolve host"),
        };

        Assert.Throws<ServiceSourcesConfigurationException>(() => Prepare(NewAppHost(), home, git));

        Assert.Single(git.ReferenceClones);
        Assert.Empty(git.PlainClones);
    }

    [Fact]
    public void AuthenticationFailure_IsAttemptedOnce()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitAuthenticationFailedException.For($"Authentication failed", new InvalidOperationException()),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Prepare(NewAppHost(), home, git));

        Assert.Contains("authentication failed", ex.Message);
        Assert.Empty(git.PlainClones);
    }

    [Fact]
    public void RealGit_ColdCheckoutInAWorktree_IsAnOrdinaryCloneOfTheCatalogUrl()
    {
        var origin = TestRepository.CreateOrigin();
        var url = new Uri(origin.Path).AbsoluteUri;
        var git = new GitCliClient(TestRepository.IsolatedEnvironment());
        var home = NewAppHost();
        var homeCheckout = LocalGitCheckout.ManagedRepoRoot(home, "orders");
        git.Clone(url, homeCheckout);
        TestRepository.At(homeCheckout).Git("branch", "home-only");

        var prepared = Prepare(NewAppHost(), home, git, url: url);

        var checkout = prepared.RepoRoot;
        var remoteRefs = TestRepository.At(checkout).Git("for-each-ref", "--format=%(refname)", "refs/remotes");
        Assert.False(File.Exists(Path.Combine(checkout, ".git", "objects", "info", "alternates")));
        Assert.Equal(url, git.GetOriginUrl(checkout));
        Assert.Contains("refs/remotes/origin/feature/x", remoteRefs);
        Assert.DoesNotContain("home-only", remoteRefs);
    }

    [Fact]
    public void Prefetch_PassesTheResolvedHomeToTheClone()
    {
        var fake = FakeWorktree.Siblings();
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, "servicesources.yaml"),
            $"services:\n  orders:\n    repository: {Url}\n    project: Service.csproj\n");
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName),
            """{ "services": { "orders": { "source": "repository" } } }""");
        var homeCheckout = HomeCheckout(fake.MainAppHost);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };
        var builder = fake.CreateWorktreeBuilder();
        builder.SetCheckoutTiming(CheckoutTiming.Eager);
        var definition = new ServiceMetadata { Repository = Url, Project = "Service.csproj" }
            .ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

        LocalCheckoutPrefetch.For(builder, git)
            .GetRepoRoot("orders", definition, DevConfig(), repositoryConfig: null, fake.WorktreeAppHost, git);

        Assert.Equal(homeCheckout, Assert.Single(git.ReferenceClones).Reference);
    }

    [Fact]
    public void Prefetch_InASeededWorktreeWithAWarmCheckout_NeverAsksGitForHome()
    {
        var fake = FakeWorktree.Siblings();
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, "servicesources.yaml"),
            $"services:\n  orders:\n    repository: {Url}\n    project: Service.csproj\n");
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName),
            """{ "services": { "orders": { "source": "repository" } } }""");
        ToolDirectory.Ensure(fake.WorktreeAppHost);
        File.WriteAllText(Path.Combine(fake.WorktreeAppHost, ".servicesources", "worktree-seed.json"), "{}");
        Directory.CreateDirectory(Path.Combine(LocalGitCheckout.ManagedRepoRoot(fake.WorktreeAppHost, "orders"), ".git"));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };
        var builder = fake.CreateWorktreeBuilder();
        builder.SetCheckoutTiming(CheckoutTiming.Eager);
        var definition = new ServiceMetadata { Repository = Url, Project = "Service.csproj" }
            .ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

        LocalCheckoutPrefetch.For(builder, git)
            .GetRepoRoot("orders", definition, DevConfig(), repositoryConfig: null, fake.WorktreeAppHost, git);

        Assert.Equal(0, fake.Calls);
        Assert.Empty(git.ReferenceClones);
        Assert.Empty(git.PlainClones);
    }
}
```

The warm-checkout test writes the marker by literal path rather than through `WorktreeSeed.MarkerPath`, so it does not depend on Task 2's internals beyond the file name.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~CheckoutSeedingTests"`
Expected: build FAILS — `PrepareRepoRoot` has no `homeAppHostDirectory` parameter.

- [ ] **Step 4: Thread the home through `PrepareRepoRoot`**

In `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs`:

Add to `PrepareRepoRoot`'s doc comment:

```csharp
    /// <param name="homeAppHostDirectory">
    /// Finds the main worktree's copy of this AppHost directory when this one is a linked worktree,
    /// whose checkout of the same repository a cold clone may borrow objects from. Called only for a
    /// cold clone, because finding it can spawn git.
    /// </param>
```

Change its signature's last parameter from `IGitProgressSink? progress = null)` to:

```csharp
        IGitProgressSink? progress = null,
        Func<string?>? homeAppHostDirectory = null)
```

This is past the warm-checkout early return and the `HasRepositoryToClone` check, so only a cold clone resolves home. Change:

```csharp
        // A clone that loses the race to a concurrent AppHost leaves us using *their*
        // checkout, not one we just made, so it gets the same treatment as a checkout found
        // there on a later run: theirs may be a clone of another repository, and may hold
        // work in flight that a checkout would discard.
        if (CloneIntoPlace(label, definition, checkoutsRoot, repoRoot, gitClient, progress))
```

to:

```csharp
        var referenceCheckout = ReferenceCheckout(homeAppHostDirectory?.Invoke(), definition, gitClient);

        // A clone that loses the race to a concurrent AppHost leaves us using *their*
        // checkout, not one we just made, so it gets the same treatment as a checkout found
        // there on a later run: theirs may be a clone of another repository, and may hold
        // work in flight that a checkout would discard.
        if (CloneIntoPlace(label, definition, checkoutsRoot, repoRoot, referenceCheckout, gitClient, progress))
```

Add, next to `RepositoryUrlsMatch`:

```csharp
    /// <summary>
    /// The home worktree's checkout of this repository, when it is safe to borrow objects from.
    /// </summary>
    /// <remarks>
    /// Only a '.git' directory: git refuses a linked-worktree reference. The origin check keeps an
    /// unrelated repository at the same name from being borrowed from.
    /// </remarks>
    internal static string? ReferenceCheckout(
        string? homeAppHostDirectory, ServiceDefinition definition, IGitClient gitClient)
    {
        if (homeAppHostDirectory is null)
        {
            return null;
        }

        var candidate = ManagedRepoRoot(homeAppHostDirectory, definition.Repository.CheckoutName);

        return Directory.Exists(Path.Combine(candidate, ".git"))
            && gitClient.GetOriginUrl(candidate) is { } origin
            && RepositoryUrlsMatch(origin, definition.Repository.Url)
                ? candidate
                : null;
    }
```

- [ ] **Step 5: Borrow, and fall back narrowly, in `CloneIntoPlace`**

Change `CloneIntoPlace`'s parameter list from:

```csharp
        string repoRoot,
        IGitClient gitClient,
        IGitProgressSink? progress)
```

to:

```csharp
        string repoRoot,
        string? referenceCheckout,
        IGitClient gitClient,
        IGitProgressSink? progress)
```

Replace:

```csharp
        // Unique per attempt: two builders resolving the same checkout concurrently (xUnit does
        // exactly that) must not clone into a shared scratch directory.
        var scratch = Path.Combine(
            checkoutsRoot, $".incoming-{definition.Repository.CheckoutName}-{Guid.NewGuid():N}");

        try
        {
            try
            {
                gitClient.Clone(definition.Repository.Url, scratch, progress);
            }
            catch (GitAuthenticationFailedException ex)
```

with:

```csharp
        var scratch = NewScratchDirectory(checkoutsRoot, definition);
        string? abandonedScratch = null;

        try
        {
            try
            {
                if (referenceCheckout is null)
                {
                    gitClient.Clone(definition.Repository.Url, scratch, progress);
                }
                else
                {
                    try
                    {
                        gitClient.CloneWithReference(definition.Repository.Url, scratch, referenceCheckout, progress);
                    }
                    catch (GitReferenceFailedException ex)
                    {
                        // The progress sink rather than ServiceSourcesWarnings: a deferred clone runs after that buffer is flushed.
                        progress?.Report(Raw.Compose(
                            $"Could not borrow objects from '{Raw.Escaped(referenceCheckout)}' ({Raw.Cause(ex)}); cloning without it.").ToString());

                        // A fresh directory, since the failed attempt may have left files behind.
                        abandonedScratch = scratch;
                        scratch = NewScratchDirectory(checkoutsRoot, definition);
                        gitClient.Clone(definition.Repository.Url, scratch, progress);
                    }
                }
            }
            catch (GitAuthenticationFailedException ex)
```

Replace the whole `finally` block of that outer `try`:

```csharp
        finally
        {
            if (Directory.Exists(scratch))
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort, deliberately not fatal. A leaked scratch directory costs disk
                    // inside a gitignored, tool-managed tree and can never block a later clone:
                    // its name is unique per attempt and the destination name is untouched.
                    // SweepAbandonedScratchDirectories collects it on a later run.
                }
            }
        }
    }
```

with:

```csharp
        finally
        {
            TryDeleteScratch(scratch);

            if (abandonedScratch is not null)
            {
                TryDeleteScratch(abandonedScratch);
            }
        }
    }

    /// <remarks>
    /// Unique per attempt: two builders resolving the same checkout concurrently (xUnit does
    /// exactly that) must not clone into a shared scratch directory.
    /// </remarks>
    private static string NewScratchDirectory(string checkoutsRoot, ServiceDefinition definition) =>
        Path.Combine(checkoutsRoot, $".incoming-{definition.Repository.CheckoutName}-{Guid.NewGuid():N}");

    private static void TryDeleteScratch(string scratch)
    {
        if (!Directory.Exists(scratch))
        {
            return;
        }

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, deliberately not fatal. A leaked scratch directory costs disk
            // inside a gitignored, tool-managed tree and can never block a later clone:
            // its name is unique per attempt and the destination name is untouched.
            // SweepAbandonedScratchDirectories collects it on a later run.
        }
    }
```

- [ ] **Step 6: Hand the prefetch's clones a lazy home**

In `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs`:

Add `using Aspire.Hosting.ServiceSources.Worktrees;`.

Add a field next to `_isRunMode`:

```csharp
    // Lazy because the prefetch exists on every run, and finding home spawns git in a linked worktree.
    // WorktreeHome caches per builder, so concurrent clones share one resolution.
    private Func<string?>? _homeAppHostDirectory;
```

In `EnsureStarted`, change:

```csharp
            _isRunMode = builder.ExecutionContext.IsRunMode;
```

to:

```csharp
            _isRunMode = builder.ExecutionContext.IsRunMode;
            _homeAppHostDirectory = () => WorktreeHome.TryResolve(builder)?.AppHostDirectory;
```

Capturing `builder` is safe here: the prefetch is the value in a per-builder `ConditionalWeakTable`, which does not keep its key alive through the value.

In `ResolveRequestedRepoRoot`, change:

```csharp
                prepared = LocalGitCheckout.PrepareRepoRoot(
                    serviceName, definition, config, repositoryConfig, appHostDirectory, gitClient, progress);
```

to:

```csharp
                prepared = LocalGitCheckout.PrepareRepoRoot(
                    serviceName, definition, config, repositoryConfig, appHostDirectory, gitClient, progress,
                    _homeAppHostDirectory);
```

In `StartCheckoutTask`, change:

```csharp
                var prepared = LocalGitCheckout.PrepareRepoRoot(
                    serviceName, definition, config, repositoryConfig, appHostDirectory, gitClient, progress);
```

to:

```csharp
                var prepared = LocalGitCheckout.PrepareRepoRoot(
                    serviceName, definition, config, repositoryConfig, appHostDirectory, gitClient, progress,
                    _homeAppHostDirectory);
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~CheckoutSeedingTests|FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~LocalCheckoutPrefetchTests|FullyQualifiedName~LocalGitCheckoutTests"`
Expected: PASS.

- [ ] **Step 8: Run all three test projects on the cheap leg**

Run: `dotnet test -f net10.0`
Expected: PASS (the Java and JavaScript fakes compile unchanged).

- [ ] **Step 9: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources test/Aspire.Hosting.ServiceSources.Tests/Worktrees
git commit -m "Borrow objects from the main worktree's checkout for a cold clone in a linked worktree

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Document it and run the full matrix

**Files:**
- Modify: `docs/sources/repository.md` (append a section at the end of the file)
- Modify: `CHANGELOG.md` (`## [Unreleased]` → `### Added`, first bullet)

**Interfaces:**
- Consumes: the behaviour from Tasks 1–5.
- Produces: nothing code-facing.

- [ ] **Step 1: Add the docs section**

Append to `docs/sources/repository.md`:

```markdown
## Working in git worktrees

`git worktree add` checks out only tracked files, so an AppHost run from a linked worktree of its
own repository would start without the git-ignored state the main worktree has built up. On its
first run, ServiceSources seeds it once from the main worktree's copy of the same AppHost directory:

- `servicesources.local.json` and `.servicesources/selection.json` are copied if the worktree has
  none. An existing file is never overwritten.
- A cold `"repository"` checkout still clones from the catalog's URL, but borrows objects from the
  main worktree's checkout of the same repository, so only what that checkout lacks is downloaded.
  The result is an ordinary, independent clone.

Seeding happens once, and `.servicesources/worktree-seed.json` records it. A copied file you delete
stays deleted; delete the marker to seed again. `prepare` steps are not seeded, so they run on the
worktree's first use of each checkout.

Afterwards the worktree's files and checkouts are its own: a checkout can sit on a different ref,
carry different edits and build independently of the main worktree's. To share the main worktree's
checkout of a service on purpose, point the service at it with the [`"path"` source](path.md) in
the worktree's `servicesources.local.json`.

If a copied `path.path` or `repository.path` resolves into the main worktree, or to a different
directory than it did there, a startup notice names the entry. Edit the worktree's
`servicesources.local.json` if that is not what you want.

Detecting a linked worktree needs git 2.31 or newer. With an older git, nothing is seeded.
```

- [ ] **Step 2: Add the changelog entry**

In `CHANGELOG.md`, directly under the `### Added` heading inside `## [Unreleased]`, insert:

```markdown
- **An AppHost run from a linked git worktree starts from the main worktree's state.** Its first run
  copies `servicesources.local.json` and the saved source picks from the main worktree's AppHost, and
  a cold `"repository"` checkout borrows objects from the main worktree's checkout of the same
  repository instead of downloading everything. Seeding happens once; afterwards the worktree's
  files and checkouts are independent. A copied `path` override that resolves differently from the
  worktree is reported at startup.
```

Other `[Unreleased]` bullets end with an issue link such as `([#415])`. If an issue has been filed for this by then, append its link the same way and add the matching link reference at the bottom of the file.

- [ ] **Step 3: Build the docs if the toolchain is available**

Run: `python -m mkdocs build --strict`
Expected: success. If `mkdocs` is not installed, skip this step and say so in the PR description.

- [ ] **Step 4: Rebase onto the latest main**

```bash
git fetch origin
git rebase origin/main
```

Expected: clean rebase. Resolve conflicts if any, then rerun `dotnet test -f net10.0`.

- [ ] **Step 5: Run the full matrix once**

Run: `dotnet test`
Expected: PASS on net8.0, net9.0 and net10.0 for all three test projects.

- [ ] **Step 6: Commit**

```bash
git add docs/sources/repository.md CHANGELOG.md
git commit -m "Document seeding a linked worktree from the main worktree

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Spec coverage check

| Spec item | Task |
| --- | --- |
| Finding home: pre-check, rev-parse, worktree list, bare, submodule / separate-git-dir, per-builder cache | 1 |
| `IWorktreeProbe`, `UseProbe` before first entry point, tests isolated by pre-check | 1, 2 (audit step) |
| Seeding config files once, marker semantics, create-new, regular files only, `ToolDirectory.Ensure` | 2 |
| Never fails the run; notice and no marker on IO failure; retried next builder | 2 |
| Reporting through `ServiceSourcesWarnings` | 2, 3 |
| Path override table (four rows, row 3 narrowed per deviation 4), `repository.path` alias, inspected once | 3 |
| Marker keeps later runs free of git calls (home resolved lazily, cold clones only) | 5 |
| `CloneWithReference` default method, `--reference-if-able`, `--dissociate` | 4 |
| `GitReferenceFailedException` classification by stderr | 4 |
| Borrow only from a `.git` directory with a matching origin | 5 |
| Narrow fallback, fresh scratch directory, both cleaned up, line to progress sink | 5 |
| Auth and network failures attempted once | 5 |
| Grouped services share via `CheckoutName` | 5 (`ReferenceCheckout` keys on `CheckoutName`) |
| Prepare markers not seeded | 2 (only two files copied), 6 (docs) |
| Docs: "Working in git worktrees" | 6 |
