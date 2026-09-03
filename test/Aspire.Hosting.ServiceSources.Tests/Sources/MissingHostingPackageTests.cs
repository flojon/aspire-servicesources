using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// The javascript and java kinds live in core but compile against hosting packages core references
/// with <c>PrivateAssets="all"</c>, so an AppHost that declares a service of one of those kinds
/// without referencing the package gets a <see cref="FileNotFoundException"/> from the runtime the
/// first time that service resolves. On its own that names an assembly and a strong name and
/// nothing a developer can act on, so the dispatch translates it.
/// </summary>
public class MissingHostingPackageTests
{
    private const string ServiceName = "frontend";

    [Theory]
    [InlineData("javascript", "Aspire.Hosting.JavaScript")]
    [InlineData("java", "CommunityToolkit.Aspire.Hosting.Java")]
    public void KindWhoseHostingAssemblyIsMissing_IsReportedAsTheMissingPackage(
        string kind, string packageId)
    {
        var repoRoot = Directory.CreateTempSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(repoRoot);
        builder.AddLocalKind(kind, new AssemblyMissingKind(packageId));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata(kind), DevConfig(repoRoot)));

        Assert.Contains(ServiceName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(kind, ex.Message, StringComparison.Ordinal);
        Assert.Contains(packageId, ex.Message, StringComparison.Ordinal);

        // The remedy, not just the diagnosis: both AppHost flavours need naming, because the
        // build-time floor check in build/KoalaSoft.Aspire.Hosting.ServiceSources.targets only
        // reaches a project that consumes core as a NuGet package. A guest-language AppHost gets
        // core through the ProjectReference the CLI generates, which imports no build/ targets, so
        // this message is the only thing that tells its author what to add.
        Assert.Contains("aspire.config.json", ex.Message, StringComparison.Ordinal);

        // The load failure is the cause, and keeping it reachable is what lets anyone diagnose a
        // case this translation guessed wrong about.
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    /// <summary>
    /// A handler failing for its own reasons must keep the generic message: this translation is
    /// only allowed to claim a missing package when the runtime actually failed to load one of the
    /// two assemblies core references privately.
    /// </summary>
    [Fact]
    public void KindThatFailsForItsOwnReasons_IsNotReportedAsAMissingPackage()
    {
        var repoRoot = Directory.CreateTempSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(repoRoot);
        builder.AddLocalKind("javascript", new BrokenKind());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata("javascript"), DevConfig(repoRoot)));

        Assert.DoesNotContain("Aspire.Hosting.JavaScript", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ILocalResourceKind.Validate), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An assembly this package knows nothing about is not ours to explain, even though the
    /// exception type is identical — a kind reading a missing file of its own would otherwise be
    /// reported as a packaging problem.
    /// </summary>
    [Fact]
    public void LoadFailureNamingSomeOtherAssembly_KeepsTheGenericMessage()
    {
        var repoRoot = Directory.CreateTempSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(repoRoot);
        builder.AddLocalKind("javascript", new AssemblyMissingKind("Contoso.Something.Else"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata("javascript"), DevConfig(repoRoot)));

        Assert.DoesNotContain("dotnet add package", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ILocalResourceKind.Validate), ex.Message, StringComparison.Ordinal);
    }

    private static ServiceMetadata Metadata(string kind) => new()
    {
        Repository = "https://example.com/frontend.git",
        Kind = kind,
    };

    private static ServiceDeveloperConfig DevConfig(string path) =>
        new() { Source = "local", Local = new() { Path = path } };

    /// <summary>
    /// Stands in for a kind whose body reached a type from an assembly that is not on disk. Thrown
    /// with the assembly's simple name as <see cref="FileNotFoundException.FileName"/> the way the
    /// runtime does, since that — not the message — is what the translation reads.
    /// </summary>
    private sealed class AssemblyMissingKind(string assemblyName) : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new FileNotFoundException(
                $"Could not load file or assembly '{assemblyName}, Version=13.5.2.0, Culture=neutral, "
                + "PublicKeyToken=cc7b13ffcd2ddd51'. The system cannot find the file specified.",
                $"{assemblyName}, Version=13.5.2.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");
    }

    private sealed class BrokenKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new InvalidOperationException("the handler is broken");
    }

    /// <summary>
    /// The service resolves through a <c>local.path</c> override, so nothing here should be called.
    /// </summary>
    private sealed class UnusedGitClient : IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null) =>
            throw new InvalidOperationException("a path override must not clone");

        public void Checkout(string repositoryPath, string reference) =>
            throw new InvalidOperationException("a path override must not check out");

        public void Fetch(string repositoryPath) =>
            throw new InvalidOperationException("a path override must not fetch");

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

        public string? GetOriginUrl(string repositoryPath) => null;
    }
}
