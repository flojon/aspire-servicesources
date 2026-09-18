using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class MigratedSiteEscapingTests
{
    private const string Forgery = "orders'\nFATAL: everything is fine";

    [Fact]
    public void For_EscapesANameHole()
    {
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name(Forgery)}' failed.");

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("' failed.\nFATAL", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("Service 'orders\\'\\n", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_CarriesAnInnerException()
    {
        var inner = new InvalidOperationException("underneath");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}'.", inner);

        Assert.Same(inner, exception.InnerException);
        Assert.Equal("Service 'orders'.", exception.Message);
    }

    [Fact]
    public void For_AcceptsAMessageWithNoHoles() =>
        Assert.Equal("nothing interpolated here",
            ServiceSourcesConfigurationException.For($"nothing interpolated here").Message);

    [Fact]
    public void Describe_CannotForgeACausedByLineFromAnInnerMessage()
    {
        // Environment.NewLine, not "\n": Describe writes the platform separator, so a payload hard-coding
        // "\n" would already be harmless on Windows and the test would pass before the fix.
        var forged = $"authentication failed{Environment.NewLine}  caused by: nothing is wrong, carry on";
        var exception = ServiceSourcesConfigurationException.For(
            $"Service '{new Name("orders")}' failed.", new InvalidOperationException(forged));

        var described = exception.Describe(fullDetail: false);

        // The forged text may survive as characters; what it must not do is start a line. Splitting on
        // the separator + prefix counts real cause lines only, so exactly one wrapped cause means two parts.
        Assert.Equal(2, described.Split(Environment.NewLine + "  caused by: ").Length);
        Assert.Contains("\\n  caused by: nothing is wrong", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_KeepsTheCausesWordingIntactApartFromLineBreaks()
    {
        var inner = new InvalidOperationException("could not read 'origin/main'");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}' failed.", inner);

        Assert.Contains("  caused by: could not read 'origin/main'",
            exception.Describe(fullDetail: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DoesNotCapALongCause()
    {
        var inner = new InvalidOperationException(new string('x', 400));
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}' failed.", inner);

        Assert.Contains(new string('x', 400), exception.Describe(fullDetail: false), StringComparison.Ordinal);
    }

    // Fully qualified deliberately: the test assembly already has an
    // Aspire.Hosting.ServiceSources.Tests.Prepare namespace, so the simple name `Prepare` binds
    // there and lookup stops — `Prepare.PreparePlan` is CS0234, not the production type.
    private static string ServiceLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.ServiceLabel(name);

    private static string RepositoryLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.RepositoryLabel(name);

    [Fact]
    public void ServiceLabel_EscapesTheName() =>
        Assert.Equal("Service 'ord\\'ers\\nFATAL'", ServiceLabel("ord'ers\nFATAL"));

    [Fact]
    public void ServiceLabel_CapsTheName() =>
        Assert.Equal($"Service '{new string('a', Name.MaxLength)}…'", ServiceLabel(new string('a', 200)));

    [Fact]
    public void RepositoryLabel_EscapesAndCaps()
    {
        Assert.Equal("Repository 'mono\\'repo'", RepositoryLabel("mono'repo"));
        Assert.EndsWith("…'", RepositoryLabel(new string('b', 200)), StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceLabel_EscapesExactlyOnce()
    {
        // The four callers embed this result in a further message that this PR does not migrate, so
        // the guard is that one pass has already happened — not that a second pass is safe. There is
        // deliberately no way to feed a string back into the seam, which is why this asserts directly.
        Assert.Equal("Service 'ord\\'ers'", ServiceLabel("ord'ers"));
        Assert.DoesNotContain("\\\\'", ServiceLabel("ord'ers"), StringComparison.Ordinal);
    }
}
