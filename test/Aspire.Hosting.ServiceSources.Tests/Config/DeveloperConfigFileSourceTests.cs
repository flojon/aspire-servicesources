using System.Reflection;
using System.Runtime.CompilerServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// <c>servicesources.local.json</c> joins the AppHost's configuration chain when the AppHost calls
/// its first ServiceSources method, not when ServiceSources first reads the chain for itself. The
/// difference is visible to the AppHost: the entries live under <c>ServiceSources:Services</c> in
/// the AppHost's own <c>IConfiguration</c>, so a read from <c>Program.cs</c> must not depend on how
/// many <c>AddService()</c> calls happen to precede it.
/// </summary>
public class DeveloperConfigFileSourceTests
{
    private const string OrdersCatalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: src/Orders.Api/Orders.Api.csproj
        """;

    private const string SourceKey = "ServiceSources:Services:orders:source";

    /// <summary>
    /// A kind name no <see cref="ServiceMetadata"/> property claims, since
    /// <see cref="Sources.LocalKindRegistry.Register"/> rejects a reserved one.
    /// </summary>
    private const string KindUnderTest = "kind-under-test";

    private sealed class FakeKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private static string CreateAppHostDirectory(string? yaml = OrdersCatalog, string source = "local")
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        if (yaml is not null)
        {
            File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), yaml);
        }
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $$"""{ "services": { "orders": { "source": "{{source}}" } } }""");
        return dir;
    }

    [Fact]
    public void AddLocalKind_MakesTheFileReadableThroughTheAppHostsOwnConfiguration()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory());

        builder.AddLocalKind(KindUnderTest, new FakeKind());

        Assert.Equal("local", builder.Configuration[SourceKey]);
    }

    /// <summary>
    /// The opt-in an AppHost using deferred checkouts calls first of all, and the one whose own
    /// guidance — a deferred <c>dotnet</c> service should declare its endpoints in the AppHost — puts
    /// a declaration right after it, which is where a source-dependent read would go.
    /// </summary>
    [Fact]
    public void UseDeferredCheckout_MakesTheFileReadableThroughTheAppHostsOwnConfiguration()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory());

        builder.UseDeferredCheckout();

        Assert.Equal("local", builder.Configuration[SourceKey]);
    }

    /// <summary>
    /// The one entry point an AppHost can call without a catalog on disk at all, since a backing
    /// service is declared by the call rather than by <c>servicesources.yaml</c>.
    /// </summary>
    /// <remarks>
    /// So it is also the entry point most likely to be an AppHost's first line, which is where a
    /// source-dependent read of the AppHost's own configuration would go wrong.
    /// </remarks>
    [Fact]
    public void AddBackingService_MakesTheFileReadableThroughTheAppHostsOwnConfiguration()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(yaml: null));

        // Named after the backing service, as a "local" factory has to be — see #200.
        builder.AddBackingService("orders-db", () => builder.AddConnectionString("orders-db"));

        Assert.Equal("local", builder.Configuration[SourceKey]);
    }

    /// <summary>
    /// The failure this rules out: the file used to be registered by the first read of our own
    /// configuration, which is a side effect of the first <c>AddService()</c>, so the same key read
    /// one line earlier returned the chain without the file's layer — <c>null</c> for a developer
    /// who configures everything in the file, with no diagnostic.
    /// </summary>
    /// <remarks>
    /// The source is deliberately one no <c>IServiceSource</c> implements, which makes
    /// <c>AddService()</c> throw after resolving the configuration and before resolving anything on
    /// disk: a real call through the real entry point that clones nothing.
    /// </remarks>
    [Fact]
    public void ReadingAKeyBeforeAndAfterTheFirstAddService_GivesTheSameAnswer()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(source: "not-a-source"));
        builder.AddLocalKind(KindUnderTest, new FakeKind());

        var beforeAnyAddService = builder.Configuration[SourceKey];
        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));
        var afterTheFirstAddService = builder.Configuration[SourceKey];

        Assert.Equal("not-a-source", beforeAnyAddService);
        Assert.Equal(beforeAnyAddService, afterTheFirstAddService);
    }

    /// <summary>
    /// Registration is the one thing every entry point does before anything else, so it happens even
    /// on the paths that then fail — a missing catalog is reported by <c>AddService()</c>, and the
    /// configuration the AppHost reads is complete either way.
    /// </summary>
    [Fact]
    public void AddService_WithNoCatalogOnDisk_StillRegistersTheFile()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(yaml: null));

        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Equal("local", builder.Configuration[SourceKey]);
    }

    /// <summary>
    /// Every entry point registers, so all but the first must do nothing: a second insert would put
    /// a duplicate provider in the chain, and each insert disposes and rebuilds every provider on
    /// it.
    /// </summary>
    [Fact]
    public void SeveralEntryPoints_RegisterTheFileOnce()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory());
        builder.AddLocalKind(KindUnderTest, new FakeKind());
        var sourcesAfterTheFirstRegistration = builder.Configuration.Sources.Count;

        builder.AddLocalKind($"{KindUnderTest}-2", new FakeKind());
        ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal(sourcesAfterTheFirstRegistration, builder.Configuration.Sources.Count);
        Assert.Equal("local", builder.Configuration[SourceKey]);
    }

    /// <summary>
    /// A configuration source that loads once and then faults, which is what an unrelated provider
    /// on the chain looks like to the rebuild our insert triggers.
    /// </summary>
    private sealed class FaultsOnReloadSource : IConfigurationSource
    {
        // On the source rather than the provider: reloading rebuilds every provider from its
        // source, so a provider instance never sees its own second load.
        private int _builds;

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(++_builds > 1);

        private sealed class Provider(bool faults) : ConfigurationProvider
        {
            public override void Load()
            {
                if (faults)
                {
                    throw new InvalidOperationException("This provider cannot be reloaded.");
                }
            }
        }
    }

    /// <summary>
    /// Inserting mutates the source list and then rebuilds every provider on it, so a fault raised
    /// by someone else's reload surfaces after ours is already in the chain. The registration has
    /// to count as done at that point: retrying it is what would put a duplicate in the chain,
    /// which is the outcome the whole slot exists to prevent.
    /// </summary>
    [Fact]
    public void EntryPointAfterAnotherProviderFaultsOnTheRebuild_DoesNotInsertASecondCopy()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory());
        builder.Configuration.Sources.Add(new FaultsOnReloadSource());

        Assert.Throws<InvalidOperationException>(() => builder.AddLocalKind(KindUnderTest, new FakeKind()));
        var sourcesAfterTheFailedRegistration = builder.Configuration.Sources.Count;

        builder.AddLocalKind($"{KindUnderTest}-2", new FakeKind());

        Assert.Equal(sourcesAfterTheFailedRegistration, builder.Configuration.Sources.Count);
    }

    /// <summary>
    /// The registration is only as complete as the list of entry points that perform it, and that
    /// list is this package's public extension surface on <see cref="IDistributedApplicationBuilder"/>.
    /// A new one that skips it reintroduces the ordering edge for any AppHost that calls it first,
    /// so it fails here until it is wired up and listed.
    /// </summary>
    [Fact]
    public void EveryPublicBuilderExtension_RegistersTheFile()
    {
        string[] accountedFor =
            ["AddBackingService", "AddLocalKind", "AddService", "UseDeferredCheckout", "UseJava", "UseJavaScript"];

        var entryPoints = typeof(ServiceSourcesBuilderExtensions).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(method => method.GetParameters() is [{ ParameterType: var first }, ..]
                && first == typeof(IDistributedApplicationBuilder))
            .Select(method => method.Name)
            .Distinct()
            .ToArray();

        Assert.True(
            entryPoints.Except(accountedFor).ToArray() is [],
            $"AppHost entry points not accounted for: {string.Join(", ", entryPoints.Except(accountedFor))}. "
            + "Each must call DeveloperConfigFileSource.EnsureRegistered(builder) before anything "
            + "else, so that servicesources.local.json is in builder.Configuration before the "
            + "AppHost's next line reads it. Wire it up, cover it, and list it here.");

        Assert.True(
            accountedFor.Except(entryPoints).ToArray() is [],
            $"Listed here but no longer an entry point: {string.Join(", ", accountedFor.Except(entryPoints))}.");
    }
}
