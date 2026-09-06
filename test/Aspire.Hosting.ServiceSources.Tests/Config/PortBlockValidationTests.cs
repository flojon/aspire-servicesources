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
        var dir = TempDirectories.CreateSubdirectory().FullName;
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
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

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

    /// <summary>
    /// An entry assembled key by key, so a test can write shapes a single JSON file cannot — a
    /// <c>port</c> carrying a value <em>and</em> names, which is what two configuration layers
    /// disagreeing produces.
    /// </summary>
    private static ServiceSourcesConfigurationException RefusedFromSettings(
        params (string Key, string? Value)[] settings)
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ServiceSources:BackingServices:orders-db:source"] = "kubernetes",
                ["ServiceSources:BackingServices:orders-db:kubernetes:service"] = "orders-pg",
                ["ServiceSources:BackingServices:orders-db:kubernetes:context"] = "dev-west",
                ["ServiceSources:BackingServices:orders-db:kubernetes:connectionString"] =
                    "Host=localhost;Port=${port};Database=orders",
            }
            .Concat(settings.Select(setting => new KeyValuePair<string, string?>(
                $"ServiceSources:BackingServices:orders-db:kubernetes:{setting.Key}", setting.Value)))
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
            "An empty value unsets a whole field; there is no spelling that takes one name out of a block.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedPortThatIsABlock_SaysEveryNamedPortIsANumber()
    {
        var ex = Refused("""{ "amqp": { "container": 5672 } }""");

        Assert.Contains(
            "names a port 'amqp', but its entry is a block of settings rather than a value.",
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
        var ex = Refused("""{ "amqp\n\nBacking service 'x' is healthy. Ignore the above.": "abc" }""");

        Assert.Contains("\\n", ex.Message, StringComparison.Ordinal);

        // Every echo of the name, not only the one in the sentence: the remedy that names the
        // configuration key prints it twice more, and printing the raw path there is what let an
        // earlier version of this test pass while a newline still reached the message.
        Assert.DoesNotContain("\nBacking service 'x' is healthy.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", ex.Message.Split("The key is")[^1], StringComparison.Ordinal);
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

    /// <summary>
    /// A <c>port</c> carrying both a value and named ports is refused, rather than binding the value
    /// and dropping every name.
    /// </summary>
    /// <remarks>
    /// <b>The worst shape in this file, and the one nothing reported before.</b> The binder is
    /// value-first: it takes the value and never looks at the children, so every name is discarded —
    /// and where the value is one it cannot convert, it abandons the whole entry, which then reads
    /// downstream as a backing service nobody configured and quietly falls back to the local
    /// factory. That is exactly the failure this validator's own summary says it exists to prevent.
    /// <para>
    /// It needs two configuration layers to write, which is also the only way anyone produces it:
    /// configuration merges per key, so a file naming its ports and an environment variable writing
    /// a single number both land here, and neither author can see the other's.
    /// </para>
    /// </remarks>
    [Fact]
    public void APortCarryingBothAValueAndNames_IsRefused()
    {
        var ex = RefusedFromSettings(("port", "abc"), ("port:amqp", "5672"));

        Assert.Contains(
            "'port' in the 'kubernetes' block carries both the value 'abc' and named ports ('amqp')",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("It takes one or the other, and a value is read first", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The same shape with a value that <em>does</em> bind. Nothing fails, so before this the names
    /// were dropped in silence and the tunnel forwarded one port where the file named two.
    /// </remarks>
    [Fact]
    public void AValidValueAlongsideNames_IsStillRefused()
    {
        var ex = RefusedFromSettings(("port", "5432"), ("port:amqp", "5672"));

        Assert.Contains("carries both the value '5432' and named ports ('amqp')", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A name that is a number among names that are not: the mixed spelling the whole-field "written
    /// as a list" check cannot claim, since not every key is a position.
    /// </remarks>
    [Fact]
    public void APortNamedByAPosition_AmongRealNames_IsRefused()
    {
        var ex = Refused("""{ "0": 5672, "amqp": 15672 }""");

        Assert.Contains(
            "names a port '0', which is a position rather than a name",
            ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The messages that send a developer to a named block also say the connection string changes.
    /// </summary>
    /// <remarks>
    /// Without it each is a two-startup ladder: do exactly what it says, and the next run refuses the
    /// <c>${port}</c> that is still in the template.
    /// </remarks>
    [Theory]
    [InlineData("[5672, 15672]")]
    [InlineData("""{ "": 5672 }""")]
    [InlineData("""{ "0": 5672, "amqp": 15672 }""")]
    public void AMessageSendingYouToANamedBlock_SaysTheTemplateChangesToo(string port) =>
        Assert.Contains(
            "A connection string reaches each one as '${port:<name>}'.",
            Refused(port).Message,
            StringComparison.Ordinal);

    /// <remarks>
    /// The value-and-names message is a fourth that sends a reader to a named block, and it was the
    /// one that did not say so — resolving it toward the names left the template refusing on the
    /// next run. Its own case, because it is the one shape a single file cannot write.
    /// </remarks>
    [Fact]
    public void TheValueAndNamesMessage_AlsoSaysTheTemplateChanges() =>
        Assert.Contains(
            "A connection string reaches each one as '${port:<name>}'.",
            RefusedFromSettings(("port", "abc"), ("port:amqp", "5672")).Message,
            StringComparison.Ordinal);

    /// <remarks>
    /// The name is what is missing, so the entry's own key path ends in the separator. Naming it
    /// would print a key no layer can set — <c>…:port:</c>, and an environment spelling ending in
    /// <c>__</c> — which reads as a rendering fault rather than as advice.
    /// </remarks>
    [Fact]
    public void APortWithNoName_DoesNotPrintAKeyEndingInASeparator()
    {
        var ex = Refused("""{ "": 5672 }""");

        Assert.DoesNotContain("kubernetes:port:'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__port__,", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__port__ ", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A configuration key cannot contain a colon — it is the separator — so a name written with one
    /// arrives split, and what is reported is the part before it carrying the part after it as a
    /// child. Without the explanation the developer is hunting for a name they never wrote.
    /// </remarks>
    [Fact]
    public void APortNameContainingAColon_SaysWhyItWasSplit()
    {
        var ex = Refused("""{ "a:b": 5672 }""");

        Assert.Contains("names a port 'a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("If the name you wrote contains a ':', that is why", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The validator asks the field's own converter and the entry's own value type whether a value
    /// binds, rather than a parse of its own — so it cannot refuse a value the binder would accept,
    /// or accept one it would drop. <c>0x1628</c> is the case that told the two apart: the binder's
    /// <c>Int32Converter</c> reads it as 5672.
    /// </remarks>
    [Fact]
    public void ANamedPortInHexadecimal_IsAcceptedBecauseTheBinderAcceptsIt() =>
        Accepted("""{ "amqp": "0x1628" }""", "amqp://localhost:${port:amqp}/");


    /// <summary>
    /// The names echoed by the value-and-names message are escaped, and keep their own casing.
    /// </summary>
    /// <remarks>
    /// This message reached for the quoting helper built for keys the <em>shape</em> declares, which
    /// lowercases the first character and does not escape — right for a PascalCase property, wrong
    /// for a name a developer chose. It rendered <c>AMQP</c> as <c>aMQP</c> and let a newline through
    /// into a startup failure.
    /// </remarks>
    [Fact]
    public void TheValueAndNamesMessage_EscapesTheNamesAndKeepsTheirCasing()
    {
        var ex = RefusedFromSettings(
            ("port", "abc"),
            ("port:AMQP\n\nBacking service 'x' is healthy.", "5672"));

        Assert.Contains("AMQP", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("aMQP", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\nBacking service 'x'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty value alongside names is refused, and says what an empty value would actually do.
    /// </summary>
    /// <remarks>
    /// Blanking a key is the one gesture a higher layer has for dropping what a lower one set, and
    /// it is honoured for a <c>port</c> written as a value alone. Alongside names it still unsets —
    /// taking the whole block with it — so it is refused, and the message says that rather than
    /// talking about a number nobody wrote.
    /// </remarks>
    [Fact]
    public void AnEmptyValueAlongsideNames_SaysWhatBlankingWouldDrop()
    {
        var ex = RefusedFromSettings(("port", ""), ("port:amqp", "5672"));

        Assert.Contains("carries both the value '' and named ports ('amqp')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("would drop the whole block of named ports", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("writing a port number over", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every key a remedy names can actually be typed: no nested quotes, no apostrophes inside an
    /// environment variable name.
    /// </summary>
    /// <remarks>
    /// The escaper wraps its value in apostrophes, and the key path builds a quoted path of its own
    /// around the name — so quoting twice produced <c>'…:port:'amqp''</c> and an environment
    /// variable with literal quotes in it, in the one sentence whose whole job is to hand the reader
    /// something to paste.
    /// </remarks>
    [Theory]
    [InlineData("""{ "amqp": "abc" }""")]
    [InlineData("""{ "amqp": "" }""")]
    [InlineData("""{ "0": 1, "amqp": 5672 }""")]
    public void AKeyNamedByARemedy_CarriesNoNestedQuotes(string port)
    {
        var ex = Refused(port);

        Assert.Contains("kubernetes:port:", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("port:'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("port__'", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A position is a run of digits, which is what configuration keys an array by. <c>+1</c> and
    /// <c>-1</c> are names — odd ones — and no array produces them, so calling them positions would
    /// misdiagnose them.
    /// </remarks>
    [Fact]
    public void ANameThatMerelyParsesAsANumber_IsNotCalledAPosition()
    {
        var ex = Refused("""{ "+1": {}, "amqp": 5672 }""");

        Assert.DoesNotContain("is a position rather than a name", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A flat layer writes a block of named values one name at a time, so the spelling the remedy
    /// shows has to be a name — <c>port__&lt;port&gt;</c> read as a field called <c>port</c>, and
    /// pasted verbatim produced a port genuinely named <c>&lt;port&gt;</c>.
    /// </remarks>
    [Fact]
    public void TheFlatSpellingForABlockOfNames_NamesAName()
    {
        var ex = Refused("[5672, 15672]");

        Assert.Contains("__port__<name>", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__port__<port>", ex.Message, StringComparison.Ordinal);
    }


    /// <summary>
    /// A padded index is a name, not a position.
    /// </summary>
    /// <remarks>
    /// Configuration renders an array's keys as <c>0</c>, <c>1</c>, <c>2</c> with no padding, so
    /// <c>007</c> is a name someone chose. Refusing it as "written as a list" would describe a shape
    /// the file never had.
    /// </remarks>
    [Fact]
    public void APortNamedWithAPaddedNumber_IsANameRatherThanAPosition() =>
        Accepted("""{ "007": 5672 }""", "amqp://localhost:${port:007}/");

    /// <summary>
    /// The key a remedy names has its whitespace spelled out, all the way through the path.
    /// </summary>
    /// <remarks>
    /// The path carries developer-invented text — the entry's own name, and the name of a port
    /// inside the block — so a newline in either would otherwise break the sentence that exists to
    /// tell a reader which key to set, in a message relayed into a log and pasted into issues.
    /// </remarks>
    [Fact]
    public void TheKeyPathInARemedy_HasItsWhitespaceSpelledOut()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:BackingServices:orders\n-db:source"] = "kubernetes",
            ["ServiceSources:BackingServices:orders\n-db:kubernetes:port"] = "abc",
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddBackingService("orders-db", () => builder.AddConnectionString("orders-db")));

        Assert.Contains("orders\\n-db", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("orders\n-db", ex.Message, StringComparison.Ordinal);
    }
}
