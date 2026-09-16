using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Java;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Adds Java support to <c>AddService()</c>'s <c>"local"</c> source.
/// </summary>
public static class JavaServiceSourcesBuilderExtensions
{
    /// <summary>
    /// Redundant today: <c>java</c> is a built-in local kind, and this registers an instance of the
    /// same built-in <see cref="JavaLocalResourceKind"/> type <c>AddService()</c> already falls back
    /// to when nothing was registered for the name (see <see cref="Sources.LocalKindRegistry"/>).
    /// Calling it changes nothing about how a <c>kind: java</c> service resolves — delete the call.
    /// To use a *different* <see cref="ILocalResourceKind"/> for <c>"java"</c> (e.g. a test double),
    /// call <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/> directly with your own handler
    /// instead of this method, which cannot take one. Note that this still occupies the <c>"java"</c>
    /// registration slot exactly as any <c>AddLocalKind</c> call would — if you don't delete it, a
    /// later <c>AddLocalKind("java", …)</c> throws "already registered" rather than being a no-op.
    /// </summary>
    /// <remarks>
    /// Only the <c>"local"</c> source consults local kinds. A Java service reached over the
    /// <c>url</c>, <c>kubernetes</c>, or <c>container</c> source needs no registration at all —
    /// those sources are already language-agnostic.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The <c>java</c> kind is already registered on this builder.
    /// </exception>
    [Obsolete("UseJava() does not change how \"java\" resolves - it's a built-in local kind and " +
        "resolves the same way with or without this call. Delete the call (do not just add another " +
        "one next to it - a later AddLocalKind(\"java\", ...) would then throw \"already " +
        "registered\"). To register a different ILocalResourceKind for \"java\" (e.g. a test " +
        "double), call AddLocalKind(\"java\", yourKind) directly instead.")]
    [AspireExport]
    public static IDistributedApplicationBuilder UseJava(this IDistributedApplicationBuilder builder) =>
        builder.AddLocalKind(JavaLocalResourceKind.KindName, new JavaLocalResourceKind());

    /// <summary>
    /// Configures this code-declared service to run as a <c>java</c>-kind <c>"local"</c> service,
    /// through a typed options handle instead of a raw dictionary. Sugar over
    /// <c>WithKind("java", …)</c> — calling this after <c>WithKind</c> (on either) throws the same
    /// "already called" error <c>WithKind</c> itself would, since this <em>is</em> that call.
    /// </summary>
    /// <example>
    /// <code>
    /// catalog.AddService("catalog")
    ///     .WithRepository("https://github.com/spring-projects/spring-petclinic")
    ///     .AsJava(o => o.WithMavenGoal("spring-boot:run").WithPort(8080));
    /// </code>
    /// </example>
    [AspireExport]
    public static ServiceDefinitionBuilder AsJava(
        this ServiceDefinitionBuilder builder, Action<JavaKindOptionsBuilder> configure)
    {
        var options = new JavaKindOptionsBuilder();
        configure(options);
        return builder.WithKind(JavaLocalResourceKind.KindName, options.Build());
    }
}
