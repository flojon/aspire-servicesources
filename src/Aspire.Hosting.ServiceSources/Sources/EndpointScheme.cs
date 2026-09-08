using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The scheme a source's endpoint is named for, resolved from configuration.
/// </summary>
/// <remarks>
/// Aspire names an endpoint after its scheme by default (<c>WithHttpEndpoint</c> is
/// <c>WithEndpoint(scheme: "http", name: "http")</c>), and consumers reference endpoints by name —
/// so whatever a source picks here is what a consumer's <c>GetEndpoint("…")</c> has to match.
/// Sources that hardcoded <c>http</c> made that name depend on which source resolved the service,
/// which is what issue #160 reported.
/// <para>
/// The scheme is worth configuring rather than fixing at <c>http</c> because the transports
/// involved are honest about it. A <c>kubectl port-forward</c> is a byte-transparent TCP tunnel: if
/// the pod behind it serves TLS, the handshake terminates at the pod and
/// <c>https://localhost:&lt;localPort&gt;</c> is the URL that works — naming that endpoint
/// <c>http</c> handed consumers a URL the listener rejects. The same holds for a container image
/// that serves TLS on its port. (What the tunnel cannot fix is certificate hostname validation,
/// since the client connects to <c>localhost</c>; that is the consumer's problem to configure, not
/// something a scheme can misrepresent.)
/// </para>
/// </remarks>
internal static class EndpointScheme
{
    public const string Http = "http";

    public const string Https = "https";

    /// <summary>
    /// The endpoint scheme for <paramref name="serviceName"/> under <paramref name="source"/>:
    /// <paramref name="developerScheme"/> if set, else <paramref name="catalogScheme"/>, else
    /// <see cref="Http"/>.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The configured scheme is neither <c>http</c> nor <c>https</c>.
    /// </exception>
    public static string Resolve(
        string serviceName, string source, string? developerScheme, string? catalogScheme, CatalogOrigin catalogOrigin)
    {
        var fromDeveloperConfig = !string.IsNullOrWhiteSpace(developerScheme);
        var configured = fromDeveloperConfig ? developerScheme : catalogScheme;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return Http;
        }

        var normalized = configured.Trim().ToLowerInvariant();

        if (normalized is Http or Https)
        {
            return normalized;
        }

        // Named per origin because the two live in different places, and only one of them is the
        // one the person reading this error owns. Phrased as "the X.scheme entry in Y" rather than
        // "Y's X.scheme" so this never collides with the closing quote CatalogOrigin.Describe()
        // wraps a yaml path in — "'servicesources.yaml''s" reads as a typo, not a possessive.
        var origin = fromDeveloperConfig ? "servicesources.local.json" : catalogOrigin.Describe();

        throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': scheme '{configured}' is not supported for source '{source}' — " +
            $"use '{Http}' or '{Https}'. Set the {source}.scheme entry in {origin}.");
    }
}
