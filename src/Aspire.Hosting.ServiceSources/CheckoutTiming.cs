namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// When a <c>"repository"</c> service's first checkout happens, relative to <c>Build()</c>. See
/// <see cref="ServiceSourcesBuilderExtensions.SetCheckoutTiming"/>.
/// </summary>
public enum CheckoutTiming
{
    /// <summary>
    /// A service whose managed checkout does not exist yet is registered stopped and started once
    /// its clone lands, so the dashboard comes up immediately and checkout progress and failure show
    /// as resource state. The default.
    /// </summary>
    Deferred,

    /// <summary>
    /// <c>AddService()</c> blocks until the checkout it needs is on disk, with full launch-profile
    /// fidelity and no resource started after <c>Build()</c> returns. The pre-0.8.0 default, for an
    /// AppHost that needs every service running by the time <c>Build()</c> returns.
    /// </summary>
    Eager,
}
