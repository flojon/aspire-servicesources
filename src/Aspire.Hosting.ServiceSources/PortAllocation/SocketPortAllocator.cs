using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.ServiceSources.PortAllocation;

internal sealed class SocketPortAllocator : IPortAllocator
{
    public int AllocatePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
