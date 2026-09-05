using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The path-only questions about a service's checkout — the ones answerable from the AppHost
/// directory and the service name, without resolving anything.
/// </summary>
public class LocalGitCheckoutTests
{
    /// <summary>
    /// A directory to compose paths against, never created on disk.
    /// <see cref="LocalGitCheckout.ManagedRepoRoot"/> does no I/O, so a real temp directory here
    /// would be one more the suite leaves behind for nothing.
    /// </summary>
    private static readonly string UnusedAppHostDirectory = Path.Combine(Path.GetTempPath(), "apphost");

    /// <summary>
    /// The service name becomes a directory name under <c>.servicesources/checkouts/</c>, so one
    /// containing <c>..</c> would put the checkout outside the directory the ignore file and the
    /// build barrier are written to cover. Refused at the one function every route to that path
    /// goes through, rather than trusted to have been validated before it got here (#224).
    /// </summary>
    [Theory]
    [InlineData("../escapee")]
    [InlineData("..\\escapee")]
    [InlineData("/etc/escapee")]
    [InlineData("nested/escapee")]
    [InlineData("..")]
    [InlineData(".")]
    // Windows strips trailing dots and spaces off a path component before resolving it, so each of
    // these reaches the filesystem as ".." or "." there while naming an ordinary directory here.
    // Refused on every platform: the verdict on shared configuration cannot depend on who reads it,
    // and a Linux-only CI would never see the escape.
    [InlineData(".. ")]
    [InlineData("...")]
    [InlineData(". ")]
    [InlineData("  ..  ")]
    public void ManagedRepoRoot_ANameThatIsNotADirectoryNameOfItsOwn_IsRefused(string serviceName)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalGitCheckout.ManagedRepoRoot(UnusedAppHostDirectory, serviceName));

        Assert.Contains(serviceName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedRepoRoot_AnOrdinaryName_IsTheCheckoutDirectoryForIt()
    {
        Assert.Equal(
            Path.Combine(UnusedAppHostDirectory, ".servicesources", "checkouts", "orders"),
            LocalGitCheckout.ManagedRepoRoot(UnusedAppHostDirectory, "orders"));
    }
}
