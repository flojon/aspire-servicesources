using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ServiceSources.BackingServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// The check that decides whether a tunnelled backing service is up — and so, through
/// <c>WaitFor</c>, whether its consumers may start.
/// </summary>
/// <remarks>
/// Against a real loopback socket, because that is the entire subject: the check asks whether
/// something accepts a TCP connection, and a fake of that would assert nothing. A
/// <see cref="TcpListener"/> on port 0 is in-process, sub-millisecond and needs no cluster, which is
/// what the "no real socket" rule in the design is actually about — no <c>kubectl</c>, no network,
/// no shared port.
/// </remarks>
public class LocalPortHealthCheckTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("orders-db-tunnel-tcp", _ => null!, null, null),
    };

    /// <summary>A listener bound to a free loopback port, and the port it got.</summary>
    private static (TcpListener Listener, int Port) Listening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>
    /// A port nobody is listening on: a socket bound to it and never told to listen. Held open by
    /// the caller, whose disposal releases it.
    /// </summary>
    /// <remarks>
    /// Bound rather than hard-coded, because "surely nothing is on 59999" is the assumption that
    /// fails on one machine and nowhere else. Bound-and-not-listening rather than bound-and-released,
    /// which is the same idea and is wrong: a released port can be claimed by anything else in the
    /// process — <c>SocketPortAllocator</c> draws from the same ephemeral range, and this suite runs
    /// collections in parallel — and something listening on it would make the check report
    /// <em>healthy</em>, which is what these tests assert against. That is a spurious failure, not a
    /// spurious pass. Holding the socket removes the race rather than reasoning about it, and the
    /// kernel answers a connection to a bound port with no listener behind it by refusing it, which
    /// is exactly the condition under test.
    /// </remarks>
    private static (Socket Held, int Port) Closed()
    {
        var held = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        held.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return (held, ((IPEndPoint)held.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task SomethingListening_IsHealthy()
    {
        var (listener, port) = Listening();

        try
        {
            var result = await new LocalPortHealthCheck("orders-db", port).CheckHealthAsync(Context);

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NothingListening_IsUnhealthy()
    {
        var (held, port) = Closed();

        using (held)
        {
            var result = await new LocalPortHealthCheck("orders-db", port).CheckHealthAsync(Context);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }
    }

    /// <summary>
    /// The unhealthy description names the backing service, the port, and where to look next.
    /// </summary>
    /// <remarks>
    /// It is what the dashboard shows, and it is shown while a developer is watching a resource
    /// fail to come up — so "unhealthy" alone would leave them with the one question the message
    /// can answer for free: which of their backing services, and whose log carries the reason.
    /// </remarks>
    [Fact]
    public async Task TheUnhealthyResult_SaysWhichBackingServiceAndWhereItsOutputIs()
    {
        var (held, port) = Closed();

        using (held)
        {
            var result = await new LocalPortHealthCheck("orders-db", port).CheckHealthAsync(Context);

            Assert.Contains("orders-db", result.Description);
            Assert.Contains($"127.0.0.1:{port}", result.Description);
            Assert.Contains("kubectl", result.Description);
            Assert.IsType<SocketException>(result.Exception);
        }
    }

    /// <summary>
    /// Cancellation propagates rather than being reported as an unhealthy tunnel.
    /// </summary>
    /// <remarks>
    /// The two mean different things: unhealthy is a claim about the tunnel, and cancellation is
    /// the host giving up on the question. Aspire's own runner turns any other exception into an
    /// unhealthy entry and rethrows a cancellation matching its token, so letting it through is
    /// what keeps a shutdown from being recorded as a failed probe.
    /// </remarks>
    [Fact]
    public async Task ACancelledProbe_ThrowsRatherThanReportingUnhealthy()
    {
        var (listener, port) = Listening();

        try
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new LocalPortHealthCheck("orders-db", port).CheckHealthAsync(Context, cancelled.Token));
        }
        finally
        {
            listener.Stop();
        }
    }
}
