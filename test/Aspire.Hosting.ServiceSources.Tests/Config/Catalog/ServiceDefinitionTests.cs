using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config.Catalog;

public class CatalogOriginTests
{
    [Fact]
    public void Code_DescribesAsCode()
    {
        Assert.Equal("code (AddServiceCatalog)", CatalogOrigin.Code.Describe());
    }

    [Fact]
    public void FromYaml_DescribesWithQuotedPath()
    {
        var origin = CatalogOrigin.FromYaml("/apphost/servicesources.yaml");

        Assert.Equal("'/apphost/servicesources.yaml'", origin.Describe());
    }

    [Fact]
    public void Code_IsNotEqualToYaml()
    {
        Assert.NotEqual(CatalogOrigin.Code, CatalogOrigin.FromYaml("/x/servicesources.yaml"));
    }
}

public class ServiceDefinitionTests
{
    [Fact]
    public void ToDefinition_CopiesEveryServiceMetadataProperty()
    {
        var metadata = new ServiceMetadata
        {
            Repository = "https://github.com/example/repo",
            Project = "src/Api/Api.csproj",
            DefaultRef = "main",
            Kubernetes = new KubernetesMetadata { Service = "svc", Port = 8080, Scheme = "https" },
            Url = new UrlMetadata { Url = "https://example.com" },
            Container = new ContainerMetadata { Image = "nginx", Port = 80, DefaultTag = "latest", Scheme = "http" },
            Prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "once" },
            Kind = "java",
            KindConfig = new Dictionary<object, object> { ["mavenGoal"] = "spring-boot:run" },
        };

        var definition = metadata.ToDefinition("/apphost/servicesources.yaml");

        Assert.Equal(metadata.Repository, definition.Repository);
        Assert.Equal(metadata.Project, definition.Project);
        Assert.Equal(metadata.DefaultRef, definition.DefaultRef);
        Assert.Same(metadata.Kubernetes, definition.Kubernetes);
        Assert.Same(metadata.Url, definition.Url);
        Assert.Same(metadata.Container, definition.Container);
        Assert.Same(metadata.Prepare, definition.Prepare);
        Assert.Equal(metadata.Kind, definition.Kind);
        Assert.Same(metadata.KindConfig, definition.KindOptions);
        Assert.Equal(CatalogOrigin.FromYaml("/apphost/servicesources.yaml"), definition.Origin);
    }
}
