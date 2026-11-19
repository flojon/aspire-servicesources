using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceProbeTests
{
    private sealed class ThrowingSource(Exception exception) : IServiceSource
    {
        public bool IsSwitchRemedy => true;

        public SourceRequirements Requirements(SourceInput input) => throw exception;

        public IResourceBuilder<ServiceResource> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
            ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null) =>
            throw new NotSupportedException();
    }

    private sealed class RefusingSource : ServiceSource<NoValues>
    {
        public override bool IsSwitchRemedy => false;

        protected override IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition) => [];

        protected override NoValues Check(SourceInput input) =>
            throw ServiceSourcesConfigurationException.For($"refused");

        protected override IResourceBuilder<ServiceResource> Build(
            IDistributedApplicationBuilder builder, SourceInput input, NoValues checkedValues) =>
            throw new NotSupportedException();
    }

    private static SourceInput Input() =>
        new("orders", new ServiceMetadata { Repository = "https://github.com/company/orders", Project = "Orders.csproj" }
            .ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories), new ServiceDeveloperConfig(), "");

    [Fact]
    public void Evaluate_ReturnsTheSourcesRequirements()
    {
        var requirements = SourceProbe.Evaluate(new DisabledSource(), Input());

        Assert.True(requirements.Resolves);
    }

    [Fact]
    public void Evaluate_OnAnyOtherException_ReturnsUnknown()
    {
        var requirements = SourceProbe.Evaluate(new ThrowingSource(new UriFormatException("not a uri")), Input());

        Assert.Same(SourceRequirements.Unknown, requirements);
        Assert.False(requirements.Offered);
    }

    [Fact]
    public void Evaluate_OnAConfigurationException_StillReturnsTheProblem()
    {
        var requirements = SourceProbe.Evaluate(new RefusingSource(), Input());

        Assert.Equal("refused", requirements.Problem?.Message);
    }
}
