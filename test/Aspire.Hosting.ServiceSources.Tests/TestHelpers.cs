using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests;

internal static class TestHelpers
{
    public static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    /// <summary>
    /// A builder that can actually publish <c>BeforeStartEvent</c>. Aspire's own
    /// <c>InitializeDcpAnnotations</c> handler runs first and validates DCP options, which a plain
    /// test builder doesn't have — harmless in a real AppHost, fatal here. The paths only have to
    /// exist as strings; nothing launches them.
    /// </summary>
    public static IDistributedApplicationBuilder CreateBuilderThatCanStart(string appHostDirectory)
    {
        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DcpPublisher:CliPath"] = "/usr/bin/true",
            ["DcpPublisher:DashboardPath"] = "/usr/bin/true",
        });
        return builder;
    }

    public static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));
}
