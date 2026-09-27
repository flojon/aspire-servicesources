using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.ServiceSources.JavaScript.Tests.TestHelpers;
using Xunit;

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
[Trait("IO", "true")]
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

        public int Run(
            string workingDirectory,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken,
            Action<string> onLine)
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
        var dir = TempDirectories.CreateSubdirectory().FullName;

        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            $"services:\n  {ServiceName}:\n    repository: https://example.com/frontend.git\n"
            + "    kind: javascript\n");

        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $"{{ \"services\": {{ \"{ServiceName}\": {{ \"source\": \"local\" }} }} }}");

        return dir;
    }

    private static ServiceDefinition Definition(PrepareMetadata? prepare) =>
        new ServiceMetadata
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
        }.ToDefinition("servicesources.yaml", ServiceName, TestHelpers.EmptyRepositories);

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
        builder.SetCheckoutTiming(CheckoutTiming.Eager);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(new FakeGitClient(), new FakePrepareRunner())
                .Resolve(builder, ServiceName, Definition(prepare: null), DevConfig()));

        Assert.Contains("package.json", ex.Message);
    }

    /// <summary>
    /// #216: deferred is the default now, and the built-in javascript kind already supported it
    /// before that flip — so a cold checkout with a prepare step, resolved with no call at all, has
    /// to run the same bootstrap the eager path does, just moved past <c>BeforeStartEvent</c>.
    /// </summary>
    [Fact]
    public async Task Deferred_WithAStepThatGeneratesIt_ResolvesAfterTheClone()
    {
        var dir = CreateAppHostDirectory();
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseJavaScript();

        var runner = new FakePrepareRunner();

        var service = new LocalProjectSource(new FakeGitClient(), runner).Resolve(
            builder, ServiceName, Definition(new PrepareMetadata { Command = ["./prepare.sh"] }), DevConfig());

        // Registered stopped immediately: nothing has cloned or run yet.
        Assert.Equal(0, runner.Runs);

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        // Stands in for DCP, which publishes NotStarted when it withholds an explicit-start
        // resource — the state each deferred task waits for before it touches the resource. The
        // javascript kind holds back its own "npm install" installer resource alongside the app, so
        // both need it, not only the resource this call returned.
        foreach (var withheld in builder.Resources.Where(r => r.Annotations.OfType<ExplicitStartupAnnotation>().Any()))
        {
            await PublishNotStartedAsync(services, withheld);
        }

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, runner.Runs);
        Assert.Equal(ServiceName, service.Resource.Name);
        Assert.True(File.Exists(
            Path.Combine(dir, ".servicesources", "checkouts", ServiceName, "package.json")));
    }

    private static Task PublishNotStartedAsync(IServiceProvider services, IResource resource) =>
        services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, null),
            });

    [Fact]
    public void WithAStepThatGeneratesIt_TheSameServiceResolves()
    {
        var dir = CreateAppHostDirectory();
        var builder = CreateBuilder(dir);
        builder.UseJavaScript();
        builder.SetCheckoutTiming(CheckoutTiming.Eager);

        var runner = new FakePrepareRunner();

        var service = new LocalProjectSource(new FakeGitClient(), runner).Resolve(
            builder, ServiceName, Definition(new PrepareMetadata { Command = ["./prepare.sh"] }), DevConfig());

        Assert.Equal(1, runner.Runs);
        Assert.Equal(ServiceName, service.Resource.Name);
        Assert.True(File.Exists(
            Path.Combine(dir, ".servicesources", "checkouts", ServiceName, "package.json")));
    }
}
