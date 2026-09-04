using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The path-only questions about a service's checkout — the ones answerable from the AppHost
/// directory, the service name and the developer's configuration, without resolving anything.
/// Pinned here rather than only through their callers because the speculative prefetch and the
/// deferral decision are both built on the same answer, for services nobody has added yet, and the
/// prefetch's filter (#76/#177) is only safe while the two agree.
/// </summary>
public class LocalGitCheckoutTests
{
    private static ServiceDeveloperConfig Managed() =>
        new() { Source = "local" };

    private static ServiceDeveloperConfig WithPathOverride(string path) =>
        new() { Source = "local", Local = new LocalDeveloperConfig { Path = path } };

    private static string NewAppHostDirectory() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void IsManagedCheckout_NoPathOverride_IsTrue() =>
        Assert.True(LocalGitCheckout.IsManagedCheckout(Managed()));

    [Fact]
    public void IsManagedCheckout_PathOverride_IsFalse() =>
        Assert.False(LocalGitCheckout.IsManagedCheckout(WithPathOverride("/somewhere/of/their/own")));

    [Fact]
    public void IsColdManagedCheckout_NothingAtTheManagedRoot_IsCold()
    {
        var appHostDirectory = NewAppHostDirectory();

        Assert.True(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", Managed()));
    }

    [Fact]
    public void IsColdManagedCheckout_ACompleteCheckoutAtTheManagedRoot_IsNotCold()
    {
        var appHostDirectory = NewAppHostDirectory();
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", Managed()));
    }

    [Fact]
    public void IsColdManagedCheckout_DebrisAtTheManagedRoot_IsNotCold()
    {
        // A directory with no ".git" in it — an interrupted clone. Not cold either: telling that
        // apart from a real working tree, and deciding what to do about it, belongs to
        // PrepareRepoRoot rather than to anything speculating from the path alone.
        var appHostDirectory = NewAppHostDirectory();
        Directory.CreateDirectory(LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders"));

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", Managed()));
    }

    [Fact]
    public void IsColdManagedCheckout_PathOverride_IsNotCold()
    {
        // Nothing at the managed root, so the directory probe on its own would say "cold". The
        // override outranks it: that path is the developer's own directory, and this package never
        // clones into it.
        var appHostDirectory = NewAppHostDirectory();
        var theirs = Directory.CreateTempSubdirectory().FullName;

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", WithPathOverride(theirs)));
    }

    [Fact]
    public void IsColdManagedCheckout_PathOverrideThatDoesNotExist_IsStillNotCold()
    {
        // A stale override is a configuration failure for whoever actually resolves the service,
        // reported there by name. It must not read as a clone waiting to happen: speculating over
        // one used to invent a failure about a repository nobody was going to download (#76).
        var appHostDirectory = NewAppHostDirectory();
        var missing = Path.Combine(appHostDirectory, "gone");

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", WithPathOverride(missing)));
    }

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
