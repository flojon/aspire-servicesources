using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceRequirementsTests
{
    private static ServiceDefinition Definition(
        string? path = null, string? url = null, string? image = null, int? containerPort = null,
        string? containerScheme = null, string? kubernetesService = null, int? kubernetesPort = null,
        string? kubernetesScheme = null, string? repository = "https://github.com/company/orders") =>
        new ServiceMetadata
        {
            Repository = repository ?? "",
            Project = "Orders.csproj",
            Path = path,
            Url = url is null ? null : new UrlMetadata { Url = url },
            Container = image is null ? null : new ContainerMetadata { Image = image, Port = containerPort, Scheme = containerScheme },
            Kubernetes = kubernetesService is null
                ? null
                : new KubernetesMetadata { Service = kubernetesService, Port = kubernetesPort, Scheme = kubernetesScheme },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static SourceInput Input(ServiceDefinition definition, ServiceDeveloperConfig? config = null, string appHost = "apphost") =>
        new("orders", definition, config ?? new ServiceDeveloperConfig(), appHost);

    private static (string Key, bool Required)[] Keys(SourceRequirements requirements) =>
        [.. requirements.Fields.Select(f => (f.Key, f.Required))];

    [Fact]
    public void Disabled_RequirementsHaveNoFields_AndResolve()
    {
        var requirements = new DisabledSource().Requirements(Input(Definition()));

        Assert.Empty(requirements.Fields);
        Assert.True(requirements.Resolves);
    }

    [Fact]
    public void Disabled_IsNotASwitchRemedy() =>
        Assert.False(new DisabledSource().IsSwitchRemedy);
}
