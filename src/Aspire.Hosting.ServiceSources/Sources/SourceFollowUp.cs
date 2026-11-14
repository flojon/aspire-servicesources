using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>One extra answer the interactive picker asks for; <paramref name="Key"/> is the config path under the service entry.</summary>
internal sealed record FollowUpField(string Key, string Label, bool Required, InputType InputType);

/// <summary>
/// The extra answers a chosen source needs beyond its name.
/// </summary>
internal static class SourceFollowUp
{
    // Mirrors what throws at Resolve time; optional ref/tag are left out since the common answer needs none.
    public static IReadOnlyList<FollowUpField> FieldsFor(string source, ServiceDefinition definition)
    {
        List<FollowUpField> fields = [];

        switch (source)
        {
            case "kubernetes":
                fields.Add(new("kubernetes:context", "Kubernetes context", true, InputType.Text));
                if (definition.Kubernetes?.Port is null)
                {
                    fields.Add(new("kubernetes:port", "Kubernetes service port", true, InputType.Number));
                }

                fields.Add(new("kubernetes:namespace", "Kubernetes namespace (optional)", false, InputType.Text));
                fields.Add(new("kubernetes:scheme", "Endpoint scheme (optional)", false, InputType.Text));
                break;
            case "path":
                if (string.IsNullOrWhiteSpace(definition.Path))
                {
                    fields.Add(new("path:path", "Local directory", true, InputType.Text));
                }

                break;
            case "url":
                if (string.IsNullOrWhiteSpace(definition.Url?.Url))
                {
                    fields.Add(new("url:url", "Service URL", true, InputType.Text));
                }

                break;
        }

        return fields;
    }
}
