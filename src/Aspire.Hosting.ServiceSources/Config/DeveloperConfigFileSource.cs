using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Puts <c>servicesources.local.json</c> into the AppHost's own configuration chain, once per
/// builder, as the <em>lowest</em>-precedence source — so every standard provider can override an
/// entry with no file edit.
/// </summary>
/// <remarks>
/// <para>
/// Every entry point an AppHost can call registers this before doing anything else, which is what
/// keeps the chain complete rather than merely eventually complete. The entries land in the
/// AppHost's live <see cref="IConfiguration"/> under <see cref="DeveloperConfiguration.ServicesKey"/>,
/// so the AppHost can read them too, and registering on the first read of our own — a side effect
/// of the first <c>AddService()</c> — made that read order-dependent: the same key read one line
/// earlier saw the chain without this layer, which for a developer who configures everything in the
/// file is <c>null</c> rather than an error.
/// </para>
/// <para>
/// It stays invisible to the AppHost author either way: registration is something an entry point
/// does, never something the author arranges.
/// </para>
/// </remarks>
internal static class DeveloperConfigFileSource
{
    /// <summary>The key the file uses at its own root, before its entries are re-rooted below.</summary>
    private const string FileServicesKey = "services";

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, Registration> Registrations = new();

    /// <summary>
    /// How far from <c>services</c> a root key can be spelled and still be taken for it. Two edits
    /// covers a dropped or doubled letter and a transposed pair, which is the whole of what a
    /// misspelling of an eight-letter word usually is.
    /// </summary>
    /// <remarks>
    /// Generous rather than exact because of where the answer is used: only in a failure that is
    /// already being thrown, and only when nothing at all is configured. A false positive adds a
    /// sentence about a key to an error message; it cannot cost a working file anything.
    /// </remarks>
    private const int NearMissEdits = 2;

    /// <summary>
    /// Registers the file on <paramref name="builder"/>'s configuration if it isn't registered
    /// already. Cheap and safe to call from every entry point, and from every read.
    /// </summary>
    public static void EnsureRegistered(IDistributedApplicationBuilder builder) =>
        // The factory has to stay free of side effects. ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, but every caller is handed
        // the one it kept — so the registration itself, guarded by that instance, happens once even
        // though the instance may be built twice.
        Registrations.GetValue(builder, static _ => new Registration()).Register(builder);

    /// <summary>
    /// One builder's slot in <see cref="Registrations"/>. The insert must happen exactly once: a
    /// duplicate provider in the chain is a second copy of the file's values, inserted from a second
    /// thread into a list the first insert is mutating, and each insert disposes and rebuilds every
    /// provider on it.
    /// </summary>
    private sealed class Registration
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        private bool _registered;

        /// <summary>
        /// A registration that throws — a malformed file is the case — leaves the slot unset, so the
        /// next caller tries again and fails the same way, rather than the first entry point
        /// swallowing the failure on behalf of every later one.
        /// </summary>
        public void Register(IDistributedApplicationBuilder builder)
        {
            lock (_gate)
            {
                if (_registered)
                {
                    return;
                }

                // Reading the file is the part that can fail on the file's own account, and it
                // touches nothing on the builder, so a malformed file throws with the slot still
                // unset and the chain still untouched.
                var source = ReadFileSource(Path.Combine(builder.AppHostDirectory, DeveloperConfiguration.FileName));

                // Set before the insert rather than after. Inserting mutates the source list and
                // then rebuilds every provider on it, so a fault raised by some unrelated provider's
                // reload surfaces here with ours already in the chain; a retry after that would add
                // a second copy, which is the one outcome this slot exists to prevent.
                _registered = true;

                builder.Configuration.Sources.Insert(0, source);
            }
        }
    }

    /// <remarks>
    /// The file's own root is <c>services</c>, too generic a key to occupy at the root of the
    /// AppHost's configuration, so its entries are re-keyed under <c>ServiceSources</c> as they are
    /// handed over — the file's shape on disk is unchanged, which is what keeps a TypeScript
    /// AppHost, with no natural place to author .NET configuration, working exactly as before.
    /// Parsing still goes through the JSON configuration provider; only the key prefix is ours.
    /// Only the <c>services</c> subtree crosses over. Anything else the file happens to carry is
    /// the file's own business, and re-rooting it wholesale would make this the route by which an
    /// unrelated key reaches the AppHost's live configuration under our prefix.
    /// </remarks>
    private static MemoryConfigurationSource ReadFileSource(string path)
    {
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: true).Build();

        // The root owns the json provider, and through it a file provider and its change watcher.
        // Every value is copied out below, so nothing needs any of that after this call returns.
        using (file as IDisposable)
        {
            // Relative paths, so each entry is named the way it sits under the file's own root and
            // the key it lands on is built from ServicesKey itself. Spelling the destination prefix
            // out here instead would be a second place that has to agree with the key the reader
            // asks for, and disagreeing costs nothing at build time and nothing at run time: the
            // section simply comes back empty, and every service reports that it is configured
            // nowhere while the file sits there fully populated.
            var reRooted = file.GetSection(FileServicesKey).AsEnumerable(makePathsRelative: true)
                .Where(entry => entry.Value is not null)
                .ToDictionary(entry => $"{DeveloperConfiguration.ServicesKey}:{entry.Key}", entry => entry.Value);

            return new MemoryConfigurationSource { InitialData = reRooted };
        }
    }

    /// <summary>
    /// The root key of the file at <paramref name="path"/> that looks like a misspelling of
    /// <c>services</c>, or <see langword="null"/> when the file has a <c>services</c> key, has no
    /// key resembling one, or is not there.
    /// </summary>
    /// <remarks>
    /// A near miss rather than a check that every root key is one this file recognizes. Only the
    /// <c>services</c> subtree crosses into the AppHost's configuration precisely so that the file
    /// can carry keys of its own, which makes an unrecognized root key indistinguishable from a
    /// typo by validity — resemblance to the one key that is read is the only thing that separates
    /// them.
    /// <para>
    /// Asked only when nothing is configured anywhere, so the parse below is never on the path of
    /// an AppHost that starts. A file carrying <c>services</c> as well is not a typo whatever else
    /// it carries: its entries are being read, so there is nothing to correct.
    /// </para>
    /// </remarks>
    public static string? NearMissForServicesKey(string path)
    {
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: true).Build();

        using (file as IDisposable)
        {
            var rootKeys = file.GetChildren().Select(section => section.Key).ToArray();

            // Folded, because a root key differing only by case is not a near miss but the key
            // itself: configuration keys are case-insensitive, so `Services` is already read.
            if (rootKeys.Any(key => key.Equals(FileServicesKey, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            // Closest first, then ordinal, so a file with two candidates names the same one every
            // run rather than whichever the provider happened to enumerate first.
            return rootKeys
                .Select(key => (Key: key, Distance: EditDistance(key.ToLowerInvariant(), FileServicesKey)))
                .Where(candidate => candidate.Distance <= NearMissEdits)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                .Select(candidate => candidate.Key)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// The Levenshtein distance between <paramref name="from"/> and <paramref name="to"/>: how many
    /// single-character inserts, deletes and substitutions separate them.
    /// </summary>
    /// <remarks>
    /// Two rows rather than the full matrix, since only the previous row is ever read. A transposed
    /// pair costs two edits here where the Damerau variant charges one, which is why
    /// <see cref="NearMissEdits"/> is two rather than one.
    /// </remarks>
    private static int EditDistance(string from, string to)
    {
        var previous = new int[to.Length + 1];
        var current = new int[to.Length + 1];

        for (var j = 0; j <= to.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= from.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= to.Length; j++)
            {
                var substitution = previous[j - 1] + (from[i - 1] == to[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[to.Length];
    }
}
