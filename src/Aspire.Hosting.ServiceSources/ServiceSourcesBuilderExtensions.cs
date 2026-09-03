using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.PortAllocation;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

public static class ServiceSourcesBuilderExtensions
{
    /// <summary>
    /// The <c>source</c> value a service's developer config names, mapped to the implementation that
    /// resolves it.
    /// </summary>
    /// <remarks>
    /// Matched with <see cref="StringComparer.OrdinalIgnoreCase"/> because everything else in an
    /// entry is. The service name, the block names and the field names all arrive through
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>, which compares keys
    /// case-insensitively; the source arrives the same way, and most often as a value someone typed
    /// into an environment variable by hand. Matching it ordinally answered
    /// <c>ServiceSources__Services__orders__Source=Local</c> with "not implemented yet", naming a
    /// missing feature instead of the capital L.
    ///
    /// That is the opposite of the deliberate case-sensitivity of <c>kind</c> names (see
    /// <see cref="Sources.LocalKindRegistry.DescribeNearMatch"/>), and for a reason: kinds are an
    /// open registry that anything may contribute names to, where folding case could collide
    /// two packages' registrations, while these four names are a closed set this package owns and
    /// nothing else can add to.
    /// </remarks>
    private static readonly Dictionary<string, IServiceSource> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = new LocalProjectSource(new GitCliClient()),
        ["kubernetes"] = new KubernetesSource(new SocketPortAllocator()),
        ["url"] = new UrlSource(),
        ["container"] = new ContainerSource(),
    };

    /// <summary>
    /// Resolves service <paramref name="name"/> to its real resource and adds it to
    /// <paramref name="builder"/>, according to the service's configured source: a local
    /// project — either a developer-managed checkout (<c>path</c> in
    /// <c>servicesources.local.json</c>) or a package-managed git clone under
    /// <c>.servicesources/checkouts/&lt;serviceName&gt;</c> beneath the AppHost directory —
    /// added via Aspire's own <c>AddProject(name, path)</c> without ever
    /// touching this AppHost's own <c>.csproj</c>/<c>.sln</c> (the <c>"local"</c> source); or a
    /// <c>kubectl port-forward</c> process against an already-running service in a Kubernetes
    /// dev cluster, added via Aspire's own <c>AddExecutable(...)</c> (the <c>"kubernetes"</c>
    /// source); or a fixed, already-known URL — e.g. a Kubernetes ingress or any other reachable
    /// HTTP endpoint — with no underlying resource for Aspire to run (the <c>"url"</c> source);
    /// or a published container image run locally via Aspire's own <c>AddContainer(...)</c>,
    /// with image pull and lifecycle managed entirely by Aspire's own container-runtime
    /// integration (the <c>"container"</c> source).
    /// </summary>
    /// <returns>
    /// An <see cref="IResourceBuilder{T}"/> over the <b>real</b> resource Aspire runs. Pass it to a
    /// consumer's <c>WithReference(...)</c>, name its endpoint with
    /// <see cref="ServiceEndpointExtensions.GetServiceEndpoint"/> (or <c>GetEndpoint(...)</c>, which
    /// ties the consumer to one source's endpoint naming), or apply this AppHost's own configuration
    /// with <see cref="ServiceConfigurationExtensions.Configure{T}"/> and
    /// <see cref="ServiceConfigurationExtensions.As{T}"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The resource is registered in Aspire's model, so configuration applied through the returned
    /// builder reaches the process that actually runs, and a container consumer's
    /// <c>WithReference(...)</c> resolves. Which configuration applies depends on the resolved
    /// source: the <c>"url"</c> and <c>"kubernetes"</c> sources run out of band — one is a fixed
    /// remote URL, the other a <c>kubectl port-forward</c> in front of something already running —
    /// so <see cref="ServiceConfigurationExtensions.Configure{T}"/> skips with a warning rather than
    /// applying it. Wait ordering survives for <c>"kubernetes"</c>, whose port-forward is a real
    /// local process to order against; <c>"url"</c> registers no resource at all, so nothing
    /// applies to it.
    /// </para>
    /// <para>
    /// The bare <c>IResourceBuilder&lt;IResourceWithServiceDiscovery&gt;</c> return type is load
    /// bearing — Aspire's TypeScript code generator emits nothing for an exported method returning a
    /// custom interface, so narrowing it would drop <c>addService</c> from the generated SDK
    /// entirely and break the TypeScript AppHost.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        // Before the resolution below, and before anything that can fail: this is the layer the
        // AppHost's own IConfiguration reads back, so it has to be present from this line on rather
        // than from whichever line first resolved a service successfully.
        DeveloperConfigFileSource.EnsureRegistered(builder);

        var (metadata, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, name);

        if (!Sources.TryGetValue(developerConfig.Source, out var source))
        {
            // Names the alternatives rather than saying "not implemented yet": the lookup folds
            // case, so reaching here means the name itself is unknown — not that the source exists
            // under a different spelling, which is what the old wording sent readers looking for.
            // An entry with no source at all never arrives here; ResolveService reports that
            // separately, against the key that would set it.
            var known = string.Join(", ", Sources.Keys.Order(StringComparer.Ordinal).Select(s => $"'{s}'"));

            // The key, not just the file: the file is only the lowest layer this value can arrive
            // from, so a developer whose environment carries a stale source would otherwise be sent
            // to edit the one place it is not. Same reasoning as ServiceDeveloperConfigValidator.
            var key = $"{DeveloperConfiguration.ServicesKey}:{name}:source";

            throw new ServiceSourcesConfigurationException(
                $"Service '{name}' has unknown source '{developerConfig.Source}'. Valid sources are {known}. "
                + $"Correct '{key}' in '{DeveloperConfiguration.FileName}', or wherever a higher layer set "
                + $"it — appsettings, user secrets, the environment variable "
                + $"{key.Replace(":", "__", StringComparison.Ordinal)}, or the command line.");
        }

        return source.Resolve(builder, name, metadata, developerConfig);
    }

    /// <summary>
    /// Opts this AppHost into deferring a <c>"local"</c> service's <em>first</em> checkout past
    /// startup: a service whose package-managed clone does not exist yet is registered stopped,
    /// cloned while the AppHost runs, and started when its checkout lands — so the dashboard comes
    /// up immediately, checkout progress and failure show as resource state, and one failed clone
    /// costs one service rather than the whole AppHost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be called before the first <see cref="AddService"/>, which is where the decision is
    /// made. A service whose checkout already exists — every service on every run after the first —
    /// resolves eagerly, with full launch-profile fidelity, exactly as it does without this call.
    /// Services with a <c>path</c> override are never deferred either; that directory is the
    /// developer's own and there is nothing to clone. Neither is anything outside run mode:
    /// <c>aspire publish</c> and manifest generation clone first as they always have, because a
    /// manifest written from a repository that is not on disk would describe a project without its
    /// endpoints or its profile environment.
    /// </para>
    /// <para>
    /// The clones stay parallel and get narrower. A deferred registration blocks on nothing, so its
    /// clone starts at its own <see cref="AddService"/> call and still overlaps the ones around it —
    /// but it no longer has to be started ahead of demand to do that, which is what let the
    /// speculative prefetch stop cloning services this AppHost never adds (#76). Without this call
    /// the clones must start before the AppHost has said what it wants, so every <c>"local"</c>
    /// entry with no checkout yet is cloned.
    /// </para>
    /// <para>
    /// Applies to the <c>"local"</c> kinds that own a managed checkout — <c>dotnet</c>, <c>java</c>
    /// and <c>javascript</c>. Those two kinds pay none of the cost below: neither has a launch
    /// profile, and both take their endpoints from the committed catalog, so a deferred one is
    /// identical to a warm one and only their post-clone checks move. <c>url</c>, <c>kubernetes</c>
    /// and <c>container</c> clone nothing, so there is nothing to defer.
    /// </para>
    /// <para>
    /// A deferred <c>dotnet</c> service's launch profile environment is put back once the clone
    /// lands, and only where the AppHost has not already set the same key — expanded, and alongside
    /// <c>DOTNET_LAUNCH_PROFILE</c>, exactly as a warm run applies it.
    /// </para>
    /// <para>
    /// A deferred <c>dotnet</c> service should declare its own endpoints in the AppHost, because a
    /// project's endpoints come from its launch profile and Aspire reads that while composing —
    /// before the repository is on disk:
    /// </para>
    /// <code lang="csharp">
    /// builder.UseDeferredCheckout();
    ///
    /// var orders = builder.AddService("orders").WithHttpEndpoint();
    /// </code>
    /// <para>
    /// That line is correct on a warm checkout too — <c>WithHttpEndpoint</c> updates an endpoint of
    /// the same name using its non-null arguments only, and it has none — so there is one call, not
    /// one per path. A service that declares none still runs: once the checkout has landed its real
    /// launch profile is read, and only a profile that declares an <c>applicationUrl</c> the AppHost
    /// did not mirror produces a warning naming the service and the URL. A service with no
    /// <c>applicationUrl</c> on either path — a run-to-completion worker — costs nothing and is
    /// never reported. See <c>DeferredCheckout.LaunchProfileEndpointWarning</c>.
    /// </para>
    /// <para>
    /// Off by default: a service that used to be running by the time <c>Build()</c> returned is
    /// started after it instead, which is visible to anything in the AppHost that assumed otherwise.
    /// </para>
    /// </remarks>
    [AspireExportIgnore]
    public static IDistributedApplicationBuilder UseDeferredCheckout(this IDistributedApplicationBuilder builder)
    {
        // The call an AppHost using deferred checkouts makes first of all, and the one whose own
        // guidance — declare a deferred service's endpoints yourself — is most likely to be followed
        // by a line that reads our configuration back.
        DeveloperConfigFileSource.EnsureRegistered(builder);

        DeferredCheckout.For(builder).Enable();
        return builder;
    }

    /// <summary>
    /// Registers <paramref name="handler"/> as the resolver for local-sourced services whose
    /// <c>servicesources.yaml</c> entry declares <c>kind: &lt;paramref name="kind"/&gt;</c>.
    /// Called by a kind's own registration method (e.g. a hypothetical
    /// <c>UseJavaScript()</c>), not typically called directly by an AppHost author.
    /// </summary>
    [AspireExportIgnore]
    public static IDistributedApplicationBuilder AddLocalKind(
        this IDistributedApplicationBuilder builder, string kind, ILocalResourceKind handler)
    {
        // Ahead of the reflection below, which would otherwise turn a null handler into a bare
        // NullReferenceException naming neither the kind nor the call it came out of.
        ArgumentNullException.ThrowIfNull(handler);

        RequireCurrentValidateSignature(kind, handler);

        // UseJavaScript()/UseJava() land here, and an AppHost calls one of those
        // before its first AddService() — so this is usually the call that completes the AppHost's
        // configuration chain, ahead of any line of theirs that reads it.
        DeveloperConfigFileSource.EnsureRegistered(builder);

        LocalKindRegistry.For(builder).Register(kind, handler);
        return builder;
    }

    /// <summary>
    /// Refuses a handler whose <c>Validate</c> does not match
    /// <see cref="ILocalResourceKind.Validate"/>, which nothing else would catch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ILocalResourceKind.Validate"/> is a defaulted interface member, so a
    /// <c>Validate</c> of any other shape compiles clean against the current interface — the kind
    /// simply stops implementing anything, and core calls the do-nothing default in its place. Every
    /// rejection that method made would silently stop running, and the typo'd options block it used
    /// to name would reach <see cref="ILocalResourceKind.Resolve"/> and surface as a handler that
    /// failed while creating its resource. There is no compiler diagnostic for that, so registration
    /// is the only seam left to put one at.
    /// </para>
    /// <para>
    /// Any <c>Validate</c> at all counts as the attempt, not just the pre-<c>repoRoot</c>
    /// <c>Validate(string, object?)</c>: adding the new parameter in the wrong position fails in
    /// exactly the same silent way as never adding it, and matching only the old shape would let the
    /// half-migrated case through. The trade is that an unrelated inherited method named
    /// <c>Validate</c> is refused too, which the message answers by naming the method it found and
    /// the signature it wanted.
    /// </para>
    /// <para>
    /// Both conditions are required, so a kind that has migrated and, for its own reasons, keeps a
    /// method of the old shape — a helper, an overload for its tests — is doing nothing wrong and is
    /// not refused. Whether the interface member is really implemented is read from the interface
    /// map rather than by name, so an explicit implementation counts and the inherited default does
    /// not.
    /// </para>
    /// </remarks>
    private static void RequireCurrentValidateSignature(string kind, ILocalResourceKind handler)
    {
        var type = handler.GetType();

        if (ImplementsCurrentValidate(type))
        {
            return;
        }

        var declared = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(ILocalResourceKind.Validate));

        if (declared is null)
        {
            return;
        }

        throw new ServiceSourcesConfigurationException(
            $"Kind '{kind}' is registered by '{type.FullName}', which declares '{Describe(declared)}' but does " +
            $"not implement '{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Validate)}" +
            "(string serviceName, string repoRoot, object? rawConfig)'. Validate gained a 'repoRoot' " +
            "parameter — the service's resolved checkout directory, so a kind can check a path its options " +
            "block names against the repository that path is relative to. Nothing failed to compile because " +
            "Validate is a defaulted member: a method of any other shape is never called, and everything it " +
            "rejected would now be accepted. Match the signature exactly — or, if that method was never " +
            "meant to be the interface's, implement Validate(string, string, object?) alongside it.");
    }

    /// <summary>
    /// Whether <paramref name="type"/> itself supplies
    /// <see cref="ILocalResourceKind.Validate"/> rather than inheriting the interface's default. The
    /// interface map answers that for an explicit implementation too, which a search by name would
    /// miss: an explicitly implemented member is named for the interface it came from.
    /// </summary>
    /// <remarks>
    /// Written to answer "no" for anything it cannot read positively. A target the runtime left
    /// unfilled is the shape a non-overridden default member has, which is the case this whole check
    /// exists to catch — reading it as "implemented" would skip the refusal in precisely the
    /// situation that needs it.
    /// </remarks>
    private static bool ImplementsCurrentValidate(Type type)
    {
        var map = type.GetInterfaceMap(typeof(ILocalResourceKind));

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name != nameof(ILocalResourceKind.Validate))
            {
                continue;
            }

            return map.TargetMethods[i] is { } target && target.DeclaringType != typeof(ILocalResourceKind);
        }

        return false;
    }

    /// <summary>
    /// A method rendered the way its author wrote it, for a message that has to be recognisable as
    /// the method in front of them — the point of naming what was found rather than what was
    /// expected.
    /// </summary>
    private static string Describe(MethodInfo method) =>
        $"{method.Name}(" +
        string.Join(", ", method.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}")) +
        ")";

    /// <summary>
    /// The C# keyword for the handful of types these signatures are built from, and the plain type
    /// name for anything else — <see cref="Type.Name"/> alone would render 'string' as 'String'.
    /// </summary>
    private static string TypeName(Type type) => type switch
    {
        _ when type == typeof(string) => "string",
        _ when type == typeof(object) => "object",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(int) => "int",
        _ => type.Name,
    };
}
