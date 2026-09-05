using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// What is said about a backing service's <c>kubernetes.port</c>, which is the first developer-config
/// field that takes either a value or a block of named values.
/// </summary>
/// <remarks>
/// Its own file rather than more cases in <c>DeveloperConfigValidatorTests</c>, because the subject
/// is one field's fourth shape rather than the walk over an entry, and because every case here has
/// to go through <c>AddBackingService</c> — the validator runs on the backing-services section as
/// that call reads it.
/// <para>
/// Two of these assert on a silence rather than on a message, and they are the load-bearing ones:
/// the binder drops a named entry it cannot convert and throws on a value it cannot convert, so
/// what is being pinned is that neither ever reaches it.
/// </para>
/// </remarks>
public class PortBlockValidationTests
{
    private static string AppHostDirectory(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        return dir;
    }

    /// <summary>An entry whose only interesting part is how <c>port</c> is written.</summary>
    private static string Entry(string port, string template = "Host=localhost;Port=${port};Database=orders") =>
        $$"""
        { "backingServices": { "orders-db": {
            "source": "kubernetes",
            "kubernetes": {
              "service": "orders-pg",
              "port": {{port}},
              "context": "dev-west",
              "connectionString": "{{template}}" } } } }
        """;

    private static ServiceSourcesConfigurationException Refused(string port)
    {
        var builder = TestHelpers.CreateBuilder(AppHostDirectory(Entry(port)));

        return Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(
                "orders-db",
                () => builder.AddConnectionString("orders-db")));
    }

    /// <summary>
    /// The same entry, but with <c>port</c> supplied by a configuration layer that can express a
    /// JSON <c>null</c> — which <c>servicesources.local.json</c> cannot; see
    /// <see cref="ANullNamedPort_FromTheFile_LeavesTheFieldMissingInstead"/>.
    /// </summary>
    private static ServiceSourcesConfigurationException RefusedFromALayer(
        params (string Key, string? Value)[] port)
    {
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ServiceSources:BackingServices:orders-db:source"] = "kubernetes",
                ["ServiceSources:BackingServices:orders-db:kubernetes:service"] = "orders-pg",
                ["ServiceSources:BackingServices:orders-db:kubernetes:context"] = "dev-west",
                ["ServiceSources:BackingServices:orders-db:kubernetes:connectionString"] =
                    "Host=localhost;Port=${port};Database=orders",
            }
            .Concat(port.Select(p => new KeyValuePair<string, string?>(
                $"ServiceSources:BackingServices:orders-db:kubernetes:port:{p.Key}", p.Value)))
            .ToDictionary(entry => entry.Key, entry => entry.Value));

        return Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService(
                "orders-db",
                () => builder.AddConnectionString("orders-db")));
    }

    private static void Accepted(string port, string? template = null)
    {
        var builder = TestHelpers.CreateBuilder(
            AppHostDirectory(template is null ? Entry(port) : Entry(port, template)));

        builder.AddBackingService("orders-db", () => builder.AddConnectionString("orders-db"));
    }

    [Fact]
    public void AValueThatIsNotANumber_SaysTheFieldTakesANumberOrANamedBlock()
    {
        var ex = Refused("\"abc\"");

        Assert.Contains(
            "'port' in the 'kubernetes' block takes a port number or a block of named ports, "
            + "but is set to 'abc'.",
            ex.Message,
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The message every other whitespace value gets. Whitespace is not the empty spelling that
    /// unsets a field, and telling this reader about port blocks would answer a question they did
    /// not ask.
    /// </remarks>
    [Fact]
    public void AWhitespaceValue_GetsTheOrdinaryBlankMessage()
    {
        var ex = Refused("\"  \"");

        Assert.Contains("whitespace rather than a value", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty block, and a JSON <c>null</c>, are reported as the field being <em>missing</em> when
    /// they are written in <c>servicesources.local.json</c>.
    /// </summary>
    /// <remarks>
    /// Not the "empty block of named ports" message, and the reason is one layer down:
    /// <c>DeveloperConfigFileSource</c> re-roots the file into configuration and drops every
    /// null-valued key on the way, because that is also what an intermediate node looks like. The
    /// JSON parser records both <c>{}</c> and <c>null</c> as exactly that, so neither survives to be
    /// walked — <c>port</c> is simply not there, and the source says so by name.
    /// <para>
    /// Which is the right message anyway: a block nobody put a port in and a field nobody wrote are
    /// the same mistake from the developer's side, and the answer to both is to write a port. The
    /// empty-block message still exists for the layers that <em>can</em> carry an empty section —
    /// appsettings and user secrets are read directly rather than re-rooted.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public void AnEmptyBlockOrANullInTheFile_IsReportedAsTheFieldBeingMissing(string port)
    {
        var ex = Refused(port);

        Assert.Contains("requires 'kubernetes.port'", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// An array binds perfectly well as a block named "0", "1", … reachable as <c>${port:0}</c>.
    /// Refused rather than accepted: a name that is a position is not one anybody meant to write.
    /// </remarks>
    [Fact]
    public void PortsWrittenAsAList_AreRefusedForHavingNoNames()
    {
        var ex = Refused("[5672, 15672]");

        Assert.Contains(
            "'port' in the 'kubernetes' block is written as a list, so its ports are keyed by "
            + "position.",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("a connection string reaches a port by name", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The case the whole walk exists for: the binder drops this entry rather than failing, so the
    /// block would bind one port short and the tunnel forward one fewer than was written.
    /// </remarks>
    [Fact]
    public void ANamedPortThatIsNotANumber_SaysItWouldBeDropped()
    {
        var ex = Refused("""{ "amqp": "abc" }""");

        Assert.Contains(
            "'port' in the 'kubernetes' block names a port 'amqp', but its value 'abc' is not a "
            + "whole number.",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("one fewer would be forwarded than the block names", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A named port set to <c>null</c> by a configuration layer that can express one is reported,
    /// because the binder would otherwise drop it and forward one port fewer than was named.
    /// </summary>
    [Fact]
    public void ANullNamedPort_FromALayerThatCanExpressOne_SaysItWouldBeDropped()
    {
        var ex = RefusedFromALayer(("amqp", "5672"), ("management", null));

        Assert.Contains(
            "names a port 'management', but it has no value.",
            ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The same thing written in <c>servicesources.local.json</c> cannot be reported at all, and
    /// this pins that rather than leaving it to be discovered.
    /// </summary>
    /// <remarks>
    /// The file source drops null-valued keys as it re-roots, so <c>{ "amqp": null }</c> arrives as
    /// a <c>port</c> with nothing in it — indistinguishable from a <c>port</c> nobody wrote. A block
    /// carrying one good port and one null would therefore bind one short in silence; there is no
    /// gap to notice, the way a list's indices give one away. Recorded as a known limit of the file
    /// rather than papered over.
    /// </remarks>
    [Fact]
    public void ANullNamedPort_FromTheFile_LeavesTheFieldMissingInstead()
    {
        var ex = Refused("""{ "amqp": null }""");

        Assert.Contains("requires 'kubernetes.port'", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The empty spelling unsets a whole field; there is no gesture for taking one name out of a
    /// block a lower layer wrote, and the message says so rather than leaving a reader to try.
    /// </remarks>
    [Fact]
    public void ANamedPortSetToEmpty_SaysTheUnsetGestureIsFieldLevel()
    {
        var ex = Refused("""{ "amqp": "" }""");

        Assert.Contains(
            "An empty value unsets a whole field; it does not take one name out of a block.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedPortThatIsABlock_SaysEveryNamedPortIsANumber()
    {
        var ex = Refused("""{ "amqp": { "container": 5672 } }""");

        Assert.Contains(
            "names a port 'amqp', but its entry is a block of settings rather than a number.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APortWithNoName_IsRefused()
    {
        var ex = Refused("""{ "": 5672 }""");

        Assert.Contains(
            "'port' in the 'kubernetes' block names a port with no name.",
            ex.Message,
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// Every problem with the block reported at once, the way an entry's problems already are.
    /// Reporting one per run costs a failed startup per mistake.
    /// </remarks>
    [Fact]
    public void SeveralBadEntries_AreAllReported()
    {
        var ex = Refused("""{ "amqp": "abc", "management": "also-not-a-number" }""");

        Assert.Contains("names a port 'amqp'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("names a port 'management'", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A name is developer-invented free text, and these messages are relayed into <c>~/.aspire/logs</c>
    /// and pasted into issues. A newline in a name would otherwise forge a line of its own.
    /// </remarks>
    [Fact]
    public void APortNameCarryingANewline_IsEscapedInTheMessage()
    {
        var ex = Refused("""{ "amqp\nBacking service 'x': all is well.": "abc" }""");

        Assert.Contains("\\n", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\nBacking service 'x': all is well.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty array arrives as an empty <em>value</em>, which is the gesture that unsets a field —
    /// not an empty block of named ports.
    /// </summary>
    /// <remarks>
    /// So it is not refused here. It leaves <c>port</c> unset, and the source then reports the field
    /// as missing, which is a different message and the right one.
    /// </remarks>
    [Fact]
    public void AnEmptyList_UnsetsTheFieldRatherThanBeingAnEmptyBlock()
    {
        var ex = Refused("[]");

        Assert.Contains("requires 'kubernetes.port'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedNamedBlock_IsAccepted() =>
        Accepted("""{ "amqp": 5672, "management": 15672 }""", "amqp://localhost:${port:amqp}/");

    [Fact]
    public void AWellFormedSinglePort_IsAccepted() => Accepted("5432");
}
