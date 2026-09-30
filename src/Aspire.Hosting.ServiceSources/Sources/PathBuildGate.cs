using System.Runtime.CompilerServices;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Serializes the build of <c>"path"</c> services that share a <see cref="BuildGroupKey"/>, so two
/// projects with a common <c>ProjectReference</c> do not race on its <c>bin/</c> and <c>obj/</c>.
/// </summary>
/// <remarks>
/// Only the build is held: the semaphore is released before the resource's process runs, since a gate
/// held across a run would deadlock two services that <c>WaitFor</c> each other. One semaphore per key,
/// created on first use; the set of keys is bounded by the catalog.
/// </remarks>
internal sealed class PathBuildGate
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, PathBuildGate> Cache = new();

    private readonly Dictionary<string, HashSet<string>> _members = new(BuildGroupKey.Comparer);

    private readonly Dictionary<string, SemaphoreSlim> _semaphores = new(BuildGroupKey.Comparer);

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    public static PathBuildGate For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new PathBuildGate());

    public void Register(string resourceName, string key)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(key, out var members))
            {
                _members[key] = members = new HashSet<string>(StringComparer.Ordinal);
            }

            members.Add(resourceName);
        }
    }

    public int MemberCount(string key)
    {
        lock (_gate)
        {
            return _members.TryGetValue(key, out var members) ? members.Count : 0;
        }
    }

    /// <summary>
    /// Builds <paramref name="projectFile"/> once no other member of <paramref name="key"/> is building.
    /// Never fails the start: only cancellation escapes, since the resource's own <c>dotnet run</c> will
    /// rebuild and report a real failure through the usual path.
    /// </summary>
    public async Task RunGatedBuildAsync(
        IBuildRunner runner, string resourceName, string key, string projectFile, string? configuration,
        ILogger logger, CancellationToken cancellationToken)
    {
        // Read once: a member registered after this point is not waited for, it will gate itself.
        if (MemberCount(key) <= 1)
        {
            return;
        }

        var semaphore = SemaphoreFor(key);

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ServiceSourcesLog.Information(
                logger, $"Building service '{new Name(resourceName)}' before it starts, one build at a time with the other services in its build group.");

            var exitCode = await runner.RunAsync(
                projectFile, configuration,
                line => ServiceSourcesLog.Information(logger, $"{Raw.Escaped(line)}"),
                cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                ServiceSourcesLog.Warning(
                    logger,
                    $"Build of service '{new Name(resourceName)}' exited with code {exitCode}; starting it anyway so its own build reports the failure.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServiceSourcesLog.Warning(
                logger, ex,
                $"Could not build service '{new Name(resourceName)}' before starting it: {Raw.Cause(ex)}. Starting it anyway so its own build reports the failure.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private SemaphoreSlim SemaphoreFor(string key)
    {
        lock (_gate)
        {
            if (!_semaphores.TryGetValue(key, out var semaphore))
            {
                _semaphores[key] = semaphore = new SemaphoreSlim(1, 1);
            }

            return semaphore;
        }
    }
}
