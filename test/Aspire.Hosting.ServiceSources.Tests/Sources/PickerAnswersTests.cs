using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class PickerAnswersTests
{
    private static ServiceDefinition Definition() =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = new KubernetesMetadata { Service = "orders", Port = 80 },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> Fields(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Validate_SourceNotOffered_Rejects()
    {
        var error = PickerAnswers.Validate("orders", "container", NoFields, Definition());

        Assert.NotNull(error);
        Assert.Contains("container", error);
    }

    [Fact]
    public void Validate_MissingRequiredField_Rejects()
    {
        var error = PickerAnswers.Validate("orders", "kubernetes", Fields(("kubernetes:context", "  ")), Definition());

        Assert.NotNull(error);
        Assert.Contains("kubernetes:context", error);
    }

    [Fact]
    public void Validate_InvalidField_ReportsValidatorMessage()
    {
        var error = PickerAnswers.Validate("orders", "kubernetes", Fields(("kubernetes:context", " dev ")), Definition());

        Assert.NotNull(error);
        Assert.Contains("context", error);
    }

    [Fact]
    public void Validate_Valid_ReturnsNull()
    {
        Assert.Null(PickerAnswers.Validate("orders", "url", Fields(("url:url", "http://localhost:5000")), Definition()));
        Assert.Null(PickerAnswers.Validate("orders", "repository", NoFields, Definition()));
        Assert.Null(PickerAnswers.Validate("orders", "Kubernetes", Fields(("kubernetes:context", "dev")), Definition()));
    }

    private static IConfigurationRoot Root(params IDictionary<string, string?>[] layers)
    {
        var builder = new ConfigurationBuilder();
        foreach (var layer in layers)
        {
            builder.AddInMemoryCollection(layer);
        }

        return builder.Build();
    }

    private static Dictionary<string, string?> Layer(string key, string? value) => new() { [key] = value };

    [Fact]
    public void ShadowingProvider_BlankInHigherLayer_NamesIt()
    {
        var root = Root(
            Layer("ServiceSources:Services:orders:source", "container"),
            Layer("ServiceSources:Services:orders:source", ""));

        var name = PickerAnswers.ShadowingProvider(root, "orders");

        Assert.NotNull(name);
        Assert.Contains("Memory", name);
    }

    [Fact]
    public void ShadowingProvider_NoBlank_ReturnsNull()
    {
        var root = Root(
            Layer("ServiceSources:Services:orders:source", "container"),
            Layer("ServiceSources:Services:orders:source", "url"),
            Layer("ServiceSources:Services:billing:source", ""));

        Assert.Null(PickerAnswers.ShadowingProvider(root, "orders"));
    }

    [Fact]
    public void ShadowingProvider_BlankOnlyInFileLayer_ReturnsNull()
    {
        var root = Root(Layer("ServiceSources:Services:orders:source", ""));

        Assert.Null(PickerAnswers.ShadowingProvider(root, "orders"));
    }

    [Fact]
    public void ShadowingProvider_ServiceNameCaseInsensitive()
    {
        var root = Root(
            Layer("ServiceSources:Services:orders:source", "container"),
            Layer("servicesources:services:ORDERS:source", "  "));

        Assert.NotNull(PickerAnswers.ShadowingProvider(root, "Orders"));
    }
}
