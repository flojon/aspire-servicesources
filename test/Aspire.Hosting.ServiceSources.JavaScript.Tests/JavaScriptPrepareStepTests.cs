using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using static Aspire.Hosting.ServiceSources.JavaScript.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// The <c>javascript</c> kind against a checkout whose <c>package.json</c> a <c>prepare</c> step
/// generates — the case #118 lists alongside the jar, and the one the design has in mind when it
/// says a step is entitled to produce the input the installer reads.
/// </summary>
/// <remarks>
/// Complementary to the kind's own install step rather than a replacement for it: a service that
/// declares no <c>prepare</c> block gets its dependencies installed exactly as before, which is what
/// #164 settled before this landed.
/// </remarks>
public class JavaScriptPrepareStepTests
{
    private const string ServiceName = "frontend";

    /// <summary>
    /// Clones a checkout holding a generator and no <c>package.json</c> — the shape a repository
    /// that produces its manifest is in.
    /// </summary>
    private sealed class FakeGitClient : IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
            File.WriteAllText(Path.Combine(destinationPath, "prepare.sh"), "#!/bin/sh\n");
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

        public string? GetHeadCommitSha(string repositoryPath) =>
            "1111111111111111111111111111111111111111";
    }

    /// <summary>Writes the manifest and the entry point the kind is about to look for.</summary>
    private sealed class FakePrepareRunner : IPrepareCommandRunner
    {
        public int Runs { get; private set; }

        public int Run(string workingDirectory, IReadOnlyList<string> command, Action<string> onLine)
        {
            Runs++;

            onLine("generating package.json");
            File.WriteAllText(
                Path.Combine(workingDirectory, "package.json"),
                """{ "name": "frontend", "scripts": { "dev": "node server.js" } }""");
            File.WriteAllText(Path.Combine(workingDirectory, "server.js"), "");

            return 0;
        }
    }

    private static string CreateAppHostDirectory()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            $"services:\n  {ServiceName}:\n    repository: https://example.com/frontend.git\n"
            + "    kind: javascript\n");

        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $"{{ \"services\": {{ \"{ServiceName}\": {{ \"source\": \"local\" }} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(PrepareMetadata? prepare) =>
        new()
        {
            Repository = "https://example.com/frontend.git",
            Kind = "javascript",
            Prepare = prepare,
            KindConfig = ParseOptionsBlock(
                """
                appType: javascript
                runScript: dev
                port: 3000
                """),
        };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "local", Local = new() };

    /// <remarks>
    /// An <c>appType</c> that runs a <c>package.json</c> script is what makes the kind demand the
    /// manifest — there is nothing to run a script from without one — so this is the check the step
    /// has to precede.
    /// </remarks>
    [Fact]
    public void WithNoStep_TheKindRejectsACheckoutWithNoManifest()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        builder.UseJavaScript();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(new FakeGitClient(), new FakePrepareRunner())
                .Resolve(builder, ServiceName, Metadata(prepare: null), DevConfig()));

        Assert.Contains("package.json", ex.Message);
    }

    [Fact]
    public void WithAStepThatGeneratesIt_TheSameServiceResolves()
    {
        var dir = CreateAppHostDirectory();
        var builder = CreateBuilder(dir);
        builder.UseJavaScript();

        var runner = new FakePrepareRunner();

        var service = new LocalProjectSource(new FakeGitClient(), runner).Resolve(
            builder, ServiceName, Metadata(new PrepareMetadata { Command = ["./prepare.sh"] }), DevConfig());

        Assert.Equal(1, runner.Runs);
        Assert.Equal(ServiceName, service.Resource.Name);
        Assert.True(File.Exists(
            Path.Combine(dir, ".servicesources", "checkouts", ServiceName, "package.json")));
    }
}
