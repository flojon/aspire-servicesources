using System.ComponentModel;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The guest-language face of <see cref="ServiceConfigurationExtensions.Configure{T}"/>: one
/// non-generic, distinctly-named method per configuration shape, each carrying
/// <see cref="AspireExportAttribute"/> so Aspire's Type System can project it into a TypeScript
/// (or Python, Go, …) AppHost.
/// </summary>
/// <remarks>
/// <para>
/// <b>C# callers should use <see cref="ServiceConfigurationExtensions.Configure{T}"/> instead</b>,
/// which covers every Aspire extension method rather than the handful mirrored here. These are
/// hidden from IntelliSense for that reason — <see cref="IResourceBuilder{T}"/> is covariant, so
/// anything declared on <c>IResourceBuilder&lt;IResourceWithServiceDiscovery&gt;</c> otherwise shows
/// up on every resource builder in the AppHost.
/// </para>
/// <para>
/// Two codegen constraints shape this API, both established by generating against Aspire CLI
/// 13.6.0 and reading the output:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Generic methods lose their type parameter.</b> <c>Configure&lt;T&gt;</c> projects as
///     <c>configure(...)</c> with no <c>T</c> — and <c>T</c> is the whole point of it, since it
///     names the capability being requested. Hence non-generic shims.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Overloads are silently dropped.</b> Only the first overload of a name reaches the
///     generated SDK, so each shape gets its own name rather than sharing one.
///     </description>
///   </item>
/// </list>
/// <para>
/// Every method here delegates to <c>Configure&lt;T&gt;</c>, so they inherit its behaviour exactly:
/// skipped with a logged warning when the service's source runs out of band (<c>"url"</c>,
/// <c>"kubernetes"</c>).
/// </para>
/// </remarks>
public static class ServiceConfigurationExports
{
    /// <summary>Sets an environment variable on the resolved service to a literal value.</summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceEnvironment(
        this IResourceBuilder<IResourceWithServiceDiscovery> service, string name, string value) =>
        service.Configure<IResourceWithEnvironment>(r => r.WithEnvironment(name, value));

    /// <summary>
    /// Sets an environment variable on the resolved service to a parameter's value — the route for a
    /// generated secret or a password the AppHost owns.
    /// </summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceEnvironmentFromParameter(
        this IResourceBuilder<IResourceWithServiceDiscovery> service,
        string name,
        IResourceBuilder<ParameterResource> parameter) =>
        service.Configure<IResourceWithEnvironment>(r => r.WithEnvironment(name, parameter));

    /// <summary>
    /// Sets an environment variable on the resolved service to another resource's endpoint URL,
    /// including a port Aspire allocated dynamically.
    /// </summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceEnvironmentFromEndpoint(
        this IResourceBuilder<IResourceWithServiceDiscovery> service,
        string name,
        EndpointReference endpoint) =>
        service.Configure<IResourceWithEnvironment>(r => r.WithEnvironment(name, endpoint));

    /// <summary>Injects service-discovery variables for another service into the resolved service.</summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceReference(
        this IResourceBuilder<IResourceWithServiceDiscovery> service,
        IResourceBuilder<IResourceWithServiceDiscovery> other) =>
        service.Configure<IResourceWithEnvironment>(r => r.WithReference(other));

    /// <summary>
    /// Injects a connection string into the resolved service — a database, cache or queue the
    /// AppHost owns.
    /// </summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceConnectionString(
        this IResourceBuilder<IResourceWithServiceDiscovery> service,
        IResourceBuilder<IResourceWithConnectionString> source) =>
        service.Configure<IResourceWithEnvironment>(r => r.WithReference(source));

    /// <summary>Holds the resolved service back until <paramref name="dependency"/> is healthy.</summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WaitForService(
        this IResourceBuilder<IResourceWithServiceDiscovery> service,
        IResourceBuilder<IResource> dependency) =>
        service.Configure<IResourceWithWaitSupport>(r => r.WaitFor(dependency));

    /// <summary>
    /// Holds the resolved service back until <paramref name="dependency"/> has run to completion —
    /// a migration or seeding step.
    /// </summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WaitForServiceCompletion(
        this IResourceBuilder<IResourceWithServiceDiscovery> service,
        IResourceBuilder<IResource> dependency,
        int exitCode = 0) =>
        service.Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(dependency, exitCode));

    /// <summary>Appends a command-line argument to the resolved service.</summary>
    [AspireExport]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceArg(
        this IResourceBuilder<IResourceWithServiceDiscovery> service, string arg) =>
        service.Configure<IResourceWithArgs>(r => r.WithArgs(arg));
}
