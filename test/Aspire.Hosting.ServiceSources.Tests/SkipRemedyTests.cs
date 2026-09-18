using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The one sentence every message offering the 'local'/'container' switch ends in — skips, reverts,
/// and the three exceptions that offer it as a way out. A sentence that dead-ends is a defect in all
/// of them at once, which is why it is pinned on its own.
/// </summary>
/// <remarks>
/// It used to say "set its source to 'local' or 'container'" with no condition attached, and a
/// service whose catalog entry declares neither — the ordinary shape of a service that is only ever
/// a url — got <see cref="ServiceSourcesConfigurationException"/> on both options it offered. That
/// each option resolves once its block is declared is already pinned where those sources are tested
/// (<c>ContainerSourceTests</c>, <c>LocalProjectSourceTests</c>); what is pinned here is that the
/// message says so before the reader acts on it.
/// </remarks>
public class SkipRemedyTests
{
    private const string UrlOnlyService = "inventory";

    private const string SwitchableService = "orders";

    private static readonly ServiceDefinition UrlOnly = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://inventory.example.com" },
    }.ToDefinition("servicesources.yaml", UrlOnlyService, TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition KubernetesOnly = new ServiceMetadata
    {
        Kubernetes = new KubernetesMetadata { Service = SwitchableService, Port = 8080 },
    }.ToDefinition("servicesources.yaml", SwitchableService, TestHelpers.EmptyRepositories);

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public bool IsAvailable(int port) => true;

        public int AllocatePort() => 51234;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static string SkipMessageFor(string source, string serviceName)
    {
        var builder = Builder();

        if (string.Equals(source, "url", StringComparison.Ordinal))
        {
            new UrlSource()
                .Resolve(builder, serviceName, UrlOnly, new ServiceDeveloperConfig { Source = "url" })
                .WithEnvironment("A", "B");
        }
        else
        {
            new KubernetesSource(new FixedPortAllocator())
                .Resolve(
                    builder, serviceName, KubernetesOnly,
                    new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } })
                .WithEnvironment("A", "B");
        }

        return Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    }

    private static void AssertStatesThePrecondition(string message)
    {
        Assert.Contains("servicesources.local.json", message);
        // Without these three the sentence reads as an unconditional instruction, which is what sent
        // a reader of a url-only service into an exception on both options.
        Assert.Contains("servicesources.yaml", message);
        Assert.Contains("'repository' or 'repositoryRef' for 'local'", message);
        Assert.Contains("'container' block for 'container'", message);
    }

    [Theory]
    [InlineData("url", UrlOnlyService)]
    [InlineData("kubernetes", SwitchableService)]
    public void TheRemedyStatesTheCatalogPreconditionRatherThanPromisingTheSwitch(
        string source, string serviceName)
    {
        AssertStatesThePrecondition(SkipMessageFor(source, serviceName));
    }

    /// <remarks>
    /// <c>Unwrap&lt;T&gt;</c> refuses an out-of-band source and offers the same switch as its way
    /// out, from an exception rather than a warning — so it owes the reader the same precondition.
    /// </remarks>
    [Theory]
    [InlineData("url", UrlOnlyService)]
    [InlineData("kubernetes", SwitchableService)]
    public void UnwrapOnAnOutOfBandSource_StatesTheCatalogPrecondition(string source, string serviceName)
    {
        var builder = Builder();

        var service = string.Equals(source, "url", StringComparison.Ordinal)
            ? new UrlSource().Resolve(
                builder, serviceName, UrlOnly, new ServiceDeveloperConfig { Source = "url" })
            : new KubernetesSource(new FixedPortAllocator()).Resolve(
                builder, serviceName, KubernetesOnly,
                new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => service.Unwrap<IResourceWithEnvironment>());

        AssertStatesThePrecondition(ex.Message);
    }

    /// <remarks>
    /// The third site, and the one where the switch is the only way forward at all: a container
    /// consumer of a url service cannot be made to work any other way.
    /// </remarks>
    [Fact]
    public async Task AContainerReferencingAUrlOnlyService_StatesTheCatalogPrecondition()
    {
        var directory = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(directory, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://inventory.example.com
            """);
        File.WriteAllText(
            Path.Combine(directory, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");

        var builder = TestHelpers.CreateBuilderThatCanStart(directory);
        builder.AddContainer("storefront", "nginx:alpine").WithReference(builder.AddService("inventory"));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        AssertStatesThePrecondition(ex.Message);
    }

    /// <remarks>
    /// A dropped <c>WaitFor</c> reaches this same sentence, and start ordering rather than
    /// configuration is what that reader lost — so the offer has to name both.
    /// </remarks>
    [Fact]
    public void TheRemedyOffersStartOrderingAsWellAsConfiguration()
    {
        Assert.Contains("configuration and start ordering", SkipMessageFor("url", UrlOnlyService));
    }

    /// <remarks>
    /// The service name is a catalog key, so it is caller-controlled exactly as the endpoint name in
    /// the same sentence is — and it used to be the one of the two interpolated raw.
    /// </remarks>
    [Fact]
    public void AServiceNameShapedLikeASecondEntry_DoesNotForgeOne()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var hostile = "inventory'\r\n" + lineSeparator + "Service 'forged': skipped everything";

        var message = SkipMessageFor("url", hostile);

        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("\r", message);
        Assert.DoesNotContain(lineSeparator, message);
        // The quote that would close the real name is escaped, so what follows stays inside it.
        Assert.DoesNotContain("Service 'forged'", message);
    }

    [Fact]
    public void AnOverlongServiceName_IsCappedRatherThanFloodingTheLine()
    {
        var message = SkipMessageFor("url", new string('x', 300));

        Assert.Contains("…", message);
        Assert.DoesNotContain(new string('x', 200), message);
    }
}
