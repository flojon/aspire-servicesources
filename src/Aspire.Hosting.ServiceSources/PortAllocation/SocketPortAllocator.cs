using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.ServiceSources.PortAllocation;

internal sealed class SocketPortAllocator : IPortAllocator
{
    public int AllocatePort() => AllocatePorts(1)[0];

    /// <remarks>
    /// Every socket is bound before any is released, which is what makes the ports distinct — the
    /// OS cannot hand out a port it is still holding for an open socket. They are released in a
    /// <c>finally</c> rather than after the loop: a bind that throws partway would otherwise leak
    /// every socket taken before it, permanently, on a path a developer will retry.
    /// </remarks>
    public IReadOnlyList<int> AllocatePorts(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var sockets = new List<Socket>(count);

        try
        {
            var ports = new int[count];

            for (var index = 0; index < count; index++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(socket);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                ports[index] = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }

            return ports;
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }
}
