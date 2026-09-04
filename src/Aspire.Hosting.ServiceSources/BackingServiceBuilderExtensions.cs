using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.BackingServices;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Adds a backing service — the database, broker or cache a service connects to — whose source
/// each developer chooses in their own <c>servicesources.local.json</c>.
/// </summary>
public static class BackingServiceBuilderExtensions
{
    /// <summary>
    /// The <c>source</c> value a backing service's developer config names, mapped to the
    /// implementation that resolves it.
    /// </summary>
    /// <remarks>
    /// Matched with <see cref="StringComparer.OrdinalIgnoreCase"/> for the same reason
    /// <c>AddService</c>'s table is: every other part of an entry arrives through
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>, which folds case, and the
    /// source most often arrives as a value someone typed into an environment variable by hand.
    /// <para>
    /// <see cref="DefaultSource"/> is not in here, and cannot be: it is the one source that runs
    /// the AppHost's own factory, which <see cref="AddBackingService"/> has to invoke itself rather
    /// than hand to an implementation. See that method's remarks.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, IBackingServiceSource> Sources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["direct"] = new DirectBackingServiceSource(),
        };

    /// <summary>
    /// The source a backing service resolves to when nothing configures it. Running it locally is
    /// what an AppHost reads as doing, so it is what an AppHost nobody has configured does.
    /// </summary>
    internal const string DefaultSource = "local";

    /// <summary>
    /// Every value <c>source</c> accepts. The authority for it, which
    /// <see cref="Config.DeveloperConfigShape.BackingService"/> is checked against by a test.
    /// </summary>
    internal static IReadOnlySet<string> KnownSources { get; } =
        Sources.Keys.Append(DefaultSource).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the backing service <paramref name="name"/> to <paramref name="builder"/> from
    /// whichever source this developer configured for it: the local instance
    /// <paramref name="local"/> provisions (the <c>"local"</c> source, and the default), or a
    /// connection string pointing at one they already run (the <c>"direct"</c> source).
    /// </summary>
    /// <param name="builder">The AppHost's builder.</param>
    /// <param name="name">
    /// The backing service's name, which is both the name of the resource Aspire runs and the key
    /// its entry is written under in <c>servicesources.local.json</c>. Unlike a service, a backing
    /// service is declared here rather than in a catalog, so this call is the only place the name
    /// has to exist.
    /// </param>
    /// <param name="local">
    /// How to provision the backing service locally — ordinary Aspire code, as
    /// <c>() =&gt; builder.AddPostgres("orders-pg").AddDatabase("orders-db")</c>. Invoked only when
    /// this developer's configured source is <c>"local"</c>, so a developer pointing the AppHost at
    /// an instance they already run does not also start a container of it.
    /// <para>
    /// <b>The resource it returns must be named <paramref name="name"/></b>, after the backing
    /// service, and this method throws when it is not. Aspire's <c>WithReference(...)</c> keys the
    /// connection string on the referenced resource's own name, which under this source is whatever
    /// this factory built — so a factory returning a resource named <c>orders</c> would give a
    /// consumer <c>ConnectionStrings__orders</c> while every other source gives it
    /// <c>ConnectionStrings__<paramref name="name"/></c>. Named alike, switching source changes the
    /// connection string's value and nothing else, which is the property this method exists to
    /// provide; named differently, it also moves the key the app reads, and the app is what reports
    /// that — by starting and finding no connection string. <c>AddDatabase("orders-db", "orders")</c>
    /// names the resource and the database separately where the two want different names. Casing
    /// counts: .NET folds it when reading configuration, but the environment variable itself does
    /// not, and a JavaScript or Java service reads that variable case-sensitively.
    /// </para>
    /// <para>
    /// A consumer can still choose a different key deliberately, by passing <c>WithReference</c> a
    /// <c>connectionName</c> — <c>WithReference(ordersDb, "OrdersDb")</c> gives
    /// <c>ConnectionStrings__OrdersDb</c> under every source. That is the answer when the app
    /// already reads a particular name. It is not a way around the rule above, because the exported
    /// shim takes the source alone and a guest-language AppHost has no way to pass the name through
    /// it. That is this package's own gap rather than a platform limit — Aspire projects an options
    /// bag perfectly well, and a project's <c>withReference</c> already accepts
    /// <c>{ connectionName }</c> — but until the shim grows one (#209), renaming the factory's
    /// resource is the one remedy every AppHost has.
    /// </para>
    /// </param>
    /// <returns>
    /// A handle to the resource that carries the connection string, to be passed to a consumer's
    /// <c>WithReference(...)</c> — through
    /// <see cref="ServiceConfigurationExtensions.Configure{T}"/> for a service added by
    /// <c>AddService</c>, or directly for any other Aspire resource.
    /// </returns>
    /// <example>
    /// <code>
    /// var ordersDb = builder.AddBackingService("orders-db",
    ///     local: () => builder.AddPostgres("orders-pg").AddDatabase("orders-db", "orders"));
    ///
    /// builder.AddService("orders")
    ///     .Configure&lt;IResourceWithEnvironment&gt;(r => r.WithReference(ordersDb))
    ///     .Configure&lt;IResourceWithWaitSupport&gt;(r => r.WaitFor(ordersDb));
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// Call this before the <c>AddService</c> whose <c>Configure</c> references it — ordinary C#
    /// variable ordering, nothing more.
    /// </para>
    /// <para>
    /// Which of a consumer's configuration actually reaches it still depends on that consumer's own
    /// source, and <see cref="ServiceConfigurationExtensions.Configure{T}"/> is what decides:
    /// <c>WithReference</c> is skipped with a warning for a <c>"kubernetes"</c>-sourced service,
    /// because environment given to a <c>kubectl port-forward</c> never reaches the pod behind it,
    /// while <c>WaitFor</c> is honoured, because holding that port-forward back is exactly what was
    /// asked for. So a <c>WaitFor</c> written against a local service survives a developer
    /// switching it, which is the property this package exists to protect.
    /// </para>
    /// <para>
    /// <c>RunSyncOnBackgroundThread</c> on the export is load-bearing rather than decoration.
    /// <paramref name="local"/> is invoked synchronously, and for a guest-language AppHost that
    /// invoke travels back over JSON-RPC while the host is still inside the capability call, which
    /// deadlocks the channel unless the dispatcher moves it off the RPC thread — measured, not
    /// assumed: the first probe run hung with a <c>ConnectionLostException</c> against the
    /// capability, and setting this was the only change between that run and the passing one.
    /// </para>
    /// <para>
    /// Aspire's <c>ASPIREEXPORT010</c> analyzer catches its absence at build time, and that is the
    /// only thing standing between a dropped attribute and a guest AppHost that hangs at startup
    /// while every C# test still passes — so this method is written to keep the analyzer able to
    /// see the invocation. It follows a static call one hop, but <b>not</b> a call through an
    /// interface: with the factory passed to an <see cref="IBackingServiceSource"/> and invoked
    /// there, the diagnostic went quiet and dropping the attribute became silent again (measured on
    /// Aspire 13.5.2, this repo's floor). Hence the local branch below, which invokes
    /// <paramref name="local"/> in this method's own body. A test asserts the attribute directly as
    /// well, since that holds however the call graph is later rearranged.
    /// </para>
    /// </remarks>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<IResourceWithConnectionString> AddBackingService(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        Func<IResourceBuilder<IResourceWithConnectionString>> local)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(local);

        // Before the resolution below, and before anything that can fail, for the same reason
        // AddService does it: this is the layer the AppHost's own IConfiguration reads back, so it
        // has to be present from this line on rather than from whichever line first resolved
        // something successfully.
        DeveloperConfigFileSource.EnsureRegistered(builder);

        // Before the resolution too, and unconditionally: the audit this feeds is about entries
        // nothing read, so a call that goes on to fail still has to count as having named its
        // backing service. Recording it only on the way out would report a configured entry as
        // orphaned because the call that would have read it threw.
        BackingServiceConfigAudit.Record(builder, name);

        var config = ServiceSourcesConfigCache.ResolveBackingService(builder, name);

        // Blank means unconfigured, and unconfigured means local — see ResolveBackingService. The
        // service side reports a blank source as an error instead, because a service has no default
        // to fall back to.
        var sourceName = string.IsNullOrWhiteSpace(config.Source) ? DefaultSource : config.Source;

        if (sourceName.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase))
        {
            var resource = local()
                ?? throw new ServiceSourcesConfigurationException(
                    $"Backing service '{name}': the factory passed to AddBackingService returned null. It has to "
                    + "return the resource that carries the connection string, as "
                    + "'() => builder.AddPostgres(\"pg\").AddDatabase(\"orders-db\", \"orders\")' does.");

            // Ordinal, so casing counts. It is tempting to fold it — .NET's IConfiguration does, so
            // a .NET consumer reads 'ConnectionStrings__Orders-DB' and 'ConnectionStrings__orders-db'
            // alike — but the variable itself is what differs, and this package runs JavaScript and
            // Java services too, where process.env and System.getenv are both case-sensitive. A
            // folded comparison would let exactly the key move this check exists to prevent through,
            // narrowed to casing and therefore harder to see. Both names are literals in the
            // AppHost's own code, so requiring them to agree exactly costs the author nothing.
            if (!string.Equals(resource.Resource.Name, name, StringComparison.Ordinal))
            {
                throw MisnamedLocalResourceError(name, resource.Resource.Name);
            }

            return resource;
        }

        if (!Sources.TryGetValue(sourceName, out var source))
        {
            throw UnknownSourceError(name, sourceName);
        }

        // The factory is deliberately not passed on. Every source below connects to something that
        // is already running, so invoking it would start a second copy of the very thing the
        // developer asked this AppHost not to run.
        return source.Resolve(builder, name, config);
    }

    /// <summary>
    /// The error for a <c>"local"</c> factory whose resource is not named after the backing service.
    /// </summary>
    /// <remarks>
    /// One remedy, and it is the rename. C# has a second — <c>WithReference(db, "orders-db")</c>
    /// names the connection from the consumer's side — but the exported shim takes the source alone,
    /// so a guest-language AppHost cannot reach it (#209). A message offering a fix that half its
    /// readers cannot reach would send them looking for an argument that is not there, so it offers
    /// the one that always works.
    /// <para>
    /// Worth stating precisely, because the obvious explanation is wrong in a way that would
    /// discourage fixing it: Aspire's Type System does erase overloads, but that is not what closes
    /// the door here. Aspire's own answer to that erasure is an options bag, which projects fine —
    /// a project's <c>withReference</c> already takes <c>{ connectionName }</c> from TypeScript. The
    /// missing argument is this package's, not the platform's.
    /// </para>
    /// <para>
    /// <c>AddDatabase</c> is named in the message because it is where the constraint most often
    /// bites: the Aspire resource and the database itself frequently want different names, and the
    /// two-argument overload is what separates them.
    /// </para>
    /// </remarks>
    private static ServiceSourcesConfigurationException MisnamedLocalResourceError(
        string name, string resourceName) =>
        new($"Backing service '{name}': the factory passed to AddBackingService returned a resource named "
            + $"'{resourceName}'. It has to be named '{name}', after the backing service, because Aspire keys a "
            + $"consumer's connection string on the referenced resource's own name — so this factory gives the app "
            + $"'ConnectionStrings__{resourceName}' while every other source gives it 'ConnectionStrings__{name}', "
            + "and switching source would move the key the app reads without anything reporting it. Rename the "
            + $"resource to '{name}'. Where the Aspire resource and the database itself want different names, "
            + $"AddDatabase names them separately: 'AddDatabase(\"{name}\", \"orders\")' is a resource called "
            + $"'{name}' holding a database called 'orders'.");

    /// <summary>
    /// The error for a <c>source</c> this package does not recognize.
    /// </summary>
    /// <remarks>
    /// Names the key rather than only the file, because the file is the lowest layer this value can
    /// arrive from: a developer whose environment carries a stale source would otherwise be sent to
    /// edit the one place it is not.
    /// </remarks>
    private static ServiceSourcesConfigurationException UnknownSourceError(string name, string sourceName)
    {
        // KnownSources rather than the dispatch table, which does not carry the default source: a
        // list of valid sources that omitted 'local' would be a list the developer could not act on.
        var known = string.Join(", ", KnownSources.Order(StringComparer.Ordinal).Select(s => $"'{s}'"));
        var key = $"{DeveloperConfiguration.BackingServicesKey}:{name}:source";

        return new ServiceSourcesConfigurationException(
            $"Backing service '{name}' has unknown source '{sourceName}'. Valid sources are {known}. "
            + $"Correct '{key}' in '{DeveloperConfiguration.FileName}', or wherever a higher layer set it — "
            + "appsettings, user secrets, the environment variable "
            + $"{key.Replace(":", "__", StringComparison.Ordinal)}, or the command line.");
    }
}
