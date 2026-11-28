using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.BackingServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>What <see cref="KubernetesSource"/>'s check computed for its build.</summary>
internal sealed record KubernetesChecked(string Scheme, string Context, int RemotePort, KubernetesMetadata Block);

internal sealed class KubernetesSource(IPortAllocator portAllocator) : ServiceSource<KubernetesChecked>, IServiceSource
{
    public override bool IsSwitchRemedy => true;

    protected override IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition)
    {
        List<FollowUpField> fields = [new("kubernetes:context", "Kubernetes context", true, InputType.Text)];

        if (definition.Kubernetes?.Port is null)
        {
            fields.Add(new("kubernetes:port", "Kubernetes service port", true, InputType.Number));
        }

        fields.Add(new("kubernetes:namespace", "Kubernetes namespace (optional)", false, InputType.Text));
        fields.Add(new("kubernetes:scheme", "Endpoint scheme (optional)", false, InputType.Text));

        return fields;
    }

    protected override KubernetesChecked Check(SourceInput input) =>
        CheckEntry(input.ServiceName, input.Definition, input.Config);

    protected override IResourceBuilder<ServiceResource> Build(
        IDistributedApplicationBuilder builder, SourceInput input, KubernetesChecked checkedValues)
    {
        var serviceName = input.ServiceName;
        var scheme = checkedValues.Scheme;

        var args = ForwardArgs(
            checkedValues.Block.Service!, checkedValues.Context, checkedValues.RemotePort,
            input.Config.Kubernetes.Namespace, portAllocator, out var localPort);

        // Built by hand rather than via AddExecutable so the resource can be a
        // ServiceExecutableResource, which adds the IResourceWithServiceDiscovery that
        // ExecutableResource lacks.
        //
        // Named for the service itself, like every other source. Aspire keys service discovery off
        // the resource name, so a suffix here would publish this service's endpoint as
        // "services__orders-portforward__..." and break consumers that resolve it as "orders".
        var executableBuilder = builder
            .AddResource(new ServiceExecutableResource(serviceName, "kubectl", builder.AppHostDirectory))
            .WithArgs(args)
            // Named for the scheme rather than always "http": the tunnel is byte-transparent, so a
            // pod serving TLS is reachable as https://localhost:<localPort> and consumers must be
            // able to ask for it under that name (#160). See EndpointScheme.
            .WithEndpoint(port: localPort, targetPort: localPort, scheme: scheme, name: scheme, isProxied: false);

        // What makes a consumer's WaitFor mean anything (#490). Aspire counts an executable as
        // Running once its process has started, which for kubectl is before it has resolved the
        // Service or opened the tunnel, so without this a consumer starts against a port nothing is
        // listening on — and, for a Service name that does not exist, against one nothing ever will.
        // A TCP connect is the same question the backing-service source asks, for the same reason
        // (see LocalPortHealthCheck): it asks whether the tunnel is up, not what is behind it.
        //
        // Added before the bridge so the facade copies it with the rest, and on the real resource
        // alone for the monitor loop's sake: only that one is ever registered, so only it is probed.
        var healthCheckKey = HealthCheckKey(serviceName);

        builder.Services
            .AddHealthChecks()
            .AddCheck(
                healthCheckKey,
                new LocalPortHealthCheck(serviceName, localPort, subject: "Service"),
                timeout: KubectlPortForward.ProbeTimeout);

        executableBuilder.WithHealthCheck(healthCheckKey);

        // And what happens to the tunnel afterwards: an exit before it was up is a failed start, an
        // exit after it was up is restarted with a backoff. See PortForwardSupervisor.
        PortForwardSupervisor.Install(builder, executableBuilder.Resource, serviceName);

        return ResolvedService.Bridge(executableBuilder, serviceName, "kubernetes");
    }

    /// <summary>The key the tunnel's TCP health check is registered under.</summary>
    internal static string HealthCheckKey(string serviceName) => $"{serviceName}-port-forward-tcp";

    public Type? DeclaredResourceType(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition) =>
        definition.Kubernetes is null ? null : typeof(ServiceExecutableResource);

    /// <summary>
    /// Whether the entry names a kubernetes service to forward to: the one precondition
    /// <see cref="RequireKubernetesBlock"/> enforces, written once.
    /// </summary>
    internal static bool HasService(ServiceDefinition definition) =>
        !string.IsNullOrWhiteSpace(definition.Kubernetes?.Service);

    internal static string[] BuildPortForwardArgs(
        string serviceName,
        ServiceDefinition definition,
        ServiceDeveloperConfig config,
        IPortAllocator portAllocator,
        out int localPort,
        out int remotePort)
    {
        var kubernetes = RequireKubernetesBlock(serviceName, definition);

        (var context, remotePort) = ResolveForwardTarget(serviceName, kubernetes, definition, config);

        return ForwardArgs(kubernetes.Service!, context, remotePort, config.Kubernetes.Namespace, portAllocator, out localPort);
    }

    private static string[] ForwardArgs(
        string service, string context, int remotePort, string? @namespace, IPortAllocator portAllocator, out int localPort)
    {
        localPort = portAllocator.AllocatePort();

        return KubectlPortForward.Args(service, localPort, remotePort, context, @namespace);
    }

    /// <summary>
    /// Every configuration check <see cref="Resolve"/> makes, in the same order, without allocating a port.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">The configuration would not resolve.</exception>
    private static KubernetesChecked CheckEntry(string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        // Judged before the context and port so a bad catalog scheme hides the source from the picker
        // (the dialog's own scheme answer is not consulted when it opens).
        var scheme = ResolveScheme(serviceName, definition, config);
        var kubernetes = RequireKubernetesBlock(serviceName, definition);
        var (context, remotePort) = ResolveForwardTarget(serviceName, kubernetes, definition, config);

        return new KubernetesChecked(scheme, context, remotePort, kubernetes);
    }

    private static string ResolveScheme(string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config) =>
        EndpointScheme.Resolve(
            serviceName, "kubernetes", config.Kubernetes.Scheme, RequireKubernetesBlock(serviceName, definition).Scheme,
            definition.Origin);

    private static (string Context, int RemotePort) ResolveForwardTarget(
        string serviceName, KubernetesMetadata kubernetes, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Kubernetes.Context))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': source 'kubernetes' requires 'kubernetes.context' in " +
                $"servicesources.local.json.").AsMissingSetting("kubernetes:context");
        }

        var remotePort = config.Kubernetes.Port ?? kubernetes.Port ?? throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}': no port configured for source 'kubernetes' — set " +
            $"'kubernetes.port' in servicesources.local.json or in {Raw.Origin(definition.Origin)}.").AsMissingSetting("kubernetes:port");

        if (remotePort is < 1 or > 65535)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': port value '{remotePort}' is not a valid port (must be between 1 and 65535).");
        }

        return (config.Kubernetes.Context, remotePort);
    }

    /// <summary>
    /// The service's <c>kubernetes</c> block, which both the port-forward arguments and the endpoint
    /// scheme are read from, so its absence is the first thing either reports.
    /// </summary>
    private static KubernetesMetadata RequireKubernetesBlock(string serviceName, ServiceDefinition definition)
    {
        // HasService is the one precondition; the null test beside it lets the compiler carry the
        // non-null block out, so a later change to HasService cannot return null from here.
        if (definition.Kubernetes is not { } kubernetes || !HasService(definition))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}' source is 'kubernetes' but {Raw.Origin(definition.Origin)} has no " +
                $"kubernetes.service entry.").AsMissingSetting("kubernetes:service");
        }

        return kubernetes;
    }
}
