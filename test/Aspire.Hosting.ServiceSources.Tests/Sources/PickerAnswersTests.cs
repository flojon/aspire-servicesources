using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class PickerAnswersTests
{
    private static ServiceDefinition Definition(bool container = false, string? catalogUrl = null) =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = new KubernetesMetadata { Service = "orders", Port = 80 },
            Container = container ? new ContainerMetadata { Image = "ghcr.io/company/orders", Port = 8080 } : null,
            Url = catalogUrl is null ? null : new UrlMetadata { Url = catalogUrl },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    // Never touched: only a 'path' answer reads the AppHost directory, and those tests are in PickerAnswersPathTests.
    private const string AppHost = "unused-apphost";

    // The entry the writer would merge: nested objects for a key such as "kubernetes:context".
    internal static JsonObject Entry(params (string Key, string Value)[] pairs)
    {
        var entry = new JsonObject();

        foreach (var (key, value) in pairs)
        {
            var parts = key.Split(':');
            var node = entry;

            foreach (var part in parts[..^1])
            {
                node = node[part] as JsonObject ?? (JsonObject)(node[part] = new JsonObject());
            }

            node[parts[^1]] = value;
        }

        return entry;
    }

    private static string? Validate(string source, JsonObject entry, ServiceDefinition? definition = null, string service = "orders") =>
        PickerAnswers.Validate(service, source, entry, definition ?? Definition(), AppHost);

    [Fact]
    public void Validate_SourceNotOffered_Rejects()
    {
        var error = Validate("container", Entry());

        Assert.NotNull(error);
        Assert.Contains("container", error);
    }

    [Fact]
    public void Validate_SourceNotOffered_RejectsTheMergedEntryToo()
    {
        var error = Validate("nonexistent", Entry(("source", "nonexistent"), ("url:url", "http://localhost:5000")));

        Assert.NotNull(error);
        Assert.Contains("is not available for service 'orders'", error);
    }

    [Fact]
    public void Validate_MissingRequiredField_Rejects()
    {
        var error = Validate("kubernetes", Entry(("kubernetes:port", "80")));

        Assert.NotNull(error);
        Assert.Contains("'kubernetes.context'", error);
    }

    [Fact]
    public void Validate_InvalidField_ReportsValidatorMessage()
    {
        var error = Validate("kubernetes", Entry(("kubernetes:context", " dev ")));

        Assert.NotNull(error);
        Assert.Contains("context", error);
    }

    [Fact]
    public void Validate_Valid_ReturnsNull()
    {
        Assert.Null(Validate("url", Entry(("url:url", "http://localhost:5000"))));
        Assert.Null(Validate("repository", Entry()));
        Assert.Null(Validate("Kubernetes", Entry(("kubernetes:context", "dev"))));
    }

    [Fact]
    public void Validate_ServiceNameWithColon_StillChecksItsFields()
    {
        Assert.NotNull(Validate("url", Entry(("url:url", "ftp://x")), service: "a:b"));
        Assert.Null(Validate("url", Entry(("url:url", "http://x")), service: "a:b"));
    }

    [Fact]
    public void Validate_SourceInOtherCase_StillRequiresItsFields()
    {
        var error = Validate("Kubernetes", Entry());

        Assert.NotNull(error);
        Assert.Contains("'kubernetes.context'", error);
    }

    [Fact]
    public void Validate_SettingAlreadyInTheFile_SatisfiesTheRequirement() =>
        Assert.Null(Validate("url", Entry(("source", "url"), ("url:url", "http://localhost:5000"))));

    [Fact]
    public void Validate_OfferedContainerAndRepositoryEntries_PassThroughCheck()
    {
        Assert.Null(Validate("container", Entry(("source", "container")), Definition(container: true)));
        Assert.Null(Validate("repository", Entry(("source", "repository"))));
    }

    [Fact]
    public void Validate_ForgedSourceName_IsEscapedInTheMessage()
    {
        var error = Validate("evil\u001b[31m\nforged line", Entry());

        Assert.NotNull(error);
        Assert.DoesNotContain('\u001b', error);
        Assert.DoesNotContain('\n', error);
    }

    [Theory]
    [InlineData("localhost:5000", "must use the http or https scheme")]
    [InlineData("ftp://orders.example.com", "must use the http or https scheme")]
    [InlineData("orders.example.com", "is not a valid absolute URL")]
    public void Validate_UrlThatWouldNotResolve_ReportsResolveMessage(string url, string expected)
    {
        var error = Validate("url", Entry(("url:url", url)));

        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.DoesNotContain(" at ", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    public void Validate_KubernetesPortOutOfRange_ReportsResolveMessage(string port)
    {
        var error = Validate("kubernetes", Entry(("kubernetes:context", "dev"), ("kubernetes:port", port)));

        Assert.NotNull(error);
        Assert.Contains($"port value '{port}' is not a valid port", error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("8080")]
    [InlineData("65535")]
    public void Validate_KubernetesPortInRange_ReturnsNull(string port) =>
        Assert.Null(Validate("kubernetes", Entry(("kubernetes:context", "dev"), ("kubernetes:port", port))));

    [Fact]
    public void Validate_KubernetesUnsupportedScheme_ReportsResolveMessage()
    {
        var error = Validate("kubernetes", Entry(("kubernetes:context", "dev"), ("kubernetes:scheme", "ftp")));

        Assert.NotNull(error);
        Assert.Contains("scheme 'ftp' is not supported", error);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public void Validate_KubernetesSupportedScheme_ReturnsNull(string scheme) =>
        Assert.Null(Validate("kubernetes", Entry(("kubernetes:context", "dev"), ("kubernetes:scheme", scheme))));

    // The first layer is the developer-config file's, as DeveloperConfigFileSource registers it.
    private static IConfigurationRoot Root(params IDictionary<string, string?>[] layers)
    {
        var builder = new ConfigurationBuilder();
        builder.Add(new DeveloperConfigFileSource.FileLayerSource { InitialData = layers[0] });
        foreach (var layer in layers.Skip(1))
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
    public void ShadowingProvider_DefaultSourceLayerBelowFile_FileBlankIsNotReported()
    {
        // Where ServiceSourcesConfigCache inserts the catalog's defaultSource layer: index 0, below the file.
        var builder = new ConfigurationBuilder();
        builder.Add(new DeveloperConfigFileSource.FileLayerSource { InitialData = Layer("ServiceSources:Services:orders:source", "") });
        builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = Layer("ServiceSources:Services:orders:source", "container") });

        Assert.Null(PickerAnswers.ShadowingProvider(builder.Build(), "orders"));
    }

    [Fact]
    public void ShadowingProvider_DefaultSourceLayerBelowFile_HigherBlankStillReported()
    {
        var builder = new ConfigurationBuilder();
        builder.Add(new DeveloperConfigFileSource.FileLayerSource { InitialData = Layer("ServiceSources:Services:orders:source", "url") });
        builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = Layer("ServiceSources:Services:orders:source", "container") });
        builder.AddInMemoryCollection(Layer("ServiceSources:Services:orders:source", " "));

        Assert.NotNull(PickerAnswers.ShadowingProvider(builder.Build(), "orders"));
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

[Trait("IO", "true")]
public class PickerAnswersPathTests
{
    private static ServiceDefinition Definition() =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static JsonObject PathField(string path) => PickerAnswersTests.Entry(("path:path", path));

    [Fact]
    public void Validate_PathThatDoesNotExist_ReportsResolveMessage()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;

        var error = PickerAnswers.Validate("orders", "path", PathField("missing-orders"), Definition(), appHost);

        Assert.NotNull(error);
        Assert.Contains("does not exist", error);
        Assert.Contains("missing-orders", error);
        Assert.Contains("'path.path' override", error);
    }

    [Fact]
    public void Validate_PathRelativeToAppHostThatExists_ReturnsNull()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(appHost, "orders"));

        Assert.Null(PickerAnswers.Validate("orders", "path", PathField("orders"), Definition(), appHost));
    }

    [Fact]
    public void Validate_AbsolutePathThatExists_ReturnsNull()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;

        Assert.Null(PickerAnswers.Validate("orders", "path", PathField(elsewhere), Definition(), appHost));
    }
}
