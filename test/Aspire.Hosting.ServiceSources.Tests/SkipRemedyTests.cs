using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Follows the remedy the package's out-of-band messages offer, literally, and checks it lands
/// somewhere. Every skip and every revert this package reports ends in that one sentence, so a
/// sentence that dead-ends is a defect in all of them at once.
/// </summary>
/// <remarks>
/// It used to say "set its source to 'local' or 'container'" with no condition attached, and a
/// service whose catalog entry declares neither — the ordinary shape of a service that is only ever
/// a url — got <see cref="ServiceSourcesConfigurationException"/> on both options it offered.
/// </remarks>
public class SkipRemedyTests
{
    private const string UrlOnlyService = "inventory";

    private const string SwitchableService = "orders";

    /// <summary>
    /// A service reachable only as a url: the catalog knows where it answers and nothing else, which
    /// is what makes both halves of the remedy unavailable to it.
    /// </summary>
    private static readonly ServiceDefinition UrlOnly = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://inventory.example.com" },
    }.ToDefinition("servicesources.yaml", UrlOnlyService, TestHelpers.EmptyRepositories);

    /// <summary>
    /// The same service with both blocks declared — the precondition the remedy now names.
    /// </summary>
    private static readonly ServiceDefinition Switchable = new ServiceMetadata
    {
        Repository = "https://github.com/company/orders",
        Project = "Orders.csproj",
        Container = new ContainerMetadata { Image = "ghcr.io/company/orders", Port = 8080 },
        Kubernetes = new KubernetesMetadata { Service = "orders", Port = 8080 },
    }.ToDefinition("servicesources.yaml", SwitchableService, TestHelpers.EmptyRepositories);

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public bool IsAvailable(int port) => true;

        public int AllocatePort() => 51234;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    /// <summary>Clones without a network, so the 'local' half of the remedy can be followed here.</summary>
    private sealed class StubGitClient : IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Orders.csproj"), "<Project />");
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

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static string SkipMessageFor(string source)
    {
        var builder = Builder();

        if (string.Equals(source, "url", StringComparison.Ordinal))
        {
            new UrlSource()
                .Resolve(builder, UrlOnlyService, UrlOnly, new ServiceDeveloperConfig { Source = "url" })
                .WithEnvironment("A", "B");
        }
        else
        {
            new KubernetesSource(new FixedPortAllocator())
                .Resolve(
                    builder, SwitchableService, Switchable,
                    new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } })
                .WithEnvironment("A", "B");
        }

        return Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("kubernetes")]
    public void TheRemedyStatesTheCatalogPreconditionRatherThanPromisingTheSwitch(string source)
    {
        var message = SkipMessageFor(source);

        Assert.Contains("servicesources.local.json", message);
        // Without these three the sentence reads as an unconditional instruction, which is what sent
        // a reader of a url-only service into an exception on both options.
        Assert.Contains("servicesources.yaml", message);
        Assert.Contains("'repository' or 'repositoryRef' for 'local'", message);
        Assert.Contains("'container' block for 'container'", message);
    }

    [Fact]
    public void FollowingTheRemedyToContainer_ResolvesWhenTheCatalogDeclaresThatBlock()
    {
        var resolved = new ContainerSource().Resolve(
            Builder(), SwitchableService, Switchable, new ServiceDeveloperConfig { Source = "container" });

        Assert.Equal(SwitchableService, resolved.Resource.Name);
    }

    [Fact]
    public void FollowingTheRemedyToLocal_ResolvesWhenTheCatalogDeclaresARepository()
    {
        var appHostDirectory = TempDirectories.CreateSubdirectory().FullName;
        var config = new ServiceDeveloperConfig { Source = "local" };

        var repoRoot = LocalGitCheckout.ResolveRepoRoot(
            SwitchableService, Switchable, config, null, appHostDirectory, new StubGitClient());

        Assert.Equal(
            Path.Combine(repoRoot, "Orders.csproj"),
            LocalProjectSource.ResolveProjectFile(SwitchableService, repoRoot, Switchable.Project));
    }

    [Fact]
    public void FollowingTheRemedyWithoutTheCatalogBlock_IsWhatTheConditionWarnsAbout()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => new ContainerSource().Resolve(
            Builder(), UrlOnlyService, UrlOnly, new ServiceDeveloperConfig { Source = "container" }));

        Assert.Contains(UrlOnlyService, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }
}
