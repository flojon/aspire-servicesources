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
        var remotePort = RequirePortInRange(name, kubernetes.Port!.Value);
        var @namespace = kubernetes.Namespace ?? KubectlPortForward.DefaultNamespace;

        var template = ConnectionStringTemplate.Parse(connectionString, name, ConfigKey(name, "ConnectionString"));

        // Decided from the template's shape, which is local configuration and therefore known now,
        // even though the value it stands for is not fetched until start time. See
        // <see cref="IsWholeSecret"/> for why the shape is enough.
        var wholeSecret = IsWholeSecret(template);

        // Judged whole before a port is taken, for the reason the service-side source gives: a
        // template this source cannot resolve is config validation like every check above it, and
        // should not burn an allocation on its way to saying so.
        RequireEveryPlaceholderIsResolvable(name, connectionString, template, wholeSecret);

        // Whole-string mode cannot choose its port. The connection string is one opaque secret
        // written against the cluster, so there is no placeholder to substitute a local port into
        // and the only rewrite available is the host — which means the port has to match the one
        // the string already names. Giving up the allocator's collision avoidance is the cost of
        // the mode, so the collision is reported here rather than left to the tunnel's log.
        var localPort = wholeSecret
            ? RequireLocalPortFree(builder, name, remotePort)
            : portAllocator.AllocatePort();

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
                        expression, localPort.ToString(CultureInfo.InvariantCulture));
                    break;

                // Late, and as a parameter rather than text: the value is in the cluster, and
                // fetching it here would run kubectl while the AppHost is still being composed —
                // the path main deliberately moved off when local project resolution became
                // deferred — and would fail the whole AppHost for a developer who is merely not
                // logged in yet. Aspire resolves a parameter when something asks for its value.
                case ConnectionStringTemplate.Secret secret:
                    expression.Append(
                        $"{SecretParameter(builder, name, service, context, @namespace, secret, wholeSecret, remotePort).Resource}");
                    break;

                // Unreachable: the walk above accepts only literals, the unnamed port and secrets.
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
                    KubectlPortForward.Args(service, localPort, remotePort, context, kubernetes.Namespace))
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

        var healthCheckKey = $"{name}-tunnel-tcp";

        builder.Services
            .AddHealthChecks()
            .AddCheck(healthCheckKey, new LocalPortHealthCheck(name, localPort), timeout: ProbeTimeout);

        // On the connection string alone, and not also on the tunnel, though the tunnel is what the
        // socket actually belongs to. The connection string is what a consumer waits for, which is
        // the whole reason this source has a health check; the tunnel would gain only a badge in
        // the dashboard. Aspire runs one monitor loop per resource, so a second resource carrying
        // this key would run the probe twice per cycle — and every probe is a connection kubectl
        // logs ("Handling connection for <port>") and the database behind it may log as an
        // incomplete startup packet. The tunnel's log is where a bad context or an expired
        // credential shows up, and it is worth keeping readable.
        return backingService.WithHealthCheck(healthCheckKey);
    }

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
    /// the model. Stage 3 took the secret branch out of it; what is left is the named port, which
    /// <see href="https://github.com/flojon/aspire-servicesources/issues/233">#233</see> will
    /// remove from here in the same way.
    /// </remarks>
    private static void RequireEveryPlaceholderIsResolvable(
        string name, string connectionString, ConnectionStringTemplate template, bool wholeSecret)
    {
        var ports = 0;

        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case ConnectionStringTemplate.Literal:
                    break;

                case ConnectionStringTemplate.Port { Name: null }:
                    ports++;
                    break;

                case ConnectionStringTemplate.Port port:
                    throw new ServiceSourcesConfigurationException(
                        $"Backing service '{name}': the connection string carries '{port.AsWritten}', which names one "
                        + "of several forwarded ports, and forwarding more than one port is not supported yet. This "
                        + $"backing service forwards the single port '{ConfigKey(name, "Port")}' names, so write "
                        + "'${port}'.");

                // The only placeholder here whose value this source does not know: it is fetched
                // from the cluster when Aspire resolves the parameter it becomes, so nothing is
                // checked about it beyond its spelling, which the parser already did.
                case ConnectionStringTemplate.Secret:
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        // Whole-string mode is the one shape that addresses the tunnel without naming a port: the
        // secret it resolves to carries the port already, written against the cluster, and the mode
        // exists precisely because there is nothing in the template to substitute into. Requiring
        // '${port}' of it would refuse the only template it can be written as.
        if (ports == 0 && !wholeSecret)
        {
            throw NothingAddressesTheTunnel(name, connectionString);
        }
    }

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
            $"Backing service '{name}': its connection string is a single '${{secret:...}}' placeholder, so the "
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

        // The same placeholder written twice is one value, so it is one parameter. Adding it twice
        // would throw on the duplicate name — naming a resource the developer never wrote — for a
        // template that is perfectly ordinary: a connection string that names the host and a
        // failover host carries the same credential twice.
        if (builder.Resources.OfType<ParameterResource>().FirstOrDefault(p => p.Name == parameterName)
            is { } existing)
        {
            return builder.CreateResourceBuilder(existing);
        }

        try
        {
            return builder.AddParameter(
                parameterName,
                () => Fetch(name, service, context, @namespace, secret, wholeSecret, remotePort),
                secret: true);
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
                $"Backing service '{name}': the placeholder '{secret.AsWritten}' becomes a parameter named "
                + $"'{parameterName}', after the backing service, the secret and the key, and Aspire rejected that "
                + $"name — \"{WithoutParameterSuffix(ex.Message)}\" The limit is on the derived name rather than on "
                + "any one part, so a shorter backing-service name is what fixes it.",
                ex);
        }
    }

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
            throw new KubernetesSecretException($"Backing service '{name}': {ex.Message}", ex);
        }

        if (!wholeSecret)
        {
            return value;
        }

        var localised = ToLocalhost(value, service, @namespace, out var rewrites);

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
                $"Backing service '{name}': the connection string in key '{secret.Key}' of secret '{secret.Name}' "
                + $"does not address '{service}' in any form this can rewrite — '{service}', '{service}.{@namespace}', "
                + $"'.svc' or '.svc.cluster.local', after a host keyword or a URI's '@' or '//'. Nothing was "
                + $"substituted, so the value still points into the cluster and would not reach the port-forward. "
                + $"Check that '{ConfigKey(name, "Service")}' names the service the secret was written against.");
        }

        RequireSecretPortMatches(name, localised, secret, remotePort);

        return localised;
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
        string name, string localised, ConnectionStringTemplate.Secret secret, int remotePort)
    {
        var written = SecretPort().Match(localised);

        if (!written.Success || !int.TryParse(
                written.Groups["port"].ValueSpan, CultureInfo.InvariantCulture, out var port) || port == remotePort)
        {
            return;
        }

        throw new KubernetesSecretException(
            $"Backing service '{name}': the connection string in key '{secret.Key}' of secret '{secret.Name}' "
            + $"addresses port {port}, and the port-forward serves {remotePort} — the port "
            + $"'{ConfigKey(name, "Port")}' names. Nothing listens on {port} locally, and the tunnel's health check "
            + $"watches {remotePort}, so every resource would report healthy while the app reached nothing. Set "
            + $"'{ConfigKey(name, "Port")}' to {port}, or point it at a secret written for port {remotePort}.");
    }

    /// <summary>The port a localised connection string addresses, in either shape it is written.</summary>
    [GeneratedRegex(@"(?:localhost:|\bPort\s*=\s*)(?<port>\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPort();

    /// <summary>
    /// Whether a match the credential scan found is really a <c>${secret:...}</c> placeholder.
    /// </summary>
    /// <remarks>
    /// The scan looks for keywords a credential is usually written under, and <c>Password=</c> is
    /// one of them whatever follows it. What follows it here is a placeholder, which is safe to
    /// show and is the whole of what the reader needs.
    /// </remarks>
    private static bool IsSecretPlaceholder(string matched) =>
        matched.Contains("${secret:", StringComparison.OrdinalIgnoreCase);

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
        string connectionString, string service, string @namespace, out int rewrites)
    {
        var count = 0;

        var localised = Regex.Replace(
            connectionString,
            HostPrefix
                + $@"(?:{Regex.Escape(service)}(?:\.{Regex.Escape(@namespace)}(?:\.svc(?:\.cluster\.local)?)?)?)"
                + @"(?![\w.-])(?![^/@]*@)",
            match =>
            {
                count++;
                return match.Groups["prefix"].Value + "localhost";
            },
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        rewrites = count;

        return localised;
    }

    /// <summary>
    /// The places a connection string can introduce a host, captured so the rewrite can put it back.
    /// </summary>
    /// <remarks>
    /// Case-insensitively, because DNS is: a secret naming <c>Orders-PG</c> reaches the same service
    /// as one naming <c>orders-pg</c>, and leaving the first unrewritten would send credentials into
    /// the cluster's address space.
    /// <para>
    /// The trailing <c>(?![^/@]*@)</c> on the match keeps a URI's user name out of it. In
    /// <c>postgresql://orders:pw@orders:5432/db</c> the text after <c>//</c> is the user, not the
    /// host — an <c>@</c> before the next <c>/</c> is what says so — and a service whose name is
    /// also the database's user name is the ordinary Postgres shape.
    /// </para>
    /// </remarks>
    private const string HostPrefix =
        @"(?<prefix>(?:\A|[;,\s])\s*(?:Host|Hostname|Server|Data\s?Source|Addr|Address)\s*=\s*|@|//)";

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
            "the port that Service listens on inside the cluster, which is what the tunnel forwards to",
            k => k.Port is not null),
        ("context", "Context", "the kubectl context to forward through",
            k => !string.IsNullOrWhiteSpace(k.Context)),
        ("connectionString", "ConnectionString",
            "the connection string consumers receive, with '${port}' standing for the local end of the tunnel",
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
        kubernetes.Port is not null
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}The local end of the tunnel is allocated rather than "
              + "configured, so a connection string names it as '${port}' and only the cluster's own port is "
              + "written here.";

    /// <summary>
    /// The remote port, once it is known to be present.
    /// </summary>
    /// <remarks>
    /// Unlike the service side there is no catalog value to fall back to — the catalog carries no
    /// backing-service data at all, by decision — so the only question left here is the range.
    /// </remarks>
    private static int RequirePortInRange(string name, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{name}': 'kubernetes.port' is '{port}', which is not a port — a port is between "
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
            // A '${secret:...}' is not a credential — it is the developer's own placeholder, naming
            // a value that lives in the cluster and is not in this string at all. Hiding it would
            // hide the one part of the template the reader needs to see to understand why the
            // message fired, and would call their syntax a leak.
            return Credentials.Replace(
                connectionString, match => IsSecretPlaceholder(match.Value) ? match.Value : "***");
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
    /// The whole-string secret form is the one template that legitimately carries no
    /// <c>${port}</c> — it arrives already addressed, and is answered by forwarding the same port
    /// number locally rather than by substitution. Stage 3 made that reachable, so this refusal is
    /// no longer asked of every template without a <c>${port}</c>: the caller exempts whole-string
    /// mode before getting here, and what still reaches this is a template that names no port and
    /// has no secret to have carried one.
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
        string name, string connectionString)
    {
        var shown = Redacted(connectionString);

        // Only when something was actually replaced. Said unconditionally it would put "***" into
        // every one of these messages, including the ordinary case where the template carries no
        // credential at all and what is quoted is exactly what the developer wrote — leaving them
        // to wonder which part of it the package had hidden.
        var note = shown == connectionString || shown == Unscannable
            ? ""
            : " (a credential in it shown as ***)";

        return new(
            $"Backing service '{name}': source 'kubernetes' opens a kubectl port-forward on a local port allocated "
            + $"at startup, but the connection string names no '${{port}}' placeholder to put it in — so nothing "
            + $"would address the tunnel: \"{shown}\"{note}. "
            + "Replace the port in it with '${port}', as "
            + "'Host=localhost;Port=${port};Database=orders'. If you did write '${port}', a shell expanded it "
            + "away before the AppHost saw it — '${...}' is a shell variable too, and double quotes do not protect "
            + "it. Single-quote the value, and use env 'NAME=value' for a key with a hyphen in it. A backing "
            + $"service reached at a fixed address the developer already has — an ingress, or an instance they run "
            + $"themselves — is source 'direct' rather "
            + $"than this one. The key is '{ConfigKey(name, "ConnectionString")}'.");
    }
}
