using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.PortAllocation;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class MigratedSiteEscapingTests
{
    private const string Forgery = "orders'\nFATAL: everything is fine";

    [Fact]
    public void For_EscapesANameHole()
    {
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name(Forgery)}' failed.");

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("' failed.\nFATAL", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("Service 'orders\\'\\n", exception.Message, StringComparison.Ordinal);
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
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.ServiceLabel(name);

    private static string RepositoryLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.RepositoryLabel(name);

    [Fact]
    public void ServiceLabel_EscapesTheName() =>
        Assert.Equal("Service 'ord\\'ers\\nFATAL'", ServiceLabel("ord'ers\nFATAL"));

    [Fact]
    public void ServiceLabel_CapsTheName() =>
        Assert.Equal($"Service '{new string('a', Name.MaxLength)}…'", ServiceLabel(new string('a', 200)));

    [Fact]
    public void RepositoryLabel_EscapesAndCaps()
    {
        Assert.Equal("Repository 'mono\\'repo'", RepositoryLabel("mono'repo"));
        Assert.EndsWith("…'", RepositoryLabel(new string('b', 200)), StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceLabel_EscapesExactlyOnce()
    {
        // The four callers embed this result in a further message that this PR does not migrate, so
        // the guard is that one pass has already happened — not that a second pass is safe. There is
        // deliberately no way to feed a string back into the seam, which is why this asserts directly.
        Assert.Equal("Service 'ord\\'ers'", ServiceLabel("ord'ers"));
        Assert.DoesNotContain("\\\\'", ServiceLabel("ord'ers"), StringComparison.Ordinal);
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
    [InlineData("orders'\nFATAL: resolved fine", "orders\\'\\nFATAL: resolved fine")]
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
    [InlineData("orders'\nFATAL: resolved fine", "orders\\'\\nFATAL: resolved fine")]
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

    [Theory]
    [InlineData("orders'\nFATAL: resolved fine", "orders\\'\\nFATAL: resolved fine")]
    [InlineData("orders\"quoted", "orders\\\"quoted")]
    [InlineData("orders\\", "orders\\\\")]
    public void LocalProjectSource_MissingProject_EscapesTheServiceName(string serviceName, string expected)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalProjectSource.ValidateProject(serviceName, project: null));

        Assert.Contains($"Service '{expected}'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
    }
}
