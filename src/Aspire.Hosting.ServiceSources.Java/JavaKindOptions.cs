namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// The <c>java:</c> block of a service's <c>servicesources.yaml</c> entry — how to run the
/// service's checkout once the <c>"local"</c> source has cloned it. Deserialized from the opaque
/// per-kind config block by <see cref="LocalKindConfig.Parse{T}"/>; validated by
/// <see cref="Parse"/>, which is what callers should use.
/// </summary>
internal sealed class JavaKindOptions
{
    /// <summary>
    /// Where in the checkout the Java project lives (the directory holding <c>pom.xml</c> /
    /// <c>build.gradle</c> and the <c>mvnw</c>/<c>gradlew</c> wrapper), relative to the repository
    /// root. Defaults to the repository root itself.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>The Maven goal to run the app with, e.g. <c>spring-boot:run</c>.</summary>
    public string? MavenGoal { get; set; }

    /// <summary>The Gradle task to run the app with, e.g. <c>bootRun</c>.</summary>
    public string? GradleTask { get; set; }

    /// <summary>
    /// A pre-built jar to run with <c>java -jar</c>, relative to <see cref="WorkingDirectory"/>.
    /// </summary>
    public string? JarPath { get; set; }

    /// <summary>
    /// Where the <c>mvnw</c>/<c>gradlew</c> wrapper script lives, relative to the repository root —
    /// like <see cref="WorkingDirectory"/>, and unlike <see cref="JarPath"/>, because the case this
    /// exists for is the monorepo one: a multi-project Gradle (or multi-module Maven) repository
    /// commits one wrapper at its root while the service itself sits further down. Defaults to the
    /// wrapper sitting in <see cref="WorkingDirectory"/> itself.
    /// </summary>
    public string? WrapperPath { get; set; }

    /// <summary>
    /// Extra arguments for whichever of the three run modes is configured — passed to the Maven
    /// wrapper, the Gradle wrapper, or the jar.
    /// </summary>
    public string[]? Args { get; set; }

    /// <summary>The port the Java app listens on.</summary>
    public int? Port { get; set; }

    /// <summary>
    /// Parses and fully validates a service's <c>java:</c> block. Called from both
    /// <see cref="JavaLocalResourceKind.Validate"/> (so a bad block is reported before this service
    /// has put anything in the app model) and <see cref="JavaLocalResourceKind.Resolve"/> — it is
    /// cheap and side-effect free, so parsing twice is preferable to smuggling state between the two
    /// calls.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The block is missing, malformed, contains an unknown property, names no run mode or more
    /// than one, omits <see cref="Port"/> or gives an out-of-range one, points
    /// <see cref="WorkingDirectory"/> or <see cref="WrapperPath"/> outside the checkout, or sets
    /// <see cref="WrapperPath"/> alongside <see cref="JarPath"/>.
    /// </exception>
    public static ValidatedJavaKindOptions Parse(string serviceName, object? rawConfig)
    {
        // A 'java:' key with nothing under it arrives as the same null an absent key does — the
        // loader hands the handler the block's value, not whether the key was written — so the
        // message has to cover both rather than sending the reader looking for a block they can see.
        var options = LocalKindConfig.Parse<JavaKindOptions>(rawConfig, serviceName)
            ?? throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' has kind 'java' but its 'java:' block in servicesources.yaml is " +
                "missing or empty. It must name how to run the service, e.g. 'mavenGoal: spring-boot:run' " +
                "and 'port: 8080'.");

        var runMode = ResolveRunMode(serviceName, options);
        var port = ValidatePort(serviceName, options.Port);
        var workingDirectory = ValidateWorkingDirectory(serviceName, options.WorkingDirectory);
        var wrapperPath = ValidateWrapperPath(serviceName, options.WrapperPath, runMode);

        return new ValidatedJavaKindOptions(workingDirectory, runMode, options.Args ?? [], port, wrapperPath);
    }

    private static JavaRunMode ResolveRunMode(string serviceName, JavaKindOptions options)
    {
        // Whitespace-only counts as absent rather than as an empty goal/task/path: the underlying
        // integration rejects those anyway, and "name exactly one run mode" is the more useful
        // message than an ArgumentException from inside AddJavaApp.
        var configured = new List<(string Field, JavaRunMode Mode)>();
        if (!string.IsNullOrWhiteSpace(options.MavenGoal))
        {
            configured.Add(("mavenGoal", new JavaRunMode(JavaRunModeKind.MavenGoal, options.MavenGoal!.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(options.GradleTask))
        {
            configured.Add(("gradleTask", new JavaRunMode(JavaRunModeKind.GradleTask, options.GradleTask!.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(options.JarPath))
        {
            configured.Add(("jarPath", new JavaRunMode(JavaRunModeKind.Jar, options.JarPath!.Trim())));
        }

        return configured.Count switch
        {
            1 => configured[0].Mode,
            0 => throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the 'java:' block must say how to run the app — set exactly one of " +
                "'mavenGoal' (e.g. spring-boot:run), 'gradleTask' (e.g. bootRun), or 'jarPath' (e.g. target/app.jar)."),
            _ => throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the 'java:' block sets " +
                string.Join(" and ", configured.Select(c => $"'{c.Field}'")) +
                ", but they are mutually exclusive run modes. Set exactly one of 'mavenGoal', 'gradleTask', or 'jarPath'."),
        };
    }

    private static int ValidatePort(string serviceName, int? port)
    {
        if (port is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the 'java:' block has no 'port' entry. Set it to the port the Java app " +
                "listens on, so consumers referencing this service can reach it.");
        }

        if (port is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.port value '{port}' is not a valid port (must be between 1 and 65535).");
        }

        return port.Value;
    }

    private static string ValidateWorkingDirectory(string serviceName, string? workingDirectory)
    {
        if (workingDirectory is null)
        {
            return ".";
        }

        var trimmed = workingDirectory.Trim();
        if (trimmed.Length == 0)
        {
            return ".";
        }

        if (IsAbsolutePath(trimmed))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.workingDirectory '{trimmed}' is an absolute path, but it must be " +
                "relative to the root of the service's checkout. Use 'path' in servicesources.local.json to point " +
                "at a checkout somewhere else on disk.");
        }

        if (EscapesRoot(trimmed))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.workingDirectory '{trimmed}' points outside the service's checkout. " +
                "It must stay within the repository.");
        }

        return trimmed;
    }

    /// <summary>
    /// Returns <see langword="null"/> when no wrapper override was given, meaning
    /// <see cref="JavaLocalResourceKind"/> looks for the wrapper in the working directory itself.
    /// </summary>
    private static string? ValidateWrapperPath(string serviceName, string? wrapperPath, JavaRunMode runMode)
    {
        var trimmed = wrapperPath?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (runMode.Kind == JavaRunModeKind.Jar)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the 'java:' block sets 'wrapperPath', but 'jarPath' starts the app with " +
                "'java -jar' and runs no Maven or Gradle wrapper at all. Drop 'wrapperPath', or run the app via " +
                "'mavenGoal' or 'gradleTask' instead.");
        }

        if (IsAbsolutePath(trimmed))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.wrapperPath '{trimmed}' is an absolute path, but it must be relative " +
                "to the root of the service's checkout — it names a wrapper script committed to the repository, not " +
                "a Maven or Gradle installation on the developer's machine.");
        }

        if (EscapesRoot(trimmed))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': java.wrapperPath '{trimmed}' points outside the service's checkout. " +
                "It must stay within the repository.");
        }

        return trimmed;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is absolute on <em>any</em> platform, rather than only on this
    /// one. <see cref="Path.IsPathRooted"/> alone is platform-dependent, so a Windows-style value
    /// ('C:\repos\api', '\\server\share') sails past it on Linux/macOS and is then reported as a
    /// directory missing from the checkout instead of as the absolute path it is.
    /// </summary>
    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path)
        || path[0] is '/' or '\\'
        || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));

    /// <summary>
    /// Whether <paramref name="relativePath"/> climbs above the directory it is relative to. Checked
    /// lexically rather than against the resolved path, because
    /// <see cref="JavaLocalResourceKind.Validate"/> isn't handed the checkout directory — a purely
    /// lexical check is what lets an escaping value be rejected there, before the service has put
    /// anything in the app model, rather than from <c>Resolve</c>. Both separators count regardless
    /// of platform, so a Windows-style relative value ('..\sibling') is still rejected on
    /// Linux/macOS; a rooted one ('C:\repos') never reaches here, being caught by
    /// <see cref="IsAbsolutePath"/> first.
    /// </summary>
    private static bool EscapesRoot(string relativePath)
    {
        var depth = 0;
        foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (--depth < 0)
                {
                    return true;
                }
            }
            else
            {
                depth++;
            }
        }

        return false;
    }
}

internal enum JavaRunModeKind
{
    MavenGoal,
    GradleTask,
    Jar,
}

/// <summary>
/// The single run mode a service's <c>java:</c> block selected, and its value — the Maven goal, the
/// Gradle task, or the jar path.
/// </summary>
internal sealed record JavaRunMode(JavaRunModeKind Kind, string Value);

/// <summary>
/// A <see cref="JavaKindOptions"/> block that has passed <see cref="JavaKindOptions.Parse"/>: every
/// optional field is resolved to the value <see cref="JavaLocalResourceKind"/> will actually use, so
/// nothing downstream has to re-apply a default or re-check a constraint.
/// </summary>
internal sealed record ValidatedJavaKindOptions(
    string WorkingDirectory,
    JavaRunMode RunMode,
    string[] Args,
    int Port,
    string? WrapperPath);
