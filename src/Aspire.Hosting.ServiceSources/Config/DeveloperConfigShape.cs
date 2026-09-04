using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// What one kind of developer-config entry is allowed to contain, read off the entry type itself
/// rather than declared a second time beside it. Deriving it means a field added to a block type is
/// immediately a valid key, with nothing to keep in step.
/// </summary>
/// <remarks>
/// One instance per entry type, because there are now two: a service entry and a backing-service
/// entry, which share every rule about how an entry is shaped and agree on none of their fields.
/// <see cref="DeveloperConfigValidator"/> is written against this rather than against either type,
/// so the second kind of entry inherited the whole of the first one's diagnostics.
/// <para>
/// Every set compares with <see cref="StringComparer.OrdinalIgnoreCase"/> because configuration
/// keys do: a <c>Local:Path</c> arriving from an environment variable and a <c>local:path</c> in
/// the file are the same key.
/// </para>
/// </remarks>
internal sealed class DeveloperConfigShape
{
    /// <summary>A service entry, keyed under <see cref="DeveloperConfiguration.ServicesKey"/>.</summary>
    public static DeveloperConfigShape Service { get; } =
        Of<ServiceDeveloperConfig>("Service", "service", ["local", "url", "kubernetes", "container"]);

    /// <summary>
    /// A backing-service entry, keyed under <see cref="DeveloperConfiguration.BackingServicesKey"/>.
    /// </summary>
    public static DeveloperConfigShape BackingService { get; } =
        Of<BackingServiceDeveloperConfig>("Backing service", "backing service", ["local", "direct"]);

    private DeveloperConfigShape(
        Type entry,
        string kind,
        string noun,
        IEnumerable<string> sourceNames)
    {
        Kind = kind;
        Noun = noun;
        SourceNames = sourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Blocks = entry.GetProperties()
            .Where(p => DeveloperConfigField.BlockFieldsOf(p.PropertyType) is not null)
            .ToArray();

        RootKeys = entry.GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        BlockFields = Blocks.ToDictionary(
            block => block.Name,
            block => (IReadOnlyDictionary<string, Type>)block.PropertyType.GetProperties()
                .ToDictionary(field => field.Name, field => field.PropertyType, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How this kind of entry is named at the start of a sentence — <c>Service</c>.</summary>
    public string Kind { get; }

    /// <summary>The same mid-sentence, and as the stem of a plural — <c>service entries</c>.</summary>
    public string Noun { get; }

    /// <summary>
    /// The values this kind of entry's <c>source</c> accepts, for the one message that has to
    /// recognize one: an entry written as a bare value, where the value is almost always a source
    /// name and the fix is the key it belongs under.
    /// </summary>
    /// <remarks>
    /// Declared rather than taken from <see cref="BlockFields"/>, which every service source
    /// happens to have an entry in. A backing service's <c>local</c> source has no block of its
    /// own — what it needs is the factory the AppHost passes to <c>AddBackingService</c>, which is
    /// code and not configuration — so deriving the names from the blocks would fail to recognize
    /// the one source a developer is most likely to write. The dispatch tables remain the
    /// authority; a test asserts these agree with them.
    /// </remarks>
    public IReadOnlySet<string> SourceNames { get; }

    /// <summary>The block properties — every property whose value is a nested settings object.</summary>
    /// <remarks>
    /// Tested for positively rather than by excluding <see cref="string"/> alone, so that a scalar
    /// added at the entry root later — a <c>bool?</c> or an <c>int?</c> — is not silently taken for
    /// a block and walked for fields it does not have. A list is excluded by the same test, since
    /// <see cref="string"/><c>[]</c> is a class and would otherwise be walked for the fields an
    /// array does not have.
    /// </remarks>
    public IReadOnlyList<PropertyInfo> Blocks { get; }

    /// <summary>The keys valid directly on an entry: <c>source</c> and the block names.</summary>
    public IReadOnlySet<string> RootKeys { get; }

    /// <summary>
    /// Block name to the keys valid inside it, each carrying the type its value has to bind to.
    /// </summary>
    /// <remarks>
    /// The type travels with the name because a key can be valid and its value still unbindable —
    /// a <c>port</c> written as <c>"abc"</c> — and the binder answers that with an exception naming
    /// a CLR type rather than the field. Checking it here keeps every complaint about an entry
    /// arriving in the same shape, at the same moment.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Type>> BlockFields { get; }

    /// <summary>
    /// The blocks that declare a field named <paramref name="field"/>, in name order, or empty when
    /// none does. Used to turn "that key does not go there" into "here is where it goes".
    /// </summary>
    /// <remarks>
    /// A list rather than the single answer both shapes can give today, because a field name shared
    /// by two blocks is coming and naming just the first would send the developer to a block they
    /// are not using. Each source that takes a <c>connectionString</c> will declare its own, since
    /// each wants its own template — the <c>kubernetes</c> one carries a <c>{port}</c> placeholder
    /// that would be dead text under <c>direct</c> — and that source is the next one to be added.
    /// <para>
    /// So no shape has such a field yet, and every caller's several-homes branch is unreachable as
    /// this ships. The list is still what the signature should be: the alternative is a lookup that
    /// answers correctly only while the coincidence holds, and it holds for one more release.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HomeBlocksOf(string field) =>
        BlockFields
            .Where(block => block.Value.ContainsKey(field))
            .Select(block => block.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The fields <paramref name="writtenKey"/> looks like a misspelling of, each with the block it
    /// lives in — empty when it resembles no field.
    /// </summary>
    /// <remarks>
    /// The fuzzy counterpart of <see cref="HomeBlocksOf"/>, asked only after that has come back
    /// empty. Spelled correctly, a field written at an entry's root is answered with the block it
    /// belongs in; one letter off, it used to be answered with the list of keys valid at the root,
    /// which cannot contain the word the developer was reaching for — the field is a level down. So
    /// the reader got a handful of words, none of them the answer, and no hint the key existed at
    /// all.
    /// <para>
    /// Every tie is returned, not the closest one, for the two reasons ties happen: a typo can sit
    /// the same distance from two differently-named fields, which happens now, and a field name can
    /// be declared by more than one block, which no shape does yet — see
    /// <see cref="HomeBlocksOf"/>. <see cref="NearMiss.Nearest"/> orders the first kind by the
    /// spelling it was given; the second it cannot order at all, since the spellings are equal, so
    /// the block is ordered on here too. The result is the same on every run either way.
    /// </para>
    /// </remarks>
    public IReadOnlyList<(string Field, string Block)> NearMissFieldsOf(string writtenKey) =>
        NearMiss.Nearest(
                writtenKey,
                BlockFields.SelectMany(block => block.Value.Keys.Select(field => (Field: field, Block: block.Key))),
                candidate => candidate.Field)
            // Ordered by block as well as field, because Nearest can only order by what it was
            // given: two candidates sharing a field name would keep the order they arrived in,
            // which is Type.GetProperties()'s and not one the CLR promises to keep stable. No shape
            // declares such a field today, so this changes no message now — it is here because the
            // one that will, a source block's connectionString, is the next thing added, and a
            // reordering nothing guarantees is not a thing to notice from a message.
            .OrderBy(candidate => candidate.Field, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Block, StringComparer.Ordinal)
            .ToArray();

    private static DeveloperConfigShape Of<TEntry>(
        string kind, string noun, IEnumerable<string> sourceNames) =>
        new(typeof(TEntry), kind, noun, sourceNames);
}
