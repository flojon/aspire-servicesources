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
    /// Call this once, before the first <c>AddService()</c> call. An unregistered kind is reported
    /// by that service's own <c>AddService()</c>, and registering up front is what lets the very
    /// first one report it before any checkout has begun — the first <c>AddService()</c> starts
    /// prefetching every <c>"local"</c> service's checkout at once. The options block accepts
    /// <c>appType</c> (<c>javascript</c> — the default — <c>vite</c>, <c>nextjs</c>, <c>node</c>,
    /// or <c>bun</c>), <c>appDirectory</c>, <c>runScript</c>, <c>scriptPath</c>,
    /// <c>packageManager</c>, <c>port</c>, <c>targetPort</c>, and <c>portEnv</c>; see this
    /// package's README for what each one means.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.UseJavaScript();
    ///
    /// var frontend = builder.AddService("frontend");
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
