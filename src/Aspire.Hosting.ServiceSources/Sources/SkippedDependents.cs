using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// What waits on a service that may be left unstarted, shown in the prompt so the consequence of
/// declining is visible: a <c>WaitFor</c> on a skipped service never resolves.
/// </summary>
internal static class SkippedDependents
{
    private const int MaxNamed = 5;

    /// <summary>
    /// For each service with at least one dependent, the "Waited on by:" line. Computed from the model
    /// as it is when the prompt opens.
    /// </summary>
    public static IReadOnlyDictionary<string, Raw> For(
        IEnumerable<IResource> model, IReadOnlyList<DeferredCheckout.Deferred> services)
    {
        // Keyed by the service name a resource carries, so a facade and its real resource, which
        // share one, are a single node; resources with no service name are keyed by their own name.
        var waitersByTarget = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var resource in model)
        {
            var waiter = KeyOf(resource);

            foreach (var wait in resource.Annotations.OfType<WaitAnnotation>())
            {
                var target = KeyOf(wait.Resource);

                if (!waitersByTarget.TryGetValue(target, out var waiters))
                {
                    waitersByTarget[target] = waiters = new HashSet<string>(StringComparer.Ordinal);
                }

                waiters.Add(waiter);
            }
        }

        var result = new Dictionary<string, Raw>(StringComparer.Ordinal);

        foreach (var service in services)
        {
            var own = new HashSet<string>(service.AllResources.Select(KeyOf), StringComparer.Ordinal)
            {
                service.ServiceName,
            };

            var dependents = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(own);
            var seen = new HashSet<string>(own, StringComparer.Ordinal);

            while (pending.Count > 0)
            {
                if (!waitersByTarget.TryGetValue(pending.Dequeue(), out var waiters))
                {
                    continue;
                }

                foreach (var waiter in waiters)
                {
                    if (seen.Add(waiter))
                    {
                        dependents.Add(waiter);
                        pending.Enqueue(waiter);
                    }
                }
            }

            if (dependents.Count > 0)
            {
                result[service.ServiceName] = Describe(dependents);
            }
        }

        return result;
    }

    private static string KeyOf(IResource resource) =>
        resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault()?.ServiceName ?? resource.Name;

    private static Raw Describe(SortedSet<string> dependents)
    {
        // Escaped rather than Name: Name caps at 64 characters, which would shorten a long name.
        var named = Raw.Join(", ", dependents.Take(MaxNamed).Select(Raw.Escaped));
        var more = dependents.Count - MaxNamed;

        return more > 0
            ? Raw.Compose($"Waited on by: {named} and {more} more")
            : Raw.Compose($"Waited on by: {named}");
    }
}
