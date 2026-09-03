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
/// The resource this adds is Aspire's own <c>ConnectionStringResource</c>, which is an
/// <c>IResourceWithoutLifetime</c>: there is nothing to start, so nothing to wait for.
/// <c>WaitFor</c> on one of these is therefore honoured but empty — a connectivity check that would
/// make it mean something is a deliberate omission for now, since the developer is pointing at
/// something they already run and the only thing a check buys them is a better diagnosis.
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
        // malformed. Telling a developer who wrote `{secret:orders-creds}` that secrets are
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
