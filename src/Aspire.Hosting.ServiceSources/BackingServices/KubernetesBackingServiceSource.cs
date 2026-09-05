using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
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
internal sealed class KubernetesBackingServiceSource(IPortAllocator portAllocator) : IBackingServiceSource
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

        // Judged whole before a port is taken, for the reason the service-side source gives: a
        // template this source cannot resolve is config validation like every check above it, and
        // should not burn an allocation on its way to saying so.
        var requested = RequireForwardablePorts(name, kubernetes.Port!);

        var template = ConnectionStringTemplate.Parse(connectionString, name, ConfigKey(name, "ConnectionString"));

        RequireEveryPlaceholderIsResolvable(name, connectionString, template, requested);

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
        var localPorts = portAllocator.AllocatePorts(requested.Count);

        var forwarded = requested
            .Select((port, index) => new ForwardedPort(port.Name, port.RemotePort, localPorts[index]))
            .ToArray();

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

                // Unreachable: the pass above accepts only literals and resolvable ports. Kept so
                // that a placeholder kind added later fails loudly here rather than vanishing from
                // the connection string.
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
                $"Backing service '{name}': its port-forward runs as a resource named '{tunnelName}', after the "
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
        IReadOnlyList<(string? Name, int RemotePort)> requested)
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

                case ConnectionStringTemplate.Secret secret:
                    problems.Add(
                        $"the connection string carries '{secret.AsWritten}', and reading a value out of a "
                        + "Kubernetes secret is not supported yet. Put the value in the connection string, or set "
                        + "the whole connection string from a configuration layer that already holds it — user "
                        + $"secrets, or {Environmentally(ConfigKey(name, "ConnectionString"))}.");

                    break;

                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        if (problems.Count > 0)
        {
            throw Failure(name, problems);
        }

        if (ports == 0)
        {
            throw NothingAddressesTheTunnel(name, connectionString, requested);
        }
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
            ? $"Backing service '{name}': {problems[0]}"
            : $"Backing service '{name}': {problems.Count} problems with the connection string:"
              + string.Concat(problems.Select(problem => $"{Environment.NewLine}  - {problem}")));

    /// <summary>
    /// The error for <c>${port}</c> where the block names its ports, so there is no "the" port.
    /// </summary>
    private static string UnnamedPortAgainstABlock(
        ConnectionStringTemplate.Port port, IReadOnlyList<string> names) =>
        $"the connection string carries '{port.AsWritten}', which stands for the one forwarded port, but this "
        + $"backing service forwards several by name: {Quoted(names)}. Name the one this addresses, as "
        + $"'${{port:{names[0]}}}'.";

    /// <summary>
    /// The error for <c>${port:&lt;name&gt;}</c> where a single unnamed port is forwarded.
    /// </summary>
    private static string NamedPortAgainstASinglePort(string name, ConnectionStringTemplate.Port port) =>
        $"the connection string carries '{port.AsWritten}', which names one of several forwarded ports, but this "
        + $"backing service forwards the single port '{ConfigKey(name, "Port")}' names, so write '${{port}}'. To "
        + "forward several, give each one a name: \"port\": { \"amqp\": 5672, \"management\": 15672 }.";

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
        var near = NearMiss.Nearest(port.Name!, names, candidate => candidate);

        var suggestion = near.Count == 1
            ? $" Did you mean '{ConfiguredValue.Escaped(near[0]).Trim('\'')}'?"
            : "";

        return $"the connection string carries '{port.AsWritten}', which names a port this backing service does "
            + $"not forward.{suggestion} It forwards {Quoted(names)}.";
    }

    /// <summary>Developer-invented names, escaped, quoted and in the order they are forwarded.</summary>
    private static string Quoted(IEnumerable<string> names) =>
        string.Join(", ", names.Select(ConfiguredValue.Escaped));

    /// <summary>
    /// The configuration key one of this block's fields is read from, for a message that has to
    /// name the layer that set it rather than only the file a developer usually writes it in.
    /// </summary>
    private static string ConfigKey(string name, string field) =>
        $"{DeveloperConfiguration.BackingServicesKey}:{name}:Kubernetes:{field}";

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
            + "or '${port:<name>}' where the block names its ports",
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
                $"Backing service '{name}': source 'kubernetes' requires 'kubernetes.{only.Field}' — "
                + $"{only.WhatItIs}. Add it {where}, or set {Environmentally(ConfigKey(name, only.Property))}."
                + PortIsWhichEnd(kubernetes));
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
            $"Backing service '{name}': source 'kubernetes' needs {missing.Length} fields the entry does not "
            + $"have, {where}:{string.Concat(lines)}{blank}{PortIsWhichEnd(kubernetes)}");
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
    private static string PortIsWhichEnd(KubernetesBackingServiceDeveloperConfig kubernetes) =>
        kubernetes.Port is { } written && (written.SinglePort is not null || written.Count > 0)
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}The local end of the tunnel is allocated rather than "
              + "configured, so a connection string names it as '${port}' and only the cluster's own port is "
              + "written here.";

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
            return [(null, RequirePortInRange(name, portName: null, single))];
        }

        if (ports.Count > MaxForwardedPorts)
        {
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{name}': 'kubernetes.port' names {ports.Count} ports, and one tunnel forwards "
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
                ? "'kubernetes.port' is"
                : $"'kubernetes.port' names a port {ConfiguredValue.Escaped(portName)}, which is";

            throw new ServiceSourcesConfigurationException(
                $"Backing service '{name}': {which} '{port}', which is not a port — a port is between "
                + $"1 and 65535. The key is '{ConfigKey(name, "Port")}'.");
        }

        return port;
    }

    /// <summary>
    /// What is shown in place of a connection string that could not be scanned.
    /// </summary>
    /// <remarks>
    /// Named so the caller can tell it from a redacted value and drop the sentence explaining
    /// how passwords are shown — nothing here was shown, redacted or otherwise.
    /// </remarks>
    private const string Unscannable =
        "<connection string omitted: it could not be scanned for credentials>";

    /// <summary>
    /// The credential-bearing parts of a connection string, for the one message that echoes one
    /// back.
    /// </summary>
    /// <remarks>
    /// Two shapes cover what a connection string does with a secret: a keyword whose value runs to
    /// the next <c>;</c>, and a URI authority's <c>user:pass@host</c>. Matched case-insensitively,
    /// because keyword casing is a dialect's own business.
    /// <para>
    /// The URI branch tries a <c>;</c>-free password first and falls back to an <c>=</c>-free one,
    /// which looks fussy and is load-bearing in both directions. Allowing <c>;</c> unconditionally
    /// let <c>Data Source=tcp://host:1433;UID=a@b.com</c> run to the <em>email's</em> <c>@</c> and
    /// redact <c>1433;UID=a</c> — corrupting the string this message exists to display. Forbidding
    /// it outright then leaked <c>redis://user:pa;ss@db</c> whole, because the password could no
    /// longer reach its own <c>@</c> and nothing matched: RFC 3986 puts <c>;</c> in
    /// <c>sub-delims</c>, which <c>userinfo</c> admits raw, so such a password is legal and
    /// unencoded. Preferring the narrow read and falling back to the wide one separates them: the
    /// corrupting case carries an <c>=</c> and the leaking case does not.
    /// </para>
    /// <para>
    /// Deliberately not exhaustive, and the message says the value was redacted rather than
    /// claiming it is safe. A backend naming its secret something this misses would still be
    /// echoed, so this narrows the blast radius rather than closing it. That is the honest trade:
    /// this message exists to show the developer what <em>arrived</em> — the shell-expansion case is
    /// only diagnosable by seeing it — and a message that showed nothing would not do that.
    /// </para>
    /// </remarks>
    private static readonly Regex Credentials = new(
        @"(?<=(?:password|pwd|secret|token|accountkey|accesskey|apikey|signature)\s*=)[^;]*"
        + @"|(?<=://[^:/@\s]{0,256}:)(?:[^@/\s;]*|[^@/\s=]*)(?=@)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// <paramref name="connectionString"/> with the credentials this recognizes replaced.
    /// </summary>
    /// <remarks>
    /// Because this message is echoed where messages go: an AppHost's startup failure is relayed
    /// into <c>~/.aspire/logs</c> and routinely pasted into an issue. Every other value this package
    /// echoes is malformed, blank or a single token — this is the only one that is a whole, valid
    /// connection string, so it is the only one that can carry a password.
    /// </remarks>
    private static string Redacted(string connectionString)
    {
        try
        {
            return Credentials.Replace(connectionString, "***");
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological value is not a reason to fail differently than the developer expects,
            // and it is emphatically not a reason to print the thing this method exists to hide.
            return Unscannable;
        }
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
    /// The whole-string secret form is the one template that will legitimately carry no
    /// <c>${port}</c> — it arrives already addressed, and is answered by forwarding the same port
    /// number locally rather than by substitution. That form needs secrets, which this source does
    /// not read yet, so today every template that reaches here without a <c>${port}</c> is a
    /// mistake.
    /// </para>
    /// <para>
    /// Two mistakes, which is why the message names both. The template may never have had a
    /// <c>${port}</c>; or it had one and a shell ate it before the AppHost ran, which
    /// <see cref="ConnectionStringTemplate"/> describes and which produces exactly this — a valid
    /// template with no placeholder left in it. That second reader cannot be told anything by the
    /// first half of this message, since the spelling they wrote was already right.
    /// </para>
    /// </remarks>
    private static ServiceSourcesConfigurationException NothingAddressesTheTunnel(
        string name, string connectionString, IReadOnlyList<(string? Name, int RemotePort)> requested)
    {
        var shown = Redacted(connectionString);

        // Only when something was actually replaced. Said unconditionally it would put "***" into
        // every one of these messages, including the ordinary case where the template carries no
        // credential at all and what is quoted is exactly what the developer wrote — leaving them
        // to wonder which part of it the package had hidden.
        var note = shown == connectionString || shown == Unscannable
            ? ""
            : " (a credential in it shown as ***)";

        // The advice has to follow the block that was written. Telling someone whose block names
        // its ports to "write '${port}'" earns them a second startup failure that contradicts this
        // one — ${port} is refused against a named block — so the two halves of that pair are the
        // one thing this message must not get wrong.
        var names = requested.Where(port => port.Name is not null).Select(port => port.Name!).ToArray();

        var remedy = names.Length == 0
            ? "Replace the port in it with '${port}', as 'Host=localhost;Port=${port};Database=orders'."
            : $"This backing service forwards its ports by name — {Quoted(names)} — so name the one this "
              + $"addresses: replace the port in it with '${{port:{names[0]}}}'.";

        return new(
            $"Backing service '{name}': source 'kubernetes' opens a kubectl port-forward on a local port allocated "
            + $"at startup, but the connection string names no '${{port}}' placeholder to put it in — so nothing "
            + $"would address the tunnel: \"{shown}\"{note}. "
            + remedy
            + " If you did write it, a shell expanded it "
            + "away before the AppHost saw it — '${...}' is a shell variable too, and double quotes do not protect "
            + "it. Single-quote the value, and use env 'NAME=value' for a key with a hyphen in it. A backing "
            + $"service reached at a fixed address the developer already has — an ingress, or an instance they run "
            + $"themselves — is source 'direct' rather "
            + $"than this one. The key is '{ConfigKey(name, "ConnectionString")}'.");
    }
}
