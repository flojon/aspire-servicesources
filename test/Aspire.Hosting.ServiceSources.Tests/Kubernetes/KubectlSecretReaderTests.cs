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
    public void Args_AddressTheKeyWithBrackets(string key, string expected)
    {
        var args = KubectlSecretReader.Args("dev-west", "orders", "orders-creds", key);

        Assert.Equal(expected, args[Array.IndexOf(args, "--output") + 1]);
    }

    /// <summary>
    /// <c>--</c> comes after every option, and nothing follows it but the secret's name.
    /// </summary>
    /// <remarks>
    /// <b>The position is the whole of it.</b> kubectl uses pflag, where a bare <c>--</c> ends
    /// option parsing for <em>everything</em> after it rather than escaping one argument. Put ahead
    /// of the flags it hands <c>--context</c> and its value to kubectl as secret names and silently
    /// drops the context, the namespace and the output format — so the fetch runs against whatever
    /// cluster the developer's kubeconfig happens to point at, which is the opposite of what naming
    /// a context is for. Asserted as "last two, in this order" rather than "somewhere before the
    /// name", because the weaker shape held while the behaviour was wrong.
    /// </remarks>
    [Fact]
    public void Args_EndOptionParsingOnlyAfterEveryOption()
    {
        var args = KubectlSecretReader.Args("dev-west", "orders", "orders-creds", "password");

        Assert.Equal(["--", "orders-creds"], args[^2..]);
        Assert.All(
            new[] { "--context", "--namespace", "--output" },
            option => Assert.True(
                Array.IndexOf(args, option) < Array.IndexOf(args, "--"),
                $"'{option}' must be parsed, so it has to come before '--'."));
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
