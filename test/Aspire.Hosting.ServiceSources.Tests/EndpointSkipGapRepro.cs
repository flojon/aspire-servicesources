using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The repro from issue #334, committed as the issue described it. All three cases are expected to
/// pass once the skip-and-warn gate closes the update-branch gap (design doc
/// docs/superpowers/specs/2026-09-14-334-endpoint-skip-gate-design.md) — before that fix,
/// <see cref="DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported"/> is the one that fails.
/// </summary>
public class EndpointSkipGapRepro
{
    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Url(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

    // FAILS before the fix — the default-named call an AppHost actually writes.
    [Fact]
    public void DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        var before = service.Resource.Annotations.OfType<EndpointAnnotation>()
            .Select(e => $"{e.Name}:{e.UriScheme}").ToArray();

        service.WithHttpsEndpoint();

        var after = service.Resource.Annotations.OfType<EndpointAnnotation>().Count();
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithHttpEndpoint/WithHttpsEndpoint"),
            $"pre-existing=[{string.Join(",", before)}]; endpoints after={after}; "
            + $"warnings={warnings.Count}; text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }

    // PASSES before and after — a genuinely new endpoint name goes through WithAnnotation and is
    // gated correctly today already.
    [Fact]
    public void NewEndpoint_OnUrlSource_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        service.WithHttpsEndpoint(port: 7777, name: "probe");

        var added = service.Resource.Annotations.OfType<EndpointAnnotation>().Count(e => e.Name == "probe");
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.True(
            added == 0 && warnings.Count == 1 && warnings[0].Contains("WithHttpEndpoint/WithHttpsEndpoint"),
            $"'probe' added to facade={added}; warnings={warnings.Count}");
    }

    // PASSES before and after — the update branch mutates nothing for a no-argument call, so the
    // kubernetes-sourced port-forward's real endpoint tuple was never at risk here (the *with*-
    // arguments mutation risk is #335, out of this file's scope). Written from the issue's own prose
    // description; no literal code for this case appeared in the issue body — see this task's notes.
    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => port;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static ServiceDefinition KubernetesDefinition() =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = new KubernetesMetadata { Service = "orders-svc", Port = 8080, Scheme = "https" },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static ServiceDeveloperConfig KubernetesDevConfig() =>
        new() { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } };

    [Fact]
    public void DefaultNamedEndpoint_OnKubernetesSource_DoesNotChangeThePortForwardsEndpoint()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

        var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var beforePort = before.Port;
        var beforeTargetPort = before.TargetPort;
        var beforeScheme = before.UriScheme;

        service.WithHttpsEndpoint();

        var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Same(before, after);
        Assert.Equal(beforePort, after.Port);
        Assert.Equal(beforeTargetPort, after.TargetPort);
        Assert.Equal(beforeScheme, after.UriScheme);
    }

    // Round-1 security-review regression: GateEndpointCall must read `source` from
    // ServiceResourceBuilder's own closure-captured field, not by re-scanning `Resource.Annotations`
    // for ServiceSourceAnnotation — that collection is public and mutable, so stripping just that one
    // bookkeeping annotation (e.g. a reset/clone helper) must not silently re-open #334's exact bug:
    // the update-branch mutation applying for real, with zero warning, on a url-sourced facade.
    [Fact]
    public void DefaultNamedEndpoint_OnUrlSource_StaysGated_EvenIfServiceSourceAnnotationIsStripped()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        var stripped = Assert.Single(service.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        service.Resource.Annotations.Remove(stripped);

        var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var beforePort = before.Port;

        service.WithHttpsEndpoint(port: 9999);

        var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.Same(before, after);
        Assert.Equal(beforePort, after.Port);
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoint port after={after.Port}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }
}
