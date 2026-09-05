using Aspire.Hosting.ServiceSources.Kubernetes;

namespace Aspire.Hosting.ServiceSources.Tests.Kubernetes;

/// <summary>
/// The command line one secret fetch runs.
/// </summary>
/// <remarks>
/// Only the arguments, for the reason <c>KubectlPortForward</c>'s tests give: the fetch itself needs
/// a cluster, and what this package decides is the command — everything after that is kubectl's.
/// <para>
/// Both assertions here stand for a correctness decision the reader's own remarks argue for, and
/// neither was pinned by anything before.
/// </para>
/// </remarks>
public class KubectlSecretReaderTests
{
    /// <summary>
    /// The key is addressed with brackets, not with a dot.
    /// </summary>
    /// <remarks>
    /// <c>{.data.ca.crt}</c> descends into a field <c>ca</c> that does not exist and prints nothing,
    /// while exiting 0 — so a key containing a dot would read as a key that is not there. Kubernetes
    /// allows the dot, and <c>.dockerconfigjson</c> is the API's own key for a pull secret, so the
    /// dotted form would be wrong for exactly the keys the bracket form exists to serve.
    /// </remarks>
    [Theory]
    [InlineData("password", "jsonpath={.data['password']}")]
    [InlineData("ca.crt", "jsonpath={.data['ca.crt']}")]
    [InlineData(".dockerconfigjson", "jsonpath={.data['.dockerconfigjson']}")]
    public void Args_AddressTheKeyWithBrackets(string key, string expected) =>
        Assert.Equal(expected, KubectlSecretReader.Args("dev-west", "orders", "orders-creds", key)[^1]);

    /// <summary>
    /// The secret's name is separated from the options by <c>--</c>.
    /// </summary>
    /// <remarks>
    /// kubectl takes options wherever they appear, so a name that looked like one would be read as
    /// one. <c>ConnectionStringTemplate</c> already refuses such a name; this is the second lock, and
    /// it is what makes the reader safe to read in isolation.
    /// </remarks>
    [Fact]
    public void Args_EndOptionParsingBeforeTheSecretName()
    {
        var args = KubectlSecretReader.Args("dev-west", "orders", "orders-creds", "password");

        Assert.Equal("--", args[Array.IndexOf(args, "orders-creds") - 1]);
    }

    /// <summary>
    /// The context and the namespace the entry configures are both passed, never left to kubectl's
    /// current ones.
    /// </summary>
    [Fact]
    public void Args_NameTheConfiguredContextAndNamespace()
    {
        var args = KubectlSecretReader.Args("dev-west", "orders", "orders-creds", "password");

        Assert.Equal("dev-west", args[Array.IndexOf(args, "--context") + 1]);
        Assert.Equal("orders", args[Array.IndexOf(args, "--namespace") + 1]);
    }
}
