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
/// <para>
/// <b><see cref="IResourceWithoutLifetime"/></b> is what stops that non-registration from hanging a
/// consumer, and is issue #170. Aspire honours a <see cref="WaitAnnotation"/> by watching the
/// waited-on resource's state until it reports <c>Running</c>; nothing ever publishes a state for a
/// resource DCP does not know about, so <c>WaitFor(service)</c> on one of these waited for the life
/// of the run — no error, no timeout, and the service absent from the resource list that would have
/// explained the stall. Aspire's own escape hatch is this marker: <c>WaitForDependenciesAsync</c>
/// filters <c>waitAnnotation.Resource is not IResourceWithoutLifetime</c> before it waits on
/// anything, so declaring it drops the wait rather than satisfying it. That is the honest answer
/// here — a fixed, pre-known URL is already up as far as this AppHost is concerned, and there is no
/// lifetime for the wait to be ordered against. It covers all three <see cref="WaitType"/>s and the
/// <c>WaitForStart</c> that <c>AddConnectionString</c> adds on the AppHost's behalf, which is
/// filtered on the same interface.
/// </para>
/// <para>
/// Publishing a <c>Running</c> state instead would also resolve <c>WaitFor</c> — Aspire's health
/// service watches the notification stream rather than the model, so it would stamp the ready event
/// this resource never gets. It is not what is done, for three reasons: <c>WaitForCompletion</c>
/// would still hang forever, because a resource reported as running never completes; the state
/// would put a row in the dashboard for something DCP cannot start, stop or restart; and it would
/// claim the URL is reachable, which nothing here has checked. Dropping the wait claims only what
/// is true.
/// </para>
/// </remarks>
internal sealed class ServiceUrlResource(string name)
    : Resource(name), IResourceWithServiceDiscovery, IResourceWithoutLifetime;
