using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed record PendingResolution(
    string ServiceName,
    ServiceMetadata Metadata,
    ServiceDeveloperConfig Config,
    IResourceBuilder<IResourceWithServiceDiscovery> Facade,
    IGitClient GitClient);

internal sealed class PendingLocalResolutions
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, PendingLocalResolutions> Cache = new();

    private readonly List<PendingResolution> _pending = [];
    private bool _resolutionStarted;

    public static PendingLocalResolutions For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static b =>
        {
            var store = new PendingLocalResolutions();
            b.Eventing.Subscribe<BeforeStartEvent>((_, ct) => store.ResolveAllAsync(b, ct));
            return store;
        });

    public void Add(PendingResolution pending)
    {
        if (_resolutionStarted)
        {
            throw new ServiceSourcesConfigurationException(
                $"Cannot register 'local'-sourced service '{pending.ServiceName}' because BeforeStartEvent has already " +
                "fired and pending 'local' services have already been resolved. All AddService calls for 'local' sources " +
                "must happen before the app host starts.");
        }

        _pending.Add(pending);
    }

    private async Task ResolveAllAsync(IDistributedApplicationBuilder builder, CancellationToken cancellationToken)
    {
        _resolutionStarted = true;

        var registry = LocalKindRegistry.For(builder);

        // Cheap, synchronous pre-flight: catch unregistered kinds before paying for anyone's git
        // clone. This depends only on the registry and each service's already-loaded metadata, so
        // it doesn't need to wait for (or trigger) any I/O.
        var unregisteredKindFailures = _pending
            .Where(p => !IsDotnet(p.Metadata.Kind) && !registry.TryGet(p.Metadata.Kind, out _))
            .Select(p => (Exception)new ServiceSourcesConfigurationException(
                $"Service '{p.ServiceName}': kind '{p.Metadata.Kind}' is not registered. " +
                registry.DescribeNearMatch(p.Metadata.Kind) +
                "Add the satellite package for this kind and call its registration method " +
                "(e.g. builder.UseJavaScript()) before this service is resolved."))
            .ToArray();
        if (unregisteredKindFailures.Length > 0)
        {
            throw AggregateFailures(unregisteredKindFailures);
        }

        var results = await Task.WhenAll(_pending.Select(pending =>
            Task.Run(() => ResolveOne(pending, builder.AppHostDirectory, registry), cancellationToken)));

        var resolutionFailures = results.Where(r => r.Exception is not null).Select(r => r.Exception!).ToArray();
        if (resolutionFailures.Length > 0)
        {
            throw AggregateFailures(resolutionFailures);
        }

        // Every check above has already passed for every service — including the registry lookup,
        // whose result each ResolutionResult carries — so this loop only ever creates resources: it
        // never throws, and so never leaves the app model partially populated.
        foreach (var result in results)
        {
            var pending = result.Pending;
            if (result.Handler is null)
            {
                // No handler means the built-in dotnet kind, for which ResolveOne always set ProjectPath.
                var projectBuilder = builder.AddProject(pending.ServiceName, result.ProjectPath!);
                ServiceResource.CopyEndpointAnnotations(pending.Facade, projectBuilder);
                continue;
            }

            var resourceBuilder = result.Handler.Resolve(
                builder, pending.ServiceName, result.RepoRoot!, pending.Metadata.KindConfig);
            ServiceResource.CopyEndpointAnnotations(pending.Facade, resourceBuilder);
        }
    }

    private static bool IsDotnet(string kind) => string.Equals(kind, LocalKinds.Dotnet, StringComparison.Ordinal);

    private static ResolutionResult ResolveOne(
        PendingResolution pending, string appHostDirectory, LocalKindRegistry registry)
    {
        try
        {
            var repoRoot = LocalGitCheckout.ResolveRepoRoot(
                pending.ServiceName, pending.Metadata, pending.Config, appHostDirectory, pending.GitClient);

            if (IsDotnet(pending.Metadata.Kind))
            {
                var projectPath = LocalProjectSource.ResolveProjectFile(pending.ServiceName, repoRoot, pending.Metadata.Project);
                return new ResolutionResult(pending, repoRoot, projectPath, null, null);
            }

            // The pre-flight above already proved this lookup succeeds; carrying the handler through
            // to the creation loop keeps that loop free of any check that could fail mid-way.
            registry.TryGet(pending.Metadata.Kind, out var handler);
            return new ResolutionResult(pending, repoRoot, null, handler, null);
        }
        catch (Exception ex)
        {
            return new ResolutionResult(pending, null, null, null, ex);
        }
    }

    private static ServiceSourcesConfigurationException AggregateFailures(IReadOnlyCollection<Exception> failures)
    {
        var lines = failures.Select(ex => ex.InnerException is not null
            ? $"  - {ex.Message} ({ex.InnerException.Message})"
            : $"  - {ex.Message}");
        var message = "Failed to resolve one or more 'local'-sourced services:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines);
        return new ServiceSourcesConfigurationException(message, failures.First());
    }

    /// <summary>
    /// One service's parallel-phase outcome. On success exactly one of <paramref name="ProjectPath"/>
    /// (built-in dotnet kind) and <paramref name="Handler"/> (any registered kind) is set, and that
    /// pair is what the sequential creation loop dispatches on.
    /// </summary>
    private readonly record struct ResolutionResult(
        PendingResolution Pending,
        string? RepoRoot,
        string? ProjectPath,
        ILocalResourceKind? Handler,
        Exception? Exception);
}
