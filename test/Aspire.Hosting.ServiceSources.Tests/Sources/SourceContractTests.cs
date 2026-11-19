using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// What every registered source owes the picker, the save validator and the error-message switch offer,
/// checked over one sample matrix so a source cannot drift from the contract unnoticed.
/// </summary>
[Trait("IO", "true")]
public class SourceContractTests
{
    private const string ServiceName = "orders";

    private sealed class FakePortAllocator : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => 54321;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    /// <param name="OutOfRangeCatalogKubernetesPort">
    /// The one catalog fact the model does not claim to report ahead of an askable setting: such an entry
    /// is offered, and the save-time check reports the port once the answers exist.
    /// </param>
    private sealed record Sample(string Label, ServiceDefinition Definition, bool OutOfRangeCatalogKubernetesPort = false);

    private static IEnumerable<Sample> Samples()
    {
        List<(string Label, ServiceMetadata Metadata, bool Gap)> metadata = [];

        foreach (var url in new[] { null, "", "  ", "https://orders.example.com", "ftp://orders.example.com", "not a url" })
        {
            metadata.Add(($"url '{url}'", new ServiceMetadata { Url = new UrlMetadata { Url = url! } }, false));
        }

        foreach (var (image, port, scheme) in new (string?, int?, string?)[]
        {
            (null, null, null), ("", 8080, null), ("company/orders", null, null), ("company/orders", 0, null),
            ("company/orders", 70000, null), ("company/orders", 8080, null), ("company/orders", 8080, "https"),
            ("company/orders", 8080, "gopher"),
        })
        {
            metadata.Add((
                $"container {image}/{port}/{scheme}",
                new ServiceMetadata { Container = new ContainerMetadata { Image = image!, Port = port, Scheme = scheme } },
                false));
        }

        foreach (var (service, port, scheme) in new (string?, int?, string?)[]
        {
            (null, null, null), ("", 80, null), ("  ", 80, null), ("orders", null, null), ("orders", 0, null),
            ("orders", 80, null), ("orders", 80, "https"), ("orders", 80, "gopher"),
        })
        {
            metadata.Add((
                $"kubernetes {service}/{port}/{scheme}",
                new ServiceMetadata { Kubernetes = new KubernetesMetadata { Service = service!, Port = port, Scheme = scheme } },
                !string.IsNullOrWhiteSpace(service) && scheme is not "gopher" && port is 0));
        }

        foreach (var path in new[] { null, "orders", "  " })
        {
            metadata.Add(($"path '{path}'", new ServiceMetadata { Path = path }, false));
        }

        metadata.Add(("empty", new ServiceMetadata(), false));

        foreach (var repository in new[] { "", "https://github.com/company/orders" })
        {
            foreach (var (label, entry, gap) in metadata)
            {
                entry.Repository = repository;
                entry.Project = "Orders.csproj";
                entry.Kind = LocalKinds.Dotnet;

                yield return new Sample(
                    $"{label}, repository '{repository}'",
                    entry.ToDefinition("servicesources.yaml", ServiceName, TestHelpers.EmptyRepositories),
                    gap);
            }
        }
    }

    // The empty config plus configs that fill each source's block or hold values that do not resolve.
    private static IEnumerable<(string Label, ServiceDeveloperConfig Config)> Configs(string existingDirectory)
    {
        foreach (var context in new[] { null, "", "dev-cluster" })
        {
            foreach (var devUrl in new[] { null, "https://dev.example.com", "ftp://dev.example.com" })
            {
                foreach (var pathOverride in new[] { null, existingDirectory, "missing-directory" })
                {
                    var config = new ServiceDeveloperConfig();
                    config.Kubernetes.Context = context;
                    config.Url.Url = devUrl;
                    config.Path.Path = pathOverride;

                    yield return ($"context '{context}', developer url '{devUrl}', path override '{pathOverride}'", config);
                }
            }
        }
    }

    private static string AppHost() => TempDirectories.CreateSubdirectory().FullName;

    private static SourceInput Input(ServiceDefinition definition, ServiceDeveloperConfig config, string appHost) =>
        new(ServiceName, definition, config, appHost);

    private static readonly Dictionary<string, string> SampleValues = new(StringComparer.Ordinal)
    {
        ["url:url"] = "http://localhost:5000",
        ["kubernetes:context"] = "dev",
        ["kubernetes:port"] = "8080",
        ["kubernetes:namespace"] = "default",
        ["kubernetes:scheme"] = "http",
    };

    private static string SampleValue(string key, string existingDirectory) =>
        key == "path:path"
            ? existingDirectory
            : SampleValues.TryGetValue(key, out var value)
                ? value
                : throw new Xunit.Sdk.XunitException($"Add a sample value for the follow-up field '{key}'.");

    // The merged-entry route PickerAnswers takes: a JSON entry bound through a configuration root.
    private static IConfigurationSection Bind(IEnumerable<(string Key, string Value)> answers)
    {
        var entry = new JsonObject();

        foreach (var (key, value) in answers)
        {
            var parts = key.Split(':');
            var node = entry;

            foreach (var part in parts[..^1])
            {
                node = node[part] as JsonObject ?? (JsonObject)(node[part] = new JsonObject());
            }

            node[parts[^1]] = value;
        }

        var wrapper = new JsonObject { [ServiceName] = entry };
        var root = new ConfigurationRoot([new JsonStreamConfigurationProvider(
            new JsonStreamConfigurationSource { Stream = new MemoryStream(Encoding.UTF8.GetBytes(wrapper.ToJsonString())) })]);

        return root.GetSection(ServiceName);
    }

    [Fact]
    public void EveryOfferedSource_AnsweredFromItsFields_Resolves()
    {
        var appHost = AppHost();
        var checkedAny = 0;

        foreach (var (name, source) in ServiceSourcesBuilderExtensions.Sources)
        {
            foreach (var sample in Samples().Where(s => !s.OutOfRangeCatalogKubernetesPort))
            {
                var requirements = source.Requirements(Input(sample.Definition, new ServiceDeveloperConfig(), appHost));

                if (!requirements.Offered)
                {
                    continue;
                }

                var section = Bind(requirements.Fields.Select(f => (f.Key, SampleValue(f.Key, appHost))));
                var answered = section.Get<ServiceDeveloperConfig>() ?? new ServiceDeveloperConfig();

                var after = source.Requirements(Input(sample.Definition, answered, appHost));

                checkedAny++;
                Assert.True(
                    after.Problem is null,
                    $"Source '{name}', {sample.Label}: still refused after every field was answered: {after.Problem?.Message}");
            }
        }

        Assert.True(checkedAny > 0);
    }

    [Fact]
    public void EveryFieldKey_IsARealConfigKey()
    {
        var appHost = AppHost();
        var empty = JsonSerializer.Serialize(new ServiceDeveloperConfig());
        var keys = new HashSet<string>();

        foreach (var (name, source) in ServiceSourcesBuilderExtensions.Sources)
        {
            foreach (var sample in Samples())
            {
                foreach (var field in source.Requirements(Input(sample.Definition, new ServiceDeveloperConfig(), appHost)).Fields)
                {
                    if (!keys.Add(field.Key))
                    {
                        continue;
                    }

                    var section = Bind([(field.Key, SampleValue(field.Key, appHost))]);

                    DeveloperConfigValidator.ValidateAll([section], DeveloperConfigShape.Service);

                    var bound = section.Get<ServiceDeveloperConfig>() ?? new ServiceDeveloperConfig();
                    Assert.True(
                        JsonSerializer.Serialize(bound) != empty,
                        $"Source '{name}': field '{field.Key}' binds to nothing.");
                }
            }
        }

        Assert.Contains("url:url", keys);
        Assert.Contains("kubernetes:context", keys);
        Assert.Contains("path:path", keys);
    }

    [Fact]
    public void EveryField_HasALabel()
    {
        var appHost = AppHost();

        foreach (var (name, source) in ServiceSourcesBuilderExtensions.Sources)
        {
            foreach (var sample in Samples())
            {
                Assert.All(
                    source.Requirements(Input(sample.Definition, new ServiceDeveloperConfig(), appHost)).Fields,
                    field => Assert.False(string.IsNullOrWhiteSpace(field.Label), $"Source '{name}': '{field.Key}' has no label."));
            }
        }
    }

    [Fact]
    public void ResolvesImpliesOffered_ForEveryRegisteredSource_OverEverySample()
    {
        var appHost = AppHost();
        var resolved = 0;

        foreach (var (name, source) in ServiceSourcesBuilderExtensions.Sources)
        {
            foreach (var sample in Samples())
            {
                foreach (var (configLabel, config) in Configs(appHost))
                {
                    var requirements = source.Requirements(Input(sample.Definition, config, appHost));

                    if (requirements.Resolves)
                    {
                        resolved++;
                        Assert.True(requirements.Offered, $"Source '{name}' resolves {sample.Label} ({configLabel}) but is not offered.");
                    }
                }
            }
        }

        Assert.True(resolved > 0);
    }

    [Fact]
    public void IsSwitchRemedy_IsExactlyContainerKubernetesUrl() =>
        Assert.Equal(
            ["container", "kubernetes", "url"],
            ServiceSourcesBuilderExtensions.Sources
                .Where(entry => entry.Value.IsSwitchRemedy)
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal));

    [Fact]
    public void EverySource_CheckAndRequirementsAgreeWithResolve()
    {
        var appHost = AppHost();
        IReadOnlyDictionary<string, IServiceSource> sources = new Dictionary<string, IServiceSource>
        {
            ["url"] = new UrlSource(),
            ["container"] = new ContainerSource(),
            ["kubernetes"] = new KubernetesSource(new FakePortAllocator()),
            ["disabled"] = new DisabledSource(),
        };

        // Check throws before Resolve touches the builder, so one builder serves every refusal.
        var refusalBuilder = TestHelpers.CreateBuilder(appHost);
        var resolvedCount = 0;

        foreach (var (name, source) in sources)
        {
            foreach (var sample in Samples())
            {
                foreach (var (configLabel, config) in Configs(appHost))
                {
                    var requirements = source.Requirements(Input(sample.Definition, config, appHost));
                    var where = $"Source '{name}', {sample.Label}, {configLabel}";

                    if (requirements.Problem is { } problem)
                    {
                        var thrown = Assert.Throws<ServiceSourcesConfigurationException>(
                            () => source.Resolve(refusalBuilder, ServiceName, sample.Definition, config));

                        Assert.True(thrown.Message == problem.Message, $"{where}: messages differ.");
                    }
                    else
                    {
                        resolvedCount++;

                        Assert.True(
                            source.Resolve(TestHelpers.CreateBuilder(appHost), ServiceName, sample.Definition, config) is not null,
                            where);
                    }
                }
            }
        }

        Assert.True(resolvedCount > 0);
    }
}
