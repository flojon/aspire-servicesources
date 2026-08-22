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

    public void Validate(string serviceName, object? rawConfig) => JavaKindOptions.Parse(serviceName, rawConfig);

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
    {
        var options = JavaKindOptions.Parse(serviceName, rawConfig);
        var workingDirectory = ResolveWorkingDirectory(serviceName, repoRoot, options.WorkingDirectory);

        // One dispatch, so a run mode added later can't compile into a resource that was added but
        // never told how to start.
        var javaApp = options.RunMode.Kind switch
        {
            // AddJavaApp's jar overload applies both the jar path and the args itself.
            JavaRunModeKind.Jar =>
                builder.AddJavaApp(serviceName, workingDirectory, options.RunMode.Value, options.Args),
            JavaRunModeKind.MavenGoal =>
                builder.AddJavaApp(serviceName, workingDirectory).WithMavenGoal(options.RunMode.Value, options.Args),
            JavaRunModeKind.GradleTask =>
                builder.AddJavaApp(serviceName, workingDirectory).WithGradleTask(options.RunMode.Value, options.Args),
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
    /// itself does exist by then — core calls <c>Validate</c> straight after resolving the repo root,
    /// still inside the phase that aggregates every service's failures — so this check belongs there
    /// and would move as soon as the signature carries <c>repoRoot</c>. Until then it runs in
    /// <see cref="Resolve"/>, where a failure aborts a partially populated app model and core wraps
    /// it in a "report this from Validate instead" message that can't be acted on.
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
}
