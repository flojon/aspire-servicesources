using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// A container-sourced service. Subclasses <see cref="ContainerResource"/> — which Aspire's own
/// integrations do routinely, so DCP still treats it as a container and creates the Service that a
/// container consumer's <c>WithReference</c> needs — and adds
/// <see cref="IResourceWithServiceDiscovery"/>, which <see cref="ContainerResource"/> itself lacks.
/// That gap is the only reason <c>AddService</c> ever needed a facade.
/// </summary>
internal sealed class ServiceContainerResource(string name) : ContainerResource(name), IResourceWithServiceDiscovery;

/// <summary>
/// The <c>kubectl port-forward</c> process standing in for a kubernetes-sourced service. Adds
/// <see cref="IResourceWithServiceDiscovery"/> to <see cref="ExecutableResource"/> for the same
/// reason as <see cref="ServiceContainerResource"/>.
/// </summary>
internal sealed class ServiceExecutableResource(string name, string command, string workingDirectory)
    : ExecutableResource(name, command, workingDirectory), IResourceWithServiceDiscovery;

/// <summary>
/// A url-sourced service: a fixed, already-running endpoint with nothing for Aspire to launch.
/// </summary>
/// <remarks>
/// Alone among the sources this resource is <b>not</b> registered in <c>builder.Resources</c>, so
/// DCP never materializes it and never creates a Service for it. Aspire's own
/// <c>ExternalServiceResource</c> would be the right home, but it is <c>sealed</c> and carries no
/// <see cref="EndpointAnnotation"/>, so it cannot satisfy <see cref="IResourceWithServiceDiscovery"/>
/// (microsoft/aspire#9965, #15961, #15993; tracked here as #5). The consequence is issue #58: a
/// <b>container</b> consumer referencing one of these fails inside DCP. <see cref="UrlSource"/>
/// installs a pre-flight check that reports that case as a ServiceSources error instead.
/// Host-process consumers (projects, executables) work, and are the tested path.
/// </remarks>
internal sealed class ServiceUrlResource(string name) : Resource(name), IResourceWithServiceDiscovery;
