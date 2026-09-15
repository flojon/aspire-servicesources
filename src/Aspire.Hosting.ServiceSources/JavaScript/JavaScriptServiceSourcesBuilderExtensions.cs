using Aspire.Hosting;
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources;

public static class JavaScriptServiceSourcesBuilderExtensions
{
    /// <summary>
    /// Teaches <c>AddService()</c> to resolve <c>"local"</c>-sourced services whose
    /// <c>servicesources.yaml</c> entry declares <c>kind: javascript</c>: the repository is cloned
    /// and checked out exactly as for a .NET service, then run through
    /// <c>Aspire.Hosting.JavaScript</c> according to the entry's <c>javascript:</c> options block.
    /// </summary>
    /// <remarks>
    /// <c>javascript</c> is a built-in kind — <c>AddService()</c> resolves it whether or not this is
    /// ever called, the same way it always has for <c>dotnet</c>. Call this only to substitute a
    /// different <see cref="ILocalResourceKind"/> for the name (e.g. a test double); calling it after
    /// a <c>javascript</c>-kind service has already resolved with the built-in handler has no effect
    /// on that service. The options block accepts <c>appType</c> (<c>javascript</c> — the default —
    /// <c>vite</c>, <c>nextjs</c>, <c>node</c>, or <c>bun</c>), <c>appDirectory</c>, <c>runScript</c>,
    /// <c>scriptPath</c>, <c>packageManager</c>, <c>port</c>, <c>targetPort</c>, and <c>portEnv</c>;
    /// see this package's README for what each one means.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var frontend = builder.AddService("frontend"); // kind: javascript needs no registration call
    /// </code>
    /// </example>
    [AspireExport]
    public static IDistributedApplicationBuilder UseJavaScript(this IDistributedApplicationBuilder builder) =>
        builder.AddLocalKind(JavaScriptLocalKind.KindName, new JavaScriptLocalKind());

    /// <summary>
    /// Configures this code-declared service to run as a <c>javascript</c>-kind <c>"local"</c>
    /// service, through a typed options handle instead of a raw dictionary. Sugar over
    /// <c>WithKind("javascript", …)</c> — calling this after <c>WithKind</c> (on either) throws the
    /// same "already called" error <c>WithKind</c> itself would, since this <em>is</em> that call.
    /// </summary>
    /// <example>
    /// <code>
    /// catalog.AddService("frontend")
    ///     .WithRepository("https://github.com/example/frontend")
    ///     .AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite).Port(3000));
    /// </code>
    /// </example>
    [AspireExport]
    public static ServiceDefinitionBuilder AsJavaScript(
        this ServiceDefinitionBuilder builder, Action<JavaScriptKindOptionsBuilder> configure)
    {
        var options = new JavaScriptKindOptionsBuilder();
        configure(options);
        return builder.WithKind(JavaScriptLocalKind.KindName, options.Build());
    }
}
