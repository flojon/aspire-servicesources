using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

/// <summary>
/// Design finding 5: a code-declared service must never see "servicesources.yaml" in an error
/// message. Exercises every reachable failure this package can throw against a code-only catalog and
/// asserts none of them names a file that doesn't exist for that AppHost.
/// </summary>
public class CatalogErrorMessageTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    private static void AssertNoYamlMention(Action act)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(act);
        Assert.DoesNotContain("servicesources.yaml", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The orphaned-developer-config-entry warning (<see cref="Config.ServiceConfigAudit"/>) is not
    /// a <see cref="ServiceSourcesConfigurationException"/> — it's reported through
    /// <c>ServiceSourcesWarnings</c> once the AppHost is composed — so it needs its own case rather
    /// than going through <see cref="AssertNoYamlMention"/> above. A code-only catalog with a
    /// typo'd <c>servicesources.local.json</c> entry used to name 'servicesources.yaml' in this
    /// message even though no such file exists for this AppHost.
    /// </summary>
    [Fact]
    public async Task OrphanedDeveloperConfigEntry_AgainstCodeOnlyCatalog_WarningNamesNoYamlFile()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """
            { "services": {
                "orders": { "source": "url" },
                "odrers": { "source": "url" } } }
            """);
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
        builder.AddService("orders");

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("odrers", warning);
        Assert.DoesNotContain("servicesources.yaml", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceNotDeclared() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
            ServiceSourcesConfigCache.ResolveService(builder, "typo'd-name");
        });

    [Fact]
    public void ContainerMissingImage() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
                """{ "services": { "svc": { "source": "container" } } }""");
            var builder = CreateBuilder(dir);
            // WithContainer requires image and port at the call site, so this exercises the case
            // where the entry declares no container block at all but is resolved as one.
            builder.AddServiceCatalog(c => c.AddService("svc").WithUrl("https://example.com"));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            Aspire.Hosting.ServiceSources.Sources.ContainerSource.ResolveContainerConfig("svc", definition, config);
        });

    [Fact]
    public void KubernetesMissingService() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            // A developer selection is required to reach KubernetesSource at all — an entry with no
            // 'source' fails at ResolveService's own NotConfiguredError first, which never mentions
            // servicesources.yaml either way but also never exercises the code this test targets.
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
                """{ "services": { "svc": { "source": "kubernetes" } } }""");
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("svc").WithUrl("https://example.com"));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            Aspire.Hosting.ServiceSources.Sources.KubernetesSource.BuildPortForwardArgs(
                "svc", definition, config, new Aspire.Hosting.ServiceSources.PortAllocation.SocketPortAllocator(),
                out _, out _);
        });

    [Fact]
    public void UrlNotConfigured() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            // Same reason as KubernetesMissingService above: a source has to be selected in the
            // developer config before UrlSource.ResolveUrl is ever reached.
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
                """{ "services": { "svc": { "source": "url" } } }""");
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("svc").WithContainer("nginx", 80));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            Aspire.Hosting.ServiceSources.Sources.UrlSource.ResolveUrl("svc", definition, config);
        });

    [Fact]
    public void UnsupportedScheme() =>
        AssertNoYamlMention(() =>
        {
            // WithContainer exposes no `scheme` parameter in this stage (Task 5), so a
            // code-declared service can never reach an invalid scheme through the public builder
            // today — construct the ServiceDefinition directly instead. Still a real regression
            // guard: it exercises the message EndpointScheme.Resolve produces for a Code-origin
            // definition, which is exactly what this task's fix has to get right, and it is the
            // test that already covers the day WithContainer (or WithKubernetes) grows a scheme
            // parameter of its own.
            var dir = TempDirectories.CreateSubdirectory().FullName;
            var builder = CreateBuilder(dir);
            var definition = new ServiceDefinition
            {
                Repository = "",
                Project = "",
                Kind = "dotnet",
                Container = new ContainerMetadata { Image = "nginx", Port = 80, Scheme = "ftp" },
                Origin = CatalogOrigin.Code,
            };
            var config = new ServiceDeveloperConfig { Source = "container" };
            new Aspire.Hosting.ServiceSources.Sources.ContainerSource().Resolve(builder, "svc", definition, config);
        });

    [Fact]
    public void KindNotRegistered() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
                """{ "services": { "svc": { "source": "local" } } }""");
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("svc")
                .WithRepository("https://github.com/example/repo")
                .WithKind("nonexistent-kind"));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            // The kind lookup is a pre-flight check that runs before any git or filesystem work
            // (LocalProjectSource.ResolveKindHandler's own doc comment says so, and it runs before
            // the clone). A git client whose every member but the no-op-by-default
            // EnsureAvailable() throws both suffices and doubles as proof that no checkout was
            // attempted before the error fired.
            new Aspire.Hosting.ServiceSources.Sources.LocalProjectSource(new NeverCalledGitClient())
                .Resolve(builder, "svc", definition, config);
        });

    /// <summary>
    /// Every member but the no-op default <see cref="IGitClient.EnsureAvailable"/> throws — used by
    /// <see cref="KindNotRegistered"/>, which must fail before any of them run.
    /// </summary>
    private sealed class NeverCalledGitClient : Aspire.Hosting.ServiceSources.Git.IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, Aspire.Hosting.ServiceSources.Git.IGitProgressSink? progress = null) =>
            throw new NotImplementedException();

        public void Checkout(string repositoryPath, string reference) => throw new NotImplementedException();

        public void Fetch(string repositoryPath) => throw new NotImplementedException();

        public bool HasUncommittedChanges(string repositoryPath) => throw new NotImplementedException();

        public bool IsRefCheckedOut(string repositoryPath, string reference) => throw new NotImplementedException();

        public string? GetOriginUrl(string repositoryPath) => throw new NotImplementedException();
    }
}
