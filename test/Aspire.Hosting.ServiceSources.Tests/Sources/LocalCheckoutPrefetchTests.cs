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

        public Barrier? StartBarrier { get; set; }

        public List<string> Cloned { get; } = [];

        public void FailFor(string repositoryUrl, Exception exception) => _failFor[repositoryUrl] = exception;

        public void Clone(string repositoryUrl, string destinationPath)
        {
            lock (Cloned)
            {
                Cloned.Add(repositoryUrl);
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

    private static ServiceMetadata Metadata(string name) =>
        new() { Repository = $"https://example.com/{name}.git", Project = "Service.csproj" };

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

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(new FakeGitClient()).Resolve(builder, "frontend", metadata, DevConfig()));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("javascript", ex.Message);
        Assert.Contains("before the first AddService call", ex.Message);
    }
}
