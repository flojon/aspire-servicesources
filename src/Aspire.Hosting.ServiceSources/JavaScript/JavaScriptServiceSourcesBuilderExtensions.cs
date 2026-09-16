using Aspire.Hosting;
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources;

public static class JavaScriptServiceSourcesBuilderExtensions
{
    /// <summary>
    /// Redundant today: <c>javascript</c> is a built-in local kind, and this registers an instance of
    /// the same built-in <see cref="JavaScriptLocalKind"/> type <c>AddService()</c> already falls back
    /// to when nothing was registered for the name (see <see cref="Sources.LocalKindRegistry"/>).
    /// Calling it changes nothing about how a <c>kind: javascript</c> service resolves — delete the
    /// call. To use a *different* <see cref="ILocalResourceKind"/> for <c>"javascript"</c> (e.g. a
    /// test double), call <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/> directly with
    /// your own handler instead of this method, which cannot take one. Note that this still occupies
    /// the <c>"javascript"</c> registration slot exactly as any <c>AddLocalKind</c> call would — if
    /// you don't delete it, a later <c>AddLocalKind("javascript", …)</c> throws "already registered"
    /// rather than being a no-op.
    /// </summary>
    /// <remarks>
    /// The options block accepts <c>appType</c> (<c>javascript</c> — the default — <c>vite</c>,
    /// <c>nextjs</c>, <c>node</c>, or <c>bun</c>), <c>appDirectory</c>, <c>runScript</c>,
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
    [Obsolete("UseJavaScript() does not change how \"javascript\" resolves - it's a built-in local " +
        "kind and resolves the same way with or without this call. Delete the call (do not just " +
        "add another one next to it - a later AddLocalKind(\"javascript\", ...) would then throw " +
        "\"already registered\"). To register a different ILocalResourceKind for \"javascript\" " +
        "(e.g. a test double), call AddLocalKind(\"javascript\", yourKind) directly instead.")]
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
    ///     .AsJavaScript(o => o.WithAppType(JavaScriptAppTypes.Vite).WithPort(3000));
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
