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
        var isDotnetKind = string.Equals(metadata.Kind, LocalKinds.Dotnet, StringComparison.Ordinal);

        // Settle everything configuration alone can settle before paying for a checkout. Looking
        // the kind up is a dictionary probe against registry state; the handler's own Validate
        // only reads the kind config. Neither needs a working tree, and running them after the
        // clone would make a typo'd kind — or a satellite package nobody registered — cost a cold
        // clone of this repository before saying so.
        //
        // Only the first "local" AddService gets that for free across the board: the prefetch below
        // starts cloning every "local" service at once, so once any of them has been resolved the
        // other clones are already in flight and this check no longer runs ahead of them.
        var handler = isDotnetKind ? null : ResolveKindHandler(builder, serviceName, metadata);
        handler?.Validate(serviceName, metadata.KindConfig);

        // Blocks on this service's checkout, but every "local" service's checkout was started
        // together on the first AddService call, so the wait is for the slowest one overall rather
        // than for this one in turn. See LocalCheckoutPrefetch.
        var repoRoot = LocalCheckoutPrefetch.For(builder, gitClient)
            .GetRepoRoot(serviceName, metadata, config, builder.AppHostDirectory, gitClient);

        if (isDotnetKind)
        {
            var projectPath = ResolveProjectFile(serviceName, repoRoot, metadata.Project);

            // Aspire's own AddProject, with a path that exists — so the project picks up every
            // default it normally would (launch-profile endpoints, OTLP exporter, certificate
            // trust, debugging support). Those come from an internal WithProjectDefaults that
            // can't be reproduced from outside the assembly, which is why resolution waits for a
            // real path rather than registering the resource early and filling the path in later.
            return ResolvedService.Tag(builder.AddProject(serviceName, projectPath), serviceName, "local");
        }

        return InvokeKindHandler(builder, serviceName, metadata, repoRoot, handler!);
    }

    /// <summary>
    /// Looks up the handler for a non-dotnet kind, or throws naming the kind. Deliberately free of
    /// filesystem and network work so it can run as a pre-flight, before the checkout.
    /// </summary>
    private static ILocalResourceKind ResolveKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata)
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

        return handler;
    }

    private static IResourceBuilder<IResourceWithServiceDiscovery> InvokeKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, string repoRoot,
        ILocalResourceKind handler)
    {
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
