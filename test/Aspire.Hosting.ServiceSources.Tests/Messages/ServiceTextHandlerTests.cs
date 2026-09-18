using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class ServiceTextHandlerTests
{
    private static string Compose(ServiceTextHandler text) => Raw.Compose(text).ToString();

    [Fact]
    public void LiteralSegments_AreVerbatim() =>
        Assert.Equal("Service is not configured.", Compose($"Service is not configured."));

    [Fact]
    public void NameHole_IsEscaped() =>
        Assert.Equal("Service 'ord\\'ers' failed.", Compose($"Service '{new Name("ord'ers")}' failed."));

    [Fact]
    public void NameHole_IsCapped()
    {
        var composed = Compose($"'{new Name(new string('a', 200))}'");

        Assert.Equal(Name.MaxLength + 3, composed.Length);
        Assert.EndsWith("…'", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void RawHole_IsNotEscapedAgain() =>
        Assert.Equal("outer 'ord\\'ers' tail", Compose($"outer {Raw.Compose($"'{new Name("ord'ers")}'")} tail"));

    [Fact]
    public void RawHole_IsNotCapped()
    {
        var longPath = Raw.Literal("/a/very/long/path/that/comfortably/exceeds/the/sixty/four/character/name/cap/orders.csproj");

        Assert.Contains("orders.csproj", Compose($"project {longPath}"), StringComparison.Ordinal);
    }

    [Fact]
    public void IntHole_IsRendered() => Assert.Equal("port 8080", Compose($"port {8080}"));

    [Fact]
    public void NullableIntHole_IsRendered() => Assert.Equal("port 8080", Compose($"port {(int?)8080}"));

    [Fact]
    public void DefaultRaw_RendersEmptyRatherThanThrowing() =>
        Assert.Equal("ab", Compose($"a{default(Raw)}b"));

    [Fact]
    public void MultipleNameHoles_AreEachEscapedIndependently() =>
        Assert.Equal("'a\\'1' and 'b\\'2'",
            Compose($"'{new Name("a'1")}' and '{new Name("b'2")}'"));
}
