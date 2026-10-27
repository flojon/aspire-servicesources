using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
[Trait("Timing", "true")]
public class UrlContainerDryRunTimeoutTests
{
    [Fact]
    public async Task ACallbackThatNeverFinishesDuringTheDryRun_IsGivenUpOnAndNothingIsRefused()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://httpbin.org
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        var inventory = builder.AddService("inventory");
        var storefront = builder.AddContainer("storefront", "nginx:alpine")
            .WithReference(inventory)
            .WithEnvironment("T", inventory.GetEndpoint("https").Property(EndpointProperty.TlsEnabled))
            .WithEnvironment(async _ => await Task.Delay(Timeout.Infinite));

        // Whatever the hung callback would have added cannot be read, so nothing is judged from it;
        // the pre-flight must return rather than hold up startup.
        await ApplyAsync(builder, storefront.Resource, [inventory.Resource]);
    }

    private static Task ApplyAsync(
        IDistributedApplicationBuilder builder, ContainerResource consumer, ServiceResource[] facades) =>
        Aspire.Hosting.ServiceSources.Sources.UrlContainerReferenceRewrite.ApplyAsync(
            consumer, facades, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            builder.Services.BuildServiceProvider(), TimeSpan.FromMilliseconds(200));
}
