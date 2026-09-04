using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// Runs a <c>"local"</c>-sourced service written in Java, by handing its checkout to the .NET Aspire
/// Community Toolkit's Java integration (<c>AddJavaApp</c> + <c>WithMavenGoal</c>/
/// <c>WithGradleTask</c>). Registered for the <c>java</c> kind by
/// <see cref="JavaServiceSourcesBuilderExtensions.UseJava"/>.
/// </summary>
internal sealed class JavaLocalResourceKind : ILocalResourceKind
{
    /// <summary>The <c>kind:</c> value in <c>servicesources.yaml</c> this handler is registered for.</summary>
    public const string KindName = "java";

    // What the Community Toolkit's integration execs when no wrapper override is annotated, and the
    // extension Windows spells that same wrapper with. Mirrored here rather than read from it (they're
    // private) so the wrapper this checks for is the very file the resource would run.
    private const string MavenWrapper = "mvnw";
    private const string MavenWindowsExtension = ".cmd";
    private const string GradleWrapper = "gradlew";
    private const string GradleWindowsExtension = ".bat";

    /// <summary>
    /// The whole verdict on a service's <c>java:</c> block, the paths in it included: core calls
    /// this against the resolved checkout, immediately before <see cref="Resolve"/> and before the
    /// service has added anything, so a <c>workingDirectory</c> or a wrapper script that is not in
    /// the repository is reported here rather than as a bare exec failure from DCP much later.
    /// </summary>
    public void Validate(string serviceName, string repoRoot, object? rawConfig) =>
        Plan(serviceName, repoRoot, rawConfig).RequireCheckout();

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
        Plan(serviceName, repoRoot, rawConfig).Add(builder);

    /// <summary>
    /// Unconditional: every java options block builds the same resource cold as warm, for the
    /// reasons on <see cref="ResolveDeferred"/>. Nothing here reads the checkout or the config.
    /// </summary>
    public bool SupportsDeferredCheckout(object? rawConfig) => true;

    /// <summary>
    /// Deferral costs this kind nothing, so it is supported unconditionally. Everything the resource
    /// needs is in the committed catalog: the working directory and wrapper are paths under a repo
    /// root that is a pure function of the service name, and <c>java.port</c> is required, so the
    /// endpoint is fully known before any clone. Unlike the <c>dotnet</c> kind there is no launch
    /// profile to read — nothing about the resource is synthesised from a file in the repository —
    /// so a deferred java service is identical to a warm one, and only the checks below move.
    /// </summary>
    public DeferredLocalResource? ResolveDeferred(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        var plan = Plan(serviceName, repoRoot, rawConfig);

        return new DeferredLocalResource
        {
            Service = plan.Add(builder),
            ValidateCheckout = plan.RequireCheckout,
        };
    }

    /// <summary>
    /// Everything decidable without the repository on disk: the options block, and the absolute
    /// paths the resource will run from. Deliberately free of filesystem access, so the same plan
    /// serves a warm checkout and one that has not been cloned yet.
    /// </summary>
    private static JavaPlan Plan(string serviceName, string repoRoot, object? rawConfig)
    {
        var options = JavaKindOptions.Parse(serviceName, rawConfig);
        var workingDirectory = Path.GetFullPath(Path.Combine(repoRoot, options.WorkingDirectory));

        // Null for a jar: "java -jar" runs no wrapper, so there is none to look for.
        var wrapper = options.RunMode.Kind == JavaRunModeKind.Jar
            ? null
            : PlanWrapper(repoRoot, workingDirectory, options);

        return new JavaPlan(serviceName, options, workingDirectory, wrapper);
    }

    /// <summary>
    /// Where the wrapper script the resource will exec is going to be, and what to say if it turns
    /// out not to be there. Handed to the integration explicitly (via <c>WithWrapperPath</c>) rather
    /// than left to its default, so the file checked is provably the one the resource runs: the
    /// integration sets the command from this path without checking it, and there is no fallback to
    /// a system-wide <c>mvn</c>/<c>gradle</c>.
    /// </summary>
    private static PlannedWrapper PlanWrapper(
        string repoRoot, string workingDirectory, ValidatedJavaKindOptions options)
    {
        var (runModeField, wrapperName, windowsExtension) = options.RunMode.Kind switch
        {
            JavaRunModeKind.MavenGoal => ("mavenGoal", MavenWrapper, MavenWindowsExtension),
            JavaRunModeKind.GradleTask => ("gradleTask", GradleWrapper, GradleWindowsExtension),
            _ => throw new InvalidOperationException(
                $"Java run mode '{options.RunMode.Kind}' runs no wrapper script."),
        };

        // A configured wrapperPath is relative to the repository root — the monorepo case it exists
        // for keeps the wrapper above the project — while the default sits in the working directory.
        // Both go through WrapperForPlatform, so an override is named for this platform exactly as the
        // default is: whichever of the two is in play, the file looked for is the runnable one.
        var relativeWrapper = WrapperForPlatform(
            options.WrapperPath ?? wrapperName, windowsExtension, OperatingSystem.IsWindows());
        var path = Path.GetFullPath(
            Path.Combine(options.WrapperPath is null ? workingDirectory : repoRoot, relativeWrapper));

        return new PlannedWrapper(path, relativeWrapper, runModeField, options.WrapperPath);
    }

    private sealed record PlannedWrapper(
        string Path, string RelativeName, string RunModeField, string? ConfiguredPath);

    /// <summary>
    /// A resolved java service: the paths it will run from, the checks that need those paths to
    /// exist, and the resource itself. Split that way because the two callers need the halves at
    /// different moments — the eager path checks from <see cref="Validate"/> and builds from
    /// <see cref="Resolve"/>, the deferred path builds now and checks once the clone has landed.
    /// </summary>
    private sealed record JavaPlan(
        string ServiceName,
        ValidatedJavaKindOptions Options,
        string WorkingDirectory,
        PlannedWrapper? Wrapper)
    {
        /// <summary>
        /// The checks that need the repository on disk. On the eager path they are what
        /// <see cref="ILocalResourceKind.Validate"/> runs, before anything reaches the app model; on
        /// the deferred path core runs them after the clone, where they surface as the service's
        /// resource state rather than as an exception out of composition.
        /// </summary>
        public void RequireCheckout()
        {
            if (!Directory.Exists(WorkingDirectory))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{ServiceName}': java.workingDirectory '{Options.WorkingDirectory}' resolves to " +
                    $"'{WorkingDirectory}', which does not exist in the service's checkout.");
            }

            if (Wrapper is null || File.Exists(Wrapper.Path))
            {
                return;
            }

            if (Wrapper.ConfiguredPath is not null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{ServiceName}': java.wrapperPath '{Wrapper.ConfiguredPath}' resolves to " +
                    $"'{Wrapper.Path}', which does not exist in the service's checkout.");
            }

            throw new ServiceSourcesConfigurationException(
                $"Service '{ServiceName}': java.{Wrapper.RunModeField} runs the repository's own " +
                $"'{Wrapper.RelativeName}' wrapper script, but '{Wrapper.Path}' does not exist. Commit the " +
                "wrapper beside the project, or set java.wrapperPath to where this checkout keeps it — a " +
                "multi-module repository usually has a single wrapper at its root, relative to which " +
                "java.wrapperPath is read.");
        }

        public IResourceBuilder<IResourceWithServiceDiscovery> Add(IDistributedApplicationBuilder builder)
        {
            // One dispatch, so a run mode added later can't compile into a resource that was added but
            // never told how to start.
            var javaApp = (Options.RunMode.Kind, Wrapper) switch
            {
                // AddJavaApp's jar overload applies both the jar path and the args itself.
                (JavaRunModeKind.Jar, _) =>
                    builder.AddJavaApp(ServiceName, WorkingDirectory, Options.RunMode.Value, Options.Args),

                // WithWrapperPath first: WithMavenGoal/WithGradleTask read the wrapper annotation as they
                // run and set the resource's command from it there and then, so annotating afterwards
                // would leave the command pointing at the default wrapper.
                (JavaRunModeKind.MavenGoal, { } mavenWrapper) =>
                    builder.AddJavaApp(ServiceName, WorkingDirectory)
                        .WithWrapperPath(mavenWrapper.Path)
                        .WithMavenGoal(Options.RunMode.Value, Options.Args),
                (JavaRunModeKind.GradleTask, { } gradleWrapper) =>
                    builder.AddJavaApp(ServiceName, WorkingDirectory)
                        .WithWrapperPath(gradleWrapper.Path)
                        .WithGradleTask(Options.RunMode.Value, Options.Args),
                _ => throw new InvalidOperationException($"Unhandled Java run mode '{Options.RunMode.Kind}'."),
            };

            // AddJavaApp adds no endpoint of its own, so declare the one the service listens on — the
            // whole point of AddService() is handing consumers something they can WithReference().
            // java.port is required, so this is as true of a deferred service as of a warm one.
            javaApp.WithHttpEndpoint(targetPort: Options.Port);

            return javaApp;
        }
    }

    /// <summary>
    /// <paramref name="wrapperPath"/> as this platform spells it: on Windows a wrapper named without
    /// an extension gets the run mode's <c>.cmd</c>/<c>.bat</c> one, which is what the repository
    /// actually commits it as — <c>mvnw</c> and <c>gradlew</c> are POSIX shell scripts, and Windows
    /// cannot exec them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole reason <c>wrapperPath</c> can be written POSIX-style: one
    /// <c>servicesources.yaml</c> is shared by a team on every platform, so the value can only be
    /// spelled one way, and the plain name is the one a developer writes. A value that <em>does</em>
    /// name an extension is left alone — it names a specific file, including the Windows wrapper
    /// itself for a repository that commits only that.
    /// </para>
    /// <para>
    /// <paramref name="isWindows"/> is a parameter rather than read from
    /// <see cref="OperatingSystem.IsWindows"/> here so the Windows naming is testable from the
    /// Linux/macOS run this repository's tests and CI do.
    /// </para>
    /// </remarks>
    internal static string WrapperForPlatform(string wrapperPath, string windowsExtension, bool isWindows) =>
        isWindows && Path.GetExtension(wrapperPath).Length == 0 ? wrapperPath + windowsExtension : wrapperPath;
}
