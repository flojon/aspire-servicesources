using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The path-only questions about a service's checkout — the ones answerable from the AppHost
/// directory and the service name, without resolving anything.
/// </summary>
public class LocalGitCheckoutTests
{
    private static string NewAppHostDirectory() => Directory.CreateTempSubdirectory().FullName;

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
    public void ManagedRepoRoot_ANameThatIsNotADirectoryNameOfItsOwn_IsRefused(string serviceName)
    {
        var appHostDirectory = NewAppHostDirectory();

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalGitCheckout.ManagedRepoRoot(appHostDirectory, serviceName));

        Assert.Contains(serviceName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedRepoRoot_AnOrdinaryName_IsTheCheckoutDirectoryForIt()
    {
        var appHostDirectory = NewAppHostDirectory();

        Assert.Equal(
            Path.Combine(appHostDirectory, ".servicesources", "checkouts", "orders"),
            LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders"));
    }
}
