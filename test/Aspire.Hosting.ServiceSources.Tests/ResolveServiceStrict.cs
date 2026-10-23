using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>The resolution <c>AddService</c> does without the opt-in: a missing source throws.</summary>
internal static class ResolveServiceStrict
{
    public static (ServiceDefinition Definition, ServiceDeveloperConfig DeveloperConfig) For(
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var (definition, developerConfig, notConfigured) =
            ServiceSourcesConfigCache.ResolveServiceOrNotConfigured(builder, serviceName);

        return notConfigured is null ? (definition, developerConfig!) : throw notConfigured;
    }
}
