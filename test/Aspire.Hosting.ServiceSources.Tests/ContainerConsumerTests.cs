using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers issue #58: a container consumer doing <c>WithReference(service)</c>. DCP needs a Service
/// object to plumb container-to-host networking, and it only makes one for a resource that is
/// actually in the app model — which the old facade never was.
/// </summary>
public class ContainerConsumerTests
{
    private static string AppHostDirectory(string source)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                repository: https://github.com/company/inventory
                project: Inventory.csproj
                url:
                  url: https://httpbin.org
                container:
                  image: nginxdemos/hello
                  port: 8080
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $$"""{ "services": { "inventory": { "source": "{{source}}" } } }""");
        return dir;
    }

    [Fact]
    public void ContainerSourcedService_IsRegistered_SoAContainerConsumerCanReferenceIt()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("container"));

        var inventory = builder.AddService("inventory");
        builder.AddContainer("storefront", "nginx:alpine").WithReference(inventory);

        // The registration is the fix: without it DCP throws "Host endpoint 'http' on resource
        // 'inventory' should have an associated DCP Service resource already set up".
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, inventory.Resource));
        Assert.IsAssignableFrom<ContainerResource>(inventory.Resource);
    }

    [Fact]
    public async Task UrlSourcedService_ReferencedByAContainer_FailsWithAServiceSourcesError()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        builder.AddContainer("storefront", "nginx:alpine").WithReference(inventory);

        // Can't be fixed for 'url' (ExternalServiceResource is sealed), so the pre-flight replaces
        // the DCP stack trace with something that names the cause.
        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("storefront", ex.Message);
        Assert.Contains("inventory", ex.Message);
        Assert.Contains("'url'", ex.Message);
    }

    [Fact]
    public async Task UrlSourcedService_ConsumedByAContainerViaWithEnvironment_FailsWithAServiceSourcesError()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        // The same consumption, expressed the other way Aspire allows. It leaves a different
        // annotation behind than WithReference does, and DCP fails it identically — so the
        // pre-flight has to recognise both or this route still surfaces the raw DCP trace.
        builder.AddContainer("storefront", "nginx:alpine")
            .WithEnvironment("INVENTORY_URL", inventory.GetEndpoint("https"));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("storefront", ex.Message);
        Assert.Contains("inventory", ex.Message);
        Assert.Contains("'url'", ex.Message);
    }

    [Fact]
    public async Task UrlSourcedService_ReferencedByAHostProcess_IsLeftAlone()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        // An executable stands in for every host-process consumer: the pre-flight keys off
        // ContainerResource, so a project takes this same path.
        builder.AddExecutable("worker", "dotnet", Directory.CreateTempSubdirectory().FullName)
            .WithReference(inventory);

        // Host-process consumers work today and must keep working — the pre-flight is narrow.
        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
    }

    [Fact]
    public async Task UrlSourcedService_MerelyParentedToAContainer_IsLeftAlone()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        builder.AddContainer("storefront", "nginx:alpine").WithParentRelationship(inventory);

        // A parent relationship is dashboard grouping, not network wiring: it reaches DCP with no
        // endpoint to plumb, so widening the pre-flight to relationships must not catch it.
        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
    }
}
