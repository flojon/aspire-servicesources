namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Asks another source for its requirements from inside a place that must not fail because of it.
/// </summary>
internal static class SourceProbe
{
    /// <summary>
    /// <paramref name="source"/>'s requirements, or <see cref="SourceRequirements.Unknown"/> when asking
    /// throws. The probe runs inside another error message's construction and inside the picker's offer
    /// list, so an unrelated exception from one source's check must not replace the diagnostic the
    /// developer was about to read: "could not tell" is "not offered".
    /// </summary>
    public static SourceRequirements Evaluate(IServiceSource source, SourceInput input)
    {
        try
        {
            return source.Requirements(input);
        }
        catch (Exception)
        {
            return SourceRequirements.Unknown;
        }
    }
}
