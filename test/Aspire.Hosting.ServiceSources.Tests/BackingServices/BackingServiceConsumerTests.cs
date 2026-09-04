using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// What a service actually receives when it is configured with a reference to a backing service.
/// </summary>
public class BackingServiceConsumerTests
{
    private static string FixtureRepoPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-service.git");

    private static IDistributedApplicationBuilder CreateBuilder(string localJson)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"""
            services:
              orders:
                repository: {FixtureRepoPath}
                project: SampleProj/SampleProj.csproj
                defaultRef: main
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), localJson);

        return TestHelpers.CreateBuilder(dir);
    }

    private static int EnvironmentCallbackCount(IResource resource) =>
        resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

    /// <summary>
    /// The environment the callbacks added past <paramref name="alreadyPresent"/> contribute, with
    /// every value provider resolved.
    /// </summary>
    /// <remarks>
    /// Only the new callbacks, which for these tests are the ones <c>WithReference</c> added. The
    /// rest belong to Aspire's own <c>WithProjectDefaults</c>, which a plain test builder cannot
    /// run to completion — the same reason <c>DeferredCheckoutTests</c> reaches for one annotation
    /// rather than all of them. Counting before and after the call under test is what keeps that
    /// precise as annotations are added or reordered elsewhere.
    /// </remarks>
    private static async Task<Dictionary<string, string>> MaterializeEnvironmentAsync(
        IResource resource, int alreadyPresent)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource);

        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Skip(alreadyPresent))
        {
            await callback.Callback(context);
        }

        var materialized = new Dictionary<string, string>();

        foreach (var (key, value) in context.EnvironmentVariables)
        {
            materialized[key] = value switch
            {
                string text => text,
                IValueProvider provider => await provider.GetValueAsync(default) ?? "",
                _ => value.ToString() ?? "",
            };
        }

        return materialized;
    }

    /// <summary>
    /// The acceptance case: a <c>"local"</c>-sourced service configured with a reference to a
    /// <c>"local"</c>-sourced backing service is started with that backing service's connection
    /// string.
    /// </summary>
    [Fact]
    public async Task LocalService_ReferencingALocalBackingService_GetsItsConnectionString()
    {
        var builder = CreateBuilder("""
            {
              "services": { "orders": { "source": "local" } },
              "backingServices": { "orders-db": { "source": "local" } }
            }
            """);

        var db = builder.AddBackingService(
            "orders-db",
            () => builder.AddConnectionString("orders-db", ReferenceExpression.Create($"Host=localhost;Database=orders")));

        var orders = builder.AddService("orders");
        var beforeTheReference = EnvironmentCallbackCount(orders.Resource);

        orders.Configure<IResourceWithEnvironment>(service => service.WithReference(db));

        var environment = await MaterializeEnvironmentAsync(orders.Resource, beforeTheReference);

        Assert.Equal("Host=localhost;Database=orders", environment["ConnectionStrings__orders-db"]);
    }

    /// <summary>
    /// The property the package exists for: the same AppHost code, with only
    /// <c>servicesources.local.json</c> changed, points the service at a database the developer
    /// already runs — under the same environment variable, so the service's own configuration does
    /// not change either.
    /// </summary>
    [Fact]
    public async Task SwitchingTheBackingServiceToDirect_ChangesOnlyTheValue()
    {
        var builder = CreateBuilder("""
            {
              "services": { "orders": { "source": "local" } },
              "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=shared-dev;Port=5432;Database=orders" } } }
            }
            """);

        // Identical to the AppHost code in the test above, down to the factory.
        var db = builder.AddBackingService(
            "orders-db",
            () => builder.AddConnectionString("orders-db", ReferenceExpression.Create($"Host=localhost;Database=orders")));

        var orders = builder.AddService("orders");
        var beforeTheReference = EnvironmentCallbackCount(orders.Resource);

        orders.Configure<IResourceWithEnvironment>(service => service.WithReference(db));

        var environment = await MaterializeEnvironmentAsync(orders.Resource, beforeTheReference);

        Assert.Equal("Host=shared-dev;Port=5432;Database=orders", environment["ConnectionStrings__orders-db"]);
    }

    /// <summary>
    /// A factory naming its resource something other than the backing service is refused, because
    /// that is what would move the key the app reads.
    /// </summary>
    /// <remarks>
    /// Aspire's own <c>WithReference</c> keys the variable on the referenced resource's name, and
    /// under <c>"local"</c> that resource is whatever the AppHost's factory built — so a factory
    /// returning <c>something-else</c> gives the app <c>ConnectionStrings__something-else</c> while
    /// every other source gives it <c>ConnectionStrings__orders-db</c>. Switching source would then
    /// move the key, and the failure is silent: the AppHost is happy, the variable is set, and the
    /// app starts and finds nothing where it looked.
    /// <para>
    /// This was documented and pinned as behaviour until #200. It is a rule now because the remedy
    /// has to be one every AppHost can reach: C# could settle it at the consumer with
    /// <c>WithReference(db, "orders-db")</c>, but the generated shim takes no such argument (#209),
    /// so renaming the factory's resource is a guest language's only route.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocalFactoryNamingItsResourceDifferently_IsRefused()
    {
        var builder = CreateBuilder("""
            {
              "services": { "orders": { "source": "local" } },
              "backingServices": { "orders-db": { "source": "local" } }
            }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(
                "orders-db",
                () => builder.AddConnectionString("something-else", ReferenceExpression.Create($"Host=localhost"))));

        Assert.Contains("Backing service 'orders-db'", ex.Message);
        Assert.Contains("'something-else'", ex.Message);
    }

    /// <summary>
    /// A resource whose name differs only by case is accepted, because configuration keys fold case
    /// and it is therefore the same key.
    /// </summary>
    [Fact]
    public async Task LocalFactoryNamingItsResourceInAnotherCasing_IsAccepted()
    {
        var builder = CreateBuilder("""
            {
              "services": { "orders": { "source": "local" } },
              "backingServices": { "orders-db": { "source": "local" } }
            }
            """);

        var db = builder.AddBackingService(
            "orders-db",
            () => builder.AddConnectionString("Orders-DB", ReferenceExpression.Create($"Host=localhost")));

        var orders = builder.AddService("orders");
        var beforeTheReference = EnvironmentCallbackCount(orders.Resource);

        orders.Configure<IResourceWithEnvironment>(service => service.WithReference(db));

        var environment = await MaterializeEnvironmentAsync(orders.Resource, beforeTheReference);

        Assert.Contains("ConnectionStrings__Orders-DB", environment.Keys);
    }
}
