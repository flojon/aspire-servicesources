using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.BackingServices;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Kubernetes;
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
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => port;
    }

    private sealed class TrackingPortAllocator(Action onAllocate, int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

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
        IPortAllocator? allocator = null,
        IKubernetesSecretReader? secretReader = null) =>
        new KubernetesBackingServiceSource(
                allocator ?? new FakePortAllocator(LocalPort),
                secretReader ?? new FakeSecretReader(SecretValue))
            .Resolve(builder, Name, config);

    /// <summary>The value the fake reader returns, so every expectation can name it.</summary>
    private const string SecretValue = "s3cr3t";

    private sealed class FakeSecretReader(string value) : IKubernetesSecretReader
    {
        public string Read(string context, string @namespace, string secretName, string key) => value;
    }

    /// <summary>
    /// Counts fetches and records what each was asked for, which is how the deferral is asserted:
    /// nothing during <c>Resolve</c>, one on the first resolution, and still one on the second.
    /// </summary>
    private sealed class TrackingSecretReader(string value) : IKubernetesSecretReader
    {
        public List<string> Reads { get; } = [];

        public string Read(string context, string @namespace, string secretName, string key)
        {
            Reads.Add($"{context}/{@namespace}/{secretName}/{key}");
            return value;
        }
    }

    /// <summary>An allocator that reports a port as taken, for whole-string mode's fail-fast.</summary>
    private sealed class OccupiedPortAllocator(int occupied) : IPortAllocator
    {
        public int AllocatePort() => throw new InvalidOperationException(
            "Whole-string mode must not allocate: it forwards the remote port to the same local port.");

        public bool IsAvailable(int port) => port != occupied;
    }

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
            // A named port, which is the placeholder this source still cannot resolve now that
            // stage 3 has taught it secrets. #233 is where that one goes.
            "a placeholder this source cannot resolve" =>
                Config(connectionString: "amqp://localhost:${port:amqp}/"),
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
            () => new KubernetesBackingServiceSource(
                    new FakePortAllocator(LocalPort), new FakeSecretReader(SecretValue))
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
    /// The echoed connection string has its credentials replaced.
    /// </summary>
    /// <remarks>
    /// This is the only message in the package that echoes a whole, valid connection string — every
    /// other echo is a malformed value or a single token — and an AppHost's startup failure is
    /// relayed into <c>~/.aspire/logs</c> and routinely pasted into an issue. The echo earns its
    /// place, since the shell-expansion case is only diagnosable by seeing what arrived, so the
    /// value is redacted rather than withheld.
    /// </remarks>
    [Theory]
    [InlineData("Host=db.internal;Port=5432;Username=dev;Password=hunter2", "hunter2", "Username=dev")]
    [InlineData("Host=db.internal;Port=5432;Pwd=hunter2", "hunter2", "Host=db.internal")]
    [InlineData("postgresql://orders_app:hunter2@db.internal:5432/orders", "hunter2", "orders_app")]
    [InlineData("redis://:hunter2@db.internal:6379", "hunter2", "db.internal")]
    // A ';' is legal and unencoded in userinfo (RFC 3986 puts it in sub-delims), so a password
    // carrying one must still be found — forbidding ';' in the password class leaked these whole.
    [InlineData("redis://user:pa;ss@db.internal:6379", "pa;ss", "user")]
    [InlineData("mongodb://user:p;w@db.internal:27017", "p;w", "mongodb://")]
    [InlineData(
        "BlobEndpoint=https://acct.blob.core.windows.net/;SharedAccessSignature=sv=2021&sig=hunter2",
        "hunter2",
        "BlobEndpoint=https://acct.blob.core.windows.net/")]
    [InlineData(
        "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=hunter2",
        "hunter2",
        "SharedAccessKeyName=root")]
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
    /// A connection string with nothing secret in it is echoed whole.
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
    /// Text that only resembles a credential keyword is echoed as written.
    /// </summary>
    /// <remarks>
    /// A keyword that merely starts with one of the reserved words is not a credential, and the
    /// lookbehind anchors on the <c>=</c> so it does not become one — <c>SharedAccessKeyName</c> is
    /// the case that matters, since it sits beside a key that genuinely is one.
    /// </remarks>
    [Theory]
    [InlineData("Host=db.internal;TokenExpiry=30;Database=orders", "TokenExpiry=30")]
    [InlineData("Host=db.internal;PasswordExpiry=30;Database=orders", "PasswordExpiry=30")]
    [InlineData("Host=db.internal;Integrated Security=SSPI;Database=orders", "Integrated Security=SSPI")]
    // The other half of the ';' question: an '@' belonging to a later keyword must not drag the
    // redaction across the fields between it and a '://' earlier in the string.
    [InlineData("Data Source=tcp://db.internal:1433;UID=a@b.com;Database=orders", "1433;UID=a@b.com")]
    public void AKeywordThatOnlyLooksLikeACredential_IsNotRedacted(string connectionString, string survives)
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: connectionString)));

        Assert.Contains(survives, ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task SecretPlaceholder_ResolvesToTheValueTheClusterHolds()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(connectionString: "Host=localhost;Port=${port};Password=${secret:orders-creds:password}"));

        Assert.Equal(
            $"Host=localhost;Port={LocalPort};Password={SecretValue}",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// The fetch is asked for with the context and namespace the entry configures, not with
    /// kubectl's own current ones.
    /// </summary>
    [Fact]
    public async Task SecretPlaceholder_FetchesFromTheConfiguredContextAndNamespace()
    {
        var builder = CreateBuilder();
        var reader = new TrackingSecretReader(SecretValue);

        var db = Resolve(
            builder,
            Config(
                context: "dev-west",
                @namespace: "orders",
                connectionString: "Port=${port};Password=${secret:orders-creds:password}"),
            secretReader: reader);

        await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Equal(["dev-west/orders/orders-creds/password"], reader.Reads);
    }

    /// <remarks>
    /// The namespace defaults the same way the port-forward's does, so a secret and the tunnel
    /// beside it are never read out of two different namespaces.
    /// </remarks>
    [Fact]
    public async Task SecretPlaceholderWithNoNamespace_FetchesFromTheDefaultNamespace()
    {
        var builder = CreateBuilder();
        var reader = new TrackingSecretReader(SecretValue);

        var db = Resolve(
            builder,
            Config(
                context: "dev-west",
                @namespace: null,
                connectionString: "Port=${port};Password=${secret:c:password}"),
            secretReader: reader);

        await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Equal(["dev-west/default/c/password"], reader.Reads);
    }

    /// <summary>
    /// The fetch is deferred: nothing runs during <c>AddBackingService</c>, one fetch happens when
    /// something first asks for the value, and asking again does not fetch again.
    /// </summary>
    /// <remarks>
    /// The whole reason the value travels as a parameter rather than as text. Resolving eagerly
    /// would run kubectl while the AppHost is being composed — the path local project resolution
    /// deliberately moved off — and would fail the whole AppHost for a developer who has simply not
    /// logged in to the cluster yet.
    /// </remarks>
    [Fact]
    public async Task SecretFetch_IsDeferredUntilTheValueIsAskedForAndHappensOnce()
    {
        var builder = CreateBuilder();
        var reader = new TrackingSecretReader(SecretValue);

        var db = Resolve(
            builder,
            Config(connectionString: "Password=${secret:orders-creds:password};Port=${port}"),
            secretReader: reader);

        Assert.Empty(reader.Reads);

        await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Single(reader.Reads);

        await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Single(reader.Reads);
    }

    /// <remarks>
    /// <c>secret: true</c> is what masks the value in the dashboard, and is most of the reason to
    /// carry it as a parameter at all.
    /// </remarks>
    [Fact]
    public void SecretPlaceholder_BecomesAParameterMarkedSecret()
    {
        var builder = CreateBuilder();

        Resolve(builder, Config(connectionString: "Password=${secret:orders-creds:password};Port=${port}"));

        var parameter = Assert.Single(builder.Resources.OfType<ParameterResource>());

        Assert.True(parameter.Secret);
        Assert.Equal($"{Name}-orders-creds-password", parameter.Name);
    }

    /// <summary>
    /// A connection string that is exactly one secret placeholder forwards the remote port to the
    /// same local port, and rewrites the in-cluster host the secret was written against.
    /// </summary>
    /// <remarks>
    /// The mode exists for hand-authored secrets — a Sealed Secret holding one whole connection
    /// string — where there are no per-field keys to fall back on and re-shaping means re-sealing
    /// against the cluster's key. Nothing in the template can be substituted into, so the only
    /// rewrite available is the host, and the port has to match what the string already names.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_ForwardsTheSamePortAndRewritesTheHost()
    {
        var builder = CreateBuilder();
        var reader = new FakeSecretReader("Host=orders-pg;Port=5432;Database=orders");

        var db = Resolve(
            builder,
            Config(service: "orders-pg", port: 5432, connectionString: "${secret:orders-cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: reader);

        Assert.Equal(
            "Host=localhost;Port=5432;Database=orders",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));

        Assert.Equal(
            ["port-forward", "svc/orders-pg", "5432:5432", "--context", "dev-west", "--namespace", "default"],
            await TunnelArgsAsync(builder));
    }

    /// <remarks>
    /// All four forms a pod can resolve, since a secret written in the cluster may use any of them.
    /// </remarks>
    [Theory]
    [InlineData("Host=orders-pg;Port=5432")]
    [InlineData("Host=orders-pg.orders;Port=5432")]
    [InlineData("Host=orders-pg.orders.svc;Port=5432")]
    [InlineData("Host=orders-pg.orders.svc.cluster.local;Port=5432")]
    public async Task WholeStringSecret_RewritesEveryInClusterHostForm(string fetched)
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(
                service: "orders-pg",
                port: 5432,
                @namespace: "orders",
                connectionString: "${secret:orders-cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(fetched));

        Assert.Equal(
            "Host=localhost;Port=5432",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// The host rewrite is bounded, so a service name that also appears as an ordinary value —
    /// a database named after the service is the common case — is left alone.
    /// </summary>
    [Fact]
    public async Task WholeStringSecret_DoesNotRewriteTheServiceNameUsedAsAValue()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:orders-cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(
                "Host=orders;Port=5432;Database=orders;User=orders;Password=orders"));

        // Every 'orders' after the first is a value, not a host. A word boundary does not separate
        // them — '=' and ';' bound a word — so this is the case a boundary-only rewrite gets wrong,
        // and it is the ordinary Postgres shape: the service, the database and the role share a name.
        Assert.Equal(
            "Host=localhost;Port=5432;Database=orders;User=orders;Password=orders",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// In a URI the text after <c>//</c> is the user when an <c>@</c> follows it, not the host.
    /// </summary>
    [Fact]
    public async Task WholeStringSecret_DoesNotRewriteAUriUserNameMatchingTheService()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "postgres", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("postgresql://postgres:pw@postgres.default:5432/postgres"));

        Assert.Equal(
            "postgresql://postgres:pw@localhost:5432/postgres",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <remarks>
    /// DNS folds case; the rewrite has to as well, or a secret naming <c>Orders-PG</c> keeps an
    /// address that only resolves inside the cluster.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_RewritesRegardlessOfCase()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders-pg", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("Host=Orders-PG.Default;Port=5432"));

        Assert.Equal(
            "Host=localhost;Port=5432",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// A secret that addresses the service by something this cannot rewrite is refused, rather than
    /// handed over still pointing into the cluster.
    /// </summary>
    /// <remarks>
    /// Whole-string mode exists because the fetched value is unusable as fetched. Zero substitutions
    /// means that premise did not hold, and passing the value through would send the credentials in
    /// it whichever way the developer's own DNS resolves a cluster name — which, behind a VPN or a
    /// search domain, need not be nowhere.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_ThatNamesTheServiceInNoRecognisedForm_IsRefused()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders-pg", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("Host=10.42.3.5;Port=5432;Database=orders"));

        var ex = await Assert.ThrowsAsync<KubernetesSecretException>(
            () => db.Resource.ConnectionStringExpression.GetValueAsync(default).AsTask());

        Assert.Contains("does not address 'orders-pg'", ex.Message);
    }

    /// <summary>
    /// Every host in a list is rewritten, not only the one a keyword or a <c>//</c> introduces.
    /// </summary>
    /// <remarks>
    /// A host list carries its later entries after a comma with nothing in front of them, so a
    /// rewrite anchored to the introducer left them addressed at the cluster while the
    /// "something was rewritten" count reached one and the value was handed over. A replica set and
    /// a failover partner are the ordinary shapes this arrives in.
    /// </remarks>
    [Theory]
    [InlineData("Server=orders,orders;Database=db", "Server=localhost,localhost;Database=db")]
    [InlineData(
        "mongodb://user:pw@orders:5432,orders:5432,orders:5432/db",
        "mongodb://user:pw@localhost:5432,localhost:5432,localhost:5432/db")]
    [InlineData("Host=orders.default.svc,orders;Port=5432", "Host=localhost,localhost;Port=5432")]
    public async Task WholeStringSecret_RewritesEveryHostInAList(string fetched, string expected)
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(fetched));

        Assert.Equal(expected, await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <remarks>
    /// The absolute form, with the trailing dot DNS allows. It resolves to the same service, so
    /// refusing it as "no form this can rewrite" would refuse a value that plainly names the host.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_RewritesTheAbsoluteFormWithATrailingDot()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("Host=orders.default.svc.cluster.local.;Port=5432"));

        Assert.Equal(
            "Host=localhost;Port=5432",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// A port written somewhere that is not a host is neither read as one nor mistaken for a
    /// mismatch.
    /// </summary>
    /// <remarks>
    /// Scanning the whole string for the first number found a decoy in a password and, worse,
    /// matched it against the forwarded port and passed while the real address named another. The
    /// ports are read only from inside a host's own region now.
    /// </remarks>
    [Theory]
    [InlineData("Password=Port=9999!;Host=orders,5432")]
    [InlineData("Options=Port=5432;Host=orders:5432")]
    public async Task WholeStringSecret_ReadsThePortFromTheHostRatherThanTheFirstNumber(string fetched)
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(fetched));

        Assert.Contains("localhost", await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <remarks>
    /// The mirror of the test above: a decoy that matches the forwarded port must not hide a real
    /// mismatch at the host.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_WithADecoyPortMatchingTheTunnel_StillRefusesTheRealMismatch()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("Options=Port=5432;Host=orders,9999"));

        var ex = await Assert.ThrowsAsync<KubernetesSecretException>(
            () => db.Resource.ConnectionStringExpression.GetValueAsync(default).AsTask());

        Assert.Contains("addresses port 9999", ex.Message);
    }

    /// <summary>
    /// A secret whose port is not the port being forwarded is refused, not silently served.
    /// </summary>
    /// <remarks>
    /// The tunnel's two ends both come from <c>kubernetes.port</c>. Unchecked, the app dials the
    /// port the secret names, the health check watches the port the tunnel serves, and every
    /// resource reports healthy while the connection reaches nothing — or reaches whatever else
    /// holds that port locally.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_AddressingAnotherPortThanTheTunnelServes_IsRefused()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders-pg", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("Host=orders-pg;Port=6432;Database=orders"));

        var ex = await Assert.ThrowsAsync<KubernetesSecretException>(
            () => db.Resource.ConnectionStringExpression.GetValueAsync(default).AsTask());

        Assert.Contains("addresses port 6432", ex.Message);
        Assert.Contains("serves 5432", ex.Message);
    }

    /// <summary>
    /// A key Aspire would refuse as a resource name is folded rather than rejected.
    /// </summary>
    /// <remarks>
    /// <c>DB_PASSWORD</c> is what <c>kubectl create secret --from-env-file</c> writes and
    /// <c>.dockerconfigjson</c> is the API's own key for a pull secret. Both are legal in a cluster
    /// and illegal in an Aspire resource name, and the developer cannot rename a secret they do not
    /// own — so refusing them would leave the placeholder unusable for the common case.
    /// </remarks>
    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData(".dockerconfigjson")]
    [InlineData("tls.key")]
    public void SecretKeyAspireWouldRefuseAsAName_IsFoldedRatherThanRejected(string key)
    {
        var builder = CreateBuilder();

        Resolve(builder, Config(connectionString: $"Port=${{port}};X=${{secret:app-secrets:{key}}}"));

        var parameter = Assert.Single(builder.Resources.OfType<ParameterResource>());

        Assert.True(parameter.Secret);

        // Character by character, because the collection overload of DoesNotContain would bind
        // instead and assert that a two-element set does not contain the name, which is true
        // whatever the name is.
        Assert.DoesNotContain('_', parameter.Name);
        Assert.DoesNotContain('.', parameter.Name);
        Assert.DoesNotContain("--", parameter.Name, StringComparison.Ordinal);
        Assert.False(parameter.Name.EndsWith('-'));
    }

    /// <summary>
    /// An <c>@</c> elsewhere in the value does not suppress the rewrite, and is not itself rewritten.
    /// </summary>
    /// <remarks>
    /// The guard that keeps a URI's user name out of the rewrite has to apply to the URI form only.
    /// Applied to a keyword connection string it reads any later <c>@</c> — a generated password
    /// containing one, or <c>User Id=admin@contoso.com</c> — as a reason to rewrite nothing, which
    /// then trips the "nothing was rewritten" refusal and blames the one setting that is right.
    /// </remarks>
    [Theory]
    [InlineData("Host=orders;Port=5432;Password=p@ssword", "Host=localhost;Port=5432;Password=p@ssword")]
    [InlineData(
        "Host=orders;Port=5432;User Id=admin@contoso.com",
        "Host=localhost;Port=5432;User Id=admin@contoso.com")]
    [InlineData(
        "Server=tcp:orders,5432;User ID=sa@orders;Password=x",
        "Server=tcp:localhost,5432;User ID=sa@orders;Password=x")]
    [InlineData(
        "Data Source=orders;Port=5432;Uid=x@y;Server=orders",
        "Data Source=localhost;Port=5432;Uid=x@y;Server=localhost")]
    public async Task WholeStringSecret_RewritesTheHostAndOnlyTheHost(string fetched, string expected)
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(fetched));

        Assert.Equal(expected, await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// A backing service whose own name Aspire refuses is reported against that name, not against
    /// the parameter a placeholder derived from it.
    /// </summary>
    /// <remarks>
    /// Aspire wants a resource name to start with a letter, and the parameter is added before the
    /// connection string is — so a backing service called <c>3proxy</c> used to fail first on the
    /// derived <c>3proxy-creds-password</c>, with a message about a name the developer never wrote
    /// and advice to shorten it, which fixes nothing. The derived name now carries a prefix, so what
    /// fails is the name the developer did write, and Aspire says why.
    /// </remarks>
    [Fact]
    public void ABackingServiceNameAspireRefuses_IsReportedAgainstThatNameNotTheParameter()
    {
        var builder = CreateBuilder();

        var ex = Record.Exception(
            () => new KubernetesBackingServiceSource(
                    new FakePortAllocator(LocalPort), new FakeSecretReader(SecretValue))
                .Resolve(builder, "3proxy", Config(connectionString: "Port=${port};P=${secret:creds:password}")));

        Assert.NotNull(ex);
        Assert.Contains("'3proxy'", ex.Message);
        Assert.DoesNotContain("creds-password", ex.Message);
    }

    /// <summary>
    /// Two keys that fold to the same characters still get separate parameters.
    /// </summary>
    [Fact]
    public void TwoKeysFoldingAlike_DoNotBecomeOneParameter()
    {
        var builder = CreateBuilder();

        Resolve(
            builder,
            Config(connectionString: "Port=${port};A=${secret:s:ca.crt};B=${secret:s:ca_crt}"));

        var names = builder.Resources.OfType<ParameterResource>().Select(p => p.Name).ToArray();

        Assert.Equal(2, names.Length);
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The same placeholder written twice is one value, so it is one parameter.
    /// </summary>
    /// <remarks>
    /// A connection string naming a host and a failover host carries the same credential twice.
    /// Adding the parameter twice would throw on the duplicate name, naming a resource the AppHost
    /// never wrote.
    /// </remarks>
    [Fact]
    public async Task TheSamePlaceholderTwice_IsOneParameterAndOneValue()
    {
        var builder = CreateBuilder();
        var reader = new TrackingSecretReader(SecretValue);

        var db = Resolve(
            builder,
            Config(connectionString: "Port=${port};A=${secret:c:password};B=${secret:c:password}"),
            secretReader: reader);

        Assert.Single(builder.Resources.OfType<ParameterResource>());

        Assert.Equal(
            $"Port={LocalPort};A={SecretValue};B={SecretValue}",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));

        Assert.Single(reader.Reads);
    }

    /// <remarks>
    /// A template with anything else in it gives somewhere to substitute a local port into, so the
    /// allocator's collision avoidance is kept rather than given up.
    /// </remarks>
    [Fact]
    public async Task SecretMixedWithOtherText_DoesNotSelectWholeStringMode()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(
                service: "orders-pg",
                port: 5432,
                connectionString: "Host=orders-pg;Port=${port};Password=${secret:c:password}"));

        Assert.Equal(
            $"Host=orders-pg;Port={LocalPort};Password={SecretValue}",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// Whole-string mode cannot pick another port, so a port already taken locally is refused
    /// before anything is added to the model, naming the backing service and the port.
    /// </summary>
    [Fact]
    public void WholeStringSecret_WithTheLocalPortTaken_FailsFast()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(
                builder,
                Config(port: 5432, connectionString: "${secret:orders-cs:connectionString}"),
                allocator: new OccupiedPortAllocator(occupied: 5432)));

        Assert.Contains(Name, ex.Message);
        Assert.Contains("5432", ex.Message);
        Assert.Empty(builder.Resources.OfType<ExecutableResource>());
    }

    /// <summary>
    /// A template that never addresses the tunnel is still refused — whole-string mode is the one
    /// exception, and only because the secret it resolves to carries the port itself.
    /// </summary>
    [Fact]
    public void SecretWithoutAPortPlaceholder_IsStillRefusedWhenItIsNotTheWholeString()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "Password=${secret:orders-creds:password}")));

        Assert.Contains("${port}", ex.Message);
    }
}
