using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// What an <see cref="ILocalResourceKind"/> hands back from
/// <see cref="ILocalResourceKind.ResolveDeferred"/>: the resource it built against a checkout that
/// is not on disk yet, plus the checks it had to skip because of that.
/// </summary>
/// <remarks>
/// <para>
/// The handler neither holds the resource back nor starts it. Core does both, for every resource
/// the <see cref="ILocalResourceKind.ResolveDeferred"/> call added to the app model rather than
/// just <see cref="Service"/> — an integration is free to add helpers of its own alongside the
/// service, and any of them would otherwise run against a directory that does not exist yet.
/// <c>Aspire.Hosting.JavaScript</c> is the case in point: it adds a separate installer resource to
/// run <c>npm install</c>, which the app already waits for, so holding back only the app would
/// leave the installer failing at startup on the missing checkout.
/// </para>
/// </remarks>
public sealed class DeferredLocalResource
{
    /// <summary>
    /// The service's resource, built exactly as <see cref="ILocalResourceKind.Resolve"/> would have
    /// built it against a warm checkout. This is what <c>AddService()</c> returns to the AppHost, so
    /// everything a consumer needs from it — above all its endpoints — has to be on it now: nothing
    /// re-runs composition once the clone lands.
    /// </summary>
    public required IResourceBuilder<IResourceWithServiceDiscovery> Service { get; init; }

    /// <summary>
    /// Everything <see cref="ILocalResourceKind.Resolve"/> checks against the working tree, deferred
    /// to the one moment it can be checked. Core runs it once, after the clone has landed and before
    /// anything is started; throw <see cref="ServiceSourcesConfigurationException"/> from it to
    /// report a problem, which surfaces as the service's resource state and resource log rather than
    /// as an exception out of composition.
    /// </summary>
    /// <remarks>
    /// Only the checks that genuinely need the repository belong here. Anything settleable from the
    /// options block alone — a bad <c>appType</c>, a path that climbs out of the checkout — is a
    /// configuration error the developer should hear about from
    /// <see cref="ILocalResourceKind.ResolveDeferred"/> itself, at composition, rather than from a
    /// resource that failed once its clone had landed.
    /// </remarks>
    public Action? ValidateCheckout { get; init; }
}
