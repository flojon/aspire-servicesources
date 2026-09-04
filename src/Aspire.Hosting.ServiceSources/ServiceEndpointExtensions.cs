using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The portable way for a consumer to name the endpoint a resolved service exposes, without
/// knowing which source resolved it.
/// </summary>
public static class ServiceEndpointExtensions
{
    /// <summary>
    /// The endpoint this service exposes: the one named <c>https</c> if there is one, else
    /// <c>http</c>, else the service's only endpoint whatever it is named.
    /// </summary>
    /// <example>
    /// <code>
    /// var commonAuth = builder.AddService("common-auth");
    ///
    /// builder.AddProject&lt;Projects.Web&gt;("web")
    ///        .WithEnvironment("Services__CommonAuth", commonAuth.GetServiceEndpoint());
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// <c>GetEndpoint("https")</c> is the non-portable spelling: the endpoint <i>name</i> a resolved
    /// service exposes is decided by the source that resolved it — a <c>"local"</c> dotnet project
    /// takes its endpoints from its launch profile, a <c>"url"</c> service is named for the URL's
    /// scheme, and <c>"kubernetes"</c> and <c>"container"</c> are named for their configured
    /// <c>scheme</c> (<c>http</c> unless set). So a consumer naming a scheme resolves only while the
    /// service happens to be on a source that produces it, and fails late when a developer switches
    /// that service in their own <c>servicesources.local.json</c> — as a
    /// <c>FailedToStart</c> on the <i>consumer</i>, from <c>ExpressionResolver</c> gathering
    /// environment. That was issue #160.
    /// </para>
    /// <para>
    /// https is preferred over http because Aspire's own service discovery resolves
    /// <c>"https+http://"</c> in that order, so a service exposing both hands back the endpoint
    /// Aspire would have picked itself.
    /// </para>
    /// <para>
    /// The endpoint is chosen when this is called, not when its value is resolved, so call it after
    /// any <see cref="ServiceConfigurationExtensions.Configure{T}"/> that adds an endpoint. The
    /// <see cref="EndpointReference"/> it returns is still lazy in the usual way — the URL is
    /// resolved once Aspire has allocated the port.
    /// </para>
    /// <para>
    /// Exported, unlike <see cref="ServiceConfigurationExtensions.Configure{T}"/>: it is
    /// non-generic, and its <see cref="EndpointReference"/> return type is one ATS models rather
    /// than drops. Verified by generating against Aspire CLI 13.5.3 and reading the output — it
    /// emits <c>getServiceEndpoint(): EndpointReferencePromise</c>, and Aspire's own
    /// <c>withEnvironment</c> accepts an <c>Awaitable&lt;EndpointReference&gt;</c>, so the value
    /// flows straight into a guest-language consumer. <c>samples/DemoAppHostTypeScript</c> exercises
    /// exactly that, and the <c>typecheck-typescript</c> CI job compiles it.
    /// </para>
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The service exposes no endpoints, or exposes several and none of them is named <c>http</c> or
    /// <c>https</c> — there is no one endpoint to mean, so name it with <c>GetEndpoint(...)</c>.
    /// </exception>
    [AspireExport]
    public static EndpointReference GetServiceEndpoint(
        this IResourceBuilder<IResourceWithServiceDiscovery> service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var names = service.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Select(endpoint => endpoint.Name)
            .ToList();

        // Aspire compares endpoint names case-insensitively, so matching them any other way here
        // would miss an endpoint that GetEndpoint would then find.
        foreach (var preferred in new[] { EndpointScheme.Https, EndpointScheme.Http })
        {
            var match = names.FirstOrDefault(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return service.GetEndpoint(match);
            }
        }

        if (names.Count == 1)
        {
            return service.GetEndpoint(names[0]);
        }

        throw new ServiceSourcesConfigurationException(Explain(service.Resource, names));
    }

    private static string Explain(IResource resource, IReadOnlyCollection<string> names)
    {
        var annotation = resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault();
        var name = annotation?.ServiceName ?? resource.Name;
        var source = annotation is null ? "" : $" Its source is '{annotation.Source}'.";

        if (names.Count == 0)
        {
            return $"Service '{name}' exposes no endpoint, so there is none to reference.{source} " +
                   "Give the service an endpoint — a 'scheme'/'port' in servicesources.yaml for a " +
                   "'kubernetes' or 'container' source, a launch profile for a 'local' one — or add one " +
                   "with Configure<IResourceWithEndpoints>(r => r.WithHttpEndpoint(...)).";
        }

        var endpointList = string.Join(", ", names.Select(n => $"'{n}'"));

        return $"Service '{name}' exposes several endpoints and none of them is named " +
               $"'{EndpointScheme.Http}' or '{EndpointScheme.Https}': {endpointList}.{source} " +
               "There is no single endpoint to mean, so name the one you want with " +
               "GetEndpoint(\"<name>\") instead.";
    }
}
