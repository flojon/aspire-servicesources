using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
public class ServiceSourceTests
{
    private sealed class RecordingSource(bool checkThrows = false) : ServiceSource<int>
    {
        public List<string> Calls { get; } = [];

        public SourceInput? CheckedInput { get; private set; }

        public int? BuiltWith { get; private set; }

        public override bool IsSwitchRemedy => false;

        protected override IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition)
        {
            Calls.Add("fields");
            return [new FollowUpField("url:url", "Service URL", true, InputType.Text)];
        }

        protected override int Check(SourceInput input)
        {
            Calls.Add("check");
            CheckedInput = input;

            return checkThrows
                ? throw ServiceSourcesConfigurationException.For($"not ready").AsMissingSetting("url:url")
                : 42;
        }

        protected override IResourceBuilder<ServiceResource> Build(
            IDistributedApplicationBuilder builder, SourceInput input, int checkedValues)
        {
            Calls.Add("build");
            BuiltWith = checkedValues;
            return null!;
        }
    }

    private static ServiceDefinition Definition() =>
        new ServiceMetadata { Repository = "https://github.com/company/orders", Project = "Orders.csproj" }
            .ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static IDistributedApplicationBuilder Builder(string? directory = null) =>
        TestHelpers.CreateBuilder(directory ?? TempDirectories.CreateSubdirectory().FullName);

    private static SourceInput Input() => new("orders", Definition(), new ServiceDeveloperConfig(), "apphost");

    [Fact]
    public void Resolve_RunsCheckOnce_ThenBuildWithTheCheckedValue()
    {
        var source = new RecordingSource();

        source.Resolve(Builder(), "orders", Definition(), new ServiceDeveloperConfig());

        Assert.Equal(["check", "build"], source.Calls);
        Assert.Equal(42, source.BuiltWith);
    }

    [Fact]
    public void Resolve_WhenCheckThrows_NeverCallsBuild()
    {
        var source = new RecordingSource(checkThrows: true);

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            source.Resolve(Builder(), "orders", Definition(), new ServiceDeveloperConfig()));

        Assert.Equal(["check"], source.Calls);
    }

    [Fact]
    public void Requirements_RunsCheck_NeverBuild()
    {
        var source = new RecordingSource();

        source.Requirements(Input());

        Assert.DoesNotContain("build", source.Calls);
        Assert.Contains("check", source.Calls);
    }

    [Fact]
    public void Requirements_CarriesFieldsForThisDefinition_AndTheCheckProblem()
    {
        var requirements = new RecordingSource(checkThrows: true).Requirements(Input());

        Assert.Equal("url:url", Assert.Single(requirements.Fields).Key);
        Assert.Equal("not ready", requirements.Problem?.Message);
        Assert.Equal("url:url", requirements.Problem?.MissingSetting);
        Assert.True(requirements.Offered);
    }

    [Fact]
    public void Resolve_PassesTheRepositoryConfigAndAppHostDirectoryIntoTheInput()
    {
        var directory = TempDirectories.CreateSubdirectory().FullName;
        var repositoryConfig = new RepositoryDeveloperConfig();
        var config = new ServiceDeveloperConfig();
        var source = new RecordingSource();

        source.Resolve(Builder(directory), "orders", Definition(), config, repositoryConfig);

        var input = source.CheckedInput!;
        Assert.Equal("orders", input.ServiceName);
        Assert.Same(config, input.Config);
        Assert.Same(repositoryConfig, input.RepositoryConfig);
        Assert.Equal(directory, input.AppHostDirectory);
    }
}
