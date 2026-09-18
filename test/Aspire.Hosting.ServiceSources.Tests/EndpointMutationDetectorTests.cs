using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The post-hoc endpoint-mutation detector: what a guest-language AppHost changes on a <c>url</c> or
/// <c>kubernetes</c> service's endpoint after it resolved is restored at <c>BeforeStartEvent</c> and
/// reported, because no C# shadow can reach those capabilities.
/// </summary>
/// <remarks>
/// Assertions read the facade's own collection. For the <em>added</em> shape that is the only place
/// to look: Aspire's create branch adds straight to <c>builder.Resource.Annotations</c>, so a
/// smuggled endpoint never reaches the real resource and asserting its absence there would pass
/// whether or not the detector ran.
/// </remarks>
public class EndpointMutationDetectorTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Url(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

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

    private static IResourceBuilder<ServiceResource> Kubernetes(IDistributedApplicationBuilder builder) =>
        new KubernetesSource(new FakePortAllocator(54321)).Resolve(
            builder, "orders", KubernetesDefinition(),
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } });

    private static IResourceBuilder<ServiceResource> Container(IDistributedApplicationBuilder builder) =>
        ResolvedService.Bridge(
            builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx"),
            "orders", "container");

    private static EndpointAnnotation[] Endpoints(IResourceBuilder<ServiceResource> service) =>
        [.. service.Resource.Annotations.OfType<EndpointAnnotation>()];

    // The harness's own coverage, on a reachable source so nothing is installed and nothing reverts:
    // it proves the reflective call really reaches Aspire's internal generic and mutates the shared
    // annotation. Every later test's premise rests on that.
    [Fact]
    public void GuestLanguageCallback_OnContainerSource_ReachesAspireAndMutatesTheEndpoint()
    {
        var builder = Builder();
        var service = Container(builder);
        service.WithHttpsEndpoint(port: 443, name: "https");

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var endpoint = Assert.Single(Endpoints(service));
        Assert.Equal("https", endpoint.Name);
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public async Task ChangedPort_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(
            service, "https", ("Port", 9999), ("TargetPort", 9999));

        var mutated = Assert.Single(Endpoints(service));
        Assert.Equal(9999, mutated.Port);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var restored = Assert.Single(Endpoints(service));
        Assert.Same(mutated, restored);
        Assert.Equal(54321, restored.Port);
        Assert.Equal(54321, restored.TargetPort);
        var warning = Assert.Single(warnings);
        Assert.Contains("Service 'orders'", warning);
        Assert.Contains(
            "endpoint 'https' was changed after this service resolved (Port, TargetPort) and has been put back",
            warning);
    }

    [Fact]
    public async Task ChangedTargetHost_OnUrlSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Url(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(
            service, "https", ("TargetHost", "attacker.internal"));

        Assert.Equal("attacker.internal", Assert.Single(Endpoints(service)).TargetHost);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal("orders.example.com", Assert.Single(Endpoints(service)).TargetHost);
        Assert.Contains(warnings, warning =>
            warning.Contains("Service 'inventory'")
            && warning.Contains(
                "endpoint 'https' was changed after this service resolved (TargetHost) and has been put back"));
    }

    [Fact]
    public async Task ChangedProtocol_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("Protocol", ProtocolType.Udp));

        Assert.Equal(ProtocolType.Udp, Assert.Single(Endpoints(service)).Protocol);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(ProtocolType.Tcp, Assert.Single(Endpoints(service)).Protocol);
        Assert.Contains("(Protocol) and has been put back", Assert.Single(warnings));
    }

    // The argument for a state-keyed detector over another per-method shadow: WithExternalHttpEndpoints
    // is a public Aspire method that sets IsExternal directly on existing annotations, so no gate
    // ever sees it. Caught here with no WithExternalHttpEndpoints-specific code at all.
    [Fact]
    public async Task ExternalHttpEndpoints_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        service.WithExternalHttpEndpoints();

        Assert.True(Assert.Single(Endpoints(service)).IsExternal);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);

        // The field the developer's call actually wrote, named in the line they have to act on.
        Assert.Contains(
            "endpoint 'https' was changed after this service resolved (IsExternal) and has been put back",
            Assert.Single(warnings));
    }

    // Subscription order, the way round the prototype measured as LOGGED=0: an earlier, unrelated
    // service's skip has already put the warnings flush handler ahead of the detector, so without an
    // immediate report the mutation is reverted and nothing is ever logged -- strictly worse than not
    // detecting it.
    [Fact]
    public async Task ChangedPort_WithTheFlushHandlerSubscribedFirst_StillReachesTheLog()
    {
        var builder = Builder();
        var earlier = Url(builder);
        earlier.WithHttpsEndpoint(port: 7777, name: "probe");
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(54321, Assert.Single(Endpoints(service)).Port);
        Assert.Single(warnings, warning =>
            warning.Contains("Service 'orders'")
            && warning.Contains("endpoint 'https' was changed after this service resolved"));
    }

    [Fact]
    public async Task AddedEndpoint_OnKubernetesSource_IsRemovedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));

        Assert.Equal(2, Endpoints(service).Length);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        // The endpoints the source registered, and only those.
        var remaining = Assert.Single(Endpoints(service));
        Assert.Equal("https", remaining.Name);
        Assert.Equal(54321, remaining.Port);
        Assert.Contains(
            "endpoint 'probe' was added after this service resolved and has been removed, so a reference " +
            "taken to it will not resolve",
            Assert.Single(warnings));
    }

    [Fact]
    public async Task ChangedAndAddedOnOneService_AreReportedInOneGroupedMessage()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));
        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var remaining = Assert.Single(Endpoints(service));
        Assert.Equal("https", remaining.Name);
        Assert.Equal(54321, remaining.Port);

        var warning = Assert.Single(warnings);
        Assert.Contains("endpoint 'https' was changed after this service resolved (Port)", warning);
        Assert.Contains("endpoint 'probe' was added after this service resolved", warning);

        // Two endpoints, one call: a count of "calls" would state a number the developer never wrote.
        Assert.DoesNotContain("2 calls", warning);
    }

    // The endpoint name is the only caller-controlled value this package interpolates into a
    // warning. Written onto the annotation directly, not through a callback: Aspire's create branch
    // runs ModelName.ValidateName, so a name this hostile cannot arrive that way today --
    // EndpointAnnotation.Name has no such validation on its setter, and the detector reads it at
    // BeforeStartEvent, long after any composition-time code could have rewritten it.
    [Fact]
    public async Task AddedEndpointWithAHostileName_IsSanitisedBeforeItReachesTheLog()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        // Neither is a control character, and both are hostile: one splits the line for a reader that
        // treats it as a terminator, the other reverses everything printed after it.
        var lineSeparator = ((char)0x2028).ToString();
        var bidiOverride = ((char)0x202E).ToString();

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));
        var added = Assert.Single(Endpoints(service), endpoint => endpoint.Name == "probe");
        added.Name = "evil\r\n" + lineSeparator + bidiOverride + "Service 'forged': " + new string('x', 300);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var warning = Assert.Single(warnings);
        Assert.DoesNotContain("\n", warning);
        Assert.DoesNotContain("\r", warning);
        // Ordinal: a format character has no collation weight, so a culture-sensitive search finds
        // one in any string at all and would pass whatever the sanitiser did.
        Assert.DoesNotContain(lineSeparator, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(bidiOverride, warning, StringComparison.Ordinal);
        Assert.Contains("u2028", warning, StringComparison.Ordinal);
        Assert.Contains("u202e", warning, StringComparison.Ordinal);

        // The quote is the delimiter the message reads the name back inside, so it cannot survive
        // unescaped either.
        Assert.DoesNotContain("Service 'forged'", warning);
        Assert.Contains("endpoint 'evil", warning);
        Assert.Contains("…' was added after this service resolved", warning);
    }

    // The forging the quoting has to survive: a name can spell the separator the message puts between
    // two entries, so one endpoint reads as two things the developer did not do.
    [Fact]
    public async Task AddedEndpointNamedLikeASecondEntry_DoesNotForgeOne()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));
        var added = Assert.Single(Endpoints(service), endpoint => endpoint.Name == "probe");
        added.Name = "a' was removed after this service resolved; endpoint 'b";

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        // The forged entry would end here, and does not: the quote that would close the real name is
        // escaped, so the whole thing stays inside one pair of delimiters.
        Assert.DoesNotContain("endpoint 'b'", Assert.Single(warnings));
    }

    // Written against the collection rather than through a callback, and named for it: no surface
    // this design polices can remove an endpoint today. The branch exists so the detector is keyed
    // on the complete set of differences between two collections rather than on two known shapes.
    [Fact]
    public async Task EndpointRemovedFromTheFacadeDirectly_IsRestoredAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));

        service.Resource.Annotations.Remove(registered);
        Assert.Empty(Endpoints(service));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Same(registered, Assert.Single(Endpoints(service)));
        Assert.Contains(
            "endpoint 'https' was removed after this service resolved and has been put back",
            Assert.Single(warnings));
    }

    // Removed is not a shape of its own: an endpoint can be changed and then taken off, and putting
    // the instance back without putting its fields back leaves the mutation live under a line that
    // says only that something was removed.
    [Fact]
    public async Task EndpointChangedAndThenRemoved_IsPutBackWithTheChangeUndone()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(
            service, "https", ("Port", 9999), ("TargetHost", "attacker.internal"));
        var registered = Assert.Single(Endpoints(service));
        service.Resource.Annotations.Remove(registered);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var restored = Assert.Single(Endpoints(service));
        Assert.Same(registered, restored);
        Assert.Equal(54321, restored.Port);
        Assert.NotEqual("attacker.internal", restored.TargetHost);

        var warning = Assert.Single(warnings);
        Assert.Contains("endpoint 'https' was removed after this service resolved and has been put back", warning);
        Assert.Contains("along with the fields changed with it (Port, TargetHost)", warning);
    }

    // The same, renamed first: the guard has to ask about the name the endpoint is being put back
    // under, not the one it was carrying when it left.
    [Fact]
    public async Task EndpointRenamedAndThenRemoved_IsNotDuplicatedOnTheRealResource()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));
        var real = Assert.Single(builder.Resources.OfType<ServiceExecutableResource>());

        registered.Name = "renamed";
        service.Resource.Annotations.Remove(registered);

        await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal("https", Assert.Single(Endpoints(service)).Name);
        Assert.Same(registered, Assert.Single(real.Annotations.OfType<EndpointAnnotation>()));
    }

    // Aspire resolves endpoints by name with SingleOrDefault, which throws on a duplicate, so the
    // re-add must never introduce one -- and the facade must end up holding exactly what the source
    // registered, which is the gate's own outcome.
    [Fact]
    public async Task EndpointRemovedAndReplacedByItsOwnName_IsReportedWithoutDuplicatingTheName()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));

        service.Resource.Annotations.Remove(registered);
        var replacement = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https", port: 1234);
        service.Resource.Annotations.Add(replacement);

        var real = Assert.Single(builder.Resources.OfType<ServiceExecutableResource>());

        // The add loop runs first and takes the replacement off the facade, so the name is free by
        // the time the re-add loop reaches the original: one endpoint named 'https', the registered
        // instance, and two entries. The duplicate guard is what still bites on `real`, which never
        // lost the instance.
        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var remaining = Assert.Single(Endpoints(service));
        Assert.Same(registered, remaining);
        Assert.Equal(54321, remaining.Port);
        Assert.Same(registered, Assert.Single(real.Annotations.OfType<EndpointAnnotation>()));
        var warning = Assert.Single(warnings);
        Assert.Contains("endpoint 'https' was added after this service resolved", warning);
        Assert.Contains("endpoint 'https' was removed after this service resolved", warning);
    }

    // The guard has to match names the way Aspire's own lookup does, which is case-insensitively --
    // a guard that agreed with it only on casing would wave through the duplicate it exists to stop.
    [Fact]
    public async Task EndpointReplacedByACaseVariantOfItsName_IsNotDuplicatedOnTheRealResource()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));
        var real = Assert.Single(builder.Resources.OfType<ServiceExecutableResource>());

        registered.Name = "HTTPS";
        service.Resource.Annotations.Remove(registered);
        real.Annotations.Remove(registered);
        var replacement = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https", port: 1234);
        real.Annotations.Add(replacement);

        await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Single(real.Annotations.OfType<EndpointAnnotation>());
    }

    // What the reference-identity key buys, and the only test that exercises Restore's Name write:
    // a renamed instance is one changed endpoint under its recorded name, not an add plus a remove.
    // Written against the annotation rather than through a callback, and named for it, because
    // EndpointUpdateContext.Name is get-only -- no surface this design polices can rename an endpoint
    // today, so this guards a field the model leaves open rather than a measured attack.
    [Fact]
    public async Task RenamedEndpoint_OnKubernetesSource_IsReportedAsOneChangeUnderItsRecordedName()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));

        registered.Name = "renamed";

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var restored = Assert.Single(Endpoints(service));
        Assert.Same(registered, restored);
        Assert.Equal("https", restored.Name);
        var warning = Assert.Single(warnings);
        Assert.Contains("endpoint 'https' was changed after this service resolved (Name)", warning);
        Assert.DoesNotContain("was added after", warning);
        Assert.DoesNotContain("was removed after", warning);
    }

    // The detector reports from inside BeforeStartEvent, where another service's skips can still be
    // buffered and waiting to be grouped -- and one of them is not recorded until DropWaitsOnUrlServices
    // runs, later in the same event. Reporting everything outstanding would cut that group in two.
    [Fact]
    public async Task AReportedRevert_DoesNotSplitAnotherServicesGroupedMessage()
    {
        var builder = Builder();

        // Resolved first, so the detector's handler is subscribed ahead of the url service's.
        var service = Kubernetes(builder);
        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var url = Url(builder);
        url.WithEnvironment("FIRST", "1");
        builder.AddExecutable("worker", "dotnet", TempDirectories.CreateSubdirectory().FullName)
            .WaitFor(url);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Single(warnings, warning => warning.Contains("Service 'orders'"));
        var inventory = Assert.Single(warnings, warning => warning.Contains("Service 'inventory'"));
        Assert.Contains("WithEnvironment", inventory);
        Assert.Contains("WaitFor", inventory);
    }

    // Aspire dispatches BeforeStartEvent sequentially in subscription order, and every
    // IDistributedApplicationEventingSubscriber registers after composition -- so a handler
    // subscribed here, after the service resolved, stands for every reader of final endpoint state.
    // It must see the restored value, not the mutated one.
    [Fact]
    public async Task TheDetectorRunsBeforeAHandlerSubscribedAfterIt()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        int? observedByALaterHandler = null;
        builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            observedByALaterHandler = Endpoints(service).Single().Port;
            return Task.CompletedTask;
        });

        await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(54321, observedByALaterHandler);
    }

    [Fact]
    public async Task OutOfBandServicesWithNoMutation_ProduceNoWarnings()
    {
        var builder = Builder();
        var url = Url(builder);
        var kubernetes = Kubernetes(builder);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Empty(warnings);
        Assert.Equal("orders.example.com", Assert.Single(Endpoints(url)).TargetHost);
        Assert.Equal(54321, Assert.Single(Endpoints(kubernetes)).Port);
    }

    // Nothing is installed for a reachable source, where a post-resolve endpoint change is
    // legitimate -- the filter that makes the no-false-positives argument true.
    [Fact]
    public async Task ChangedEndpoint_OnContainerSource_IsNeitherRevertedNorReported()
    {
        var builder = Builder();
        var service = Container(builder);
        service.WithHttpsEndpoint(port: 443, name: "https");

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Empty(warnings);
        Assert.Equal(9999, Assert.Single(Endpoints(service)).Port);
    }

    // The one Aspire handler that writes a fingerprinted field before the detector runs:
    // MutateHttp2TransportAsync sets Transport when the resource carries Http2ServiceAnnotation.
    // AsHttp2Service adds that through WithAnnotation, so Reachability skips it and the write stays
    // a self-assignment. Asserted by type NAME because the annotation is internal to
    // Aspire.Hosting.dll -- the same constraint Reachability.CapabilityLabel already documents.
    // If a future Reachability change lets it through, this fails and names the reason.
    [Fact]
    public async Task AsHttp2Service_OnKubernetesSource_NeitherLandsNorRevertsTheTransport()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var transport = Assert.Single(Endpoints(service)).Transport;

        service.AsHttp2Service();

        Assert.DoesNotContain(
            service.Resource.Annotations,
            annotation => annotation.GetType().Name == "Http2ServiceAnnotation");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(transport, Assert.Single(Endpoints(service)).Transport);
        Assert.DoesNotContain(warnings, warning => warning.Contains("was changed after this service resolved"));
    }
}
