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

        var results = await Task.WhenAll(_pending.Select(pending =>
            Task.Run(() => ResolveOne(pending, builder.AppHostDirectory), cancellationToken)));

        var failures = results.Where(r => r.Exception is not null).ToArray();
        if (failures.Length > 0)
        {
            throw AggregateFailures(failures);
        }

        foreach (var result in results)
        {
            var pending = result.Pending;
            if (pending.Metadata.Kind == "dotnet")
            {
                var projectPath = Path.Combine(result.RepoRoot!, pending.Metadata.Project);
                if (!File.Exists(projectPath))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{pending.ServiceName}': project file '{pending.Metadata.Project}' was not found under '{result.RepoRoot}'.");
                }

                var projectBuilder = builder.AddProject(pending.ServiceName, projectPath);
                ServiceResource.CopyEndpointAnnotations(pending.Facade, projectBuilder);
                continue;
            }

            if (!LocalKindRegistry.For(builder).TryGet(pending.Metadata.Kind, out var handler))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{pending.ServiceName}': kind '{pending.Metadata.Kind}' is not registered. " +
                    "Add the satellite package for this kind and call its registration method " +
                    "(e.g. builder.UseJavaScript()) before this service is resolved.");
            }

            var resourceBuilder = handler!.Resolve(builder, pending.ServiceName, result.RepoRoot!, pending.Metadata.KindConfig);
            ServiceResource.CopyEndpointAnnotations(pending.Facade, resourceBuilder);
        }
    }

    private static ResolutionResult ResolveOne(PendingResolution pending, string appHostDirectory)
    {
        try
        {
            var repoRoot = LocalGitCheckout.ResolveRepoRoot(
                pending.ServiceName, pending.Metadata, pending.Config, appHostDirectory, pending.GitClient);
            return new ResolutionResult(pending, repoRoot, null);
        }
        catch (Exception ex)
        {
            return new ResolutionResult(pending, null, ex);
        }
    }

    private static ServiceSourcesConfigurationException AggregateFailures(IReadOnlyCollection<ResolutionResult> failures)
    {
        var lines = failures.Select(f => f.Exception!.InnerException is not null
            ? $"  - {f.Exception.Message} ({f.Exception.InnerException.Message})"
            : $"  - {f.Exception.Message}");
        var message = "Failed to resolve one or more 'local'-sourced services:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines);
        return new ServiceSourcesConfigurationException(message, failures.First().Exception!);
    }

    private readonly record struct ResolutionResult(PendingResolution Pending, string? RepoRoot, Exception? Exception);
}
