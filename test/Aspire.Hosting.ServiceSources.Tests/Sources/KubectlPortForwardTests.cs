using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// The <c>kubectl port-forward</c> command line, which is the one part of either tunnelling source
/// no test can check by observing behaviour — nothing here runs <c>kubectl</c>.
/// </summary>
public class KubectlPortForwardTests
{
    /// <summary>
    /// The single-pair spelling, asserted verbatim, because it is what the service-side source and
    /// a single-port backing service both emit and neither should change.
    /// </summary>
    [Fact]
    public void OnePair_IsTheCommandLineItHasAlwaysBeen() =>
        Assert.Equal(
            ["port-forward", "svc/orders-pg", "54321:5432", "--context", "dev-west", "--namespace", "default"],
            KubectlPortForward.Args("orders-pg", 54321, 5432, "dev-west", @namespace: null));

    /// <remarks>
    /// One invocation carries every pair, in the order it was given, with a single <c>--context</c>
    /// and <c>--namespace</c> after them — the shape <c>kubectl</c> accepts.
    /// </remarks>
    [Fact]
    public void SeveralPairs_GoOnOneCommandLineInTheOrderGiven() =>
        Assert.Equal(
            [
                "port-forward", "svc/rabbitmq", "54321:5672", "54322:15672",
                "--context", "dev-west", "--namespace", "brokers",
            ],
            KubectlPortForward.Args("rabbitmq", [(54321, 5672), (54322, 15672)], "dev-west", "brokers"));

    /// <summary>
    /// The single-pair spelling is the pairs one with a list of length one, rather than a second
    /// implementation of the same array.
    /// </summary>
    /// <remarks>
    /// The reason this type exists at all: two arrays written apart drift, and a test asserting each
    /// separately would not notice. Asserted as an equality between the two rather than as two
    /// expectations, so it keeps holding whatever the array becomes.
    /// </remarks>
    [Fact]
    public void TheSinglePairSpelling_IsThePairsOneWithOnePair() =>
        Assert.Equal(
            KubectlPortForward.Args("orders-pg", [(54321, 5432)], "dev-west", "orders"),
            KubectlPortForward.Args("orders-pg", 54321, 5432, "dev-west", "orders"));

    [Fact]
    public void NoNamespace_FallsBackToTheDefaultThisPackageChooses() =>
        Assert.Contains(
            KubectlPortForward.DefaultNamespace,
            KubectlPortForward.Args("orders-pg", [(1, 2)], "dev-west", @namespace: null));
}
