using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>The outcome of a save: either it landed, or <see cref="Error"/> says why it did not.</summary>
internal sealed record SaveResult(bool Saved, string? Error);

/// <summary>Records a developer's source choice for one service in <c>servicesources.local.json</c>.</summary>
internal interface IDeveloperConfigWriter
{
    /// <param name="serviceName">The service as the catalog spells it.</param>
    /// <param name="source">The chosen source.</param>
    /// <param name="fields">Follow-up answers; a <c>:</c> in a key nests the value one object deeper.</param>
    /// <param name="validateMerged">
    /// Checks the service's entry as it would be written, existing fields included; a message refuses the save.
    /// </param>
    Task<SaveResult> SaveAsync(
        string serviceName,
        string source,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken,
        Func<JsonObject, string?>? validateMerged = null);
}

/// <summary>
/// Merges a choice into <c>servicesources.local.json</c> beside the AppHost, leaving everything else
/// in the file as it was.
/// </summary>
internal sealed class JsonFileDeveloperConfigWriter : IDeveloperConfigWriter
{
    /// <summary>Largest file read; anything bigger is refused rather than buffered.</summary>
    public const int MaxBytes = 256 * 1024;

    // Process-wide so two writers over one file cannot interleave their read-modify-write.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // The configuration reader accepts these, so a file that loads today must also be savable.
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public JsonFileDeveloperConfigWriter(string appHostDirectory)
    {
        _path = Path.Combine(appHostDirectory, DeveloperConfiguration.FileName);
    }

    public async Task<SaveResult> SaveAsync(
        string serviceName,
        string source,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken,
        Func<JsonObject, string?>? validateMerged = null)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? scratch = null;

        try
        {
            var root = ReadRoot(out var error);
            if (root is null)
            {
                return new SaveResult(false, error);
            }

            var services = GetOrAddObject(root, DeveloperConfigFileSource.FileServicesKey);
            if (services is null)
            {
                return new SaveResult(false, $"'{DeveloperConfigFileSource.FileServicesKey}' in {_path} is not an object.");
            }

            var entry = GetOrAddObject(services, serviceName);
            if (entry is null)
            {
                return new SaveResult(false, $"The entry for '{serviceName}' in {_path} is not an object.");
            }

            Set(entry, "source", source);

            foreach (var (key, value) in fields)
            {
                var target = entry;
                var parts = key.Split(':');
                foreach (var part in parts[..^1])
                {
                    target = GetOrAddObject(target, part, replaceScalar: true)!;
                }

                Set(target, parts[^1], value);
            }

            if (validateMerged?.Invoke(entry) is { } invalid)
            {
                return new SaveResult(
                    false, $"{invalid} The entry for '{serviceName}' in {_path} would not load, so nothing was saved.");
            }

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            scratch = Path.Combine(directory, $".incoming-{DeveloperConfiguration.FileName}-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(
                scratch, root.ToJsonString(WriteOptions) + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            PrepareMarker.MoveOntoMarker(scratch, _path);
            scratch = null;

            return new SaveResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveResult(false, $"Could not write {_path}: {ex.Message}");
        }
        finally
        {
            if (scratch is not null)
            {
                PrepareMarker.TryDelete(scratch);
            }

            Gate.Release();
        }
    }

    private JsonObject? ReadRoot(out string? error)
    {
        error = null;

        byte[] bytes;
        switch (BoundedFile.TryRead(_path, MaxBytes, out bytes))
        {
            case BoundedReadStatus.Missing:
                return new JsonObject();
            case BoundedReadStatus.NotRegularFile:
                error = $"{_path} is not a regular file.";
                return null;
            case BoundedReadStatus.TooLarge:
                error = $"{_path} is larger than {MaxBytes} bytes.";
                return null;
        }

        // The configuration reader accepts a BOM; JsonNode.Parse does not.
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (bytes.AsSpan().StartsWith(bom))
        {
            bytes = bytes[bom.Length..];
        }

        try
        {
            if (HasComments(bytes))
            {
                // Comments cannot survive a JsonNode round trip, so refuse rather than drop them.
                error = $"{_path} contains comments, which saving would remove. Edit the file by hand instead.";
                return null;
            }

            // The configuration reader rejects these too; JsonNode would only fail later, on first access.
            if (FindDuplicateKey(bytes) is { } duplicate)
            {
                error = $"{_path} is not valid JSON: the key '{duplicate}' appears twice in one object (keys are case-insensitive).";
                return null;
            }

            var node = JsonNode.Parse(bytes, nodeOptions: null, ParseOptions);
            if (node is JsonObject root)
            {
                return root;
            }

            error = $"{_path} does not contain a JSON object at its root.";
        }
        catch (JsonException ex)
        {
            error = $"{_path} is not valid JSON: {ex.Message}";
        }

        return null;
    }

    private static bool HasComments(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Comment)
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindDuplicateKey(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var objects = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (!objects.Peek().Add(name))
                    {
                        return name;
                    }

                    break;
            }
        }

        return null;
    }

    // Matches an existing key whatever its case, so the developer's spelling is kept.
    private static string? FindKey(JsonObject parent, string name) =>
        parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static JsonObject? GetOrAddObject(JsonObject parent, string name, bool replaceScalar = false)
    {
        var key = FindKey(parent, name);
        if (key is not null)
        {
            var node = parent[key];
            if (node is JsonObject existing)
            {
                return existing;
            }

            // A null loads as an absent key, so it is replaced rather than refused.
            if (node is not null && !replaceScalar)
            {
                return null;
            }
        }

        var created = new JsonObject();
        parent[key ?? name] = created;
        return created;
    }

    private static void Set(JsonObject parent, string name, string value) =>
        parent[FindKey(parent, name) ?? name] = value;
}
