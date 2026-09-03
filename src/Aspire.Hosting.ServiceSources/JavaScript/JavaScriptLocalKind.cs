using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Resolves a <c>"local"</c>-sourced service whose catalog entry declares <c>kind: javascript</c>
/// by handing the already-cloned checkout to <c>Aspire.Hosting.JavaScript</c>. This package owns no
/// process-launch logic of its own: it translates the service's <c>javascript:</c> options block
/// into the matching <c>AddJavaScriptApp</c>/<c>AddViteApp</c>/<c>AddNextJsApp</c>/
/// <c>AddNodeApp</c>/<c>AddBunApp</c> call plus its package-manager modifier, and lets that
/// integration do the rest.
/// </summary>
internal sealed class JavaScriptLocalKind : ILocalResourceKind
{
    /// <summary>
    /// The <c>kind</c> value this handler is registered under, and therefore also the name of the
    /// options block the catalog loader hands it.
    /// </summary>
    public const string KindName = "javascript";

    public void Validate(string serviceName, object? rawConfig) => ResolveOptions(serviceName, rawConfig);

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        var plan = Plan(serviceName, repoRoot, rawConfig);

        // Checked before anything reaches the app model, so an appDirectory that names nothing is
        // reported from here rather than as an npm failure from a resource much later.
        plan.RequireCheckout();

        return plan.Add(builder, deferred: false);
    }

    /// <summary>
    /// Answered from the options block alone, and without touching the checkout — see
    /// <c>ResolvedOptions.SupportsDeferral</c> for which blocks can be built cold and why.
    /// </summary>
    /// <remarks>
    /// A block that will not parse answers <see langword="false"/> rather than throwing. This is
    /// probed for services that may never be added, so it is not this call's place to report a
    /// malformed block; the eager path it falls back to raises the same parse failure from
    /// <see cref="Validate"/>, naming the service.
    /// </remarks>
    public bool SupportsDeferredCheckout(object? rawConfig)
    {
        try
        {
            // The service name is only ever used to build messages, and nothing here reports one.
            return ResolveOptions("?", rawConfig).SupportsDeferral();
        }
        catch (ServiceSourcesConfigurationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The resource is built entirely from the committed catalog: <c>port</c>/<c>targetPort</c> are
    /// optional and Aspire allocates them when unset, the service always gets an <c>http</c>
    /// endpoint, and nothing is read out of the repository at composition time — so for the app
    /// types <see cref="SupportsDeferredCheckout"/> admits, a deferred javascript service is
    /// identical to a warm one and only the checks below move.
    /// </summary>
    /// <remarks>
    /// <c>AddViteApp</c> and friends also add a separate installer resource to run
    /// <c>npm install</c>, which the app already waits for. Holding that back is core's job, not
    /// this handler's: core withholds every resource this call adds and starts them in order, so the
    /// installer runs against a checkout that exists and the app's wait resolves the way it does on
    /// a warm run.
    /// </remarks>
    public DeferredLocalResource? ResolveDeferred(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        var plan = Plan(serviceName, repoRoot, rawConfig);

        // Core asks SupportsDeferredCheckout first, so this is belt-and-braces — but it is the last
        // point at which registering the wrong resource is still avoidable, and the cost of being
        // wrong here is a service that runs without its installer.
        if (!plan.Options.SupportsDeferral())
        {
            return null;
        }

        return new DeferredLocalResource
        {
            Service = plan.Add(builder, deferred: true),
            ValidateCheckout = plan.RequireCheckout,
        };
    }

    /// <summary>
    /// Everything decidable without the repository on disk: the options block, and the absolute
    /// paths the resource will run from — including the containment checks, which are pure path
    /// arithmetic and so belong on this side of the split, before a cold clone is paid for.
    /// </summary>
    private static JavaScriptPlan Plan(string serviceName, string repoRoot, object? rawConfig)
    {
        var options = ResolveOptions(serviceName, rawConfig);

        // Trailing separators are trimmed once here: Path.GetFullPath preserves them, and a
        // developer "path" override reaches this handler verbatim — so an override written with the
        // trailing slash shell tab-completion produces would otherwise make every containment check
        // below compare against "root//" and reject even the default appDirectory ".".
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));

        var appDirectory = RequireInsideCheckout(serviceName, "appDirectory", root, root, options.AppDirectory);

        // Anchored to appDirectory, which is the working directory the integration runs it from, but
        // still confined to the checkout — a sibling app directory is a legitimate target, anything
        // outside the repository is not.
        var scriptPath = options.ScriptPath is null
            ? null
            : RequireInsideCheckout(serviceName, "scriptPath", root, appDirectory, options.ScriptPath);

        return new JavaScriptPlan(serviceName, options, root, appDirectory, scriptPath);
    }

    /// <summary>
    /// A resolved javascript service: the paths it will run from, the checks that need those paths
    /// to exist, and the resource itself. Split that way because the two callers need the halves in
    /// different orders — the eager path checks then builds, the deferred path builds now and checks
    /// once the clone has landed.
    /// </summary>
    private sealed record JavaScriptPlan(
        string ServiceName,
        ResolvedOptions Options,
        string Root,
        string AppDirectory,
        string? ScriptPath)
    {
        /// <summary>
        /// The checks that need the repository on disk. On the eager path these run before anything
        /// reaches the app model; on the deferred path core runs them after the clone, where they
        /// surface as the service's resource state rather than as an exception out of composition.
        /// </summary>
        /// <remarks>
        /// These cannot move into <see cref="ILocalResourceKind.Validate"/>, which is deliberately
        /// not given the checkout path — core calls it before resolving the repo root so that a
        /// malformed options block fails without first paying for a cold clone.
        /// </remarks>
        public void RequireCheckout()
        {
            if (!Directory.Exists(AppDirectory))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{ServiceName}': javascript appDirectory '{Options.AppDirectory}' was not found " +
                    $"under '{Root}'.");
            }

            // Without the existence check a typo reaches the developer as "node: cannot find module"
            // at run time rather than as a named config error.
            if (ScriptPath is not null && !File.Exists(ScriptPath))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{ServiceName}': javascript scriptPath '{Options.ScriptPath}' was not found under " +
                    $"'{AppDirectory}'.");
            }

            RequirePackageJsonIfOneIsNeeded(ServiceName, AppDirectory, Options);
        }

        /// <param name="deferred">
        /// Whether this is being built against a checkout that has not landed yet, which changes
        /// what <c>AddNodeApp</c>/<c>AddBunApp</c> managed to attach on their own.
        /// </param>
        public IResourceBuilder<IResourceWithServiceDiscovery> Add(
            IDistributedApplicationBuilder builder, bool deferred)
        {
            var app = AddApp(builder, ServiceName, AppDirectory, Options);

            // AddNodeApp/AddBunApp attach their package manager only when they can see a
            // package.json in the app directory, and on the deferred path there is no directory yet
            // to see one in — so they attach nothing, and everything hanging off that annotation
            // goes with it: the npm install resource, the app's wait for it, and the rewrite that
            // turns runScript into 'npm run <script>' instead of running scriptPath directly.
            // Attaching it here is what makes the deferred resource the one a warm run builds.
            // Only reached for an options block SupportsDeferral has already established a
            // package.json is coming for.
            if (deferred)
            {
                ApplyPackageManager(app, Options.PackageManagerForColdCheckout());
            }

            ApplyPackageManager(app, Options.PackageManager);

            // For node/bun the run script is an override on top of the script file the integration is
            // already told to execute, so it can only be applied afterwards. The other app types took
            // it as the AddXxx argument above.
            if (JavaScriptAppTypes.RunsAScriptFile(Options.AppType) && Options.RunScript is not null)
            {
                app.WithRunScript(Options.RunScript);
            }

            // AddViteApp/AddNextJsApp already added an "http" endpoint, which this call updates in
            // place with whatever was configured (a null argument leaves the existing value alone); for
            // the other app types nothing added one, and without an endpoint the facade AddService
            // hands back would carry nothing for a consumer's WithReference to resolve.
            return JavaScriptAppTypes.BindsItsOwnPort(Options.AppType)
                ? app.WithHttpEndpoint(port: Options.Port, targetPort: Options.TargetPort)
                : app.WithHttpEndpoint(port: Options.Port, targetPort: Options.TargetPort, env: Options.PortEnv);
        }
    }

    private static IResourceBuilder<JavaScriptAppResource> AddApp(
        IDistributedApplicationBuilder builder, string serviceName, string appDirectory, ResolvedOptions options) =>
        options.AppType switch
        {
            // Each integration defaults runScriptName to "dev" itself, so pass the argument only
            // when the service actually set it rather than duplicating that default here.
            JavaScriptAppTypes.Vite => options.RunScript is null
                ? builder.AddViteApp(serviceName, appDirectory)
                : builder.AddViteApp(serviceName, appDirectory, options.RunScript),
#pragma warning disable ASPIREJAVASCRIPT001 // AddNextJsApp is [Experimental] in Aspire.Hosting.JavaScript; reaching it from yaml is the whole point of the nextjs app type.
            JavaScriptAppTypes.NextJs => options.RunScript is null
                ? builder.AddNextJsApp(serviceName, appDirectory)
                : builder.AddNextJsApp(serviceName, appDirectory, options.RunScript),
#pragma warning restore ASPIREJAVASCRIPT001
            // ScriptPath is non-null for these two — ResolveOptions requires it.
            JavaScriptAppTypes.Node => builder.AddNodeApp(serviceName, appDirectory, options.ScriptPath!),
            JavaScriptAppTypes.Bun => builder.AddBunApp(serviceName, appDirectory, options.ScriptPath!),
            _ => options.RunScript is null
                ? builder.AddJavaScriptApp(serviceName, appDirectory)
                : builder.AddJavaScriptApp(serviceName, appDirectory, options.RunScript),
        };

    private static void ApplyPackageManager(IResourceBuilder<JavaScriptAppResource> app, string? packageManager)
    {
        switch (packageManager)
        {
            case JavaScriptPackageManagers.Npm:
                app.WithNpm();
                break;
            case JavaScriptPackageManagers.Yarn:
                app.WithYarn();
                break;
            case JavaScriptPackageManagers.Pnpm:
                app.WithPnpm();
                break;
            case JavaScriptPackageManagers.Bun:
                app.WithBun();
                break;
            // Unset: leave whichever package manager the integration picked for this app type.
        }
    }

    /// <summary>
    /// Requires a <c>package.json</c> in the app directory for the app types that run one of its
    /// scripts. Every app type but node/bun does, and a <c>runScript</c> on those two means the same
    /// thing — Aspire's <c>AddNodeApp</c>/<c>AddBunApp</c> only attach a package manager when the
    /// app directory has a <c>package.json</c>, so a run script set without one is silently dropped
    /// and the service starts the <c>scriptPath</c> it was meant to override. Left to run time both
    /// cases surface as an npm "could not read package.json" from the installer resource, detached
    /// from the service whose entry named the wrong directory.
    /// </summary>
    private static void RequirePackageJsonIfOneIsNeeded(
        string serviceName, string appDirectory, ResolvedOptions options)
    {
        if (JavaScriptAppTypes.RunsAScriptFile(options.AppType) && options.RunScript is null)
        {
            // A checkout holding nothing but the entry-point file is exactly what these two are for.
            return;
        }

        if (File.Exists(Path.Combine(appDirectory, "package.json")))
        {
            return;
        }

        var (what, remedy) = JavaScriptAppTypes.RunsAScriptFile(options.AppType)
            ? ($"runScript '{options.RunScript}' names a package.json script",
                "Remove it to run 'scriptPath' directly, or point 'appDirectory' at the directory holding the app's package.json.")
            : ($"appType '{options.AppType}' runs a package.json script",
                "Point 'appDirectory' at the directory holding the app's package.json.");

        throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': javascript {what}, but no 'package.json' was found in " +
            $"'{appDirectory}'. {remedy}");
    }

    /// <summary>
    /// How resolved paths are compared with the checkout root: the way the filesystem itself
    /// compares them. An <c>appDirectory</c> that climbs out and back in (<c>"../Frontend/web"</c>)
    /// is the one way a path genuinely inside the checkout can differ from the root in casing, and
    /// on Windows and macOS that is the same directory — an ordinal comparison would reject it as
    /// being outside. The trade-off is a case-sensitive macOS volume, where a sibling differing only
    /// in casing is let through; that is a mistake this check would rather have caught, but a far
    /// cheaper outcome than refusing a path that really is inside the checkout.
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="value"/> against <paramref name="basePath"/> and refuses anything
    /// that lands outside <paramref name="root"/>. <see cref="Path.Combine(string, string)"/> hands
    /// back an absolute value unchanged, and <c>"../.."</c> climbs out — either of which would
    /// silently run something from outside the service's own checkout. This catches the mistake, not
    /// a determined author: the catalog is a file in the AppHost's own repository, and symlinks are
    /// deliberately left unresolved so that a checkout linked into place from elsewhere — a normal
    /// thing to do while working on a service locally — keeps working.
    /// </summary>
    private static string RequireInsideCheckout(
        string serviceName, string field, string root, string basePath, string value)
    {
        var resolved = Path.GetFullPath(Path.Combine(basePath, value));

        if (!string.Equals(resolved, root, PathComparison)
            && !resolved.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript {field} '{value}' resolves to '{resolved}', " +
                "which is outside the service's checkout. It must be a relative path within the repository.");
        }

        return resolved;
    }

    /// <summary>
    /// Parses and fully validates the options block, applying every default. Shared by
    /// <see cref="Validate"/> and <see cref="Resolve"/> so a service whose options are wrong is
    /// rejected from <see cref="Validate"/> — which core calls first, and before this service's
    /// checkout — rather than part-way through creating its resource.
    /// </summary>
    private static ResolvedOptions ResolveOptions(string serviceName, object? rawConfig)
    {
        var options = LocalKindConfig.Parse<JavaScriptKindOptions>(rawConfig, serviceName) ?? new JavaScriptKindOptions();

        // Through RequireNonBlank first so the choice fields follow the same rule as the free-text
        // ones below: an explicitly empty value is a mistake to name, not a reason to fall back to
        // the default. Non-null: ParseChoice only returns null when the fallback it is given is null.
        var appType = ParseChoice(
            serviceName, "appType", RequireNonBlank(serviceName, "appType", options.AppType),
            JavaScriptAppTypes.All, JavaScriptAppTypes.JavaScript)!;
        var packageManager = ParseChoice(
            serviceName, "packageManager", RequireNonBlank(serviceName, "packageManager", options.PackageManager),
            JavaScriptPackageManagers.All, null);

        var appDirectory = RequireNonBlank(serviceName, "appDirectory", options.AppDirectory) ?? ".";
        var runScript = RequireNonBlank(serviceName, "runScript", options.RunScript);
        var scriptPath = RequireNonBlank(serviceName, "scriptPath", options.ScriptPath);
        var portEnv = RequireNonBlank(serviceName, "portEnv", options.PortEnv);

        if (JavaScriptAppTypes.RunsAScriptFile(appType))
        {
            if (scriptPath is null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': javascript appType '{appType}' runs a script file directly, so " +
                    "'scriptPath' is required (e.g. scriptPath: server.js).");
            }
        }
        else if (scriptPath is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript 'scriptPath' only applies to appType " +
                $"'{JavaScriptAppTypes.Node}' or '{JavaScriptAppTypes.Bun}', but this service's appType is " +
                $"'{appType}', which runs a package.json script — use 'runScript' instead.");
        }

        if (JavaScriptAppTypes.BindsItsOwnPort(appType) && portEnv is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript 'portEnv' does not apply to appType '{appType}', whose " +
                "integration binds the dev server's port itself. Use 'port' or 'targetPort' instead.");
        }

        RequireValidPort(serviceName, "port", options.Port);
        RequireValidPort(serviceName, "targetPort", options.TargetPort);

        return new ResolvedOptions(
            appType, appDirectory, runScript, scriptPath, packageManager, options.Port, options.TargetPort,
            portEnv ?? DefaultPortEnv);
    }

    /// <summary>
    /// The environment variable Node apps conventionally read their listen port from, and what
    /// <c>WithHttpEndpoint(env:)</c> is given when a service doesn't name one itself.
    /// </summary>
    private const string DefaultPortEnv = "PORT";

    private static string? ParseChoice(
        string serviceName, string field, string? value, IReadOnlyList<string> allowed, string? whenUnset)
    {
        // Null is "not set" — an empty or whitespace value was already rejected by RequireNonBlank,
        // which every caller routes its value through.
        if (value is null)
        {
            return whenUnset;
        }

        var normalized = value.ToLowerInvariant();
        if (!allowed.Contains(normalized))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript {field} '{value}' is not supported. " +
                $"Use one of: {string.Join(", ", allowed)}.");
        }

        return normalized;
    }

    /// <summary>
    /// Distinguishes "not set" (null, so a default applies) from "set to nothing" — an explicit
    /// empty or whitespace value is a mistake worth naming rather than silently defaulting.
    /// </summary>
    private static string? RequireNonBlank(string serviceName, string field, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript '{field}' is set but empty. Give it a value or remove it.");
        }

        return value.Trim();
    }

    private static void RequireValidPort(string serviceName, string field, int? port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript {field} value '{port}' is not a valid port " +
                "(must be between 1 and 65535).");
        }
    }

    private sealed record ResolvedOptions(
        string AppType,
        string AppDirectory,
        string? RunScript,
        string? ScriptPath,
        string? PackageManager,
        int? Port,
        int? TargetPort,
        string PortEnv)
    {
        /// <summary>
        /// Whether the resource built for this options block without the checkout on disk is the
        /// one a warm run builds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>vite</c>, <c>nextjs</c> and <c>javascript</c> always are: their builder calls attach a
        /// package manager unconditionally, so the installer resource and the app's wait for it are
        /// there either way.
        /// </para>
        /// <para>
        /// <c>node</c> and <c>bun</c> are the exception. <c>AddNodeApp</c>/<c>AddBunApp</c> attach
        /// one only if they can see a <c>package.json</c> in the app directory, so what a warm run
        /// produces depends on the repository's contents — an installer and a wait when the file is
        /// there, neither when it is not — and a cold checkout cannot be looked at. Deferring on a
        /// guess is not harmless either way round: guessing "present" puts an <c>npm install</c> in
        /// front of a repository that holds a single entry-point file, and guessing "absent" is
        /// worse, because the service then starts against a checkout with no <c>node_modules</c> and
        /// a <see cref="RunScript"/> silently downgraded to running <c>scriptPath</c> directly.
        /// </para>
        /// <para>
        /// So they are admitted only where the answer is already known without looking:
        /// <see cref="PackageManager"/> names one, which is attached on both paths regardless of
        /// what is on disk; or <see cref="RunScript"/> is set, which
        /// <c>RequirePackageJsonIfOneIsNeeded</c> demands a <c>package.json</c> for and fails the
        /// service after the clone if it is missing. Otherwise the honest answer is "resolve me
        /// eagerly", which costs this one service its dashboard-during-clone and nothing else.
        /// </para>
        /// </remarks>
        public bool SupportsDeferral() =>
            !JavaScriptAppTypes.RunsAScriptFile(AppType) || PackageManager is not null || RunScript is not null;

        /// <summary>
        /// The package manager <c>AddNodeApp</c>/<c>AddBunApp</c> would have attached had the
        /// checkout been on disk, for the deferred path to attach in its place, or
        /// <see langword="null"/> when nothing needs attaching by hand — either the app type
        /// attaches its own, or the catalog named one and <c>ApplyPackageManager</c> attaches it.
        /// </summary>
        public string? PackageManagerForColdCheckout() =>
            !JavaScriptAppTypes.RunsAScriptFile(AppType) || PackageManager is not null
                ? null
                : AppType == JavaScriptAppTypes.Bun ? JavaScriptPackageManagers.Bun : JavaScriptPackageManagers.Npm;
    }
}
