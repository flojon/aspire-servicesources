using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Whether an unconfigured service resolves to a placeholder, decided during composition, when
/// <c>IInteractionService</c> does not exist yet.
/// </summary>
internal static class InteractiveServiceConfiguration
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, object> OptedIn = new();

    public static void OptIn(IDistributedApplicationBuilder builder) => OptedIn.TryAdd(builder, new object());

    // The same CI and SERVICESOURCES_INTERACTIVE decision as the source-pick prompt, so one setting
    // silences both. The override applies only with the opt-in: without it nothing here changes.
    public static bool IsEnabled(IDistributedApplicationBuilder builder) =>
        // A manifest cannot describe the placeholder, so publish keeps failing fast on a missing source.
        builder.ExecutionContext.IsRunMode
        && OptedIn.TryGetValue(builder, out _)
        && InteractivePrompts.IsEnabled(builder.Configuration);
}
