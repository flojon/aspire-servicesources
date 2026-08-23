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
/// DCP never materializes it and never creates a Service for it. The consequence was reported as
/// issue #58 and stays open as #72: a <b>container</b> consumer referencing one of these fails
/// inside DCP. <see cref="UrlSource"/>
/// installs a pre-flight check that reports that case as a ServiceSources error instead.
/// Host-process consumers (projects, executables) work, and are the tested path.
/// <para>
/// Two routes could fix #58 here, and both are blocked — which is why there is a pre-flight rather
/// than a fix.
/// </para>
/// <para>
/// <b>Registering this resource</b> (#58's option 1) does not work; measured, not assumed. DCP
/// creates a Service for every resource in the model carrying an <see cref="EndpointAnnotation"/>
/// whatever its type, so registration does clear the <c>"should have an associated DCP Service
/// resource"</c> failure, and nothing tries to launch it. But this endpoint names a remote host DCP
/// cannot bind a local port for, so it then fails to allocate the container-network port
/// (<c>"Unable to allocate a network port for service '&lt;name&gt;-1'"</c>) and the consuming
/// container is never created at all — a silent hang in place of a clear error.
/// </para>
/// <para>
/// <b>Delegating to Aspire's <c>ExternalServiceResource</c></b> (#58's option 2) is the route that
/// would work. It carries no <see cref="EndpointAnnotation"/> at all, so DCP never plumbs
/// container-to-host networking for it and injects the URL directly instead
/// (<c>services__&lt;name&gt;__https__0=https://…</c>) — verified against plain Aspire on 13.4.6,
/// where a container consumer of an <c>AddExternalService</c> comes up clean. It is blocked because
/// that same missing <see cref="EndpointAnnotation"/> means it cannot satisfy
/// <see cref="IResourceWithServiceDiscovery"/> (microsoft/aspire#9965, #15961, #15993; tracked here
/// as #72), and because it is <c>sealed</c>, so this package cannot add the interface itself. That
/// is the real dependency on the upstream issue, and why <c>sealed</c> matters.
/// </para>
/// </remarks>
internal sealed class ServiceUrlResource(string name) : Resource(name), IResourceWithServiceDiscovery;
