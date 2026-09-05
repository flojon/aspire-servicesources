using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// Healthy once something accepts a TCP connection on a local port — which, for a tunnelled backing
/// service, means the <c>kubectl port-forward</c> behind it is listening.
/// </summary>
/// <remarks>
/// This is what makes a consumer's <c>WaitFor</c> mean anything under the <c>"kubernetes"</c>
/// source, and it is a correctness fix rather than a nicety. Measured on a live host: with a tunnel
/// that only began listening after 8 seconds, the connection-string resource reported <c>Running</c>
/// at 3.4s — as soon as its template resolved, knowing nothing about the tunnel — and the consumer
/// started with it, about five seconds before anything was there to connect to. With this check
/// attached, the same consumer started at 11.5s. <c>WaitFor</c> waits for <c>Running</c> <em>and</em>
/// healthy, and the health check is the only part of that pair which knows about the socket.
/// <para>
/// A connect and an immediate disconnect, deliberately: this asks whether the tunnel is up, not
/// whether the database behind it will accept credentials. A protocol-level check would need a
/// client per backend — Postgres, MySQL, RabbitMQ, Redis — which is the per-dialect knowledge this
/// whole design exists to avoid, and it would also fail for a reason the developer has to fix
/// elsewhere while reading as "the tunnel is down".
/// </para>
/// <para>
/// The loopback address rather than the host name <c>kubectl</c> prints: <c>kubectl port-forward</c>
/// binds <c>127.0.0.1</c> unless told otherwise, and on a machine where <c>localhost</c> resolves to
/// <c>::1</c> first, connecting by name would try an address nothing is listening on and wait for
/// that attempt to fail before falling back.
/// </para>
/// </remarks>
internal sealed class LocalPortHealthCheck(string backingServiceName, int port) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (SocketException ex)
        {
            // The exception travels with the result rather than being swallowed: Aspire surfaces a
            // health check's description in the dashboard, and "connection refused" against a named
            // port is the difference between "the tunnel has not come up yet" and "kubectl exited".
            return HealthCheckResult.Unhealthy(
                $"Backing service '{backingServiceName}': nothing is listening on 127.0.0.1:{port} yet, so the "
                + "kubectl port-forward has not come up. Its own resource carries kubectl's output.",
                ex);
        }
    }
}
