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
        // Blocks on this service's checkout, but every "local" service's checkout was started
        // together on the first AddService call, so the wait is for the slowest one overall rather
        // than for this one in turn. See LocalCheckoutPrefetch.
        var repoRoot = LocalCheckoutPrefetch.For(builder, gitClient)
            .GetRepoRoot(serviceName, metadata, config, builder.AppHostDirectory, gitClient);

        if (string.Equals(metadata.Kind, LocalKinds.Dotnet, StringComparison.Ordinal))
        {
            var projectPath = ResolveProjectFile(serviceName, repoRoot, metadata.Project);

            // Aspire's own AddProject, with a path that exists — so the project picks up every
            // default it normally would (launch-profile endpoints, OTLP exporter, certificate
            // trust, debugging support). Those come from an internal WithProjectDefaults that
            // can't be reproduced from outside the assembly, which is why resolution waits for a
            // real path rather than registering the resource early and filling the path in later.
            return ResolvedService.Tag(builder.AddProject(serviceName, projectPath), serviceName, "local");
        }

        return ResolveViaKindHandler(builder, serviceName, metadata, repoRoot);
    }

    private static IResourceBuilder<IResourceWithServiceDiscovery> ResolveViaKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, string repoRoot)
    {
        var registry = LocalKindRegistry.For(builder);

        if (!registry.TryGet(metadata.Kind, out var handler) || handler is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': kind '{metadata.Kind}' is not registered. " +
                registry.DescribeNearMatch(metadata.Kind) +
                "Add the satellite package for this kind and call its registration method " +
                "(e.g. builder.UseJavaScript()) before the first AddService call.");
        }

        handler.Validate(serviceName, metadata.KindConfig);

        IResourceBuilder<IResourceWithServiceDiscovery>? resourceBuilder;
        try
        {
            resourceBuilder = handler.Resolve(builder, serviceName, repoRoot, metadata.KindConfig);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the handler for kind '{metadata.Kind}' failed while creating its " +
                $"resource. If this is a configuration problem, report it from " +
                $"{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Validate)} instead, which runs first.", ex);
        }

        if (resourceBuilder is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the handler for kind '{metadata.Kind}' returned no resource. " +
                $"{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Resolve)} must return the resource it created.");
        }

        return ResolvedService.Tag(resourceBuilder, serviceName, "local");
    }

    /// <summary>
    /// Resolves and validates the project file path for a "dotnet"-kind service whose repo root has
    /// already been resolved.
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
