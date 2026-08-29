namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceSourcesConfigurationExceptionTests
{
    /// <summary>
    /// The summary drops every stack frame, so it has to say where the dropped detail went.
    /// </summary>
    private const string FullDetailHint =
        "  (set SERVICESOURCES_FULL_ERRORS=1 for the full exception detail, including stack traces)";

    [Fact]
    public void ToString_NoInnerException_IsTheMessageAlone()
    {
        var exception = new ServiceSourcesConfigurationException("Service 'orders': project file was not found.");

        Assert.Equal("Service 'orders': project file was not found.", exception.ToString());
    }

    [Fact]
    public void ToString_InnerExceptions_ListEachCauseOnItsOwnLine()
    {
        var exception = new ServiceSourcesConfigurationException(
            "Service 'orders': failed to clone repository 'https://github.com/company/orders'.",
            new InvalidOperationException(
                "authentication failed",
                new InvalidOperationException("could not find appropriate mechanism for credentials")));

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Service 'orders': failed to clone repository 'https://github.com/company/orders'.",
                "  caused by: authentication failed",
                "  caused by: could not find appropriate mechanism for credentials",
                FullDetailHint),
            exception.ToString());
    }

    [Fact]
    public void ToString_ACauseRepeatingTheMessageItWraps_IsListedOnce()
    {
        // GitAuthenticationFailedException rewraps libgit2's exception under libgit2's own message,
        // so this chain is what a real authentication failure actually produces.
        var exception = new ServiceSourcesConfigurationException(
            "Service 'orders': failed to clone.",
            new InvalidOperationException(
                "could not find appropriate mechanism for credentials",
                new InvalidOperationException("could not find appropriate mechanism for credentials")));

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Service 'orders': failed to clone.",
                "  caused by: could not find appropriate mechanism for credentials",
                FullDetailHint),
            exception.ToString());
    }

    [Fact]
    public void ToString_TheSameMessageRecurringAfterADifferentCause_IsKept()
    {
        var exception = new ServiceSourcesConfigurationException(
            "Service 'orders': failed to clone.",
            new InvalidOperationException(
                "connection reset",
                new InvalidOperationException(
                    "retrying",
                    new InvalidOperationException("connection reset"))));

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Service 'orders': failed to clone.",
                "  caused by: connection reset",
                "  caused by: retrying",
                "  caused by: connection reset",
                FullDetailHint),
            exception.ToString());
    }

    [Fact]
    public void ToString_ThrownWithAnInnerException_CarriesNoStackTraces()
    {
        // The runtime prints ToString() when this reaches Main unhandled, so anything it returns is
        // what the developer sees instead of the remediation the message exists to deliver.
        var exception = Capture(new InvalidOperationException("authentication failed"));

        Assert.DoesNotContain("   at ", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("End of inner exception stack trace", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_FullDetail_KeepsTheStackTracesForDiagnosingTheLibraryItself()
    {
        var exception = Capture(new InvalidOperationException("authentication failed"));

        Assert.Contains("   at ", exception.Describe(fullDetail: true), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FullDetailRequested_ReadsTheEnvironmentVariable(string? value, bool expected) =>
        Assert.Equal(expected, ServiceSourcesConfigurationException.FullDetailRequested(value));

    [Fact]
    public void ToString_AWrappedCause_SaysHowToGetTheDroppedDetailBack()
    {
        // Several call sites wrap a bare `catch (Exception)`, where the message says only that a
        // clone failed and the trace this rendering drops is the entire diagnosis. A developer who
        // is never told the fuller rendering exists cannot produce it, or ask for it in a bug report.
        var exception = new ServiceSourcesConfigurationException(
            "Service 'orders': failed to clone.",
            new IOException("The process cannot access the file because it is being used by another process."));

        Assert.EndsWith(FullDetailHint, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_NoInnerException_OmitsTheFullDetailHint()
    {
        // Nothing was wrapped, so the full rendering has nothing to add beyond this package's own
        // frames — pointing at it would only add noise to a message that is already complete.
        var exception = new ServiceSourcesConfigurationException("Service 'orders': project file was not found.");

        Assert.DoesNotContain("SERVICESOURCES_FULL_ERRORS", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Throws and catches, so the exception carries a real stack trace — an unthrown exception has
    /// none, which would let a ToString that prints them pass anyway.
    /// </summary>
    private static ServiceSourcesConfigurationException Capture(Exception innerException)
    {
        try
        {
            throw new ServiceSourcesConfigurationException("Service 'orders': failed to clone.", innerException);
        }
        catch (ServiceSourcesConfigurationException caught)
        {
            return caught;
        }
    }
}
