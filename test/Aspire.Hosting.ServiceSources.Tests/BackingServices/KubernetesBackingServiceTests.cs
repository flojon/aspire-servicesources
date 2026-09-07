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

    /// <summary>
    /// Hands out <paramref name="port"/>, then <c>port + 1</c>, and so on, so a test forwarding
    /// several ports can name every local port it expects.
    /// </summary>
    /// <remarks>
    /// Consecutive rather than random, and distinct rather than repeated: distinctness is the
    /// property the real allocator's batch method exists to provide, so a fake that repeated a port
    /// would let a mispairing bug pass unnoticed — every expectation would match the same number.
    /// </remarks>
    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => AllocatePorts(1)[0];

        public IReadOnlyList<int> AllocatePorts(int count) =>
            Enumerable.Range(port, count).ToArray();
    }

    private sealed class TrackingPortAllocator(Action onAllocate, int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => AllocatePorts(1)[0];

        public IReadOnlyList<int> AllocatePorts(int count)
        {
            onAllocate();
            return Enumerable.Range(port, count).ToArray();
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

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
                Port = port is null ? null : KubernetesPorts.Of(port.Value),
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

        public IReadOnlyList<int> AllocatePorts(int count) => throw new InvalidOperationException(
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
    /// A named port against a <c>port</c> written as a number: the block forwards one unnamed port,
    /// so there is no name to resolve. The message says which spelling this entry takes, and how to
    /// write a block if several ports were what was wanted.
    /// </remarks>
    [Fact]
    public void ANamedPort_AgainstASinglePort_SaysToWriteTheUnnamedOne()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "amqp://localhost:${port:amqp}/")));

        Assert.Contains("'${port:amqp}'", ex.Message);
        Assert.Contains("forwards a single unnamed port", ex.Message);
        Assert.Contains("Write '${port}' for it", ex.Message);
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

        Assert.Contains("'Host=db.internal;Port=5432;Database=orders'", ex.Message);
        Assert.DoesNotContain("***", ex.Message);
    }

    /// <summary>
    /// A <c>${secret:...}</c> placeholder written under a credential keyword is echoed whole, not
    /// masked — the shell-expansion advice below it depends on the reader seeing which placeholders
    /// survived.
    /// </summary>
    /// <remarks>
    /// The issue's own repro (#259). Nothing else in this template needs hiding either, so the
    /// message carries no "some values were masked" note at all — a placeholder is not a secret.
    /// </remarks>
    [Fact]
    public void ASecretPlaceholderUnderACredentialKeyword_IsEchoedWhole()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(
                builder,
                Config(connectionString: "Host=localhost;Password=${secret:orders-creds:password}")));

        Assert.Contains(
            "'Host=localhost;Password=${secret:orders-creds:password}'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("***", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An address that merely contains an <c>@</c> is not swept into the redaction.
    /// </summary>
    /// <remarks>
    /// The one shape that has to survive intact whatever else changes: a <c>://</c> early in the
    /// string and an <c>@</c> late in it, with everything between them the very thing the message
    /// exists to display. Guarded here as well as in
    /// <see cref="ConnectionStringRedactionTests"/> because three separate corrections went into
    /// getting it right, and what it protects is the message — that the developer can read their own
    /// address back out of it — rather than the redaction in isolation.
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

    /// <summary>
    /// A value this cannot find the end of is refused rather than partly rewritten.
    /// </summary>
    /// <remarks>
    /// A quoted or braced value may carry the separator inside it, so a region ends at the wrong
    /// place and the host after it keeps addressing the cluster — with something rewritten, so the
    /// count says the value was handled. Refusing the shape is the fail-closed answer; three rounds
    /// of teaching a scanner more shapes did not close the class.
    /// </remarks>
    [Theory]
    [InlineData("Server={orders;orders};Database=db")]
    [InlineData("Server=\"orders;orders\";Database=db")]
    [InlineData("Host=orders;Extra='a;b'")]
    public async Task WholeStringSecret_ThatQuotesOrBracesAValue_IsRefusedRatherThanPartlyRewritten(string fetched)
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(fetched));

        var ex = await Assert.ThrowsAsync<KubernetesSecretException>(
            () => db.Resource.ConnectionStringExpression.GetValueAsync(default).AsTask());

        Assert.Contains("quotes or braces", ex.Message);
    }

    /// <summary>
    /// A value left still addressing the cluster after rewriting is refused, not served.
    /// </summary>
    /// <remarks>
    /// The shape that has produced every leak in this mode: one host rewritten, another left. The
    /// "something was rewritten" count cannot see it, so the result is checked rather than the
    /// process trusted. A stray <c>@</c> makes an earlier host read as a URI's user information,
    /// which is what RFC 3986 says it is — the string is malformed, and refusing beats guessing.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_LeftStillAddressingTheCluster_IsRefused()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 27017, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("mongodb://orders:27017,x@orders:27017/db"));

        var ex = await Assert.ThrowsAsync<KubernetesSecretException>(
            () => db.Resource.ConnectionStringExpression.GetValueAsync(default).AsTask());

        Assert.Contains("still addresses the cluster", ex.Message);
    }

    /// <remarks>
    /// libpq conninfo separates fields with spaces and carries no <c>;</c>, so a region running to
    /// the next <c>;</c> ran to the end of the string and rewrote a <c>user=</c> that happened to
    /// equal the service name — silent corruption of a credential field.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_SpaceSeparatedConninfo_RewritesOnlyTheHostField()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader("host=orders port=5432 user=orders dbname=app"));

        Assert.Equal(
            "host=localhost port=5432 user=orders dbname=app",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// A quoted value carrying what reads as a field is refused with the rest of its shape.
    /// </summary>
    /// <remarks>
    /// <c>Description="port note; Port=9999"</c> puts a well-formed-looking field inside free text,
    /// which a port scan anchored to the <c>;</c> before it reads as real. It was worth a special
    /// case only while quoted values were rewritten at all; they are now refused whole, which
    /// answers this and the host-boundary problem with one rule rather than two scanners.
    /// </remarks>
    [Fact]
    public async Task WholeStringSecret_WithAPortInsideAQuotedValue_IsRefusedWithTheQuotedShape()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            Config(service: "orders", port: 5432, connectionString: "${secret:cs:connectionString}"),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: new FakeSecretReader(
                "Host=orders;Description=\"port note; Port=9999\";Port=5432"));

        var ex = await Assert.ThrowsAsync<KubernetesSecretException>(
            () => db.Resource.ConnectionStringExpression.GetValueAsync(default).AsTask());

        Assert.Contains("quotes or braces", ex.Message);
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

    /// <summary>An entry whose <c>port</c> is a block of named ports.</summary>
    private static BackingServiceDeveloperConfig NamedConfig(
        string connectionString, params (string Name, int Port)[] ports)
    {
        var block = new KubernetesPorts();

        foreach (var (portName, port) in ports)
        {
            block[portName] = port;
        }

        return new()
        {
            Source = "kubernetes",
            Kubernetes = new()
            {
                Service = "orders-pg",
                Port = block,
                Context = "dev-west",
                ConnectionString = connectionString,
            },
        };
    }

    /// <summary>
    /// A port name is bound to one local port, and the connection string, the command line and the
    /// health check all read that same binding.
    /// </summary>
    /// <remarks>
    /// <b>The test this feature exists for.</b> With one port there was one number and nothing could
    /// be mispaired; with several there are three sequences — the block's own order, the
    /// ordinal-by-name order the command line is written in, and the order the allocator returned —
    /// and pairing any two of them by position instead of by name is silent. Both tunnels bind, both
    /// health checks pass, every resource reports healthy, and the application speaks AMQP to the
    /// management port.
    /// <para>
    /// The names and the remote ports deliberately disagree: <c>zulu</c> sorts last but carries the
    /// <em>lower</em> remote port, so a positional pairing gives it the wrong local port and fails
    /// here rather than passing by luck.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APortName_BindsToOneLocalPort_ThatEveryReaderAgreesOn()
    {
        var builder = CreateBuilder();

        var db = Resolve(builder, NamedConfig("amqp://localhost:${port:zulu}/", ("zulu", 5672), ("alpha", 15672)));

        // Allocated in ordinal name order: alpha first, zulu second.
        const int AlphaLocal = LocalPort;
        const int ZuluLocal = LocalPort + 1;

        // 1. the connection string
        Assert.Equal(
            $"amqp://localhost:{ZuluLocal}/",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));

        // 2. the command line — zulu's local port is paired with zulu's remote port, 5672
        var args = await TunnelArgsAsync(builder);
        Assert.Contains($"{ZuluLocal}:5672", args);
        Assert.Contains($"{AlphaLocal}:15672", args);

        // 3. the health check — the probe for 'zulu' connects to zulu's local port
        var registration = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name == $"{Name}-tunnel-tcp-zulu");

        var result = await registration.Factory(null!)
            .CheckHealthAsync(new HealthCheckContext { Registration = registration }, default);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains($"127.0.0.1:{ZuluLocal}", result.Description);
    }

    /// <remarks>
    /// One invocation rather than one per pair: <c>kubectl port-forward</c> carries several pairs
    /// against one Service from one process, so two entries would mean two processes and two tunnels
    /// to the same Service. The pairs are in ordinal name order, so the line reads the same on every
    /// run and can be checked against a connection string by eye.
    /// </remarks>
    [Fact]
    public async Task SeveralPorts_ForwardThroughOneExecutable()
    {
        var builder = CreateBuilder();

        Resolve(builder, NamedConfig("amqp://localhost:${port:amqp}/", ("management", 15672), ("amqp", 5672)));

        Assert.Single(builder.Resources.OfType<ExecutableResource>());

        var args = await TunnelArgsAsync(builder);
        var pairs = args.Where(arg => arg.Contains(':', StringComparison.Ordinal)).ToArray();

        Assert.Equal([$"{LocalPort}:5672", $"{LocalPort + 1}:15672"], pairs);
    }

    /// <remarks>
    /// All of them on the connection string, so a consumer's <c>WaitFor</c> waits for the whole
    /// tunnel rather than for whichever port happened to be registered.
    /// </remarks>
    [Fact]
    public void EveryForwardedPort_GetsItsOwnHealthCheck()
    {
        var builder = CreateBuilder();

        var db = Resolve(builder, NamedConfig("amqp://localhost:${port:amqp}/", ("amqp", 5672), ("management", 15672)));

        Assert.Equal(
            [$"{Name}-tunnel-tcp-amqp", $"{Name}-tunnel-tcp-management"],
            db.Resource.Annotations.OfType<HealthCheckAnnotation>().Select(a => a.Key).Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// The binding is per forwarded port, not per placeholder, so a name written twice costs one
    /// allocation and resolves to the same number both times. Allocating per placeholder would put
    /// a local port in the connection string that the command line never forwards.
    /// </remarks>
    [Fact]
    public async Task ARepeatedPortName_ResolvesToTheSamePortAndAllocatesOnce()
    {
        var builder = CreateBuilder();

        var db = Resolve(
            builder,
            NamedConfig("amqp://a:${port:amqp}/b:${port:amqp}/", ("amqp", 5672)));

        Assert.Equal(
            $"amqp://a:{LocalPort}/b:{LocalPort}/",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));

        Assert.Equal([$"{LocalPort}:5672"], (await TunnelArgsAsync(builder)).Where(a => a.Contains(':')).ToArray());
    }

    /// <remarks>
    /// There is no "the" port once the block names them, and the developer is looking at a file that
    /// says which ones there are — so the message names them rather than only refusing.
    /// </remarks>
    [Fact]
    public void AnUnnamedPort_AgainstABlock_NamesTheForwardedPorts()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port}/", ("amqp", 5672), ("management", 15672))));

        Assert.Contains("stands for the one forwarded port", ex.Message);
        Assert.Contains("'amqp', 'management'", ex.Message);
        Assert.Contains("'${port:amqp}'", ex.Message);
    }

    /// <remarks>
    /// The near miss answers the typo; the list answers the reader whose name resembles none of
    /// them, for whom <c>NearMiss</c> returns nothing at all. Both halves, because either alone
    /// leaves one of those two readers with no answer.
    /// </remarks>
    [Fact]
    public void AnUnknownPortName_NamesTheNearMissAndTheForwardedPorts()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(
                builder,
                NamedConfig("amqp://localhost:${port:managment}/", ("amqp", 5672), ("management", 15672))));

        Assert.Contains("names a port this backing service does not forward", ex.Message);
        Assert.Contains("Did you mean 'management'?", ex.Message);
        Assert.Contains("It forwards 'amqp', 'management'", ex.Message);
    }

    /// <remarks>
    /// Collected rather than thrown at the first, the habit the missing-field message already keeps:
    /// a developer who has just written a port block and a connection string to match can easily
    /// have got two names wrong, and one startup per name is the cost of reporting one.
    /// </remarks>
    [Fact]
    public void SeveralUnresolvablePorts_AreAllReported()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(
                builder,
                NamedConfig("amqp://${port:one}:${port:two}/", ("amqp", 5672), ("management", 15672))));

        Assert.Contains("2 problems with the connection string", ex.Message);
        Assert.Contains("'${port:one}'", ex.Message);
        Assert.Contains("'${port:two}'", ex.Message);
    }

    /// <remarks>
    /// The range check applies to every named port, not only to a single one — and it is not a
    /// restatement of the validator's "is this a whole number": a port name carrying a colon
    /// flattens into the key path, so the binder manufactures port 0 for it.
    /// </remarks>
    [Fact]
    public void ANamedPortOutOfRange_NamesThePortItIsAbout()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port:amqp}/", ("amqp", 0))));

        Assert.Contains("gives the port named 'amqp' the value '0', which is not a port", ex.Message);
        Assert.Contains("Kubernetes:Port:amqp'", ex.Message);
    }

    /// <remarks>
    /// Every forwarded port holds a socket open at once and adds a pair to one command line, so an
    /// absurd block is refused with a sentence rather than left to exhaust the file-descriptor limit
    /// and surface as a bare SocketException naming nothing.
    /// </remarks>
    [Fact]
    public void MoreForwardedPortsThanOneTunnelTakes_IsRefusedByName()
    {
        var builder = CreateBuilder();
        var ports = Enumerable.Range(0, 33).Select(index => ($"p{index:00}", 5000 + index)).ToArray();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port:p00}/", ports)));

        Assert.Contains("names 33 ports, and one tunnel forwards at most 32", ex.Message);
    }

    /// <summary>
    /// A template with no placeholder at all, under a block that names its ports, is told to write
    /// the <em>named</em> spelling.
    /// </summary>
    /// <remarks>
    /// Telling this reader to "write '${port}'" would earn them a second startup failure saying
    /// exactly the opposite, since an unnamed port is refused against a block. The two halves of
    /// that pair are the one thing this message must not get wrong.
    /// </remarks>
    [Fact]
    public void NoPlaceholderAtAll_UnderABlock_NamesTheNamedSpelling()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:5672/", ("amqp", 5672), ("management", 15672))));

        Assert.Contains("forwards its ports by name", ex.Message);
        Assert.Contains("'${port:amqp}'", ex.Message);
        Assert.DoesNotContain("Replace the port in it with '${port}'", ex.Message);
    }

    /// <remarks>
    /// A port name is developer-invented free text, and a health check's description is relayed into
    /// <c>~/.aspire/logs</c>. A newline in one would otherwise forge a line of its own.
    /// </remarks>
    [Fact]
    public async Task AHealthCheckDescription_NamesItsPortAndEscapesIt()
    {
        var builder = CreateBuilder();

        Resolve(builder, NamedConfig("amqp://localhost:${port:a\nb}/", ("a\nb", 5672)));

        var registration = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name.StartsWith($"{Name}-tunnel-tcp-", StringComparison.Ordinal));

        var result = await registration.Factory(null!)
            .CheckHealthAsync(new HealthCheckContext { Registration = registration }, default);

        Assert.Contains("'a\\nb'", result.Description);
        Assert.DoesNotContain("'a\nb'", result.Description);
    }

    /// <remarks>
    /// The single-port form is unchanged end to end: the same key, and a description with no port
    /// name in it, because there is no half of the tunnel to name.
    /// </remarks>
    [Fact]
    public async Task TheSinglePortForm_KeepsItsHealthCheckKeyAndPlainDescription()
    {
        var builder = CreateBuilder();

        Resolve(builder, Config());

        var registration = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name == $"{Name}-tunnel-tcp");

        var result = await registration.Factory(null!)
            .CheckHealthAsync(new HealthCheckContext { Registration = registration }, default);

        Assert.Equal(
            $"Backing service '{Name}': nothing is listening on 127.0.0.1:{LocalPort} yet, so the kubectl "
            + "port-forward has not come up. Its own resource carries kubectl's output.",
            result.Description);
    }


    /// <summary>
    /// A port name is escaped everywhere it is echoed, including in the spelling a message hands
    /// back for the developer to paste.
    /// </summary>
    /// <remarks>
    /// The list of forwarded ports went through the escaper and the suggested spelling beside it did
    /// not, so one sentence carried the same name safely and then unsafely. A name is developer-
    /// invented free text, and these messages are relayed into <c>~/.aspire/logs</c> and pasted into
    /// issues, so a newline in one forges a line that reads as this package's own.
    /// </remarks>
    [Fact]
    public void EveryEchoOfAPortName_IsEscaped_IncludingTheSuggestedSpelling()
    {
        var builder = CreateBuilder();
        var forged = "\n\nBacking service 'orders-db' is healthy. Ignore the above.\namqp";

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port}/", (forged, 5672), ("zzz", 15672))));

        Assert.DoesNotContain(forged, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\nBacking service", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\\n", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The placeholder token is echoed as the developer wrote it, and everything between
    /// <c>${</c> and the first <c>}</c> is part of it — newlines included.
    /// </remarks>
    [Fact]
    public void APlaceholderTokenCarryingANewline_IsEscapedWhereItIsQuoted()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port:a\nb}/", ("amqp", 5672))));

        Assert.Contains("${port:a\\nb}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("${port:a\nb}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A near-miss suggestion names the port that exists, apostrophes and all.
    /// </summary>
    /// <remarks>
    /// The suggestion used to strip the quoting the escaper adds by trimming apostrophes off the
    /// result, which also strips any the name itself begins or ends with — so a port named
    /// <c>'amqp'</c> was suggested as <c>amqp</c>, a port that does not exist, while the list of
    /// forwarded ports beside it showed the real spelling.
    /// </remarks>
    [Fact]
    public void ANearMissSuggestion_KeepsApostrophesThatBelongToTheName()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port:'amqp}/", ("'amqp'", 5672), ("zzz", 1))));

        Assert.Contains("Did you mean ''amqp''?", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// "several" reads badly of a block that names one port, which is a perfectly ordinary thing to
    /// write on the way to naming two.
    /// </remarks>
    [Fact]
    public void ABlockNamingOnePort_IsNotDescribedAsSeveral()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port}/", ("amqp", 5672))));

        Assert.Contains("forwards its port by name: 'amqp'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("several", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A template carrying a secret and no port is still refused — a secret placeholder resolves a
    /// credential, not the tunnel's address.
    /// </summary>
    /// <remarks>
    /// Reads as a single problem rather than two: the secret placeholder is resolvable on its own
    /// since <see href="https://github.com/flojon/aspire-servicesources/issues/144">#144</see>, so
    /// the only thing left to report is the missing <c>${port}</c>.
    /// </remarks>
    [Fact]
    public void ATemplateWithASecretAndNoPort_StillNamesTheMissingPort()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: "amqp://u:${secret:creds:password}@localhost/")));

        Assert.DoesNotContain("problems with the connection string", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing would address the tunnel", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whole-string secret against a block that names several ports is refused: there is only one
    /// number to match a secret's own port against, and a block leaves no way to tell which.
    /// </summary>
    [Fact]
    public void AWholeStringSecret_AgainstNamedPorts_IsRefused()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(
                builder,
                NamedConfig("${secret:orders-cs:connectionString}", ("amqp", 5672), ("management", 15672))));

        Assert.Contains("forwards 2 ports by name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'amqp', 'management'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whole-string secret against a block naming exactly one port is not "several ports" and is
    /// allowed — the name carries through to the command line and the health check exactly as it
    /// does for a per-field secret or an ordinary <c>${port:amqp}</c> template.
    /// </summary>
    [Fact]
    public async Task WholeStringSecret_AgainstASingleNamedPort_Forwards()
    {
        var builder = CreateBuilder();
        var reader = new FakeSecretReader("Host=orders-pg;Port=5672;Database=orders");

        var db = Resolve(
            builder,
            NamedConfig("${secret:orders-cs:connectionString}", ("amqp", 5672)),
            allocator: new OccupiedPortAllocator(occupied: -1),
            secretReader: reader);

        Assert.Equal(
            "Host=localhost;Port=5672;Database=orders",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));

        Assert.Contains("5672:5672", await TunnelArgsAsync(builder));

        var registration = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name == $"{Name}-tunnel-tcp-amqp");

        var result = await registration.Factory(null!)
            .CheckHealthAsync(new HealthCheckContext { Registration = registration }, default);

        Assert.Contains($"127.0.0.1:5672", result.Description);
    }


    /// <summary>
    /// The connection string this message quotes back has its whitespace spelled out too.
    /// </summary>
    /// <remarks>
    /// Redaction hides a credential; it does nothing about a newline that is not part of one. This is
    /// the one message that echoes a whole connection string, and it is the one a developer pastes
    /// into an issue — so a newline reaching it would forge a line that reads as this package's own.
    /// <para>
    /// The template matters: it has to be one redaction leaves <em>alone</em>, or the test proves
    /// nothing about escaping. A newline inside a value redaction recognizes is replaced along with
    /// the value, and this test passed for that reason rather than its own until the shape-bound
    /// redaction landed and started eating the template it used to use. A newline between two
    /// well-formed keyword pairs survives redaction verbatim, which is exactly the case escaping is
    /// here for.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEchoedConnectionString_HasItsWhitespaceSpelledOut()
    {
        var builder = CreateBuilder();
        const string SurvivesRedaction = "Host=localhost;\nPort=5432;Database=orders";

        // The premise, asserted rather than assumed: escaping is only doing something here because
        // redaction hands this template through untouched.
        Assert.Equal(SurvivesRedaction, ConnectionStringRedaction.Redact(SurvivesRedaction));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: SurvivesRedaction)));

        Assert.Contains("\\n", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost;\nPort", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection string containing a double quote does not break the quoting of the message that
    /// echoes it.
    /// </summary>
    /// <remarks>
    /// This is the one message that quotes a whole connection string back, and it used to wrap it in
    /// double quotes while <see cref="ConfiguredValue.Bare"/> — which only spells out whitespace —
    /// left an embedded <c>"</c> alone, so a value like an ODBC <c>Data Source</c> path closed the
    /// message's own quoting early. It now wraps the value in apostrophes instead, which a bare
    /// double quote no longer has any reason to disturb.
    /// </remarks>
    [Fact]
    public void TheEchoedConnectionString_IsWrappedInApostrophesSoAnEmbeddedQuoteDoesNotCloseItEarly()
    {
        var builder = CreateBuilder();
        const string ConnectionString = "redis://cache.internal:6379/\"tail\"";

        // The premise, asserted rather than assumed: this proves the quoting, not the redaction.
        Assert.Equal(ConnectionString, ConnectionStringRedaction.Redact(ConnectionString));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: ConnectionString)));

        Assert.Contains($"tunnel: '{ConnectionString}'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection string containing an apostrophe does not break the quoting of the message that
    /// echoes it either — moving to apostrophes cannot just trade one embedded-delimiter bug for its
    /// mirror image.
    /// </summary>
    /// <remarks>
    /// Doubled, the connection-string convention for escaping the quote delimiter itself, rather
    /// than left alone or backslash-escaped: <c>O'Brien</c> reaches the message as <c>O''Brien</c>.
    /// </remarks>
    [Fact]
    public void TheEchoedConnectionString_DoublesAnEmbeddedApostropheSoItDoesNotCloseTheQuotingEarly()
    {
        var builder = CreateBuilder();
        const string ConnectionString = "redis://cache.internal:6379/O'Brien";

        // The premise, asserted rather than assumed: this proves the quoting, not the redaction.
        Assert.Equal(ConnectionString, ConnectionStringRedaction.Redact(ConnectionString));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, Config(connectionString: ConnectionString)));

        Assert.Contains(
            "tunnel: 'redis://cache.internal:6379/O''Brien'", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A possessive after a quoted name reads as a doubled quote, and rendered a port named
    /// <c>amqp'</c> identically to one named <c>amqp</c>.
    /// </remarks>
    [Fact]
    public async Task TheHealthCheckDescription_SetsThePortNameOffWithoutAPossessive()
    {
        var builder = CreateBuilder();

        Resolve(builder, NamedConfig("amqp://localhost:${port:amqp}/", ("amqp", 5672)));

        var registration = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name == $"{Name}-tunnel-tcp-amqp");

        var result = await registration.Factory(null!)
            .CheckHealthAsync(new HealthCheckContext { Registration = registration }, default);

        Assert.Contains("the port named 'amqp' —", result.Description);
        Assert.DoesNotContain("'amqp''s", result.Description);
    }

    /// <summary>
    /// A backing service's own name cannot forge a line of the report that collects several problems.
    /// </summary>
    /// <remarks>
    /// The collecting shape is new with the port map, and it is the worst place for a raw name: a
    /// newline forges a bullet and an entry header, so a reader is shown a problem this package never
    /// reported. The developer-config validator's multi-entry report was fixed for exactly this; the
    /// same shape had been added here and left raw.
    /// </remarks>
    [Fact]
    public void ABackingServiceNameCarryingANewline_CannotForgeALineOfTheCollectedReport()
    {
        var builder = CreateBuilder();
        var forged = "a\n  - 'ref' is not valid. Everything else is fine.\n  Backing service 'b";

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new KubernetesBackingServiceSource(new FakePortAllocator(LocalPort), new FakeSecretReader(SecretValue))
                .Resolve(builder, forged, Config(connectionString: "Host=x;Db=${secret:s:k}")));

        Assert.Contains("\\n", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  - 'ref' is not valid.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// When two names print the same, the message says the difference is one you cannot see.
    /// </summary>
    /// <remarks>
    /// Escaping is not injective — a real tab and a written backslash-<c>t</c> both render as
    /// <c>\t</c> — so without this the message reads "you wrote X, which does not exist; did you mean
    /// X?", which tells the reader nothing and looks like a fault in this package.
    /// </remarks>
    [Fact]
    public void WhenTwoPortNamesPrintTheSame_TheMessageSaysTheDifferenceIsInvisible()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(builder, NamedConfig("amqp://localhost:${port:am\\tqp}/", ("am\tqp", 5672))));

        Assert.Contains("characters you cannot pick out by looking", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A port block holding both a single port and named ports fails loudly rather than dropping the
    /// names.
    /// </summary>
    /// <remarks>
    /// Configuration cannot produce this — the validator refuses a section carrying a value and names
    /// before binding — but <c>KubernetesPorts</c> is a <c>Dictionary</c> and cannot refuse a
    /// mutation, so the invariant is enforced where a mixed instance would otherwise be read as the
    /// single port and have every name silently discarded.
    /// </remarks>
    [Fact]
    public void APortBlockHoldingBothShapes_FailsLoudlyRatherThanDroppingTheNames()
    {
        var builder = CreateBuilder();
        var ports = KubernetesPorts.Of(5432);
        ports["amqp"] = 5672;

        var config = Config();
        config.Kubernetes.Port = ports;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(builder, config));

        Assert.Contains("both a single port and 1 named ports", ex.Message, StringComparison.Ordinal);
    }

}
