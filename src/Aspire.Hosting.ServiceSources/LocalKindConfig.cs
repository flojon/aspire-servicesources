using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Parses a service's opaque per-kind config block (<see cref="Config.ServiceMetadata.KindConfig"/>,
/// as handed to <see cref="ILocalResourceKind.Resolve"/>) into a strongly-typed options object.
/// Satellite packages (e.g. a JavaScript or Java local-kind implementation) call this instead of
/// working with the raw <c>Dictionary&lt;object, object&gt;</c> directly.
/// </summary>
public static class LocalKindConfig
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();

    // Deliberately NOT IgnoreUnmatchedProperties(): the kind block is the one block the loader's own
    // unknown-property checks can't validate (it's opaque to core), so this is the only place a typo
    // like "runScrip:" can be caught instead of silently leaving the option at its default.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="rawConfig"/> is <see langword="null"/>
    /// (i.e. the service's yaml had no block matching its <c>kind</c>). When
    /// <paramref name="rawConfig"/> is already an instance of <typeparamref name="T"/> — as when a
    /// service was declared in code via <c>WithKind(kind, options)</c> rather than yaml — it is
    /// returned unchanged, with no parsing. Otherwise it is round-tripped back through yaml rather
    /// than reflected over directly, since it arrives as an untyped
    /// <c>Dictionary&lt;object, object&gt;</c> produced by YamlDotNet's dynamic deserialization.
    /// Pass <paramref name="serviceName"/> so a malformed block names the offending service.
    /// </summary>
    /// <remarks>
    /// The already-typed instance this method returns for a code-declared service is the exact
    /// object the caller of <c>WithKind</c> passed in, shared across every
    /// <c>Validate</c>/<c>Resolve</c>/<c>ResolveDeferred</c> call that reads it — local-kind
    /// implementations must treat the returned options object as read-only and must not mutate or
    /// retain a reference expecting it to stay unchanged by other code (design finding 6). This
    /// mirrors the existing yaml-sourced case, where the returned instance is freshly deserialized
    /// per call and safe to treat the same way.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="rawConfig"/> is an instance of some type other than <typeparamref name="T"/>
    /// that could only have come from code (not yaml) — the wrong kind's options object was passed.
    /// Otherwise, for a yaml-sourced block: it isn't a mapping (usually an indentation slip), or it
    /// contains a property that <typeparamref name="T"/> doesn't define (usually a typo).
    /// </exception>
    public static T? Parse<T>(object? rawConfig, string? serviceName = null) where T : class
    {
        if (rawConfig is null)
        {
            return null;
        }

        // Branch 1: already the right type — a WithKind(kind, options) call passed a real T. Nothing
        // to parse; the caller's instance is returned as-is. Per design finding 6, the caller must not
        // retain or mutate it afterwards — every shipped kind (Java/JavaScript) already respects this
        // by projecting a fresh immutable record per call, so nothing here needs to defensively copy.
        if (rawConfig is T alreadyTyped)
        {
            return alreadyTyped;
        }

        // Branch 1b: a guest language's WithKind block, which Aspire's Type System marshals across as
        // a JsonObject — not the non-generic IDictionary the yaml path tests for, so without this it
        // is rejected as "a list". Yaml is a superset of JSON, so the same strict deserializer reads
        // it, keeping the unknown-property check. JsonArray/JsonValue fall through to the shape
        // messages.
        if (rawConfig is JsonObject jsonObject)
        {
            return Deserialize<T>(jsonObject.ToJsonString(), serviceName);
        }

        // Branch 2: came from code, but for a *different* options type — a WithKind(kind, options) call
        // passed the wrong kind's options object. CameFromCode must mirror the branch below exactly:
        // anything YamlDotNet's dynamic deserialization can produce (string, boxed primitive, IList,
        // IDictionary) is NOT this branch, even if T doesn't match — those fall through to the existing
        // scalar/list message instead, unchanged.
        if (CameFromCode(rawConfig))
        {
            throw new ServiceSourcesConfigurationException(
                $"{Prefix(serviceName)}the per-kind config block is a '{rawConfig.GetType().Name}', but this " +
                $"kind expects '{typeof(T).Name}'. Pass the options type this kind's registration method " +
                "documents, not another kind's.");
        }

        if (rawConfig is not System.Collections.IDictionary)
        {
            // A sequence under the kind key stringifies to its CLR type name, which points the reader
            // nowhere — name the shape instead, and only quote the value when it really is a scalar.
            var found = rawConfig is System.Collections.IEnumerable and not string
                ? "a list"
                : $"the scalar '{rawConfig}'";

            throw new ServiceSourcesConfigurationException(
                $"{Prefix(serviceName)}the per-kind config block must be a block of key/value pairs, " +
                $"but found {found}. {IndentationAdvice}");
        }

        return Deserialize<T>(Serializer.Serialize(rawConfig), serviceName);
    }

    /// <summary>
    /// The trailing sentence of the shape-rejection message above: sound advice for a yaml-sourced
    /// block, meaningless for a code-declared one. Exposed so <c>LocalProjectSource</c> can find and
    /// replace it once it knows the service's origin, since <see cref="Parse{T}"/> itself has no
    /// <see cref="Config.Catalog.CatalogOrigin"/> to consult.
    /// </summary>
    internal const string IndentationAdvice = "Check the indentation under the kind's key.";

    /// <summary>
    /// Re-renders a shape-rejection <see cref="ServiceSourcesConfigurationException"/> for a
    /// code-declared service, replacing <see cref="IndentationAdvice"/> with wording that doesn't
    /// point at a yaml file. Deliberately does not chain <paramref name="ex"/> as an
    /// <see cref="Exception.InnerException"/>: <see cref="ServiceSourcesConfigurationException.Describe"/>
    /// prints inner-exception messages as "caused by" lines, which would print the discarded
    /// yaml advice right back into the output this method exists to remove.
    /// </summary>
    internal static ServiceSourcesConfigurationException RewriteIndentationAdviceForCodeOrigin(
        ServiceSourcesConfigurationException ex) =>
        new(
            ex.Message.Replace(
                IndentationAdvice,
                "This block was passed to WithKind in code, not read from a file — pass the options object " +
                "this kind's registration method documents, not a list or a raw value.",
                StringComparison.Ordinal));

    /// <summary>
    /// Reads a block already in yaml text: yaml's own untyped shape re-serialized, or a guest
    /// language's JSON, which is valid yaml.
    /// </summary>
    private static T? Deserialize<T>(string yaml, string? serviceName) where T : class
    {
        try
        {
            return Deserializer.Deserialize<T>(yaml);
        }
        catch (YamlException ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"{Prefix(serviceName)}the per-kind config block is not valid: " +
                (ex.InnerException ?? ex).Message,
                ex);
        }
    }

    /// <summary>
    /// Whether <paramref name="rawConfig"/> can only have come from a <c>WithKind(kind, options)</c>
    /// call — i.e. it is none of the shapes YamlDotNet's dynamic deserialization produces. Mirrors the
    /// scalar/list test above rather than checking <see cref="System.Collections.IList"/> directly, so
    /// the two branches classify every input the same way (design finding 6).
    /// </summary>
    private static bool CameFromCode(object rawConfig) =>
        rawConfig is not System.Collections.IDictionary
        && rawConfig is not (System.Collections.IEnumerable and not string)
        && rawConfig is not string
        && !YamlScalarTypes.Contains(rawConfig.GetType())
        && !rawConfig.GetType().IsPrimitive;

    // The CLR types YamlDotNet's dynamic (untyped) deserialization can box a scalar into that
    // Type.IsPrimitive (checked alongside this set in CameFromCode) doesn't already cover — i.e.
    // non-primitive value types yaml still produces, like decimal and DateTime. Getting this list
    // subtly wrong would misclassify a legitimate yaml scalar as "came from code", producing a
    // confusing wrong-type error instead of the existing scalar/list message.
    private static readonly HashSet<Type> YamlScalarTypes =
    [
        typeof(decimal),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(Guid),
        typeof(TimeSpan),
    ];

    private static string Prefix(string? serviceName) =>
        serviceName is null ? "" : $"Service '{serviceName}': ";
}
