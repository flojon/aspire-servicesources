using System.Reflection;
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

    /// <summary>
    /// Property names that legitimately differ between <see cref="ServiceMetadata"/> and
    /// <see cref="ServiceDefinition"/> — everything else is expected to match by name and CLR type.
    /// Add an entry here only for a deliberate rename; anything not listed and not matching by name
    /// fails the drift guard below.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedProperties = new()
    {
        ["KindConfig"] = "KindOptions",
    };

    /// <summary>
    /// Reflection-based drift guard (design's Testing section): a property added to
    /// <see cref="ServiceMetadata"/> and forgotten in <see cref="ServiceMetadata.ToDefinition"/>
    /// would otherwise go unnoticed until a yaml-only value silently failed to reach
    /// <see cref="ServiceDefinition"/> downstream.
    /// </summary>
    [Fact]
    public void ServiceMetadataProperties_AllHaveMatchingServiceDefinitionProperty()
    {
        var definitionProperties = typeof(ServiceDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);

        foreach (var metadataProperty in typeof(ServiceMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var expectedName = RenamedProperties.GetValueOrDefault(metadataProperty.Name, metadataProperty.Name);

            Assert.True(
                definitionProperties.TryGetValue(expectedName, out var definitionProperty),
                $"ServiceMetadata.{metadataProperty.Name} has no matching ServiceDefinition.{expectedName}. " +
                "Add it to ServiceDefinition and thread it through ServiceMetadata.ToDefinition(), or add a " +
                "rename entry to RenamedProperties above if this is a deliberate rename.");

            Assert.Equal(metadataProperty.PropertyType, definitionProperty!.PropertyType);
        }
    }

    /// <summary>
    /// Value-level companion to the structural guard above: every property on a fully-populated
    /// <see cref="ServiceMetadata"/> must actually reach <see cref="ServiceDefinition"/> through
    /// <see cref="ServiceMetadata.ToDefinition"/>, reflection-driven so a newly added property is
    /// covered automatically rather than requiring a hand-written assertion.
    /// </summary>
    [Fact]
    public void ToDefinition_CopiesEveryPropertyValue_ReflectionDriven()
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

        var definitionProperties = typeof(ServiceDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);

        foreach (var metadataProperty in typeof(ServiceMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var expectedName = RenamedProperties.GetValueOrDefault(metadataProperty.Name, metadataProperty.Name);
            var definitionProperty = definitionProperties[expectedName];

            var metadataValue = metadataProperty.GetValue(metadata);
            var definitionValue = definitionProperty.GetValue(definition);

            Assert.True(
                ReferenceEquals(metadataValue, definitionValue) || Equals(metadataValue, definitionValue),
                $"ServiceMetadata.{metadataProperty.Name}'s value did not carry through " +
                $"ToDefinition() to ServiceDefinition.{expectedName}.");
        }
    }
}
