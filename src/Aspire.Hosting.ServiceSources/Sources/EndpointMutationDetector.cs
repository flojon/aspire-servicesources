using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.ApplicationModel;

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
    private const int MaxNameLength = 64;

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
        var capabilities = new List<string>();

        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>().ToArray())
        {
            if (snapshot.TryGetValue(endpoint, out var recorded) && Restore(endpoint, recorded))
            {
                capabilities.Add($"endpoint '{Label(recorded.Name)}' changed after resolve");
            }
        }

        if (capabilities.Count == 0)
        {
            return;
        }

        // ReporterFor, not For: subscribing during this event's own dispatch is inert.
        var warnings = ServiceSourcesWarnings.ReporterFor(builder);

        foreach (var capability in capabilities)
        {
            warnings.AddSkip(facade.Name, source, capability);
        }

        // Flushed here because a flush handler subscribed ahead of this one has already run.
        warnings.Flush(services);
    }

    /// <summary>
    /// Restores <paramref name="recorded"/> onto <paramref name="endpoint"/>, and reports whether
    /// anything differed.
    /// </summary>
    private static bool Restore(EndpointAnnotation endpoint, Fingerprint recorded)
    {
        var current = Capture(endpoint);

        if (current == recorded)
        {
            return false;
        }

        if (current.Name != recorded.Name)
        {
            endpoint.Name = recorded.Name;
        }

        if (current.Protocol != recorded.Protocol)
        {
            endpoint.Protocol = recorded.Protocol;
        }

        if (current.UriScheme != recorded.UriScheme)
        {
            endpoint.UriScheme = recorded.UriScheme;
        }

        if (current.TargetHost != recorded.TargetHost)
        {
            endpoint.TargetHost = recorded.TargetHost;
        }

        if (current.IsExternal != recorded.IsExternal)
        {
            endpoint.IsExternal = recorded.IsExternal;
        }

        // IsExplicitlyProxied, never IsProxied: its setter keeps both backing fields in lockstep,
        // and null reconstructs the untouched state exactly.
        if (current.IsExplicitlyProxied != recorded.IsExplicitlyProxied)
        {
            endpoint.IsExplicitlyProxied = recorded.IsExplicitlyProxied;
        }

        if (current.ExcludeReferenceEndpoint != recorded.ExcludeReferenceEndpoint)
        {
            endpoint.ExcludeReferenceEndpoint = recorded.ExcludeReferenceEndpoint;
        }

        // Re-read rather than reuse `current`: these four derive from the fields above, so one may
        // already be back in line, and writing anyway would materialise a value left unset.
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

        return true;
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

    /// <summary>
    /// <paramref name="name"/> made safe to interpolate into a log line.
    /// </summary>
    private static string Label(string name)
    {
        // The only caller-controlled string this package logs: a newline in it forges log lines.
        var label = new StringBuilder(Math.Min(name.Length, MaxNameLength) + 1);

        foreach (var character in name)
        {
            if (label.Length == MaxNameLength)
            {
                label.Append('…');
                break;
            }

            label.Append(char.IsControl(character) ? '?' : character);
        }

        return label.ToString();
    }

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
