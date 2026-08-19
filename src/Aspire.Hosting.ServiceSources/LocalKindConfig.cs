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

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="rawConfig"/> is <see langword="null"/>
    /// (i.e. the service's yaml had no block matching its <c>kind</c>). Round-trips
    /// <paramref name="rawConfig"/> back through yaml rather than reflecting over it directly,
    /// since it arrives as an untyped <c>Dictionary&lt;object, object&gt;</c> produced by
    /// YamlDotNet's dynamic deserialization.
    /// </summary>
    public static T? Parse<T>(object? rawConfig) where T : class
    {
        if (rawConfig is null)
        {
            return null;
        }

        var yaml = Serializer.Serialize(rawConfig);
        return Deserializer.Deserialize<T>(yaml);
    }
}
