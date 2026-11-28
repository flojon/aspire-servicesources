using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Where an AppHost's managed checkouts live: the default <c>&lt;AppHost&gt;\.servicesources</c>, or a
/// <c>&lt;root&gt;\&lt;hash&gt;</c> directory this tool owns under <see cref="DeveloperConfiguration.CheckoutsDirectoryKey"/>.
/// </summary>
/// <remarks>
/// Computing one is string work only, so a checkout path can be named before anything exists, which
/// deferral depends on. A bad setting is carried in <see cref="Problem"/> and thrown by the callers
/// about to act, never by the ones that only ask.
/// </remarks>
/// <param name="OwnedDirectory">The directory the tool writes its own files into; checkouts sit in its <c>checkouts</c> child.</param>
/// <param name="Problem">A lexical configuration error; the other paths then hold the default layout as a placeholder.</param>
internal sealed record CheckoutsLocation(
    string AppHostDirectory,
    string OwnedDirectory,
    string CheckoutsRoot,
    bool IsExternal,
    string? ConfiguredValue,
    Raw? Problem)
{
    internal const string OwnerFileName = "owner.json";

    internal const string EnvironmentVariable = "ServiceSources__CheckoutsDirectory";

    internal const string SuggestedDirectory = @"C:\ss";

    private const int HashLength = 10;

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CheckoutsLocation> Cache = new();

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static CheckoutsLocation Default(string appHostDirectory)
    {
        var owned = ToolDirectory.PathIn(appHostDirectory);
        return new CheckoutsLocation(
            appHostDirectory, owned, Path.Combine(owned, LocalGitCheckout.CheckoutsDirectoryName), false, null, null);
    }

    public static CheckoutsLocation For(IDistributedApplicationBuilder builder)
    {
        DeveloperConfigFileSource.EnsureRegistered(builder);

        return Cache.GetValue(
            builder,
            static b => Resolve(b.AppHostDirectory, b.Configuration[DeveloperConfiguration.CheckoutsDirectoryKey]));
    }

    public static CheckoutsLocation Resolve(string appHostDirectory, string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return Default(appHostDirectory);
        }

        var fallback = Default(appHostDirectory) with { ConfiguredValue = configuredValue };
        var key = Raw.Literal(DeveloperConfiguration.CheckoutsDirectoryKey);

        if (!PathForms.IsLocalAbsolute(configuredValue))
        {
            return fallback with
            {
                Problem = Raw.Compose(
                    $"'{key}' is '{Raw.Display(configuredValue)}', which is not an absolute local directory. It must be a full path on a local drive, such as '{Raw.Literal(SuggestedDirectory)}'; relative, drive-relative ('C:foo'), network (UNC) and device paths are not accepted."),
            };
        }

        string root;
        string appHost;
        try
        {
            root = ManagedGitDirectory.Normalize(configuredValue);
            appHost = ManagedGitDirectory.Normalize(appHostDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return fallback with
            {
                Problem = Raw.Compose($"'{key}' is '{Raw.Display(configuredValue)}', which is not a usable path ({Raw.Cause(ex)})."),
            };
        }

        // A filesystem root contains everything on its drive, yet its hashed child is nowhere near the source.
        var isFilesystemRoot = string.Equals(Path.GetPathRoot(root), root, ManagedGitDirectory.PathComparison);
        if ((!isFilesystemRoot && IsSameOrInside(appHost, root)) || string.Equals(appHost, root, ManagedGitDirectory.PathComparison))
        {
            return fallback with
            {
                Problem = Raw.Compose(
                    $"'{key}' is '{Raw.Display(configuredValue)}', which resolves to '{Raw.Display(root)}': that is the AppHost directory '{Raw.Display(appHost)}' or a directory containing it, so checkouts would land among its source. Choose a directory outside it."),
            };
        }

        if (IsSameOrInside(root, ToolDirectory.PathIn(appHost)))
        {
            return fallback with
            {
                Problem = Raw.Compose(
                    $"'{key}' is '{Raw.Display(configuredValue)}', which resolves to '{Raw.Display(root)}', inside the AppHost's own '{Raw.Literal(ToolDirectory.Name)}' directory where the default checkouts live. Choose a directory outside it."),
            };
        }

        var owned = Path.Combine(root, HashOf(appHostDirectory));
        return new CheckoutsLocation(
            appHostDirectory, owned, Path.Combine(owned, LocalGitCheckout.CheckoutsDirectoryName), true, configuredValue, null);
    }

    /// <summary>The first ten hex digits of SHA-256 over the AppHost path, in the form <see cref="ManagedGitDirectory.HashOf"/> uses.</summary>
    public static string HashOf(string appHostDirectory)
    {
        var normalized = ManagedGitDirectory.Normalize(appHostDirectory).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..HashLength];
    }

    public void ThrowIfProblem()
    {
        if (Problem is { } problem)
        {
            throw ServiceSourcesConfigurationException.For($"{problem}");
        }
    }

    /// <summary>
    /// Creates what a clone is about to land in, and returns the owned directory: its <c>.gitignore</c>,
    /// the build barrier, and for an external location the ownership record.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">The setting is invalid, unusable, or names a directory this tool did not create.</exception>
    public string EnsureOwned()
    {
        ThrowIfProblem();

        if (IsExternal)
        {
            EnsureOwnership();
        }

        var owned = ToolDirectory.EnsureAt(OwnedDirectory);
        CheckoutBuildBarrier.Ensure(owned);
        return owned;
    }

    private void EnsureOwnership()
    {
        var root = Path.GetDirectoryName(OwnedDirectory)!;
        var key = Raw.Literal(DeveloperConfiguration.CheckoutsDirectoryKey);

        try
        {
            if (File.Exists(root))
            {
                throw ServiceSourcesConfigurationException.For(
                    $"'{key}' is '{Raw.Display(root)}', which is a file rather than a directory.");
            }

            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw ServiceSourcesConfigurationException.For(
                $"'{key}' is '{Raw.Display(root)}', which cannot be created as a directory: {Raw.Cause(ex)}", ex);
        }

        if (File.Exists(OwnedDirectory) || Directory.Exists(OwnedDirectory))
        {
            RequireOwnRecord();
            return;
        }

        var staging = $"{OwnedDirectory}.tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllText(
                Path.Combine(staging, OwnerFileName),
                JsonSerializer.Serialize(new OwnerFile(AppHostDirectory), SerializerOptions));

            try
            {
                Directory.Move(staging, OwnedDirectory);
            }
            catch (IOException) when (Directory.Exists(OwnedDirectory) || File.Exists(OwnedDirectory))
            {
                // A concurrent run of this AppHost may have created it first; its record decides.
                RequireOwnRecord();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ServiceSourcesConfigurationException.For(
                $"The checkouts directory '{Raw.Display(OwnedDirectory)}' under '{key}' could not be created: {Raw.Cause(ex)}", ex);
        }
        finally
        {
            try
            {
                ScratchDirectory.DeleteTree(staging);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover staging directory is ignored by every reader.
            }
        }
    }

    private void RequireOwnRecord()
    {
        var key = Raw.Literal(DeveloperConfiguration.CheckoutsDirectoryKey);

        if (Directory.Exists(OwnedDirectory)
            && !new DirectoryInfo(OwnedDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint)
            && ReadOwner(OwnedDirectory) is { } owner
            && string.Equals(
                ManagedGitDirectory.Normalize(owner), ManagedGitDirectory.Normalize(AppHostDirectory), ManagedGitDirectory.PathComparison))
        {
            return;
        }

        throw ServiceSourcesConfigurationException.For(
            $"'{Raw.Display(OwnedDirectory)}', under '{key}', already exists and was not created by this tool for the AppHost '{Raw.Display(AppHostDirectory)}': it is a file, a link, or a directory without this AppHost's record. It is not used or changed. Delete it, or point '{key}' elsewhere.");
    }

    private static string? ReadOwner(string directory)
    {
        try
        {
            var record = JsonSerializer.Deserialize<OwnerFile>(
                File.ReadAllText(Path.Combine(directory, OwnerFileName)), SerializerOptions);
            return string.IsNullOrWhiteSpace(record?.AppHost) ? null : record.AppHost;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        if (string.Equals(path, parent, ManagedGitDirectory.PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, ManagedGitDirectory.PathComparison);
    }

    private sealed record OwnerFile([property: JsonPropertyName("appHost")] string AppHost);
}
