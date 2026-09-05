using System.Globalization;
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
        var remotePort = RequirePortInRange(name, kubernetes.Port!.Value);

        var template = ConnectionStringTemplate.Parse(connectionString, name, ConfigKey(name, "ConnectionString"));

        // Judged whole before a port is taken, for the reason the service-side source gives: a
        // template this source cannot resolve is config validation like every check above it, and
        // should not burn an allocation on its way to saying so.
        RequireEveryPlaceholderIsResolvable(name, connectionString, template);

        var localPort = portAllocator.AllocatePort();
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

                // Unreachable: the walk above accepts only literals and the unnamed port. Kept so
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
    /// the model. It also puts the two "not supported yet" branches in one place, which is where
    /// stage 3 and <see href="https://github.com/flojon/aspire-servicesources/issues/233">#233</see>
    /// will remove them from.
    /// </remarks>
    private static void RequireEveryPlaceholderIsResolvable(
        string name, string connectionString, ConnectionStringTemplate template)
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

                case ConnectionStringTemplate.Secret secret:
                    throw new ServiceSourcesConfigurationException(
                        $"Backing service '{name}': the connection string carries '{secret.AsWritten}', and reading a "
                        + "value out of a Kubernetes secret is not supported yet. Put the value in the connection "
                        + "string, or set the whole connection string from a configuration layer that already holds "
                        + $"it — user secrets, or {Environmentally(ConfigKey(name, "ConnectionString"))}.");

                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        if (ports == 0)
        {
            throw NothingAddressesTheTunnel(name, connectionString);
        }
    }

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
        string name, string connectionString)
    {
        var shown = ConnectionStringRedaction.Apply(connectionString);

        // Only when something was actually replaced. Said unconditionally it would put "***" into
        // every one of these messages, including the ordinary case where nothing in the template
        // needed hiding and what is quoted is exactly what the developer wrote — leaving them to
        // wonder which part of it the package had hidden.
        var note = shown == connectionString || shown == ConnectionStringRedaction.Unscannable
            ? ""
            : " (a value is shown only under a key known to hold no secret; the rest read as ***, which does not mean they were secret)";

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
