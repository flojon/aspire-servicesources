using System.Collections;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// What shape a developer-config field's value has to be written in: a value, a list of values, or a
/// block of settings of its own.
/// </summary>
/// <remarks>
/// Every field in this file was a scalar until <c>local.prepare</c>, so the question had one answer
/// and nobody had to ask it. It now has three, and the order they are asked in is load-bearing:
/// <see cref="string"/><c>[]</c> is a class, so a list asked about as a block is classified as one
/// and reported with precisely the message this type exists to stop producing — "takes a value, not
/// a block of settings", about a field whose value is a list.
/// </remarks>
internal static class DeveloperConfigField
{
    /// <summary>
    /// Whether the field holds several values. <see cref="IConfiguration"/> renders a JSON array as
    /// indexed children, so such a field arrives looking exactly like a block.
    /// </summary>
    /// <remarks>
    /// <see cref="string"/> is excluded explicitly: it is an <see cref="IEnumerable"/> of
    /// characters, and every string field in this file would otherwise be a list.
    /// </remarks>
    public static bool IsList(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    /// <summary>
    /// The keys valid inside <paramref name="type"/> when the field is a block of settings of its
    /// own, or <see langword="null"/> when it is a value or a list.
    /// </summary>
    /// <remarks>
    /// Keyed the way <see cref="DeveloperConfigShape.BlockFields"/> is — name to the type the value
    /// has to bind to — so a nested block is walked by the same code as a top-level one.
    /// </remarks>
    public static IReadOnlyDictionary<string, Type>? BlockFieldsOf(Type type)
    {
        if (!type.IsClass || type == typeof(string) || IsList(type))
        {
            return null;
        }

        return type.GetProperties()
            .ToDictionary(field => field.Name, field => field.PropertyType, StringComparer.OrdinalIgnoreCase);
    }
}
