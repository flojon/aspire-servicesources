using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The literal text a container sees for a <c>"url"</c>-sourced service, computed from the facade's own
/// <see cref="EndpointAnnotation"/>.
/// </summary>
/// <remarks>
/// Never asks Aspire to resolve the endpoint: resolving the facade's reference from inside a callback
/// is cancelled and the container silently fails to start. Only scheme, host and port exist on the
/// annotation, so the literal cannot carry userinfo, a path or a query.
/// </remarks>
internal static class UrlFacadeLiteral
{
    /// <summary><c>scheme://host[:port]</c>, the port omitted when it is the scheme's default.</summary>
    public static string Literal(EndpointAnnotation endpoint)
    {
        var port = endpoint.Port;
        var host = endpoint.TargetHost;

        return port is null || port == DefaultPort(endpoint.UriScheme)
            ? $"{endpoint.UriScheme}://{host}"
            : $"{endpoint.UriScheme}://{host}:{port.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The value of <paramref name="property"/>, or <see langword="false"/> when it has no mapping.</summary>
    public static bool TryProperty(EndpointAnnotation endpoint, EndpointProperty property, [NotNullWhen(true)] out string? value)
    {
        var port = endpoint.Port?.ToString(CultureInfo.InvariantCulture);

        value = property switch
        {
            EndpointProperty.Url => Literal(endpoint),
            EndpointProperty.Scheme => endpoint.UriScheme,
            EndpointProperty.Host or EndpointProperty.IPV4Host => endpoint.TargetHost,
            EndpointProperty.Port or EndpointProperty.TargetPort => port,
            EndpointProperty.HostAndPort => port is null ? null : $"{endpoint.TargetHost}:{port}",
            _ => null,
        };

        return value is not null;
    }

    private static int? DefaultPort(string scheme) => scheme switch
    {
        "http" => 80,
        "https" => 443,
        _ => null,
    };
}
