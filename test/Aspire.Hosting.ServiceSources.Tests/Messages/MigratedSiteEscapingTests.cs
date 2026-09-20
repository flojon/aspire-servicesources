using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.PortAllocation;
using Aspire.Hosting.ServiceSources.Sources;
using ProjectResource = Aspire.Hosting.ApplicationModel.ProjectResource;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

[Trait("IO", "true")]
public class MigratedSiteEscapingTests
{
    private const string Forgery = "orders'\nFATAL: everything is fine";

    [Fact]
    public void For_EscapesANameHole()
    {
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name(Forgery)}' failed.");

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("' failed.\nFATAL", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("Service 'orders\\u0027\\n", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_CarriesAnInnerException()
    {
        var inner = new InvalidOperationException("underneath");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}'.", inner);

        Assert.Same(inner, exception.InnerException);
        Assert.Equal("Service 'orders'.", exception.Message);
    }

    [Fact]
    public void For_AcceptsAMessageWithNoHoles() =>
        Assert.Equal("nothing interpolated here",
            ServiceSourcesConfigurationException.For($"nothing interpolated here").Message);

    [Fact]
    public void Describe_CannotForgeACausedByLineFromAnInnerMessage()
    {
        // Environment.NewLine, not "\n": Describe writes the platform separator, so a payload hard-coding
        // "\n" would already be harmless on Windows and the test would pass before the fix.
        var forged = $"authentication failed{Environment.NewLine}  caused by: nothing is wrong, carry on";
        var exception = ServiceSourcesConfigurationException.For(
            $"Service '{new Name("orders")}' failed.", new InvalidOperationException(forged));

        var described = exception.Describe(fullDetail: false);

        // The forged text may survive as characters; what it must not do is start a line. Splitting on
        // the separator + prefix counts real cause lines only, so exactly one wrapped cause means two parts.
        Assert.Equal(2, described.Split(Environment.NewLine + "  caused by: ").Length);
        Assert.Contains("\\n  caused by: nothing is wrong", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_KeepsTheCausesWordingIntactApartFromLineBreaks()
    {
        var inner = new InvalidOperationException("could not read 'origin/main'");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}' failed.", inner);

        Assert.Contains("  caused by: could not read 'origin/main'",
            exception.Describe(fullDetail: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DoesNotCapALongCause()
    {
        var inner = new InvalidOperationException(new string('x', 400));
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}' failed.", inner);

        Assert.Contains(new string('x', 400), exception.Describe(fullDetail: false), StringComparison.Ordinal);
    }

    // Fully qualified deliberately: the test assembly already has an
    // Aspire.Hosting.ServiceSources.Tests.Prepare namespace, so the simple name `Prepare` binds
    // there and lookup stops — `Prepare.PreparePlan` is CS0234, not the production type.
    private static string ServiceLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.ServiceLabel(name).ToString();

    private static string RepositoryLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.RepositoryLabel(name).ToString();

    [Fact]
    public void ServiceLabel_EscapesTheName() =>
        Assert.Equal("Service 'ord\\u0027ers\\nFATAL'", ServiceLabel("ord'ers\nFATAL"));

    [Fact]
    public void ServiceLabel_CapsTheName() =>
        Assert.Equal($"Service '{new string('a', Name.MaxLength)}…'", ServiceLabel(new string('a', 200)));

    [Fact]
    public void RepositoryLabel_EscapesAndCaps()
    {
        Assert.Equal("Repository 'mono\\u0027repo'", RepositoryLabel("mono'repo"));
        Assert.EndsWith("…'", RepositoryLabel(new string('b', 200)), StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceLabel_EscapesExactlyOnce()
    {
        // The four callers embed this result in a further message that this PR does not migrate, so
        // the guard is that one pass has already happened — not that a second pass is safe. There is
        // deliberately no way to feed a string back into the seam, which is why this asserts directly.
        Assert.Equal("Service 'ord\\u0027ers'", ServiceLabel("ord'ers"));
        Assert.DoesNotContain("\\\\u0027", ServiceLabel("ord'ers"), StringComparison.Ordinal);
    }

    private sealed class FakePortAllocator : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => 12345;

        public IReadOnlyList<int> AllocatePorts(int count) => Enumerable.Range(12345, count).ToArray();
    }

    private static ServiceDefinition UrlDefinition(
        string serviceName, string? url = "https://orders.example.com", string yamlPath = "servicesources.yaml") =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Url = url is null ? null : new UrlMetadata { Url = url },
        }.ToDefinition(yamlPath, serviceName, TestHelpers.EmptyRepositories);

    private static ServiceDefinition KubernetesDefinition(string serviceName) =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = new KubernetesMetadata { Service = "orders", Port = 8080 },
        }.ToDefinition("servicesources.yaml", serviceName, TestHelpers.EmptyRepositories);

    // Each case carries its own expected rendering. Asserting only "no newline" would pass on the
    // UNMIGRATED code for every input that contains no newline, which is two of these three.
    [Theory]
    [InlineData("orders'\nFATAL: resolved fine", "orders\\u0027\\nFATAL: resolved fine")]
    [InlineData("orders\"quoted", "orders\\\"quoted")]
    [InlineData("orders\\", "orders\\\\")]
    public void UrlSource_MissingUrl_EscapesTheServiceName(string serviceName, string expected)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(serviceName, UrlDefinition(serviceName, url: null),
                new ServiceDeveloperConfig { Source = "url", Url = new() { Url = null } }));

        Assert.Contains($"Service '{expected}'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
    }

    // A guard, not a red test: this passes on the unmigrated code too. It exists to catch a
    // migration that reached for a Name hole here and silently truncated the origin.
    [Fact]
    public void UrlSource_MissingUrl_DoesNotTruncateTheOrigin()
    {
        var yamlPath = new string('p', 200) + ".yaml";

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl("orders", UrlDefinition("orders", url: null, yamlPath),
                new ServiceDeveloperConfig { Source = "url", Url = new() { Url = null } }));

        Assert.Contains(yamlPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlSource_InvalidUrl_EscapesTheUrlWithoutCappingIt()
    {
        var url = "htttps://" + new string('h', 200) + ".example.com/'\n";

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl("orders", UrlDefinition("orders"),
                new ServiceDeveloperConfig { Source = "url", Url = new() { Url = url } }));

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        // Not capped at the name cap: truncating the URL removes the diagnosis the message is for.
        Assert.DoesNotContain("…", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders'\nFATAL: resolved fine", "orders\\u0027\\nFATAL: resolved fine")]
    [InlineData("orders\"quoted", "orders\\\"quoted")]
    [InlineData("orders\\", "orders\\\\")]
    public void KubernetesSource_MissingContext_EscapesTheServiceName(string serviceName, string expected)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                serviceName,
                KubernetesDefinition(serviceName),
                new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = null } },
                new FakePortAllocator(),
                out _,
                out _));

        Assert.Contains($"Service '{expected}'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
    }

    private const string OrdersCatalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: Orders.csproj
            container:
              image: ghcr.io/company/orders
              port: 8080
        """;

    private static async Task<string> OrphanWarningAsync(string localJson)
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), OrdersCatalog);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), localJson);

        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.AddService("orders");

        return Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    [Fact]
    public async Task OrphanedEntries_EscapeEachNameInTheList()
    {
        // The escaped orphan must be PRESENT, not merely newline-free: an implementation that
        // dropped the offending name entirely would satisfy a DoesNotContain-only assertion.
        // No ':' in the forged name: configuration treats it as a key separator, so the entry would
        // be refused as a malformed key long before the audit that this test is about.
        var warning = await OrphanWarningAsync("""
            { "services": {
                "orders": { "source": "container" },
                "ord'ers\nFATAL everything is fine": { "source": "container" },
                "payments": { "source": "container" } } }
            """);

        Assert.Contains("'ord\\u0027ers\\nFATAL everything is fine'", warning, StringComparison.Ordinal);
        Assert.Contains("'payments'", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanedEntries_DoNotCapTheJoinedList()
    {
        // Ten names of ten characters joined is well over the 64-character name cap; capping the
        // list rather than each name would hide most of what the developer has to fix.
        string[] orphans = [.. Enumerable.Range(0, 10).Select(i => $"service-{i:00}")];
        var entries = string.Join(", ", orphans.Select(name => $"\"{name}\": {{ \"source\": \"container\" }}"));

        var warning = await OrphanWarningAsync($$"""
            { "services": { "orders": { "source": "container" }, {{entries}} } }
            """);

        Assert.All(orphans, name => Assert.Contains($"'{name}'", warning, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("orders'\nFATAL: resolved fine", "orders\\u0027\\nFATAL: resolved fine")]
    [InlineData("orders\"quoted", "orders\\\"quoted")]
    [InlineData("orders\\", "orders\\\\")]
    public void LocalProjectSource_MissingProject_EscapesTheServiceName(string serviceName, string expected)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalProjectSource.ValidateProject(serviceName, project: null));

        Assert.Contains($"Service '{expected}'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
    }

    private static string? LaunchProfileWarning(string serviceName, params string[] applicationUrls) =>
        DeferredCheckout.LaunchProfileEndpointWarning(
            serviceName,
            new LandedLaunchProfile(null, applicationUrls, new Dictionary<string, string>(StringComparer.Ordinal)),
            new ProjectResource("orders"));

    [Theory]
    [InlineData("orders'\nFATAL: started fine", "orders\\u0027\\nFATAL: started fine")]
    [InlineData("orders\"quoted", "orders\\\"quoted")]
    public void LaunchProfileEndpointWarning_EscapesTheServiceName(string serviceName, string expected)
    {
        var warning = LaunchProfileWarning(serviceName, "http://localhost:8081");

        Assert.NotNull(warning);
        Assert.Contains($"Service '{expected}'", warning, StringComparison.Ordinal);

        // The name is also quoted inside a double-quoted AddService("…") snippet the reader pastes.
        Assert.Contains($"AddService(\"{expected}\")", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchProfileEndpointWarning_EscapesTheDeclaredUrl()
    {
        var warning = LaunchProfileWarning("orders", "http://localhost:8081/'\nFATAL: bound fine");

        Assert.NotNull(warning);
        Assert.Contains("http://localhost:8081/\\u0027\\nFATAL: bound fine", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchProfileEndpointWarning_DoesNotCapTheJoinedUrlList()
    {
        // Every URL is over the 64-character cap on its own, so this fails for a per-URL cap as well
        // as for a cap over the list. Three short URLs caught only the second, and a per-URL Name
        // hole — which is what this site had — truncated away the port, the one fact it reports.
        string[] urls =
        [
            "http://localhost:8081/orders/api/v1/health/ready/with/a/long/path/segment",
            "http://localhost:8082/payments/api/v1/health/ready/with/a/long/path/segment",
            "http://localhost:8083/shipping/api/v1/health/ready/with/a/long/path/segment",
        ];

        var warning = LaunchProfileWarning("orders", urls);

        Assert.NotNull(warning);
        Assert.All(urls, url => Assert.Contains(url, warning, StringComparison.Ordinal));
        Assert.DoesNotContain("…", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchProfileEndpointWarning_KeepsThePortOfALongUrl()
    {
        // The regression this guards: a Name hole capped each URL at 64 and dropped the ":5001" the
        // sentence exists to report.
        var url = "http://a-long-development-hostname.internal.example.com:5001/api/v1/health";

        var warning = LaunchProfileWarning("orders", url);

        Assert.NotNull(warning);
        Assert.Contains(":5001/api/v1/health", warning, StringComparison.Ordinal);
    }

    // Raw.Origin blesses CatalogOrigin.Describe(), whose yaml path is a developer-chosen filesystem
    // path sitting inside this message's own quotes. Four migrated messages consume it.
    [Fact]
    public void KubernetesSource_MissingService_EscapesTheCatalogOriginPath()
    {
        var definition = new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
        }.ToDefinition(
            "/home/o'brien/src\nFATAL: catalog is fine/servicesources.yaml",
            "orders",
            TestHelpers.EmptyRepositories);

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                "orders",
                definition,
                new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } },
                new FakePortAllocator(),
                out _,
                out _));

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.Contains("brien", exception.Message, StringComparison.Ordinal);
        // The apostrophe is what the escaping is for: a directory named with one closes
        // the quotes this message wraps the path in.
        Assert.Contains("o\\u0027brien", exception.Message, StringComparison.Ordinal);
        // Escaped but never capped: it is a path, not a name.
        Assert.Contains("servicesources.yaml", exception.Message, StringComparison.Ordinal);
    }

    // Raw.Cause hands the reader a third party's wording. ConfiguredValue.Bare spells out every
    // invisible rather than a set of terminators someone enumerated: U+000B and ESC are not line
    // endings, but a terminal honouring ESC[2K erases the lines this package already printed.
    [Theory]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u000C")]
    [InlineData("\u000B")]
    [InlineData("\u001B[2K")]
    public void Describe_CannotForgeALineWithAnyInvisible(string terminator)
    {
        var forged = $"authentication failed{terminator}  caused by: nothing is wrong, carry on";
        var exception = ServiceSourcesConfigurationException.For(
            $"Service '{new Name("orders")}' failed.", new InvalidOperationException(forged));

        var described = exception.Describe(fullDetail: false);

        Assert.DoesNotContain(terminator, described, StringComparison.Ordinal);
        Assert.Equal(2, described.Split(Environment.NewLine + "  caused by: ").Length);
    }
}
