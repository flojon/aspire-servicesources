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

                AddFileSource(builder, Path.Combine(builder.AppHostDirectory, DeveloperConfiguration.FileName));
                _registered = true;
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
    private static void AddFileSource(IDistributedApplicationBuilder builder, string path)
    {
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: true).Build();

        // The root owns the json provider, and through it a file provider and its change watcher.
        // Every value is copied out below, so nothing needs any of that after this call returns.
        using (file as IDisposable)
        {
            var reRooted = file.GetSection(FileServicesKey).AsEnumerable()
                .Where(entry => entry.Value is not null)
                .ToDictionary(entry => $"ServiceSources:{entry.Key}", entry => entry.Value);

            builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = reRooted });
        }
    }
}
