using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Restores and reports endpoint state that changed between an out-of-band service resolving and
/// <c>BeforeStartEvent</c>.
/// </summary>
/// <remarks>
/// The call-site gate (<see cref="Reachability"/>, <c>GateEndpointCall</c>,
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>) can only stop calls that reach
/// this package. A guest-language AppHost invokes Aspire's own endpoint-callback capabilities
/// directly, and Aspire's <c>WithExternalHttpEndpoints</c> writes existing annotations in place, so
/// neither is interceptable at all. This watches the state instead of the call, which is why it
/// covers both without naming either.
/// <para>
/// Installed only for <see cref="Reachability.OutOfBandSources"/>, where
/// <see cref="Reachability.IsUnreachable"/> is unconditionally true for an
/// <see cref="EndpointAnnotation"/> — so any change after resolve is one that should have been
/// skipped, and there is no legitimate change to mistake it for. On <c>local</c> and
/// <c>container</c> such a change is legitimate and nothing is installed.
/// </para>
/// </remarks>
internal static class EndpointMutationDetector
{
    /// <summary>
    /// Snapshots <paramref name="facade"/>'s endpoints and subscribes the reconciliation, when
    /// <paramref name="source"/> is out of band.
    /// </summary>
    public static void Install(
        IDistributedApplicationBuilder builder, ServiceResource facade, IResource? real, string source)
    {
        if (!Reachability.OutOfBandSources.Contains(source))
        {
            return;
        }

        // Keyed by instance: Name is settable, so a rename would otherwise read as a removal
        // plus an addition and restore wrongly in both directions.
        var snapshot = new Dictionary<EndpointAnnotation, Fingerprint>(ReferenceEqualityComparer.Instance);

        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>())
        {
            snapshot[endpoint] = Capture(endpoint);
        }

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Reconcile(@event.Services, builder, facade, real, source, snapshot);
            return Task.CompletedTask;
        });
    }

    private static void Reconcile(
        IServiceProvider services,
        IDistributedApplicationBuilder builder,
        ServiceResource facade,
        IResource? real,
        string source,
        Dictionary<EndpointAnnotation, Fingerprint> snapshot)
    {
        var reverts = new List<string>();
        var everyRevertWasAnAddition = true;

        // Materialised first: Annotations is the live collection this loop removes from.
        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>().ToArray())
        {
            if (snapshot.TryGetValue(endpoint, out var recorded))
            {
                var changed = Restore(endpoint, recorded);

                if (changed.Count > 0)
                {
                    everyRevertWasAnAddition = false;

                    reverts.Add(
                        $"endpoint '{Label(recorded.Name)}' was changed after this service resolved " +
                        $"({string.Join(", ", changed)}) and has been put back");
                }

                continue;
            }

            facade.Annotations.Remove(endpoint);

            // A no-op for every path that exists today; kept so the two collections cannot diverge
            // if a future Aspire routes its create branch through WithAnnotation.
            real?.Annotations.Remove(endpoint);

            reverts.Add(
                $"endpoint '{Label(endpoint.Name)}' was added after this service resolved and has been " +
                "removed, so a reference taken to it will not resolve");
        }

        foreach (var (endpoint, recorded) in snapshot)
        {
            if (facade.Annotations.Contains(endpoint))
            {
                continue;
            }

            // Before the guards below: one removed after being changed would otherwise come back
            // carrying the change, and be looked for under a name it no longer has.
            var changed = Restore(endpoint, recorded);

            // Aspire resolves an endpoint by name with SingleOrDefault, which throws on a duplicate.
            // The loop filter above already excludes anything facade still holds, so only the name
            // guard can block it here.
            var facadeHasIt = !HoldsEndpointNamed(facade, recorded.Name);
            if (facadeHasIt)
            {
                facade.Annotations.Add(endpoint);
            }

            // `real` starts out sharing this exact instance with `facade` (ResolvedService.Bridge), so
            // Contains is checked before the name guard -- otherwise the guard would read the instance
            // it is about to restore as the collision blocking it.
            var realHasIt = real is null || real.Annotations.Contains(endpoint);
            if (real is not null && !realHasIt && !HoldsEndpointNamed(real, recorded.Name))
            {
                real.Annotations.Add(endpoint);
                realHasIt = true;
            }

            everyRevertWasAnAddition = false;

            // Each guard is reported on its own terms: a blocked facade restore is the failure a
            // consumer's GetEndpoint call will see, while a blocked `real` mirror is a quieter
            // divergence the facade restore already masks from callers.
            reverts.Add(!facadeHasIt
                ? $"endpoint '{Label(recorded.Name)}' was removed after this service resolved, but " +
                  "could not be put back because another endpoint already uses that name"
                : !realHasIt
                    ? $"endpoint '{Label(recorded.Name)}' was removed after this service resolved and " +
                      "has been put back, but could not be mirrored onto the underlying resource " +
                      "because another endpoint there already uses that name"
                    : changed.Count == 0
                        ? $"endpoint '{Label(recorded.Name)}' was removed after this service resolved " +
                          "and has been put back"
                        : $"endpoint '{Label(recorded.Name)}' was removed after this service resolved " +
                          $"and has been put back, along with the fields changed with it ({string.Join(", ", changed)})");
        }

        // ReporterFor, not For: subscribing during this event's own dispatch is inert.
        ServiceSourcesWarnings.ReporterFor(builder).ReportRevertsNow(
            services,
            facade.Name,
            source,
            reverts,
            everyRevertWasAnAddition,
            // The snapshot, not the live collection: restored names, unreachable by a guest rename.
            snapshot.Values.Select(recorded => recorded.Name).ToArray());
    }

    // Aspire matches an endpoint name case-insensitively, so a guard that matched any other way
    // would miss the duplicate it exists to stop.
    private static bool HoldsEndpointNamed(IResource resource, string name) =>
        resource.Annotations.OfType<EndpointAnnotation>()
            .Any(endpoint => string.Equals(endpoint.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Restores <paramref name="recorded"/> onto <paramref name="endpoint"/>, and names the fields
    /// that had differed.
    /// </summary>
    /// <remarks>
    /// The names are the warning's whole diagnostic value: the developer is told an endpoint changed
    /// by a package they did not call, and the fingerprint carries eleven fields to bisect by hand
    /// otherwise.
    /// </remarks>
    private static IReadOnlyList<string> Restore(EndpointAnnotation endpoint, Fingerprint recorded)
    {
        var current = Capture(endpoint);

        if (current == recorded)
        {
            return [];
        }

        var changed = new List<string>();

        if (current.Name != recorded.Name)
        {
            changed.Add(nameof(recorded.Name));
            endpoint.Name = recorded.Name;
        }

        if (current.Port != recorded.Port)
        {
            changed.Add(nameof(recorded.Port));
        }

        if (current.TargetPort != recorded.TargetPort)
        {
            changed.Add(nameof(recorded.TargetPort));
        }

        if (current.UriScheme != recorded.UriScheme)
        {
            changed.Add(nameof(recorded.UriScheme));
            endpoint.UriScheme = recorded.UriScheme;
        }

        if (current.TargetHost != recorded.TargetHost)
        {
            changed.Add(nameof(recorded.TargetHost));
            endpoint.TargetHost = recorded.TargetHost;
        }

        if (current.Transport != recorded.Transport)
        {
            changed.Add(nameof(recorded.Transport));
        }

        if (current.IsExternal != recorded.IsExternal)
        {
            changed.Add(nameof(recorded.IsExternal));
            endpoint.IsExternal = recorded.IsExternal;
        }

        // IsExplicitlyProxied, never IsProxied: its setter keeps both backing fields in lockstep,
        // and null reconstructs the untouched state exactly. Named for the caller's spelling, which
        // is the IsProxied on Aspire's callback context.
        if (current.IsExplicitlyProxied != recorded.IsExplicitlyProxied)
        {
            changed.Add("IsProxied");
            endpoint.IsExplicitlyProxied = recorded.IsExplicitlyProxied;
        }

        if (current.ExcludeReferenceEndpoint != recorded.ExcludeReferenceEndpoint)
        {
            changed.Add(nameof(recorded.ExcludeReferenceEndpoint));
            endpoint.ExcludeReferenceEndpoint = recorded.ExcludeReferenceEndpoint;
        }

        if (current.TlsEnabled != recorded.TlsEnabled)
        {
            changed.Add(nameof(recorded.TlsEnabled));
        }

        if (current.Protocol != recorded.Protocol)
        {
            changed.Add(nameof(recorded.Protocol));
            endpoint.Protocol = recorded.Protocol;
        }

        RestoreDerived(endpoint, recorded);

        return changed;
    }

    /// <summary>
    /// The four fields that fall back to the ones <see cref="Restore"/> has just written.
    /// </summary>
    /// <remarks>
    /// Written last and compared against a re-read rather than against the capture: one may already
    /// be back in line, and writing anyway would materialise a value the endpoint had left unset.
    /// </remarks>
    private static void RestoreDerived(EndpointAnnotation endpoint, Fingerprint recorded)
    {
        if (endpoint.Transport != recorded.Transport)
        {
            endpoint.Transport = recorded.Transport;
        }

        if (endpoint.TlsEnabled != recorded.TlsEnabled)
        {
            endpoint.TlsEnabled = recorded.TlsEnabled;
        }

        if (endpoint.Port != recorded.Port)
        {
            endpoint.Port = recorded.Port;
        }

        if (endpoint.TargetPort != recorded.TargetPort)
        {
            endpoint.TargetPort = recorded.TargetPort;
        }
    }

    private static Fingerprint Capture(EndpointAnnotation endpoint) => new(
        endpoint.Name,
        endpoint.Port,
        endpoint.TargetPort,
        endpoint.UriScheme,
        endpoint.TargetHost,
        endpoint.Transport,
        endpoint.IsExternal,
        endpoint.IsExplicitlyProxied,
        endpoint.ExcludeReferenceEndpoint,
        endpoint.TlsEnabled,
        endpoint.Protocol);

    // The service name in the same sentence needs the identical treatment, so the helper lives beside
    // the messages rather than here.
    private static string Label(string name) => ServiceSourcesWarnings.Label(name);

    /// <summary>
    /// Every field the endpoint-callback surface can write, plus <c>Name</c>.
    /// </summary>
    private readonly record struct Fingerprint(
        string Name,
        int? Port,
        int? TargetPort,
        string UriScheme,
        string TargetHost,
        string Transport,
        bool IsExternal,
        bool? IsExplicitlyProxied,
        bool ExcludeReferenceEndpoint,
        bool TlsEnabled,
        ProtocolType Protocol);
}
