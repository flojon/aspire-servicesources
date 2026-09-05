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
