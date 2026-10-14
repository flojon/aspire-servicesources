using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Java;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// The hosting package a <c>java</c> service is handed to: the Community Toolkit's
/// <c>CommunityToolkit.Aspire.Hosting.Java</c>, or Aspire's own <c>Aspire.Hosting.Java</c>. Both
/// expose the same shape — <c>AddJavaApp</c>, <c>WithWrapperPath</c>, <c>WithMavenGoal</c>,
/// <c>WithGradleTask</c> — over different resource types, so the kind plans once and lets whichever
/// the AppHost installed build the resource.
/// </summary>
internal abstract class JavaIntegration
{
    private static readonly Lazy<JavaIntegration> InstalledIntegration = new(Detect);

    /// <summary>
    /// The integration this AppHost has, decided once per process: the official package when it is
    /// loadable, otherwise the Community Toolkit's.
    /// </summary>
    /// <remarks>
    /// The official one wins when both are present, since referencing it is the deliberate step —
    /// the Toolkit's is the one a project already had. The Toolkit's is also the fallback when
    /// neither is present, so a missing package still fails as a load of the Toolkit's assembly,
    /// which <see cref="GuestLanguagePackages"/> turns into the message naming both.
    /// </remarks>
    public static JavaIntegration Installed => InstalledIntegration.Value;

    /// <summary>The package id, for messages.</summary>
    public abstract string PackageId { get; }

    /// <summary>The type every resource <see cref="Add"/> returns is, or derives from.</summary>
    public abstract Type ResourceType { get; }

    /// <summary>
    /// Adds the Java app, configured to start the way <paramref name="runMode"/> says, through the
    /// wrapper at <paramref name="wrapperPath"/> for a Maven goal or Gradle task.
    /// </summary>
    public abstract IResourceBuilder<IResourceWithServiceDiscovery> Add(
        IDistributedApplicationBuilder builder,
        string name,
        string workingDirectory,
        JavaRunMode runMode,
        string? wrapperPath,
        string[] args,
        int port,
        string scheme);

    private static JavaIntegration Detect() =>
        (JavaIntegration?)OfficialJavaIntegration.TryLoad() ?? new CommunityToolkitJavaIntegration();

    /// <summary>
    /// What both integrations leave to the caller: neither adds an endpoint, and an executable
    /// resource does not get the OTLP wiring <c>AddProject</c> gives a project for free.
    /// </summary>
    protected static IResourceBuilder<IResourceWithServiceDiscovery> Finish<T>(
        IResourceBuilder<T> javaApp, int port, string scheme)
        where T : class, IResourceWithEndpoints, IResourceWithEnvironment, IResourceWithServiceDiscovery
    {
        // Named after the scheme (matching ContainerSource/KubernetesSource) rather than always
        // "http", so a https-scheme service is the one GetServiceEndpoint() and GetEndpoint("https") find.
        javaApp.WithEndpoint(targetPort: port, scheme: scheme, name: scheme);
        javaApp.WithOtlpExporter();

        return javaApp;
    }
}

/// <summary>
/// <c>CommunityToolkit.Aspire.Hosting.Java</c>, which core compiles against. Its types appear only
/// inside method bodies, so the assembly is loaded only once a <c>java</c> service resolves.
/// </summary>
internal sealed class CommunityToolkitJavaIntegration : JavaIntegration
{
    public override string PackageId => "CommunityToolkit.Aspire.Hosting.Java";

    public override Type ResourceType => typeof(JavaAppExecutableResource);

    public override IResourceBuilder<IResourceWithServiceDiscovery> Add(
        IDistributedApplicationBuilder builder,
        string name,
        string workingDirectory,
        JavaRunMode runMode,
        string? wrapperPath,
        string[] args,
        int port,
        string scheme)
    {
        var javaApp = (runMode.Kind, wrapperPath) switch
        {
            // AddJavaApp's jar overload applies both the jar path and the args itself.
            (JavaRunModeKind.Jar, _) =>
                JavaAppHostingExtension.AddJavaApp(builder, name, workingDirectory, runMode.Value, args),

            // WithWrapperPath first: WithMavenGoal/WithGradleTask read the wrapper annotation as they
            // run and set the resource's command from it there and then, so annotating afterwards
            // would leave the command pointing at the default wrapper.
            (JavaRunModeKind.MavenGoal, { } mavenWrapper) =>
                JavaAppHostingExtension.AddJavaApp(builder, name, workingDirectory)
                    .WithWrapperPath(mavenWrapper)
                    .WithMavenGoal(runMode.Value, args),
            (JavaRunModeKind.GradleTask, { } gradleWrapper) =>
                JavaAppHostingExtension.AddJavaApp(builder, name, workingDirectory)
                    .WithWrapperPath(gradleWrapper)
                    .WithGradleTask(runMode.Value, args),
            _ => throw new InvalidOperationException($"Unhandled Java run mode '{runMode.Kind}'."),
        };

        return Finish(javaApp, port, scheme);
    }
}

/// <summary>
/// Aspire's own <c>Aspire.Hosting.Java</c>. Its <c>AddJavaApp</c> has the Community Toolkit's exact
/// signature in the same namespace, so both are called through their declaring class.
/// </summary>
internal sealed class OfficialJavaIntegration : JavaIntegration
{
    internal const string AssemblyName = "Aspire.Hosting.Java";

    /// <summary>The version core compiles against; an older one fails to bind.</summary>
    internal const string MinimumVersion = "13.6.1-preview.1.26506.6";

    public override string PackageId => AssemblyName;

    public override Type ResourceType => typeof(JavaAppResource);

    /// <summary>
    /// The official integration if this AppHost can load it, otherwise <see langword="null"/>.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The package is installed, but older than the version core was built against.
    /// </exception>
    public static OfficialJavaIntegration? TryLoad()
    {
        try
        {
            Probe();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException ex)
        {
            // The runtime refuses an assembly version below the one core references.
            throw ServiceSourcesConfigurationException.For(
                $"Services with kind 'java' run through {Raw.Literal(AssemblyName)} when the AppHost references it, but the installed version is older than {Raw.Literal(MinimumVersion)}, which this package needs. Raise {Raw.Literal(AssemblyName)} to {Raw.Literal(MinimumVersion)} or newer, or remove it to use {Raw.Literal("CommunityToolkit.Aspire.Hosting.Java")} instead.",
                ex);
        }

        return new OfficialJavaIntegration();
    }

    // Kept out of line so loading the assembly happens here, inside TryLoad's handlers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Probe() => GC.KeepAlive(typeof(JavaAppResource));

    public override IResourceBuilder<IResourceWithServiceDiscovery> Add(
        IDistributedApplicationBuilder builder,
        string name,
        string workingDirectory,
        JavaRunMode runMode,
        string? wrapperPath,
        string[] args,
        int port,
        string scheme)
    {
        var javaApp = (runMode.Kind, wrapperPath) switch
        {
            (JavaRunModeKind.Jar, _) =>
                JavaHostingExtensions.AddJavaApp(builder, name, workingDirectory, runMode.Value, args),
            (JavaRunModeKind.MavenGoal, { } mavenWrapper) =>
                JavaHostingExtensions.AddJavaApp(builder, name, workingDirectory)
                    .WithWrapperPath(mavenWrapper)
                    .WithMavenGoal(runMode.Value, args),
            (JavaRunModeKind.GradleTask, { } gradleWrapper) =>
                JavaHostingExtensions.AddJavaApp(builder, name, workingDirectory)
                    .WithWrapperPath(gradleWrapper)
                    .WithGradleTask(runMode.Value, args),
            _ => throw new InvalidOperationException($"Unhandled Java run mode '{runMode.Kind}'."),
        };

        return Finish(javaApp, port, scheme);
    }
}
