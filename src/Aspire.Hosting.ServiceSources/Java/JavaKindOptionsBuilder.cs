namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// A fluent handle over a service's <c>java:</c> options — the typed alternative to passing a
/// <c>Dictionary&lt;string, object&gt;</c> to <c>WithKind("java", …)</c>. Writes directly into an
/// internal <see cref="JavaKindOptions"/> instance; kept as a handle rather than making
/// <see cref="JavaKindOptions"/> itself public, so its yaml-bound shape can keep changing without
/// that being a breaking change here. See design "Kind options".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class JavaKindOptionsBuilder
{
    private readonly JavaKindOptions _options = new();

    internal JavaKindOptionsBuilder()
    {
    }

    /// <summary>Where in the checkout the Java project lives, relative to the repository root.</summary>
    public JavaKindOptionsBuilder WorkingDirectory(string workingDirectory)
    {
        _options.WorkingDirectory = workingDirectory;
        return this;
    }

    /// <summary>The Maven goal to run the app with, e.g. <c>spring-boot:run</c>.</summary>
    public JavaKindOptionsBuilder MavenGoal(string goal)
    {
        _options.MavenGoal = goal;
        return this;
    }

    /// <summary>The Gradle task to run the app with, e.g. <c>bootRun</c>.</summary>
    public JavaKindOptionsBuilder GradleTask(string task)
    {
        _options.GradleTask = task;
        return this;
    }

    /// <summary>A pre-built jar to run with <c>java -jar</c>, relative to <see cref="WorkingDirectory"/>.</summary>
    public JavaKindOptionsBuilder JarPath(string jarPath)
    {
        _options.JarPath = jarPath;
        return this;
    }

    /// <summary>Where the <c>mvnw</c>/<c>gradlew</c> wrapper script lives, relative to the repository root.</summary>
    public JavaKindOptionsBuilder WrapperPath(string wrapperPath)
    {
        _options.WrapperPath = wrapperPath;
        return this;
    }

    /// <summary>Extra arguments for whichever run mode is configured.</summary>
    public JavaKindOptionsBuilder Args(string[] args)
    {
        _options.Args = args;
        return this;
    }

    /// <summary>The port the Java app listens on.</summary>
    public JavaKindOptionsBuilder Port(int port)
    {
        _options.Port = port;
        return this;
    }

    /// <summary>
    /// The <see cref="JavaKindOptions"/> this handle has been writing into. Validated the same way
    /// a yaml <c>java:</c> block is — by <see cref="JavaKindOptions.Parse"/>, at
    /// <c>Resolve</c>/<c>Validate</c>/<c>ResolveDeferred</c> time — not here.
    /// </summary>
    internal JavaKindOptions Build() => _options;
}
