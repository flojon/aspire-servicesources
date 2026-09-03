using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// One way of reaching a backing service that is <em>already running</em> — the database, broker or
/// cache a service connects to.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IServiceSource"/> rather than an overload of it, because the two
/// abstractions carry different things and answer to different config. A service is reached by
/// service discovery, so its sources return <see cref="IResourceBuilder{T}"/> of
/// <see cref="IResourceWithServiceDiscovery"/> and read a catalog for the shared half of their
/// settings; a backing service is reached by connection string, and has no catalog at all.
/// </para>
/// <para>
/// The <c>"local"</c> source is deliberately <b>not</b> one of these. It has no settings and builds
/// nothing — it runs the factory the AppHost passed to <c>AddBackingService</c> and returns the
/// result — so as an implementation it would be a class whose whole body is the argument it was
/// handed. Keeping it out has a second, larger benefit: the factory never has to be passed through
/// this interface, so no implementation is in a position to invoke it, and
/// <c>AddBackingService</c> can invoke it where Aspire's <c>ASPIREEXPORT010</c> analyzer can see
/// that it does. Behind an interface dispatch the analyzer sees nothing — measured, and the reason
/// this interface is shaped this way; see <c>AddBackingService</c>'s remarks.
/// </para>
/// </remarks>
internal interface IBackingServiceSource
{
    /// <summary>
    /// Adds the resource that carries this backing service's connection string, and returns a
    /// handle to it. Called once per <c>AddBackingService</c> call: the returned builder is what
    /// every consumer references, exactly as a vanilla Aspire resource would be.
    /// </summary>
    IResourceBuilder<IResourceWithConnectionString> Resolve(
        IDistributedApplicationBuilder builder,
        string name,
        BackingServiceDeveloperConfig config);
}
