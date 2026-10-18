using System.Reflection;
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
    // PublicationOnly: a failure to settle (a too-old preview, a broken dependency) is reported to
    // whoever asks, and asked again after the AppHost is fixed, rather than cached for the process.
    private static readonly Lazy<JavaIntegration> InstalledIntegration =
        new(Detect, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>
    /// The integration this AppHost has, decided once per process: the Community Toolkit's when it
    /// is loadable, otherwise Aspire's official one.
    /// </summary>
    /// <remarks>
    /// The Toolkit's wins when both are present. The official package can arrive without anyone
    /// choosing it - transitively, or through a later <c>dotnet add package</c> - and picking it
    /// then would change the resource type <c>Unwrap&lt;T&gt;</c> sees under an AppHost that did
    /// not change. Referencing only the official package is the way to opt into it. The Toolkit's is
    /// also the fallback when neither is present, so a missing package still fails as a load of the
    /// Toolkit's assembly, which <see cref="GuestLanguagePackages"/> turns into the message naming both.
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
        Choose(CommunityToolkitJavaIntegration.TryLoad, OfficialJavaIntegration.TryLoad);

    /// <summary>The precedence <see cref="Installed"/> documents, with the probes injected.</summary>
    internal static JavaIntegration Choose(Func<JavaIntegration?> communityToolkit, Func<JavaIntegration?> official) =>
        communityToolkit() ?? official() ?? new CommunityToolkitJavaIntegration();

    /// <summary>
    /// Whether a load failure names <paramref name="assemblyName"/> itself, rather than something
    /// it depends on. <c>FileName</c> is the full display name, "Name, Version=…, PublicKeyToken=…".
    /// </summary>
    protected static bool Names(string? fileName, string assemblyName) =>
        fileName is not null
        && string.Equals(fileName.Split(',')[0].Trim(), assemblyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one plan both integrations follow. Only the types differ between them, so each supplies
    /// the calls and this owns the order: <c>WithWrapperPath</c> first, because
    /// <c>WithMavenGoal</c>/<c>WithGradleTask</c> read the wrapper annotation as they run and set the
    /// resource's command from it there and then, so annotating afterwards would leave the command
    /// pointing at the default wrapper.
    /// </summary>
    protected static IResourceBuilder<IResourceWithServiceDiscovery> Build<T>(
        JavaRunMode runMode,
        string? wrapperPath,
        string[] args,
        int port,
        string scheme,
        Func<IResourceBuilder<T>> addJar,
        Func<IResourceBuilder<T>> addBare,
        Func<IResourceBuilder<T>, string, IResourceBuilder<T>> withWrapperPath,
        Func<IResourceBuilder<T>, string, string[], IResourceBuilder<T>> withMavenGoal,
        Func<IResourceBuilder<T>, string, string[], IResourceBuilder<T>> withGradleTask)
        where T : class, IResourceWithEndpoints, IResourceWithEnvironment, IResourceWithServiceDiscovery
    {
        var javaApp = (runMode.Kind, wrapperPath) switch
        {
            // AddJavaApp's jar overload applies both the jar path and the args itself.
            (JavaRunModeKind.Jar, _) => addJar(),
            (JavaRunModeKind.MavenGoal, { } mavenWrapper) =>
                withMavenGoal(withWrapperPath(addBare(), mavenWrapper), runMode.Value, args),
            (JavaRunModeKind.GradleTask, { } gradleWrapper) =>
                withGradleTask(withWrapperPath(addBare(), gradleWrapper), runMode.Value, args),
            _ => throw new InvalidOperationException($"Unhandled Java run mode '{runMode.Kind}'."),
        };

        return Finish(javaApp, port, scheme);
    }

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
    internal const string AssemblyName = "CommunityToolkit.Aspire.Hosting.Java";

    public override string PackageId => AssemblyName;

    public override Type ResourceType => typeof(JavaAppExecutableResource);

    /// <summary>
    /// The Toolkit's integration if this AppHost has its assembly, otherwise <see langword="null"/>.
    /// Anything else that goes wrong loading it is left to surface where the type is used, which
    /// is where <see cref="GuestLanguagePackages"/> reports it.
    /// </summary>
    public static CommunityToolkitJavaIntegration? TryLoad()
    {
        try
        {
            Probe();
        }
        catch (FileNotFoundException ex) when (Names(ex.FileName, AssemblyName))
        {
            return null;
        }

        return new CommunityToolkitJavaIntegration();
    }

    // Kept out of line so loading the assembly happens here, inside TryLoad's handler.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Probe() => GC.KeepAlive(typeof(JavaAppExecutableResource));

    public override IResourceBuilder<IResourceWithServiceDiscovery> Add(
        IDistributedApplicationBuilder builder,
        string name,
        string workingDirectory,
        JavaRunMode runMode,
        string? wrapperPath,
        string[] args,
        int port,
        string scheme) =>
        Build(
            runMode, wrapperPath, args, port, scheme,
            () => JavaAppHostingExtension.AddJavaApp(builder, name, workingDirectory, runMode.Value, args),
            () => JavaAppHostingExtension.AddJavaApp(builder, name, workingDirectory),
            (app, wrapper) => app.WithWrapperPath(wrapper),
            (app, goal, goalArgs) => app.WithMavenGoal(goal, goalArgs),
            (app, task, taskArgs) => app.WithGradleTask(task, taskArgs));
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
    /// The package is installed but older than the version core was built against, or it is
    /// installed and one of its own dependencies cannot be loaded.
    /// </exception>
    public static OfficialJavaIntegration? TryLoad()
    {
        string? installed;

        try
        {
            installed = InstalledVersion();
        }
        catch (FileNotFoundException ex) when (Names(ex.FileName, AssemblyName))
        {
            return null;
        }
        catch (FileLoadException ex) when (Names(ex.FileName, AssemblyName))
        {
            // The runtime refuses an assembly version below the one core references.
            throw TooOld(null, ex);
        }
        catch (IOException ex) when (ex is FileNotFoundException or FileLoadException)
        {
            // The package is there; something it needs is not. Saying it is missing or old would
            // send the reader to change the one thing that is fine.
            var dependency = ex is FileNotFoundException notFound
                ? notFound.FileName
                : ((FileLoadException)ex).FileName;

            throw ServiceSourcesConfigurationException.For(
                $"Services with kind 'java' run through {Raw.Literal(AssemblyName)} when the AppHost references it, but it could not be loaded because its dependency {Raw.Escaped(dependency ?? "(unnamed)")} could not be. Fix that reference, or remove {Raw.Literal(AssemblyName)} to use {Raw.Literal(CommunityToolkitJavaIntegration.AssemblyName)} instead.",
                ex).AsUnsettledResourceType();
        }

        // A preview cut before a release carries that release's assembly version, so an older one
        // binds without a FileLoadException and fails later, on the member it does not have.
        if (installed is not null && PackageVersionComparison.IsOlder(installed, MinimumVersion))
        {
            throw TooOld(installed, null);
        }

        return new OfficialJavaIntegration();
    }

    private static ServiceSourcesConfigurationException TooOld(string? installed, Exception? inner)
    {
        var found = installed is null
            ? Raw.Compose($"")
            : Raw.Compose($" ({Raw.Escaped(installed)} is installed)");

        return ServiceSourcesConfigurationException.For(
            $"Services with kind 'java' run through {Raw.Literal(AssemblyName)} when the AppHost references it, but the installed version{found} is older than {Raw.Literal(MinimumVersion)}, which this package needs. Raise {Raw.Literal(AssemblyName)} to {Raw.Literal(MinimumVersion)} or newer, or remove it to use {Raw.Literal(CommunityToolkitJavaIntegration.AssemblyName)} instead.",
            inner).AsUnsettledResourceType();
    }

    /// <summary>
    /// The installed package's version, or <see langword="null"/> when the assembly does not say.
    /// Kept out of line so loading the assembly happens here, inside <see cref="TryLoad"/>'s handlers.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? InstalledVersion() =>
        typeof(JavaAppResource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public override IResourceBuilder<IResourceWithServiceDiscovery> Add(
        IDistributedApplicationBuilder builder,
        string name,
        string workingDirectory,
        JavaRunMode runMode,
        string? wrapperPath,
        string[] args,
        int port,
        string scheme) =>
        Build(
            runMode, wrapperPath, args, port, scheme,
            () => JavaHostingExtensions.AddJavaApp(builder, name, workingDirectory, runMode.Value, args),
            () => JavaHostingExtensions.AddJavaApp(builder, name, workingDirectory),
            (app, wrapper) => app.WithWrapperPath(wrapper),
            (app, goal, goalArgs) => app.WithMavenGoal(goal, goalArgs),
            (app, task, taskArgs) => app.WithGradleTask(task, taskArgs));
}
