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

    /// <summary>
    /// Allocates <paramref name="count"/> free local TCP ports, all different from each other.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as calling <see cref="AllocatePort"/> in a loop</b>, which is why it exists.
    /// That method releases its socket before returning, so the OS is free to hand the same port
    /// back on the next call: measured over 2000 sequential allocations on a Linux host, 721 of them
    /// repeated a port already returned. With one port per backing service that is only the accepted
    /// TOCTOU race; within one backing service it is two equal local ports in a single
    /// <c>kubectl port-forward svc/x 5000:5672 5000:15672</c>, which cannot bind its second pair and
    /// fails in kubectl's words about a command line the developer never wrote.
    /// <para>
    /// Distinctness comes from holding every socket open until all of them have been read, which is
    /// the one thing a loop cannot do. The same release-time race as <see cref="AllocatePort"/>
    /// remains against the rest of the machine, and is the same accepted trade.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> a default interface member. A default of "call
    /// <see cref="AllocatePort"/> <paramref name="count"/> times" would compile, satisfy every
    /// implementer, and reintroduce exactly the duplicate this exists to prevent — silently. An
    /// implementation that has no use for it should throw rather than inherit that.
    /// </para>
    /// </remarks>
    IReadOnlyList<int> AllocatePorts(int count);
}
