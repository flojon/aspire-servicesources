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
