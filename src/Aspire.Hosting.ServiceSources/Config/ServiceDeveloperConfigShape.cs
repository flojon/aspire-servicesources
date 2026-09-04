using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// What a service entry is allowed to contain, read off <see cref="ServiceDeveloperConfig"/> itself
/// rather than declared a second time beside it. Deriving it means a field added to a block type is
/// immediately a valid key, with nothing to keep in step.
/// </summary>
/// <remarks>
/// Every set compares with <see cref="StringComparer.OrdinalIgnoreCase"/> because configuration
/// keys do: a <c>Local:Path</c> arriving from an environment variable and a <c>local:path</c> in
/// the file are the same key.
/// </remarks>
internal static class ServiceDeveloperConfigShape
{
    /// <summary>The block properties — every property whose value is a nested settings object.</summary>
    /// <remarks>
    /// Tested for positively rather than by excluding <see cref="string"/> alone, so that a scalar
    /// added at the entry root later — a <c>bool?</c> or an <c>int?</c> — is not silently taken for
    /// a block and walked for fields it does not have.
    /// </remarks>
    public static IReadOnlyList<PropertyInfo> Blocks { get; } =
        typeof(ServiceDeveloperConfig).GetProperties()
            .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string))
            .ToArray();

    /// <summary>The keys valid directly on a service entry: <c>source</c> and the block names.</summary>
    public static IReadOnlySet<string> RootKeys { get; } =
        typeof(ServiceDeveloperConfig).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Block name to the keys valid inside it, each carrying the type its value has to bind to.
    /// </summary>
    /// <remarks>
    /// The type travels with the name because a key can be valid and its value still unbindable —
    /// a <c>port</c> written as <c>"abc"</c> — and the binder answers that with an exception naming
    /// a CLR type rather than the field. Checking it here keeps every complaint about an entry
    /// arriving in the same shape, at the same moment.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Type>> BlockFields { get; } =
        Blocks.ToDictionary(
            block => block.Name,
            block => (IReadOnlyDictionary<string, Type>)block.PropertyType.GetProperties()
                .ToDictionary(field => field.Name, field => field.PropertyType, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The block <paramref name="field"/> belongs in, or <see langword="null"/> if no block has a
    /// field by that name. Used to turn "that key does not go there" into "here is where it goes",
    /// which is unambiguous only because no field name is shared by two blocks.
    /// </summary>
    public static string? HomeBlockOf(string field) =>
        BlockFields.FirstOrDefault(block => block.Value.ContainsKey(field)).Key;

    /// <summary>
    /// The field <paramref name="writtenKey"/> looks like a misspelling of, and the block it lives
    /// in — or <see langword="null"/> when it resembles no field.
    /// </summary>
    /// <remarks>
    /// The fuzzy counterpart of <see cref="HomeBlockOf"/>, and asked only after that has come back
    /// empty. Spelled correctly, a field written at an entry's root is answered with the block it
    /// belongs in; one letter off, it used to be answered with the list of keys valid at the root,
    /// which cannot contain the word the developer was reaching for — the field is a level down. So
    /// the reader got five words, none of them the answer, and no hint the key existed at all.
    /// <para>
    /// One answer rather than every tie, because no two blocks declare a field by the same name
    /// today. Ties are possible even so — a typo can sit one edit from fields in two blocks — so
    /// the closest candidates are ordered here by field <em>and</em> block before one is taken.
    /// <see cref="NearMiss.Nearest"/> orders by the spelling it was given, which separates two
    /// differently-named fields but not one field name declared by two blocks; for that pair it
    /// leaves the order it received, and what it received is
    /// <see cref="System.Type.GetProperties()"/>'s, which the CLR does not promise to keep stable.
    /// Ordering by the block as well makes the answer total, so the same one is named on every run
    /// whichever kind of tie it is — and a section that does declare a field in two blocks, which
    /// is the ordinary case for a backing service's <c>connectionString</c>, cannot reintroduce the
    /// question.
    /// </para>
    /// </remarks>
    public static (string Field, string Block)? NearMissFieldOf(string writtenKey) =>
        NearMiss.Nearest(
                writtenKey,
                BlockFields.SelectMany(block => block.Value.Keys.Select(field => (Field: field, Block: block.Key))),
                candidate => candidate.Field)
            .OrderBy(candidate => candidate.Field, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Block, StringComparer.Ordinal)
            .Select(candidate => ((string, string)?)candidate)
            .FirstOrDefault();
}
