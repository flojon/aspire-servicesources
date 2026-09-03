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

    /// <summary>
    /// A kind written against the older <c>Validate(string, object?)</c>. It compiles against the
    /// current interface — the member is defaulted, so nothing here has to implement it — which is
    /// exactly the problem: core would call the do-nothing default, and every rejection this method
    /// makes would silently stop happening.
    /// </summary>
    private sealed class KindWithTheOldValidateSignature : ILocalResourceKind
    {
        public void Validate(string serviceName, object? rawConfig) =>
            throw new ServiceSourcesConfigurationException("Never reached, and that is the point.");

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// Migrated, but keeping a method of the old shape for its own reasons. Nothing is wrong with
    /// that, so it must not be caught by the check above.
    /// </summary>
    private sealed class KindWithBothValidateSignatures : ILocalResourceKind
    {
        public void Validate(string serviceName, object? rawConfig)
        {
        }

        public void Validate(string serviceName, string repoRoot, object? rawConfig) =>
            Validate(serviceName, rawConfig);

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// Migrated and implementing the current member explicitly, which names it for the interface
    /// rather than "Validate" — so the check has to read the interface map, not the type's methods.
    /// </summary>
    private sealed class KindImplementingValidateExplicitly : ILocalResourceKind
    {
        public void Validate(string serviceName, object? rawConfig)
        {
        }

        void ILocalResourceKind.Validate(string serviceName, string repoRoot, object? rawConfig) =>
            Validate(serviceName, rawConfig);

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    [Fact]
    public void AddLocalKind_HandlerStillOnTheOldValidateSignature_IsRefusedAtRegistration()
    {
        var builder = CreateBuilder();

        // Nothing else catches this: the old method compiles, and core runs the defaulted no-op in
        // its place, so the kind's own rejections just stop happening.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddLocalKind("javascript", new KindWithTheOldValidateSignature()));

        Assert.Contains("javascript", ex.Message);
        Assert.Contains(nameof(KindWithTheOldValidateSignature), ex.Message);
        Assert.Contains("repoRoot", ex.Message);
        Assert.False(LocalKindRegistry.For(builder).TryGet("javascript", out _));
    }

    [Fact]
    public void AddLocalKind_HandlerWithNoValidateAtAll_IsAccepted()
    {
        // The defaulted member left alone is the ordinary case, and says nothing about migration.
        var builder = CreateBuilder();

        builder.AddLocalKind("javascript", new FakeKind());

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out _));
    }

    [Theory]
    [InlineData(typeof(KindWithBothValidateSignatures))]
    [InlineData(typeof(KindImplementingValidateExplicitly))]
    public void AddLocalKind_HandlerThatImplementsTheCurrentValidate_IsAccepted(Type handlerType)
    {
        var builder = CreateBuilder();

        builder.AddLocalKind("javascript", (ILocalResourceKind)Activator.CreateInstance(handlerType)!);

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out _));
    }
}
