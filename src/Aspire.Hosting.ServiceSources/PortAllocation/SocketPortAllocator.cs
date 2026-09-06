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

    /// <summary>
    /// Answers by binding the port and letting it go again, which is the same question
    /// <see cref="AllocatePort"/> asks the OS, only about a port that was chosen elsewhere.
    /// </summary>
    /// <remarks>
    /// Loopback rather than every address, matching <see cref="AllocatePort"/> and the address a
    /// port-forward actually listens on: a port already bound on another interface is not a
    /// collision this cares about.
    /// </remarks>
    public bool IsAvailable(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
