using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Sources;

// C#'s extension-method lookup walks namespace declarations from innermost to outermost and stops
// at the first level containing any applicable candidate, even if a more specific one exists further
// out. `Aspire.Hosting.ServiceSources.Tests` is a namespace-descendant of both
// `Aspire.Hosting.ServiceSources` (this repo's shadow overloads) and `Aspire.Hosting` (Aspire's real
// ones, one level further out), so a call written directly in that test namespace never lets the real
// method compete once any shadow sibling already applies — the search stops one level too early. This
// namespace has no ancestor relationship to either, so both extension classes are genuine, equal
// candidates here, and ordinary overload-resolution betterness rules decide which one wins.
namespace ServiceSourcesOverloadProbe;

public static class OverloadProbe
{
    public static IResourceBuilder<ServiceResource> CallWithEndpointCompatShimWithProtocol(
        IResourceBuilder<ServiceResource> service, int? port, int? targetPort, string? scheme,
        string? name, string? env, bool isProxied, bool? isExternal, ProtocolType protocol) =>
        service.WithEndpoint(port: port, targetPort: targetPort, scheme: scheme, name: name, env: env,
            isProxied: isProxied, isExternal: isExternal, protocol: protocol);

    public static IResourceBuilder<ServiceResource> CallWithEndpointCompatShimNullableIsProxiedNoProtocol(
        IResourceBuilder<ServiceResource> service, int? port, int? targetPort, string? scheme,
        string? name, string? env, bool? isProxied, bool? isExternal) =>
        service.WithEndpoint(port: port, targetPort: targetPort, scheme: scheme, name: name, env: env,
            isProxied: isProxied, isExternal: isExternal);

    public static IResourceBuilder<ServiceResource> CallWithEndpointCompatShimBoolIsProxiedNoProtocol(
        IResourceBuilder<ServiceResource> service, int? port, int? targetPort, string? scheme,
        string? name, string? env, bool isProxied, bool? isExternal) =>
        service.WithEndpoint(port: port, targetPort: targetPort, scheme: scheme, name: name, env: env,
            isProxied: isProxied, isExternal: isExternal);

    public static IResourceBuilder<ServiceResource> CallWithHttpEndpointCompatShim(
        IResourceBuilder<ServiceResource> service, int? port, int? targetPort, string? name,
        string? env, bool isProxied) =>
        service.WithHttpEndpoint(port: port, targetPort: targetPort, name: name, env: env, isProxied: isProxied);

    public static IResourceBuilder<ServiceResource> CallWithHttpsEndpointCompatShim(
        IResourceBuilder<ServiceResource> service, int? port, int? targetPort, string? name,
        string? env, bool isProxied) =>
        service.WithHttpsEndpoint(port: port, targetPort: targetPort, name: name, env: env, isProxied: isProxied);

    public static IResourceBuilder<ServiceResource> CallWithEndpointPrimary(
        IResourceBuilder<ServiceResource> service, int? port, string? scheme) =>
        service.WithEndpoint(port: port, scheme: scheme);

    public static IResourceBuilder<ServiceResource> CallWithEndpointCallback(
        IResourceBuilder<ServiceResource> service, string endpointName, Action<EndpointAnnotation> callback) =>
        service.WithEndpoint(endpointName, callback);

    // No sibling shadow to disambiguate, so no [OverloadResolutionPriority] on the target: this
    // compiling and binding the non-generic shadow is the whole assertion (#359).
    public static IResourceBuilder<ServiceResource> CallWithExternalHttpEndpoints(
        IResourceBuilder<ServiceResource> service) =>
        service.WithExternalHttpEndpoints();

    public static IResourceBuilder<ServiceResource> CallWithCommand(
        IResourceBuilder<ServiceResource> service, string name, string displayName,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand,
        CommandOptions? commandOptions = null) =>
        service.WithCommand(name, displayName, executeCommand, commandOptions);

    // The three-argument form an AppHost actually writes. Compiling at all is the assertion: both
    // shadows are applicable here, so dropping [OverloadResolutionPriority(1)] from the
    // CommandOptions one makes this CS0121 -- which is how Aspire's own pair disambiguates, and
    // which every other probe here hides by passing a disambiguating argument.
    public static IResourceBuilder<ServiceResource> CallWithCommandNoOptions(
        IResourceBuilder<ServiceResource> service, string name, string displayName,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand) =>
        service.WithCommand(name, displayName, executeCommand);

    // The winning candidate is this package's own [Obsolete] shadow, which carries Aspire's
    // deprecation message verbatim -- so CS0618 here is that deprecation, not a defect in the probe.
#pragma warning disable CS0618
    public static IResourceBuilder<ServiceResource> CallWithCommandLegacy(
        IResourceBuilder<ServiceResource> service, string name, string displayName,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand,
        string? displayDescription = null) =>
        service.WithCommand(name, displayName, executeCommand, updateState: null,
            displayDescription: displayDescription);
#pragma warning restore CS0618
}
