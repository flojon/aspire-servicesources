using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// The <c>"local"</c> source resolves eagerly now, so that <c>AddService()</c> can hand back the
/// real resource (issues #53/#58). These cover the machinery that keeps checkouts parallel anyway,
/// and the speculative-prefetch rules that stop it inventing failures for services the AppHost
/// never asks for.
/// </summary>
public class LocalCheckoutPrefetchTests
{
    private sealed class FakeGitClient : IGitClient
    {
        private readonly Dictionary<string, Exception> _failFor = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ManualResetEventSlim> _blockUntil = new(StringComparer.Ordinal);

        public Barrier? StartBarrier { get; set; }

        private readonly Dictionary<string, string[]> _progressLines = new(StringComparer.Ordinal);

        public List<string> Cloned { get; } = [];

        /// <summary>Whether each repository's clone was given somewhere to report progress.</summary>
        public Dictionary<string, bool> ProgressAttachedFor { get; } = new(StringComparer.Ordinal);

        public List<(string RepositoryPath, string Reference)> CheckedOut { get; } = [];

        public void FailFor(string repositoryUrl, Exception exception) => _failFor[repositoryUrl] = exception;

        /// <summary>Holds this repository's clone open until the returned gate is set.</summary>
        public ManualResetEventSlim BlockFor(string repositoryUrl) =>
            _blockUntil[repositoryUrl] = new ManualResetEventSlim(false);

        /// <summary>Progress lines this repository's clone reports before it finishes.</summary>
        public void ReportProgress(string repositoryUrl, params string[] lines) =>
            _progressLines[repositoryUrl] = lines;

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            lock (Cloned)
            {
                Cloned.Add(repositoryUrl);
                ProgressAttachedFor[repositoryUrl] = progress is not null;
            }

            if (_progressLines.TryGetValue(repositoryUrl, out var lines))
            {
                Assert.NotNull(progress);

                foreach (var line in lines)
                {
                    progress.Report(line);
                }
            }

            if (_blockUntil.TryGetValue(repositoryUrl, out var gate))
            {
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            // Rendezvous with the other clone(s): if the prefetch were sequential, only one
            // participant would ever be here at a time and this would time out, failing the test
            // deterministically rather than by timing.
            if (StartBarrier is not null && !StartBarrier.SignalAndWait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting for the other clone to start concurrently.");
            }

            if (_failFor.TryGetValue(repositoryUrl, out var exception))
            {
                throw exception;
            }

            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Service.csproj"), "<Project />");
        }

        public void Checkout(string repositoryPath, string reference)
        {
            lock (CheckedOut)
            {
                CheckedOut.Add((repositoryPath, reference));
            }
        }

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => false;

        public string? GetOriginUrl(string repositoryPath) => null;
    }

    private sealed class FakeKindResource(string name) : Resource(name), IResourceWithServiceDiscovery;

    private sealed class FakeLocalResourceKind : ILocalResourceKind
    {
        public List<(string ServiceName, string RepoRoot)> Calls { get; } = [];

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            Calls.Add((serviceName, repoRoot));
            return builder.AddResource(new FakeKindResource(serviceName)).WithHttpEndpoint(port: 5555, name: "http");
        }
    }

    /// <summary>
    /// Writes an app host directory whose config declares <paramref name="localServices"/> as
    /// <c>"local"</c> — which is what the prefetch enumerates.
    /// </summary>
    private static string CreateAppHostDirectory(params string[] localServices)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var yaml = string.Join("\n", localServices.Select(name =>
            $"  {name}:\n    repository: https://example.com/{name}.git\n    project: Service.csproj"));
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"services:\n{yaml}\n");

        var json = string.Join(",", localServices.Select(name => $"\"{name}\": {{ \"source\": \"local\" }}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $"{{ \"services\": {{ {json} }} }}");

        return dir;
    }

    /// <summary>
    /// Like <see cref="CreateAppHostDirectory"/>, but the catalog names a <c>defaultRef</c> — the
    /// config without which there is no ref reconciliation to defer in the first place.
    /// </summary>
    private static string CreateAppHostDirectoryOnRef(string defaultRef, params string[] localServices)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var yaml = string.Join("\n", localServices.Select(name =>
            $"  {name}:\n    repository: https://example.com/{name}.git\n    project: Service.csproj\n" +
            $"    defaultRef: {defaultRef}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"services:\n{yaml}\n");

        var json = string.Join(",", localServices.Select(name => $"\"{name}\": {{ \"source\": \"local\" }}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $"{{ \"services\": {{ {json} }} }}");

        return dir;
    }

    /// <summary>
    /// Plants a checkout that already exists — a working tree this run did not create, which is the
    /// only kind the prefetch is not allowed to touch.
    /// </summary>
    private static string PlantExistingCheckout(string appHostDirectory, string serviceName)
    {
        var repoRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        File.WriteAllText(Path.Combine(repoRoot, "Service.csproj"), "<Project />");

        return repoRoot;
    }

    /// <summary>
    /// Writes an app host directory whose services all live in <b>one</b> repository — the monorepo
    /// shape, where each service is a different project inside the same clone.
    /// </summary>
    private static string CreateMonorepoAppHostDirectory(string repository, params string[] localServices)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var yaml = string.Join("\n", localServices.Select(name =>
            $"  {name}:\n    repository: {repository}\n    project: Service.csproj"));
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"services:\n{yaml}\n");

        var json = string.Join(",", localServices.Select(name => $"\"{name}\": {{ \"source\": \"local\" }}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $"{{ \"services\": {{ {json} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(string name, string? defaultRef = null) =>
        new() { Repository = $"https://example.com/{name}.git", Project = "Service.csproj", DefaultRef = defaultRef };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "local" };

    [Fact]
    public void FirstAddService_ClonesEveryLocalServiceInParallel()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        // Two participants: neither Clone call can return until both have started.
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };
        var source = new LocalProjectSource(git);

        // Resolving one service triggers the prefetch for both.
        source.Resolve(builder, "orders", Metadata("orders"), DevConfig());

        Assert.Equal(2, git.Cloned.Count);
    }

    /// <summary>
    /// The prefetch enumerates on the <c>source</c> value, so it has to read that value the same way
    /// <c>AddService()</c> resolves it — case-insensitively. A service spelled <c>"Local"</c> used to
    /// be dropped from the set silently, and its clone then serialised on the <c>AddService()</c>
    /// thread instead of running with the others: no error, just a slower start (#167).
    /// </summary>
    [Fact]
    public void FirstAddService_LocalSpelledWithACapital_IsStillPrefetchedInParallel()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        // Re-spell one service's source. The Barrier below is what makes this an assertion about the
        // prefetch rather than about cloning at all: "billing" is only ever cloned on the prefetch's
        // own thread, so if the filter drops it, "orders" waits alone and times out.
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "local" }, "billing": { "source": "Local" } } }""");

        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };
        var source = new LocalProjectSource(git);

        source.Resolve(builder, "orders", Metadata("orders"), DevConfig());

        Assert.Equal(2, git.Cloned.Count);
    }

    [Fact]
    public void FirstAddService_TwoServicesInOneRepository_DownloadsItTwiceConcurrently()
    {
        const string Repository = "https://example.com/monorepo.git";
        var dir = CreateMonorepoAppHostDirectory(Repository, "orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        // Barrier(2): neither clone may return until both have started, so this cannot pass unless
        // the two downloads really do overlap.
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };
        var source = new LocalProjectSource(git);

        source.Resolve(builder, "orders", new ServiceMetadata { Repository = Repository, Project = "Service.csproj" }, DevConfig());

        // Checkouts are keyed by service, not by repository, so a monorepo is fetched once per
        // service that lives in it — concurrently, competing for the same bandwidth. Documented
        // rather than asserted-against: each service needs its own working tree (they can sit on
        // different refs), so sharing one clone is not a drop-in change.
        Assert.Equal([Repository, Repository], git.Cloned);
    }

    [Fact]
    public void SecondAddService_ReusesThePrefetchedCheckout()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };
        var source = new LocalProjectSource(git);

        source.Resolve(builder, "orders", Metadata("orders"), DevConfig());
        source.Resolve(builder, "billing", Metadata("billing"), DevConfig());

        // Two services, two clones total — the second AddService cloned nothing more.
        Assert.Equal(2, git.Cloned.Count);
    }

    [Fact]
    public void ResolvedDotnetService_IsRegisteredAsARealProjectResource()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);
        var source = new LocalProjectSource(new FakeGitClient());

        var service = source.Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // The heart of #58: the thing AddService returns is in the app model, so DCP gives it a
        // Service object and a container consumer can reference it.
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
        Assert.IsAssignableFrom<ProjectResource>(service.Resource);
    }

    [Fact]
    public void CheckoutFailure_SurfacesOnlyWhenThatServiceIsRequested()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();
        git.FailFor("https://example.com/billing.git", new InvalidOperationException("no such repo"));
        var source = new LocalProjectSource(git);

        // "billing" failed during the speculative prefetch, but this AppHost only wants "orders".
        var service = source.Resolve(builder, "orders", Metadata("orders"), DevConfig());
        Assert.NotNull(service);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => source.Resolve(builder, "billing", Metadata("billing"), DevConfig()));
        Assert.Contains("billing", ex.Message);
    }

    [Fact]
    public async Task SlowCheckoutForAnotherService_DoesNotBlockTheRequestedOne()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();
        // "billing" is prefetched speculatively and never finishes. The AppHost only asks for
        // "orders", so it must not wait on it — the prefetch is parallel *and* deferred.
        var billingGate = git.BlockFor("https://example.com/billing.git");
        var source = new LocalProjectSource(git);

        try
        {
            var resolve = Task.Run(() => source.Resolve(builder, "orders", Metadata("orders"), DevConfig()));
            var finished = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(
                ReferenceEquals(finished, resolve),
                "AddService blocked on a speculative checkout for a service it never asked for.");
            Assert.NotNull(await resolve);
        }
        finally
        {
            billingGate.Set();
        }
    }

    [Fact]
    public void CheckoutFailure_KeepsTheStackTraceFromWhereItActuallyFailed()
    {
        var dir = CreateAppHostDirectory("billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();
        git.FailFor("https://example.com/billing.git", new InvalidOperationException("no such repo"));
        var source = new LocalProjectSource(git);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => source.Resolve(builder, "billing", Metadata("billing"), DevConfig()));

        // A plain `throw storedException` would have reset this to the re-throw site, hiding the
        // prefetch worker the clone actually failed on.
        Assert.Contains(nameof(LocalGitCheckout.PrepareRepoRoot), ex.StackTrace);
    }

    [Fact]
    public void ServiceInDeveloperConfigButNotInCatalog_IsSkippedByThePrefetch()
    {
        var dir = CreateAppHostDirectory("orders");
        // "ghost" is local in the developer's config but absent from the catalog. The prefetch must
        // not try to clone it, and must not fail the AppHost over it.
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "local" }, "ghost": { "source": "local" } } }""");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();

        var service = new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        Assert.NotNull(service);
        Assert.Equal(["https://example.com/orders.git"], git.Cloned);
    }

    [Fact]
    public void RegisteredNonDotnetKind_ReceivesTheCheckoutAndItsResourceIsRegistered()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        var handler = new FakeLocalResourceKind();
        builder.AddLocalKind("javascript", handler);
        var metadata = new ServiceMetadata { Repository = "https://example.com/frontend.git", Kind = "javascript" };

        var service = new LocalProjectSource(new FakeGitClient()).Resolve(builder, "frontend", metadata, DevConfig());

        Assert.Equal("frontend", Assert.Single(handler.Calls).ServiceName);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }

    [Fact]
    public void UnregisteredKind_ThrowsNamingTheServiceAndTheRegistrationOrder()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        var metadata = new ServiceMetadata { Repository = "https://example.com/frontend.git", Kind = "javascript" };
        var git = new FakeGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(git).Resolve(builder, "frontend", metadata, DevConfig()));

        // The kind lookup is a registry probe, so it has to happen before the checkout: a typo'd
        // kind must not cost a cold clone of this repository — nor, through the prefetch, of every
        // other "local" service in the developer config.
        Assert.Empty(git.Cloned);

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("javascript", ex.Message);
        Assert.Contains("before the first AddService call", ex.Message);
    }

    [Fact]
    public void ServiceMarkedLocalButNeverAdded_IsReportedRatherThanClonedSilently()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // "billing" was cloned because the prefetch cannot know which services the AppHost will
        // add — but the developer can act on that, since the file is theirs. Paying for it in
        // silence is what makes a first run look like a hang.
        var message = LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage;

        Assert.NotNull(message);
        Assert.Contains("billing", message);
        Assert.DoesNotContain("orders", message);
        Assert.Contains("1 service", message);
    }

    [Fact]
    public void ExistingCheckoutForAServiceNeverAdded_IsLeftOnTheRefItWasFoundOn()
    {
        var dir = CreateAppHostDirectoryOnRef("main", "orders", "billing");
        var ordersRoot = PlantExistingCheckout(dir, "orders");
        PlantExistingCheckout(dir, "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders", defaultRef: "main"), DevConfig());

        // Both checkouts already existed, so there was nothing to clone...
        Assert.Empty(git.Cloned);

        // ...and only the service this AppHost actually added was moved onto its configured ref.
        // Reconciling "billing" too would run `git checkout` inside a working tree on the strength
        // of a config entry alone: it discards nothing, but it silently moves committed, unpushed
        // work off the branch the developer left it on, in a run that never mentioned that service.
        Assert.Equal([(ordersRoot, "main")], git.CheckedOut);
    }

    [Fact]
    public void CheckoutFailureForAServiceNeverAdded_IsReportedRatherThanSwallowed()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();
        git.FailFor("https://example.com/billing.git", new InvalidOperationException("no such repo"));

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        var prefetch = LocalCheckoutPrefetch.For(builder, git);

        // "billing" is never added, so GetRepoRoot never re-throws its failure. Without this report
        // the run pays a failing clone — credential-helper round trip included — and says nothing,
        // leaving a config entry that has been broken since someone renamed the repository.
        Assert.True(
            SpinWait.SpinUntil(
                () => prefetch.FailedUnusedCheckoutMessages.Count == 1, TimeSpan.FromSeconds(30)),
            "The failed speculative checkout for 'billing' was never reported.");

        var message = Assert.Single(prefetch.FailedUnusedCheckoutMessages);
        Assert.Contains("billing", message);
        Assert.Contains("failed to clone", message);
    }

    [Fact]
    public void EveryLocalServiceAdded_ReportsNothing()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };
        var source = new LocalProjectSource(git);

        source.Resolve(builder, "orders", Metadata("orders"), DevConfig());
        source.Resolve(builder, "billing", Metadata("billing"), DevConfig());

        // Nothing was speculative in the end, so there is nothing to tell the developer about.
        Assert.Null(LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage);
    }

    /// <remarks>
    /// The prefetch matches its candidates against the catalog by name, and the names it matches
    /// with come from configuration, whose keys are case-insensitive — so an entry that spells a
    /// service differently from the catalog must still reach this phase. Two participants on the
    /// barrier is what makes that observable: if 'billing' were dropped for being spelled
    /// 'Billing', only 'orders' would ever arrive and the clone would time out rather than the
    /// assertion merely counting one fewer.
    /// </remarks>
    [Fact]
    public void FirstAddService_ConfigurationSpellsAServiceDifferentlyFromTheCatalog_StillPrefetchesIt()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                repository: https://example.com/orders.git
                project: Service.csproj
              billing:
                repository: https://example.com/billing.git
                project: Service.csproj
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """
            { "services": { "orders": { "source": "local" }, "Billing": { "source": "local" } } }
            """);

        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        Assert.Equal(2, git.Cloned.Count);
        Assert.Contains("https://example.com/billing.git", git.Cloned);
    }

    /// <summary>
    /// Every line the prefetched clone reported, once its stream has ended. Fails the test rather
    /// than hanging if it never does — a stream left open is a deferred service left in "Checking
    /// out" forever.
    /// </summary>
    private static async Task<IReadOnlyList<string>> DrainProgressAsync(
        LocalCheckoutPrefetch prefetch, string serviceName)
    {
        var progress = prefetch.WatchCheckout(serviceName);

        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var lines = new List<string>();
        await foreach (var line in progress.ReadAllAsync(giveUp.Token))
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public async Task Progress_CarriesWhatTheCloneReported()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // The fake reports nothing, which is a clone git had nothing to say about — the small
        // repository of the "silence is normal" case. What matters here is that the stream ends.
        Assert.Empty(await DrainProgressAsync(LocalCheckoutPrefetch.For(builder, git), "orders"));
    }

    [Fact]
    public async Task Progress_EndsWhenThereWasNoCloneToRun()
    {
        var dir = CreateAppHostDirectory("orders");
        PlantExistingCheckout(dir, "orders");

        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // A warm checkout: nothing was cloned, so nothing was reported — but the stream still has to
        // end, because whoever is watching waits for that rather than polling.
        Assert.Empty(git.Cloned);
        Assert.Empty(await DrainProgressAsync(LocalCheckoutPrefetch.For(builder, git), "orders"));
    }

    [Fact]
    public async Task Progress_EndsWhenTheCloneFailed()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();
        git.FailFor("https://example.com/orders.git", new InvalidOperationException("no such repo"));

        Assert.ThrowsAny<Exception>(
            () => new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig()));

        Assert.Empty(await DrainProgressAsync(LocalCheckoutPrefetch.For(builder, git), "orders"));
    }

    [Fact]
    public void Progress_IsNotRequestedForACheckoutNobodyIsWatching()
    {
        var dir = CreateAppHostDirectory("orders");

        // "billing" is in the catalog but not configured as "local", so the prefetch never
        // enumerates it and its checkout is resolved on the calling thread instead.
        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                repository: https://example.com/orders.git
                project: Service.csproj
              billing:
                repository: https://example.com/billing.git
                project: Service.csproj
            """);

        var builder = TestHelpers.CreateBuilder(dir);
        var git = new FakeGitClient();

        new LocalProjectSource(git).Resolve(builder, "billing", Metadata("billing"), DevConfig());

        // Nobody asked to watch it, so git was never asked for progress either — which is what
        // keeps --progress off every clone that has no audience. ("orders" is cloned too, by the
        // speculative prefetch, and that one does get a stream: it is created with the checkout.)
        Assert.False(git.ProgressAttachedFor["https://example.com/billing.git"]);
    }

    [Fact]
    public async Task ConcurrentClones_EachReportToTheirOwnStream()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);

        var git = new FakeGitClient();
        git.ReportProgress("https://example.com/orders.git", "Receiving objects:  10% (1/10)");
        git.ReportProgress("https://example.com/billing.git", "Receiving objects:  20% (2/10)");

        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        var prefetch = LocalCheckoutPrefetch.For(builder, git);

        // Both clones run at once, so a shared buffer or a shared parser would show up as one
        // service's progress on the other's resource.
        Assert.Equal(["Receiving objects:  10% (1/10)"], await DrainProgressAsync(prefetch, "orders"));
        Assert.Equal(["Receiving objects:  20% (2/10)"], await DrainProgressAsync(prefetch, "billing"));
    }
}
