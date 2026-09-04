using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// Connects to a backing service the developer already runs, at an address they supply — no tunnel
/// in the way, and no process for Aspire to manage.
/// </summary>
/// <remarks>
/// "Direct" is about that absence of a tunnel rather than about location. A Postgres started by
/// hand on <c>localhost</c> and a cluster database published through an ingress are one case from
/// the AppHost's side: here is a connection string, connect to it. That is why the key is not
/// <c>"remote"</c> — the common use is emphatically local — and not <c>"external"</c>, which Aspire
/// already uses for an external HTTP service.
/// <para>
/// The resource this adds is Aspire's own <c>ConnectionStringResource</c>. There is nothing for
/// Aspire to start behind it, so a <c>WaitFor</c> on one has nothing whose readiness it could track
/// — a connectivity check that would make it mean something is a deliberate omission for now, since
/// the developer is pointing at something they already run and the only thing a check buys them is a
/// better diagnosis.
/// </para>
/// <para>
/// <b>A consumer's <c>WaitFor</c> on one of these hangs today (#220).</b> This used to say the type
/// is an <c>IResourceWithoutLifetime</c> and that a wait on it was "honoured but empty". It is not
/// that type: on Aspire 13.5.2 it declares <c>IResourceWithConnectionString</c> and
/// <c>IResourceWithWaitSupport</c> and no lifetime marker at all, read off the loaded assembly. The
/// missing marker is the whole difference — it is what
/// <see cref="Sources.ServiceUrlResource"/> declares to keep a wait on <i>it</i> from hanging
/// (#170) — so with wait support present and the marker absent the wait is accepted and never
/// resolves. Measured through <c>ResourceNotificationService.WaitForDependenciesAsync</c>, the same
/// route the orchestrator takes, and pinned by <c>BackingServiceWaitTests</c>: the consumer stays in
/// <c>Waiting</c> with "orders-db: Unable to retrieve current state".
/// </para>
/// </remarks>
internal sealed class DirectBackingServiceSource : IBackingServiceSource
{
    public IResourceBuilder<IResourceWithConnectionString> Resolve(
        IDistributedApplicationBuilder builder,
        string name,
        BackingServiceDeveloperConfig config)
    {
        var configKey = $"{DeveloperConfiguration.BackingServicesKey}:{name}:Direct:ConnectionString";

        if (string.IsNullOrWhiteSpace(config.Direct.ConnectionString))
        {
            throw new ServiceSourcesConfigurationException(
                $"Backing service '{name}': source 'direct' requires 'direct.connectionString' — it is the whole "
                + $"of what this source supplies. Add \"{name}\": {{ \"source\": \"direct\", \"direct\": "
                + "{ \"connectionString\": \"...\" } } under \"backingServices\" in "
                + $"'{DeveloperConfiguration.FileName}', or set "
                + $"{configKey.Replace(":", "__", StringComparison.Ordinal)}.");
        }

        var template = ConnectionStringTemplate.Parse(config.Direct.ConnectionString, name, configKey);

        // Parsed before this check rather than after, so that a malformed placeholder is reported as
        // malformed. Telling a developer who wrote `${secret:orders-creds}` that secrets are
        // unsupported would send them to work around a limit while their real mistake — the missing
        // key — went unmentioned.
        var text = new System.Text.StringBuilder();

        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case ConnectionStringTemplate.Literal literal:
                    text.Append(literal.Text);
                    break;

                // Neither message explains that the text cannot be kept as written, which both used
                // to. Under the brace syntax a value that was never meant as a placeholder could
                // land here — `PWD={secret}` is ODBC for a password that happens to be the word —
                // so the reader had to be told no spelling would help. Placeholders open on `${`
                // now, which no connection-string dialect uses, so anything arriving here was
                // written as a placeholder and the paragraph would answer a question nobody asked.
                case ConnectionStringTemplate.Port port:
                    throw new ServiceSourcesConfigurationException(
                        $"Backing service '{name}': the connection string carries '{port.AsWritten}', but source "
                        + "'direct' forwards nothing, so there is no local port to substitute. Write the port the "
                        + $"backing service already listens on. The key is '{configKey}'.");

                case ConnectionStringTemplate.Secret secret:
                    throw new ServiceSourcesConfigurationException(
                        $"Backing service '{name}': the connection string carries '{secret.AsWritten}', and reading "
                        + "a value out of a Kubernetes secret is not supported yet. Put the value in the connection "
                        + "string, or set the whole connection string from a configuration layer that already holds "
                        + $"it — user secrets, or {configKey.Replace(":", "__", StringComparison.Ordinal)}.");

                default:
                    throw new InvalidOperationException($"Unhandled template segment '{segment.GetType().Name}'.");
            }
        }

        // AddConnectionString's expression overload rather than its name overload, which means
        // something else entirely: that one declares a connection string the *developer* supplies
        // through configuration, which is the job this source is already doing.
        var expression = new ReferenceExpressionBuilder();
        ConnectionStringTemplate.AppendLiteral(expression, text.ToString());

        return builder.AddConnectionString(name, expression.Build());
    }
}
