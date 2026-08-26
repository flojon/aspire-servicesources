using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class StubGitServerBindRetryTests
{
    [Fact]
    public void BindToFreePort_FirstAttemptSucceeds_ReturnsThatPort()
    {
        var port = StubGitServer.BindToFreePort(pickPort: () => 4242, bind: _ => { });

        Assert.Equal(4242, port);
    }

    [Fact]
    public void BindToFreePort_CollidesOnce_RetriesOnAFreshPortAndSucceeds()
    {
        var offeredPorts = new Queue<int>([4242, 4243]);
        var boundPorts = new List<int>();

        var port = StubGitServer.BindToFreePort(
            pickPort: offeredPorts.Dequeue,
            bind: candidate =>
            {
                boundPorts.Add(candidate);
                if (candidate == 4242)
                {
                    throw new HttpListenerException();
                }
            });

        Assert.Equal(4243, port);
        Assert.Equal([4242, 4243], boundPorts);
    }

    [Fact]
    public void BindToFreePort_CollidesOnEveryAttempt_GivesUpAfterTheAttemptLimitAndThrows()
    {
        var attempts = 0;

        var exception = Assert.Throws<HttpListenerException>(() =>
            StubGitServer.BindToFreePort(
                pickPort: () => 4242,
                bind: _ =>
                {
                    attempts++;
                    throw new HttpListenerException();
                }));

        Assert.Equal(5, attempts);
        Assert.IsType<HttpListenerException>(exception);
    }

    [Fact]
    public void ListenOnFreeLoopbackPort_RecoversFromAPortSomethingElseAlreadyHolds()
    {
        // The collision #93 reports, staged deterministically: a socket holds the first port offered,
        // so the retry has to survive a genuine HttpListener.Start failure rather than a stubbed one.
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var heldPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var offeredPorts = new Queue<int>([heldPort, UnheldPort()]);

        var (listener, port) = StubGitServer.ListenOnFreeLoopbackPort(offeredPorts.Dequeue);

        try
        {
            Assert.NotEqual(heldPort, port);
            Assert.True(listener.IsListening);
            Assert.Equal([$"http://127.0.0.1:{port}/"], listener.Prefixes);
        }
        finally
        {
            listener.Close();
        }
    }

    /// <summary>A port nothing is listening on at the moment it is returned.</summary>
    private static int UnheldPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
