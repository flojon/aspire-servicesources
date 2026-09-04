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

    /// <summary>
    /// Half-migrated: the parameter was added, in the wrong position. This fails in exactly the way
    /// <see cref="KindWithTheOldValidateSignature"/> does — no interface member implemented, the
    /// method never called — so matching only the old two-parameter shape would let it through.
    /// </summary>
    private sealed class KindWithTheParameterInTheWrongPosition : ILocalResourceKind
    {
        public void Validate(string serviceName, object? rawConfig, string repoRoot) =>
            throw new ServiceSourcesConfigurationException("Never reached, and that is the point.");

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// Everything right but the return type, which does not implement the member either. Rendering
    /// the parameter list alone would print the found signature and the wanted one identically.
    /// </summary>
    private sealed class KindWhoseValidateReturnsAValue : ILocalResourceKind
    {
        public bool Validate(string serviceName, string repoRoot, object? rawConfig) => true;

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// The one mismatch that survives rendering the return type: a generic <c>Validate</c> does not
    /// implement the non-generic member, and prints exactly like it.
    /// </summary>
    private sealed class KindWhoseValidateIsGeneric : ILocalResourceKind
    {
        public void Validate<T>(string serviceName, string repoRoot, object? rawConfig)
        {
        }

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    [Theory]
    [InlineData(typeof(KindWithTheOldValidateSignature), "void Validate(string serviceName, object rawConfig)")]
    [InlineData(
        typeof(KindWithTheParameterInTheWrongPosition),
        "void Validate(string serviceName, object rawConfig, string repoRoot)")]
    [InlineData(
        typeof(KindWhoseValidateReturnsAValue),
        "bool Validate(string serviceName, string repoRoot, object rawConfig)")]
    public void AddLocalKind_HandlerWhoseValidateDoesNotMatchTheInterface_IsRefusedAtRegistration(
        Type handlerType, string expectedSignature)
    {
        var builder = CreateBuilder();

        // Nothing else catches any of these shapes: all compile, and core runs the defaulted no-op
        // in their place, so the kind's own rejections just stop happening.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddLocalKind(
                "javascript", (ILocalResourceKind)Activator.CreateInstance(handlerType)!));

        Assert.Contains("javascript", ex.Message);
        Assert.Contains(handlerType.Name, ex.Message);
        Assert.Contains("repoRoot", ex.Message);

        // The method it actually found, not just the one it wanted — otherwise an author who put the
        // parameter in the wrong place is told to add a parameter that is already there, and one who
        // got only the return type wrong is shown two signatures that read the same.
        Assert.Contains($"declares '{expectedSignature}'", ex.Message);
        Assert.False(LocalKindRegistry.For(builder).TryGet("javascript", out _));
    }

    [Fact]
    public void AddLocalKind_HandlerWhoseValidateStillReadsAlike_SaysWhereToLookInstead()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddLocalKind("javascript", new KindWhoseValidateIsGeneric()));

        // Nothing in the parameter list distinguishes them, so quoting both back would read as a
        // message that contradicts itself unless it says where the difference actually is.
        Assert.Contains("not in the parameter list", ex.Message);
        Assert.Contains("generic parameters", ex.Message);
    }

    [Fact]
    public void AddLocalKind_NullHandler_IsRejectedByName()
    {
        // The reflection above dereferences the handler, so without this the caller gets a bare
        // NullReferenceException naming neither the argument nor the call it came out of.
        var builder = CreateBuilder();

        var ex = Assert.Throws<ArgumentNullException>(() => builder.AddLocalKind("javascript", null!));

        Assert.Equal("handler", ex.ParamName);
    }

    [Fact]
    public void AddLocalKind_HandlerWithNoValidateAtAll_IsAccepted()
    {
        // The defaulted member left alone is the ordinary case, and says nothing about migration.
        var builder = CreateBuilder();

        builder.AddLocalKind("javascript", new FakeKind());

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out _));
    }

    /// <summary>
    /// A kind that leaves the defaulted member alone on purpose and has <c>Validate</c>s of its own
    /// for something else: one private, one not starting with a service name, and one — the shape a
    /// diagnostic helper or an unrelated <c>IValidatable</c> would have — that starts with a string
    /// but carries no options block. None is an attempt at the interface member, and all of them
    /// registered before the parameter was added, so refusing any would invent a migration the
    /// author never had to make.
    /// </summary>
    private sealed class KindWithItsOwnValidateHelpers : ILocalResourceKind
    {
        private sealed class Options;

        private void Validate(Options options)
        {
        }

        public void Validate(int port) => Validate(new Options());

        public void Validate(string message) => Validate(message.Length);

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    [Fact]
    public void AddLocalKind_HandlerWhoseValidateIsItsOwnHelper_IsAccepted()
    {
        var builder = CreateBuilder();

        builder.AddLocalKind("javascript", new KindWithItsOwnValidateHelpers());

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out _));
    }

    /// <summary>
    /// The narrowest form of that, on its own: nothing but a public <c>Validate(string)</c> beside a
    /// correct <c>Resolve</c>. It passes a filter that asks only for a leading string, so it is
    /// pinned separately from the class above — a kind reduced to exactly this shape is the one a
    /// logging helper, or a member inherited from an unrelated interface, actually produces.
    /// </summary>
    private sealed class KindWithAStringOnlyValidateHelper : ILocalResourceKind
    {
        public void Validate(string message)
        {
        }

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    [Fact]
    public void AddLocalKind_HandlerWhoseOnlyValidateTakesNoOptionsBlock_IsAccepted()
    {
        var builder = CreateBuilder();

        builder.AddLocalKind("javascript", new KindWithAStringOnlyValidateHelper());

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
