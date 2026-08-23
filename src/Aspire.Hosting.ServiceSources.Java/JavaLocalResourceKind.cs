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

    // What the Community Toolkit's integration execs when no wrapper override is annotated. Mirrored
    // here rather than read from it (they're private) so the wrapper this checks for is the very file
    // the resource would run.
    private static string DefaultMavenWrapper => OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw";

    private static string DefaultGradleWrapper => OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew";

    public void Validate(string serviceName, object? rawConfig) => JavaKindOptions.Parse(serviceName, rawConfig);

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        var options = JavaKindOptions.Parse(serviceName, rawConfig);
        var workingDirectory = ResolveWorkingDirectory(serviceName, repoRoot, options.WorkingDirectory);

        // Resolved and checked before anything reaches the app model, so a wrapper that isn't in the
        // checkout is reported from here rather than as a bare exec failure from DCP much later. Null
        // for a jar: "java -jar" runs no wrapper, so there is none to look for.
        var wrapper = options.RunMode.Kind == JavaRunModeKind.Jar
            ? null
            : ResolveWrapper(serviceName, repoRoot, workingDirectory, options);

        // One dispatch, so a run mode added later can't compile into a resource that was added but
        // never told how to start.
        var javaApp = (options.RunMode.Kind, wrapper) switch
        {
            // AddJavaApp's jar overload applies both the jar path and the args itself.
            (JavaRunModeKind.Jar, _) =>
                builder.AddJavaApp(serviceName, workingDirectory, options.RunMode.Value, options.Args),

            // WithWrapperPath first: WithMavenGoal/WithGradleTask read the wrapper annotation as they
            // run and set the resource's command from it there and then, so annotating afterwards
            // would leave the command pointing at the default wrapper.
            (JavaRunModeKind.MavenGoal, { } mavenWrapper) =>
                builder.AddJavaApp(serviceName, workingDirectory)
                    .WithWrapperPath(mavenWrapper)
                    .WithMavenGoal(options.RunMode.Value, options.Args),
            (JavaRunModeKind.GradleTask, { } gradleWrapper) =>
                builder.AddJavaApp(serviceName, workingDirectory)
                    .WithWrapperPath(gradleWrapper)
                    .WithGradleTask(options.RunMode.Value, options.Args),
            _ => throw new InvalidOperationException($"Unhandled Java run mode '{options.RunMode.Kind}'."),
        };

        // AddJavaApp adds no endpoint of its own, so declare the one the service listens on — the
        // whole point of AddService() is handing consumers something they can WithReference().
        javaApp.WithHttpEndpoint(targetPort: options.Port);

        return javaApp;
    }

    /// <summary>
    /// Checked here rather than in <see cref="Validate"/> only because
    /// <see cref="ILocalResourceKind.Validate"/> isn't handed the checkout directory. The checkout
    /// itself does exist by then — core resolves the repo root, then calls <c>Validate</c>, then
    /// <c>Resolve</c> — so this check belongs in <c>Validate</c> and would move there as soon as the
    /// signature carries <c>repoRoot</c> (flojon/aspire-servicesources#63). Until then it runs in
    /// <see cref="Resolve"/>, which core wraps in a "report this from Validate instead" message that
    /// can't be acted on.
    /// </summary>
    private static string ResolveWorkingDirectory(string serviceName, string repoRoot, string relativeDirectory)
    {
        var workingDirectory = Path.GetFullPath(Path.Combine(repoRoot, relativeDirectory));

        if (!Directory.Exists(workingDirectory))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.workingDirectory '{relativeDirectory}' resolves to " +
                $"'{workingDirectory}', which does not exist in the service's checkout.");
        }

        return workingDirectory;
    }

    /// <summary>
    /// The absolute path of the wrapper script the resource will exec, verified to be in the checkout.
    /// Handed to the integration explicitly (via <c>WithWrapperPath</c>) rather than left to its
    /// default, so the file checked here is provably the one the resource runs: the integration sets
    /// the command from this path without checking it, and there is no fallback to a system-wide
    /// <c>mvn</c>/<c>gradle</c>.
    /// </summary>
    /// <remarks>
    /// Checked from <see cref="Resolve"/> for the same reason as the working directory — see
    /// <see cref="ResolveWorkingDirectory"/>.
    /// </remarks>
    private static string ResolveWrapper(
        string serviceName, string repoRoot, string workingDirectory, ValidatedJavaKindOptions options)
    {
        var (runModeField, defaultWrapperName) = options.RunMode.Kind switch
        {
            JavaRunModeKind.MavenGoal => ("mavenGoal", DefaultMavenWrapper),
            JavaRunModeKind.GradleTask => ("gradleTask", DefaultGradleWrapper),
            _ => throw new InvalidOperationException(
                $"Java run mode '{options.RunMode.Kind}' runs no wrapper script."),
        };

        // A configured wrapperPath is relative to the repository root — the monorepo case it exists
        // for keeps the wrapper above the project — while the default sits in the working directory.
        var wrapper = options.WrapperPath is null
            ? Path.GetFullPath(Path.Combine(workingDirectory, defaultWrapperName))
            : Path.GetFullPath(Path.Combine(repoRoot, options.WrapperPath));

        if (File.Exists(wrapper))
        {
            return wrapper;
        }

        if (options.WrapperPath is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.wrapperPath '{options.WrapperPath}' resolves to '{wrapper}', " +
                "which does not exist in the service's checkout.");
        }

        throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': java.{runModeField} runs the repository's own '{defaultWrapperName}' " +
            $"wrapper script, but '{wrapper}' does not exist. Commit the wrapper beside the project, or set " +
            "java.wrapperPath to where this checkout keeps it — a multi-module repository usually has a single " +
            "wrapper at its root, relative to which java.wrapperPath is read.");
    }
}
