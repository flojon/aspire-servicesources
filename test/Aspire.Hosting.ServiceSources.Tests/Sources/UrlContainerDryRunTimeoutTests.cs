using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
[Trait("Timing", "true")]
public class UrlContainerDryRunTimeoutTests
{
    // The timeout also bounds scheduling a trivial callback onto the pool, which stalls past a short
    // limit while the suite runs in parallel; only the hung callbacks should ever reach it.
    private static readonly TimeSpan DryRunTimeout = TimeSpan.FromSeconds(2);

    private static (IDistributedApplicationBuilder Builder, IResourceBuilder<ServiceResource> Inventory) Arrange()
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
        return (builder, builder.AddService("inventory"));
    }

    private static EndpointReferenceExpression TlsEnabled(IResourceBuilder<ServiceResource> inventory) =>
        inventory.GetEndpoint("https").Property(EndpointProperty.TlsEnabled);

    [Fact]
    public async Task ACallbackThatNeverFinishesDuringTheDryRun_IsGivenUpOnAndNothingIsRefused()
    {
        var (builder, inventory) = Arrange();
        var storefront = builder.AddContainer("storefront", "nginx:alpine")
            .WithReference(inventory)
            .WithEnvironment("T", TlsEnabled(inventory))
            .WithEnvironment(async _ => await Task.Delay(Timeout.Infinite));

        // Whatever the hung callback would have added cannot be read, so nothing is judged from it;
        // the pre-flight must return rather than hold up startup.
        await ApplyAsync(builder, storefront.Resource, [inventory.Resource]);
    }

    [Fact]
    public async Task ACallbackThatBlocksBeforeItsFirstAwait_IsStillGivenUpOn()
    {
        var (builder, inventory) = Arrange();
        using var release = new ManualResetEventSlim();
        var storefront = builder.AddContainer("storefront", "nginx:alpine")
            .WithReference(inventory)
            .WithEnvironment(_ =>
            {
                release.Wait();
                return Task.CompletedTask;
            });

        try
        {
            // Run off this thread: before the fix the blocking call happened inside ApplyAsync itself.
            await Task.Run(() => ApplyAsync(builder, storefront.Resource, [inventory.Resource]))
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AnEnvironmentTimeout_DoesNotCancelOrSkipTheArgsCallbacks()
    {
        var (builder, inventory) = Arrange();
        var storefront = builder.AddContainer("storefront", "nginx:alpine")
            .WithReference(inventory)
            .WithEnvironment(async _ => await Task.Delay(Timeout.Infinite))
            .WithArgs(context =>
            {
                if (!context.CancellationToken.IsCancellationRequested)
                {
                    context.Args.Add(TlsEnabled(inventory));
                }
            });

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => ApplyAsync(builder, storefront.Resource, [inventory.Resource]));

        Assert.Contains("argument 0", error.Message);
    }

    [Fact]
    public async Task AnArgsCallback_CanReadTheServicesOfItsExecutionContext()
    {
        var (builder, inventory) = Arrange();
        var storefront = builder.AddContainer("storefront", "nginx:alpine")
            .WithReference(inventory)
            .WithArgs(context =>
            {
                _ = context.ExecutionContext.Services;
                context.Args.Add(TlsEnabled(inventory));
            });

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => ApplyAsync(builder, storefront.Resource, [inventory.Resource]));

        Assert.Contains("argument 0", error.Message);
    }

    private static Task ApplyAsync(
        IDistributedApplicationBuilder builder, ContainerResource consumer, ServiceResource[] facades) =>
        Aspire.Hosting.ServiceSources.Sources.UrlContainerReferenceRewrite.ApplyAsync(
            consumer, facades, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            builder.Services.BuildServiceProvider(), DryRunTimeout);
}
