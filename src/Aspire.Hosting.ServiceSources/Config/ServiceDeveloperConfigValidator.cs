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
    /// <remarks>
    /// Every problem with the entry is collected and reported together rather than thrown at the
    /// first one found. Moving an entry off the flat shape misplaces keys in bunches — an entry
    /// carrying <c>path</c>, <c>ref</c> and <c>context</c> at its root has three — and reporting
    /// one per run costs a failed startup per key. Nor was the order they surface in stable enough
    /// to make that a predictable march: it is <see cref="IConfigurationSection.GetChildren"/>'s,
    /// which is the provider's, so a file and a set of environment variables carrying the same
    /// mistakes need not agree on which one is named first.
    /// </remarks>
    public static void Validate(string serviceName, IConfigurationSection entry)
    {
        // The entry itself, before its keys: an entry written as a value has no children, so the
        // walk below would find nothing to object to. It is the flat shape's shortest entry — a
        // source and nothing else — written without the key that used to carry it, and the binder
        // answers a scalar where an object goes with null, which the dictionary binder then drops.
        // Left unchecked it is the one wrong shape reported as no shape at all: the service reads
        // downstream as one nobody configured, out of a file that plainly names it.
        //
        // Reported alone rather than collected with others, because it is the whole entry that is
        // wrong: it has no keys to walk, so there is nothing else to find.
        if (entry.Value is not null)
        {
            throw Failure(serviceName, [EntryExpected(serviceName, entry)]);
        }

        var problems = new List<string>();

        foreach (var key in entry.GetChildren())
        {
            if (!ServiceDeveloperConfigShape.RootKeys.Contains(key.Key))
            {
                problems.Add(NotValidHere(serviceName, key));
                continue;
            }

            if (!ServiceDeveloperConfigShape.BlockFields.TryGetValue(key.Key, out var fields))
            {
                // `source` is the one root key that takes a value rather than a block. An object
                // written there binds it to the empty string and, because the binder gives up on
                // the whole entry, takes every sibling block down with it.
                if (HasChildren(key))
                {
                    problems.Add(ValueExpected(serviceName, key, block: null));
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
                problems.Add(BlockExpected(serviceName, key, fields));
                continue;
            }

            foreach (var field in key.GetChildren())
            {
                if (!fields.TryGetValue(field.Key, out var fieldType))
                {
                    problems.Add(NotValidInBlock(field, key.Key, fields));
                    continue;
                }

                // The mirror of the check above: a block written where a field's value goes. Like
                // a non-scalar `source`, it binds to nothing and costs the entry every other key
                // it carries, so the service reads downstream as one nobody configured at all.
                if (HasChildren(field))
                {
                    problems.Add(ValueExpected(serviceName, field, key.Key.ToLowerInvariant()));
                    continue;
                }

                // A value of one or more spaces is refused whatever type the field takes, which a
                // string field would otherwise not be: it binds, and the blank-to-absent walk in
                // DeveloperConfiguration then drops it, so a whitespace `local.path` sent the
                // service to its managed checkout rather than the developer's directory and said
                // nothing about it. It is the same mistake for a string as for a number — reaching
                // for the empty value that unsets a field and missing by a character — so it gets
                // the same answer, which already names the spelling that works.
                var blank = field.Value is { Length: > 0 } spaces && string.IsNullOrWhiteSpace(spaces);

                if (field.Value is { } value && (blank || !BindsTo(fieldType, value)))
                {
                    problems.Add(NotBindable(field, key.Key.ToLowerInvariant(), fieldType, value));
                }
            }
        }

        if (problems.Count > 0)
        {
            throw Failure(serviceName, problems);
        }
    }

    /// <summary>
    /// One exception for however many problems an entry turned out to have, naming the service once
    /// rather than once per problem.
    /// </summary>
    /// <remarks>
    /// A lone problem reads exactly as it did when each was thrown where it was found, so the
    /// ordinary case pays nothing for the collecting. Several are listed, each keeping its own
    /// remedy line, because the key path is what makes the layer that set it findable and that
    /// differs per problem.
    /// </remarks>
    private static ServiceSourcesConfigurationException Failure(
        string serviceName, IReadOnlyList<string> problems) =>
        new(problems.Count == 1
            ? $"Service '{serviceName}': {problems[0]}"
            : $"Service '{serviceName}': {problems.Count} problems with the entry:"
              + string.Concat(problems.Select(p => $"{Environment.NewLine}  - {p}")));

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
    /// gesture there is for dropping a value the layer below supplied. Whitespace is not that
    /// gesture, and is refused alongside this check rather than by it, since for a string field the
    /// binder would take it.
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
    private static string NotValidHere(string serviceName, IConfigurationSection key)
    {
        var home = ServiceDeveloperConfigShape.HomeBlockOf(key.Key)?.ToLowerInvariant();

        return (home is not null
                ? $"'{key.Key}' is not a valid key here. It belongs in the "
                  + $"'{home}' block: \"{serviceName}\": {{ ..., \"{home}\": {{ \"{key.Key}\": ... }} }}."
                : $"'{key.Key}' is not a valid key. Valid keys are "
                  + $"{Quoted(ServiceDeveloperConfigShape.RootKeys)}.")
            + SetAt(key);
    }

    /// <summary>The error for a key that no block of this name declares.</summary>
    private static string NotValidInBlock(
        IConfigurationSection field, string block, IReadOnlyDictionary<string, Type> fields) =>
        $"'{field.Key}' is not a valid key in the "
        + $"'{block.ToLowerInvariant()}' block. Valid keys there are {Quoted(fields.Keys)}."
        + SetAt(field);

    /// <summary>
    /// The error for a whole service entry written as a value instead of a block of settings.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="BlockExpected"/> for the suggestion: the entry is the outermost
    /// object, so there is no surrounding one to show, and the value it was given is worth reading
    /// rather than only reporting back. Every source has a block named for it, so a value naming one
    /// of those blocks is a source name — someone writing <c>"orders": "local"</c>, where the fix is
    /// the <c>source</c> key that value belongs under and not a list of the keys an entry takes.
    /// </remarks>
    private static string EntryExpected(string serviceName, IConfigurationSection entry)
    {
        var value = entry.Value ?? "";

        var source = ServiceDeveloperConfigShape.BlockFields.ContainsKey(value) ? value : "...";

        return $"the entry takes a block of settings, not the value '{value}': "
            + $"\"{serviceName}\": {{ \"source\": \"{source}\" }}. "
            + $"Valid keys there are {Quoted(ServiceDeveloperConfigShape.RootKeys)}."
            + SetAtBlock(entry, nameof(ServiceDeveloperConfig.Source));
    }

    /// <summary>
    /// The error for a block name carrying a value: the key is a valid one, and what is wrong is
    /// the value written where a block of settings goes.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NotValidHere"/> because three of the four block names — everything
    /// but <c>url</c> — are block names and nothing else, so that message's fallback branch would
    /// call the key invalid and then list it among the valid ones.
    /// </remarks>
    private static string BlockExpected(
        string serviceName, IConfigurationSection key, IReadOnlyDictionary<string, Type> fields)
    {
        var block = key.Key.ToLowerInvariant();

        return $"'{key.Key}' takes a block of settings, not a value: "
            + $"\"{serviceName}\": {{ ..., \"{block}\": {{ ... }} }}. "
            + $"Valid keys there are {Quoted(fields.Keys)}."
            // FirstOrDefault rather than First: every block declares at least one field, but a
            // message builder that can throw would replace the configuration error being reported
            // with one about itself.
            + SetAtBlock(key, fields.Keys.Order(StringComparer.Ordinal).FirstOrDefault() ?? "<field>");
    }

    /// <summary>
    /// The error for a key carrying a block where a value goes — a non-scalar <c>source</c>, or a
    /// field inside a block written as an object.
    /// </summary>
    /// <param name="block">
    /// The block <paramref name="key"/> sits in, or <see langword="null"/> when it sits directly on
    /// the service entry.
    /// </param>
    /// <remarks>
    /// Keeps the plain <see cref="SetAt"/> remedy, unlike the two errors above it: the key this one
    /// names is a key that has to hold a value, so setting it from an environment variable is the
    /// fix rather than the mistake.
    /// </remarks>
    private static string ValueExpected(string serviceName, IConfigurationSection key, string? block) =>
        $"'{key.Key}'{(block is null ? "" : $" in the '{block}' block")} "
        + $"takes a value, not a block of settings: \"{block ?? serviceName}\": {{ \"{key.Key}\": ... }}."
        + SetAt(key);

    /// <summary>
    /// The error for a value the binder could not turn into the field's type — a key that is valid
    /// everywhere except in what it was set to.
    /// </summary>
    /// <remarks>
    /// Worth its own check rather than being left to the binder, which reports it as a failure to
    /// convert a value at a key path into a CLR type, from a plain
    /// <see cref="InvalidOperationException"/> that no handler upstream treats as a configuration
    /// problem. The empty spelling is named for a whitespace value because that is someone reaching
    /// for the gesture that unsets a field and missing by one character — the reason a whitespace
    /// value is routed here for a string field too, which the binder itself would have accepted.
    /// </remarks>
    private static string NotBindable(
        IConfigurationSection field, string block, Type type, string value) =>
        $"'{field.Key}' in the '{block}' block takes a {Described(type)}, "
        + $"but is set to '{value}'."
        + (string.IsNullOrWhiteSpace(value) ? " Set it to an empty value to leave the field unset." : "")
        + SetAt(field);

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
    /// a CI machine carrying a stale <c>ServiceSources__Services__orders__Local__Path</c> is the
    /// case that costs the most to track down. Naming the key, and its environment spelling, is
    /// what makes the layer that set it findable; naming a file would send that developer to edit
    /// the one place the value is not.
    /// </remarks>
    private static string SetAt(IConfigurationSection section) =>
        $" The key is '{section.Path}', which any configuration layer can set: "
        + $"{DeveloperConfiguration.FileName}, appsettings, user secrets, the environment variable "
        + $"{section.Path.Replace(":", "__", StringComparison.Ordinal)}, or the command line.";

    /// <summary>
    /// The same, for a key that has to hold a block of settings rather than a value.
    /// </summary>
    /// <remarks>
    /// <see cref="SetAt"/> names the key's own environment spelling, which for a block is advice
    /// that cannot be followed and, worse, describes the mistake being reported: the flat providers
    /// carry one leaf each, so no environment variable can put an object at this key, and reaching
    /// for one is how an entry ends up written as a value in the first place. Naming the key path
    /// still locates the layer that set it — that half is the point of saying any of this — so what
    /// changes is the spelling, which has to be of a field inside the block rather than of the
    /// block.
    /// </remarks>
    private static string SetAtBlock(IConfigurationSection section, string exampleField) =>
        $" The key is '{section.Path}', which any configuration layer can set: "
        + $"{DeveloperConfiguration.FileName}, appsettings, user secrets, the environment or the "
        + "command line — the flat layers a field at a time, as "
        + $"{$"{section.Path}:{exampleField}".Replace(":", "__", StringComparison.Ordinal)}.";

    private static string Quoted(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(k => $"'{k.ToLowerInvariant()}'").Order(StringComparer.Ordinal));
}
