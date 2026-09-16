using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Java;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Adds Java support to <c>AddService()</c>'s <c>"local"</c> source.
/// </summary>
public static class JavaServiceSourcesBuilderExtensions
{
    /// <summary>
    /// No-op today: <c>java</c> is a built-in local kind, and this registers the exact same
    /// <see cref="JavaLocalResourceKind"/> instance <c>AddService()</c> already falls back to when
    /// nothing was registered for the name (see <see cref="Sources.LocalKindRegistry"/>). Calling it
    /// changes nothing about how a <c>kind: java</c> service resolves — delete the call. To use a
    /// *different* <see cref="ILocalResourceKind"/> for <c>"java"</c> (e.g. a test double), call
    /// <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/> directly with your own handler
    /// instead of this method, which cannot take one.
    /// </summary>
    /// <remarks>
    /// Only the <c>"local"</c> source consults local kinds. A Java service reached over the
    /// <c>url</c>, <c>kubernetes</c>, or <c>container</c> source needs no registration at all —
    /// those sources are already language-agnostic.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The <c>java</c> kind is already registered on this builder.
    /// </exception>
    [Obsolete("UseJava() is a no-op: \"java\" is a built-in local kind and resolves without it. " +
        "Delete the call. To register a different ILocalResourceKind for \"java\" (e.g. a test " +
        "double), call AddLocalKind(\"java\", yourKind) directly.")]
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
