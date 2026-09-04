using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// <c>AddBackingService</c> resolves the database, broker or cache a service connects to from
/// whichever source the developer configured, without the AppHost's own code changing.
/// </summary>
public class AddBackingServiceTests
{
    /// <summary>
    /// An AppHost directory carrying only <c>servicesources.local.json</c>.
    /// </summary>
    /// <remarks>
    /// No <c>servicesources.yaml</c>, deliberately and in nearly every test here: a backing service
    /// is declared by the <c>AddBackingService</c> call rather than by a catalog, so an AppHost that
    /// connects to a database and adds no source-switched service at all is a complete AppHost.
    /// Requiring an empty catalog to satisfy a lookup that never happens would be a bug.
    /// </remarks>
    private static string CreateAppHostDirectory(string? json = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        if (json is not null)
        {
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        }

        return dir;
    }

    private static IDistributedApplicationBuilder CreateBuilder(string? json = null) =>
        TestHelpers.CreateBuilder(CreateAppHostDirectory(json));

    /// <summary>
    /// A stand-in for whatever the AppHost would really provision — <c>AddPostgres(…)</c>,
    /// <c>AddRabbitMQ(…)</c> — that neither pulls an image nor needs a container runtime.
    /// </summary>
    private static Func<IResourceBuilder<IResourceWithConnectionString>> LocalFactory(
        IDistributedApplicationBuilder builder, Action? onInvoke = null) =>
        () =>
        {
            onInvoke?.Invoke();
            return builder.AddConnectionString("orders-db-local");
        };

    [Fact]
    public void NoEntryAtAll_InvokesTheLocalFactory()
    {
        var builder = CreateBuilder();
        var invocations = 0;

        var db = builder.AddBackingService("orders-db", LocalFactory(builder, () => invocations++));

        Assert.Equal(1, invocations);
        Assert.Equal("orders-db-local", db.Resource.Name);
    }

    /// <summary>
    /// The default adds nothing of its own: what the AppHost asked for, and not a wrapper around it.
    /// </summary>
    [Fact]
    public void NoEntryAtAll_AddsOnlyWhatTheFactoryAdded()
    {
        var builder = CreateBuilder();

        builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal(["orders-db-local"], builder.Resources.Select(resource => resource.Name));
    }

    /// <remarks>
    /// A blank source arrives from a higher layer blanking the key, which is the one gesture
    /// configuration offers for dropping a value a layer below set. Dropping the source is asking
    /// for the default, not naming a source nobody implements — the opposite of what a blank source
    /// means for a service, which has no default to fall back to.
    /// </remarks>
    [Fact]
    public void BlankSource_InvokesTheLocalFactory()
    {
        var builder = CreateBuilder("""{ "backingServices": { "orders-db": { "source": "" } } }""");
        var invocations = 0;

        builder.AddBackingService("orders-db", LocalFactory(builder, () => invocations++));

        Assert.Equal(1, invocations);
    }

    /// <summary>
    /// Whitespace is refused rather than read as the default, which an empty value legitimately is.
    /// </summary>
    /// <remarks>
    /// The same refusal every block field gets, and the reason is the same: whitespace is neither a
    /// value nor the empty spelling that unsets a key, and it is nearly always the latter missed by
    /// a character. Read as the default it started a database container for a developer who had
    /// written a source, and said nothing about the spaces that lost it.
    /// </remarks>
    [Fact]
    public void WhitespaceSource_IsRefusedRatherThanTakenForTheDefault()
    {
        var builder = CreateBuilder("""{ "backingServices": { "orders-db": { "source": "  " } } }""");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("Backing service 'orders-db'", ex.Message);
        Assert.Contains("'source'", ex.Message);
        Assert.Contains("whitespace rather than a value", ex.Message);
        Assert.Contains("empty value", ex.Message);
    }

    [Fact]
    public void ExplicitLocalSource_InvokesTheLocalFactory()
    {
        var builder = CreateBuilder("""{ "backingServices": { "orders-db": { "source": "local" } } }""");
        var invocations = 0;

        builder.AddBackingService("orders-db", LocalFactory(builder, () => invocations++));

        Assert.Equal(1, invocations);
    }

    /// <summary>
    /// The point of the whole exercise: a developer pointing the AppHost at a database they already
    /// run does not also get a container of it.
    /// </summary>
    [Fact]
    public void DirectSource_NeverInvokesTheLocalFactory()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=localhost;Port=5432;Database=orders" } } } }
            """);
        var invocations = 0;

        builder.AddBackingService("orders-db", LocalFactory(builder, () => invocations++));

        Assert.Equal(0, invocations);
        Assert.DoesNotContain("orders-db-local", builder.Resources.Select(resource => resource.Name));
    }

    [Fact]
    public async Task DirectSource_CarriesTheConfiguredConnectionString()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=localhost;Port=5432;Database=orders" } } } }
            """);

        var db = builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal("orders-db", db.Resource.Name);
        Assert.Equal(
            "Host=localhost;Port=5432;Database=orders",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// A brace that does not open a placeholder is literal text, so an ODBC-style connection string
    /// survives intact.
    /// </summary>
    /// <remarks>
    /// <c>Driver={PostgreSQL}</c> and <c>Server={host}\instance</c> are ordinary connection strings.
    /// A parser that claimed every <c>{…}</c> would reject them, and one that dropped the braces
    /// while building the reference expression would corrupt them silently — which is the failure
    /// worth a test, since the string only stops working once something tries to connect with it.
    /// </remarks>
    [Fact]
    public async Task DirectSource_ConnectionStringWithLiteralBraces_IsUnchanged()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Driver={PostgreSQL};Server={host}\\instance" } } } }
            """);

        var db = builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal(
            @"Driver={PostgreSQL};Server={host}\instance",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    [Fact]
    public void DirectSource_WithNoConnectionString_FailsNamingTheKeyThatSetsIt()
    {
        var builder = CreateBuilder("""{ "backingServices": { "orders-db": { "source": "direct" } } }""");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("Backing service 'orders-db'", ex.Message);
        Assert.Contains("'direct.connectionString'", ex.Message);
        Assert.Contains("ServiceSources__BackingServices__orders-db__Direct__ConnectionString", ex.Message);
    }

    /// <remarks>
    /// A permanent limit rather than an unfinished one: <c>"direct"</c> connects to an address the
    /// developer supplies, so there is no port of ours to substitute. Said that way, rather than as
    /// "unknown placeholder", because the placeholder is real and works under another source.
    /// </remarks>
    [Fact]
    public void DirectSource_WithPortPlaceholder_FailsSayingNothingIsForwarded()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=localhost;Port={port}" } } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("'{port}'", ex.Message);
        Assert.Contains("forwards nothing", ex.Message);

        // The escape, for the reader who did not mean a placeholder at all — without it the message
        // explains a substitution they never asked for and offers them nothing to write.
        Assert.Contains("'{{port}}'", ex.Message);
    }

    /// <summary>
    /// A doubled brace passes the placeholder through as text, so a connection string can carry
    /// <c>{port}</c> literally.
    /// </summary>
    [Fact]
    public async Task DirectSource_EscapedPlaceholder_ReachesTheAppAsText()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=localhost;Note={{port}}" } } } }
            """);

        var db = builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal(
            "Host=localhost;Note={port}",
            await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    [Fact]
    public void DirectSource_WithSecretPlaceholder_FailsSayingItIsNotSupportedYet()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Password={secret:orders-creds:password}" } } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("'{secret:orders-creds:password}'", ex.Message);
        Assert.Contains("not supported yet", ex.Message);
    }

    /// <summary>
    /// A malformed placeholder is reported as malformed, ahead of the "not supported yet" above.
    /// </summary>
    /// <remarks>
    /// Order matters: telling a developer who wrote <c>{secret:orders-creds}</c> that secrets are
    /// unsupported would send them off to work around a limit while their actual mistake — the
    /// missing key — went unmentioned.
    /// </remarks>
    [Fact]
    public void DirectSource_WithMalformedSecretPlaceholder_ReportsTheMalformedPlaceholder()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Password={secret:orders-creds}" } } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("names a secret and a key", ex.Message);
        Assert.DoesNotContain("not supported yet", ex.Message);
    }

    /// <remarks>
    /// <c>'local'</c> has to be in the list even though it is not in the dispatch table — it is the
    /// default, resolved before the table is consulted — or the list would be one the developer
    /// cannot act on.
    /// </remarks>
    [Fact]
    public void UnknownSource_NamesEveryValidSourceIncludingTheDefault()
    {
        var builder = CreateBuilder("""{ "backingServices": { "orders-db": { "source": "clsuter" } } }""");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("unknown source 'clsuter'", ex.Message);
        Assert.Contains("'direct'", ex.Message);
        Assert.Contains("'local'", ex.Message);
    }

    /// <remarks>
    /// The source most often arrives as a value someone typed into an environment variable by hand,
    /// and every other part of an entry is matched case-insensitively because configuration keys
    /// are.
    /// </remarks>
    [Fact]
    public void SourceSpelledWithCapitals_IsStillRecognized()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "Direct",
                "direct": { "connectionString": "Host=localhost" } } } }
            """);

        var db = builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal("orders-db", db.Resource.Name);
    }

    /// <remarks>
    /// Configuration merges keys case-insensitively, so the surviving key carries whichever casing a
    /// provider happened to use. Binding produces an ordinal dictionary, which would then miss the
    /// name the <c>AddBackingService</c> call spells.
    /// </remarks>
    [Fact]
    public void EntryKeyedWithDifferentCasing_IsStillFound()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "Orders-DB": {
                "source": "direct",
                "direct": { "connectionString": "Host=localhost" } } } }
            """);

        var db = builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal("orders-db", db.Resource.Name);
    }

    /// <summary>
    /// A higher configuration layer switches the source with no file edit, which is what the layered
    /// read exists for.
    /// </summary>
    [Fact]
    public async Task HigherLayer_SwitchesTheSourceWithoutTouchingTheFile()
    {
        var builder = CreateBuilder("""{ "backingServices": { "orders-db": { "source": "local" } } }""");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:BackingServices:orders-db:Source"] = "direct",
            ["ServiceSources:BackingServices:orders-db:Direct:ConnectionString"] = "Host=elsewhere",
        });

        var invocations = 0;
        var db = builder.AddBackingService("orders-db", LocalFactory(builder, () => invocations++));

        Assert.Equal(0, invocations);
        Assert.Equal("Host=elsewhere", await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <remarks>
    /// Reported as a backing service, not as a service: the two sections are edited in the same
    /// file, and "Service 'orders-db'" would send the reader to the wrong half of it.
    /// </remarks>
    [Fact]
    public void MalformedEntry_IsReportedAgainstTheBackingServiceShape()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": { "source": "direct", "connectionString": "Host=x" } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("Backing service 'orders-db'", ex.Message);
        Assert.DoesNotContain("Service 'orders-db'", ex.Message);
        Assert.Contains("'connectionString' is not a valid key here", ex.Message);
        Assert.Contains("'direct' block", ex.Message);
    }

    /// <summary>
    /// A misspelled field in a backing-service entry gets the near-miss message the service section
    /// gained in #182, because both sections are validated through the same shape-driven walk.
    /// </summary>
    [Fact]
    public void MisspelledFieldAtEntryRoot_NamesTheFieldAndItsBlock()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": { "source": "direct", "conectionString": "Host=x" } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("Did you mean 'connectionstring'", ex.Message);
        Assert.Contains("'direct' block", ex.Message);
    }

    /// <remarks>
    /// Every entry is validated, not only the one being resolved, so a mistake in an entry nothing
    /// has asked for yet is still found — the same widening the service section has.
    /// </remarks>
    [Fact]
    public void MalformedEntryForAnotherBackingService_StillFailsTheLoad()
    {
        var builder = CreateBuilder("""
            { "backingServices": {
                "orders-db": { "source": "local" },
                "unused": { "source": "direct", "nonsense": "x" } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", LocalFactory(builder)));

        Assert.Contains("unused", ex.Message);
        Assert.Contains("'nonsense'", ex.Message);
    }

    [Fact]
    public void LocalFactoryReturningNull_FailsNamingTheFactory()
    {
        var builder = CreateBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", () => null!));

        Assert.Contains("Backing service 'orders-db'", ex.Message);
        Assert.Contains("returned null", ex.Message);
    }

    /// <summary>
    /// A backing service resolves with no <c>servicesources.yaml</c> on disk.
    /// </summary>
    /// <remarks>
    /// The catalog is loaded for services and is required when one is resolved; a backing service
    /// has no catalog data at all, so its resolution must not be behind that load. Every other test
    /// here also runs without a catalog, but this one says so on purpose: it is the property that
    /// keeps "an AppHost that only connects to a database" a legal AppHost, and a future change that
    /// merged the two loads would break it with no other test noticing.
    /// </remarks>
    [Fact]
    public async Task NoCatalogOnDisk_ResolvesAnyway()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=localhost" } } } }
            """);

        Assert.False(File.Exists(Path.Combine(builder.AppHostDirectory, "servicesources.yaml")));

        var db = builder.AddBackingService("orders-db", LocalFactory(builder));

        Assert.Equal("Host=localhost", await db.Resource.ConnectionStringExpression.GetValueAsync(default));
    }

    /// <summary>
    /// The export carries <c>RunSyncOnBackgroundThread</c>, without which a guest-language AppHost
    /// deadlocks at startup while every C# test still passes.
    /// </summary>
    /// <remarks>
    /// Asserted directly, as well as being enforced by Aspire's <c>ASPIREEXPORT010</c> analyzer at
    /// build time, because the analyzer only sees the invocation while it is statically reachable
    /// from the exported method: passing the factory to an <c>IBackingServiceSource</c> and invoking
    /// it there silenced the diagnostic, measured on Aspire 13.5.2. That is a rearrangement a future
    /// refactor could make for perfectly good reasons, and this assertion survives it.
    /// </remarks>
    [Fact]
    public void Export_IsMarkedToRunTheCallbackOffTheRpcThread()
    {
        var export = typeof(BackingServiceBuilderExtensions)
            .GetMethod(nameof(BackingServiceBuilderExtensions.AddBackingService))!
            .GetCustomAttribute<AspireExportAttribute>();

        Assert.NotNull(export);
        Assert.True(
            export.RunSyncOnBackgroundThread,
            "AddBackingService invokes its 'local' delegate synchronously. Without "
            + "RunSyncOnBackgroundThread that invoke travels back over JSON-RPC on the thread the "
            + "host is still occupying, and a guest-language AppHost hangs with a "
            + "ConnectionLostException against the capability.");
    }

    /// <summary>
    /// The source names the config shape reports for its messages are the ones the dispatch actually
    /// accepts.
    /// </summary>
    /// <remarks>
    /// The shape declares them rather than deriving them from its blocks, because <c>"local"</c> has
    /// no block of its own — so the two can drift, and drifting means an entry written as a bare
    /// value stops being recognized as naming a source.
    /// </remarks>
    [Fact]
    public void ShapesSourceNames_MatchWhatTheDispatchAccepts() =>
        Assert.Equal(
            BackingServiceBuilderExtensions.KnownSources.Order(StringComparer.Ordinal),
            DeveloperConfigShape.BackingService.SourceNames.Order(StringComparer.Ordinal));
}
