using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IReadOnlySet<string> RelevantFields { get; } = new HashSet<string> { "path", "ref" };

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var facade = ServiceResource.CreateEmptyFacade(builder, serviceName);

        PendingLocalResolutions.For(builder).Add(new PendingResolution(serviceName, metadata, config, facade, gitClient));

        return facade;
    }

    /// <summary>
    /// Resolves and validates the project file path for a "dotnet"-kind service whose repo root
    /// has already been resolved. Called by <see cref="PendingLocalResolutions"/>'s resolution
    /// path.
    /// </summary>
    internal static string ResolveProjectFile(string serviceName, string repoRoot, string project)
    {
        var projectPath = Path.Combine(repoRoot, project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }
}
