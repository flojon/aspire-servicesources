using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// A <c>services</c> entry naming no service <c>servicesources.yaml</c> declares is reported once
/// the whole AppHost is composed (#215) — the narrower of the two gaps that issue raised; an entry
/// the catalog does declare but that no <c>AddService</c> call adds is filed separately as #217.
/// </summary>
/// <remarks>
/// Unlike the backing-service audit (#206), this needs no record of which <c>AddService</c> calls
/// happened: an entry naming no catalog service at all is already knowable the moment configuration
/// is read against the catalog, before any particular service is resolved. What still waits for
/// <c>BeforeStartEvent</c> is only the reporting of it, gated on at least one <c>AddService</c> call
/// having happened — an AppHost that adds no service should never hear about the section.
/// </remarks>
public class ServiceConfigAuditTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string catalogYaml, string localJson)
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), catalogYaml);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), localJson);

        return TestHelpers.CreateBuilderThatCanStart(dir);
    }

    /// <summary>The one real, resolvable catalog entry every test adds a service for.</summary>
    private const string OrdersCatalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: Orders.csproj
            container:
              image: ghcr.io/company/orders
              port: 8080
        """;

    /// <summary>
    /// An entry whose key matches no catalog service is named, along with what happened instead.
    /// </summary>
    /// <remarks>
    /// The failure #215 was filed for: <c>planning-fronend</c> against a catalog that declares
    /// <c>planning-frontend</c> — a well-formed entry, one edit from a real service name, that binds
    /// and validates and is never looked up. Unlike an orphaned backing-service entry, nothing here
    /// silently defaults to anything: the entry simply configures no service at all.
    /// </remarks>
    [Fact]
    public async Task EntryMatchingNoCatalogService_IsReported()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "services": {
                "orders": { "source": "container" },
                "odrers": { "source": "container" } } }
            """);

        builder.AddService("orders");

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("odrers", warning);
        Assert.Contains("servicesources.yaml", warning);
    }

    /// <summary>
    /// The orphan is offered the catalog name it resembles, which is the whole of what a typo needs.
    /// </summary>
    [Fact]
    public async Task EntryResemblingACatalogName_SuggestsIt()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "services": {
                "orders": { "source": "container" },
                "odrers": { "source": "container" } } }
            """);

        builder.AddService("orders");

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        // The suggestion itself, not just 'orders' appearing anywhere — the trailing "this
        // AppHost's catalog declares" clause names every catalog entry unconditionally, so
        // asserting on 'orders' alone would still pass if the near-miss lookup regressed to
        // finding nothing.
        Assert.Contains("(did you mean 'orders'?)", warning);
    }

    /// <summary>
    /// An entry that names a real catalog service is not reported, whatever casing it arrived under.
    /// </summary>
    /// <remarks>
    /// Configuration keys are case-insensitive, and the catalog's own canonicalization re-keys onto
    /// its spelling — so <c>Orders</c> is the same entry as <c>orders</c>, not an orphan of it.
    /// </remarks>
    [Fact]
    public async Task EntryMatchingACatalogServiceInAnotherCasing_IsNotReported()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "services": { "Orders": { "source": "container" } } }
            """);

        builder.AddService("orders");

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// The healthy case says nothing at all — every configured entry names a real catalog service.
    /// </summary>
    /// <remarks>
    /// The regression that would matter most, since this is what every AppHost that has configured
    /// itself correctly looks like.
    /// </remarks>
    [Fact]
    public async Task NothingOrphaned_IsNotReported()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "services": { "orders": { "source": "container" } } }
            """);

        builder.AddService("orders");

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// Several orphans are one message, not one message each.
    /// </summary>
    /// <remarks>
    /// The anti-noise rule the warnings channel already follows for skipped configuration and for
    /// the backing-service side of this same audit: the entries share a cause and a fix, so they
    /// share a line.
    /// </remarks>
    [Fact]
    public async Task SeveralOrphanedEntries_AreOneMessage()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "services": {
                "orders": { "source": "container" },
                "billing": { "source": "container" },
                "shiping": { "source": "container" } } }
            """);

        builder.AddService("orders");

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("billing", warning);
        Assert.Contains("shiping", warning);
    }

    /// <summary>
    /// A misspelled <c>services</c> root key is named, even though the call that discovers it fails.
    /// </summary>
    /// <remarks>
    /// It cannot be caught by rejecting unrecognised root keys: only the sections this package reads
    /// cross into the AppHost's configuration, precisely so the file can carry keys of its own, which
    /// leaves resemblance as the only thing separating a typo from a key the file legitimately holds.
    /// <para>
    /// Unlike the backing-service side, a service with no source anywhere throws — so the very call
    /// that first subscribes this audit also fails here, over the misspelled root key it is about to
    /// report. The audit runs anyway: it is subscribed before resolution is attempted, and the state
    /// it reports is a property of the file, not of whether any one call over it succeeded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MisspelledRootKey_IsReportedEvenThoughTheTriggeringCallFails()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "servics": { "orders": { "source": "container" } } }
            """);

        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("servics", warning);
        Assert.Contains("services", warning);
    }

    /// <summary>
    /// A misspelled root key is still reported when another configuration layer has contributed an
    /// entry of its own, so the call it feeds succeeds.
    /// </summary>
    /// <remarks>
    /// The check used to be gated on the bound section being empty on the backing-service side,
    /// which is the <em>merged</em> view across every layer — so a single environment variable
    /// setting one entry hid the fact that the developer's whole file was going unread. The two
    /// questions are independent: whether the file's root key is a typo is a property of the file
    /// alone, and no other layer has a root key to answer it with.
    /// </remarks>
    [Fact]
    public async Task MisspelledRootKeyWithAnEntryFromAnotherLayer_IsStillReported()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "servics": { "orders": { "source": "direct" } } }
            """);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:Services:orders:Source"] = "container",
        });

        builder.AddService("orders");

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("servics", warning);
    }

    /// <summary>
    /// An empty <c>services</c> section does not count as having the key, so a misspelling beside
    /// it is still reported.
    /// </summary>
    /// <remarks>
    /// The shape that defeated this check on the backing-service side: <c>"services": { }</c>
    /// contributes no configuration values, but it <em>is</em> returned by <c>GetChildren()</c> as a
    /// null-valued entry, so a presence test over those keys saw the key and stopped looking. A
    /// developer whose real entries sat under a misspelled key beside it was told nothing at all.
    /// </remarks>
    [Fact]
    public async Task EmptySectionBesideAMisspelledRootKey_StillReportsTheMisspelling()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            {
              "services": { },
              "servics": { "orders": { "source": "container" } }
            }
            """);

        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        var warning = Assert.Single(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));

        Assert.Contains("servics", warning);
    }

    /// <summary>
    /// A root key resembling nothing is left alone, since the file is allowed keys of its own.
    /// </summary>
    [Fact]
    public async Task UnrelatedRootKey_IsNotReported()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "myOwnSettings": { "anything": "at all" } }
            """);

        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// A <c>backingServices</c> key is not a near miss of <c>services</c>, and does not become one
    /// just because no service is configured under either.
    /// </summary>
    /// <remarks>
    /// The mirror of the backing-service side's own pin on this: the two root keys are far enough
    /// apart that resemblance is a property being tested, not a coincidence.
    /// </remarks>
    [Fact]
    public async Task BackingServicesRootKeyAlone_IsNotReadAsAMisspelling()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "backingServices": { "orders-db": { "source": "local" } } }
            """);

        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    /// <summary>
    /// With no <c>AddService</c> call at all, a misspelled root key says nothing.
    /// </summary>
    /// <remarks>
    /// There is nothing the key would have fed, so there is nothing to report — an AppHost that adds
    /// no service should never hear about the section.
    /// </remarks>
    [Fact]
    public async Task MisspelledRootKeyWithNoServiceCalls_IsNotReported()
    {
        var builder = CreateBuilder(OrdersCatalog, """
            { "servics": { "orders": { "source": "container" } } }
            """);

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }
}
