using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// Buffers the eager path's prepare-step output during composition, and replays a capped copy of
/// it into each service's own resource log once <c>BeforeStartEvent</c> gives access to one.
/// </summary>
/// <remarks>
/// <para>
/// The eager path's only sink used to be <see cref="ConsolePrepareOutputSink"/> — fine under
/// <c>dotnet run</c>, where the console is the developer's own terminal, but invisible under
/// <c>aspire run</c>, the documented way to start an AppHost: the CLI captures the AppHost's
/// standard output and relays it, live, into its own log under <c>~/.aspire/logs/</c> rather than
/// the dashboard. This wraps that sink rather than replacing it, so <c>dotnet run</c>'s live
/// terminal output is unchanged, and additionally buffers the same lines, keyed by service name,
/// for replay into that service's resource log at <c>BeforeStartEvent</c> — the house pattern
/// <see cref="ServiceSourcesWarnings"/>, <see cref="Sources.LocalCheckoutPrefetch"/> and
/// <see cref="Sources.UrlSource"/> already use for the same reason: composition has no
/// <see cref="ILogger"/> to write to.
/// </para>
/// <para>
/// The dashboard then carries the same lines the console already showed. Duplication is accepted
/// rather than solved: what this buys is presence in the one place a developer would think to
/// look for a running service, not liveness — a step this reports on has already finished its
/// bootstrap by the time anyone reads it there.
/// </para>
/// </remarks>
internal sealed class BufferingPrepareOutputSink
{
    /// <summary>
    /// How many of a step's first output lines are kept.
    /// </summary>
    /// <remarks>
    /// Not optional: the reason the step is running and its full resolved command
    /// (<see cref="CheckoutPreparation"/>'s first <see cref="IPrepareOutputSink.Report"/> call) are
    /// the very first line reported, and they carry the trust value #118 attached to logging the
    /// command loudly before it runs. A cap that kept only a tail would drop exactly that line.
    /// </remarks>
    private const int HeadLines = 20;

    /// <summary>
    /// How many of a step's last output lines are kept, mirroring
    /// <see cref="CheckoutPreparation"/>'s own tail-on-failure budget.
    /// </summary>
    private const int TailLines = 20;

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, BufferingPrepareOutputSink> Cache = new();

    private readonly object _gate = new();

    private readonly Dictionary<string, ServiceBuffer> _buffers = new(StringComparer.Ordinal);

    private bool _subscribed;

    /// <summary>
    /// Wraps <paramref name="inner"/> so every line it would have reported still reaches it
    /// immediately, and is also buffered for <paramref name="serviceName"/>'s resource log.
    /// </summary>
    public static IPrepareOutputSink Wrap(
        IDistributedApplicationBuilder builder, string serviceName, IPrepareOutputSink inner)
    {
        var instance = Cache.GetValue(builder, static _ => new BufferingPrepareOutputSink());

        instance.EnsureSubscribed(builder);

        return new ComposedSink(inner, instance.BufferFor(serviceName));
    }

    private ServiceBuffer BufferFor(string serviceName)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(serviceName, out var buffer))
            {
                buffer = new ServiceBuffer();
                _buffers.Add(serviceName, buffer);
            }

            return buffer;
        }
    }

    private void EnsureSubscribed(IDistributedApplicationBuilder builder)
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;

            builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                Flush(@event);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Writes every buffered service's capped output to its own resource log, matched via the
    /// <see cref="ServiceSourceAnnotation"/> every resolved service's resource carries.
    /// </summary>
    private void Flush(BeforeStartEvent @event)
    {
        Dictionary<string, ServiceBuffer> snapshot;

        lock (_gate)
        {
            snapshot = new Dictionary<string, ServiceBuffer>(_buffers, StringComparer.Ordinal);
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var loggers = @event.Services.GetRequiredService<ResourceLoggerService>();

        foreach (var resource in @event.Model.Resources)
        {
            if (resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault() is not { } annotation
                || !snapshot.TryGetValue(annotation.ServiceName, out var buffer))
            {
                continue;
            }

            var lines = buffer.Snapshot();

            if (lines.Count == 0)
            {
                continue;
            }

            var logger = loggers.GetLogger(resource);

            foreach (var line in lines)
            {
                logger.LogInformation("{PrepareOutput}", line);
            }
        }
    }

    /// <summary>
    /// One service's output, capped to its first <see cref="HeadLines"/> and last
    /// <see cref="TailLines"/> lines.
    /// </summary>
    private sealed class ServiceBuffer
    {
        private readonly object _gate = new();

        private readonly List<string> _head = new(HeadLines);

        private readonly Queue<string> _tail = new(TailLines);

        private int _total;

        public void Add(string line)
        {
            lock (_gate)
            {
                _total++;

                if (_head.Count < HeadLines)
                {
                    _head.Add(line);
                    return;
                }

                _tail.Enqueue(line);

                if (_tail.Count > TailLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        /// <summary>
        /// Every line kept, with an elision marker inserted where lines were dropped — never
        /// present when the head and tail together already cover the whole of what was reported,
        /// so a truncated record can never be read as a complete one or vice versa.
        /// </summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                var tail = _tail.ToArray();
                var elided = _total - _head.Count - tail.Length;

                if (elided <= 0)
                {
                    return [.. _head, .. tail];
                }

                var marker = $"... ({elided} line{(elided == 1 ? "" : "s")} elided) ...";

                return [.. _head, marker, .. tail];
            }
        }
    }

    private sealed class ComposedSink(IPrepareOutputSink inner, ServiceBuffer buffer) : IPrepareOutputSink
    {
        public void Report(string line)
        {
            inner.Report(line);
            buffer.Add(line);
        }
    }
}
