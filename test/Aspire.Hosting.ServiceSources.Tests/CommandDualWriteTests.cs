using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using ServiceSourcesOverloadProbe;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers <c>WithCommand</c>'s remove-then-add against a <see cref="ServiceResource"/> facade
/// (#371). Aspire drops the superseded <see cref="ResourceCommandAnnotation"/> with a direct
/// <c>Resource.Annotations.Remove(...)</c> on the facade and adds the replacement through
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>, which dual-writes — so the real
/// resource keeps both. Every assertion here reads the real resource's own collection: the facade
/// alone looks correct against the bug, which is why a facade-only assertion proves nothing.
/// </summary>
public class CommandDualWriteTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static IResourceBuilder<ServiceResource> ContainerService(
        IDistributedApplicationBuilder builder, string name) =>
        ResolvedService.Bridge(
            builder.AddResource(new ServiceContainerResource(name)).WithImage("nginx"), name, "container");

    // ServiceResourceBuilder.Real is internal; the test assembly reaches it through InternalsVisibleTo
    // (src/Aspire.Hosting.ServiceSources/AssemblyInfo.cs), exactly as EndpointCallbackDualWriteTests does.
    private static ResourceAnnotationCollection RealAnnotations(IResourceBuilder<ServiceResource> service) =>
        ((ServiceResourceBuilder)service).Real!.Resource.Annotations;

    private static Task<ExecuteCommandResult> Ok(ExecuteCommandContext context) =>
        Task.FromResult(new ExecuteCommandResult { Success = true });

    private static IReadOnlyList<ResourceCommandAnnotation> CommandsOn(ResourceAnnotationCollection annotations) =>
        annotations.OfType<ResourceCommandAnnotation>().ToArray();

    [Fact]
    public void SecondWithCommand_OnContainerSource_LeavesExactlyOneCommandOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        OverloadProbe.CallWithCommand(service, "restart", "Restart v1", Ok);
        OverloadProbe.CallWithCommand(service, "restart", "Restart v2", Ok);

        var onReal = Assert.Single(CommandsOn(RealAnnotations(service)), c => c.Name == "restart");
        var onFacade = Assert.Single(CommandsOn(service.Resource.Annotations), c => c.Name == "restart");

        Assert.Same(onFacade, onReal);
        Assert.Equal("Restart v2", onReal.DisplayName);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void SecondWithCommand_OnLocalSource_LeavesExactlyOneCommandOnTheRealResource()
    {
        var builder = Builder();
        var real = builder.AddResource(new ProjectResource("orders"));
        var service = ResolvedService.Bridge(real, "orders", "local");

        OverloadProbe.CallWithCommand(service, "restart", "Restart v1", Ok);
        OverloadProbe.CallWithCommand(service, "restart", "Restart v2", Ok);

        var onReal = Assert.Single(CommandsOn(real.Resource.Annotations), c => c.Name == "restart");
        var onFacade = Assert.Single(CommandsOn(service.Resource.Annotations), c => c.Name == "restart");

        Assert.Same(onFacade, onReal);
        Assert.Equal("Restart v2", onReal.DisplayName);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    // The second of Aspire's two WithCommand overloads — [Obsolete] upstream, but still live API an
    // AppHost compiled before CommandOptions existed reaches, and it carries the identical
    // remove-then-add shape.
    [Fact]
    public void SecondWithCommand_ViaLegacyOverload_OnContainerSource_LeavesExactlyOneCommandOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        OverloadProbe.CallWithCommandLegacy(service, "restart", "Restart v1", Ok, "first");
        OverloadProbe.CallWithCommandLegacy(service, "restart", "Restart v2", Ok, "second");

        var onReal = Assert.Single(CommandsOn(RealAnnotations(service)), c => c.Name == "restart");
        var onFacade = Assert.Single(CommandsOn(service.Resource.Annotations), c => c.Name == "restart");

        Assert.Same(onFacade, onReal);
        Assert.Equal("Restart v2", onReal.DisplayName);
        Assert.Equal("second", onReal.DisplayDescription);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    // Confirms the winning candidate is the shadow, not Aspire's generic. That the call compiles at
    // all is the other half of the guard, documented on OverloadProbe.CallWithCommandNoOptions.
    [Fact]
    public void SecondWithCommand_WithoutCommandOptions_BindsTheShadowAndLeavesOneCommandOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        OverloadProbe.CallWithCommandNoOptions(service, "restart", "Restart v1", Ok);
        OverloadProbe.CallWithCommandNoOptions(service, "restart", "Restart v2", Ok);

        var onReal = Assert.Single(CommandsOn(RealAnnotations(service)), c => c.Name == "restart");

        Assert.Same(Assert.Single(CommandsOn(service.Resource.Annotations), c => c.Name == "restart"), onReal);
        Assert.Equal("Restart v2", onReal.DisplayName);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    // Guards the direction the fix must NOT overreach in: a command registered under a name nothing
    // else uses must still land on both collections, and re-registering "restart" must leave a
    // differently-named command alone.
    [Fact]
    public void SecondWithCommand_OnContainerSource_LeavesOtherCommandsUntouched()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        OverloadProbe.CallWithCommand(service, "restart", "Restart v1", Ok);
        OverloadProbe.CallWithCommand(service, "flush", "Flush cache", Ok);
        OverloadProbe.CallWithCommand(service, "restart", "Restart v2", Ok);

        var onReal = CommandsOn(RealAnnotations(service));

        Assert.Equal(2, onReal.Count);
        Assert.Equal("Restart v2", Assert.Single(onReal, c => c.Name == "restart").DisplayName);
        Assert.Equal("Flush cache", Assert.Single(onReal, c => c.Name == "flush").DisplayName);
        Assert.Same(
            Assert.Single(CommandsOn(service.Resource.Annotations), c => c.Name == "flush"),
            Assert.Single(onReal, c => c.Name == "flush"));
    }

    [Fact]
    public void FirstWithCommand_OnContainerSource_ReachesTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        OverloadProbe.CallWithCommand(service, "restart", "Restart", Ok);

        var onReal = Assert.Single(CommandsOn(RealAnnotations(service)));
        var onFacade = Assert.Single(CommandsOn(service.Resource.Annotations));

        Assert.Same(onFacade, onReal);
        Assert.Equal("Restart", onReal.DisplayName);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Url(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

    // Out-of-band sources are the no-regression half of #371: IsUnreachable is true for
    // ResourceCommandAnnotation, so the FIRST call is skipped with a warning and never lands on the
    // facade — which is precisely why the second call's lookup finds nothing and Aspire's Remove
    // never fires. Nothing lands anywhere, and the skip is still reported (as one message, since
    // ServiceSourcesWarnings groups skips per service and source).
    [Fact]
    public void RepeatedWithCommand_OnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = Url(builder);

        OverloadProbe.CallWithCommand(service, "restart", "Restart v1", Ok);
        OverloadProbe.CallWithCommand(service, "restart", "Restart v2", Ok);

        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.Empty(CommandsOn(service.Resource.Annotations));
        Assert.Contains(nameof(ResourceCommandAnnotation), Assert.Single(warnings));
    }

    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => port;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static IResourceBuilder<ServiceResource> Kubernetes(IDistributedApplicationBuilder builder) =>
        new KubernetesSource(new FakePortAllocator(54321)).Resolve(
            builder,
            "orders",
            new ServiceMetadata
            {
                Repository = "https://github.com/company/orders",
                Project = "Orders.csproj",
                Kubernetes = new KubernetesMetadata { Service = "orders-svc", Port = 8080, Scheme = "https" },
            }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories),
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } });

    // Same no-regression case with a real resource behind the facade — a live kubectl port-forward,
    // which the "url" case above does not have, so nothing but the reachability gate stands between
    // a WithCommand call and the wrong process.
    [Fact]
    public void RepeatedWithCommand_OnKubernetesSource_IsSkippedAndNeverReachesThePortForward()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        OverloadProbe.CallWithCommand(service, "restart", "Restart v1", Ok);
        OverloadProbe.CallWithCommand(service, "restart", "Restart v2", Ok);

        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.Empty(CommandsOn(RealAnnotations(service)));
        Assert.Empty(CommandsOn(service.Resource.Annotations));
        Assert.Contains(nameof(ResourceCommandAnnotation), Assert.Single(warnings));
    }
}
