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
}
