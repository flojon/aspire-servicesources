using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Starts, in parallel and once, the git checkout for every <c>"local"</c>-sourced service whose
/// clone an <c>AddService()</c> call would otherwise have to wait for on its own.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddService()</c> has to hand back the real resource, so a <c>"local"</c> service can no longer
/// wait for <c>BeforeStartEvent</c> to be resolved. Cloning each service as it is asked for would
/// serialize every cold clone — the tax issue #2 removed. Instead the trigger moves rather than the
/// parallelism: the first call starts them all at once, so wall-clock stays <c>max(checkout)</c>,
/// and every later call finds its checkout already done.
/// </para>
/// <para>
/// The prefetch set comes from the developer configuration, which must already name every service
/// the AppHost adds. The converse does not hold — it may mark services <c>"local"</c> that this
/// AppHost never calls <c>AddService()</c> for — so the prefetch is <b>speculative</b> in
/// both what it does and what it reports. It must never invent a failure: a service missing from the
/// catalog is skipped, and a checkout that throws has its exception stored, re-thrown only if that
/// service is actually requested, and merely logged when it is not.
/// </para>
/// <para>
/// Nothing here blocks on the speculative part. Each checkout is its own task and
/// <see cref="GetRepoRoot"/> waits only on the one it was asked for, so a developer whose config
/// marks ten services <c>"local"</c> while the AppHost adds two waits for those two, not for all
/// ten.
/// </para>
/// <para>
/// The set itself cannot be narrowed to what the AppHost adds — <c>AddService()</c> is called one
/// service at a time and must return the real resource before the next call happens, so the
/// prefetch cannot see the calls that have not happened yet. But it does not need that set. It
/// needs the set of services whose clone <em>something would block on</em>, and two kinds of
/// service are excluded from that without knowing anything about demand (#76):
/// </para>
/// <list type="bullet">
/// <item>
/// A checkout there is nothing to clone for. A working tree already on disk, or a
/// <c>local.path</c> override naming the developer's own directory: speculating over one costs a
/// <c>Directory.Exists</c>, buys no parallelism, and — for a stale override — invents a failure
/// about a repository nobody was going to download. <see cref="GetRepoRoot"/> resolves these
/// directly, which is the same work in a different thread.
/// </item>
/// <item>
/// A service that <em>would be deferred if it were added</em>. Whether a service is deferrable is a
/// pure function of configuration (see <see cref="DeferredCheckout.ShouldDefer"/> and
/// <see cref="ILocalResourceKind.SupportsDeferredCheckout"/>), so it is answerable here, for a
/// service nobody has mentioned. A deferred registration blocks composition on nothing, so its
/// clone no longer has to be started ahead of demand to overlap with the others — it starts at its
/// own <c>AddService()</c> call, via <see cref="StartCheckout"/>, and overlaps just the same.
/// </item>
/// </list>
/// <para>
/// What is left in the set is the case that genuinely needs speculating over: a cold clone that
/// <c>AddService()</c> will block on. That still costs a repository the AppHost may never add, for
/// as long as deferral is opt-in and refused in publish mode.
/// </para>
/// <para>
/// Free of waiting is not free of cost, and the difference matters. Resolving a checkout is not a
/// read-only operation, so speculation does only the half of it that cannot destroy anything: a
/// service with no checkout yet is <b>cloned</b>, and a checkout that already exists is left exactly
/// as it was found. Moving one onto its configured ref — a fetch and a checkout inside a working
/// tree this run did not create — is deferred to <see cref="GetRepoRoot"/>, which runs only for the
/// services the AppHost really added. Without that split, a developer with committed work on a
/// branch of a service this AppHost never adds would find it checked out back onto the configured
/// ref by a run that never mentioned that service, on the strength of a config entry alone. The
/// price is that ref reconciliation is serial across services where it used to be parallel; the
/// clone, which is the part that actually costs time, stays parallel.
/// </para>
/// <para>
/// What remains speculative is a cold clone of a repository that goes unused, and any failure of
/// one. Both are reported at <c>BeforeStartEvent</c> by <see cref="ReportSpeculativeWork"/> rather
/// than paid silently: the cost, because the remedy is for the developer to drop the entries they
/// never <c>AddService()</c>, and the failures, because nothing else ever mentions them —
/// <see cref="GetRepoRoot"/> re-throws only for a service that was actually asked for.
/// </para>
/// </remarks>
internal sealed class LocalCheckoutPrefetch
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalCheckoutPrefetch> Cache = new();

    private readonly Dictionary<string, Task<CheckoutResult>> _checkouts = new(StringComparer.Ordinal);

    /// <summary>
    /// Services <see cref="GetRepoRoot"/> was actually asked for — the set the prefetch could not
    /// know up front, learned by the time <c>BeforeStartEvent</c> runs.
    /// </summary>
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    private bool _started;

    public static LocalCheckoutPrefetch For(
        IDistributedApplicationBuilder builder, IGitClient gitClient)
    {
        // The factory has to stay free of side effects. ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so starting the clones
        // in there would let a discarded instance race the surviving one into the same checkout
        // directories. Starting them behind a lock on the instance that actually won is the shape
        // UrlSource's CheckRegistration uses for the same reason.
        var prefetch = Cache.GetValue(builder, _ => new LocalCheckoutPrefetch());

        prefetch.EnsureStarted(builder, gitClient);

        return prefetch;
    }

    /// <summary>
    /// Starts the prefetch once per builder. Returns only after every speculative checkout task has
    /// been created, so the speculative part of <see cref="_checkouts"/> is complete before any
    /// caller can reach <see cref="GetRepoRoot"/>.
    /// </summary>
    /// <remarks>
    /// It is <b>not</b> complete thereafter, which it used to be: <see cref="StartCheckout"/> adds
    /// an entry each time a deferred service is registered, interleaved with the
    /// <c>AddService()</c> calls that read the dictionary in <see cref="GetRepoRoot"/>. That is why
    /// both sides take <see cref="_gate"/> — the lock became load-bearing when the prefetch stopped
    /// being the only writer (#76), and dropping it on the strength of "populated once, then read"
    /// would now be a data race.
    /// </remarks>
    private void EnsureStarted(IDistributedApplicationBuilder builder, IGitClient gitClient)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;

            // Buffered to BeforeStartEvent for the same reason ServiceConfigurationWarnings is:
            // AddService() runs while the AppHost is still being composed, before there is an
            // ILogger to write to. It is also the first point at which the notice can be correct —
            // only once every AddService() call has happened is the unused set known.
            builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                ReportSpeculativeWork(@event.Services);
                return Task.CompletedTask;
            });

            Run(builder, gitClient);
        }
    }

    /// <summary>
    /// The notice for checkouts that were prefetched but never asked for, or <see langword="null"/>
    /// when the AppHost used every service configured as <c>"local"</c>.
    /// Exposed for tests — the log itself isn't observable in-process.
    /// </summary>
    public string? UnusedCheckoutsMessage
    {
        get
        {
            var unused = UnusedCheckouts().Select(entry => entry.Name).ToArray();

            if (unused.Length == 0)
            {
                return null;
            }

            // Named by configuration key rather than by file: the entry can equally have arrived
            // from appsettings, user secrets, an environment variable or the command line, and
            // sending a developer to a file that holds nothing — or doesn't exist — leaves them
            // nothing to act on.
            return $"{unused.Length} {(unused.Length == 1 ? "service is" : "services are")} configured as " +
                   $"'local' with no checkout yet that this AppHost never adds ({string.Join(", ", unused)}). " +
                   "Cloning them was paid for anyway: AddService() has to hand back the real resource, so a " +
                   "'local' entry whose first checkout the AppHost would have to wait for is cloned in parallel " +
                   "with the others, before the AppHost says which ones it wants. Only the services this AppHost " +
                   "adds are reconciled to their configured ref. Clear " +
                   $"'{DeveloperConfiguration.ServicesKey}:<service>:source' for the ones you don't call " +
                   $"AddService() for — usually their entries in {DeveloperConfiguration.FileName} — to stop " +
                   "paying for them. builder.UseDeferredCheckout() also stops it: a service whose first checkout " +
                   "is deferred past startup is cloned only when it is added.";
        }
    }

    /// <summary>
    /// Notices for speculative checkouts that failed for services this AppHost never added — the
    /// failures <see cref="GetRepoRoot"/> will never re-throw, because nothing asked for them. Only
    /// checkouts that have already finished are included: waiting on one would undo the deferral
    /// this class exists for. Exposed for tests — the log itself isn't observable in-process.
    /// </summary>
    public IReadOnlyList<string> FailedUnusedCheckoutMessages =>
        UnusedCheckouts()
            .Where(entry => entry.Checkout.IsCompleted && entry.Checkout.Result.Exception is not null)
            .Select(entry => FailedCheckoutMessage(entry.Name, entry.Checkout.Result.Exception!))
            .ToArray();

    /// <summary>
    /// The checkouts the prefetch started that no <c>AddService()</c> call ever asked for. Correct
    /// only once every <c>AddService()</c> call has happened, which is why the report waits for
    /// <c>BeforeStartEvent</c>.
    /// </summary>
    private (string Name, Task<CheckoutResult> Checkout)[] UnusedCheckouts()
    {
        lock (_gate)
        {
            return _checkouts
                .Where(entry => !_requested.Contains(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Value))
                .ToArray();
        }
    }

    /// <summary>
    /// Reports the speculative part of the prefetch: what it cost, and what failed inside it while
    /// nobody was waiting. A first run that quietly clones repositories the AppHost never uses
    /// should read as a cost rather than as a hang, and a clone that failed for one of those
    /// services has no other route to the developer at all.
    /// </summary>
    private void ReportSpeculativeWork(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources");

        if (logger is null)
        {
            return;
        }

        if (UnusedCheckoutsMessage is { } message)
        {
            logger.LogInformation("{ServiceSourcesNotice}", message);
        }

        foreach (var (name, checkout) in UnusedCheckouts())
        {
            // A checkout nothing waits on may still be running here, and startup must not block on
            // one — so the failure is reported when it lands instead. ExecuteSynchronously runs the
            // continuation inline for those already finished, which is the common case by now.
            _ = checkout.ContinueWith(
                task => ReportFailedCheckout(logger, name, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static void ReportFailedCheckout(ILogger logger, string serviceName, Task<CheckoutResult> checkout)
    {
        // Never faulted — the worker captures its exception into the result — so this cannot throw.
        if (checkout.Result.Exception is not { } exception)
        {
            return;
        }

        try
        {
            logger.LogWarning(exception, "{ServiceSourcesNotice}", FailedCheckoutMessage(serviceName, exception));
        }
        catch (ObjectDisposedException)
        {
            // A continuation for a still-running clone can land after the host has torn its logging
            // down. There is nothing left to report to, and faulting a continuation nobody awaits
            // would be worse than losing the notice.
        }
    }

    private static string FailedCheckoutMessage(string serviceName, Exception exception) =>
        $"'{serviceName}' is configured as 'local', so its git checkout was prefetched, and the prefetch " +
        $"failed: {exception.Message} This AppHost never adds '{serviceName}', so nothing else reports it — " +
        $"clear '{DeveloperConfiguration.ServicesKey}:{serviceName}:source' if you don't use it, usually the " +
        $"service's entry in {DeveloperConfiguration.FileName}, or fix what the failure names.";

    /// <summary>
    /// Starts <paramref name="serviceName"/>'s checkout now, and records that the AppHost really
    /// does add it — without waiting for either. For a service the prefetch deliberately left out of
    /// its speculative set because this registration was going to claim it: a deferred one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what keeps the clones overlapping after #76 narrowed the speculative set. A deferred
    /// registration returns without blocking on anything, so the next <c>AddService()</c> call
    /// starts its own clone while this one is still running — the same wall-clock the prefetch used
    /// to buy by starting them all at once, for exactly the services the AppHost asked for.
    /// </para>
    /// <para>
    /// Marking the service requested matters on its own: the report at <c>BeforeStartEvent</c>
    /// decides what to name before a deferred service has waited on its checkout, so without this
    /// one would be named as speculative work nobody wanted.
    /// </para>
    /// </remarks>
    public void StartCheckout(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config,
        string appHostDirectory, IGitClient gitClient)
    {
        lock (_gate)
        {
            _requested.Add(serviceName);

            // One checkout per service, whoever asked for it first. The filter in Run means this is
            // normally a fresh entry, but the two decisions are made separately and a service in
            // both sets must still be cloned once.
            if (!_checkouts.ContainsKey(serviceName))
            {
                _checkouts[serviceName] = StartCheckoutTask(
                    serviceName, metadata, config, appHostDirectory, gitClient);
            }
        }
    }

    /// <summary>
    /// The checkout directory for <paramref name="serviceName"/>, re-throwing the failure the
    /// parallel phase recorded for it.
    /// </summary>
    public string GetRepoRoot(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config,
        string appHostDirectory, IGitClient gitClient)
    {
        Task<CheckoutResult>? checkout;

        lock (_gate)
        {
            _requested.Add(serviceName);

            // Locked because _checkouts is no longer written only during Run: StartCheckout adds to
            // it as deferred services are registered, which is interleaved with the AddService calls
            // that read it here.
            _checkouts.TryGetValue(serviceName, out checkout);
        }

        if (checkout is null)
        {
            // Not in the prefetch set. Either nothing needed prefetching for it — a warm checkout, a
            // 'local.path' override — or the developer config was loaded before this service was
            // added to it, or it is being resolved through a path the prefetch doesn't enumerate.
            // Resolve it directly rather than failing.
            return LocalGitCheckout.ResolveRepoRoot(serviceName, metadata, config, appHostDirectory, gitClient);
        }

        // Waits on this service's checkout only. The other prefetched checkouts keep running in the
        // background; a service this AppHost never asks for is never waited on.
        var result = checkout.GetAwaiter().GetResult();

        if (result.Exception is not null)
        {
            // Capture/Throw rather than `throw result.Exception`, which would overwrite the stack
            // trace from the worker the clone actually failed on with this call site — and would
            // mangle the same instance again if two services share a checkout failure.
            ExceptionDispatchInfo.Capture(result.Exception).Throw();
        }

        // The prefetch stopped at "the checkout exists". Reconciling it onto the configured ref
        // mutates a working tree, so it happens here — on the thread of the AddService call that
        // asked for this service, and never for a service the AppHost turns out not to add.
        return LocalGitCheckout.ReconcileRepoRoot(
            serviceName, metadata, config, result.Checkout!.Value, gitClient);
    }

    private void Run(IDistributedApplicationBuilder builder, IGitClient gitClient)
    {
        ServiceSourcesConfigCache.LoadedConfig config;
        try
        {
            config = ServiceSourcesConfigCache.LoadedFor(builder);
        }
        catch (ServiceSourcesConfigurationException)
        {
            // Speculation must never be the thing that fails the AddService call. A config problem
            // real enough to matter is reported by ResolveService, against the service the AppHost
            // actually asked for; here it just means there is nothing to prefetch.
            return;
        }

        var appHostDirectory = builder.AppHostDirectory;

        var deferred = DeferredCheckout.For(builder);
        var kinds = LocalKindRegistry.For(builder);

        var candidates = config.DeveloperConfig.Services
            // Case-insensitive to agree with how AddService resolves the same value: a service whose
            // source is spelled "Local" is one AddService resolves locally, so dropping it here
            // would leave its clone to run alone on the AddService thread rather than with the
            // others — no error, just a slower first start. The same reasoning as the re-keying in
            // DeveloperConfiguration.CanonicalizeToCatalog, applied to the value instead of the key.
            .Where(entry => string.Equals(entry.Value.Source, "local", StringComparison.OrdinalIgnoreCase))
            // A service the developer marked "local" but that the catalog doesn't describe can't be
            // checked out and isn't this phase's problem to report — AddService still rejects it
            // properly if the AppHost actually asks for it.
            .Where(entry => config.Catalog.Services.ContainsKey(entry.Key))
            .Select(entry => (Name: entry.Key, Metadata: config.Catalog.Services[entry.Key], Config: entry.Value))
            // Only checkouts there is something to clone for. Everything else resolves to the same
            // answer in GetRepoRoot for a fraction of the code, and reaches nobody at all when the
            // service is never added — which is where speculating over one used to go wrong.
            .Where(candidate => IsColdManagedCheckout(candidate.Name, candidate.Config, appHostDirectory))
            // ...minus the ones a deferred registration would clone for itself.
            .Where(candidate => !WouldBeDeferredIfAdded(
                builder, deferred, kinds, candidate.Name, candidate.Metadata, candidate.Config))
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            _checkouts[candidate.Name] = StartCheckoutTask(
                candidate.Name, candidate.Metadata, candidate.Config, appHostDirectory, gitClient);
        }
    }

    /// <summary>
    /// Whether there is anything for a clone to do here: a package-managed checkout directory with
    /// nothing in it yet. A <c>local.path</c> override is the developer's own directory and is never
    /// cloned into, and a working tree already on disk is one
    /// <see cref="LocalGitCheckout.PrepareRepoRoot"/> deliberately leaves alone.
    /// </summary>
    /// <remarks>
    /// Pure configuration plus one <c>Directory.Exists</c> — the same question
    /// <see cref="DeferredCheckout.ShouldDefer"/> asks, for the same reason: it has to be answerable
    /// about a service nobody has added.
    /// </remarks>
    private static bool IsColdManagedCheckout(
        string serviceName, ServiceDeveloperConfig config, string appHostDirectory) =>
        config.Local.Path is null
        && !Directory.Exists(LocalGitCheckout.ManagedRepoRoot(appHostDirectory, serviceName));

    /// <summary>
    /// Whether this service would be registered deferred if the AppHost added it — in which case its
    /// clone is <see cref="StartCheckout"/>'s to start, at that call, and starting it speculatively
    /// here would only download a repository for a service that may never be mentioned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mirrors the decision <c>LocalProjectSource</c> makes per service, and has to: a service
    /// dropped here that then takes the eager path would clone alone on the <c>AddService()</c>
    /// thread instead of alongside the others. Every input is available without demand —
    /// <c>UseDeferredCheckout()</c> and <c>AddLocalKind</c> both run before the first
    /// <c>AddService()</c>, the execution mode is fixed, and the kind is asked the deliberately
    /// speculative form of the question.
    /// </para>
    /// <para>
    /// The mirror is exact but for one case, which cannot be mirrored: a kind whose
    /// <see cref="ILocalResourceKind.ResolveDeferred"/> returns <see langword="null"/> after its
    /// <see cref="ILocalResourceKind.SupportsDeferredCheckout"/> answered <see langword="true"/>.
    /// That is honoured (<c>DeferredCheckout.RegisterKind</c> returns null and the eager path takes
    /// over), and the service then clones alone rather than with the others.
    /// </para>
    /// <para>
    /// It is a permitted choice rather than a defect — the interface documents deciding late for a
    /// kind that can only tell once it has looked at everything — and it is the divergence the probe
    /// exists to make cheap to avoid and cannot make impossible: the deciding call is the one with
    /// side effects, so it can never be the one asked here. Both built-in satellites answer both
    /// questions from the same predicate and so never diverge. The cost is a serial clone, not a
    /// wrong one, and it is disclosed on
    /// <see cref="ILocalResourceKind.ResolveDeferred"/> where a handler author reads it.
    /// </para>
    /// </remarks>
    private static bool WouldBeDeferredIfAdded(
        IDistributedApplicationBuilder builder,
        DeferredCheckout deferred,
        LocalKindRegistry kinds,
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config)
    {
        if (!deferred.ShouldDefer(builder, serviceName, config))
        {
            return false;
        }

        if (string.Equals(metadata.Kind, LocalKinds.Dotnet, StringComparison.Ordinal))
        {
            return true;
        }

        if (!kinds.TryGet(metadata.Kind, out var handler) || handler is null)
        {
            // A kind nothing registered. AddService rejects this service before it ever reaches a
            // checkout, so the answer only matters for how much is cloned in the meantime, and the
            // conservative one — keep it in the set — is what happened before.
            return false;
        }

        try
        {
            return handler.SupportsDeferredCheckout(metadata.KindConfig);
        }
        catch (Exception)
        {
            // Documented as answering rather than throwing, and asked here about a service the
            // AppHost may never mention — so a handler that breaks the contract must not take an
            // unrelated AddService() call down with it. LocalProjectSource puts the same question
            // for a service that really is added, and reports the breach there by name.
            return false;
        }
    }

    /// <summary>
    /// The speculative half of a checkout, on its own thread: clone what is missing, and stop at
    /// anything already there. Never throws — the failure is carried in the result — because this
    /// task may be one nobody ever awaits.
    /// </summary>
    private static Task<CheckoutResult> StartCheckoutTask(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient) =>
        Task.Run(() =>
        {
            try
            {
                // Prepare, not resolve: cloning what is missing is safe to do for a service that
                // may never be added, but reconciling an existing checkout is not — see
                // GetRepoRoot, which finishes the job for the services that are.
                var prepared = LocalGitCheckout.PrepareRepoRoot(
                    serviceName, metadata, config, appHostDirectory, gitClient);
                return new CheckoutResult(prepared, null);
            }
            catch (Exception ex)
            {
                // Captured, never thrown from the task itself: this service may never be
                // requested, and a faulted task nobody awaits is an unobserved exception. If it
                // is never requested, ReportSpeculativeWork is what surfaces this.
                return new CheckoutResult(null, ex);
            }
        });

    private sealed record CheckoutResult(LocalGitCheckout.PreparedCheckout? Checkout, Exception? Exception);
}
