using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// Connects to a backing service running in a Kubernetes cluster, through a
/// <c>kubectl port-forward</c> this AppHost opens and Aspire manages.
/// </summary>
/// <remarks>
/// Three resources' worth of behaviour behind one handle: the tunnel process, the connection string
/// that addresses its local end, and a health check that reports whether the tunnel is listening.
/// The AppHost sees only the connection string — the same handle every other source returns — so
/// switching a backing service to this source changes no AppHost code.
/// <para>
/// <b>The health check is required, not a nicety.</b> Without it a consumer's <c>WaitFor</c> on this
/// source is decorative: the connection-string resource reaches <c>Running</c> as soon as its
/// template resolves, which is immediately and knows nothing about the tunnel. See
/// <see cref="LocalPortHealthCheck"/>, which carries the measurement.
/// </para>
/// <para>
/// The local port is allocated rather than configured, so two backing services forwarded at once
/// cannot collide — and that is why a connection string here writes <c>${port}</c> rather than a
/// number. Substituted eagerly, as a literal, because the port is known synchronously: what a
/// consumer receives is an ordinary connection string with no deferred parts in it.
/// </para>
/// </remarks>
internal sealed class KubernetesBackingServiceSource(IPortAllocator portAllocator) : IBackingServiceSource
{
    public IResourceBuilder<IResourceWithConnectionString> Resolve(
        IDistributedApplicationBuilder builder,
        string name,
        BackingServiceDeveloperConfig config)
    {
        var kubernetes = config.Kubernetes;

        // Every field before anything is allocated or added, so that an entry missing two of them
        // reports the first rather than a port allocation on the way to reporting it. The order is
        // the order they are written in, which is the order a developer filling the block in would
        // hit them.
        var service = Required(name, kubernetes.Service, "service", "the Kubernetes Service to forward to");
        var remotePort = RequiredPort(name, kubernetes.Port);
        var context = Required(name, kubernetes.Context, "context", "the kubectl context to forward through");
        var connectionString = Required(
            name, kubernetes.ConnectionString, "connectionString",
            "the connection string consumers receive, with '${port}' standing for the local end of the tunnel");

        var template = ConnectionStringTemplate.Parse(connectionString, name, ConfigKey(name, "connectionString"));

        var localPort = portAllocator.AllocatePort();
        var expression = new ReferenceExpressionBuilder();
        var ports = 0;

        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case ConnectionStringTemplate.Literal literal:
                    ConnectionStringTemplate.AppendLiteral(expression, literal.Text);
                    break;

                // Eager, and as a literal: the port is known here, so nothing about it has to be
                // deferred to resolution time. A named one is the multi-port form, which arrives
                // with the port map it would resolve against (#233).
                case ConnectionStringTemplate.Port { Name: null }:
                    ConnectionStringTemplate.AppendLiteral(
                        expression, localPort.ToString(CultureInfo.InvariantCulture));
                    ports++;
                    break;

                case ConnectionStringTemplate.Port port:
                    throw new ServiceSourcesConfigurationException(
                        $"Backing service '{name}': the connection string carries '{port.AsWritten}', which names one "
                        + "of several forwarded ports, and forwarding more than one port is not supported yet. This "
                        + $"backing service forwards the single port '{ConfigKey(name, "port")}' names, so write "
                        + "'${port}'.");

                case ConnectionStringTemplate.Secret secret:
                    throw new ServiceSourcesConfigurationException(
                        $"Backing service '{name}': the connection string carries '{secret.AsWritten}', and reading a "
                        + "value out of a Kubernetes secret is not supported yet. Put the value in the connection "
                        + "string, or set the whole connection string from a configuration layer that already holds "
                        + $"it — user secrets, or {Environmentally(ConfigKey(name, "connectionString"))}.");

                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        if (ports == 0)
        {
            throw NothingAddressesTheTunnel(name, connectionString);
        }

        var backingService = builder.AddConnectionString(name, expression.Build());

        // Named after the backing service and marked as its child, because that is what it is: a
        // developer reading the dashboard should see one thing they configured, with the process
        // that serves it underneath, rather than two resources they have to work out the relation
        // between. Aspire keys nothing off this name — unlike the service-side source, where the
        // executable *is* the service and its name is what service discovery publishes.
        var tunnel = builder
            .AddExecutable(
                $"{name}-tunnel",
                "kubectl",
                builder.AppHostDirectory,
                KubectlPortForward.Args(service, localPort, remotePort, context, kubernetes.Namespace))
            .WithParentRelationship(backingService);

        var healthCheckKey = $"{name}-tunnel-tcp";

        builder.Services
            .AddHealthChecks()
            .AddCheck(healthCheckKey, new LocalPortHealthCheck(name, localPort));

        // On the connection string rather than on the tunnel, because the connection string is what
        // a consumer waits for — measured, and the reason this source has a health check at all.
        // The tunnel carries it too, so that the dashboard reports the process as unhealthy while
        // kubectl is still opening the socket rather than as running-and-fine.
        tunnel.WithHealthCheck(healthCheckKey);

        return backingService.WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// The configuration key one of this block's fields is read from, for a message that has to
    /// name the layer that set it rather than only the file a developer usually writes it in.
    /// </summary>
    private static string ConfigKey(string name, string field) =>
        $"{DeveloperConfiguration.BackingServicesKey}:{name}:Kubernetes:{field}";

    /// <summary>The same key spelled as the environment variable that sets it.</summary>
    private static string Environmentally(string configKey) =>
        configKey.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// The value of a field this source cannot work without, or the error naming it.
    /// </summary>
    /// <param name="whatItIs">
    /// What the field holds, in a phrase that completes "…requires 'kubernetes.<c>field</c>' —". A
    /// developer who has just switched a backing service to this source is reading these messages
    /// as the block's documentation, one field per run, so each one says what to write and not only
    /// that something is missing.
    /// </param>
    private static string Required(string name, string? value, string field, string whatItIs)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var key = ConfigKey(name, field);

        throw new ServiceSourcesConfigurationException(
            $"Backing service '{name}': source 'kubernetes' requires 'kubernetes.{field}' — {whatItIs}. Add it "
            + $"under \"{name}\" in \"{DeveloperConfiguration.BackingServicesKey}\" in "
            + $"'{DeveloperConfiguration.FileName}', or set {Environmentally(key)}.");
    }

    /// <summary>
    /// The remote port, which is required and has to be a port.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Required"/> because the value is an <c>int?</c>: it is absent or
    /// it is a number, and a number outside the port range is a different mistake from a missing
    /// field. Unlike the service side there is no catalog value to fall back to — the catalog
    /// carries no backing-service data at all, by decision — so absence is simply absence.
    /// </remarks>
    private static int RequiredPort(string name, int? port)
    {
        var key = ConfigKey(name, "port");

        if (port is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{name}': source 'kubernetes' requires 'kubernetes.port' — the port the Service "
                + "listens on inside the cluster, which is what the tunnel forwards to. Add it under "
                + $"\"{name}\" in \"{DeveloperConfiguration.BackingServicesKey}\" in "
                + $"'{DeveloperConfiguration.FileName}', or set {Environmentally(key)}. The local end of the tunnel "
                + "is allocated rather than configured, so a connection string names it as '${port}'.");
        }

        if (port is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{name}': 'kubernetes.port' is '{port}', which is not a port — a port is between "
                + $"1 and 65535. The key is '{key}'.");
        }

        return port.Value;
    }

    /// <summary>
    /// The error for a connection string that never mentions the tunnel this source opens.
    /// </summary>
    /// <remarks>
    /// Refused rather than accepted, because the alternative fails silently and in the worst way it
    /// could. A template that writes the cluster's own port — <c>Port=5432</c>, copied from a
    /// manifest — addresses <c>localhost:5432</c> on the developer's machine: nothing is listening,
    /// or, far worse, their own Postgres container is, and the AppHost connects to the wrong
    /// database while reporting every resource healthy. The tunnel would sit alongside, forwarding a
    /// port nothing dials.
    /// <para>
    /// The whole-string secret form is the one template that will legitimately carry no
    /// <c>${port}</c> — it arrives already addressed, and is answered by forwarding the same port
    /// number locally rather than by substitution. That form needs secrets, which this source does
    /// not read yet, so today every template that reaches here without a <c>${port}</c> is the
    /// mistake above.
    /// </para>
    /// </remarks>
    private static ServiceSourcesConfigurationException NothingAddressesTheTunnel(
        string name, string connectionString) =>
        new($"Backing service '{name}': source 'kubernetes' opens a kubectl port-forward on a local port allocated "
            + $"at startup, but the connection string names no '${{port}}' placeholder to put it in — so nothing "
            + $"would address the tunnel: \"{connectionString}\". Replace the port in it with '${{port}}', as "
            + "'Host=localhost;Port=${port};Database=orders'. A backing service reached at a fixed address the "
            + $"developer already has — an ingress, or an instance they run themselves — is source 'direct' rather "
            + $"than this one. The key is '{ConfigKey(name, "connectionString")}'.");
}
