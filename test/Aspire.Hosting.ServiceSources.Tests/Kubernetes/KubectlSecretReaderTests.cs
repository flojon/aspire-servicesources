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
    /// Every <c>.</c> in the key is escaped, and the key is not wrapped in brackets.
    /// </summary>
    /// <remarks>
    /// <b>The bracket form reads as the exact one and is not.</b> kubectl's jsonpath returns empty
    /// for <c>{.data['ca.crt']}</c> while exiting 0, so a key containing a dot reads as a key that
    /// is not there — and dotted keys are the ones this has to serve, since
    /// <c>.dockerconfigjson</c> is the API's own key for a pull secret. Measured against kubectl
    /// v1.24.3:
    /// <code>
    /// -o "jsonpath={.data['ca.crt']}"   # prints nothing, exit 0
    /// -o 'jsonpath={.data.ca\.crt}'     # prints Q0VSVA==
    /// </code>
    /// <para>
    /// These expectations are the exact strings that were run against that binary, so the assertion
    /// is a record of a measurement rather than a restatement of the code. Nothing in a unit test
    /// can run kubectl — that is what let the bracket form ship — so the measurement is written
    /// down here instead.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("password", @"jsonpath={.data.password}")]
    [InlineData("DB_PASSWORD", @"jsonpath={.data.DB_PASSWORD}")]
    [InlineData("redis-password", @"jsonpath={.data.redis-password}")]
    [InlineData("ca.crt", @"jsonpath={.data.ca\.crt}")]
    [InlineData("tls.key", @"jsonpath={.data.tls\.key}")]
    [InlineData(".dockerconfigjson", @"jsonpath={.data.\.dockerconfigjson}")]
    public void Args_EscapeEveryDotInTheKeyAndUseNoBrackets(string key, string expected)
    {
        var args = KubectlSecretReader.Args("dev-west", "orders", "orders-creds", key);
        var written = args[Array.IndexOf(args, "--output") + 1];

        Assert.Equal(expected, written);
        Assert.DoesNotContain('[', written);
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
