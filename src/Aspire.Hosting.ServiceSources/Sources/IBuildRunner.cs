namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>Builds one project on behalf of <see cref="PathBuildGate"/>; the seam tests replace.</summary>
internal interface IBuildRunner
{
    /// <returns>The build's exit code.</returns>
    Task<int> RunAsync(string projectFile, string? configuration, Action<string> onLine, CancellationToken cancellationToken);
}
