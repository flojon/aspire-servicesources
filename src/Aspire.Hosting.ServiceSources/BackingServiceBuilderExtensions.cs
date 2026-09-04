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
    /// <b>Name the resource it returns after the backing service.</b> Aspire's
    /// <c>WithReference(...)</c> keys the connection string on the referenced resource's own name,
    /// which under this source is whatever this factory built — so a factory returning a resource
    /// named <c>orders</c> gives a consumer <c>ConnectionStrings__orders</c>, while every other
    /// source gives it <c>ConnectionStrings__<paramref name="name"/></c>. Named alike, switching
    /// source changes the connection string's value and nothing else; named differently, it also
    /// moves the key the app reads, and the app is what reports that — by starting and finding no
    /// connection string. <c>AddDatabase("orders-db", "orders")</c> names the resource and the
    /// database separately where the two want different names.
    /// </para>
    /// <para>
    /// A consumer can settle it from its own side instead, by passing <c>WithReference</c> a
    /// <c>connectionName</c> — <c>WithReference(ordersDb, "OrdersDb")</c> gives
    /// <c>ConnectionStrings__OrdersDb</c> under every source, whatever this factory named its
    /// resource. That is the answer when the app already reads a particular name, or when the
    /// factory is not the caller's to rename; naming the resource after the backing service remains
    /// what keeps the default right for consumers that ask for nothing.
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

        var config = ServiceSourcesConfigCache.ResolveBackingService(builder, name);

        // Blank means unconfigured, and unconfigured means local — see ResolveBackingService. The
        // service side reports a blank source as an error instead, because a service has no default
        // to fall back to.
        var sourceName = string.IsNullOrWhiteSpace(config.Source) ? DefaultSource : config.Source;

        if (sourceName.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase))
        {
            return local()
                ?? throw new ServiceSourcesConfigurationException(
                    $"Backing service '{name}': the factory passed to AddBackingService returned null. It has to "
                    + "return the resource that carries the connection string, as "
                    + "'() => builder.AddPostgres(\"pg\").AddDatabase(\"orders\")' does.");
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
