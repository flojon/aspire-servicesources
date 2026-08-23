using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Records which <c>AddService()</c> source produced a resource, and under which service name.
/// </summary>
/// <remarks>
/// Carried on the resource itself rather than in a side table so it survives everywhere the
/// resource goes: <see cref="ServiceConfigurationExtensions"/> reads it to explain <em>why</em> a
/// configuration call doesn't apply (naming the source the developer would change), and
/// <c>UrlSource</c>'s pre-flight uses it to recognise a url-sourced service that a container is
/// trying to reference.
/// </remarks>
internal sealed record ServiceSourceAnnotation(string ServiceName, string Source) : IResourceAnnotation;
