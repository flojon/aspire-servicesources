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
/// Aspire to start behind it, so a consumer's <c>WaitFor</c> is honoured but empty: the orchestrator
/// publishes <c>Running</c> for the resource as soon as its connection string is available, which is
/// immediately, so the wait is satisfied without anything having been waited for. A connectivity
/// check that would make it mean something is a deliberate omission for now, since the developer is
/// pointing at something they already run and the only thing a check buys them is a better
/// diagnosis (#220).
/// </para>
/// <para>
/// Two details of that are easy to get wrong in opposite directions, so both are measured. The type
/// carries <c>IResourceWithConnectionString</c> and <c>IResourceWithWaitSupport</c> and <i>no</i>
/// <c>IResourceWithoutLifetime</c>, read off the loaded assembly on Aspire 13.5.2 — so unlike
/// <see cref="Sources.ServiceUrlResource"/>, which declares that marker deliberately (#170), the
/// wait here is honoured rather than dropped, and <c>BackingServiceWaitTests</c> pins that. But
/// honoured is not never-resolving: against a live host the consumer leaves <c>Waiting</c> in about
/// a second. Reasoning from the marker alone says "hang", and that is wrong.
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
