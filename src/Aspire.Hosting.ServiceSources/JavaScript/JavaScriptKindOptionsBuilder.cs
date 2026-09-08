namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// A fluent handle over a service's <c>javascript:</c> options — the typed alternative to passing a
/// <c>Dictionary&lt;string, object&gt;</c> to <c>WithKind("javascript", …)</c>. Writes directly into
/// an internal <see cref="JavaScriptKindOptions"/> instance; kept as a handle rather than making
/// <see cref="JavaScriptKindOptions"/> itself public, for the same reason
/// <see cref="Java.JavaKindOptionsBuilder"/> is — see design "Kind options".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class JavaScriptKindOptionsBuilder
{
    private readonly JavaScriptKindOptions _options = new();

    internal JavaScriptKindOptionsBuilder()
    {
    }

    /// <summary>
    /// Which <c>Aspire.Hosting.JavaScript</c> integration runs the app — see
    /// <see cref="JavaScriptAppTypes"/> for the accepted values (<c>javascript</c>, <c>vite</c>,
    /// <c>nextjs</c>, <c>node</c>, <c>bun</c>).
    /// </summary>
    public JavaScriptKindOptionsBuilder AppType(string appType)
    {
        _options.AppType = appType;
        return this;
    }

    /// <summary>The directory holding the app's <c>package.json</c>, relative to the repository root.</summary>
    public JavaScriptKindOptionsBuilder AppDirectory(string appDirectory)
    {
        _options.AppDirectory = appDirectory;
        return this;
    }

    /// <summary>The <c>package.json</c> script to run.</summary>
    public JavaScriptKindOptionsBuilder RunScript(string runScript)
    {
        _options.RunScript = runScript;
        return this;
    }

    /// <summary>
    /// The entry-point file to run directly, relative to <see cref="AppDirectory"/> — for the
    /// <c>node</c>/<c>bun</c> app types.
    /// </summary>
    public JavaScriptKindOptionsBuilder ScriptPath(string scriptPath)
    {
        _options.ScriptPath = scriptPath;
        return this;
    }

    /// <summary>
    /// The package manager used to install dependencies — see
    /// <see cref="JavaScriptPackageManagers"/> for the accepted values.
    /// </summary>
    public JavaScriptKindOptionsBuilder PackageManager(string packageManager)
    {
        _options.PackageManager = packageManager;
        return this;
    }

    /// <summary>The port consumers reach the service on.</summary>
    public JavaScriptKindOptionsBuilder Port(int port)
    {
        _options.Port = port;
        return this;
    }

    /// <summary>The port the app itself listens on, when fixed rather than read from <see cref="PortEnv"/>.</summary>
    public JavaScriptKindOptionsBuilder TargetPort(int targetPort)
    {
        _options.TargetPort = targetPort;
        return this;
    }

    /// <summary>The environment variable the app reads its listen port from.</summary>
    public JavaScriptKindOptionsBuilder PortEnv(string portEnv)
    {
        _options.PortEnv = portEnv;
        return this;
    }

    /// <summary>
    /// The <see cref="JavaScriptKindOptions"/> this handle has been writing into. Validated the same
    /// way a yaml <c>javascript:</c> block is, not here.
    /// </summary>
    internal JavaScriptKindOptions Build() => _options;
}
