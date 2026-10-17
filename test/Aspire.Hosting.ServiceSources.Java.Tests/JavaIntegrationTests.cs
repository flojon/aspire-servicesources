using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// This project references only the Community Toolkit's integration, so it is the AppHost the
/// official package's absence has to leave exactly as it was. The precedence between the two is
/// tested on <see cref="JavaIntegration.Choose"/>, since no one project can reference both.
/// </summary>
public class JavaIntegrationTests
{
    [Fact]
    public void Installed_WithoutTheOfficialPackage_IsTheCommunityToolkits()
    {
        Assert.IsType<CommunityToolkitJavaIntegration>(JavaIntegration.Installed);
        Assert.Equal(typeof(JavaAppExecutableResource), new JavaLocalResourceKind().ResourceType);
    }

    [Fact]
    public void OfficialIntegration_NotInstalled_IsNotLoaded() =>
        Assert.Null(OfficialJavaIntegration.TryLoad());

    [Fact]
    public void Choose_BothInstalled_IsTheCommunityToolkits()
    {
        var toolkit = new CommunityToolkitJavaIntegration();

        Assert.Same(toolkit, JavaIntegration.Choose(() => toolkit, () => throw new InvalidOperationException("not probed")));
    }

    [Fact]
    public void Choose_OnlyTheOfficialInstalled_IsTheOfficial()
    {
        var official = new OfficialJavaIntegration();

        Assert.Same(official, JavaIntegration.Choose(() => null, () => official));
    }

    [Fact]
    public void Choose_NeitherInstalled_FallsBackToTheCommunityToolkits() =>
        Assert.IsType<CommunityToolkitJavaIntegration>(JavaIntegration.Choose(() => null, () => null));

    [Theory]
    [InlineData("13.6.1-preview.1.26506.5", true)]
    [InlineData("13.6.1-preview.1.26506.6", false)]
    [InlineData("13.6.1-preview.1.26506.6+abcdef", false)]
    [InlineData("13.6.1-preview.1.26506.10", false)]
    [InlineData("13.6.1-preview.2.1.1", false)]
    [InlineData("13.6.1-preview.1.9", true)]
    [InlineData("13.6.1", false)]
    [InlineData("13.6.0", true)]
    [InlineData("13.6.0-preview.9.99999.9", true)]
    [InlineData("13.7.0-preview.1.1.1", false)]
    [InlineData("not a version", false)]
    [InlineData("13.6.1.0-preview.1.26506.6", false)]   // a fourth component of zero is the same version
    [InlineData("13.6.0.0", true)]
    [InlineData("13.6.1-dev", false)]                    // a label never seen before is not evidence of age
    [InlineData("13.6.1-rc.1", false)]
    [InlineData("13.6.1-preview.1.99999999999999999999999.1", false)]   // longer than a long
    [InlineData("13.6.1-preview.+1.26506.6", false)]
    [InlineData("13.6.1-preview.1.0026506.5", true)]     // leading zeros do not change the number
    public void IsOlder_ComparesLikeNuGet(string installed, bool expected) =>
        Assert.Equal(expected, PackageVersionComparison.IsOlder(installed, "13.6.1-preview.1.26506.6"));

    /// <summary>
    /// A kind that reports a configuration problem from <c>ResourceType</c> - the official package
    /// being too old - cannot say what it declares, so the probe other sources' <c>Unwrap</c> makes
    /// claims nothing instead of failing for services that are not its own.
    /// </summary>
    [Fact]
    public void KindResourceType_HandlerThatReportsAConfigurationProblem_ClaimsNothing()
    {
        var builder = DistributedApplication.CreateBuilder([TestBuilderDefaults.DisableConfigReloadArg]);
        builder.AddLocalKind("unsettled", new UnsettledTypeKind());

        var definition = new ServiceDefinition
        {
            Repository = new RepositoryDefinition { Url = "https://github.com/example/tool", CheckoutName = "tool" },
            Project = "tool",
            Kind = "unsettled",
            Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
        };

        Assert.Equal(
            typeof(IResourceWithServiceDiscovery),
            LocalProjectSource.KindResourceType(builder, "tool", definition));
    }

    /// <summary>
    /// Only a kind that says it is unsettled gets the pass: any other configuration problem a handler
    /// reports from <c>ResourceType</c> is still the handler's to report.
    /// </summary>
    [Fact]
    public void KindResourceType_HandlerThatReportsAnOrdinaryConfigurationProblem_StillThrows()
    {
        var builder = DistributedApplication.CreateBuilder([TestBuilderDefaults.DisableConfigReloadArg]);
        builder.AddLocalKind("broken", new BrokenTypeKind());

        var definition = new ServiceDefinition
        {
            Repository = new RepositoryDefinition { Url = "https://github.com/example/tool", CheckoutName = "tool" },
            Project = "tool",
            Kind = "broken",
            Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
        };

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.KindResourceType(builder, "tool", definition));
    }

    private sealed class BrokenTypeKind : ILocalResourceKind
    {
        public Type ResourceType => throw new ServiceSourcesConfigurationException("misconfigured");

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException();
    }

    private sealed class UnsettledTypeKind : ILocalResourceKind
    {
        public Type ResourceType => throw new ServiceSourcesConfigurationException("too old").AsUnsettledResourceType();

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException();
    }
}
