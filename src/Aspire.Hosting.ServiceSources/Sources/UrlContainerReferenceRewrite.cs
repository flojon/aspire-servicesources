using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Lets a container consume a <c>"url"</c>-sourced service: DCP has no resource to plumb container
/// networking through, so the shape annotations that make it try are removed and the consumer's
/// environment and argument callbacks are wrapped to emit the URL as a literal instead.
/// </summary>
/// <remarks>
/// Runs from the guard's <c>BeforeStartEvent</c> handler, so a handler subscribed earlier, or a later
/// one that adds annotations afterwards, escapes it. Wrapping runs the original callback untouched and
/// only then rewrites values referring to the facade, so other packages' values (and secrets) pass
/// through. A throw inside a callback is a silent <c>FailedToStart</c>, so nothing here throws.
/// <para>
/// A connection string built from the facade's endpoint is replaced by its rewritten expression: DCP
/// resolves the expression for the container network, where the facade has no address.
/// </para>
/// </remarks>
internal static class UrlContainerReferenceRewrite
{
    private const string ReferenceRelationship = "Reference";

    /// <summary>Wrappers this class created, so a repeated pre-flight does not wrap one again.</summary>
    private static readonly ConditionalWeakTable<IResourceAnnotation, object> Wrappers = new();

    private static readonly object Marker = new();

    public static void Apply(ContainerResource consumer, IReadOnlyCollection<ServiceResource> facades, ILogger logger)
    {
        var shape = consumer.Annotations
            .Where(annotation => TargetsAFacade(annotation, facades))
            .ToArray();

        foreach (var annotation in shape)
        {
            consumer.Annotations.Remove(annotation);
        }

        foreach (var original in consumer.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray())
        {
            if (Wrappers.TryGetValue(original, out _))
            {
                continue;
            }

            var wrapper = new EnvironmentCallbackAnnotation(async context =>
            {
                await original.Callback(context).ConfigureAwait(false);
                Rewrite(consumer, facades, logger, context.EnvironmentVariables);
            });

            Replace(consumer, original, wrapper);
        }

        foreach (var original in consumer.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToArray())
        {
            if (Wrappers.TryGetValue(original, out _))
            {
                continue;
            }

            var wrapper = new CommandLineArgsCallbackAnnotation(async context =>
            {
                await original.Callback(context).ConfigureAwait(false);
                Rewrite(consumer, facades, logger, context.Args);
            });

            Replace(consumer, original, wrapper);
        }
    }

    private static void Replace(IResource consumer, IResourceAnnotation original, IResourceAnnotation wrapper)
    {
        var index = consumer.Annotations.IndexOf(original);
        consumer.Annotations.Remove(original);
        consumer.Annotations.Insert(index, wrapper);
        Wrappers.Add(wrapper, Marker);
    }

    private static bool TargetsAFacade(IResourceAnnotation annotation, IReadOnlyCollection<ServiceResource> facades) =>
        annotation switch
        {
            EndpointReferenceAnnotation reference => facades.Contains(reference.Resource),
            ResourceRelationshipAnnotation relationship
                when string.Equals(relationship.Type, ReferenceRelationship, StringComparison.Ordinal)
                => facades.Contains(relationship.Resource),
            _ => false,
        };

    private static void Rewrite(
        ContainerResource consumer, IReadOnlyCollection<ServiceResource> facades, ILogger logger,
        Dictionary<string, object> environment)
    {
        foreach (var key in environment.Keys.ToArray())
        {
            if (TryRewrite(environment[key], consumer, facades, logger, key, out var rewritten))
            {
                environment[key] = rewritten;
            }
        }
    }

    private static void Rewrite(
        ContainerResource consumer, IReadOnlyCollection<ServiceResource> facades, ILogger logger, IList<object> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (TryRewrite(args[i], consumer, facades, logger, $"argument {i}", out var rewritten))
            {
                args[i] = rewritten;
            }
        }
    }

    private static bool TryRewrite(
        object value, ContainerResource consumer, IReadOnlyCollection<ServiceResource> facades, ILogger logger,
        string key, out object rewritten)
    {
        rewritten = value;

        try
        {
            if (FacadeOf(value, facades) is not { } facade)
            {
                return false;
            }

            try
            {
                rewritten = RewriteValue(value, facades);
                return true;
            }
            catch (CannotRewriteException)
            {
                // The URL is deliberately not in the message: only the key and the service name.
                ServiceSourcesLog.Warning(logger,
                    $"Container '{Raw.Escaped(consumer.Name)}' uses service '{new Name(facade.Name)}', whose source is 'url', " +
                    $"in '{new Name(key)}' in a form this package cannot turn into a literal, so it is left as " +
                    $"Aspire produced it and the container may fail to start.");
                return false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ServiceSourcesLog.Warning(logger, exception,
                $"Container '{Raw.Escaped(consumer.Name)}': rewriting '{new Name(key)}' for a 'url'-sourced service failed, " +
                $"so it is left as Aspire produced it.");
            return false;
        }
    }

    /// <summary>The facade <paramref name="value"/> refers to (searching nested expressions), or null.</summary>
    private static ServiceResource? FacadeOf(object? value, IReadOnlyCollection<ServiceResource> facades) => value switch
    {
        EndpointReference reference => facades.FirstOrDefault(f => ReferenceEquals(f, reference.Resource)),
        EndpointReferenceExpression property => FacadeOf(property.Endpoint, facades),
        ConnectionStringReference connectionString => FacadeOf(connectionString.Resource, facades),
        IResourceWithConnectionString resource => FacadeOf(resource.ConnectionStringExpression, facades),
        ReferenceExpression expression => expression.ValueProviders
                .Select(provider => FacadeOf(provider, facades))
                .FirstOrDefault(facade => facade is not null)
            ?? FacadeOf(expression.Condition, facades)
            ?? (expression.IsConditional
                ? FacadeOf(expression.WhenTrue, facades) ?? FacadeOf(expression.WhenFalse, facades)
                : null),
        _ => null,
    };

    private static object RewriteValue(object value, IReadOnlyCollection<ServiceResource> facades) => value switch
    {
        EndpointReference reference => UrlFacadeLiteral.Literal(EndpointOf(reference, facades)),
        EndpointReferenceExpression property => UrlFacadeLiteral.TryProperty(
            EndpointOf(property.Endpoint, facades), property.Property, out var text)
            ? text
            : throw new CannotRewriteException(),
        ConnectionStringReference connectionString => RewriteValue(connectionString.Resource, facades),
        IResourceWithConnectionString resource => RewriteValue(resource.ConnectionStringExpression, facades),
        ReferenceExpression { IsConditional: false } expression => RewriteExpression(expression, facades),
        _ => throw new CannotRewriteException(),
    };

    private static EndpointAnnotation EndpointOf(EndpointReference reference, IReadOnlyCollection<ServiceResource> facades)
    {
        var facade = facades.FirstOrDefault(f => ReferenceEquals(f, reference.Resource))
            ?? throw new CannotRewriteException();

        return facade.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, reference.EndpointName, StringComparison.OrdinalIgnoreCase))
            ?? throw new CannotRewriteException();
    }

    /// <summary>
    /// A string when no provider is left, otherwise a new expression in which only the facade's providers
    /// became text. Foreign providers stay providers: resolving one here would use the host network's
    /// value for a container and turn a parameter secret into plaintext.
    /// </summary>
    private static object RewriteExpression(ReferenceExpression expression, IReadOnlyCollection<ServiceResource> facades)
    {
        var parts = new List<(string? Text, IValueProvider? Provider, string? Format)>();
        var format = expression.Format;
        var literal = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];

            if ((c == '{' || c == '}') && i + 1 < format.Length && format[i + 1] == c)
            {
                literal.Append(c);
                i++;
            }
            else if (c == '{')
            {
                var end = format.IndexOf('}', i);
                if (end < 0 || !int.TryParse(format.AsSpan(i + 1, end - i - 1), out var index)
                    || index < 0 || index >= expression.ValueProviders.Count)
                {
                    throw new CannotRewriteException();
                }

                Flush(parts, literal);
                var provider = expression.ValueProviders[index];
                var stringFormat = index < expression.StringFormats.Count ? expression.StringFormats[index] : null;

                if (FacadeOf(provider, facades) is null)
                {
                    parts.Add((null, provider, stringFormat));
                }
                else if (!string.IsNullOrEmpty(stringFormat))
                {
                    throw new CannotRewriteException();
                }
                else
                {
                    var rewritten = RewriteValue(provider, facades);
                    parts.Add(rewritten is string text ? (text, null, null) : (null, (IValueProvider)rewritten, null));
                }

                i = end;
            }
            else
            {
                literal.Append(c);
            }
        }

        Flush(parts, literal);

        if (parts.All(part => part.Provider is null))
        {
            return string.Concat(parts.Select(part => part.Text));
        }

        var builder = new ReferenceExpressionBuilder();
        foreach (var (text, provider, stringFormat) in parts)
        {
            if (provider is null)
            {
                builder.AppendLiteral(text!.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
            }
            else
            {
                builder.AppendValueProvider(provider, stringFormat);
            }
        }

        return builder.Build();
    }

    private static void Flush(List<(string? Text, IValueProvider? Provider, string? Format)> parts, StringBuilder literal)
    {
        if (literal.Length > 0)
        {
            parts.Add((literal.ToString(), null, null));
            literal.Clear();
        }
    }

    private sealed class CannotRewriteException : Exception;
}
