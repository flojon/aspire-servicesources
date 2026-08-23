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
        var options = ResolveOptions(serviceName, rawConfig);

        // Trailing separators are trimmed once here: Path.GetFullPath preserves them, and a
        // developer "path" override reaches this handler verbatim — so an override written with the
        // trailing slash shell tab-completion produces would otherwise make every containment check
        // below compare against "root//" and reject even the default appDirectory ".".
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));

        var appDirectory = ResolveAppDirectory(serviceName, root, options.AppDirectory);

        if (options.ScriptPath is not null)
        {
            RequireScriptPath(serviceName, root, appDirectory, options.ScriptPath);
        }

        RequirePackageJsonIfOneIsNeeded(serviceName, appDirectory, options);

        var app = AddApp(builder, serviceName, appDirectory, options);

        ApplyPackageManager(app, options.PackageManager);

        // For node/bun the run script is an override on top of the script file the integration is
        // already told to execute, so it can only be applied afterwards. The other app types took
        // it as the AddXxx argument above.
        if (JavaScriptAppTypes.RunsAScriptFile(options.AppType) && options.RunScript is not null)
        {
            app.WithRunScript(options.RunScript);
        }

        // AddViteApp/AddNextJsApp already added an "http" endpoint, which this call updates in
        // place with whatever was configured (a null argument leaves the existing value alone); for
        // the other app types nothing added one, and without an endpoint the facade AddService
        // hands back would carry nothing for a consumer's WithReference to resolve.
        return JavaScriptAppTypes.BindsItsOwnPort(options.AppType)
            ? app.WithHttpEndpoint(port: options.Port, targetPort: options.TargetPort)
            : app.WithHttpEndpoint(port: options.Port, targetPort: options.TargetPort, env: options.PortEnv);
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
    /// Anchors <paramref name="appDirectory"/> to the checkout and checks it exists. Runs in
    /// <see cref="Resolve"/> rather than <see cref="Validate"/> because these are the only checks
    /// here that need the repository on disk, and <see cref="ILocalResourceKind.Validate"/> is
    /// deliberately not given the checkout path.
    /// </summary>
    private static string ResolveAppDirectory(string serviceName, string root, string appDirectory)
    {
        var resolved = RequireInsideCheckout(serviceName, "appDirectory", root, root, appDirectory);

        if (!Directory.Exists(resolved))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript appDirectory '{appDirectory}' was not found under '{root}'.");
        }

        return resolved;
    }

    /// <summary>
    /// The same pair of checks for the file node/bun are handed to execute. Anchored to
    /// <paramref name="appDirectory"/>, which is the working directory the integration runs it from,
    /// but still confined to the checkout — a sibling app directory is a legitimate target, anything
    /// outside the repository is not. Without the existence check a typo reaches the developer as
    /// <c>node: cannot find module</c> at run time rather than as a named config error.
    /// </summary>
    private static void RequireScriptPath(string serviceName, string root, string appDirectory, string scriptPath)
    {
        var resolved = RequireInsideCheckout(serviceName, "scriptPath", root, appDirectory, scriptPath);

        if (!File.Exists(resolved))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript scriptPath '{scriptPath}' was not found under '{appDirectory}'.");
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
        string PortEnv);
}
