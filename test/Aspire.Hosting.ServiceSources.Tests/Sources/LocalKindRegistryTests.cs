using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class LocalKindRegistryTests
{
    private sealed class FakeKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    [Fact]
    public void For_SameBuilder_ReturnsSameInstance()
    {
        var builder = CreateBuilder();

        Assert.Same(LocalKindRegistry.For(builder), LocalKindRegistry.For(builder));
    }

    [Fact]
    public void For_DifferentBuilders_ReturnIndependentInstances()
    {
        var builderA = CreateBuilder();
        var builderB = CreateBuilder();
        LocalKindRegistry.For(builderA).Register("javascript", new FakeKind());

        Assert.False(LocalKindRegistry.For(builderB).TryGet("javascript", out _));
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsSameHandler()
    {
        var builder = CreateBuilder();
        var handler = new FakeKind();

        LocalKindRegistry.For(builder).Register("javascript", handler);

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out var found));
        Assert.Same(handler, found);
    }

    [Fact]
    public void TryGet_UnregisteredKind_ReturnsFalse()
    {
        var builder = CreateBuilder();

        Assert.False(LocalKindRegistry.For(builder).TryGet("java", out var found));
        Assert.Null(found);
    }

    [Fact]
    public void Register_SameKindTwice_ThrowsNamingKind()
    {
        var builder = CreateBuilder();
        LocalKindRegistry.For(builder).Register("javascript", new FakeKind());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindRegistry.For(builder).Register("javascript", new FakeKind()));

        Assert.Contains("javascript", ex.Message);
    }

    [Fact]
    public void AddLocalKind_RegistersHandlerRetrievableViaFor()
    {
        var builder = CreateBuilder();
        var handler = new FakeKind();

        builder.AddLocalKind("javascript", handler);

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out var found));
        Assert.Same(handler, found);
    }
}
