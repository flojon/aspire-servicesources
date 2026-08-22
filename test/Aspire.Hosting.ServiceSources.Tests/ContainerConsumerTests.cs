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
    public async Task UrlSourcedService_ReferencedByAProject_IsLeftAlone()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        builder.AddExecutable("worker", "dotnet", Directory.CreateTempSubdirectory().FullName)
            .WithReference(inventory);

        // Host-process consumers work today and must keep working — the pre-flight is narrow.
        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
    }
}
