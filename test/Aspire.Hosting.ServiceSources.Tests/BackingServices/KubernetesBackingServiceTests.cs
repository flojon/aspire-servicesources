using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// The <c>"kubernetes"</c> backing-service source: a <c>kubectl port-forward</c> this AppHost runs,
/// a connection string addressing its local end, and the health check that makes a consumer's
/// <c>WaitFor</c> wait for the tunnel rather than for the string.
/// </summary>
/// <remarks>
/// Nothing here runs <c>kubectl</c>. What is asserted is the model the AppHost builds — the
/// executable's command line, the connection string's text, the annotations — which is the whole of
/// what this source decides; everything after that is Aspire's to run.
/// </remarks>
public class KubernetesBackingServiceTests
{
    private const string Name = "orders-db";

    private static IDistributedApplicationBuilder CreateBuilder(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);

        return TestHelpers.CreateBuilder(dir);
    }

    /// <summary>
    /// An entry with every field set, so that a test about one of them changes only that one.
    /// </summary>
    private static string Entry(
        string? service = "orders-pg",
        string? port = "5432",
        string? context = "dev-west",
        string? @namespace = null,
        string connectionString = "Host=localhost;Port=${port};Database=orders")
    {
        var fields = new List<string> { $"\"source\": \"kubernetes\"" };
        var kubernetes = new List<string>();

        if (service is not null)
        {
            kubernetes.Add($"\"service\": \"{service}\"");
        }

        if (port is not null)
        {
            kubernetes.Add($"\"port\": {port}");
        }

        if (context is not null)
        {
            kubernetes.Add($"\"context\": \"{context}\"");
        }

        if (@namespace is not null)
        {
            kubernetes.Add($"\"namespace\": \"{@namespace}\"");
        }

        kubernetes.Add($"\"connectionString\": {System.Text.Json.JsonSerializer.Serialize(connectionString)}");
        fields.Add($"\"kubernetes\": {{ {string.Join(", ", kubernetes)} }}");

        return $$"""{ "backingServices": { "{{Name}}": { {{string.Join(", ", fields)}} } } }""";
    }

    /// <summary>
    /// A factory naming a resource the backing service is not called, so that asserting its absence
    /// says something: this source never invokes it, and the name rule #200 added therefore never
    /// applies to it.
    /// </summary>
    private static Func<IResourceBuilder<IResourceWithConnectionString>> UnusedFactory(
        IDistributedApplicationBuilder builder, Action? onInvoke = null) =>
        () =>
        {
            onInvoke?.Invoke();
            return builder.AddConnectionString("not-the-backing-service");
        };

    private static ExecutableResource Tunnel(IDistributedApplicationBuilder builder) =>
        builder.Resources.OfType<ExecutableResource>().Single(resource => resource.Name == $"{Name}-tunnel");

    /// <summary>The command line the tunnel would run, as one array.</summary>
    private static async Task<string[]> TunnelArgsAsync(IDistributedApplicationBuilder builder)
    {
        var tunnel = Tunnel(builder);
        var context = new CommandLineArgsCallbackContext([]);

        foreach (var annotation in tunnel.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return context.Args.Select(arg => arg.ToString()!).ToArray();
    }

    /// <summary>The local port the tunnel forwards, read off its own arguments.</summary>
    /// <remarks>
    /// Read back rather than fixed, because the port is allocated by the OS: what these tests can
    /// assert is that the connection string and the tunnel agree on it, which is the property that
    /// matters and the one a fixed number would not check.
    /// </remarks>
    private static async Task<int> LocalPortAsync(IDistributedApplicationBuilder builder)
    {
        var args = await TunnelArgsAsync(builder);
        var pair = args.Single(arg => arg.Contains(':', StringComparison.Ordinal));

        return int.Parse(pair.Split(':')[0], CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task AllFieldsSet_ForwardsTheConfiguredServiceAndPort()
    {
        var builder = CreateBuilder(Entry(service: "orders-pg", port: "5432", context: "dev-west", @namespace: "orders"));

        builder.AddBackingService(Name, UnusedFactory(builder));

        var localPort = await LocalPortAsync(builder);

        Assert.Equal("kubectl", Tunnel(builder).Command);
        Assert.Equal(
            [
                "port-forward", "svc/orders-pg", $"{localPort}:5432",
                "--context", "dev-west", "--namespace", "orders",
            ],
            await TunnelArgsAsync(builder));
    }

    /// <remarks>
    /// <c>kubectl</c>'s own default is the context's configured namespace, which is whatever the
    /// developer last set in a shell. This package's is <c>default</c>, so that an AppHost behaves
    /// the same however that shell was left.
    /// </remarks>
    [Fact]
    public async Task NamespaceOmitted_ForwardsInTheDefaultNamespace()
    {
        var builder = CreateBuilder(Entry(@namespace: null));

        builder.AddBackingService(Name, UnusedFactory(builder));

        var args = await TunnelArgsAsync(builder);

        Assert.Equal("default", args[Array.IndexOf(args, "--namespace") + 1]);
    }

    /// <summary>
    /// The tunnel is a second resource, named after the backing service and shown beneath it.
    /// </summary>
    /// <remarks>
    /// The connection string keeps the backing service's own name, because that is what a consumer's
    /// <c>WithReference</c> keys the app's <c>ConnectionStrings__…</c> variable on — the rule #200
    /// pinned. A tunnel that took the name would move that key for this source alone.
    /// </remarks>
    [Fact]
    public void TheTunnel_IsNamedAfterTheBackingServiceAndParentedToIt()
    {
        var builder = CreateBuilder(Entry());

        var db = builder.AddBackingService(Name, UnusedFactory(builder));

        Assert.Equal(Name, db.Resource.Name);
        Assert.Equal($"{Name}-tunnel", Tunnel(builder).Name);
        Assert.Same(
            db.Resource,
            Tunnel(builder).Annotations.OfType<ResourceRelationshipAnnotation>()
                .Single(relationship => relationship.Type == "Parent").Resource);
    }

    [Fact]
    public void TheLocalFactory_IsNeverInvoked()
    {
        var builder = CreateBuilder(Entry());
        var invocations = 0;

        builder.AddBackingService(Name, UnusedFactory(builder, () => invocations++));

        Assert.Equal(0, invocations);
        Assert.DoesNotContain("not-the-backing-service", builder.Resources.Select(resource => resource.Name));
    }

    [Fact]
    public async Task PortPlaceholder_ResolvesToTheEndOfTheTunnelTheAppHostOpened()
    {
        var builder = CreateBuilder(Entry(connectionString: "Host=localhost;Port=${port};Database=orders"));

        var db = builder.AddBackingService(Name, UnusedFactory(builder));

        var localPort = await LocalPortAsync(builder);

        Assert.Equal(
            $"Host=localhost;Port={localPort};Database=orders",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <remarks>
    /// One placeholder written twice is two substitutions of the same port, not one — a connection
    /// string that names the host and a failover host is the ordinary case.
    /// </remarks>
    [Fact]
    public async Task PortPlaceholderWrittenTwice_IsSubstitutedBothTimes()
    {
        var builder = CreateBuilder(Entry(connectionString: "Server=localhost,${port};Failover=localhost,${port}"));

        var db = builder.AddBackingService(Name, UnusedFactory(builder));

        var localPort = await LocalPortAsync(builder);

        Assert.Equal(
            $"Server=localhost,{localPort};Failover=localhost,{localPort}",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// Braces in the template survive to the app, rather than being read as a format placeholder of
    /// Aspire's own.
    /// </summary>
    /// <remarks>
    /// <c>Driver={PostgreSQL}</c> is ordinary ODBC, and an unescaped <c>{</c> in a
    /// <c>ReferenceExpression</c> throws a <c>FormatException</c> at app start naming neither the
    /// connection string nor the backing service. The escaping lives in
    /// <c>ConnectionStringTemplate.AppendLiteral</c> so that no source can forget it; this asserts
    /// that this source did not.
    /// </remarks>
    [Fact]
    public async Task BracesInTheTemplate_ReachTheAppAsWritten()
    {
        var builder = CreateBuilder(
            Entry(connectionString: "Driver={PostgreSQL};Server=localhost;Port=${port}"));

        var db = builder.AddBackingService(Name, UnusedFactory(builder));

        var localPort = await LocalPortAsync(builder);

        Assert.Equal(
            $"Driver={{PostgreSQL}};Server=localhost;Port={localPort}",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// The health check is attached to the connection string, which is what a consumer waits for.
    /// </summary>
    /// <remarks>
    /// A regression guard on the measurement that made this source's health check required rather
    /// than optional: without it the connection-string resource reaches <c>Running</c> as soon as
    /// its template resolves, and a consumer's <c>WaitFor</c> lets it start about five seconds
    /// before the tunnel is listening.
    /// </remarks>
    [Fact]
    public void TheConnectionString_CarriesATcpHealthCheckOnTheForwardedPort()
    {
        var builder = CreateBuilder(Entry());

        var db = builder.AddBackingService(Name, UnusedFactory(builder));

        Assert.Equal(
            $"{Name}-tunnel-tcp",
            db.Resource.Annotations.OfType<HealthCheckAnnotation>().Single().Key);
    }

    /// <remarks>
    /// The same check on the tunnel too, so the dashboard reports the process as unhealthy while
    /// <c>kubectl</c> is still opening the socket rather than as running-and-fine.
    /// </remarks>
    [Fact]
    public void TheTunnel_CarriesTheSameHealthCheck()
    {
        var builder = CreateBuilder(Entry());

        builder.AddBackingService(Name, UnusedFactory(builder));

        Assert.Equal(
            $"{Name}-tunnel-tcp",
            Tunnel(builder).Annotations.OfType<HealthCheckAnnotation>().Single().Key);
    }

    /// <summary>
    /// The annotation names a check that is actually registered, rather than a key nothing answers.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the two halves are written apart — an annotation on the resource,
    /// a registration in the service collection — and a mismatch between them is not a compile
    /// error. Aspire resolves the key at start time, so a dangling one would first be seen on a
    /// developer's machine.
    /// </remarks>
    [Fact]
    public void TheHealthCheckKey_IsRegisteredWithTheHealthCheckService()
    {
        var builder = CreateBuilder(Entry());

        builder.AddBackingService(Name, UnusedFactory(builder));

        var registrations = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Contains(registrations, registration => registration.Name == $"{Name}-tunnel-tcp");
    }

    [Theory]
    [InlineData("service", "the Kubernetes Service to forward to")]
    [InlineData("context", "the kubectl context to forward through")]
    [InlineData("connectionString", "the connection string consumers receive")]
    public void AMissingField_IsNamedWithWhatItHolds(string field, string whatItIs)
    {
        var builder = CreateBuilder(field switch
        {
            "service" => Entry(service: null),
            "context" => Entry(context: null),
            _ => WithoutConnectionString(),
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains($"Backing service '{Name}'", ex.Message);
        Assert.Contains($"requires 'kubernetes.{field}'", ex.Message);
        Assert.Contains(whatItIs, ex.Message);
        Assert.Contains($"ServiceSources__BackingServices__{Name}__Kubernetes__{field}", ex.Message);
    }

    /// <remarks>
    /// The port's message says where the <em>local</em> end comes from as well, because that is the
    /// question a developer filling this field in is about to ask: they have two ports in front of
    /// them and only one goes here.
    /// </remarks>
    [Fact]
    public void AMissingPort_SaysWhichEndOfTheTunnelItIs()
    {
        var builder = CreateBuilder(Entry(port: null));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains("requires 'kubernetes.port'", ex.Message);
        Assert.Contains("inside the cluster", ex.Message);
        Assert.Contains("allocated rather than configured", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void APortOutsideTheRange_IsRefused(string port)
    {
        var builder = CreateBuilder(Entry(port: port));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains($"'kubernetes.port' is '{port}'", ex.Message);
        Assert.Contains("between 1 and 65535", ex.Message);
    }

    /// <summary>
    /// A connection string with no <c>${port}</c> in it is refused rather than run.
    /// </summary>
    /// <remarks>
    /// The failure it prevents is silent and can be worse than a failure: a template carrying the
    /// cluster's own port, copied from a manifest, addresses that port on the developer's machine —
    /// where their own database container may well be listening, so the AppHost connects to the
    /// wrong database with every resource reporting healthy.
    /// </remarks>
    [Fact]
    public void AConnectionStringThatNamesNoPort_IsRefusedRatherThanLeavingTheTunnelUndialled()
    {
        var builder = CreateBuilder(Entry(connectionString: "Host=localhost;Port=5432;Database=orders"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains("names no '${port}' placeholder", ex.Message);
        Assert.Contains("Host=localhost;Port=5432;Database=orders", ex.Message);
        Assert.Contains("source 'direct'", ex.Message);
    }

    /// <remarks>
    /// The parser reads a named port already, so this source refuses one by name rather than
    /// reporting it as malformed — the mistake is asking for a feature, not mistyping one.
    /// </remarks>
    [Fact]
    public void ANamedPort_IsRefusedUntilOneTunnelCanCarrySeveral()
    {
        var builder = CreateBuilder(Entry(connectionString: "amqp://localhost:${port:amqp}/"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains("'${port:amqp}'", ex.Message);
        Assert.Contains("not supported yet", ex.Message);
        Assert.Contains("write '${port}'", ex.Message);
    }

    /// <remarks>
    /// Secrets arrive with stage 3. Until then the message says what to do instead, rather than
    /// only that the placeholder is unsupported — the value has to come from somewhere today.
    /// </remarks>
    [Fact]
    public void ASecretPlaceholder_IsRefusedWithSomewhereElseToPutTheValue()
    {
        var builder = CreateBuilder(
            Entry(connectionString: "Host=localhost;Port=${port};Password=${secret:orders-creds:password}"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains("'${secret:orders-creds:password}'", ex.Message);
        Assert.Contains("not supported yet", ex.Message);
        Assert.Contains("user secrets", ex.Message);
    }

    /// <summary>
    /// A malformed placeholder is reported as malformed, ahead of anything this source checks.
    /// </summary>
    /// <remarks>
    /// Parsing runs before the <c>${port}</c> requirement, so a developer who wrote
    /// <c>${port:}</c> is told what is wrong with the token rather than that their connection
    /// string names no port — which would be true, and would send them to fix the wrong thing.
    /// </remarks>
    [Fact]
    public void AMalformedPlaceholder_IsReportedAsMalformed()
    {
        var builder = CreateBuilder(Entry(connectionString: "Host=localhost;Port=${port:}"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Contains("the port name after 'port:' is empty", ex.Message);
        Assert.DoesNotContain("names no '${port}' placeholder", ex.Message);
    }

    /// <summary>
    /// Nothing is added before the entry is known to be usable.
    /// </summary>
    /// <remarks>
    /// A tunnel left behind by a call that then threw would be a resource the AppHost never asked
    /// for, and — since AppHost construction fails anyway — one whose only effect is to make the
    /// model harder to read in whatever reports it.
    /// </remarks>
    [Fact]
    public void AFailedEntry_AddsNothing()
    {
        var builder = CreateBuilder(Entry(context: null));

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(Name, UnusedFactory(builder)));

        Assert.Empty(builder.Resources.OfType<ExecutableResource>());
    }

    private static string WithoutConnectionString() =>
        $$"""
        { "backingServices": { "{{Name}}": { "source": "kubernetes", "kubernetes": {
            "service": "orders-pg", "port": 5432, "context": "dev-west" } } } }
        """;
}
