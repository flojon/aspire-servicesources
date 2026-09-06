using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ServiceSources.PortAllocation;

namespace Aspire.Hosting.ServiceSources.Tests.PortAllocation;

public class SocketPortAllocatorTests
{
    [Fact]
    public void AllocatePort_ReturnsPortInValidRangeAndBindable()
    {
        var allocator = new SocketPortAllocator();

        var port = allocator.AllocatePort();

        Assert.InRange(port, 1, 65535);

        // The allocator releases its own socket before returning, so the port must be
        // immediately bindable again (modulo the TOCTOU race the design doc accepts).
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public void AllocatePort_CalledTwice_ReturnsDifferentPorts()
    {
        var allocator = new SocketPortAllocator();

        var first = allocator.AllocatePort();
        var second = allocator.AllocatePort();

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Several ports at once are all different from each other.
    /// </summary>
    /// <remarks>
    /// The property the method exists for, and the one a loop over <c>AllocatePort</c> does not
    /// have: that method releases its socket before returning, so the OS is free to hand the same
    /// port back — measured over 2000 sequential allocations on a Linux host, 721 repeated a port
    /// already returned. Two equal local ports in one <c>kubectl port-forward</c> is an invocation
    /// that cannot bind its second pair.
    /// <para>
    /// Sixteen rather than two, because a duplicate from a walk through the ephemeral range is not
    /// something two draws would reliably show.
    /// </para>
    /// </remarks>
    [Fact]
    public void AllocatePorts_ReturnsPortsThatAreAllDifferent()
    {
        var ports = new SocketPortAllocator().AllocatePorts(16);

        Assert.Equal(16, ports.Count);
        Assert.Equal(16, ports.Distinct().Count());
        Assert.All(ports, port => Assert.InRange(port, 1, 65535));
    }

    /// <remarks>
    /// Every socket is released before the method returns, exactly as the single-port spelling does,
    /// so the same accepted TOCTOU trade applies and nothing is held for the life of the AppHost.
    /// </remarks>
    [Fact]
    public void AllocatePorts_ReleasesEverySocketBeforeReturning()
    {
        var ports = new SocketPortAllocator().AllocatePorts(4);

        foreach (var port in ports)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AllocatePorts_RefusesACountThatIsNotAPositiveNumber(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SocketPortAllocator().AllocatePorts(count));

}
