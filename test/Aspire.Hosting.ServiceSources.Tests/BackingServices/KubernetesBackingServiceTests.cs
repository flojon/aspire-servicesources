using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.BackingServices;
using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// The <c>"kubernetes"</c> backing-service source: a <c>kubectl port-forward</c> this AppHost runs,
/// a connection string addressing its local end, and the health check that makes a consumer's
/// <c>WaitFor</c> wait for the tunnel rather than for the string.
/// </summary>
/// <remarks>
/// Resolved directly, with a fake <see cref="IPortAllocator"/>, exactly as
/// <c>KubernetesSourceTests</c> does for the service-side source: no socket is bound, so the
/// forwarded port is a number these tests can name. That the entry <em>binds</em> from
/// <c>servicesources.local.json</c> and dispatches here is covered in <c>AddBackingServiceTests</c>,
/// where the config layers are the subject.
/// <para>
/// Nothing here runs <c>kubectl</c>. What is asserted is the model the AppHost builds — the
/// executable's command line, the connection string's text, the annotations — which is the whole of
/// what this source decides; everything after that is Aspire's to run.
/// </para>
/// </remarks>
public class KubernetesBackingServiceTests
{
    private const string Name = "orders-db";

    /// <summary>The port the fake allocator hands out, so every expectation can name it.</summary>
    private const int LocalPort = 54321;

    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public int AllocatePort() => port;
    }

    private sealed class TrackingPortAllocator(Action onAllocate, int port) : IPortAllocator
    {
        public int AllocatePort()
        {
            onAllocate();
            return port;
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    /// <summary>
    /// An entry with every field set, so a test about one of them changes only that one.
    /// </summary>
    private static BackingServiceDeveloperConfig Config(
        string? service = "orders-pg",
        int? port = 5432,
        string? context = "dev-west",
        string? @namespace = null,
        string? connectionString = "Host=localhost;Port=${port};Database=orders") =>
        new()
        {
            Source = "kubernetes",
            Kubernetes = new()
            {
                Service = service,
                Port = port,
                Context = context,
                Namespace = @namespace,
                ConnectionString = connectionString,
            },
        };

    private static IResourceBuilder<IResourceWithConnectionString> Resolve(
        IDistributedApplicationBuilder builder,
        BackingServiceDeveloperConfig config,
        IPortAllocator? allocator = null) =>
        new KubernetesBackingServiceSource(allocator ?? new FakePortAllocator(LocalPort))
            .Resolve(builder, Name, config);

    private static ExecutableResource Tunnel(IDistributedApplicationBuilder builder) =>
        builder.Resources.OfType<ExecutableResource>().Single(resource => resource.Name == $"{Name}-tunnel");

    /// <summary>The command line the tunnel would run, as one array.</summary>
    private static async Task<string[]> TunnelArgsAsync(IDistributedApplicationBuilder builder)
    {
        var context = new CommandLineArgsCallbackContext([]);

        foreach (var annotation in Tunnel(builder).Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return context.Args.Select(arg => arg.ToString()!).ToArray();
    }

    [Fact]
    public async Task AllFieldsSet_ForwardsTheConfiguredServiceAndPort()
    {
        var builder = CreateBuilder();

        Resolve(builder, Config(service: "orders-pg", port: 5432, context: "dev-west", @namespace: "orders"));

        Assert.Equal("kubectl", Tunnel(builder).Command);
        Assert.Equal(
            [
                "port-forward", "svc/orders-pg", $"{LocalPort}:5432",
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
        var builder = CreateBuilder();

        Resolve(builder, Config(@namespace: null));

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
        var builder = CreateBuilder();

        var db = Resolve(builder, Config());

        Assert.Equal(Name, db.Resource.Name);
        Assert.Equal($"{Name}-tunnel", Tunnel(builder).Name);
        Assert.Same(
            db.Resource,
            Tunnel(builder).Annotations.OfType<ResourceRelationshipAnnotation>()
                .Single(relationship => relationship.Type == "Parent").Resource);
    }

    [Fact]
    public async Task PortPlaceholder_ResolvesToTheEndOfTheTunnelTheAppHostOpened()
    {
        var builder = CreateBuilder();

        var db = Resolve(builder, Config(connectionString: "Host=localhost;Port=${port};Database=orders"));

        Assert.Equal(
            $"Host=localhost;Port={LocalPort};Database=orders",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <remarks>
    /// One placeholder written twice is two substitutions of the same port, not one — a connection
    /// string that names the host and a failover host is the ordinary case.
    /// </remarks>
    [Fact]
    public async Task PortPlaceholderWrittenTwice_IsSubstitutedBothTimes()
    {
        var builder = CreateBuilder();

        var db = Resolve(builder, Config(connectionString: "Server=localhost,${port};Failover=localhost,${port}"));

        Assert.Equal(
            $"Server=localhost,{LocalPort};Failover=localhost,{LocalPort}",
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
        var builder = CreateBuilder();

        var db = Resolve(builder, Config(connectionString: "Driver={PostgreSQL};Server=localhost;Port=${port}"));

        Assert.Equal(
            $"Driver={{PostgreSQL}};Server=localhost;Port={LocalPort}",
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
        var builder = CreateBuilder();

        var db = Resolve(builder, Config());

        Assert.Equal(
            $"{Name}-tunnel-tcp",
            db.Resource.Annotations.OfType<HealthCheckAnnotation>().Single().Key);
    }

    /// <summary>
    /// The tunnel does <em>not</em> carry the same check, though the socket is its own.
    /// </summary>
    /// <remarks>
    /// Aspire runs one monitor loop per resource, each executing the registrations its resource
    /// names, so a second resource carrying this key would run the probe twice per cycle. Every
    /// probe is a connection <c>kubectl</c> logs and the database behind it may log as an
    /// incomplete startup packet — into the log a developer reads to find out why the tunnel is
    /// down. Nothing waits on the tunnel, so the second annotation would buy a dashboard badge and
    /// pay for it in the diagnostic channel.
    /// </remarks>
    [Fact]
    public void TheTunnel_DoesNotCarryASecondCopyOfTheHealthCheck()
    {
        var builder = CreateBuilder();

        Resolve(builder, Config());

        Assert.Empty(Tunnel(builder).Annotations.OfType<HealthCheckAnnotation>());
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
    public void TheHealthCheckKey_IsRegisteredWithABoundedTimeout()
    {
        var builder = CreateBuilder();

        Resolve(builder, Config());

        var registration = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name == $"{Name}-tunnel-tcp");

        // Bounded, because AddCheck's instance overload leaves it infinite: a connect that hangs
        // rather than refuses would stall that resource's monitor loop for the life of the run.
        Assert.NotEqual(Timeout.InfiniteTimeSpan, registration.Timeout);
    }

    /// <summary>
    /// An entry missing several required fields names all of them, in one run.
    /// </summary>
    /// <remarks>
    /// The property the message exists for. Reporting one field per run costs a failed startup per
    /// key — the trade <c>DeveloperConfigValidator</c> rejects for the same reason — and this block
    /// has four fields, so a developer filling in a fresh one would otherwise pay four startups to
    /// be told what it contains.
    /// </remarks>
    [Fact]
    public void AnEmptyBlock_NamesEveryMissingFieldAtOnce()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(service: null, port: null, context: null, connectionString: null)));

        Assert.Contains("'kubernetes.service'", ex.Message);
        Assert.Contains("'kubernetes.port'", ex.Message);
        Assert.Contains("'kubernetes.context'", ex.Message);
        Assert.Contains("'kubernetes.connectionString'", ex.Message);
    }

    [Theory]
    [InlineData("service", "the Kubernetes Service to forward to")]
    [InlineData("context", "the kubectl context to forward through")]
    [InlineData("connectionString", "the connection string consumers receive")]
    public void AMissingField_IsNamedWithWhatItHolds(string field, string whatItIs)
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Resolve(builder, field switch
        {
            "service" => Config(service: null),
            "context" => Config(context: null),
            _ => Config(connectionString: null),
        }));

        Assert.Contains($"Backing service '{Name}'", ex.Message);
        Assert.Contains($"requires 'kubernetes.{field}'", ex.Message);
        Assert.Contains(whatItIs, ex.Message);
    }

    /// <summary>
    /// The message points at the file by the name the file itself uses.
    /// </summary>
    /// <remarks>
    /// <c>DeveloperConfiguration.BackingServicesKey</c> is the <c>IConfiguration</c> path
    /// (<c>ServiceSources:BackingServices</c>) and belongs only in the environment-variable half of
    /// the sentence. A developer sent to <c>servicesources.local.json</c> to add a key under
    /// "ServiceSources:BackingServices" would find no such section — the file spells it
    /// <c>backingServices</c>.
    /// </remarks>
    [Fact]
    public void AMissingField_NamesTheFilesOwnSectionAndTheEnvironmentVariable()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(service: null)));

        Assert.Contains("\"backingServices\" in 'servicesources.local.json'", ex.Message);
        Assert.DoesNotContain("\"ServiceSources:BackingServices\"", ex.Message);
        Assert.Contains($"ServiceSources__BackingServices__{Name}__Kubernetes__Service", ex.Message);
    }

    /// <remarks>
    /// The port's message says where the <em>local</em> end comes from as well, because that is the
    /// question a developer filling this field in is about to ask: they have two ports in front of
    /// them and only one goes here.
    /// </remarks>
    [Fact]
    public void AMissingPort_SaysWhichEndOfTheTunnelItIs()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(port: null)));

        Assert.Contains("requires 'kubernetes.port'", ex.Message);
        Assert.Contains("inside the cluster", ex.Message);
        Assert.Contains("allocated rather than configured", ex.Message);
    }

    /// <remarks>
    /// A port that is present but not a port is a different mistake from a missing one, and is not
    /// folded into the list of what the block lacks — the developer filled this field in.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void APortOutsideTheRange_IsRefusedAsAValueRatherThanAsAnAbsence(int port)
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Resolve(builder, Config(port: port)));

        Assert.Contains($"'kubernetes.port' is '{port}'", ex.Message);
        Assert.Contains("between 1 and 65535", ex.Message);
        Assert.DoesNotContain("requires", ex.Message);
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
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "Host=localhost;Port=5432;Database=orders")));

        Assert.Contains("names no '${port}' placeholder", ex.Message);
        Assert.Contains("Host=localhost;Port=5432;Database=orders", ex.Message);
        Assert.Contains("source 'direct'", ex.Message);
    }

    /// <summary>
    /// That same message names the shell, because a mangled template arrives looking identical.
    /// </summary>
    /// <remarks>
    /// <c>${…}</c> is a shell variable too, so a template set through an environment variable can
    /// reach the AppHost with its placeholder already expanded away — and what arrives is exactly
    /// what someone who wrote a literal port produces. The first half of the message tells that
    /// reader to write the spelling they already wrote, so the second half has to name the shell.
    /// </remarks>
    [Fact]
    public void AConnectionStringThatNamesNoPort_AlsoNamesTheShellThatMayHaveEatenIt()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "Host=localhost;Port=;Database=orders")));

        Assert.Contains("a shell expanded it away", ex.Message);
        Assert.Contains("Single-quote the value", ex.Message);
    }

    /// <remarks>
    /// The parser reads a named port already, so this source refuses one by name rather than
    /// reporting it as malformed — the mistake is asking for a feature, not mistyping one.
    /// </remarks>
    [Fact]
    public void ANamedPort_IsRefusedUntilOneTunnelCanCarrySeveral()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "amqp://localhost:${port:amqp}/")));

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
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Resolve(
            builder,
            Config(connectionString: "Host=localhost;Port=${port};Password=${secret:orders-creds:password}")));

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
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "Host=localhost;Port=${port:}")));

        Assert.Contains("the port name after 'port:' is empty", ex.Message);
        Assert.DoesNotContain("names no '${port}' placeholder", ex.Message);
    }

    /// <summary>
    /// Nothing is allocated and nothing is added before the entry is known to be usable.
    /// </summary>
    /// <remarks>
    /// The property the second commit exists for. A template this source cannot resolve is config
    /// validation like the field checks above it, so it is judged in a pass of its own before a
    /// port is taken — and a tunnel left behind by a call that then threw would be a resource the
    /// AppHost never asked for.
    /// </remarks>
    [Theory]
    [InlineData("a missing field")]
    [InlineData("a placeholder this source cannot resolve")]
    [InlineData("a template that addresses no tunnel")]
    public void AFailedEntry_AllocatesNoPortAndAddsNothing(string because)
    {
        var builder = CreateBuilder();
        var allocations = 0;

        var config = because switch
        {
            "a missing field" => Config(context: null),
            "a placeholder this source cannot resolve" =>
                Config(connectionString: "Host=localhost;Port=${port};Password=${secret:creds:password}"),
            _ => Config(connectionString: "Host=localhost;Port=5432;Database=orders"),
        };

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, config, new TrackingPortAllocator(() => allocations++, LocalPort)));

        Assert.Equal(0, allocations);
        Assert.Empty(builder.Resources);
    }

    /// <summary>
    /// A backing-service name Aspire accepts, whose derived tunnel name it does not, is reported
    /// against the backing service.
    /// </summary>
    /// <remarks>
    /// Aspire caps a resource name's length, and the tunnel's name is seven characters longer than
    /// the one the AppHost wrote — so there is a band of names where the backing service is legal
    /// and its tunnel is not, and Aspire's own complaint would name a resource nobody wrote. The
    /// limit itself stays Aspire's to define: this asserts only that the failure says where the
    /// rejected name came from.
    /// </remarks>
    [Fact]
    public void ABackingServiceNameTooLongOnceSuffixed_IsReportedAgainstTheBackingService()
    {
        var builder = CreateBuilder();
        var longName = new string('a', 64);

        var ex = Record.Exception(
            () => new KubernetesBackingServiceSource(new FakePortAllocator(LocalPort))
                .Resolve(builder, longName, Config()));

        Assert.NotNull(ex);
        Assert.IsType<ServiceSourcesConfigurationException>(ex);
        Assert.Contains($"Backing service '{longName}'", ex.Message);
        Assert.Contains($"{longName}-tunnel", ex.Message);

        // Aspire's own rule, in its own words, without the parameter of a call the developer never
        // made — which would otherwise land immediately before the sentence saying the name was
        // derived rather than written.
        Assert.DoesNotContain("(Parameter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The echoed connection string reaches the message with its credentials replaced.
    /// </summary>
    /// <remarks>
    /// This is the only message in the package that echoes a whole, valid connection string — every
    /// other echo is a malformed value or a single token — and an AppHost's startup failure is
    /// relayed into <c>~/.aspire/logs</c> and routinely pasted into an issue. The echo earns its
    /// place, since the shell-expansion case is only diagnosable by seeing what arrived, so the
    /// value is redacted rather than withheld.
    /// <para>
    /// One row per syntax, because what is covered here is that the redaction is applied at all and
    /// that its result is what the message quotes. The dialects it has to survive are a matrix of
    /// connection strings rather than of AppHost configurations, and live in
    /// <see cref="ConnectionStringRedactionTests"/>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Host=db.internal;Port=5432;Username=dev;Password=hunter2", "hunter2", "Username=dev")]
    [InlineData("postgresql://orders_app:hunter2@db.internal:5432/orders", "hunter2", "db.internal:5432/orders")]
    // The case an allowlist exists for: a key no blocklist would have thought to name.
    [InlineData("Host=db.internal;Rotation Key=hunter2", "hunter2", "Rotation Key=")]
    public void TheEchoedConnectionString_HasItsCredentialsRedacted(
        string connectionString, string secret, string survives)
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: connectionString)));

        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.Contains("***", ex.Message);

        // Something unique to this input, so the assertion distinguishes the echoed value from the
        // worked example the message hard-codes — "localhost" would pass with the echo suppressed.
        Assert.Contains(survives, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The note explaining the masking does not call what it hid a credential.
    /// </summary>
    /// <remarks>
    /// Under an allowlist a <c>***</c> means "not recognised", which is not the same as "secret" —
    /// the <c>Rotation Key</c> here holds a timeout. A note asserting a credential was found would
    /// tell the developer something the package does not know.
    /// </remarks>
    [Fact]
    public void TheNoteAboutMaskedValues_DoesNotClaimACredentialWasFound()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "Host=db.internal;Rotation Key=30")));

        Assert.Contains("the rest read as ***, which does not mean they were secret", ex.Message);
        Assert.DoesNotContain("credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A connection string with nothing to hide is echoed whole.
    /// </summary>
    /// <remarks>
    /// The redaction narrows what the echo can leak; it must not narrow what the echo is
    /// <em>for</em>. Showing the developer what arrived is how the shell-expansion case is
    /// diagnosed, and most templates carry no credential at all.
    /// </remarks>
    [Fact]
    public void AConnectionStringWithNoCredential_IsEchoedUntouched()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "Host=db.internal;Port=5432;Database=orders")));

        Assert.Contains("\"Host=db.internal;Port=5432;Database=orders\"", ex.Message);
        Assert.DoesNotContain("***", ex.Message);
    }

    /// <summary>
    /// An address that merely contains an <c>@</c> is not swept into the redaction.
    /// </summary>
    /// <remarks>
    /// The one shape that has to survive intact whatever else changes: a <c>://</c> early in the
    /// string and an <c>@</c> late in it, with everything between them the very thing the message
    /// exists to display.
    /// </remarks>
    [Fact]
    public void AnAddressBetweenTheSchemeAndAnEmail_IsNotSweptIntoTheRedaction()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(
                builder,
                Config(connectionString: "Data Source=tcp://db.internal:1433;UID=a@b.com;Database=orders")));

        Assert.Contains("1433;UID=a@b.com", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("***", ex.Message);
    }

    /// <summary>
    /// A whole entry nobody has filled in is answered with a whole entry.
    /// </summary>
    /// <remarks>
    /// The all-four case is the fresh-block case, where a literal example is the most useful
    /// sentence available — the same thing the <c>"direct"</c> source offers for its one field. The
    /// message also pairs each field with its own environment variable on its own line rather than
    /// listing the fields and then the variables, which is the shape
    /// <c>DeveloperConfigValidator.Failure</c> uses for the same reason.
    /// </remarks>
    [Fact]
    public void AnEmptyBlock_ShowsAWholeEntryAndPairsEachFieldWithItsVariable()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(service: null, port: null, context: null, connectionString: null)));

        Assert.Contains("A whole entry reads:", ex.Message);
        Assert.Contains("\"source\": \"kubernetes\"", ex.Message);
        Assert.Contains(
            "  - 'kubernetes.service' — the Kubernetes Service to forward to. Set it in the file, or as "
            + $"ServiceSources__BackingServices__{Name}__Kubernetes__Service.",
            ex.Message);
    }

    /// <remarks>
    /// One missing field reads as a sentence rather than as a list of one, which is what
    /// <c>DeveloperConfigValidator.Failure</c> does and why: the ordinary case pays nothing for the
    /// collecting.
    /// </remarks>
    [Fact]
    public void OneMissingField_ReadsAsASentenceRatherThanAListOfOne()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(context: null)));

        Assert.Contains("requires 'kubernetes.context'", ex.Message);
        Assert.DoesNotContain("  - ", ex.Message);
        Assert.DoesNotContain("A whole entry reads:", ex.Message);
    }
}
