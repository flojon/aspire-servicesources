namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The shared body of <see cref="IServiceSource.CanResolve"/>: run the source's own resolution
/// validation and report whether it passed.
/// </summary>
internal static class SourceProbe
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="validate"/> returns, <see langword="false"/> when it
    /// throws anything. Not just <see cref="ServiceSourcesConfigurationException"/>: the probe runs inside
    /// another error message's construction, so an unrelated exception from a source's validation must
    /// not replace the diagnostic the developer was about to read. "Could not tell" is "not offered".
    /// </summary>
    public static bool Resolves(Action validate)
    {
        try
        {
            validate();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
