using Aspire.Hosting.ServiceSources.Java;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Adds Java support to <c>AddService()</c>'s <c>"local"</c> source.
/// </summary>
public static class JavaServiceSourcesBuilderExtensions
{
    /// <summary>
    /// Registers the <c>java</c> local kind, so <c>AddService(name)</c> can clone and run a service
    /// whose <c>servicesources.yaml</c> entry declares <c>kind: java</c>. The service's <c>java:</c>
    /// block says how to run its checkout:
    /// <code>
    /// services:
    ///   java-api:
    ///     repository: https://github.com/example/java-api
    ///     kind: java
    ///     java:
    ///       workingDirectory: .          # optional, defaults to the repository root
    ///       mavenGoal: spring-boot:run   # or gradleTask: bootRun, or jarPath: target/app.jar
    ///       args: ["-Dspring-boot.run.profiles=dev"]   # optional
    ///       port: 8080
    /// </code>
    /// Call this before the <c>AddService(...)</c> calls it applies to, and at most once per builder
    /// — a second call throws, since registering the same kind twice is a mistake rather than a
    /// no-op.
    /// </summary>
    /// <remarks>
    /// Only the <c>"local"</c> source consults local kinds. A Java service reached over the
    /// <c>url</c>, <c>kubernetes</c>, or <c>container</c> source needs no registration at all —
    /// those sources are already language-agnostic.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The <c>java</c> kind is already registered on this builder.
    /// </exception>
    [AspireExport]
    public static IDistributedApplicationBuilder UseJava(this IDistributedApplicationBuilder builder) =>
        builder.AddLocalKind(JavaLocalResourceKind.KindName, new JavaLocalResourceKind());
}
