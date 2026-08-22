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
    public void Register_ReservedDotnetKind_ThrowsAndDoesNotRegister()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindRegistry.For(builder).Register("dotnet", new FakeKind()));

        Assert.Contains("dotnet", ex.Message);
        Assert.False(LocalKindRegistry.For(builder).TryGet("dotnet", out _));
    }

    [Theory]
    [InlineData("container")]
    [InlineData("url")]
    [InlineData("kubernetes")]
    [InlineData("repository")]
    [InlineData("kind")]
    public void Register_KindNameCollidingWithAWellKnownServiceProperty_Throws(string reserved)
    {
        var builder = CreateBuilder();

        // A block named after a typed ServiceMetadata property is bound as that property, so a kind
        // by that name could never receive its own options — reject it at registration instead.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindRegistry.For(builder).Register(reserved, new FakeKind()));

        Assert.Contains(reserved, ex.Message);
        Assert.False(LocalKindRegistry.For(builder).TryGet(reserved, out _));
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
