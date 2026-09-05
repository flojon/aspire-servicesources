using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

/// <summary>
/// The step's decision, its output and its failure — the code both the eager and the deferred path
/// call, exercised without either of them and without spawning a process.
/// </summary>
public class CheckoutPreparationTests
{
    private const string ServiceName = "routing";

    /// <summary>
    /// A runner that records what it was asked to run, and answers however the test says.
    /// </summary>
    private sealed class FakeRunner : IPrepareCommandRunner
    {
        public List<(string WorkingDirectory, string[] Command)> Runs { get; } = [];

        public int ExitCode { get; set; }

        public string[] Output { get; set; } = [];

        public PrepareLaunchException? LaunchException { get; set; }

        /// <summary>Runs while the command is notionally still running.</summary>
        public Action? DuringRun { get; set; }

        public int Run(
            string workingDirectory,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            // A cancelled token means the command should not start, which is what a real runner
            // answers by never reaching Process.Start.
            cancellationToken.ThrowIfCancellationRequested();

            Runs.Add((workingDirectory, [.. command]));

            if (LaunchException is not null)
            {
                throw LaunchException;
            }

            foreach (var line in Output)
            {
                onLine(line);
            }

            DuringRun?.Invoke();

            return ExitCode;
        }
    }

    /// <summary>
    /// A git that knows only which commit the checkout is on, which is all this step asks of one.
    /// </summary>
    private sealed class FakeGitClient : IGitClient
    {
        public string? HeadCommitSha { get; set; } = "1111111111111111111111111111111111111111";

        public string? GetHeadCommitSha(string repositoryPath) => HeadCommitSha;

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
        }

        public void Checkout(string repositoryPath, string reference)
        {
        }

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

        public string? GetOriginUrl(string repositoryPath) => null;
    }

    private sealed class RecordingSink : IPrepareOutputSink
    {
        public List<string> Lines { get; } = [];

        public void Report(string line) => Lines.Add(line);
    }

    /// <summary>
    /// A managed checkout on disk: a directory with a <c>.git</c> in it, which is where its marker
    /// goes.
    /// </summary>
    private static string CreateManagedCheckout()
    {
        var repoRoot = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        return repoRoot;
    }

    private static PrepareStep Step(string mode = "oncePerCommit", params string[] command) =>
        PrepareStep.Create(
            ServiceName,
            command.Length == 0 ? ["./prepare.sh"] : command,
            PrepareModes.Parse(ServiceName, mode, "prepare.mode"),
            "prepare");

    private sealed record Fixture(
        string RepoRoot, string AppHostDirectory, FakeRunner Runner, FakeGitClient Git, RecordingSink Sink)
    {
        public string MarkerPath =>
            Path.Combine(RepoRoot, ".git", "servicesources-prepare.json");
    }

    private static Fixture NewFixture() =>
        new(CreateManagedCheckout(), Directory.CreateTempSubdirectory().FullName, new(), new(), new());

    private static void Run(
        Fixture fixture,
        PrepareStep step,
        bool managedCheckout = true,
        CancellationToken cancellationToken = default) =>
        CheckoutPreparation.Run(
            ServiceName, step, fixture.RepoRoot, fixture.AppHostDirectory, managedCheckout,
            fixture.Git, fixture.Runner, fixture.Sink, cancellationToken);

    // ---- the marker ---------------------------------------------------------

    [Fact]
    public void NoMarker_Runs()
    {
        var fixture = NewFixture();

        Run(fixture, Step());

        var run = Assert.Single(fixture.Runner.Runs);
        Assert.Equal(fixture.RepoRoot, run.WorkingDirectory);
        Assert.Equal<string[]>(["./prepare.sh"], run.Command);
        Assert.True(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public void MatchingMarker_Skips()
    {
        var fixture = NewFixture();
        Run(fixture, Step());

        Run(fixture, Step());

        Assert.Single(fixture.Runner.Runs);
    }

    [Fact]
    public void ChangedCommand_RerunsUnderEveryGuardedMode()
    {
        foreach (var mode in new[] { "oncePerCommit", "once" })
        {
            var fixture = NewFixture();
            Run(fixture, Step(mode, "./prepare.sh"));

            Run(fixture, Step(mode, "./prepare.sh", "--full"));

            Assert.Equal(2, fixture.Runner.Runs.Count);
        }
    }

    [Fact]
    public void MovedCommit_RerunsUnderOncePerCommit()
    {
        var fixture = NewFixture();
        Run(fixture, Step());

        fixture.Git.HeadCommitSha = "2222222222222222222222222222222222222222";
        Run(fixture, Step());

        Assert.Equal(2, fixture.Runner.Runs.Count);
    }

    /// <remarks>
    /// The whole difference between the two guarded modes: <c>once</c> records the commit and never
    /// consults it, which is what a bootstrap pinned by the catalog rather than by the repository
    /// wants — a developer committing a one-line README fix should not pay a four-minute import.
    /// </remarks>
    [Fact]
    public void MovedCommit_DoesNotRerunUnderOnce()
    {
        var fixture = NewFixture();
        Run(fixture, Step("once"));

        fixture.Git.HeadCommitSha = "2222222222222222222222222222222222222222";
        Run(fixture, Step("once"));

        Assert.Single(fixture.Runner.Runs);
    }

    /// <summary>
    /// The executor's own refusal to run a <c>never</c> step, which should never reach it.
    /// </summary>
    /// <remarks>
    /// Defence in depth rather than a path the plan takes: <c>PrepareMode.Never</c> means "no step",
    /// so a plan resolves it to one. It is asserted because the cost of a plan being wrong about
    /// that is a command running in a directory the developer manages — and one already was.
    /// </remarks>
    [Fact]
    public void Never_RunsNothingAndWritesNothing()
    {
        var fixture = NewFixture();

        Run(fixture, Step("never"));

        Assert.Empty(fixture.Runner.Runs);
        Assert.Empty(fixture.Sink.Lines);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public void Always_IgnoresAMatchingMarkerAndWritesNone()
    {
        var fixture = NewFixture();

        Run(fixture, Step("always"));
        Run(fixture, Step("always"));

        Assert.Equal(2, fixture.Runner.Runs.Count);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    /// <remarks>
    /// A step that fails halfway is re-run from the beginning against a checkout that already holds
    /// whatever the first attempt produced. That is why a prepare command has to be safe to re-run
    /// under every mode, not only under <c>always</c>.
    /// </remarks>
    [Fact]
    public void AFailedStep_WritesNoMarkerAndRunsAgain()
    {
        var fixture = NewFixture();
        fixture.Runner.ExitCode = 1;

        Assert.Throws<ServiceSourcesConfigurationException>(() => Run(fixture, Step()));
        Assert.False(File.Exists(fixture.MarkerPath));

        fixture.Runner.ExitCode = 0;
        Run(fixture, Step());

        Assert.Equal(2, fixture.Runner.Runs.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "commandHash": "" }""")]
    public void AnUnreadableMarker_RunsRatherThanThrows(string content)
    {
        var fixture = NewFixture();
        File.WriteAllText(fixture.MarkerPath, content);

        Run(fixture, Step());

        Assert.Single(fixture.Runner.Runs);
    }

    /// <remarks>
    /// "Cannot verify" must not be allowed to mean "assume done".
    /// </remarks>
    [Fact]
    public void NoResolvableCommit_RunsEveryTimeUnderOncePerCommit()
    {
        var fixture = NewFixture();
        fixture.Git.HeadCommitSha = null;

        Run(fixture, Step());
        Run(fixture, Step());

        Assert.Equal(2, fixture.Runner.Runs.Count);
    }

    /// <remarks>
    /// The two modes fork rather than degrading toward each other where the commit is unknowable —
    /// which for a <c>path</c> checkout pointed at a plain unpacked directory is routine.
    /// </remarks>
    [Fact]
    public void NoResolvableCommit_RunsOnceUnderOnce()
    {
        var fixture = NewFixture();
        fixture.Git.HeadCommitSha = null;

        Run(fixture, Step("once"));
        Run(fixture, Step("once"));

        Assert.Single(fixture.Runner.Runs);
    }

    // ---- would it run at all ------------------------------------------------

    /// <summary>
    /// The question the publish-mode skip asks before saying anything.
    /// </summary>
    /// <remarks>
    /// Reporting the skip unconditionally named a step that was not going to run anyway — every
    /// start after the first — and told the developer to materialize a checkout they already had.
    /// Asked through the same decision <see cref="CheckoutPreparation.Run"/> makes, so the two
    /// cannot drift apart about what "would run" means.
    /// </remarks>
    private static bool WouldRun(Fixture fixture, PrepareStep step, bool managedCheckout = true) =>
        CheckoutPreparation.WouldRun(
            ServiceName, step, fixture.RepoRoot, fixture.AppHostDirectory, managedCheckout, fixture.Git);

    [Fact]
    public void WouldRun_NoMarker_IsTrue() => Assert.True(WouldRun(NewFixture(), Step()));

    [Fact]
    public void WouldRun_AMarkerThatSatisfiesIt_IsFalse()
    {
        var fixture = NewFixture();
        Run(fixture, Step());

        Assert.False(WouldRun(fixture, Step()));
    }

    [Fact]
    public void WouldRun_Always_IsTrueEvenWithAMarker()
    {
        var fixture = NewFixture();
        Run(fixture, Step());

        Assert.True(WouldRun(fixture, Step("always")));
    }

    [Fact]
    public void WouldRun_Never_IsFalse() => Assert.False(WouldRun(NewFixture(), Step("never")));

    /// <remarks>
    /// Asking must not be the thing that runs it, since the caller is the path that has decided not
    /// to.
    /// </remarks>
    [Fact]
    public void WouldRun_RunsNothingAndRecordsNothing()
    {
        var fixture = NewFixture();

        WouldRun(fixture, Step());

        Assert.Empty(fixture.Runner.Runs);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    // ---- what is reported ---------------------------------------------------

    [Fact]
    public void EveryDecisionToRun_ReportsTheReasonAndTheCommand()
    {
        var fixture = NewFixture();

        Run(fixture, Step());

        var announcement = fixture.Sink.Lines[0];
        Assert.Contains("[prepare routing]", announcement);
        Assert.Contains("no completed prepare step is recorded", announcement);
        Assert.Contains("./prepare.sh", announcement);
    }

    [Fact]
    public void AChangedCommand_SaysSo()
    {
        var fixture = NewFixture();
        Run(fixture, Step("oncePerCommit", "./prepare.sh"));
        fixture.Sink.Lines.Clear();

        Run(fixture, Step("oncePerCommit", "make", "bootstrap"));

        Assert.Contains("prepare command has changed", fixture.Sink.Lines[0]);
    }

    [Fact]
    public void AMovedCommit_SaysSo()
    {
        var fixture = NewFixture();
        Run(fixture, Step());
        fixture.Sink.Lines.Clear();

        fixture.Git.HeadCommitSha = "2222222222222222222222222222222222222222";
        Run(fixture, Step());

        Assert.Contains("moved to another commit", fixture.Sink.Lines[0]);
    }

    [Fact]
    public void AnUnresolvableCommit_IsReportedAsAFactAboutThisStart()
    {
        var fixture = NewFixture();
        fixture.Git.HeadCommitSha = null;
        Run(fixture, Step());
        fixture.Sink.Lines.Clear();

        Run(fixture, Step());

        Assert.Contains("could not be determined", fixture.Sink.Lines[0]);
    }

    [Fact]
    public void Always_SaysThatIsWhy()
    {
        var fixture = NewFixture();

        Run(fixture, Step("always"));

        Assert.Contains("runs on every start", fixture.Sink.Lines[0]);
    }

    /// <remarks>
    /// Skipping is the ordinary case — every start after the first — and the marker already records
    /// it, so a line saying nothing happened would be noise on every start of every service.
    /// </remarks>
    [Fact]
    public void ASkip_ReportsNothing()
    {
        var fixture = NewFixture();
        Run(fixture, Step());
        fixture.Sink.Lines.Clear();

        Run(fixture, Step());

        Assert.Empty(fixture.Sink.Lines);
    }

    [Fact]
    public void TheCommandsOutput_IsReportedLineByLineTagged()
    {
        var fixture = NewFixture();
        fixture.Runner.Output = ["Downloading graphhopper-web-11.0.jar...", "Importing sweden-latest.osm.pbf"];

        Run(fixture, Step());

        Assert.Equal(
            [
                "[prepare routing] Downloading graphhopper-web-11.0.jar...",
                "[prepare routing] Importing sweden-latest.osm.pbf",
            ],
            fixture.Sink.Lines.Skip(1));
    }

    /// <remarks>
    /// A process-backed runner reads the command's two streams on separate threads, so the callback
    /// is re-entered concurrently — which the output tail, a plain queue, cannot survive unguarded.
    /// Reproduced with a runner that reports from two threads at once, as the real one does.
    /// </remarks>
    [Fact]
    public void OutputReportedFromTwoThreads_IsNeitherLostNorCorrupting()
    {
        var fixture = NewFixture();
        var runner = new ConcurrentlyReportingRunner();

        CheckoutPreparation.Run(
            ServiceName, Step(), fixture.RepoRoot, fixture.AppHostDirectory, managedCheckout: true,
            fixture.Git, runner, fixture.Sink);

        // The announcement plus every line both streams wrote, and nothing torn.
        Assert.Equal(
            1 + (ConcurrentlyReportingRunner.PerStream * 2),
            fixture.Sink.Lines.Count);
        Assert.All(fixture.Sink.Lines, line => Assert.StartsWith("[prepare routing]", line));
    }

    private sealed class ConcurrentlyReportingRunner : IPrepareCommandRunner
    {
        public const int PerStream = 500;

        public int Run(
            string workingDirectory,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            var streams = Enumerable.Range(0, 2).Select(stream => Task.Run(() =>
            {
                for (var i = 0; i < PerStream; i++)
                {
                    onLine($"stream {stream} line {i}");
                }
            }));

            Task.WaitAll([.. streams]);

            return 0;
        }
    }

    /// <summary>
    /// Cancellation travels out as itself, and records nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately not wrapped in a configuration exception: the deferred start task already treats
    /// <see cref="OperationCanceledException"/> as the shutdown it is — nothing left to start and
    /// nobody to tell — where a wrapped one would be reported as this service having failed. And no
    /// marker, because an interrupted step did not complete.
    /// </remarks>
    [Fact]
    public void Cancellation_TravelsOutUnwrappedAndRecordsNothing()
    {
        var fixture = NewFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => Run(fixture, Step(), cancellationToken: cancelled.Token));

        Assert.False(File.Exists(fixture.MarkerPath));
    }

    // ---- failure ------------------------------------------------------------

    [Fact]
    public void ANonZeroExit_NamesServiceCommandAndExitCode()
    {
        var fixture = NewFixture();
        fixture.Runner.ExitCode = 17;
        fixture.Runner.Output = ["fetching...", "boom"];

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Run(fixture, Step("oncePerCommit", "./prepare.sh", "--full")));

        Assert.Contains($"'{ServiceName}'", ex.Message);
        Assert.Contains("./prepare.sh --full", ex.Message);
        Assert.Contains("code 17", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void ANonZeroExitWithNoOutput_SaysSoRatherThanQuotingNothing()
    {
        var fixture = NewFixture();
        fixture.Runner.ExitCode = 1;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Run(fixture, Step()));

        Assert.Contains("wrote no output", ex.Message);
    }

    /// <summary>
    /// The output tail a failure quotes is snapshotted under the same lock the callback enqueues
    /// through, so a stream reader still delivering a line is waited for rather than raced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reachable because the drain is bounded: a stream held open by something the command started
    /// outlives the command, so the runner returns while a reader is still appending. Reading the
    /// queue unguarded then either throws over the top of a report about someone else's failed
    /// command, or quietly quotes a tail missing the line that was arriving.
    /// </para>
    /// <para>
    /// Nothing here exists only for the test. The sink is called from inside that lock in production
    /// — it is how a line reaches the developer before it reaches the tail — so a sink that stops
    /// there is a reader caught mid-append, and a runner that returns while it is stopped is the
    /// bounded drain returning early. Both assertions then say the same thing from two directions:
    /// unguarded, the snapshot is already finished by the time the reader is let go, and the tail it
    /// took has no line in it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFailureWhileALineIsStillArriving_WaitsForItRatherThanQuotingAroundIt()
    {
        const string StillArriving = "the line the bounded drain did not wait for";

        var fixture = NewFixture();
        using var insideTheCallback = new ManualResetEventSlim();
        using var releaseTheCallback = new ManualResetEventSlim();

        var sink = new StoppingSink(StillArriving, insideTheCallback, releaseTheCallback);
        var runner = new ExitsWhileStillReportingRunner(StillArriving, insideTheCallback);

        var run = Task.Run(() => CheckoutPreparation.Run(
            ServiceName, Step(), fixture.RepoRoot, fixture.AppHostDirectory, managedCheckout: true,
            fixture.Git, runner, sink));

        Assert.True(insideTheCallback.Wait(Rendezvous), "the reader never reached the sink");

        // The reader is inside the callback holding the lock, with its line reported but not yet
        // enqueued, and the command has already exited non-zero — so the failure message is being
        // built right now. Guarded, it cannot be: the snapshot waits for the append it interrupted.
        await Task.WhenAny(run, Task.Delay(Unguarded));

        Assert.False(
            run.IsCompleted,
            "the failure message was built while a reader was still appending to the output tail");

        releaseTheCallback.Set();

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(() => run);

        Assert.Contains(StillArriving, ex.Message);

        await runner.Reporting.WaitAsync(Rendezvous);
    }

    /// <summary>
    /// How long a hand-off in the test above may take before the test gives up rather than hanging.
    /// </summary>
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the snapshot is given to prove it is not waiting.
    /// </summary>
    /// <remarks>
    /// A bound rather than a sleep, and it can only ever err into a pass. The guarded snapshot is
    /// blocked on a lock that nothing releases until the assertion has run, so no amount of load
    /// makes it finish early; the unguarded one is a queue copy and a string concatenation past the
    /// runner returning, and with the lock taken out it fails this in tens of milliseconds.
    /// </remarks>
    private static readonly TimeSpan Unguarded = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A runner whose command exits while one of its streams is still delivering a line — what the
    /// bounded drain leaves behind, without a process to hold a pipe open.
    /// </summary>
    private sealed class ExitsWhileStillReportingRunner(string line, ManualResetEventSlim reported)
        : IPrepareCommandRunner
    {
        /// <summary>The reader, still going after <see cref="Run"/> has returned.</summary>
        public Task Reporting { get; private set; } = Task.CompletedTask;

        public int Run(
            string workingDirectory,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            Reporting = Task.Run(() => onLine(line), CancellationToken.None);

            // Returning with the reader still inside the callback, which is the whole of what a
            // drain that timed out hands back to the caller.
            Assert.True(reported.Wait(Rendezvous), "the reader never reached the sink");

            return 1;
        }
    }

    /// <summary>
    /// A sink that stops on one particular line, and so stops inside the lock the callback reporting
    /// it is holding.
    /// </summary>
    private sealed class StoppingSink(
        string line, ManualResetEventSlim reached, ManualResetEventSlim release) : IPrepareOutputSink
    {
        public void Report(string reported)
        {
            if (!reported.EndsWith(line, StringComparison.Ordinal))
            {
                return;
            }

            reached.Set();

            Assert.True(release.Wait(Rendezvous), "the test never released the reader");
        }
    }

    [Fact]
    public void ACommandThatCannotBeLaunched_IsDistinguishedFromOneThatFailed()
    {
        var fixture = NewFixture();
        fixture.Runner.LaunchException = new PrepareLaunchException("'./prepare.sh' could not be started: no such file");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Run(fixture, Step()));

        Assert.Contains("could not be started", ex.Message);
        Assert.DoesNotContain("exited with code", ex.Message);
    }

    [Fact]
    public void OnWindowsWithNoVariant_ALaunchFailureNamesThatAsTheLikelyCause()
    {
        var fixture = NewFixture();
        fixture.Runner.LaunchException = new PrepareLaunchException("not executable");

        var windowsStep = PrepareStep.Create(
            ServiceName, ["./prepare.sh"], PrepareMode.OncePerCommit, "prepare", windowsWithoutVariant: true);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Run(fixture, windowsStep));

        Assert.Contains("declares no 'windowsCommand'", ex.Message);
        Assert.Contains("prepare.ps1", ex.Message);
    }

    // ---- a path checkout's marker ------------------------------------------

    private static string PathMarkerPath(string appHostDirectory) =>
        Path.Combine(appHostDirectory, ".servicesources", "prepare", $"{ServiceName}.json");

    /// <summary>
    /// A step that succeeded is not undone by a marker that cannot be written.
    /// </summary>
    /// <remarks>
    /// The recovery this asserts is the whole reason the write is best-effort: the command has
    /// already done its work, so failing the service now would turn "the completion could not be
    /// recorded" into "the service does not start". A <c>path</c> checkout is the case that reaches
    /// it, because its marker goes in the AppHost's own tree — and acquiring that tree sat outside
    /// the recovery, so a read-only AppHost directory did exactly what the recovery forbids. What it
    /// costs instead is a step that runs again next start, which every mode's command has to
    /// tolerate anyway.
    /// </remarks>
    [Fact]
    public void AMarkerThatCannotBeWritten_DoesNotFailTheStep()
    {
        if (OperatingSystem.IsWindows())
        {
            // The mode bits below are POSIX, and what is under test is the recovery, not the
            // platform.
            return;
        }

        var fixture = NewFixture();
        var readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;

        File.SetUnixFileMode(fixture.AppHostDirectory, readOnly);

        try
        {
            Run(fixture, Step(), managedCheckout: false);
        }
        finally
        {
            File.SetUnixFileMode(fixture.AppHostDirectory, readOnly | UnixFileMode.UserWrite);
        }

        // It ran, and nothing threw: the tool directory could not be created, so no marker exists.
        Assert.Single(fixture.Runner.Runs);
        Assert.False(Directory.Exists(Path.Combine(fixture.AppHostDirectory, ".servicesources")));
    }

    [Fact]
    public void APathCheckout_KeepsItsMarkerInTheToolsOwnTree()
    {
        var fixture = NewFixture();

        Run(fixture, Step(), managedCheckout: false);

        Assert.True(File.Exists(PathMarkerPath(fixture.AppHostDirectory)));
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    /// <remarks>
    /// Without it the marker becomes the one tool-managed file a developer would see listed as
    /// untracked in their own repository.
    /// </remarks>
    [Fact]
    public void APathCheckout_CreatesTheToolDirectoryAndItsGitignore()
    {
        var fixture = NewFixture();

        Run(fixture, Step(), managedCheckout: false);

        var ignore = Path.Combine(fixture.AppHostDirectory, ".servicesources", ".gitignore");
        Assert.True(File.Exists(ignore));
        Assert.Contains("*", File.ReadAllText(ignore));
    }

    [Fact]
    public void APathCheckoutRepointedElsewhere_Reruns()
    {
        var fixture = NewFixture();
        Run(fixture, Step(), managedCheckout: false);

        var elsewhere = fixture with { RepoRoot = CreateManagedCheckout() };
        Run(elsewhere, Step(), managedCheckout: false);

        // Twice, against the two directories: the second checkout has no completion recorded for
        // it, even though the command and the commit are the same. The marker lives in the tool's
        // tree rather than with the directory, so the resolved path has to be part of its key.
        Assert.Equal(
            [fixture.RepoRoot, elsewhere.RepoRoot],
            fixture.Runner.Runs.Select(run => run.WorkingDirectory));
    }

    /// <remarks>
    /// The monorepo arrangement the README documents. Independent markers are correct here, because
    /// the two services' commands can differ.
    /// </remarks>
    [Fact]
    public void TwoServicesSharingOneDirectory_KeepIndependentMarkers()
    {
        var fixture = NewFixture();
        var appHostDirectory = fixture.AppHostDirectory;

        CheckoutPreparation.Run(
            "orders", Step(), fixture.RepoRoot, appHostDirectory, managedCheckout: false,
            fixture.Git, fixture.Runner, fixture.Sink);

        CheckoutPreparation.Run(
            "payments", Step(), fixture.RepoRoot, appHostDirectory, managedCheckout: false,
            fixture.Git, fixture.Runner, fixture.Sink);

        Assert.Equal(2, fixture.Runner.Runs.Count);
        var markers = Path.Combine(appHostDirectory, ".servicesources", "prepare");
        Assert.True(File.Exists(Path.Combine(markers, "orders.json")));
        Assert.True(File.Exists(Path.Combine(markers, "payments.json")));
    }

    /// <remarks>
    /// A managed checkout's marker dies with the directory, which is what makes a
    /// deleted-and-recloned checkout re-prepare even at the same commit.
    /// </remarks>
    [Fact]
    public void ARecklonedManagedCheckout_Reprepares()
    {
        var fixture = NewFixture();
        Run(fixture, Step());

        Directory.Delete(fixture.RepoRoot, recursive: true);
        Directory.CreateDirectory(Path.Combine(fixture.RepoRoot, ".git"));

        Run(fixture, Step());

        Assert.Equal(2, fixture.Runner.Runs.Count);
    }

    /// <remarks>
    /// The marker is renamed into place rather than written where a reader can see it half-written,
    /// which is the case two <c>aspire run</c>s over one AppHost directory are in: both resolve the
    /// same managed checkout, and so the same marker path.
    /// </remarks>
    [Fact]
    public async Task AMarkerBeingWritten_IsNeverReadHalfWritten()
    {
        var fixture = NewFixture();
        Run(fixture, Step());

        var recorded = File.ReadAllText(fixture.MarkerPath);
        var observed = new List<string>();

        // Read from another thread throughout a second write, which replaces the same file.
        using var reading = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!reading.IsCancellationRequested)
            {
                try
                {
                    observed.Add(File.ReadAllText(fixture.MarkerPath));
                }
                catch (IOException)
                {
                    // The rename can be observed mid-flight as a momentary sharing violation, which
                    // is a read that saw nothing rather than a read that saw half a file.
                }
            }
        });

        fixture.Git.HeadCommitSha = "3333333333333333333333333333333333333333";
        Run(fixture, Step());

        await reading.CancelAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        var after = File.ReadAllText(fixture.MarkerPath);
        Assert.NotEqual(recorded, after);
        Assert.All(observed, seen => Assert.True(
            seen == recorded || seen == after, $"a reader saw neither record whole: '{seen}'"));
    }

    /// <summary>
    /// The whole route, not just the function that builds the path. A name that would put the
    /// marker outside the tool directory is refused before the step runs, and nothing lands in the
    /// AppHost's own tree — which is the property, and the one a test against
    /// <see cref="PrepareMarker.LocationFor"/> alone would keep asserting after a refactor stopped
    /// routing through it. That is the shape of the gap this guard exists to close (#224).
    /// </summary>
    [Fact]
    public void APathCheckout_WhoseNameWouldEscapeTheToolDirectory_IsRefusedBeforeTheStepRuns()
    {
        var fixture = NewFixture();

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            CheckoutPreparation.Run(
                "../../evil", Step(), fixture.RepoRoot, fixture.AppHostDirectory,
                managedCheckout: false, fixture.Git, fixture.Runner, fixture.Sink));

        Assert.Contains("../../evil", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Runner.Runs);

        // Where this name lands when nothing stops it: the marker path is rooted at
        // .servicesources/prepare/, so "../../evil" climbs exactly back to the AppHost directory.
        Assert.False(File.Exists(Path.Combine(fixture.AppHostDirectory, "evil.json")));
    }

    /// <summary>
    /// The same refusal at the function that builds the path, over the spellings the route test
    /// does not enumerate. <c>"..\evil"</c> is a traversal only on Windows, and <c>".. "</c> is not
    /// a directory of its own only on Windows, but both are refused everywhere: a name in shared
    /// configuration cannot mean one thing to one reader and something else to the next.
    /// </summary>
    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\evil")]
    [InlineData("..")]
    [InlineData(".. ")]
    public void LocationFor_APathServiceWhoseNameIsNotADirectoryName_IsRefused(string serviceName)
    {
        // Neither directory is created: LocationFor throws before it combines anything, and this
        // suite already leaves more temp directories behind than it cleans up.
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => PrepareMarker.LocationFor(serviceName, "/unused", "/unused", managedCheckout: false));

        Assert.Contains(serviceName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A managed checkout's marker lives inside the checkout's own <c>.git</c>, so the name reaches
    /// no path here — it was already refused when <see cref="LocalGitCheckout.ManagedRepoRoot"/>
    /// produced <c>repoRoot</c>. Pinned so the guard above is not later applied to a branch that
    /// does not need it and does not have a name to judge.
    /// </summary>
    [Fact]
    public void LocationFor_AManagedCheckout_IsInsideTheCheckoutsOwnGitDirectory()
    {
        Assert.Equal(
            Path.Combine("/repo", ".git", "servicesources-prepare.json"),
            PrepareMarker.LocationFor(ServiceName, "/repo", "/unused", managedCheckout: true));
    }
}
