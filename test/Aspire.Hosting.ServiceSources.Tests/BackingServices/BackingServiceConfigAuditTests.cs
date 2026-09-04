using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// Backing-service configuration that nothing reads is reported once the whole AppHost is composed
/// (#206).
/// </summary>
/// <remarks>
/// The state it catches is indistinguishable from the legitimate default at the moment each entry is
/// read: a backing service with no entry runs from the AppHost's own factory, which is correct for
/// an AppHost nobody has pointed anywhere. Only once every <c>AddBackingService</c> call has
/// happened is it knowable that a given entry was read by nobody — which is why this is a
/// <c>BeforeStartEvent</c> audit rather than a check inside the call.
/// <para>
/// A warning rather than an error, because a shared <c>servicesources.local.json</c> may carry
/// entries for backing services only some configurations add — the same reason the service side
/// validates every entry without requiring each to be used.
/// </para>
/// </remarks>
public class BackingServiceConfigAuditTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);

        return TestHelpers.CreateBuilder(dir);
    }

    private static Func<IResourceBuilder<IResourceWithConnectionString>> Factory(
        IDistributedApplicationBuilder builder, string name) =>
        () => builder.AddConnectionString(name);

    /// <summary>
    /// An entry whose key matches no <c>AddBackingService</c> call is named, along with what
    /// happened instead.
    /// </summary>
    /// <remarks>
    /// The failure #206 was filed for: <c>orders_db</c> against <c>AddBackingService("orders-db")</c>
    /// binds, validates, and is never looked up. <c>orders-db</c> then has no entry, no entry means
    /// <c>"local"</c>, and <c>"local"</c> starts the very container the developer was pointing the
    /// AppHost away from.
    /// </remarks>
    [Fact]
    public async Task EntryMatchingNoCall_IsReported()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders_db": {
                "source": "direct",
                "direct": { "connectionString": "Host=shared-dev;Database=orders" } } } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("orders_db", warning);
        Assert.Contains("servicesources.local.json", warning);
    }

    /// <summary>
    /// The orphan is offered the declared name it resembles, which is the whole of what a typo needs.
    /// </summary>
    [Fact]
    public async Task EntryResemblingADeclaredName_SuggestsIt()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders_db": { "source": "local" } } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("'orders-db'", warning);
    }

    /// <summary>
    /// An entry that is read is not reported, whatever casing it arrived under.
    /// </summary>
    /// <remarks>
    /// Configuration keys are case-insensitive, so <c>Orders-DB</c> is the entry
    /// <c>AddBackingService("orders-db")</c> reads. Compared ordinally this would warn about an
    /// entry that is working perfectly.
    /// </remarks>
    [Fact]
    public async Task EntryMatchingACallInAnotherCasing_IsNotReported()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "Orders-DB": { "source": "local" } } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// The healthy case says nothing at all — no entries, every backing service on its factory.
    /// </summary>
    /// <remarks>
    /// The regression that would matter most, since this is what every AppHost that has configured
    /// nothing looks like.
    /// </remarks>
    [Fact]
    public async Task NothingConfigured_IsNotReported()
    {
        var builder = CreateBuilder("""{ "services": { } }""");

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// Several orphans are one message, not one message each.
    /// </summary>
    /// <remarks>
    /// The anti-noise rule the warnings channel already follows for skipped configuration: the
    /// entries share a cause and a fix, so they share a line.
    /// </remarks>
    [Fact]
    public async Task SeveralOrphanedEntries_AreOneMessage()
    {
        var builder = CreateBuilder("""
            { "backingServices": {
                "orders_db": { "source": "local" },
                "billing_db": { "source": "local" } } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("orders_db", warning);
        Assert.Contains("billing_db", warning);
    }

    /// <summary>
    /// A misspelled <c>backingServices</c> root key is named, which is the half filed as #201.
    /// </summary>
    /// <remarks>
    /// It cannot be caught by rejecting unrecognised root keys: only the sections this package reads
    /// cross into the AppHost's configuration, precisely so the file can carry keys of its own, which
    /// leaves resemblance as the only thing separating a typo from a key the file legitimately holds.
    /// And it has no failure to attach itself to — every backing service simply falls back to
    /// <c>"local"</c>, which is a legal state.
    /// </remarks>
    [Fact]
    public async Task MisspelledRootKey_IsReported()
    {
        var builder = CreateBuilder("""
            { "backingSerivces": { "orders-db": { "source": "direct" } } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("backingSerivces", warning);
        Assert.Contains("backingServices", warning);
    }

    /// <summary>
    /// A misspelled root key is still reported when another configuration layer has contributed an
    /// entry of its own.
    /// </summary>
    /// <remarks>
    /// The check used to be gated on the bound section being empty, which is the <em>merged</em>
    /// view across every layer — so a single environment variable setting one entry hid the fact
    /// that the developer's whole file was going unread. The two questions are independent: whether
    /// the file's root key is a typo is a property of the file alone, and no other layer has a root
    /// key to answer it with.
    /// </remarks>
    [Fact]
    public async Task MisspelledRootKeyWithAnEntryFromAnotherLayer_IsStillReported()
    {
        var builder = CreateBuilder("""
            { "backingSerivces": { "orders-db": { "source": "direct" } } }
            """);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:BackingServices:orders-db:Source"] = "local",
        });

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("backingSerivces", warning);
    }

    /// <summary>
    /// A root key resembling nothing is left alone, since the file is allowed keys of its own.
    /// </summary>
    [Fact]
    public async Task UnrelatedRootKey_IsNotReported()
    {
        var builder = CreateBuilder("""
            { "myOwnSettings": { "anything": "at all" } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// A <c>services</c> key is not a near miss of <c>backingServices</c>, and does not become one
    /// just because no backing service is configured.
    /// </summary>
    /// <remarks>
    /// The two root keys are seven edits apart against a tolerance of two, so this is pinned as the
    /// property it relies on rather than as a coincidence.
    /// </remarks>
    [Fact]
    public async Task ServicesRootKeyAlone_IsNotReadAsAMisspelling()
    {
        var builder = CreateBuilder("""
            { "services": { "orders": { "source": "local" } } }
            """);

        builder.AddBackingService("orders-db", Factory(builder, "orders-db"));

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// With no <c>AddBackingService</c> call at all, a misspelled root key says nothing.
    /// </summary>
    /// <remarks>
    /// There is nothing the key would have fed, so there is nothing to report — and an AppHost that
    /// uses no backing services should never hear about the section.
    /// </remarks>
    [Fact]
    public async Task MisspelledRootKeyWithNoBackingServices_IsNotReported()
    {
        var builder = CreateBuilder("""
            { "backingSerivces": { "orders-db": { "source": "direct" } } }
            """);

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }
}
