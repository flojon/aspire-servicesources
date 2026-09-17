using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using ServiceSourcesOverloadProbe;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Regression repro for the endpoint skip-gate bug, covering both the original #334 report and the
/// additional call surfaces and edge cases #335 added on top of it. All cases are expected to pass
/// once the skip-and-warn gate closes the update-branch gap (design docs
/// docs/superpowers/specs/2026-09-14-334-endpoint-skip-gate-design.md and
/// docs/superpowers/specs/2026-09-15-335-raw-withendpoint-gate-design.md) — before the #334 fix,
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

    // The callback overload against a "url" facade, which is the one source whose ServiceResourceBuilder
    // has no real resource at all. That makes it the case that distinguishes the gate from the
    // annotation-diff forward's own null check: the null check would still have let the delegated
    // call run, invoke the callback, and add "admin" to the facade. Nothing is added and the callback
    // never runs, so the gate is what stopped it.
    [Fact]
    public void NewEndpoint_OnUrlSource_ViaCallback_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);
        var before = service.Resource.Annotations.OfType<EndpointAnnotation>().Count();
        var callbackInvoked = false;

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithEndpointCallback(service, "admin", endpoint =>
        {
            callbackInvoked = true;
            endpoint.Port = 9999;
        });

        var after = service.Resource.Annotations.OfType<EndpointAnnotation>().Count();
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.False(callbackInvoked);
        Assert.Equal(before, after);
        Assert.DoesNotContain(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "admin");
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoints before={before}, after={after}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
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

    // #335's actual scenario, against a real kubectl port-forward: WithHttpsEndpoint(port: 9999)
    // must not silently repoint the kubernetes-sourced resource's real, already-running local
    // port-forward — the risk the issue reports. The no-args case above proves the update branch is
    // gated for a call with nothing to mutate; this proves the gate holds even when the call carries
    // an explicit port that would otherwise flow straight into the shared EndpointAnnotation.
    [Fact]
    public void DefaultNamedEndpoint_OnKubernetesSource_WithArguments_DoesNotChangeThePortForwardsEndpoint()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

        var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var beforePort = before.Port;
        var beforeTargetPort = before.TargetPort;
        var beforeScheme = before.UriScheme;

        service.WithHttpsEndpoint(port: 9999);

        var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.Same(before, after);
        Assert.NotEqual(9999, beforePort);
        Assert.Equal(beforePort, after.Port);
        Assert.Equal(beforeTargetPort, after.TargetPort);
        Assert.Equal(beforeScheme, after.UriScheme);
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoint port after={after.Port}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }

    // #335's scenario reached through the raw overload directly, rather than through
    // WithHttpsEndpoint (already proven gated by #334's own fix) — the primary WithEndpoint<T>
    // overload is what WithHttpsEndpoint forwards to internally, but an AppHost author can call it
    // directly too, and #334's shadow never touched this call surface.
    [Fact]
    public void DefaultNamedEndpoint_OnKubernetesSource_ViaRawWithEndpoint_DoesNotChangeThePortForwardsEndpoint()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

        var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var beforePort = before.Port;

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithEndpointPrimary(service, port: 9999, scheme: "https");

        var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.Same(before, after);
        Assert.NotEqual(9999, beforePort);
        Assert.Equal(beforePort, after.Port);
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoint port after={after.Port}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }

    // #335's most severe scenario: an arbitrary-mutation callback against the real kubectl
    // port-forward's endpoint must never run at all when unreachable -- not run-then-reverted, since
    // this overload can mutate fields (scheme, protocol, target) no numeric overload exposes, so
    // "gated before it ran" and "ran but its effect was reverted" are not equivalent guarantees here.
    [Fact]
    public void DefaultNamedEndpoint_OnKubernetesSource_ViaCallback_DoesNotChangeThePortForwardsEndpoint()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

        var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var beforePort = before.Port;
        var callbackInvoked = false;

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithEndpointCallback(service, "https", endpoint =>
        {
            callbackInvoked = true;
            endpoint.Port = 9999;
        });

        var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.False(callbackInvoked);
        Assert.Same(before, after);
        Assert.Equal(beforePort, after.Port);
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoint port after={after.Port}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }

    // The add branch through the callback overload against a kubernetes source. Unlike the "url"
    // case above, `Real` here is a live kubectl port-forward executable, so the forward's own null
    // check offers no protection and GateEndpointCall is the only thing standing in front of it.
    [Fact]
    public void NewEndpoint_OnKubernetesSource_ViaCallback_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());
        var real = ((ServiceResourceBuilder)service).Real!.Resource.Annotations;

        var beforeOnReal = real.OfType<EndpointAnnotation>().ToArray();
        var callbackInvoked = false;

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithEndpointCallback(service, "admin", endpoint =>
        {
            callbackInvoked = true;
            endpoint.Port = 9999;
        });

        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.False(callbackInvoked);
        Assert.DoesNotContain(real.OfType<EndpointAnnotation>(), e => e.Name == "admin");
        Assert.Equal(beforeOnReal, real.OfType<EndpointAnnotation>().ToArray());
        Assert.DoesNotContain(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "admin");
        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"),
            $"endpoints on real={real.OfType<EndpointAnnotation>().Count()}; warnings={warnings.Count}; "
            + $"text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
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
