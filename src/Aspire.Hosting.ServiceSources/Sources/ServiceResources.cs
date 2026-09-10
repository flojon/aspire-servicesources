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
