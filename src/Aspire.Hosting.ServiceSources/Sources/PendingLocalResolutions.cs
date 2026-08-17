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
            var projectBuilder = builder.AddProject(result.Pending.ServiceName, result.ProjectPath!);
            ServiceResource.CopyEndpointAnnotations(result.Pending.Facade, projectBuilder);
        }
    }

    private static ResolutionResult ResolveOne(PendingResolution pending, string appHostDirectory)
    {
        try
        {
            var projectPath = LocalProjectSource.ResolveProjectPath(
                pending.ServiceName, pending.Metadata, pending.Config, appHostDirectory, pending.GitClient);
            return new ResolutionResult(pending, projectPath, null);
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

    private readonly record struct ResolutionResult(PendingResolution Pending, string? ProjectPath, Exception? Exception);
}
