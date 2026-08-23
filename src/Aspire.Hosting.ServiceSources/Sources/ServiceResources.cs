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
/// DCP never materializes it and never creates a Service for it. The consequence is issue #58: a
/// <b>container</b> consumer referencing one of these fails inside DCP. <see cref="UrlSource"/>
/// installs a pre-flight check that reports that case as a ServiceSources error instead.
/// Host-process consumers (projects, executables) work, and are the tested path.
/// <para>
/// The type exists at all because Aspire's own <c>ExternalServiceResource</c> cannot be reused: it
/// is <c>sealed</c> and carries no <see cref="EndpointAnnotation"/>, so it cannot satisfy
/// <see cref="IResourceWithServiceDiscovery"/> (microsoft/aspire#9965, #15961, #15993; tracked here
/// as #5). That decides which type we declare, though, and is not the reason #58 stays open.
/// </para>
/// <para>
/// Registering this resource is the fix that #58 asks for, and it does not work — measured, not
/// assumed. DCP creates a Service for every resource in the model carrying an
/// <see cref="EndpointAnnotation"/> whatever its type, so registration does clear the
/// <c>"should have an associated DCP Service resource"</c> failure. But this endpoint names a remote
/// host DCP cannot bind a local port for, so it then fails to allocate the container-network port
/// (<c>"Unable to allocate a network port for service '&lt;name&gt;-1'"</c>) and the consuming
/// container is never created at all. That trades a clear error for a silent hang, so the resource
/// stays unregistered and the pre-flight explains the limitation instead.
/// </para>
/// </remarks>
internal sealed class ServiceUrlResource(string name) : Resource(name), IResourceWithServiceDiscovery;
