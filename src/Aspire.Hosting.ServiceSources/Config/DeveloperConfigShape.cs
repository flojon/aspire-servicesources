using System.Reflection;
using Aspire.Hosting.ServiceSources.Messages;

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
        Of<ServiceDeveloperConfig>(
            "Service", "service", ["repository", "url", "kubernetes", "container", "path", "disabled"],
            // The block-name counterpart of the retired source value "local": kept working as
            // ServiceDeveloperConfig.Repository's deprecated alias (see
            // ServiceDeveloperConfig.ReconcileRepositoryAlias), so it is still a home HomeBlocksOf
            // has to report — just never the one a message picks to illustrate.
            deprecatedBlockNames: ["local"]);

    /// <summary>
    /// A backing-service entry, keyed under <see cref="DeveloperConfiguration.BackingServicesKey"/>.
    /// </summary>
    public static DeveloperConfigShape BackingService { get; } =
        Of<BackingServiceDeveloperConfig>(
            "Backing service", "backing service", ["local", "direct", "kubernetes"]);

    /// <summary>
    /// A repository entry, keyed under <see cref="DeveloperConfiguration.RepositoriesKey"/> — a
    /// developer's override for a whole group of services (#291) sharing a checkout, rather than for
    /// one service's own <c>repository</c> block. Its <c>source</c> takes <c>path</c> or <c>repository</c> only.
    /// </summary>
    public static DeveloperConfigShape Repository { get; } =
        Of<RepositoryDeveloperConfig>("Repository", "repository", ["repository", "path"]);

    private DeveloperConfigShape(
        Type entry,
        string kind,
        string noun,
        IEnumerable<string> sourceNames,
        IEnumerable<string> deprecatedBlockNames)
    {
        Entry = entry;
        Kind = kind;
        Noun = noun;
        SourceNames = sourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        DeprecatedBlockNames = deprecatedBlockNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Blocks = entry.GetProperties()
            .Where(p => DeveloperConfigField.BlockFieldsOf(p.PropertyType) is not null)
            .ToArray();

        RootKeys = DeveloperConfigField.BlockFieldsOf(entry)!.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Read through the same method the validator asks about a block field with, rather than
        // enumerating the properties again here: two derivations of "what may be written inside
        // this" agree only until one of them learns something, and the one that learned to leave
        // out a computed property did.
        BlockFields = Blocks.ToDictionary(
            block => block.Name,
            block => DeveloperConfigField.BlockFieldsOf(block.PropertyType)!,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The type an entry of this kind binds to.</summary>
    /// <remarks>
    /// Exposed so that a check can ask about the entry's own properties and not only about what is
    /// inside its blocks — <c>source</c> lives here and is handled outside the block walk, so a
    /// field-level rule placed on it would be inert. Nothing in the walk needs this; the test that
    /// pins where <see cref="NoSurroundingWhitespaceAttribute"/> may be declared does.
    /// </remarks>
    public Type Entry { get; }

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

    /// <summary>
    /// Block names on this shape that are deprecated aliases of another block — <c>local</c> for
    /// <c>repository</c>, see <see cref="ServiceDeveloperConfig.ReconcileRepositoryAlias"/>.
    /// </summary>
    /// <remarks>
    /// Never changes whether a block is valid, or which homes <see cref="HomeBlocksOf"/> reports for
    /// a field — a deprecated block still binds and is still named alongside its current spelling.
    /// It changes only which single home <see cref="HomeBlocksOf"/> orders first, which is what a
    /// message picks when it has to illustrate one paste-ready fix rather than list every valid
    /// answer: recommending the spelling this shape means to retire would undo the deprecation for
    /// anyone who follows the message's own advice.
    /// </remarks>
    public IReadOnlySet<string> DeprecatedBlockNames { get; }

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
    /// Block name to the keys valid inside it, each carrying the property its value binds to.
    /// </summary>
    /// <remarks>
    /// The property travels with the name because a key can be valid and its value still
    /// unbindable — a <c>port</c> written as <c>"abc"</c> — and the binder answers that with an
    /// exception naming a CLR type rather than the field. Checking it here keeps every complaint
    /// about an entry arriving in the same shape, at the same moment.
    /// <para>
    /// The whole property rather than its type alone, because a field can also be <em>declared</em>
    /// with a rule the walk has to see. <see cref="NoSurroundingWhitespaceAttribute"/> is the first:
    /// it sits on the property, and a dictionary keyed to types would have thrown it away one step
    /// before the only code that asks about it.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PropertyInfo>> BlockFields { get; }

    /// <summary>
    /// The blocks that declare a field named <paramref name="field"/>, current spellings before any
    /// deprecated alias and in name order within each group, or empty when none does. Used to turn
    /// "that key does not go there" into "here is where it goes".
    /// </summary>
    /// <remarks>
    /// A list rather than a single answer, because a field name can be declared by more than one
    /// block: a backing service's <c>connectionString</c> lives in both <c>direct</c> and
    /// <c>kubernetes</c>, since each source wants its own template — the <c>kubernetes</c> one
    /// carries a <c>${port}</c> placeholder that <c>direct</c> has nothing to resolve. Naming only
    /// the first would send a developer to the block they are not using.
    /// <para>
    /// A deprecated alias sorts after every current spelling rather than joining the plain
    /// alphabetical order, because the caller most likely to look only at the first entry
    /// (<see cref="DeveloperConfigValidator"/>'s exact-match illustration) has to land on a spelling
    /// this shape isn't trying to retire. <c>local</c> sorting ahead of <c>repository</c> is exactly
    /// the ordering that bug looks like: alphabetically first, and deprecated.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> HomeBlocksOf(string field) =>
        BlockFields
            .Where(block => block.Value.ContainsKey(field))
            .Select(block => block.Key)
            .OrderBy(block => DeprecatedBlockNames.Contains(block))
            .ThenBy(block => block, StringComparer.Ordinal)
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
    /// the same distance from two differently-named fields, and a field name can be declared by
    /// more than one block — a backing service's <c>connectionString</c> is, see
    /// <see cref="HomeBlocksOf"/>. <see cref="NearMiss.Nearest"/> orders the first kind by the
    /// spelling it was given; the second it cannot order at all, since the spellings are equal, so
    /// the block is ordered on here too — which is what keeps a near miss of
    /// <c>connectionString</c>, declared by both <c>direct</c> and <c>kubernetes</c>, naming those
    /// two blocks in the same order on every run.
    /// </para>
    /// </remarks>
    public IReadOnlyList<(string Field, string Block)> NearMissFieldsOf(string writtenKey) =>
        NearMiss.Nearest(
                writtenKey,
                BlockFields.SelectMany(block => block.Value.Keys.Select(field => (Field: field, Block: block.Key))),
                candidate => candidate.Field)
            // Ordered by block as well as field, because Nearest can only order by what it was
            // given: two candidates sharing a field name would keep the order they arrived in,
            // which is Type.GetProperties()'s and not one the CLR promises to keep stable. A
            // backing service's connectionString is declared by two blocks, so without this a
            // message naming both could reorder them between runs for no reason a reader could see.
            .OrderBy(candidate => candidate.Field, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Block, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Throws unless <paramref name="value"/> is one of <see cref="SourceNames"/> — the one check
    /// both a yaml <c>defaultSource:</c> entry (<see cref="ServiceCatalogLoader"/>) and its code
    /// twin (<see cref="Catalog.ServiceDefinitionBuilder.WithDefaultSource"/>) run, so the message
    /// naming the valid values can't drift between the two authoring paths.
    /// </summary>
    /// <param name="label">
    /// Already formatted to name what's wrong — <c>"Service 'orders': defaultSource value 'foo'"</c>
    /// or <c>"Service 'orders': WithDefaultSource('foo')"</c> — so this stays agnostic to which
    /// caller it's validating for.
    /// </param>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="value"/> is not one of <see cref="SourceNames"/>.
    /// </exception>
    public void ValidateSourceName(Raw label, string value)
    {
        if (!SourceNames.Contains(value))
        {
            // Retired, not unknown: a catalog's defaultSource/WithDefaultSource("local") deserves the
            // same named migration AddService's dispatch gives a developer's source: "local", rather
            // than the generic message below. Only where the value is not a source of this shape —
            // a backing service's "local" is its own, current, default source.
            ThrowIfRetiredSource(label, value, Raw.Literal("Change it to 'repository'."));

            throw ServiceSourcesConfigurationException.For(
                $"{label} is not a valid source. Expected one of: {Raw.Join(", ", SourceNames.Select(Raw.Escaped))}.");
        }
    }

    /// <summary>
    /// The source to suggest for a value written where a whole entry was expected: the value itself
    /// if this shape has it, <c>"repository"</c> for the retired <c>"local"</c> this shape no longer
    /// has, or <see langword="null"/> when it names no source.
    /// </summary>
    public string? SuggestedSourceFor(string value) =>
        SourceNames.Contains(value) ? value
        : string.Equals(value, RetiredSource, StringComparison.OrdinalIgnoreCase) ? RetiredSourceReplacement
        : null;

    private const string RetiredSource = "local";

    private const string RetiredSourceReplacement = "repository";

    /// <summary>
    /// Throws the migration error for the retired source name <c>"local"</c>, now <c>"repository"</c>,
    /// if <paramref name="value"/> is it (case-insensitively, as source names are matched); otherwise
    /// returns. The one place the rename is explained, shared by a developer's <c>source</c> and a
    /// catalog's <c>defaultSource</c>/<c>WithDefaultSource</c>.
    /// </summary>
    /// <param name="subject">What names the value, as the sentence's subject — e.g. <c>"Service 'orders': source 'local'"</c>.</param>
    /// <param name="remedy">The fix, in the caller's terms.</param>
    public static void ThrowIfRetiredSource(Raw subject, string value, Raw remedy)
    {
        if (!string.Equals(value, RetiredSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw ServiceSourcesConfigurationException.For(
            $"{subject} was renamed to 'repository' — same behavior (clone the catalog's 'repository:' url, "
            + $"reconcile onto 'ref'), new name, so it doesn't read as the same word "
            + $"'{Raw.Literal(DeveloperConfiguration.FileName)}' uses for something else. {remedy}");
    }

    private static DeveloperConfigShape Of<TEntry>(
        string kind, string noun, IEnumerable<string> sourceNames,
        IEnumerable<string>? deprecatedBlockNames = null) =>
        new(typeof(TEntry), kind, noun, sourceNames, deprecatedBlockNames ?? []);
}
