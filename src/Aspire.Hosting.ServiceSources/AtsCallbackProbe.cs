using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// THROWAWAY SPIKE — probes which callback parameter shapes survive ATS codegen.
/// Not part of the package's API. Delete before merging.
/// </summary>
public static class AtsCallbackProbe
{
    /// <summary>Probe 1: the exact AddBackingService shape — a zero-arg Func returning a handle.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="name">The backing service name.</param>
    /// <param name="local">Factory producing the local resource.</param>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<IResourceWithConnectionString> ProbeFuncReturnsHandle(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        Func<IResourceBuilder<IResourceWithConnectionString>> local)
    {
        ArgumentNullException.ThrowIfNull(local);
        return local();
    }

    /// <summary>Probe 2: a callback that receives a handle and returns nothing.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="name">The name.</param>
    /// <param name="configure">Callback receiving the handle.</param>
    [AspireExport]
    public static IResourceBuilder<IResourceWithConnectionString> ProbeActionTakesHandle(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        Action<IResourceBuilder<IResourceWithConnectionString>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var cs = builder.AddConnectionString(name, ReferenceExpression.Create($"probe"));
        configure(cs);
        return cs;
    }

    /// <summary>Probe 3: the simplest possible callback — no args, returns a string.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="name">The name.</param>
    /// <param name="value">Factory producing a string.</param>
    [AspireExport]
    public static IResourceBuilder<IResourceWithConnectionString> ProbeFuncReturnsString(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        Func<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return builder.AddConnectionString(name, ReferenceExpression.Create($"{value()}"));
    }
}
