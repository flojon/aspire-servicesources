using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
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

    public static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));
}
