using System.Net;

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
}
