using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// What core hands <see cref="ILocalResourceKind.Validate"/> and when it calls it. Exercised
/// through a stand-in kind rather than through the java or javascript ones, so these stay
/// statements about core rather than about either handler.
/// </summary>
public class LocalKindValidationTests
{
    private const string KindName = "stand-in";
    private const string ServiceName = "frontend";

    /// <summary>
    /// Records what each call was handed, so the order and the arguments can be asserted from
    /// outside. Every checkout-relative check a kind makes needs a <c>repoRoot</c> that is really
    /// there, which is the whole point of these tests.
    /// </summary>
    private sealed class RecordingKind(bool rejectFromValidate = false, bool faultFromValidate = false)
        : ILocalResourceKind
    {
        public List<string> Calls { get; } = [];

        public string? ValidateRepoRoot { get; private set; }

        public string? ResolveRepoRoot { get; private set; }

        public void Validate(string serviceName, string repoRoot, object? rawConfig)
        {
            Calls.Add(nameof(Validate));
            ValidateRepoRoot = repoRoot;

            if (rejectFromValidate)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': '{repoRoot}' does not hold what this kind needs.");
            }

            if (faultFromValidate)
            {
                throw new InvalidOperationException("the handler is broken");
            }
        }

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            Calls.Add(nameof(Resolve));
            ResolveRepoRoot = repoRoot;

            return builder
                .AddResource(new StandInResource(serviceName, repoRoot))
                .WithHttpEndpoint(targetPort: 3000);
        }
    }

    [Fact]
    public void Validate_IsHandedTheCheckoutResolveGets()
    {
        var checkout = Directory.CreateTempSubdirectory("servicesources-kind-validate-").FullName;
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);
        var kind = new RecordingKind();
        builder.AddLocalKind(KindName, kind);

        new LocalProjectSource(new UnusedGitClient())
            .Resolve(builder, ServiceName, Metadata(), DevConfig(checkout));

        // The same directory, and one that exists — without both, a kind still cannot check that a
        // path its options block names is actually in the repository.
        Assert.Equal(kind.ResolveRepoRoot, kind.ValidateRepoRoot);
        Assert.True(Directory.Exists(kind.ValidateRepoRoot));
    }

    [Fact]
    public void Validate_RunsImmediatelyBeforeResolve()
    {
        var checkout = Directory.CreateTempSubdirectory("servicesources-kind-validate-").FullName;
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);
        var kind = new RecordingKind();
        builder.AddLocalKind(KindName, kind);

        new LocalProjectSource(new UnusedGitClient())
            .Resolve(builder, ServiceName, Metadata(), DevConfig(checkout));

        Assert.Equal([nameof(ILocalResourceKind.Validate), nameof(ILocalResourceKind.Resolve)], kind.Calls);
    }

    [Fact]
    public void ValidateThatRejects_KeepsTheServiceOutOfTheAppModel()
    {
        var checkout = Directory.CreateTempSubdirectory("servicesources-kind-validate-").FullName;
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);
        var kind = new RecordingKind(rejectFromValidate: true);
        builder.AddLocalKind(KindName, kind);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata(), DevConfig(checkout)));

        // The ordering that makes reporting a checkout-relative problem from Validate worth the
        // signature: the handler is never asked to build anything, so nothing of this service is in
        // the app model to leave behind.
        Assert.Contains(ServiceName, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ILocalResourceKind.Resolve), kind.Calls);
        Assert.DoesNotContain(builder.Resources, r => r.Name == ServiceName);
    }

    /// <summary>
    /// Reported as it was thrown, rather than through the "the handler failed while creating its
    /// resource" wrapper: that message tells a handler author to move the check into
    /// <see cref="ILocalResourceKind.Validate"/>, which is unusable advice for a check that is
    /// already there.
    /// </summary>
    [Fact]
    public void ValidateThatRejects_IsNotWrappedInTheHandlerFailedMessage()
    {
        var checkout = Directory.CreateTempSubdirectory("servicesources-kind-validate-").FullName;
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);
        builder.AddLocalKind(KindName, new RecordingKind(rejectFromValidate: true));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata(), DevConfig(checkout)));

        Assert.Contains(checkout, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed while creating", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// The other half of that, and the reason this call is wrapped at all: <c>Validate</c> now
    /// resolves the whole options block and makes every check a kind has against the working tree,
    /// so it faults for the same reasons <see cref="ILocalResourceKind.Resolve"/> does. Core names
    /// the service and the kind either way — an unwrapped call here would report the identical fault
    /// as a bare exception out of <c>AddService()</c> depending only on which method it came from.
    /// </summary>
    [Fact]
    public void ValidateThatFaults_IsReportedAgainstTheServiceAndTheKind()
    {
        var checkout = Directory.CreateTempSubdirectory("servicesources-kind-validate-").FullName;
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);
        builder.AddLocalKind(KindName, new RecordingKind(faultFromValidate: true));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new UnusedGitClient())
                .Resolve(builder, ServiceName, Metadata(), DevConfig(checkout)));

        Assert.Contains(ServiceName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(KindName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ILocalResourceKind.Validate), ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private sealed class StandInResource(string name, string workingDirectory)
        : ExecutableResource(name, "run", workingDirectory), IResourceWithServiceDiscovery;

    private static ServiceMetadata Metadata() => new()
    {
        Repository = "https://example.com/frontend.git",
        Kind = KindName,
    };

    private static ServiceDeveloperConfig DevConfig(string path) =>
        new() { Source = "local", Local = new() { Path = path } };

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
