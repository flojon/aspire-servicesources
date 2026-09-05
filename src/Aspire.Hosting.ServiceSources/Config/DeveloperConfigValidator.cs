using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Fails fast on a key that would bind to nothing, so a typo, a field written flat where it belongs
/// to a block, and a value and a block written the wrong way round are all reported instead of being
/// silently dropped. The last of those costs more than itself: the binder answers a key of the wrong
/// shape by abandoning the whole entry, which then reads downstream as a service nobody configured.
/// </summary>
/// <remarks>
/// Written against <see cref="DeveloperConfigShape"/> rather than against one entry type, so that
/// the backing-service section added alongside <c>services:</c> arrives with the same diagnostics
/// rather than a thinner copy of them. Every message names the kind of entry it is about, because
/// the two sections are edited in the same file and "Service 'orders-db'" sends the reader to the
/// wrong half of it.
/// </remarks>
internal static class DeveloperConfigValidator
{
    /// <summary>
    /// The one root key both entry shapes spell the same way, and the only one that takes a value.
    /// </summary>
    /// <remarks>
    /// A literal rather than <c>nameof</c> of either entry type's property: naming one of them here
    /// would read as though the other's <c>source</c> were a different key, which is exactly the
    /// confusion the shape indirection exists to remove. The shapes derive their root keys from
    /// their own properties, so a rename that dropped this key would fail the lookup below.
    /// </remarks>
    private const string SourceKey = "Source";

    /// <summary>
    /// Checks every entry of one kind and reports all of their problems together.
    /// </summary>
    /// <remarks>
    /// Across entries as well as within one. This is the release that moves every existing file
    /// into blocks, so a file with five still-flat services is five faulted entries: stopping at
    /// the first would cost a startup per service, which is the same objection that makes the walk
    /// over one entry collect rather than throw at its first bad key.
    /// </remarks>
    public static void ValidateAll(IEnumerable<IConfigurationSection> entries, DeveloperConfigShape shape)
    {
        List<(string Service, IReadOnlyList<string> Problems)>? faulted = null;

        foreach (var entry in entries)
        {
            var problems = Collect(entry.Key, entry, shape);

            if (problems.Count > 0)
            {
                (faulted ??= []).Add((entry.Key, problems));
            }
        }

        if (faulted is null)
        {
            return;
        }

        // A single faulted entry reads exactly as it did when entries were checked one at a time,
        // so the ordinary case — one service, one mistake — pays nothing for the collecting.
        throw faulted.Count == 1
            ? Failure(faulted[0].Service, faulted[0].Problems, shape)
            : CombinedFailure(faulted, shape);
    }

    /// <summary>
    /// Every problem with one service's entry. Every block is checked, not only the one
    /// <c>source</c> names: a block for a source this entry is not currently using is legitimate
    /// and left alone, but a typo inside it would otherwise lie in wait until the day the source is
    /// switched to it.
    /// </summary>
    /// <remarks>
    /// Collected rather than thrown at the first problem found. Moving an entry off the flat shape
    /// misplaces keys in bunches — an entry carrying <c>path</c>, <c>ref</c> and <c>context</c> at
    /// its root has three — and reporting one per run costs a failed startup per key. Nor was the
    /// order they surface in stable enough to make that a predictable march: it is
    /// <see cref="IConfigurationSection.GetChildren"/>'s, which is the provider's, so a file and a
    /// set of environment variables carrying the same mistakes need not agree on which one is
    /// named first.
    /// </remarks>
    private static IReadOnlyList<string> Collect(
        string serviceName, IConfigurationSection entry, DeveloperConfigShape shape)
    {
        var problems = new List<string>();

        // The entry itself, before its keys. Two different mistakes put a value here, and they do
        // not deserve the same complaint.
        //
        // A value-*only* entry is the flat shape's shortest entry — a source and nothing else —
        // written without the key that used to carry it. The binder answers a scalar where an
        // object goes with null, which the dictionary binder then drops, so left unchecked it is
        // the one wrong shape reported as no shape at all: the service reads downstream as one
        // nobody configured, out of a file that plainly names it.
        //
        // An entry can also carry a value *and* keys, because configuration merges per key: a
        // block in the file underneath a higher layer's scalar —
        // ServiceSources__Services__orders=local over an entry in local.json — yields both. That
        // one binds correctly, the binder finding no string converter for the entry type and
        // falling through to the children, so nothing is dropped and the fault is a different
        // one: the value is inert, and whoever set it to choose a source got silence. Reported
        // rather than tolerated for exactly that reason, and collected rather than thrown so that
        // a merged entry's keys are still walked.
        if (entry.Value is not null)
        {
            problems.Add(EntryExpected(serviceName, entry, alsoHasKeys: HasChildren(entry), shape));
        }

        foreach (var key in entry.GetChildren())
        {
            if (!shape.RootKeys.Contains(key.Key))
            {
                problems.Add(NotValidHere(serviceName, key, shape));
                continue;
            }

            if (!shape.BlockFields.TryGetValue(key.Key, out var fields))
            {
                // `source` is the one root key that takes a value rather than a block. An object
                // written there binds it to the empty string and, because the binder gives up on
                // the whole entry, takes every sibling block down with it.
                if (HasChildren(key))
                {
                    problems.Add(ValueExpected(serviceName, key, block: null));
                }
                else if (key.Value is { Length: > 0 } blankSource && string.IsNullOrWhiteSpace(blankSource))
                {
                    // The same refusal every block field gets, and for the same reason: whitespace
                    // is neither a value nor the empty spelling that unsets a key. Left out, it
                    // reached the source dispatch as a value — which reads a blank source as "not
                    // configured", so a backing service ran locally and a service reported having
                    // no source at all, neither of them mentioning the spaces that caused it.
                    problems.Add(Blank(key, block: null));
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

            CollectBlock(problems, serviceName, key, key.Key.ToLowerInvariant(), fields);
        }

        return problems;
    }

    /// <summary>
    /// Every problem inside one block of settings — a source block, or a block nested in one.
    /// </summary>
    /// <param name="blockPath">
    /// How the block is named in a message, dotted for a nested one: <c>local</c>,
    /// <c>local.prepare</c>.
    /// </param>
    /// <remarks>
    /// Recursive because <c>local.prepare</c> is the first block inside a block this file has held.
    /// Nothing about the walk is specific to that depth, so it is the same code rather than a second
    /// copy for level two — which is also what keeps a nested block's diagnostics identical to a
    /// top-level one's rather than a thinner version of them.
    /// </remarks>
    private static void CollectBlock(
        List<string> problems,
        string serviceName,
        IConfigurationSection block,
        string blockPath,
        IReadOnlyDictionary<string, PropertyInfo> fields)
    {
        foreach (var field in block.GetChildren())
        {
            if (!fields.TryGetValue(field.Key, out var declared))
            {
                problems.Add(NotValidInBlock(field, blockPath, fields));
                continue;
            }

            // Asked before the block question below, and it has to be: a list arrives from
            // IConfiguration as indexed children, and its type is a class, so a list asked about as
            // a block is classified as one and answered with "takes a value, not a block of
            // settings" — about a field whose value is neither.
            if (DeveloperConfigField.IsList(declared.PropertyType))
            {
                CollectList(problems, field, blockPath);
                continue;
            }

            if (DeveloperConfigField.BlockFieldsOf(declared.PropertyType) is { } nested)
            {
                // The same mistake one level down as a block name carrying a value: it binds to
                // nothing, and the binder giving up takes the surrounding block with it.
                if (field.Value is not null)
                {
                    problems.Add(BlockExpected(blockPath, field, nested));
                    continue;
                }

                CollectBlock(
                    problems, serviceName, field, $"{blockPath}.{field.Key.ToLowerInvariant()}", nested);
                continue;
            }

            // The mirror of the check above: a block written where a field's value goes. Like
            // a non-scalar `source`, it binds to nothing and costs the entry every other key
            // it carries, so the service reads downstream as one nobody configured at all.
            if (HasChildren(field))
            {
                problems.Add(ValueExpected(serviceName, field, blockPath));
                continue;
            }

            // A value of one or more spaces, whatever type the field takes. Refused rather than
            // read as absent — which is what a string field would otherwise become, since it
            // binds and the blank-to-absent walk in DeveloperConfiguration then drops it, so a
            // whitespace `local.path` sent the service to its managed checkout instead of the
            // developer's directory and said nothing about it.
            if (field.Value is { Length: > 0 } spaces && string.IsNullOrWhiteSpace(spaces))
            {
                problems.Add(Blank(field, blockPath));
                continue;
            }

            if (field.Value is { } value && !BindsTo(declared.PropertyType, value))
            {
                problems.Add(NotBindable(field, blockPath, declared.PropertyType, value));
            }
        }
    }

    /// <summary>
    /// Every problem with a field whose value is a list of values.
    /// </summary>
    /// <remarks>
    /// Only the shape is checked here. Whether the list is long enough to be a command, and whether
    /// its first element points where it is allowed to, belong to whoever reads it — a message about
    /// those can name the service and what the list is for, which this walk cannot. An empty list
    /// reaches that reader and is refused there by name, which is why nothing is said about it here;
    /// it is <em>not</em> a way to drop what a layer below wrote, because it binds to an empty array
    /// rather than to absent, and the flat providers cannot express one at all.
    /// <para>
    /// A null element is the exception, and it has to be caught here because it does not survive to
    /// the reader: the JSON provider records the key with a null value, and the binder then omits it
    /// from the array entirely, so the list <em>shortens</em> and every argument after it shifts
    /// down. <c>["mvn", null, "-Pprod"]</c> runs <c>mvn -Pprod</c>. Nothing downstream can report
    /// what it never receives.
    /// </para>
    /// </remarks>
    private static void CollectList(List<string> problems, IConfigurationSection field, string blockPath)
    {
        if (!HasChildren(field))
        {
            if (field.Value is { Length: > 0 } written)
            {
                problems.Add(string.IsNullOrWhiteSpace(written) ? Blank(field, blockPath) : ListExpected(field, blockPath));
            }

            return;
        }

        var elements = field.GetChildren().ToArray();

        foreach (var element in elements)
        {
            if (HasChildren(element))
            {
                problems.Add(ListElementExpected(field, element, blockPath));
            }
            else if (element.Value is null)
            {
                problems.Add(ListElementMissing(field, element, blockPath));
            }
        }

        // A list is keyed by position, so a position that is missing is an element that is missing.
        // The one that produces it is a null written in the file: the re-rooting that hands this
        // file over drops a null-valued key, because that is what an intermediate node looks like
        // too, so index 1 of a three-element list simply is not here — and the binder then closes
        // the gap, shortening the command and shifting every argument after it down a place.
        // `["mvn", null, "-Pprod"]` runs `mvn -Pprod`. Checked by shape rather than by cause, so a
        // hole from any other direction — a layer setting only `command:2` — is reported too.
        if (FirstMissingIndex(elements) is { } missing)
        {
            // The section for the position that is absent, rather than one of the positions that is
            // present: it has no value, which is the point, but it does have the key path the
            // remedy has to name — and naming element 0's instead sends the reader to a key that is
            // perfectly fine.
            problems.Add(ListElementMissing(field, field.GetSection(missing.ToString()), blockPath));
        }
    }

    /// <summary>
    /// The lowest position below the highest one present that no element occupies, or
    /// <see langword="null"/> when the positions run 0, 1, 2 … as a list's must.
    /// </summary>
    /// <remarks>
    /// Answers <see langword="null"/> for anything whose keys are not positions at all rather than
    /// guessing at it: a list always arrives keyed by index, so a key that is not one means this is
    /// not the shape being reasoned about and something else is already reporting it.
    /// </remarks>
    private static int? FirstMissingIndex(IConfigurationSection[] elements)
    {
        var present = new HashSet<int>(elements.Length);

        foreach (var element in elements)
        {
            if (!int.TryParse(element.Key, out var index))
            {
                return null;
            }

            present.Add(index);
        }

        if (present.Count == 0)
        {
            return null;
        }

        for (var index = 0; index < present.Max(); index++)
        {
            if (!present.Contains(index))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// One exception for however many problems one entry turned out to have, naming the service
    /// once rather than once per problem.
    /// </summary>
    /// <remarks>
    /// A lone problem reads exactly as it did when each was thrown where it was found, so the
    /// ordinary case pays nothing for the collecting. Several are listed, each keeping its own
    /// remedy line, because the key path is what makes the layer that set it findable and that
    /// differs per problem.
    /// </remarks>
    private static ServiceSourcesConfigurationException Failure(
        string serviceName, IReadOnlyList<string> problems, DeveloperConfigShape shape) =>
        new(problems.Count == 1
            ? $"{shape.Kind} '{serviceName}': {problems[0]}"
            : $"{shape.Kind} '{serviceName}': {problems.Count} problems with the entry:"
              + string.Concat(problems.Select(p => $"{Environment.NewLine}  - {p}")));

    /// <summary>
    /// One exception for problems spread across several service entries.
    /// </summary>
    /// <remarks>
    /// Grouped by service rather than flattened into one list: the entries are independent of each
    /// other, and a developer migrating a file works through it a service at a time.
    /// </remarks>
    private static ServiceSourcesConfigurationException CombinedFailure(
        IReadOnlyList<(string Service, IReadOnlyList<string> Problems)> faulted, DeveloperConfigShape shape)
    {
        var total = faulted.Sum(entry => entry.Problems.Count);

        var message = new StringBuilder()
            .Append($"{total} problems across {faulted.Count} {shape.Noun} entries:");

        foreach (var (service, problems) in faulted)
        {
            message.Append(Environment.NewLine).Append($"  {shape.Kind} '{service}':");

            foreach (var problem in problems)
            {
                message.Append(Environment.NewLine).Append($"    - {problem}");
            }
        }

        return new ServiceSourcesConfigurationException(message.ToString());
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
    /// gesture there is for dropping a value the layer below supplied. Whitespace is not that
    /// gesture, and never reaches this check — <see cref="Blank"/> refuses it first.
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
    /// <para>
    /// A field several blocks declare names all of them rather than picking one, since which of
    /// them the developer meant is exactly what this message does not know. No shape has such a
    /// field yet, so that branch is unreachable as this ships; the field that will have it is a
    /// source block's <c>connectionString</c>, since each source wants its own template.
    /// </para>
    /// <para>
    /// A key that is nearly a field gets the same sentence with the field named, because the list
    /// in the last branch cannot help there: the keys valid at an entry's root are <c>source</c>
    /// and the block names, so a misspelled <c>path</c> was answered with five words that could
    /// never include <c>path</c>. Every field moved into a block in the release before this one,
    /// which makes "a field written flat at the entry root" the shape of every unmigrated file
    /// there is — and the shape a developer retyping one gets wrong.
    /// </para>
    /// <para>
    /// The near miss is not extended to <see cref="NotValidInBlock"/>, where the same mistake
    /// inside a block is already answered with the block's own list of valid keys — two to four
    /// words, one of which is the answer. A guess adds nothing to a list the reader can already
    /// read, and would put a wrong field name in front of them when the typo is closer to a field
    /// they did not mean.
    /// </para>
    /// </remarks>
    private static string NotValidHere(
        string serviceName, IConfigurationSection key, DeveloperConfigShape shape)
    {
        var homes = shape.HomeBlocksOf(key.Key).Select(Spelled).ToArray();

        if (homes.Length == 1)
        {
            return $"'{key.Key}' is not a valid key here. It belongs in the "
                + $"'{homes[0]}' block: \"{serviceName}\": {{ ..., \"{homes[0]}\": {{ \"{key.Key}\": ... }} }}."
                + SetAt(key);
        }

        if (homes.Length > 1)
        {
            return $"'{key.Key}' is not a valid key here. It belongs inside the block of the source "
                + $"it configures — {Quoted(homes)}: "
                + $"\"{serviceName}\": {{ ..., \"{homes[0]}\": {{ \"{key.Key}\": ... }} }}."
                + SetAt(key);
        }

        var near = shape.NearMissFieldsOf(key.Key);

        // One candidate keeps the shape to write, exactly as the exact-match branch above shows it.
        // Several cannot: there is no single block to put the key in, so the sentence names the
        // candidates and stops rather than illustrating one of them as though it were the answer.
        if (near.Count == 1)
        {
            var (field, block) = (Spelled(near[0].Field), Spelled(near[0].Block));

            return $"'{key.Key}' is not a valid key here. Did you mean '{field}', in the "
                + $"'{block}' block: \"{serviceName}\": {{ ..., \"{block}\": {{ \"{field}\": ... }} }}?"
                + SetAt(key);
        }

        if (near.Count > 1)
        {
            return $"'{key.Key}' is not a valid key here. Did you mean {DescribeNearMisses(near)}?"
                + SetAt(key);
        }

        return $"'{key.Key}' is not a valid key. Valid keys are {Quoted(shape.RootKeys)}."
            + SetAt(key);
    }

    /// <summary>
    /// Near-miss candidates as prose: <c>'port', in the 'kubernetes' block, or 'path', in the
    /// 'local' block</c>.
    /// </summary>
    /// <remarks>
    /// Grouped by field name so that a field two blocks each declare their own copy of reads as one
    /// suggestion in more than one place rather than as the same word twice — which a backing
    /// service's <c>connectionString</c>, declared by both <c>direct</c> and <c>kubernetes</c>,
    /// does. Candidates arrive already ordered by
    /// <see cref="NearMiss.Nearest"/>, so what is added here is only the block ordering within a
    /// group.
    /// </remarks>
    private static string DescribeNearMisses(IEnumerable<(string Field, string Block)> candidates) =>
        string.Join(
            ", or ",
            candidates
                .GroupBy(candidate => candidate.Field, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                    $"'{Spelled(group.Key)}', in the "
                    + string.Join(
                        " or ",
                        group.Select(candidate => $"'{Spelled(candidate.Block)}'")
                            .Order(StringComparer.Ordinal))
                    + " block"));

    /// <summary>The error for a key that no block of this name declares.</summary>
    private static string NotValidInBlock(
        IConfigurationSection field, string block, IReadOnlyDictionary<string, PropertyInfo> fields) =>
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
    /// <param name="alsoHasKeys">
    /// Whether the entry carries settings as well as this value, which a merge of configuration
    /// layers can produce and a single file cannot.
    /// </param>
    private static string EntryExpected(
        string serviceName, IConfigurationSection entry, bool alsoHasKeys, DeveloperConfigShape shape)
    {
        var value = entry.Value ?? "";

        // The entry has its block of settings, so the shape is not what is wrong and the value is
        // not what the entry was written as. Saying "the entry takes a block of settings" of an
        // entry that plainly has one — and then showing a shape the file already has — would
        // describe a mistake nobody made. What is wrong is that this value does nothing.
        if (alsoHasKeys)
        {
            return $"the entry carries the value {Escaped(value)} as well as its settings, and that "
                + $"value is inert: a scalar at a {shape.Noun}'s own key binds to nothing, so "
                + "nothing reads it. If it was meant to choose the source, that is the 'source' key "
                + "inside the entry."
                + SetAtBlock(entry, SourceKey);
        }

        // The suggestion needs no escaping of its own: a whitespace value is not the name of any
        // source, so it fails this lookup and the placeholder is what gets shown.
        var source = shape.SourceNames.Contains(value) ? value : "...";

        return $"the entry takes a block of settings, not the value {Escaped(value)}: "
            + $"\"{serviceName}\": {{ \"source\": \"{source}\" }}. "
            + $"Valid keys there are {Quoted(shape.RootKeys)}."
            + SetAtBlock(entry, SourceKey);
    }

    /// <summary>
    /// The error for a block name carrying a value: the key is a valid one, and what is wrong is
    /// the value written where a block of settings goes.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NotValidHere"/> because a block name is nearly always a block name
    /// and nothing else — <c>url</c>, which is also a service's <c>url.url</c> field, is the
    /// exception — so that message's fallback branch would call the key invalid and then list it
    /// among the valid ones.
    /// </remarks>
    /// <param name="container">
    /// What the block sits in, as the message shows it: the service's own name for a source block,
    /// and the enclosing block's path for one nested inside it.
    /// </param>
    private static string BlockExpected(
        string container, IConfigurationSection key, IReadOnlyDictionary<string, PropertyInfo> fields)
    {
        var block = key.Key.ToLowerInvariant();

        return $"'{key.Key}' takes a block of settings, not a value: "
            + $"\"{container}\": {{ ..., \"{block}\": {{ ... }} }}. "
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
    /// The error for a list field written as a single value — <c>"command": "./prepare.sh"</c>.
    /// </summary>
    /// <remarks>
    /// A list has no scalar spelling that binds, so the fix is the brackets rather than a different
    /// value, and the remedy names the indexed key each flat configuration layer sets an element
    /// through.
    /// </remarks>
    private static string ListExpected(IConfigurationSection field, string block) =>
        $"'{field.Key}' in the '{block}' block takes a list of values, not the single value "
        + $"{Escaped(field.Value)}: \"{field.Key}\": [ ... ]."
        + SetAtList(field);

    /// <summary>
    /// The error for a null element of a list — a JSON <c>null</c>, or a key a provider recorded
    /// with no value.
    /// </summary>
    /// <remarks>
    /// Its own message rather than <see cref="Blank"/>'s, because what is wrong is not the value but
    /// what becomes of the list: the binder omits a null element, so everything after it moves down
    /// a place and the command that runs is one argument shorter than the one that was written. An
    /// empty element is a different thing and is allowed, since a command may take an empty
    /// argument — which is also the spelling this suggests, being what someone writing <c>null</c>
    /// most likely meant.
    /// </remarks>
    private static string ListElementMissing(
        IConfigurationSection field, IConfigurationSection element, string block) =>
        $"'{field.Key}' in the '{block}' block has no value at element '{element.Key}'. A null element is "
        + "dropped rather than passed on, which shortens the list and shifts every element after it down a "
        + "place — so the command that ran would be missing an argument, with nothing to say so. Remove the "
        + "element, or write it as \"\" if the command really takes an empty one."
        + SetAt(element);

    /// <summary>
    /// The error for an element of a list field that is itself a block of settings.
    /// </summary>
    private static string ListElementExpected(
        IConfigurationSection field, IConfigurationSection element, string block) =>
        $"'{field.Key}' in the '{block}' block is a list of values, but its element at "
        + $"'{element.Key}' is a block of settings. Every element has to be a value."
        + SetAt(element);

    /// <summary>
    /// The error for a value the binder could not turn into the field's type — a key that is valid
    /// everywhere except in what it was set to.
    /// </summary>
    /// <remarks>
    /// Worth its own check rather than being left to the binder, which reports it as a failure to
    /// convert a value at a key path into a CLR type, from a plain
    /// <see cref="InvalidOperationException"/> that no handler upstream treats as a configuration
    /// problem. A whitespace value never arrives here: <see cref="Blank"/> takes it first, for
    /// every field type rather than only the ones the binder chokes on.
    ///
    /// The empty case is unreachable while every field in the shape is nullable, since
    /// <see cref="BindsTo"/> accepts an empty value for anything that can hold null. It is spelled
    /// out anyway, because the field that makes it reachable is one non-nullable property away and
    /// would otherwise be reported as a value of the wrong type with no mention of the one gesture
    /// the developer was reaching for. The wording has to differ from <see cref="Blank"/>'s: a
    /// field that cannot be unset is not helped by being told how to unset one.
    /// </remarks>
    private static string NotBindable(
        IConfigurationSection field, string block, Type type, string value) =>
        $"'{field.Key}' in the '{block}' block takes a {Described(type)}, "
        + $"but is set to {Escaped(value)}."
        + (value.Length == 0
            ? " An empty value leaves a field unset where the field can be unset; this one cannot, "
              + "so it needs a value."
            : "")
        + SetAt(field);

    /// <summary>
    /// The error for a value of one or more spaces, whatever type the field takes.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="NotBindable"/>, which says what the field takes and what it was
    /// given. That pairing reads as a contradiction for a string field — a space *is* a string —
    /// and the field's type is beside the point in any case: what is wrong is that whitespace is
    /// neither a value nor the empty spelling that unsets a field, and the empty spelling is what
    /// this is nearly always someone reaching for.
    ///
    /// "Whitespace" rather than "spaces" because the check is
    /// <see cref="string.IsNullOrWhiteSpace"/>, which a tab, a newline and a non-breaking space
    /// all satisfy. Naming the space would send someone who typed one of those to retype a space
    /// and meet the identical error — and the value goes through <see cref="Escaped"/> for the
    /// same reason, since none of them can be told from a space by looking.
    /// </remarks>
    /// <param name="block">
    /// The block the field sits in, or <see langword="null"/> when it sits directly on the entry —
    /// which is <c>source</c>, the one root key that takes a value.
    /// </param>
    private static string Blank(IConfigurationSection field, string? block) =>
        $"'{field.Key}'{(block is null ? "" : $" in the '{block}' block")} is set to "
        + $"{Escaped(field.Value)}, which is whitespace rather than a value. Set it to an empty "
        + "value to leave the field unset."
        + SetAt(field);

    /// <summary>
    /// A value as a quoted literal with its whitespace spelled out, so that a character which
    /// looks like a space — a tab, a newline, U+00A0 — is distinguishable from one.
    /// </summary>
    /// <remarks>
    /// The plain space is left as itself: it is the character a reader assumes, so escaping it
    /// would add noise to the common case and nothing else. Everything else whitespace gets its
    /// code point, which is what a developer needs in order to find it in the file.
    ///
    /// Every message that echoes a value back goes through this, rather than only the ones about
    /// whitespace. A message is read by someone who cannot see what they typed, and which messages
    /// a whitespace value can reach is not a thing to work out per message: it was reaching
    /// <see cref="EntryExpected"/> unescaped for exactly as long as it took to notice.
    /// </remarks>
    private static string Escaped(string? value) =>
        value is null
            ? "''"
            : $"'{string.Concat(value.Select(c => c switch
            {
                ' ' => " ",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                _ when char.IsWhiteSpace(c) => $"\\u{(int)c:x4}",

                // A character with no glyph of its own is worse than one that merely looks like a
                // space: echoed as itself it is not there at all, so the value reads back as
                // exactly what the developer typed and the message appears to be complaining about
                // nothing. Control characters and Unicode's Format category are the two slices of
                // that this can name without reaching a character somebody meant — a combining mark
                // is invisible too, and a decomposed accented letter carries one.
                _ when char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
                    => $"\\u{(int)c:x4}",

                _ => c.ToString(),
            }))}'";

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
    /// The same, for a key that has to hold a list. The flat providers carry one leaf each, so an
    /// element is set through its index — which is also how such a layer's value merges over one in
    /// the file, per index rather than wholesale.
    /// </summary>
    private static string SetAtList(IConfigurationSection section) =>
        $" The key is '{section.Path}', which any configuration layer can set: "
        + $"{DeveloperConfiguration.FileName}, appsettings, user secrets, the environment or the "
        + "command line — the flat layers an element at a time, as "
        + $"{$"{section.Path}:0".Replace(":", "__", StringComparison.Ordinal)}.";

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
        string.Join(", ", keys.Select(k => $"'{Spelled(k)}'").Order(StringComparer.Ordinal));

    /// <summary>
    /// A key as a developer writes it, from the property name the shape derived it from.
    /// </summary>
    /// <remarks>
    /// Lowercasing the whole name was invisible while every field in this file was a single word,
    /// and stopped being invisible the moment one was not: `WindowsCommand` was advertised as
    /// `windowscommand`, and `ConnectionString` — which predates it — as `connectionstring`. Both
    /// bind, since configuration keys are case-insensitive, so the spelling was never wrong so much
    /// as not the one the documentation uses, in the one sentence whose whole job is to tell a
    /// developer what to type.
    /// <para>
    /// Applied only to names that came from the shape, never to a key echoed back from what the
    /// developer wrote: this lowercases the first character and keeps the rest, which is right for a
    /// PascalCase property and would mangle an oddly-cased key from a file.
    /// </para>
    /// </remarks>
    private static string Spelled(string key) =>
        key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key[1..];
}
