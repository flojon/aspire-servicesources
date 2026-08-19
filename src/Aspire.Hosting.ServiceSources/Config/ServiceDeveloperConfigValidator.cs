namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceDeveloperConfigValidator
{
    private static readonly Dictionary<string, string[]> RelevantFieldsBySource = new()
    {
        ["local"] = ["path", "ref"],
        ["kubernetes"] = ["context", "namespace", "port"],
        ["container"] = ["tag"],
        ["url"] = ["url"],
    };

    /// <summary>
    /// Fails fast if <paramref name="config"/> sets a field that the given <paramref name="source"/>
    /// does not use — e.g. <c>port</c> under a <c>local</c> source — instead of silently ignoring it,
    /// which would let a developer typo or leftover field from switching sources go unnoticed.
    /// </summary>
    public static void Validate(string serviceName, string source, ServiceDeveloperConfig config)
    {
        if (!RelevantFieldsBySource.TryGetValue(source, out var relevantFields))
        {
            return;
        }

        var foreignFields = new List<string>();

        void CheckField(string? value, string fieldName)
        {
            if (value is not null && !relevantFields.Contains(fieldName))
            {
                foreignFields.Add(fieldName);
            }
        }

        CheckField(config.Path, "path");
        CheckField(config.Ref, "ref");
        CheckField(config.Context, "context");
        CheckField(config.Namespace, "namespace");
        CheckField(config.Port?.ToString(), "port");
        CheckField(config.Url, "url");
        CheckField(config.Tag, "tag");

        if (foreignFields.Count > 0)
        {
            var fieldList = string.Join(", ", foreignFields.Select(f => $"'{f}'"));
            var isAre = foreignFields.Count > 1 ? "are" : "is";
            var themIt = foreignFields.Count > 1 ? "them" : "it";
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': {fieldList} {isAre} not valid for source '{source}' — remove {themIt} from servicesources.local.json.");
        }
    }
}
