using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.BackingServices;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Resolves a service reachable at a fixed, pre-known URL.
/// </summary>
/// <remarks>
/// The odd one out: every other source registers its resource, but there is nothing here for Aspire
/// to run. Aspire's <c>ExternalServiceResource</c> would be the natural home, but it is
/// <c>sealed</c> and carries no <see cref="EndpointAnnotation"/>, so it cannot satisfy
/// <see cref="IResourceWithServiceDiscovery"/> (tracked here as #72). So this source keeps building
/// the <see cref="EndpointAnnotation"/> by hand, with the <see cref="AllocatedEndpoint"/> set
/// eagerly since DCP will never allocate one, and leaves the resource unregistered.
/// <para>
/// That is issue #58, which stays open for this source as #72: a <b>container</b> consumer of one
/// of these fails inside DCP with
/// <c>"Host endpoint 'x' on resource 'y' should have an associated DCP Service resource already set
/// up"</c>. <see cref="UnregisteredServiceStartupGuard"/> catches that case up front and explains it.
/// Host-process consumers work and are unaffected.
/// </para>
/// <para>
/// Registering the resource (#58's option 1) clears that DCP failure but replaces it with a worse
/// one: the consuming container is never created and nothing says why. Delegating to
/// <c>ExternalServiceResource</c> (option 2) is the route that would work, and is what Aspire's own
/// type currently blocks.
/// </para>
/// <para>
/// The other consequence of leaving the resource unregistered is that nothing ever publishes a
/// state for it, so a consumer's <c>WaitFor</c> on one waited for the life of the run (#170).
/// <see cref="ServiceResource"/> is shared by every source, so it cannot declare
/// <see cref="IResourceWithoutLifetime"/> unconditionally the way this resource once did — that
/// would also suppress <c>WaitFor</c> on <c>container</c>/<c>kubernetes</c>/<c>local</c>-sourced
/// services, which must keep working. Instead <see cref="UnregisteredServiceStartupGuard"/> removes a
/// consumer's <see cref="WaitAnnotation"/> on a <c>"url"</c>-sourced service before start, because
/// Aspire also reads it as a dependency and a container consumer fails on that — and does the same
/// for <see cref="DisabledSource"/>, the other source sharing this class's "no real resource"
/// shape.
/// </para>
/// </remarks>
internal sealed class UrlSource : IServiceSource
{
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        var uri = ResolveUrl(serviceName, definition, config);

        var facade = new ServiceResource(serviceName);
        var endpoint = new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: uri.Scheme, name: uri.Scheme, transport: "http", port: uri.Port, targetPort: uri.Port)
        {
            TargetHost = uri.Host,
            IsProxied = false,
        };
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(
            endpoint, uri.Host, uri.Port, EndpointBindingMode.SingleAddress, targetPortExpression: null);
        facade.Annotations.Add(endpoint);

        UnregisteredServiceStartupGuard.EnsureRegistered(builder);

        return ResolvedService.BridgeUnregistered(builder, facade, serviceName, "url");
    }

    /// <summary>
    /// <paramref name="url"/> with any credentials in it replaced, for the messages that quote it
    /// back.
    /// </summary>
    /// <remarks>
    /// Both refusals below echo the value, because a developer with several services needs to know
    /// which one is being refused — and a URL is where credentials live. The wrong-scheme refusal is
    /// the one that matters: pointing a <c>"url"</c> service at a Redis or AMQP endpoint is an
    /// ordinary mistake, and those endpoints carry credentials as a matter of course.
    /// <para>
    /// <see cref="ConnectionStringRedaction"/> rather than <see cref="GitUrl.RedactAll"/>, which
    /// only finds a <c>scheme://user:pass@host</c>. A credential arrives in three other ways here
    /// and that one answers none of them: in a query, as in
    /// <c>redis://cache:6379?password=…</c>; under a keyword, as in a whole Azure connection string
    /// pasted into the field; and with no <c>//</c> to hang an authority on, as in
    /// <c>//token:x-oauth-basic@host</c>, where the secret is the <em>username</em>.
    /// </para>
    /// <para>
    /// A value carrying none of the characters that could delimit or introduce one is echoed as
    /// written, because there is nothing in it to hide and the echo is how a bare
    /// <c>orders.example.com</c> — a URL missing its scheme, the most ordinary mistake here — gets
    /// diagnosed. Passing that through the redaction would answer it with <c>***</c>.
    /// </para>
    /// </remarks>
    private static Raw Redacted(string url)
        => Raw.Escaped(url.AsSpan().IndexOfAny(CouldCarryACredential) < 0
            ? url
            : ConnectionStringRedaction.Redact(url));

    /// <summary>
    /// The characters without which a value can hold neither a userinfo nor a pair.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> CouldCarryACredential =
        System.Buffers.SearchValues.Create("@=;&?, \t\r\n");

    internal static Uri ResolveUrl(string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        var rawUrl = config.Url.Url ?? definition.Url?.Url;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}' source is 'url' but no URL is configured — set " +
                $"'url.url' in servicesources.local.json or in {Raw.Origin(definition.Origin)}.");
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'url' value '{Redacted(rawUrl)}' is not a valid absolute URL.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'url' value '{Redacted(rawUrl)}' must use the http or https scheme.");
        }

        return uri;
    }
}
