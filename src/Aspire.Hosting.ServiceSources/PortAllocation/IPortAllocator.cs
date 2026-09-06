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

    /// <summary>
    /// Whether <paramref name="port"/> can be bound locally right now.
    /// </summary>
    /// <remarks>
    /// For the one case that cannot choose its own port: whole-string mode forwards the remote
    /// port to the same local port, because the connection string it must serve is a single secret
    /// value written against the cluster and there is no placeholder in it to substitute a
    /// different one into. Giving up <see cref="AllocatePort"/>'s collision avoidance is the real
    /// cost of the mode, so the collision is worth reporting before it happens rather than leaving
    /// a developer to read a port-forward's log.
    /// <para>
    /// <b>Deliberately not defaulted.</b> A default would have to be <see langword="true"/> — there
    /// is no safe guess — and an implementation that never answered the question would then assert
    /// that every port is free, which is fail-open in the one place this method exists to close.
    /// <c>IGitClient.GetHeadCommitSha</c> defaults precisely because its default is the fail-safe
    /// answer; this one has no such answer, so every implementer says what it means.
    /// </para>
    /// <para>
    /// The same TOCTOU caveat as <see cref="AllocatePort"/> applies, and for the same reason it is
    /// accepted.
    /// </para>
    /// </remarks>
    bool IsAvailable(int port);
}
