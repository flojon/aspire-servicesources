using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class ServiceDeveloperConfigValidatorTests
{
    private const string ServiceName = "orders";

    private static readonly Dictionary<string, IReadOnlySet<string>> RelevantFieldsBySource = new()
    {
        ["local"] = new HashSet<string> { "path", "ref" },
        ["kubernetes"] = new HashSet<string> { "context", "namespace", "port" },
        ["container"] = new HashSet<string> { "tag" },
        ["url"] = new HashSet<string> { "url" },
    };

    [Theory]
    [InlineData("local", "path", "/checkout")]
    [InlineData("local", "ref", "main")]
    public void Validate_LocalSourceWithRelevantField_DoesNotThrow(string source, string field, string value)
    {
        var config = ConfigWith(source, field, value);

        ServiceDeveloperConfigValidator.Validate(ServiceName, source, RelevantFieldsBySource[source], config);
    }

    [Theory]
    [InlineData("kubernetes", "context", "dev-west")]
    [InlineData("kubernetes", "namespace", "orders-ns")]
    public void Validate_KubernetesSourceWithRelevantField_DoesNotThrow(string source, string field, string value)
    {
        var config = ConfigWith(source, field, value);

        ServiceDeveloperConfigValidator.Validate(ServiceName, source, RelevantFieldsBySource[source], config);
    }

    [Fact]
    public void Validate_KubernetesSourceWithPort_DoesNotThrow()
    {
        var config = new ServiceDeveloperConfig { Source = "kubernetes", Port = 8080 };

        ServiceDeveloperConfigValidator.Validate(ServiceName, "kubernetes", RelevantFieldsBySource["kubernetes"], config);
    }

    [Fact]
    public void Validate_ContainerSourceWithTag_DoesNotThrow()
    {
        var config = new ServiceDeveloperConfig { Source = "container", Tag = "v1.4.2" };

        ServiceDeveloperConfigValidator.Validate(ServiceName, "container", RelevantFieldsBySource["container"], config);
    }

    [Fact]
    public void Validate_UrlSourceWithUrl_DoesNotThrow()
    {
        var config = new ServiceDeveloperConfig { Source = "url", Url = "https://orders.dev.internal" };

        ServiceDeveloperConfigValidator.Validate(ServiceName, "url", RelevantFieldsBySource["url"], config);
    }

    [Fact]
    public void Validate_NoOptionalFieldsSet_DoesNotThrow()
    {
        var config = new ServiceDeveloperConfig { Source = "local" };

        ServiceDeveloperConfigValidator.Validate(ServiceName, "local", RelevantFieldsBySource["local"], config);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("namespace")]
    [InlineData("port")]
    [InlineData("url")]
    [InlineData("tag")]
    public void Validate_LocalSourceWithForeignField_ThrowsNamingServiceFieldAndSource(string field)
    {
        var config = ConfigWith("local", field, ForeignValue(field));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ServiceDeveloperConfigValidator.Validate(ServiceName, "local", RelevantFieldsBySource["local"], config));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(field, ex.Message);
        Assert.Contains("local", ex.Message);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("ref")]
    [InlineData("url")]
    [InlineData("tag")]
    public void Validate_KubernetesSourceWithForeignField_ThrowsNamingServiceFieldAndSource(string field)
    {
        var config = ConfigWith("kubernetes", field, ForeignValue(field));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ServiceDeveloperConfigValidator.Validate(ServiceName, "kubernetes", RelevantFieldsBySource["kubernetes"], config));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(field, ex.Message);
        Assert.Contains("kubernetes", ex.Message);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("ref")]
    [InlineData("context")]
    [InlineData("namespace")]
    [InlineData("port")]
    [InlineData("url")]
    public void Validate_ContainerSourceWithForeignField_ThrowsNamingServiceFieldAndSource(string field)
    {
        var config = ConfigWith("container", field, ForeignValue(field));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ServiceDeveloperConfigValidator.Validate(ServiceName, "container", RelevantFieldsBySource["container"], config));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(field, ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("ref")]
    [InlineData("context")]
    [InlineData("namespace")]
    [InlineData("port")]
    [InlineData("tag")]
    public void Validate_UrlSourceWithForeignField_ThrowsNamingServiceFieldAndSource(string field)
    {
        var config = ConfigWith("url", field, ForeignValue(field));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ServiceDeveloperConfigValidator.Validate(ServiceName, "url", RelevantFieldsBySource["url"], config));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(field, ex.Message);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void Validate_MultipleForeignFields_ThrowsListingAllOfThem()
    {
        var config = new ServiceDeveloperConfig { Source = "local", Context = "dev-west", Port = 8080, Tag = "v1" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ServiceDeveloperConfigValidator.Validate(ServiceName, "local", RelevantFieldsBySource["local"], config));

        Assert.Contains("context", ex.Message);
        Assert.Contains("port", ex.Message);
        Assert.Contains("tag", ex.Message);
    }

    private static ServiceDeveloperConfig ConfigWith(string source, string field, string value)
    {
        var config = new ServiceDeveloperConfig { Source = source };

        switch (field)
        {
            case "path": config.Path = value; break;
            case "ref": config.Ref = value; break;
            case "context": config.Context = value; break;
            case "namespace": config.Namespace = value; break;
            case "port": config.Port = int.Parse(value); break;
            case "url": config.Url = value; break;
            case "tag": config.Tag = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        return config;
    }

    private static string ForeignValue(string field) => field == "port" ? "8080" : "some-value";
}
