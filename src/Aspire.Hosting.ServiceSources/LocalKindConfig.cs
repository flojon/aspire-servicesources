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
    /// (i.e. the service's yaml had no block matching its <c>kind</c>). Round-trips
    /// <paramref name="rawConfig"/> back through yaml rather than reflecting over it directly,
    /// since it arrives as an untyped <c>Dictionary&lt;object, object&gt;</c> produced by
    /// YamlDotNet's dynamic deserialization. Pass <paramref name="serviceName"/> so a malformed
    /// block names the offending service.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The block isn't a mapping (usually an indentation slip), or it contains a property that
    /// <typeparamref name="T"/> doesn't define (usually a typo).
    /// </exception>
    public static T? Parse<T>(object? rawConfig, string? serviceName = null) where T : class
    {
        if (rawConfig is null)
        {
            return null;
        }

        if (rawConfig is not System.Collections.IDictionary)
        {
            throw new ServiceSourcesConfigurationException(
                $"{Prefix(serviceName)}the per-kind config block must be a block of key/value pairs, " +
                $"but found the scalar '{rawConfig}'. Check the indentation under the kind's key.");
        }

        var yaml = Serializer.Serialize(rawConfig);

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

    private static string Prefix(string? serviceName) =>
        serviceName is null ? "" : $"Service '{serviceName}': ";
}
