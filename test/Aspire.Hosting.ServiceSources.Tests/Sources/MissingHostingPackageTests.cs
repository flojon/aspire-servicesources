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
        // build-time floor check in buildTransitive/ only reaches a project that consumes core as
        // a NuGet package. A guest-language AppHost gets core through the ProjectReference the CLI
        // generates, which imports none of a package's build-time targets, so this message is the
        // only thing that tells its author what to add.
        Assert.Contains("aspire.config.json", ex.Message, StringComparison.Ordinal);

        // The load failure is the cause, and keeping it reachable is what lets anyone diagnose a
        // case this translation guessed wrong about.
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    /// <summary>
    /// <see cref="ILocalResourceKind.Validate"/> resolves the whole options block and makes every
    /// check a kind has against its checkout, so it reaches a hosting type exactly as
    /// <see cref="ILocalResourceKind.Resolve"/> can — and it is the call core makes first. The
    /// translation has to cover it, or which of the two methods happened to touch the type decides
    /// whether the developer is told to install a package or handed a raw load error.
    /// </summary>
    [Theory]
    [InlineData("javascript", "Aspire.Hosting.JavaScript")]
    [InlineData("java", "CommunityToolkit.Aspire.Hosting.Java")]
    public void KindWhoseHostingAssemblyIsMissingFromValidate_IsReportedTheSameWay(
        string kind, string packageId)
    {
        var repoRoot = Directory.CreateTempSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(repoRoot);
        builder.AddLocalKind(kind, new AssemblyMissingKind(packageId, fromValidate: true));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata(kind), DevConfig(repoRoot)));

        Assert.Contains(ServiceName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(kind, ex.Message, StringComparison.Ordinal);
        Assert.Contains(packageId, ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// An unregistered kind is a different failure from a missing package, and takes different
    /// advice: it is fixed by calling the registration method, not by installing anything. Nothing
    /// is missing from the AppHost's references, so the message must not send the reader looking
    /// for a package to add.
    /// </summary>
    [Fact]
    public void UnregisteredKind_TellsTheReaderToCallTheRegistrationMethod()
    {
        var repoRoot = Directory.CreateTempSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(repoRoot);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata("javascript"), DevConfig(repoRoot)));

        Assert.Contains("is not registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UseJavaScript()", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("satellite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of #187 item (1). A package present but older than the floor does not always
    /// fail to load: a prerelease cut before a release carries that release's assembly version, so
    /// it binds and then fails on the member that is not there yet. That arrives as a
    /// <see cref="TypeLoadException"/> or a <see cref="MissingMemberException"/> rather than a
    /// <see cref="FileNotFoundException"/>, and names no assembly, so it is answered by asking what
    /// is actually installed rather than by reading the exception.
    /// </summary>
    [Theory]
    [InlineData("javascript", "Aspire.Hosting.JavaScript", "13.5.2")]
    [InlineData("java", "CommunityToolkit.Aspire.Hosting.Java", "13.3.0")]
    public void PackagePresentButOlderThanTheFloor_IsReportedAsTooOld(
        string kind, string packageId, string floor)
    {
        // Old for the package this kind needs, comfortably new for the other.
        var message = GuestLanguagePackages.DescribeMissingPackage(
            new TypeLoadException("could not load type 'Whatever'"),
            "frontend",
            kind,
            name => name == packageId ? new Version(13, 0, 0) : new Version(99, 0, 0));

        Assert.NotNull(message);
        Assert.Contains(packageId, message, StringComparison.Ordinal);
        Assert.Contains(floor, message, StringComparison.Ordinal);
        Assert.Contains("13.0.0", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failing service's kind decides which package can possibly be to blame. A
    /// <c>kind: javascript</c> service cannot be failing because the java package is old — it never
    /// touches it — and a sentence that names one while quoting the other is worse than the generic
    /// message, because it sends the reader to change something irrelevant.
    /// </summary>
    [Theory]
    [InlineData("javascript", "CommunityToolkit.Aspire.Hosting.Java")]
    [InlineData("java", "Aspire.Hosting.JavaScript")]
    public void OldPackageBelongingToAnotherKind_IsNotBlamed(string kind, string unrelatedPackage)
    {
        var message = GuestLanguagePackages.DescribeMissingPackage(
            new TypeLoadException("could not load type 'Whatever'"),
            "frontend",
            kind,
            name => name == unrelatedPackage ? new Version(1, 0, 0) : new Version(99, 0, 0));

        Assert.Null(message);
    }

    /// <summary>
    /// A kind registered by someone else through <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/>
    /// reaches whatever packages its own author chose, none of which this type knows the floors for.
    /// </summary>
    [Fact]
    public void KindThisPackageDoesNotOwn_IsNeverAttributedToOneOfThesePackages()
    {
        var message = GuestLanguagePackages.DescribeMissingPackage(
            new TypeLoadException("could not load type 'Whatever'"),
            "frontend",
            "rust",
            _ => new Version(1, 0, 0));

        Assert.Null(message);
    }

    [Fact]
    public void MissingMemberFailure_IsAlsoAnsweredByWhatIsInstalled()
    {
        var message = GuestLanguagePackages.DescribeMissingPackage(
            new MissingMethodException("Method not found: 'Void Whatever.Missing()'"),
            "frontend",
            "javascript",
            _ => new Version(13, 4, 6));

        Assert.NotNull(message);
        Assert.Contains("13.4.6", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every package being present and new enough means the handler broke for its own reasons, even
    /// though the exception type is one a version mismatch also produces.
    /// </summary>
    [Fact]
    public void LoadMismatchWithEveryPackageNewEnough_KeepsTheGenericMessage()
    {
        var message = GuestLanguagePackages.DescribeMissingPackage(
            new TypeLoadException("could not load type 'Whatever'"),
            "frontend",
            "javascript",
            _ => new Version(99, 0, 0));

        Assert.Null(message);
    }

    /// <summary>
    /// An exception type a version mismatch does not produce must not be attributed to one, however
    /// old the installed package happens to be - otherwise an unrelated bug in a handler is reported
    /// as a packaging problem.
    /// </summary>
    [Fact]
    public void OrdinaryHandlerFailure_IsNotAttributedToAnOldPackage()
    {
        var message = GuestLanguagePackages.DescribeMissingPackage(
            new InvalidOperationException("the handler is broken"),
            "frontend",
            "javascript",
            _ => new Version(13, 0, 0));

        Assert.Null(message);
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
    private sealed class AssemblyMissingKind(string assemblyName, bool fromValidate = false) : ILocalResourceKind
    {
        public void Validate(string serviceName, string repoRoot, object? rawConfig)
        {
            if (fromValidate)
            {
                throw LoadFailure();
            }
        }

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw LoadFailure();

        private FileNotFoundException LoadFailure() =>
            new(
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
