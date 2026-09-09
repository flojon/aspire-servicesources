using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// The <c>WithPrepare(...)</c> mode-validation and <see cref="PrepareMetadata"/> construction shared
/// by <see cref="ServiceDefinitionBuilder.WithPrepare"/> and <see cref="RepositoryBuilder.WithPrepare"/>
/// — one bootstrap-command block, declarable on either builder (design "prepare moves to the
/// repository"), so the validation that decides whether <c>mode</c> names one of the four
/// <see cref="PrepareMode"/> values lives once rather than twice.
/// </summary>
internal static class PrepareMetadataFactory
{
    private const string MethodName = "WithPrepare";

    /// <param name="label">
    /// Already formatted — <c>"Service 'orders'"</c> or <c>"Repository 'monorepo'"</c> — so this stays
    /// agnostic to which builder called it.
    /// </param>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="mode"/> is not one of the four <see cref="PrepareMode"/> values.
    /// </exception>
    public static PrepareMetadata Create(
        string label, string[] command, string[]? windowsCommand, PrepareMode mode)
    {
        // Before PrepareModes.Written, which is a lookup over the defined members and total only
        // over those.
        if (!Enum.IsDefined(mode))
        {
            throw new ServiceSourcesConfigurationException(
                $"{label}: {MethodName} was given mode '{(int)mode}', which is not a "
                + $"{nameof(PrepareMode)}. Set it to one of "
                + string.Join(", ", Enum.GetValues<PrepareMode>().Select(m => $"{nameof(PrepareMode)}.{m}"))
                + " — the four the yaml block spells "
                + string.Join(", ", Enum.GetValues<PrepareMode>().Select(m => $"'{PrepareModes.Written(m)}'"))
                + ".");
        }

        // Stored as the spelling the yaml block uses, so that PrepareMetadata.Mode carries one
        // representation whichever file or builder the block came from and PreparePlan parses it in
        // one place.
        return new PrepareMetadata
        {
            Command = command,
            WindowsCommand = windowsCommand,
            Mode = PrepareModes.Written(mode),
        };
    }
}
