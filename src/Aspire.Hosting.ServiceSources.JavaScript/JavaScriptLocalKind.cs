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
        var appDirectory = ResolveAppDirectory(serviceName, repoRoot, options.AppDirectory);

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
    /// <see cref="Resolve"/> rather than <see cref="Validate"/> because it is the only check here
    /// that needs the repository on disk, and <see cref="ILocalResourceKind.Validate"/> is
    /// deliberately not given the checkout path.
    /// </summary>
    private static string ResolveAppDirectory(string serviceName, string repoRoot, string appDirectory)
    {
        var root = Path.GetFullPath(repoRoot);
        var resolved = Path.GetFullPath(Path.Combine(root, appDirectory));

        // Path.Combine hands back an absolute appDirectory unchanged, and "../.." climbs out, either
        // of which would silently run something from outside the service's own checkout.
        if (resolved != root && !resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript appDirectory '{appDirectory}' resolves to '{resolved}', " +
                "which is outside the service's checkout. It must be a relative path within the repository.");
        }

        if (!Directory.Exists(resolved))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript appDirectory '{appDirectory}' was not found under '{root}'.");
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

        // Non-null: ParseChoice only returns null when the fallback it is given is null.
        var appType = ParseChoice(
            serviceName, "appType", options.AppType, JavaScriptAppTypes.All, JavaScriptAppTypes.JavaScript)!;
        var packageManager = ParseChoice(
            serviceName, "packageManager", options.PackageManager, JavaScriptPackageManagers.All, null);

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
        if (string.IsNullOrWhiteSpace(value))
        {
            return whenUnset;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (!allowed.Contains(normalized))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': javascript {field} '{value.Trim()}' is not supported. " +
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
