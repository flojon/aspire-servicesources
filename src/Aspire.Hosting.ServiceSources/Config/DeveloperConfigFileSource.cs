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

    /// <summary>The same, for the backing services a service connects to.</summary>
    /// <remarks>
    /// Internal rather than private because a message that tells a developer which key to write has
    /// to name the key that is actually read. A second copy of the spelling somewhere else is a
    /// copy that can drift from this one, and the drift would be invisible: the section would go on
    /// being read under this spelling while the advice named the other.
    /// </remarks>
    internal const string FileBackingServicesKey = "backingServices";

    /// <summary>
    /// Every subtree of the file that crosses into the AppHost's configuration, and the key it
    /// lands under.
    /// </summary>
    /// <remarks>
    /// A list rather than a special case per section, so that adding one is adding a line. Both
    /// keys are too generic to occupy at the root of the AppHost's configuration, which is why
    /// neither crosses over under the name the file gives it.
    /// </remarks>
    private static readonly (string FileKey, string ConfigurationKey)[] ReRootedSections =
    [
        (FileServicesKey, DeveloperConfiguration.ServicesKey),
        (FileBackingServicesKey, DeveloperConfiguration.BackingServicesKey),
    ];

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, Registration> Registrations = new();

    /// <summary>
    /// Registers the file on <paramref name="builder"/>'s configuration if it isn't registered
    /// already. Cheap and safe to call from every entry point, and from every read.
    /// </summary>
    public static void EnsureRegistered(IDistributedApplicationBuilder builder) => RegistrationFor(builder);

    /// <summary>
    /// The registration for <paramref name="builder"/>, registered.
    /// </summary>
    private static Registration RegistrationFor(IDistributedApplicationBuilder builder)
    {
        // The factory has to stay free of side effects. ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, but every caller is handed
        // the one it kept — so the registration itself, guarded by that instance, happens once even
        // though the instance may be built twice.
        var registration = Registrations.GetValue(builder, static _ => new Registration());

        registration.Register(builder);

        return registration;
    }

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

        private IReadOnlyList<string> _rootKeys = [];

        private IReadOnlySet<string> _configuringRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every root key the file mentions, captured while it was read.
        /// </summary>
        /// <remarks>
        /// Kept because the near-miss checks want them and the file has already been parsed by the
        /// time anything asks: reading them back off disk is a second parse of a file whose contents
        /// cannot have changed, on the path of an AppHost that is starting normally.
        /// </remarks>
        public IReadOnlyList<string> RootKeys
        {
            get
            {
                lock (_gate)
                {
                    return _rootKeys;
                }
            }
        }

        /// <summary>
        /// The root keys the file writes something under, which is a subset of <see cref="RootKeys"/>.
        /// </summary>
        /// <remarks>
        /// The distinction is "the file mentions this key" against "the file configures anything
        /// through it", and the near-miss check needs both: the second decides whether the key being
        /// looked for is really there, the first decides what could be a misspelling of it. Using
        /// one for both gets the other wrong — see where these are built.
        /// </remarks>
        public IReadOnlySet<string> ConfiguringRootKeys
        {
            get
            {
                lock (_gate)
                {
                    return _configuringRootKeys;
                }
            }
        }

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
                var (source, rootKeys, configuringRootKeys) = ReadFileSource(
                    Path.Combine(builder.AppHostDirectory, DeveloperConfiguration.FileName));

                _rootKeys = rootKeys;
                _configuringRootKeys = configuringRootKeys;

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
    /// The file's own roots — <c>services</c> and <c>backingServices</c> — are too generic to
    /// occupy at the root of the AppHost's configuration, so their entries are re-keyed under
    /// <c>ServiceSources</c> as they are handed over. The file's shape on disk is unchanged, which
    /// is what keeps a TypeScript AppHost, with no natural place to author .NET configuration,
    /// working exactly as before. Parsing still goes through the JSON configuration provider; only
    /// the key prefix is ours. Only the subtrees in <see cref="ReRootedSections"/> cross over.
    /// Anything else the file happens to carry is the file's own business, and re-rooting it
    /// wholesale would make this the route by which an unrelated key reaches the AppHost's live
    /// configuration under our prefix.
    /// </remarks>
    private static (
        MemoryConfigurationSource Source,
        IReadOnlyList<string> RootKeys,
        IReadOnlySet<string> ConfiguringRootKeys) ReadFileSource(string path)
    {
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: true).Build();

        // The root owns the json provider, and through it a file provider and its change watcher.
        // Every value is copied out below, so nothing needs any of that after this call returns.
        using (file as IDisposable)
        {
            // Relative paths, so each entry is named the way it sits under the file's own root and
            // the key it lands on is built from the destination key itself. Spelling the prefix out
            // here instead would be a second place that has to agree with the key the reader asks
            // for, and disagreeing costs nothing at build time and nothing at run time: the section
            // simply comes back empty, and every service reports that it is configured nowhere
            // while the file sits there fully populated.
            var reRooted = ReRootedSections
                .SelectMany(section => file.GetSection(section.FileKey).AsEnumerable(makePathsRelative: true)
                    .Where(entry => entry.Value is not null)
                    .Select(entry => ($"{section.ConfigurationKey}:{entry.Key}", entry.Value)))
                .ToDictionary(entry => entry.Item1, entry => entry.Item2);

            // Two lists, because the near-miss check asks two different questions and collapsing
            // them into one gets one of them wrong whichever way it is collapsed.
            //
            // Every root key the file mentions, for the candidates. A misspelling is worth naming
            // whether or not anything is written under it yet: `"serivces": { "orders": { } }` is a
            // developer part-way through, and `"serivces": { }` is one about to be, and neither
            // carries a leaf value.
            var rootKeys = file.GetChildren().Select(section => section.Key).ToArray();

            // The subset that actually configures something, for deciding whether the key being
            // looked for is there. `"backingServices": { }` contributes nothing to the configuration
            // above — AsEnumerable over it yields no values — but the JSON parser still emits the key
            // as a null-valued entry, so it appears in the list above. Asking that list "does the
            // file have this key?" answered yes for a section configuring nothing, and the search
            // stopped: a file whose real entries sat under a misspelled key beside an empty correct
            // one was told nothing at all. A scalar at the root — `"services": "oops"` — counts as
            // configuring, and should: it is a value someone wrote.
            var configuringRootKeys = file.AsEnumerable()
                .Where(entry => entry.Value is not null)
                .Select(entry => entry.Key.Split(':')[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return (new MemoryConfigurationSource { InitialData = reRooted }, rootKeys, configuringRootKeys);
        }
    }

    /// <summary>
    /// The root key of <paramref name="builder"/>'s file that looks like a misspelling of
    /// <c>services</c>, or <see langword="null"/> when the file has a <c>services</c> key, has no
    /// key resembling one, or is not there.
    /// </summary>
    public static string? NearMissForServicesKey(IDistributedApplicationBuilder builder) =>
        NearMissForRootKey(builder, FileServicesKey);

    /// <summary>
    /// The same for <c>backingServices</c>, which is the half of #206 that a misspelling costs every
    /// backing service at once.
    /// </summary>
    public static string? NearMissForBackingServicesKey(IDistributedApplicationBuilder builder) =>
        NearMissForRootKey(builder, FileBackingServicesKey);

    /// <summary>
    /// The root key of <paramref name="builder"/>'s file that looks like a misspelling of
    /// <paramref name="fileKey"/>, or <see langword="null"/> when the file has that key, has no key
    /// resembling it, or is not there.
    /// </summary>
    /// <remarks>
    /// A near miss rather than a check that every root key is one this file recognizes. Only the
    /// subtrees in <see cref="ReRootedSections"/> cross into the AppHost's configuration precisely
    /// so that the file can carry keys of its own, which makes an unrecognized root key
    /// indistinguishable from a typo by validity — resemblance to a key that <i>is</i> read is the
    /// only thing that separates them.
    /// <para>
    /// Reads the keys captured when the file was parsed for its values, rather than parsing it
    /// again. The re-parse was affordable while this was asked only from inside a failure; the
    /// backing-service audit asks it from an AppHost that is starting normally, which is a cost
    /// nobody should pay twice for a file whose contents cannot have changed in between.
    /// </para>
    /// <para>
    /// A file <em>configuring</em> something under <paramref name="fileKey"/> is not a typo whatever
    /// else it carries: those entries are being read, so there is nothing to correct. Mentioning the
    /// key is not enough — an empty section configures nothing, and a file whose real entries sit
    /// under a misspelling beside one still wants to hear about it.
    /// </para>
    /// </remarks>
    private static string? NearMissForRootKey(IDistributedApplicationBuilder builder, string fileKey)
    {
        var registration = RegistrationFor(builder);

        // Asked of the keys that configure something, not of every key the file mentions. Folded,
        // because a root key differing only by case is not a near miss but the key itself:
        // configuration keys are case-insensitive, so `Services` is already read.
        if (registration.ConfiguringRootKeys.Contains(fileKey))
        {
            return null;
        }

        // Candidates come from every key the file mentions, which is the other question — a
        // misspelling is worth naming before its entries have any values in them. The key being
        // looked for is excluded explicitly: it is at distance zero from itself, so an empty
        // `services` beside a populated `service` would otherwise be offered as a correction of
        // itself.
        var rootKeys = registration.RootKeys
            .Where(key => !key.Equals(fileKey, StringComparison.OrdinalIgnoreCase));

        // Both sides folded, since the vocabulary itself is not all lower case — `backingServices`
        // compared against a lower-cased candidate would never match its own spelling.
        var folded = fileKey.ToLowerInvariant();

        // Closest first, then ordinal, so a file with two candidates names the same one every run
        // rather than whichever the provider happened to enumerate first.
        //
        // The tolerance is taken from the key being looked for rather than from the one the
        // developer wrote, which is the direction NearMiss.MaxEdits is meant to be asked in: the
        // file's own root keys are the fixed vocabulary here. It is what keeps the two apart, too —
        // `services` and `backingServices` are seven edits from each other, far outside any
        // tolerance, so neither is ever offered as a correction of the other.
        return rootKeys
            .Select(key => (Key: key, Distance: NearMiss.EditDistance(key.ToLowerInvariant(), folded)))
            .Where(candidate => candidate.Distance <= NearMiss.MaxEdits(fileKey))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
            .Select(candidate => candidate.Key)
            .FirstOrDefault();
    }
}
