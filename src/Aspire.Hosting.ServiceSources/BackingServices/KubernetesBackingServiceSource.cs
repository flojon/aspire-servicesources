using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Kubernetes;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// Connects to a backing service running in a Kubernetes cluster, through a
/// <c>kubectl port-forward</c> this AppHost opens and Aspire manages.
/// </summary>
/// <remarks>
/// Three resources' worth of behaviour behind one handle: the tunnel process, the connection string
/// that addresses its local end, and a health check that reports whether the tunnel is listening.
/// The AppHost sees only the connection string — the same handle every other source returns — so
/// switching a backing service to this source changes no AppHost code.
/// <para>
/// <b>The health check is required, not a nicety.</b> Without it a consumer's <c>WaitFor</c> on this
/// source is decorative: the connection-string resource reaches <c>Running</c> as soon as its
/// template resolves, which is immediately and knows nothing about the tunnel. See
/// <see cref="LocalPortHealthCheck"/>, which carries the measurement.
/// </para>
/// <para>
/// The local port is allocated rather than configured, so two backing services forwarded at once
/// cannot collide — and that is why a connection string here writes <c>${port}</c> rather than a
/// number. Substituted eagerly, as a literal, because the port is known synchronously: what a
/// consumer receives is an ordinary connection string with no deferred parts in it.
/// </para>
/// </remarks>
internal sealed partial class KubernetesBackingServiceSource(
    IPortAllocator portAllocator, IKubernetesSecretReader secretReader) : IBackingServiceSource
{
    public IResourceBuilder<IResourceWithConnectionString> Resolve(
        IDistributedApplicationBuilder builder,
        string name,
        BackingServiceDeveloperConfig config)
    {
        var kubernetes = config.Kubernetes;

        RequireEveryField(name, kubernetes);

        var service = kubernetes.Service!;
        var context = kubernetes.Context!;
        var connectionString = kubernetes.ConnectionString!;
        var @namespace = kubernetes.Namespace ?? KubectlPortForward.DefaultNamespace;

        // Judged whole before a port is taken, for the reason the service-side source gives: a
        // template this source cannot resolve is config validation like every check above it, and
        // should not burn an allocation on its way to saying so.
        var requested = RequireForwardablePorts(name, kubernetes.Port!);

        var template = ConnectionStringTemplate.Parse(connectionString, name, ConfigKey(name, "ConnectionString"));

        // Decided from the template's shape, which is local configuration and therefore known now,
        // even though the value it stands for is not fetched until start time. See
        // <see cref="IsWholeSecret"/> for why the shape is enough.
        var wholeSecret = IsWholeSecret(template);

        RequireEveryPlaceholderIsResolvable(name, connectionString, template, requested, wholeSecret);

        // THE binding of a name to a local port, made once and read by everything below: the
        // connection string, the kubectl command line, and the health checks. Nothing downstream
        // re-derives an order of its own, and nothing pairs by position.
        //
        // That is not tidiness. With one port there was one number and nothing could be mispaired;
        // with several there are three sequences in play — the block's own order, the ordinal-by-name
        // order the command line is written in, and the order the allocator returned — and pairing
        // any two of them by index gives ${port:amqp} the port kubectl forwarded to the management
        // port. Both health checks pass, every resource reports healthy, and the application talks
        // to the wrong listener. It is the failure NothingAddressesTheTunnel exists to prevent, one
        // level down.
        //
        // Whole-string mode is the one case that cannot choose its own port: the connection string is
        // one opaque secret written against the cluster, so there is no placeholder to substitute a
        // local port into and the only rewrite available is the host — which means the local end has
        // to match the one the secret already names. RequireEveryPlaceholderIsResolvable has already
        // refused this mode against anything but a single forwarded port, so requested here has
        // exactly one entry.
        ForwardedPort[] forwarded;

        if (wholeSecret)
        {
            var remotePort = requested[0].RemotePort;
            var localPort = RequireLocalPortFree(builder, name, remotePort);
            forwarded = [new ForwardedPort(requested[0].Name, remotePort, localPort)];
        }
        else
        {
            var localPorts = portAllocator.AllocatePorts(requested.Count);

            forwarded = requested
                .Select((port, index) => new ForwardedPort(port.Name, port.RemotePort, localPorts[index]))
                .ToArray();
        }

        var byName = forwarded
            .Where(port => port.Name is not null)
            .ToDictionary(port => port.Name!, StringComparer.OrdinalIgnoreCase);

        var expression = new ReferenceExpressionBuilder();

        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case ConnectionStringTemplate.Literal literal:
                    ConnectionStringTemplate.AppendLiteral(expression, literal.Text);
                    break;

                // Eager, and as a literal: the port is known here, so nothing about it has to be
                // deferred to resolution time. What a consumer receives is an ordinary connection
                // string with no late parts in it.
                case ConnectionStringTemplate.Port { Name: null }:
                    ConnectionStringTemplate.AppendLiteral(
                        expression, forwarded[0].LocalPort.ToString(CultureInfo.InvariantCulture));
                    break;

                // Looked up by name, never by position. A name repeated in the template resolves to
                // the same forwarded port both times, because the binding is per forwarded port and
                // not per placeholder.
                case ConnectionStringTemplate.Port port:
                    ConnectionStringTemplate.AppendLiteral(
                        expression, byName[port.Name!].LocalPort.ToString(CultureInfo.InvariantCulture));
                    break;

                // Late, and as a parameter rather than text: the value is in the cluster, and
                // fetching it here would run kubectl while the AppHost is still being composed —
                // the path main deliberately moved off when local project resolution became
                // deferred — and would fail the whole AppHost for a developer who is merely not
                // logged in yet. Aspire resolves a parameter when something asks for its value.
                // forwarded[0] regardless of how many ports this backing service declares: the
                // remote port only matters to a whole-string secret, and RequireEveryPlaceholderIsResolvable
                // already refused a whole-string secret against more than one declared port, so
                // whenever it is read there is exactly one entry to read it from.
                case ConnectionStringTemplate.Secret secret:
                    expression.Append(
                        $"{SecretParameter(builder, name, service, context, @namespace, secret, wholeSecret, forwarded[0].RemotePort).Resource}");
                    break;

                // Unreachable: the walk above accepts only literals, resolvable ports and secrets.
                // Kept so that a placeholder kind added later fails loudly here rather than
                // vanishing from the connection string.
                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        var backingService = builder.AddConnectionString(name, expression.Build());

        // Named after the backing service and marked as its child, because that is what it is: a
        // developer reading the dashboard should see one thing they configured, with the process
        // that serves it underneath, rather than two resources they have to work out the relation
        // between. Aspire keys nothing off this name — unlike the service-side source, where the
        // executable *is* the service and its name is what service discovery publishes.
        var tunnelName = $"{name}-tunnel";

        try
        {
            builder
                .AddExecutable(
                    tunnelName,
                    "kubectl",
                    builder.AppHostDirectory,
                    KubectlPortForward.Args(
                        service,
                        forwarded.Select(port => (port.LocalPort, port.RemotePort)).ToArray(),
                        context,
                        kubernetes.Namespace))
                .WithParentRelationship(backingService);
        }
        catch (ArgumentException ex)
        {
            // The tunnel's name is derived, so Aspire's complaint about it names a resource the
            // AppHost never wrote — and the only rule this can break by deriving is length, since
            // a backing service whose own name Aspire rejected would not have reached this line.
            // Aspire stays the authority on what the rule is: this adds the missing half, which is
            // where the name came from.
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{Named(name)}': its port-forward runs as a resource named '{tunnelName}', after the "
                + $"backing service, and Aspire rejected that name — \"{WithoutParameterSuffix(ex.Message)}\" Aspire's limit is on the "
                + $"derived name rather than on '{name}', so a shorter backing-service name is what fixes it.",
                ex);
        }

        // One per forwarded port, all of them on the connection string, so that a consumer's
        // WaitFor waits for the whole tunnel rather than for whichever port happened to be
        // registered.
        //
        // On the connection string alone, and not also on the tunnel, though the tunnel is what the
        // sockets actually belong to. The connection string is what a consumer waits for, which is
        // the whole reason this source has a health check; the tunnel would gain only a badge in
        // the dashboard. Aspire runs one monitor loop per resource, so a second resource carrying
        // these keys would run every probe twice per cycle — and every probe is a connection kubectl
        // logs ("Handling connection for <port>") and the database behind it may log as an
        // incomplete startup packet. The tunnel's log is where a bad context or an expired
        // credential shows up, and it is worth keeping readable.
        foreach (var port in forwarded)
        {
            var healthCheckKey = HealthCheckKey(name, port.Name);

            builder.Services
                .AddHealthChecks()
                .AddCheck(
                    healthCheckKey,
                    new LocalPortHealthCheck(name, port.LocalPort, port.Name),
                    timeout: ProbeTimeout);

            backingService = backingService.WithHealthCheck(healthCheckKey);
        }

        return backingService;
    }

    /// <summary>One port this source forwards: its name, the cluster's port, and the local one.</summary>
    /// <remarks>
    /// <see cref="Name"/> is <see langword="null"/> for a <c>port</c> written as a number, which is
    /// what <c>${port}</c> resolves against — and the reason the single form is not carried as a
    /// one-entry map: <c>${port}</c> is accepted against a port written as a number and refused
    /// against a block of one named port.
    /// </remarks>
    private sealed record ForwardedPort(string? Name, int RemotePort, int LocalPort);

    /// <summary>
    /// The health check watching one forwarded port. The single-port form keeps the key it has
    /// always had; a named port adds its name.
    /// </summary>
    private static string HealthCheckKey(string name, string? portName) =>
        portName is null ? $"{name}-tunnel-tcp" : $"{name}-tunnel-tcp-{portName}";

    /// <summary>
    /// How long one probe may take before it counts as a failure.
    /// </summary>
    /// <remarks>
    /// Set explicitly because <c>AddCheck</c>'s instance overload leaves the registration's timeout
    /// infinite, and a connect that <em>hangs</em> rather than refuses would then stall that
    /// resource's monitor loop for the life of the run — no result, and so no report to act on.
    /// Loopback makes that nearly impossible, which is the reason to spend one argument on it
    /// rather than a mechanism: the failure it forecloses is cheap to prevent and silent to hit.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Refuses a template this source cannot resolve, and one that never mentions the tunnel.
    /// </summary>
    /// <remarks>
    /// A pass of its own, ahead of the one that builds the expression, so that every reason a
    /// template is refused is reached before a port is allocated and before anything is added to
    /// the model.
    /// <para>
    /// Every problem is collected rather than thrown at the first, which is the habit
    /// <see cref="RequireEveryField"/> already keeps in this file and for its reason: reporting one
    /// per run costs a failed startup per mistake, and a developer who has just written a port block
    /// and a connection string to go with it can easily have got two names wrong at once.
    /// </para>
    /// </remarks>
    private static void RequireEveryPlaceholderIsResolvable(
        string name,
        string connectionString,
        ConnectionStringTemplate template,
        IReadOnlyList<(string? Name, int RemotePort)> requested,
        bool wholeSecret)
    {
        var forwardsOneUnnamedPort = requested is [{ Name: null }];
        var names = requested.Where(port => port.Name is not null).Select(port => port.Name!).ToArray();

        var problems = new List<string>();
        var ports = 0;

        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case ConnectionStringTemplate.Literal:
                    break;

                case ConnectionStringTemplate.Port { Name: null } unnamed:
                    ports++;

                    if (!forwardsOneUnnamedPort)
                    {
                        problems.Add(UnnamedPortAgainstABlock(unnamed, names));
                    }

                    break;

                case ConnectionStringTemplate.Port port:
                    ports++;

                    if (forwardsOneUnnamedPort)
                    {
                        problems.Add(NamedPortAgainstASinglePort(name, port));
                    }
                    else if (!names.Contains(port.Name!, StringComparer.OrdinalIgnoreCase))
                    {
                        problems.Add(NoSuchForwardedPort(port, names));
                    }

                    break;

                // The only placeholder here whose value this source does not know: it is fetched
                // from the cluster when Aspire resolves the parameter it becomes, so nothing is
                // checked about it beyond its spelling, which the parser already did.
                case ConnectionStringTemplate.Secret:
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        if (wholeSecret && requested.Count > 1)
        {
            // Whole-string mode addresses the tunnel at the port the secret already names, with
            // nothing in the template to substitute a different one into — which only makes sense
            // once there is a single number to match it against. A block naming several ports leaves
            // no way to tell which one the secret was written for.
            problems.Add(WholeSecretAgainstNamedPorts(requested));
        }
        else if (ports == 0 && !wholeSecret)
        {
            // Collected alongside the rest rather than thrown after them, so a template that carries a
            // secret and no port does not cost two startups to be told both.
            problems.Add(NothingAddressesTheTunnel(connectionString, requested));
        }

        if (problems.Count > 0)
        {
            throw Failure(name, problems);
        }
    }

    /// <summary>
    /// The error for a whole-string secret against a backing service that forwards its ports by name.
    /// </summary>
    /// <remarks>
    /// Whole-string mode forwards the secret's own port with nothing in the template to substitute a
    /// different one into, which only makes sense once there is a single number to match against. A
    /// block naming several ports leaves no way to tell which one the secret was written for.
    /// </remarks>
    private static string WholeSecretAgainstNamedPorts(IReadOnlyList<(string? Name, int RemotePort)> requested)
    {
        var names = requested.Where(port => port.Name is not null).Select(port => port.Name!).ToArray();

        return "the connection string is a single '${secret:...}' placeholder, which addresses the tunnel at "
            + "the port it already names, but this backing service forwards "
            + $"{requested.Count} ports by name: {Quoted(names)}. Forward a single port for a whole-string "
            + "secret, or write the connection string with per-field '${secret:...}' placeholders and "
            + "'${port:<name>}' for each port instead.";
    }

    /// <summary>
    /// One exception for however many problems one connection string turned out to have, naming the
    /// backing service once rather than once per problem.
    /// </summary>
    /// <remarks>
    /// The shape <c>DeveloperConfigValidator.Failure</c> uses, and for its reason: a lone problem
    /// reads exactly as it did when it was thrown where it was found, so the ordinary case pays
    /// nothing for the collecting, and several read as a list with each remedy beside its own
    /// problem.
    /// </remarks>
    private static ServiceSourcesConfigurationException Failure(string name, IReadOnlyList<string> problems) =>
        new(problems.Count == 1
            ? $"Backing service '{Named(name)}': {problems[0]}"
            : $"Backing service '{Named(name)}': {problems.Count} problems with the connection string:"
              + string.Concat(problems.Select(problem => $"{Environment.NewLine}  - {problem}")));

    /// <summary>
    /// The error for <c>${port}</c> where the block names its ports, so there is no "the" port.
    /// </summary>
    private static string UnnamedPortAgainstABlock(
        ConnectionStringTemplate.Port port, IReadOnlyList<string> names) =>
        $"the connection string carries {ConfiguredValue.Escaped(port.AsWritten)}, which stands for the one "
        + $"forwarded port, but this backing service forwards {(names.Count == 1 ? "its port" : "several ports")} "
        + $"by name: {Quoted(names)}. Name the one this addresses, as {Spelled(names[0])}.";

    /// <summary>
    /// The error for <c>${port:&lt;name&gt;}</c> where a single unnamed port is forwarded.
    /// </summary>
    private static string NamedPortAgainstASinglePort(string name, ConnectionStringTemplate.Port port) =>
        $"the connection string carries {ConfiguredValue.Escaped(port.AsWritten)}, which names one of several "
        + "forwarded ports, but this backing service forwards a single unnamed port — the one written at "
        + $"'{ConfigKey(name, "Port")}'. Write '${{port}}' for it. To forward several instead, give each a name: "
        + "\"port\": { \"amqp\": 5672, \"management\": 15672 }.";

    /// <summary>
    /// The error for <c>${port:&lt;name&gt;}</c> naming a port the block does not carry.
    /// </summary>
    /// <remarks>
    /// Names the forwarded ports unconditionally, and adds a near miss when there is one. Either
    /// half alone is not enough: a near miss is what answers a typo, but
    /// <see cref="NearMiss.Nearest"/> returns nothing when the written name resembles none of them —
    /// and a developer looking at a name this backing service does not forward needs to be told
    /// which ones it does.
    /// </remarks>
    private static string NoSuchForwardedPort(
        ConnectionStringTemplate.Port port, IReadOnlyList<string> names)
    {
        // Only worth asking of a name short enough to be a typo. The edit distance is O(n·m) with
        // both operands developer-written and neither length-capped, and every other caller in this
        // package compares against short declared field names rather than against free text.
        var near = port.Name!.Length <= LongestNameWorthComparing
            ? NearMiss.Nearest(port.Name!, names, candidate => candidate)
            : [];

        // Escaped, not trimmed of its quotes: Escaped wraps its value in apostrophes, and stripping
        // them back off also strips any the name itself begins or ends with — so a port named
        // 'amqp', apostrophes and all, was suggested as amqp, a port that does not exist.
        var suggestion = near.Count == 1 ? $" Did you mean {ConfiguredValue.Escaped(near[0])}?" : "";

        // Escaping is not injective — a real tab and a written backslash-t both print as '\t' — so a
        // message quoting two names can render them identically and read as "you wrote X, which does
        // not exist; did you mean X?". Saying that the difference is one you cannot see is the only
        // thing that makes such a message actionable, and it is the caveat the developer-config
        // validator already attaches for the same reason.
        var indistinguishable =
            !ConfiguredValue.PrintsAsItself(port.Name!)
            || names.Any(candidate => !ConfiguredValue.PrintsAsItself(candidate));

        var caveat = indistinguishable
            ? " One of these spellings carries characters you cannot pick out by looking, so two of them "
              + "can print the same — retype the name rather than copying it back."
            : "";

        return $"the connection string carries {ConfiguredValue.Escaped(port.AsWritten)}, which names a port this "
            + $"backing service does not forward.{suggestion} It forwards {Quoted(names)}.{caveat}";
    }

    /// <summary>
    /// How long a written port name may be before a near-miss suggestion stops being worth its cost.
    /// </summary>
    private const int LongestNameWorthComparing = 128;

    /// <summary>
    /// A port name as it is written in a connection string placeholder, ready to paste.
    /// </summary>
    /// <remarks>
    /// Escaped like every other echo of a name — the list of forwarded ports beside this one always
    /// was, so a name carrying a newline was rendered safely in one half of a sentence and forged a
    /// line in the other. Escaped <em>bare</em>, since this builds its own quoting around the name.
    /// </remarks>
    private static string Spelled(string portName) => $"'${{port:{ConfiguredValue.Bare(portName)}}}'";

    /// <summary>Developer-invented names, escaped, quoted and in the order they are forwarded.</summary>
    private static string Quoted(IEnumerable<string> names) =>
        string.Join(", ", names.Select(ConfiguredValue.Escaped));

    /// <summary>
    /// Whether the whole connection string is one <c>${secret:...}</c> placeholder and nothing else.
    /// </summary>
    /// <remarks>
    /// The shape is enough to decide by, and it is knowable now: the template comes from
    /// <c>servicesources.local.json</c>, which is read while the AppHost is composed, even though
    /// the value it stands for is not fetched until start time. A template with anything else in it
    /// — a literal, a <c>${port}</c>, a second secret — is not this mode, because the developer has
    /// given somewhere to substitute a local port into and the allocator's collision avoidance can
    /// be kept.
    /// <para>
    /// Both shapes occur in practice. Operator-generated secrets (CloudNativePG's <c>&lt;cluster&gt;-app</c>)
    /// carry per-field keys, and per-field is preferred where it exists. Hand-authored secrets —
    /// a Sealed Secret holding one <c>connectionString</c> — often carry only the whole string, and
    /// re-shaping one means re-sealing against the cluster's key and a commit to a GitOps repo that
    /// a platform team frequently owns rather than the developer.
    /// </para>
    /// </remarks>
    private static bool IsWholeSecret(ConnectionStringTemplate template) =>
        template.Segments is [ConnectionStringTemplate.Secret];

    /// <summary>
    /// The remote port, once it is known that nothing local holds it.
    /// </summary>
    /// <remarks>
    /// Asked only when the AppHost is being run. Publishing writes a manifest: there is no
    /// port-forward, nothing binds a local port, and whatever happens to be listening on the
    /// machine doing the publishing has no bearing on the file it produces. Checking there would
    /// fail a CI publish for a developer's running database.
    /// </remarks>
    private int RequireLocalPortFree(IDistributedApplicationBuilder builder, string name, int remotePort)
    {
        if (!builder.ExecutionContext.IsRunMode || portAllocator.IsAvailable(remotePort))
        {
            return remotePort;
        }

        throw new ServiceSourcesConfigurationException(
            $"Backing service '{Named(name)}': its connection string is a single '${{secret:...}}' placeholder, so the "
            + $"port-forward has to listen locally on {remotePort} — the port "
            + $"'{ConfigKey(name, "Port")}' names — and something is already listening on {remotePort} here. "
            + $"Free that port, or write the connection string yourself with per-field '${{secret:...}}' "
            + $"placeholders and a '${{port}}' in it, which lets this source pick a local port that is free. "
            + $"Adding '${{port}}' to the whole-string placeholder does not work: it stops being a whole-string "
            + "secret, and the in-cluster host inside the fetched value is then left as written.");
    }

    /// <summary>
    /// The parameter one <c>${secret:...}</c> placeholder becomes.
    /// </summary>
    /// <remarks>
    /// <c>secret: true</c> so the dashboard masks it, which is free and is the whole reason the
    /// value should travel as a parameter rather than as text spliced into a connection string.
    /// <paramref name="remotePort"/> is only read by <see cref="Fetch"/> when
    /// <paramref name="wholeSecret"/> is <see langword="true"/> — a per-field secret ignores it.
    /// </remarks>
    private IResourceBuilder<ParameterResource> SecretParameter(
        IDistributedApplicationBuilder builder,
        string name,
        string service,
        string context,
        string @namespace,
        ConnectionStringTemplate.Secret secret,
        bool wholeSecret,
        int remotePort)
    {
        var parameterName = ParameterName(name, secret);

        var origin = new SecretParameterOrigin(name, secret.Name, secret.Key);

        // The same placeholder written twice is one value, so it is one parameter. Adding it twice
        // would throw on the duplicate name — naming a resource the developer never wrote — for a
        // template that is perfectly ordinary: a connection string that names the host and a
        // failover host carries the same credential twice.
        //
        // Matched on what the parameter was made FOR, not on the name it derived. The name joins
        // three parts with hyphens and the join is ambiguous — backing service 'db' with key 'b-c'
        // and backing service 'db-a' with key 'c' both read as 'db-a-b-c' — so trusting the name
        // would hand one backing service another's credential, silently, where before there was at
        // least a duplicate-name failure. Aspire folds case in a resource name, so the lookup does
        // too.
        if (builder.Resources.OfType<ParameterResource>()
                .FirstOrDefault(p => string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase))
            is { } existing)
        {
            if (!existing.Annotations.OfType<SecretParameterOrigin>().Contains(origin))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Backing service '{Named(name)}': the placeholder '{secret.AsWritten}' derives the parameter name "
                    + $"'{parameterName}', which another resource in this AppHost already uses for something else. "
                    + "The name is the backing service, the secret and the key joined by hyphens, so two different "
                    + "placeholders can spell it the same way. Rename the backing service, or use a secret or key "
                    + "that does not collide.");
            }

            return builder.CreateResourceBuilder(existing);
        }

        try
        {
            return builder
                .AddParameter(
                    parameterName,
                    () => Fetch(name, service, context, @namespace, secret, wholeSecret, remotePort),
                    secret: true)
                .WithAnnotation(origin);
        }
        catch (ArgumentException ex)
        {
            // Derived from three names the developer wrote separately, none of which Aspire saw, so
            // its complaint names a resource that appears nowhere in the AppHost or the config file.
            // The same shape the tunnel's name uses, and for the same reason: Aspire stays the
            // authority on the rule, this adds where the name came from. Only length can reach here
            // now that the characters are folded — which is why the remedy named is a shorter name
            // and nothing about spelling.
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{Named(name)}': the placeholder '{secret.AsWritten}' becomes a parameter named "
                + $"'{parameterName}', after the backing service, the secret and the key, and Aspire rejected that "
                + $"name — \"{WithoutParameterSuffix(ex.Message)}\" It is {parameterName.Length} characters, built "
                + $"from the backing service ('{name}', {name.Length}), the secret ('{secret.Name}', "
                + $"{secret.Name.Length}) and the key ('{secret.Key}', {secret.Key.Length}), joined. Shorten "
                + "whichever of those you own — the backing service's name is the one this AppHost decides.",
                ex);
        }
    }

    /// <summary>
    /// What a secret parameter was created for, so that reusing one can be sure it is the same.
    /// </summary>
    /// <remarks>
    /// The derived name cannot answer that question: it joins three parts with hyphens, and the
    /// join is ambiguous. Carried on the resource rather than in a table here because this source is
    /// one instance shared by every AppHost in the process, and the answer belongs to one model.
    /// </remarks>
    private sealed record SecretParameterOrigin(string BackingService, string SecretName, string Key)
        : IResourceAnnotation;

    /// <summary>
    /// The parameter name one placeholder derives, in the characters Aspire allows.
    /// </summary>
    /// <remarks>
    /// Aspire admits ASCII letters, digits and hyphens in a resource name. Kubernetes admits
    /// <c>-</c>, <c>.</c>, <c>_</c> and alphanumerics in a secret's name and its keys, and the ones
    /// that differ are not exotic: <c>DB_PASSWORD</c> is what <c>--from-env-file</c> produces,
    /// <c>ca.crt</c> and <c>tls.key</c> are TLS material, and <c>.dockerconfigjson</c> is the key the
    /// API itself gives a pull secret. Refusing those would leave the placeholder unusable against
    /// most real secrets, and the developer could not fix it: the key is in a cluster they may not
    /// own.
    /// <para>
    /// So the name is folded rather than refused. It is an identifier nobody writes — it appears in
    /// the dashboard and in a failure message, never in configuration — so folding costs nothing a
    /// developer relies on.
    /// </para>
    /// <para>
    /// A folded name carries four hex digits of the original, because folding is lossy:
    /// <c>ca.crt</c> and <c>ca_crt</c> both read as <c>ca-crt</c>, and two parameters sharing a
    /// name would be one value serving two keys. The suffix is derived from the text, so it is the
    /// same on every run — a name that changed between runs would move what the dashboard shows.
    /// </para>
    /// </remarks>
    private static string ParameterName(string name, ConnectionStringTemplate.Secret secret)
    {
        var written = $"{name}-{secret.Name}-{secret.Key}";
        var folded = new StringBuilder(written.Length);

        foreach (var c in written)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                folded.Append(c);
            }
            // Runs collapse rather than each becoming its own hyphen: Aspire refuses consecutive
            // hyphens as well as the characters that produced them, and '.dockerconfigjson' after
            // the separator is exactly that pair.
            else if (folded.Length > 0 && folded[^1] != '-')
            {
                folded.Append('-');
            }
        }

        // A trailing hyphen is refused too, and a key ending in '.' or '_' leaves one.
        while (folded.Length > 0 && folded[^1] == '-')
        {
            folded.Length--;
        }

        var foldedName = folded.ToString();

        // Aspire wants a letter first, and a backing service may legitimately begin with a digit —
        // '3proxy' names a real thing. Prefixed rather than refused, for the same reason the
        // characters are folded: this identifier is derived, nobody writes it, and a name the
        // developer cannot spell differently is not a mistake to report back to them.
        if (foldedName.Length > 0 && !char.IsAsciiLetter(foldedName[0]))
        {
            return $"s-{foldedName}-{Fingerprint(written)}";
        }

        return foldedName == written ? written : $"{foldedName}-{Fingerprint(written)}";
    }

    /// <summary>
    /// Four hex digits standing for a string, stable across runs.
    /// </summary>
    /// <remarks>
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>, which .NET randomises per process:
    /// a parameter that changed its name between runs would change what the dashboard shows and
    /// what a failure message names.
    /// </remarks>
    private static string Fingerprint(string text)
    {
        var hash = 2166136261u;

        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (hash & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One deferred fetch, with the backing service's name added to whatever went wrong.
    /// </summary>
    private string Fetch(
        string name,
        string service,
        string context,
        string @namespace,
        ConnectionStringTemplate.Secret secret,
        bool wholeSecret,
        int remotePort)
    {
        string value;

        try
        {
            value = secretReader.Read(context, @namespace, secret.Name, secret.Key);
        }
        catch (KubernetesSecretException ex)
        {
            // The reader knows the secret and the key; only this knows which backing service asked,
            // and a parameter's name is the only other thing the dashboard shows beside the failure.
            throw new KubernetesSecretException($"Backing service '{Named(name)}': {ex.Message}", ex);
        }

        if (!wholeSecret)
        {
            return value;
        }

        RequireRewritableShape(name, value, secret);

        var localised = ToLocalhost(value, service, @namespace, out var rewrites, out var ports);

        RequireNothingStillAddressesTheCluster(name, localised, service, @namespace, secret);

        // Whole-string mode exists because the fetched string is written for use inside the cluster
        // and is unusable as fetched. If nothing was rewritten, that premise did not hold — the
        // secret addresses the service by a name this does not recognise (a ClusterIP, a pod's
        // StatefulSet name, a namespace other than the configured one), and handing the value over
        // unchanged would send the credentials in it to whatever that name resolves to here, which
        // on a machine with a VPN or a search domain need not be nothing. Refusing is the only safe
        // answer, and the developer can see both halves in the message.
        if (rewrites == 0)
        {
            throw new KubernetesSecretException(
                $"Backing service '{Named(name)}': the connection string in key '{secret.Key}' of secret '{secret.Name}' "
                + $"does not address '{service}' in any form this can rewrite — '{service}', '{service}.{@namespace}', "
                + $"'.svc' or '.svc.cluster.local', after a host keyword or a URI's '@' or '//'. Nothing was "
                + $"substituted, so the value still points into the cluster and would not reach the port-forward. "
                + $"Check that '{ConfigKey(name, "Service")}' names the service the secret was written against.");
        }

        RequireSecretPortMatches(name, ports, secret, remotePort);

        return localised;
    }

    /// <summary>
    /// Refuses a fetched value whose shape this cannot account for, before rewriting any of it.
    /// </summary>
    /// <remarks>
    /// <b>The mode is bounded by what it can parse, rather than by what has been thought of.</b>
    /// Finding where a host ends means knowing where a value ends, and a quoted or braced value may
    /// carry the separator inside it — <c>Server={orders;orders}</c> and
    /// <c>Server="orders;orders"</c> both end a naive region at the first <c>;</c>, leaving the
    /// second host addressed at the cluster while something was rewritten and the check below saw
    /// a rewrite happen.
    /// <para>
    /// Three rounds of review each found another shape that slipped through a scanner built to
    /// recognise shapes. So this stops recognising and starts refusing: a value carrying a quote or
    /// a brace is not rewritten at all. That is a narrow loss — a whole-string secret is a
    /// hand-authored connection string, and quoting is rare in one — against a class of silent
    /// leak that patching had not closed in three attempts.
    /// </para>
    /// </remarks>
    private static void RequireRewritableShape(string name, string value, ConnectionStringTemplate.Secret secret)
    {
        if (value.AsSpan().IndexOfAny("\"'{}") < 0)
        {
            return;
        }

        throw new KubernetesSecretException(
            $"Backing service '{Named(name)}': the connection string in key '{secret.Key}' of secret '{secret.Name}' "
            + "quotes or braces one of its values, and this cannot find where a host ends in a value that may "
            + "carry a separator inside it. Rather than rewrite part of it and leave the rest addressed at the "
            + "cluster, it is refused. Write the connection string yourself with per-field '${secret:...}' "
            + "placeholders and a '${port}', which needs no rewriting at all.");
    }

    /// <summary>
    /// Refuses a rewritten value that still addresses the cluster somewhere.
    /// </summary>
    /// <remarks>
    /// The rewrite decides what to change; this decides whether to trust the result, and the two
    /// are deliberately not the same code. Every leak found in this mode has had the same shape —
    /// one host rewritten, another left — which the "something was rewritten" count cannot see.
    /// <para>
    /// It asks only about forms that can be nothing but an address: a name qualified by its
    /// namespace, and a name carrying a port. A bare service name is left alone, because
    /// <c>Database=orders</c> beside <c>Host=orders</c> is the ordinary shape and refusing it would
    /// make the mode unusable for the case it was built for.
    /// </para>
    /// </remarks>
    private static void RequireNothingStillAddressesTheCluster(
        string name, string localised, string service, string @namespace, ConnectionStringTemplate.Secret secret)
    {
        var escaped = Regex.Escape(service);

        var stillAddressed = Regex.Match(
            localised,
            $@"(?<![\w.-])(?:{escaped}\.{Regex.Escape(@namespace)}(?:\.svc(?:\.cluster\.local)?)?\.?"
                + $@"|{escaped}\s*[:,]\s*\d{{1,5}})(?![\w-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        if (!stillAddressed.Success)
        {
            return;
        }

        throw new KubernetesSecretException(
            $"Backing service '{Named(name)}': the connection string in key '{secret.Key}' of secret '{secret.Name}' "
            + $"still addresses the cluster after rewriting — '{stillAddressed.Value}' was left in it. Something "
            + "was rewritten, so this is a shape only partly recognised rather than one that names the service "
            + "in no form at all. Handing it over would send the credentials in it wherever that name resolves "
            + "here. Write the connection string yourself with per-field '${secret:...}' placeholders and a "
            + "'${port}', which needs no rewriting.");
    }

    /// <summary>
    /// Refuses a whole-string secret whose port is not the one being forwarded.
    /// </summary>
    /// <remarks>
    /// The tunnel's two ends both come from <c>kubernetes.port</c>; the port in the connection
    /// string comes from the secret, and nothing made them agree. Unchecked, the app dials a port
    /// the tunnel does not serve while the health check probes the port it does — so every resource
    /// reports healthy and the connection goes nowhere, or worse, to whatever else on the developer's
    /// machine happens to hold that port. That is the failure the <c>${port}</c> rule exists to
    /// prevent, arriving through the one door that rule no longer guards.
    /// </remarks>
    private static void RequireSecretPortMatches(
        string name, int[] ports, ConnectionStringTemplate.Secret secret, int remotePort)
    {
        // Every port a rewritten host is addressed on, not the first number in the string. A host
        // list can name several, and the tunnel serves one — so any of them differing is the
        // mismatch, and the first is only the one to name.
        if (ports.FirstOrDefault(p => p != remotePort) is not (var port and not 0))
        {
            return;
        }

        throw new KubernetesSecretException(
            $"Backing service '{Named(name)}': the connection string in key '{secret.Key}' of secret '{secret.Name}' "
            + $"addresses port {port}, and the port-forward serves {remotePort} — the port "
            + $"'{ConfigKey(name, "Port")}' names. The tunnel's health check watches {remotePort}, so every "
            + $"resource would report healthy while the app dialled {port}: nothing there, or whatever else on "
            + $"this machine happens to hold it. Set '{ConfigKey(name, "Port")}' to {port}, or point it at a "
            + $"secret written for port {remotePort}.");
    }

    /// <summary>
    /// Rewrites the in-cluster host in a whole-string secret to the local end of the tunnel.
    /// </summary>
    /// <remarks>
    /// A secret written for use inside the cluster addresses the service by its Kubernetes name, in
    /// any of the four forms a pod can resolve: <c>&lt;service&gt;</c>, <c>&lt;service&gt;.&lt;namespace&gt;</c>,
    /// with <c>.svc</c>, and fully qualified with <c>.svc.cluster.local</c>. None of them resolve
    /// on the developer's machine, so the string is useless as fetched, and the port-forward is the
    /// thing that makes it usable — which is why this mode forwards the same port and rewrites only
    /// the host.
    /// <para>
    /// <b>Anchored to where a host can appear, not merely bounded.</b> A word boundary is not
    /// enough: a service named <c>orders</c> reaches a database usually also named <c>orders</c>,
    /// and <c>Host=orders;Database=orders</c> would have both rewritten — leaving a string that
    /// connects to the right server and then asks for a database called <c>localhost</c>, which
    /// fails far from its cause. So the name is rewritten only where a connection string can put a
    /// host: after one of the keywords that introduces one, after <c>@</c> in a URI's authority, or
    /// after <c>//</c> in a scheme.
    /// </para>
    /// <para>
    /// The keyword list is the cost of that: a dialect spelling its host key some other way is
    /// left alone, and the developer sees an unrewritten in-cluster name rather than a silently
    /// wrong value. That is the right direction to fail in — one is visible immediately, the other
    /// is a wrong database.
    /// </para>
    /// </remarks>
    private static string ToLocalhost(
        string connectionString, string service, string @namespace, out int rewrites, out int[] ports)
    {
        try
        {
            var (localised, count, found) = Rewrite(connectionString, service, @namespace);

            rewrites = count;
            ports = found;

            return localised;
        }
        catch (RegexMatchTimeoutException ex)
        {
            // A named failure rather than a stack trace out of the callback Aspire is resolving.
            // Reachable only for a value shaped to be pathological, whose author already owns the
            // credential in it — but what the dashboard shows is the message, so there should be one.
            throw new KubernetesSecretException(
                "Rewriting the in-cluster host in the fetched connection string took too long and was abandoned. "
                + "The value is shaped in a way this cannot scan quickly; a per-field '${secret:...}' template "
                + "avoids the rewrite entirely.",
                ex);
        }
    }

    /// <summary>
    /// Rewrites every in-cluster host, and reports the ports the rewritten hosts are addressed on.
    /// </summary>
    /// <remarks>
    /// <b>Region by region, not prefix by prefix.</b> A prefix-anchored rewrite finds the host a
    /// keyword or a <c>//</c> introduces and stops there — but a host list carries the rest after a
    /// comma with nothing in front of them, so <c>Server=orders,orders</c> and a three-node
    /// <c>mongodb://…orders:27017,orders:27018,orders:27019/…</c> left every host but the first
    /// addressed at the cluster, while the "something was rewritten" check counted one and passed.
    /// So the prefix now selects a <em>region</em> — everything up to the <c>;</c> that ends a
    /// keyword's value, or to the <c>/</c> that ends a URI's authority — and every host in it is
    /// rewritten.
    /// <para>
    /// The ports come from the same pass, read off what follows each host that was actually
    /// rewritten. Scanning the whole string for a port instead finds whichever number appears first,
    /// which is a decoy in <c>Password=Port=9999!;Server=orders,1433</c> and a false pass in
    /// <c>Options=Port=1433;Server=orders,9999</c>.
    /// </para>
    /// </remarks>
    private static (string Localised, int Rewrites, int[] Ports) Rewrite(
        string connectionString, string service, string @namespace)
    {
        var replaced = 0;
        var ports = new List<int>();

        var host = $@"(?<![\w.-])(?:{Regex.Escape(service)}"
            + $@"(?:\.{Regex.Escape(@namespace)}(?:\.svc(?:\.cluster\.local)?)?)?)"
            // Either a trailing dot that ends the name — the absolute form, which resolves the same
            // — or no host character at all after it. Written as two alternatives because the dot
            // has to be consumed in the first case and refused in the second, where it would mean
            // the name continues into a namespace this is not looking for.
            + @"(?:\.(?![\w-])|(?![\w.-]))";

        var localised = Regex.Replace(
            connectionString,
            // A keyword's region ends at the ';' that ends its value — or at whitespace, because a
            // libpq conninfo string ('host=orders port=5432 user=orders') separates its fields with
            // spaces and carries no ';' at all. Running to the ';' there means running to the end,
            // which rewrote a 'user=' that happened to equal the service name.
            $"(?<lead>{Keyword})(?<region>[^;\\s]*)"
                + $"|(?<lead>//(?:[^/@;\\s]*@)?)(?<region>[^/?;\\s]*)",
            region =>
            {
                var rewritten = Regex.Replace(
                    region.Groups["region"].Value,
                    host,
                    _ =>
                    {
                        replaced++;
                        return "localhost";
                    },
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));

                foreach (Match addressed in AddressedPort().Matches(rewritten))
                {
                    if (int.TryParse(addressed.Groups["port"].ValueSpan, CultureInfo.InvariantCulture, out var port))
                    {
                        ports.Add(port);
                    }
                }

                return region.Groups["lead"].Value + rewritten;
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        // A port written as its own field rather than beside the host, which is how Npgsql and most
        // keyword dialects spell it: 'Host=orders;Port=5432'. Anchored to the start of a field, so
        // that a 'Port=' appearing inside some other field's *value* — a password of 'Port=9999!' —
        // is not read as one. Only asked once the hosts are known, so a string this does not
        // address at all contributes nothing.
        if (replaced > 0)
        {
            foreach (Match field in PortField().Matches(localised))
            {
                if (int.TryParse(field.Groups["port"].ValueSpan, CultureInfo.InvariantCulture, out var port))
                {
                    ports.Add(port);
                }
            }
        }

        return (localised, replaced, [.. ports]);
    }

    /// <summary>A port written as its own field, anchored so a value containing one is not read.</summary>
    [GeneratedRegex(@"(?:\A|;)\s*Port\s*=\s*(?<port>\d{1,5})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PortField();

    /// <summary>The port a rewritten host is addressed on, in either shape a host list writes it.</summary>
    /// <remarks>
    /// <c>:</c> in a URI and most keyword dialects, <c>,</c> in SQL Server's
    /// <c>Server=tcp:host,1433</c>. Read only inside a host region, so a <c>Port=</c> elsewhere in
    /// the string cannot be mistaken for one.
    /// </remarks>
    [GeneratedRegex(@"localhost[:,](?<port>\d{1,5})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressedPort();

    /// <summary>
    /// The places a connection string introduces a host, and how far that host's region reaches.
    /// </summary>
    /// <remarks>
    /// Case-insensitively, because DNS is: a secret naming <c>Orders-PG</c> reaches the same service
    /// as one naming <c>orders-pg</c>, and leaving the first unrewritten would send credentials into
    /// the cluster's address space.
    /// <para>
    /// A URI's region starts after the optional user information, which is what keeps a user name
    /// equal to the service name out of the rewrite: in
    /// <c>postgresql://orders:pw@orders:5432/db</c> the first <c>orders</c> is the user and the
    /// second is the host. A keyword's region ends at the <c>;</c> that ends its value, so nothing
    /// in another field can be rewritten or read as a port.
    /// </para>
    /// </remarks>
    private const string Keyword =
        @"(?:\A|[;,\s])\s*(?:Host|Hostname|Server|Data\s?Source|Addr|Address)\s*=\s*";

    /// <summary>
    /// The configuration key one of this block's fields is read from, for a message that has to
    /// name the layer that set it rather than only the file a developer usually writes it in.
    /// </summary>
    /// <remarks>
    /// #264: built from this class's own declared property names (<c>Kubernetes</c>, and whatever
    /// <paramref name="field"/> is passed), because this runs after binding and has no access to
    /// the raw section a developer actually wrote — unlike
    /// <c>DeveloperConfigValidator.Path</c>, which walks the raw config and echoes its casing
    /// verbatim. The two can print the same key with different casing; harmless, since
    /// configuration keys are case-insensitive.
    /// </remarks>
    private static string ConfigKey(string name, string field) =>
        $"{DeveloperConfiguration.BackingServicesKey}:{Named(name)}:Kubernetes:{field}";

    /// <summary>
    /// The backing service's name as a message shows it.
    /// </summary>
    /// <remarks>
    /// Escaped like every other echo. It is the AppHost author's own C# literal rather than
    /// configuration, and Aspire refuses anything but a plain resource name — but it refuses it at
    /// <c>AddConnectionString</c>, which is *after* every message in this file can be thrown, so an
    /// unusable name reaches these sentences before anything has rejected it. That matters most for
    /// the collecting report, where a newline forges a bullet and an entry header, which is the same
    /// defect the developer-config validator's own multi-entry report was fixed for.
    /// </remarks>
    private static string Named(string name) => ConfiguredValue.Bare(name);

    /// <summary>The same key spelled as the environment variable that sets it.</summary>
    private static string Environmentally(string configKey) =>
        configKey.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// An <see cref="ArgumentException"/>'s message without the <c>(Parameter 'name')</c> that
    /// <see cref="ArgumentException.Message"/> appends.
    /// </summary>
    /// <remarks>
    /// Quoted into a message of ours, so the parameter is plumbing from a call the developer did
    /// not make — and it lands immediately before the sentence explaining that the name was derived
    /// rather than written, which is the opposite of what naming a parameter suggests. The rule
    /// itself stays in Aspire's own words.
    /// </remarks>
    private static string WithoutParameterSuffix(string message)
    {
        var suffix = message.IndexOf(" (Parameter ", StringComparison.Ordinal);

        return suffix < 0 ? message : message[..suffix];
    }

    /// <summary>
    /// What each field this source cannot work without holds, in a phrase completing
    /// "…requires 'kubernetes.<c>field</c>' —", in the order the block is written in.
    /// </summary>
    /// <remarks>
    /// A developer who has just switched a backing service to this source reads these as the
    /// block's documentation, so each says what to write rather than only that something is
    /// missing. Ordered so a message listing several reads down the block rather than across a
    /// dictionary.
    /// </remarks>
    /// <remarks>
    /// <c>Field</c> is how the developer writes it in the file, <c>Property</c> how the same key is
    /// spelled in a configuration path. Both, rather than one derived from the other, because a
    /// message uses each in a different half of the same sentence and getting either wrong sends
    /// the reader looking for a key nobody wrote.
    /// <para>
    /// <c>IsWritten</c> travels in the row rather than in a lookup beside it, so that a fifth field
    /// is one row and cannot be half-added: a table entry with no predicate beside it would throw
    /// on lookup, and a predicate with no table entry would silently never be required.
    /// </para>
    /// </remarks>
    private static readonly (
        string Field,
        string Property,
        string WhatItIs,
        Func<KubernetesBackingServiceDeveloperConfig, bool> IsWritten)[] RequiredFields =
    [
        ("service", "Service", "the Kubernetes Service to forward to",
            k => !string.IsNullOrWhiteSpace(k.Service)),
        ("port", "Port",
            "the port that Service listens on inside the cluster, which is what the tunnel forwards to — one "
            + "number, or a name per port to forward several through the one tunnel",
            k => k.Port is { } ports && (ports.SinglePort is not null || ports.Count > 0)),
        ("context", "Context", "the kubectl context to forward through",
            k => !string.IsNullOrWhiteSpace(k.Context)),
        ("connectionString", "ConnectionString",
            "the connection string consumers receive, with '${port}' standing for the local end of the tunnel — "
            + "or '${port:<name>}' where the block names its ports, and '${secret:<name>:<key>}' reading a "
            + "credential from a Kubernetes secret",
            k => !string.IsNullOrWhiteSpace(k.ConnectionString)),
    ];

    /// <summary>
    /// Refuses an entry missing any field this source cannot work without, naming <b>all</b> of
    /// them.
    /// </summary>
    /// <remarks>
    /// All of them, rather than the first, for the reason <see cref="DeveloperConfigValidator"/>
    /// gives for collecting an entry's problems: reporting one per run costs a failed startup per
    /// key. That was invisible while <c>"direct"</c> was the only configured source, since it has a
    /// single field; this source has four, and a developer filling in a fresh block would otherwise
    /// pay four startups to be told what the block contains.
    /// <para>
    /// The port is checked for presence here and for range at the call site. They are different
    /// mistakes — one is a field nobody filled in, the other a field filled in wrongly — and only
    /// the first belongs in a list of what the block is missing.
    /// </para>
    /// </remarks>
    private static void RequireEveryField(string name, KubernetesBackingServiceDeveloperConfig kubernetes)
    {
        var missing = RequiredFields.Where(field => !field.IsWritten(kubernetes)).ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        // The file's own root key, not DeveloperConfiguration.BackingServicesKey: this sentence
        // sends the reader to the file, and the file spells the section "backingServices". The
        // colon-separated path belongs only in the environment-variable half, which is the one
        // place it is what the reader types.
        var where = $"under \"{name}\" in \"{DeveloperConfigFileSource.FileBackingServicesKey}\" in "
            + $"'{DeveloperConfiguration.FileName}'";

        // The shape DeveloperConfigValidator.Failure uses, and for its reason: one problem reads as
        // a sentence, several read as a list, and each keeps its own remedy beside it rather than
        // in a second list the reader has to pair up across a paragraph.
        if (missing.Length == 1)
        {
            var only = missing[0];

            throw new ServiceSourcesConfigurationException(
                $"Backing service '{Named(name)}': source 'kubernetes' requires 'kubernetes.{only.Field}' — "
                + $"{only.WhatItIs}. Add it {where}, or set {Environmentally(ConfigKey(name, only.Property))}."
                + PortIsWhichEnd(name, kubernetes));
        }

        var lines = missing.Select(field =>
            $"{Environment.NewLine}  - 'kubernetes.{field.Field}' — {field.WhatItIs}. Set it in the file, or as "
            + $"{Environmentally(ConfigKey(name, field.Property))}.");

        // Every field missing at once is a block nobody has filled in, which is the case a literal
        // example answers better than a list does — the same thing DirectBackingServiceSource
        // offers for its one field.
        var blank = missing.Length == RequiredFields.Length
            ? $"{Environment.NewLine}{Environment.NewLine}A whole entry reads: \"{name}\": {{ \"source\": "
              + "\"kubernetes\", \"kubernetes\": { \"service\": \"orders-pg\", \"port\": 5432, \"context\": "
              + "\"dev-west\", \"connectionString\": \"Host=localhost;Port=${port};Database=orders\" } }."
            : "";

        throw new ServiceSourcesConfigurationException(
            $"Backing service '{Named(name)}': source 'kubernetes' needs {missing.Length} fields the entry does not "
            + $"have, {where}:{string.Concat(lines)}{blank}{PortIsWhichEnd(name, kubernetes)}");
    }

    /// <summary>
    /// The sentence that answers "which of my two ports goes here", when the port is what is
    /// missing.
    /// </summary>
    /// <remarks>
    /// A developer filling this field in has the cluster's port and the one they would connect to
    /// in front of them, and only one of them goes in the file — so the message that asks for it
    /// says which. Nothing to say when the port is already written.
    /// </remarks>
    private static string PortIsWhichEnd(string name, KubernetesBackingServiceDeveloperConfig kubernetes) =>
        kubernetes.Port is { } written && (written.SinglePort is not null || written.Count > 0)
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}The local end of the tunnel is allocated rather than "
              + "configured, so only the cluster's own port is written here and a connection string names the "
              + "local end as '${port}' — or, where the block names its ports, as '${port:<name>}'. A block is "
              + "written a name at a time from a flat layer, as "
              + $"{Environmentally(ConfigKey(name, "Port"))}__<name>.";

    /// <summary>
    /// How many ports one backing service may forward through its tunnel.
    /// </summary>
    /// <remarks>
    /// A limit at all because the count comes from a developer-config block with no cardinality of
    /// its own, and every forwarded port costs a socket bound at once inside <c>Resolve</c> plus an
    /// argument on a command line. A block with thousands of entries would exhaust the process's
    /// file-descriptor limit and surface as a bare <c>SocketException</c> naming no backing service
    /// and no key — the one shape every message in this package is written to avoid.
    /// <para>
    /// The number is arbitrary and deliberately generous: the case this feature exists for is a
    /// broker with two ports.
    /// </para>
    /// </remarks>
    private const int MaxForwardedPorts = 32;

    /// <summary>
    /// The ports this source will forward, in the order the command line writes them, once each is
    /// known to be a port and there are not absurdly many.
    /// </summary>
    /// <remarks>
    /// Ordered by name, ordinally, so that the command line, the dashboard and every message listing
    /// them read the same on every run — a developer checks a connection string against a `kubectl`
    /// line by eye, and an order that moved between runs would make that impossible. The single-port
    /// form is one entry whose name is <see langword="null"/>.
    /// <para>
    /// Unlike the service side there is no catalog value to fall back to — the catalog carries no
    /// backing-service data at all, by decision — so the only questions left here are the range and
    /// the count.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(string? Name, int RemotePort)> RequireForwardablePorts(
        string name, KubernetesPorts ports)
    {
        if (ports.SinglePort is { } single)
        {
            // Unreachable through configuration: a section carrying both a value and named children
            // is refused by DeveloperConfigValidator before binding, and the binder itself takes one
            // path or the other. Kept because KubernetesPorts is a Dictionary and cannot refuse a
            // mutation — so a later caller adding an entry to a single-port instance fails here,
            // loudly, instead of having every name silently dropped by the return below.
            if (ports.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Backing service '{Named(name)}': 'kubernetes.port' bound to both a single port and "
                    + $"{ports.Count} named ports, which is not a shape configuration can produce.");
            }

            return [(null, RequirePortInRange(name, portName: null, single))];
        }

        if (ports.Count > MaxForwardedPorts)
        {
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{Named(name)}': 'kubernetes.port' names {ports.Count} ports, and one tunnel forwards "
                + $"at most {MaxForwardedPorts}. Every forwarded port holds a local socket open and adds a pair to "
                + $"one kubectl command line. The key is '{ConfigKey(name, "Port")}'.");
        }

        return ports
            .OrderBy(port => port.Key, StringComparer.Ordinal)
            .Select(port => ((string?)port.Key, RequirePortInRange(name, port.Key, port.Value)))
            .ToArray();
    }

    /// <summary>
    /// One port, once it is known to be present.
    /// </summary>
    /// <remarks>
    /// Applied to every named port and not only to a single one, and it is not a restatement of the
    /// validator's "is this a whole number". A port <em>name</em> carrying a colon flattens into the
    /// configuration key path, so the binder sees a section with children and no value and
    /// manufactures <c>default(int)</c> for it — measured. This is what stops a port number nobody
    /// wrote reaching a kubectl command line, and it must not be relaxed as redundant.
    /// </remarks>
    private static int RequirePortInRange(string name, string? portName, int port)
    {
        if (port is < 1 or > 65535)
        {
            var which = portName is null
                ? $"'kubernetes.port' is '{port}'"
                : $"'kubernetes.port' gives the port named {ConfiguredValue.Escaped(portName)} the value '{port}'";

            // The named form points at the entry rather than at the block, so the key named is the
            // one that is wrong — as the validator's own per-entry messages do.
            var key = portName is null
                ? ConfigKey(name, "Port")
                : $"{ConfigKey(name, "Port")}:{ConfiguredValue.Bare(portName)}";

            throw new ServiceSourcesConfigurationException(
                $"Backing service '{Named(name)}': {which}, which is not a port — a port is between "
                + $"1 and 65535. The key is '{key}'.");
        }

        return port;
    }

    /// <summary>
    /// The error for a connection string that never mentions the tunnel this source opens.
    /// </summary>
    /// <remarks>
    /// Refused rather than accepted, because the alternative fails silently and in the worst way it
    /// could. A template that writes the cluster's own port — <c>Port=5432</c>, copied from a
    /// manifest — addresses <c>localhost:5432</c> on the developer's machine: nothing is listening,
    /// or, far worse, their own Postgres container is, and the AppHost connects to the wrong
    /// database while reporting every resource healthy. The tunnel would sit alongside, forwarding a
    /// port nothing dials.
    /// <para>
    /// The whole-string secret form is the one template that legitimately carries no <c>${port}</c>
    /// — it arrives already addressed, and is answered by forwarding the same port number locally
    /// rather than by substitution. <see cref="RequireEveryPlaceholderIsResolvable"/> exempts that
    /// form before reaching here, so every template that does reach here without one is a mistake.
    /// </para>
    /// <para>
    /// Two mistakes, which is why the message names both. The template may never have had a
    /// <c>${port}</c>; or it had one and a shell ate it before the AppHost ran, which
    /// <see cref="ConnectionStringTemplate"/> describes and which produces exactly this — a valid
    /// template with no placeholder left in it. That second reader cannot be told anything by the
    /// first half of this message, since the spelling they wrote was already right.
    /// </para>
    /// </remarks>
    private static string NothingAddressesTheTunnel(
        string connectionString, IReadOnlyList<(string? Name, int RemotePort)> requested)
    {
        var shown = ConnectionStringRedaction.Redact(connectionString);

        // Only when something was actually replaced. Said unconditionally it would put "***" into
        // every one of these messages, including the ordinary case where nothing in the template
        // needed hiding and what is quoted is exactly what the developer wrote — leaving them to
        // wonder which part of it the package had hidden.
        var note = shown == connectionString || shown == ConnectionStringRedaction.Unscannable
            ? ""
            : " (a value is shown only under a key known to hold no secret; the rest read as ***, which does not mean they were secret)";

        // The advice has to follow the block that was written. Telling someone whose block names
        // its ports to "write '${port}'" earns them a second startup failure that contradicts this
        // one — ${port} is refused against a named block — so the two halves of that pair are the
        // one thing this message must not get wrong.
        var names = requested.Where(port => port.Name is not null).Select(port => port.Name!).ToArray();

        // Every clause that names a spelling names the one this entry actually takes. Telling a
        // developer whose block names its ports to write '${port}' would earn them a second startup
        // failure saying the opposite — which is the one thing this message must not do.
        // What is missing, said no more precisely than it is known: under a block of names, any one
        // of them would have done, and naming the first would tell a developer who meant the second
        // that they wrote the wrong one. The remedy below is where a single spelling belongs, because
        // there it is offered rather than asserted.
        var missing = names.Length == 0 ? "'${port}'" : "'${port:<name>}'";

        var remedy = names.Length == 0
            ? "Replace the port in it with '${port}', as 'Host=localhost;Port=${port};Database=orders'."
            : $"This backing service forwards its ports by name — {Quoted(names)} — so name the one this "
              + $"addresses: replace the port in it with {Spelled(names[0])}.";

        return "source 'kubernetes' opens a kubectl port-forward on a local port allocated at startup, but the "
            + $"connection string names no {missing} placeholder to put it in — so nothing would address the "
            + $"tunnel: \"{ConfiguredValue.Bare(shown)}\"{note}."
            + $"{Environment.NewLine}    {remedy}"
            + $"{Environment.NewLine}    If you did write {missing}, a shell expanded it away before the AppHost "
            + "saw it — '${...}' is a shell variable too, and double quotes do not protect it. Single-quote the "
            + "value, and use env 'NAME=value' for a key with a hyphen in it."
            + $"{Environment.NewLine}    A backing service reached at a fixed address you already have — an "
            + "ingress, or an instance you run yourself — is source 'direct' rather than this one.";
    }
}
