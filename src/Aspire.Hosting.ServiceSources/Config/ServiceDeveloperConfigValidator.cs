using Microsoft.Extensions.Configuration;
using System.ComponentModel;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Fails fast on a key that would bind to nothing, so a typo, a field written flat where it belongs
/// to a block, and a value and a block written the wrong way round are all reported instead of being
/// silently dropped. The last of those costs more than itself: the binder answers a key of the wrong
/// shape by abandoning the whole entry, which then reads downstream as a service nobody configured.
/// </summary>
internal static class ServiceDeveloperConfigValidator
{
    /// <summary>
    /// Checks one service's entry. Every block is checked, not only the one <c>source</c> names: a
    /// block for a source this entry is not currently using is legitimate and left alone, but a
    /// typo inside it would otherwise lie in wait until the day the source is switched to it.
    /// </summary>
    public static void Validate(string serviceName, IConfigurationSection entry)
    {
        foreach (var key in entry.GetChildren())
        {
            if (!ServiceDeveloperConfigShape.RootKeys.Contains(key.Key))
            {
                throw NotValidHere(serviceName, key);
            }

            if (!ServiceDeveloperConfigShape.BlockFields.TryGetValue(key.Key, out var fields))
            {
                // `source` is the one root key that takes a value rather than a block. An object
                // written there binds it to the empty string and, because the binder gives up on
                // the whole entry, takes every sibling block down with it.
                if (HasChildren(key))
                {
                    throw ValueExpected(serviceName, key, block: null);
                }

                continue;
            }

            // A block name carrying a value rather than an object is the old flat shape written
            // with a name this type happens to also use for a block — `"url": "https://…"` against
            // the `url` block. It binds to nothing, and passing the check above on the strength of
            // the name alone would let the one field most likely to be written flat through in
            // silence.
            if (key.Value is not null)
            {
                throw BlockExpected(serviceName, key, fields);
            }

            foreach (var field in key.GetChildren())
            {
                if (!fields.TryGetValue(field.Key, out var fieldType))
                {
                    throw NotValidInBlock(serviceName, field, key.Key, fields);
                }

                // The mirror of the check above: a block written where a field's value goes. Like
                // a non-scalar `source`, it binds to nothing and costs the entry every other key
                // it carries, so the service reads downstream as one nobody configured at all.
                if (HasChildren(field))
                {
                    throw ValueExpected(serviceName, field, key.Key.ToLowerInvariant());
                }

                if (field.Value is { } value && !BindsTo(fieldType, value))
                {
                    throw NotBindable(serviceName, field, key.Key.ToLowerInvariant(), fieldType, value);
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="section"/> holds an object or an array rather than a value.
    /// </summary>
    private static bool HasChildren(IConfigurationSection section) => section.GetChildren().Any();

    /// <summary>
    /// Whether <paramref name="value"/> would survive the binder on its way into a field of
    /// <paramref name="type"/>.
    /// </summary>
    /// <remarks>
    /// An empty value is bindable for a nullable field and means the field is unset: a higher
    /// configuration layer can set a key but has no way to remove one, so blanking it is the only
    /// gesture there is for dropping a value the layer below supplied. Whitespace is not the same
    /// thing to the binder, which is why it fails the check here and gets told so.
    /// </remarks>
    private static bool BindsTo(Type type, string value)
    {
        var underlying = Nullable.GetUnderlyingType(type);

        if ((underlying ?? type) == typeof(string))
        {
            return true;
        }

        // Empty is the absent value, which only a field that can hold null has room for.
        return value.Length == 0
            ? underlying is not null
            : TypeDescriptor.GetConverter(underlying ?? type).IsValid(value);
    }

    /// <remarks>
    /// The suggestion names the block and nothing else. Telling a developer which <c>source</c> to
    /// set would be advice to change what the service resolves to — a stray <c>port</c> on a
    /// container-sourced entry belongs in the <c>kubernetes</c> block, but that is emphatically not
    /// a reason to make the service kubernetes-sourced.
    /// </remarks>
    private static ServiceSourcesConfigurationException NotValidHere(
        string serviceName, IConfigurationSection key)
    {
        var home = ServiceDeveloperConfigShape.HomeBlockOf(key.Key)?.ToLowerInvariant();

        return new ServiceSourcesConfigurationException(
            (home is not null
                ? $"Service '{serviceName}': '{key.Key}' is not a valid key here. It belongs in the "
                  + $"'{home}' block: \"{serviceName}\": {{ ..., \"{home}\": {{ \"{key.Key}\": ... }} }}."
                : $"Service '{serviceName}': '{key.Key}' is not a valid key. Valid keys are "
                  + $"{Quoted(ServiceDeveloperConfigShape.RootKeys)}.")
            + SetAt(key));
    }

    /// <summary>The error for a key that no block of this name declares.</summary>
    private static ServiceSourcesConfigurationException NotValidInBlock(
        string serviceName, IConfigurationSection field, string block, IReadOnlyDictionary<string, Type> fields) =>
        new($"Service '{serviceName}': '{field.Key}' is not a valid key in the "
            + $"'{block.ToLowerInvariant()}' block. Valid keys there are {Quoted(fields.Keys)}."
            + SetAt(field));

    /// <summary>
    /// The error for a block name carrying a value: the key is a valid one, and what is wrong is
    /// the value written where a block of settings goes.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NotValidHere"/> because three of the four block names — everything
    /// but <c>url</c> — are block names and nothing else, so that message's fallback branch would
    /// call the key invalid and then list it among the valid ones.
    /// </remarks>
    private static ServiceSourcesConfigurationException BlockExpected(
        string serviceName, IConfigurationSection key, IReadOnlyDictionary<string, Type> fields)
    {
        var block = key.Key.ToLowerInvariant();

        return new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': '{key.Key}' takes a block of settings, not a value: "
            + $"\"{serviceName}\": {{ ..., \"{block}\": {{ ... }} }}. "
            + $"Valid keys there are {Quoted(fields.Keys)}."
            + SetAt(key));
    }

    /// <summary>
    /// The error for a key carrying a block where a value goes — a non-scalar <c>source</c>, or a
    /// field inside a block written as an object.
    /// </summary>
    /// <param name="block">
    /// The block <paramref name="key"/> sits in, or <see langword="null"/> when it sits directly on
    /// the service entry.
    /// </param>
    private static ServiceSourcesConfigurationException ValueExpected(
        string serviceName, IConfigurationSection key, string? block) =>
        new($"Service '{serviceName}': '{key.Key}'{(block is null ? "" : $" in the '{block}' block")} "
            + $"takes a value, not a block of settings: \"{block ?? serviceName}\": {{ \"{key.Key}\": ... }}."
            + SetAt(key));

    /// <summary>
    /// The error for a value the binder could not turn into the field's type — a key that is valid
    /// everywhere except in what it was set to.
    /// </summary>
    /// <remarks>
    /// Worth its own check rather than being left to the binder, which reports it as a failure to
    /// convert a value at a key path into a CLR type, from a plain
    /// <see cref="InvalidOperationException"/> that no handler upstream treats as a configuration
    /// problem. The empty spelling is named for a whitespace value because that is someone reaching
    /// for the gesture that unsets a field and missing by one character.
    /// </remarks>
    private static ServiceSourcesConfigurationException NotBindable(
        string serviceName, IConfigurationSection field, string block, Type type, string value) =>
        new($"Service '{serviceName}': '{field.Key}' in the '{block}' block takes a {Described(type)}, "
            + $"but is set to '{value}'."
            + (string.IsNullOrWhiteSpace(value) ? " Set it to an empty value to leave the field unset." : "")
            + SetAt(field));

    private static string Described(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        return target == typeof(int) || target == typeof(long) ? "whole number"
            : target == typeof(bool) ? "true or false"
            : target.Name.ToLowerInvariant();
    }

    /// <summary>
    /// Where the offending key came from, as a configuration key path rather than a file.
    /// </summary>
    /// <remarks>
    /// The file is only the lowest of the layers this is read from, and validation deliberately
    /// covers entries no <c>AddService()</c> call names, so the key a message is about may have
    /// been contributed by appsettings, user secrets, an environment variable or the command line —
    /// a CI machine carrying a stale <c>ServiceSources__Services__orders__Path</c> is the case that
    /// costs the most to track down. Naming the key, and its environment spelling, is what makes
    /// the layer that set it findable; naming a file would send that developer to edit the one
    /// place the value is not.
    /// </remarks>
    private static string SetAt(IConfigurationSection section) =>
        $" The key is '{section.Path}', which any configuration layer can set: "
        + $"{DeveloperConfiguration.FileName}, appsettings, user secrets, the environment variable "
        + $"{section.Path.Replace(":", "__", StringComparison.Ordinal)}, or the command line.";

    private static string Quoted(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(k => $"'{k.ToLowerInvariant()}'").Order(StringComparer.Ordinal));
}
