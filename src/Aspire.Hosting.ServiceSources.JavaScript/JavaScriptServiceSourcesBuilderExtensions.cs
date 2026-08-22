using Aspire.Hosting;

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
    /// Call this once, before the <c>AddService()</c> call for any such service — a kind with no
    /// registered handler fails at startup, before anything is cloned. The options block accepts
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
}
