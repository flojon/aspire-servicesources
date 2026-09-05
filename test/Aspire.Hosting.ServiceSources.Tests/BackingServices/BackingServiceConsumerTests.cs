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
    /// The same again for <c>"kubernetes"</c>, where the value the service receives is not written
    /// anywhere: the port in it is the local end of a tunnel this AppHost opened.
    /// </summary>
    /// <remarks>
    /// The port is read back off the tunnel's own command line rather than fixed, because it is
    /// allocated at startup. What is asserted is that the service and the tunnel agree on it —
    /// which a fixed number could not check, and which is the whole of what could go wrong here.
    /// </remarks>
    [Fact]
    public async Task SwitchingTheBackingServiceToKubernetes_KeepsTheKeyAndTunnelsTheValue()
    {
        var builder = CreateBuilder("""
            {
              "services": { "orders": { "source": "local" } },
              "backingServices": { "orders-db": {
                "source": "kubernetes",
                "kubernetes": {
                  "service": "orders-pg",
                  "port": 5432,
                  "context": "dev-west",
                  "connectionString": "Host=localhost;Port=${port};Database=orders" } } }
            }
            """);

        // Identical to the AppHost code in the two tests above, down to the factory.
        var db = builder.AddBackingService(
            "orders-db",
            () => builder.AddConnectionString("orders-db", ReferenceExpression.Create($"Host=localhost;Database=orders")));

        var orders = builder.AddService("orders");
        var beforeTheReference = EnvironmentCallbackCount(orders.Resource);

        orders.Configure<IResourceWithEnvironment>(service => service.WithReference(db));

        var environment = await MaterializeEnvironmentAsync(orders.Resource, beforeTheReference);
        var tunnel = builder.Resources.OfType<ExecutableResource>().Single(r => r.Name == "orders-db-tunnel");
        var localPort = await LocalPortOfAsync(tunnel);

        Assert.Equal(
            $"Host=localhost;Port={localPort};Database=orders", environment["ConnectionStrings__orders-db"]);
    }

    /// <summary>The local port a port-forward executable forwards, read off its own arguments.</summary>
    private static async Task<int> LocalPortOfAsync(ExecutableResource tunnel)
    {
        var context = new CommandLineArgsCallbackContext([]);

        foreach (var annotation in tunnel.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        var pair = context.Args.Select(arg => arg.ToString()!)
            .Single(arg => arg.Contains(':', StringComparison.Ordinal));

        return int.Parse(pair.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture);
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
    /// A resource whose name differs only by case is refused too, because the environment variable
    /// differs by case and not every consumer folds it.
    /// </summary>
    /// <remarks>
    /// The comparison was written as <c>OrdinalIgnoreCase</c> on the grounds that a configuration
    /// key folds case, which is true of .NET's <c>IConfiguration</c> and of nothing else here. This
    /// package runs JavaScript and Java services as well, and <c>process.env</c> and
    /// <c>System.getenv</c> are both case-sensitive — so a factory named <c>Orders-DB</c> behind
    /// <c>orders-db</c> hands a Node app <c>ConnectionStrings__Orders-DB</c> under <c>"local"</c>
    /// and <c>ConnectionStrings__orders-db</c> under <c>"direct"</c>, which is exactly the silent
    /// key move #200 exists to prevent, narrowed to casing.
    /// <para>
    /// Both names are literals in the AppHost's own code, so requiring them to agree exactly costs
    /// the author nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocalFactoryNamingItsResourceInAnotherCasing_IsRefused()
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
                () => builder.AddConnectionString("Orders-DB", ReferenceExpression.Create($"Host=localhost"))));

        Assert.Contains("'Orders-DB'", ex.Message);
    }

    /// <summary>
    /// A factory whose resource cannot be renamed satisfies the rule by returning a connection string
    /// that forwards it under the right name.
    /// </summary>
    /// <remarks>
    /// The remedy the README and the error message both name, so it has to work. It is what replaces
    /// <c>WithReference(db, connectionName)</c> for the case that overload used to cover — a shared
    /// helper, or a resource handed to the caller — which the rule makes unreachable, since the throw
    /// happens before a consumer gets to say anything.
    /// </remarks>
    [Fact]
    public async Task LocalFactoryForwardingAResourceItCannotRename_IsAcceptedAndKeepsTheValue()
    {
        var builder = CreateBuilder("""
            {
              "services": { "orders": { "source": "local" } },
              "backingServices": { "orders-db": { "source": "local" } }
            }
            """);

        var db = builder.AddBackingService("orders-db", () =>
        {
            // Stands in for whatever a shared helper would have named its resource.
            var shared = builder.AddConnectionString(
                "some-helpers-own-name", ReferenceExpression.Create($"Host=localhost;Database=orders"));

            return builder.AddConnectionString("orders-db", ReferenceExpression.Create($"{shared}"));
        });

        var orders = builder.AddService("orders");
        var beforeTheReference = EnvironmentCallbackCount(orders.Resource);

        orders.Configure<IResourceWithEnvironment>(service => service.WithReference(db));

        var environment = await MaterializeEnvironmentAsync(orders.Resource, beforeTheReference);

        Assert.Equal("Host=localhost;Database=orders", environment["ConnectionStrings__orders-db"]);

        // The substitution the README and the error message both warn about: what a consumer ends
        // up holding is the forwarding resource, not the resource the factory built, so a WaitFor
        // on it is a wait on the forwarder.
        //
        // Asserted as identity against the inner resource, which is the only form that can fail. A
        // predicate combining reference equality with the inner resource's name cannot be satisfied
        // by anything — the wrapper is the sole reference match and carries the outer name — so it
        // would pass whatever this returned.
        var inner = Assert.Single(builder.Resources, resource => resource.Name == "some-helpers-own-name");

        Assert.NotSame(inner, db.Resource);
    }
}
