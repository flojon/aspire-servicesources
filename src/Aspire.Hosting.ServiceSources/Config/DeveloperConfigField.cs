using System.Collections;
using System.ComponentModel;
using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// What shape a developer-config field's value has to be written in: a value, a list of values, or a
/// block of settings of its own.
/// </summary>
/// <remarks>
/// Every field in this file was a scalar until <c>local.prepare</c>, so the question had one answer
/// and nobody had to ask it. It now has four, and the order they are asked in is load-bearing:
/// <see cref="string"/><c>[]</c> is a class, so a list asked about as a block is classified as one
/// and reported with precisely the message this type exists to stop producing — "takes a value, not
/// a block of settings", about a field whose value is a list.
/// <para>
/// The fourth, <see cref="IsValueOrMap"/>, has to be asked <em>before</em>
/// <see cref="IsList"/> for the same reason: a map is a <see cref="IEnumerable"/> too, so a
/// <c>port</c> written as a block of named ports would otherwise be walked as a list and answered
/// with a sentence about list elements — about a field that has none.
/// </para>
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
    /// Whether the field takes <em>either</em> a value <em>or</em> a block of named values — the
    /// shape a backing service's <c>kubernetes.port</c> has, where one port is written as a number
    /// and several are written with a name each.
    /// </summary>
    /// <remarks>
    /// Recognized by the two things that make such a field bindable at all, rather than by naming
    /// the type: it binds children as a map, and it carries a <see cref="TypeConverterAttribute"/>
    /// for the value spelling. That pairing is not a convention, it is what the standard binder
    /// requires — value-first conversion for the scalar, the dictionary walk for the block — so a
    /// second such field cannot be added without both halves and cannot be added <em>with</em> them
    /// and go unclassified.
    /// <para>
    /// Asked ahead of <see cref="IsList"/>, which would otherwise claim it: see the remarks on this
    /// type.
    /// </para>
    /// </remarks>
    public static bool IsValueOrMap(Type type) =>
        MapValueTypeOf(type) is not null
        && type.IsDefined(typeof(TypeConverterAttribute), inherit: true);

    /// <summary>
    /// The type each named value in a value-or-map field has to bind to, or <see langword="null"/>
    /// when <paramref name="type"/> is not keyed by name at all.
    /// </summary>
    /// <remarks>
    /// The value type travels out of here because the walk over such a block has to check each entry
    /// itself: the binder <em>drops</em> an entry it cannot convert rather than failing, so the map
    /// binds one shorter than it was written and nothing downstream receives the entry that would
    /// have reported it.
    /// <para>
    /// Only a <see cref="string"/> key counts. Configuration has no other kind — every key arrives as
    /// text — so a dictionary keyed by anything else is not a shape configuration can produce, and
    /// claiming it here would classify a field the binder cannot fill.
    /// </para>
    /// </remarks>
    public static Type? MapValueTypeOf(Type type) =>
        type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            .Select(i => i.GetGenericArguments())
            .Where(arguments => arguments[0] == typeof(string))
            .Select(arguments => arguments[1])
            .FirstOrDefault();

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
        if (!type.IsClass || type == typeof(string) || IsList(type) || IsValueOrMap(type))
        {
            return null;
        }

        return type.GetProperties()
            .Where(IsConfigurable)
            .ToDictionary(field => field.Name, field => field.PropertyType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether configuration could put a value at this property — which is what makes it a key
    /// rather than merely a member.
    /// </summary>
    /// <remarks>
    /// A computed property is not a key, and offering one is worse than cosmetic: the validator
    /// accepts it, and its "valid keys there are …" list — the sentence that exists to tell a
    /// developer what they may write — names something they cannot. <c>PrepareDeveloperConfig</c>
    /// carries the first such member in this file, an <c>IsDeclared</c> the block's own rules are
    /// expressed in terms of.
    /// <para>
    /// A setter is what settles it for a value and for a list. A nested block needs none, because
    /// the binder populates the instance a block property already holds rather than replacing it —
    /// which is how every source block in an entry binds today.
    /// </para>
    /// </remarks>
    private static bool IsConfigurable(PropertyInfo property) =>
        property.CanWrite || BlockFieldsOf(property.PropertyType) is not null;
}
