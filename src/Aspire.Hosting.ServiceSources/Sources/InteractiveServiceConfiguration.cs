using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Whether an unconfigured service resolves to a placeholder, decided during composition, when
/// <c>IInteractionService</c> does not exist yet.
/// </summary>
internal static class InteractiveServiceConfiguration
{
    internal const string OverrideKey = "SERVICESOURCES_INTERACTIVE";

    private const string CiKey = "CI";

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, object> OptedIn = new();

    public static void OptIn(IDistributedApplicationBuilder builder) => OptedIn.TryAdd(builder, new object());

    public static bool IsEnabled(IDistributedApplicationBuilder builder)
    {
        // A manifest cannot describe the placeholder, so publish keeps failing fast on a missing source.
        if (!builder.ExecutionContext.IsRunMode)
        {
            return false;
        }

        var overrideValue = builder.Configuration[OverrideKey];

        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return ParseOverride(overrideValue);
        }

        return OptedIn.TryGetValue(builder, out _) && string.IsNullOrWhiteSpace(builder.Configuration[CiKey]);
    }

    private static bool ParseOverride(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw ServiceSourcesConfigurationException.For(
            $"{Raw.Literal(OverrideKey)} is '{new Name(value)}', which is neither a true nor a false value. Use 1 or true to " +
            $"ask about unconfigured services, 0 or false to fail on them."),
    };
}
