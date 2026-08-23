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
    /// test builder doesn't have — harmless in a real AppHost, fatal here.
    /// </summary>
    /// <remarks>
    /// The validation only requires these to be non-empty; it never resolves them and nothing is
    /// launched, so a deliberately non-path sentinel keeps the tests OS-agnostic and stops the value
    /// from reading like a real Linux-only dependency.
    /// </remarks>
    public static IDistributedApplicationBuilder CreateBuilderThatCanStart(string appHostDirectory)
    {
        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DcpPublisher:CliPath"] = "unused-dcp-path",
            ["DcpPublisher:DashboardPath"] = "unused-dcp-path",
        });
        return builder;
    }

    public static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));
}
