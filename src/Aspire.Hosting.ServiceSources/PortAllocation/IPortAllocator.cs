namespace Aspire.Hosting.ServiceSources.PortAllocation;

internal interface IPortAllocator
{
    /// <summary>
    /// Allocates a free local TCP port by binding an ephemeral socket, reading the OS-assigned
    /// port, and releasing the socket immediately. There is an inherent TOCTOU race between this
    /// release and whatever later binds the returned port (e.g. <c>kubectl port-forward</c>) —
    /// accepted per the cluster-source design doc.
    /// </summary>
    int AllocatePort();
}
