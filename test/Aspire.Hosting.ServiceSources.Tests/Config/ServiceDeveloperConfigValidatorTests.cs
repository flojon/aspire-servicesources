using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// Keys are checked against the shape of the bound types, so a key that would bind to nothing is
/// reported rather than silently dropped. Every block is checked, not only the one the entry's
/// source names.
/// </summary>
public class ServiceDeveloperConfigValidatorTests
{
    private const string Catalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: Orders.csproj
        """;

    private static string CreateAppHostDirectory(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), Catalog);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        return dir;
    }

    private static ServiceSourcesConfigurationException Load(string json)
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));
        return Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));
    }

    [Fact]
    public void Validate_FlatFieldAtEntryRoot_NamesTheBlockItBelongsUnder()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "path": "/src/orders" } } }""");

        Assert.Contains("'path' is not a valid key here", ex.Message);
        Assert.Contains("'local' block", ex.Message);
    }

    /// <remarks>
    /// The flat shape's worst case, because the field's old flat name is also a block name: the key
    /// is valid at this level, so only its scalar value gives it away. It binds to nothing, so
    /// letting it through would resolve the service off the catalog's url as though nothing were
    /// wrong.
    /// </remarks>
    [Fact]
    public void Validate_FlatValueWrittenAtABlockKey_IsRejected()
    {
        var ex = Load("""{ "services": { "orders": { "source": "url", "url": "https://orders.invalid" } } }""");

        Assert.Contains("'url' takes a block of settings, not a value", ex.Message);
    }

    /// <remarks>
    /// Replaces AddServiceTests.AddService_ContainerSourceWithForeignPortField_…, which Task 1
    /// deleted. The rule it guards has changed: a stray field is now rejected for not being a valid
    /// key anywhere at this level, not for belonging to a source other than the entry's, so the
    /// message names the block 'port' belongs in and says nothing about the entry's own source.
    /// </remarks>
    [Fact]
    public void Validate_FlatFieldBelongingToAnotherSource_NamesThatSourcesBlock()
    {
        var ex = Load("""{ "services": { "orders": { "source": "container", "port": 9090 } } }""");

        Assert.Contains("orders", ex.Message);
        Assert.Contains("'port' is not a valid key here", ex.Message);
        Assert.Contains("'kubernetes' block", ex.Message);

        // Naming a source to switch to would be advice to change what the service resolves to.
        Assert.DoesNotContain("\"source\"", ex.Message);
    }

    /// <remarks>
    /// The widening this change makes deliberately: 'orders' is well formed and is the service being
    /// resolved, while the malformed entry names a service nothing asks for. Validation runs over
    /// every entry when the config is read, so it is still reported.
    /// </remarks>
    [Fact]
    public void Validate_MalformedEntryForAnotherService_StillFailsTheLoad()
    {
        var ex = Load("""
            { "services": {
                "orders": { "source": "local" },
                "unused": { "source": "local", "path": "/src/unused" } } }
            """);

        Assert.Contains("unused", ex.Message);
        Assert.Contains("'path'", ex.Message);
    }

    [Fact]
    public void Validate_UnknownKeyBelongingToNoBlock_ListsTheValidKeys()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "nonsense": "x" } } }""");

        Assert.Contains("'nonsense' is not a valid key", ex.Message);
        Assert.Contains("'source'", ex.Message);
        Assert.Contains("'kubernetes'", ex.Message);
    }

    [Fact]
    public void Validate_TypoInsideAnInactiveBlock_IsStillRejected()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "local",
                "kubernetes": { "contxt": "dev-west" } } } }
            """);

        Assert.Contains("'contxt'", ex.Message);
        Assert.Contains("kubernetes", ex.Message);
    }

    [Fact]
    public void Validate_ValidInactiveBlock_IsAccepted()
    {
        var dir = CreateAppHostDirectory("""
            { "services": { "orders": {
                "source": "url",
                "url": { "url": "https://orders.invalid" },
                "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 } } } }
            """);

        var builder = TestHelpers.CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("url", config.Source);
        Assert.Equal("dev-west", config.Kubernetes.Context);
    }

    /// <remarks>
    /// The file spells its keys lowercase and the properties they are checked against are
    /// PascalCase, so an ordinal comparison would reject every well-formed file there is. The
    /// PascalCase case is the spelling an environment variable would supply, reaching the same
    /// fields through the file here because that is the cheaper way to drive it.
    /// </remarks>
    [Theory]
    [InlineData("local", "path")]
    [InlineData("Local", "Path")]
    [InlineData("LOCAL", "PATH")]
    public void Validate_AnyKeyCasing_IsAccepted(string block, string field)
    {
        var dir = CreateAppHostDirectory(
            $$"""{ "services": { "orders": { "source": "local", "{{block}}": { "{{field}}": "/src/orders" } } } }""");

        var builder = TestHelpers.CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("/src/orders", config.Local.Path);
    }

    /// <remarks>
    /// The mirror of the block-key case: an object written where a field's value goes. The key is a
    /// valid one, so only its shape gives it away — and the binder's answer to it is to drop the
    /// whole entry, which then reads downstream as a service nobody configured.
    /// </remarks>
    [Fact]
    public void Validate_BlockWrittenWhereAFieldValueGoes_IsRejected()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "local",
                "local": { "path": { "a": "b" } } } } }
            """);

        Assert.Contains("'path'", ex.Message);
        Assert.Contains("'local' block", ex.Message);
        Assert.Contains("takes a value, not a block of settings", ex.Message);
    }

    /// <remarks>
    /// 'source' is the one root key that is not a block, and an object written there binds it to ""
    /// while taking the rest of the entry down with it.
    /// </remarks>
    [Fact]
    public void Validate_BlockWrittenAtSource_IsRejected()
    {
        var ex = Load("""{ "services": { "orders": { "source": { "x": "y" } } } }""");

        Assert.Contains("'source'", ex.Message);
        Assert.Contains("takes a value, not a block of settings", ex.Message);
    }

    /// <remarks>
    /// 'container' is a block name and nothing else, so a message that fell back to listing the
    /// keys valid at this level would name it as invalid and then list it as valid.
    /// </remarks>
    [Fact]
    public void Validate_FlatValueWrittenAtANonUrlBlockKey_SaysTheKeyTakesABlock()
    {
        var ex = Load("""{ "services": { "orders": { "source": "container", "container": "v1.4.2" } } }""");

        Assert.Contains("'container' takes a block of settings, not a value", ex.Message);
        Assert.Contains("'tag'", ex.Message);
        Assert.DoesNotContain("is not a valid key", ex.Message);
    }

    /// <remarks>
    /// HomeBlockOf turns "that key does not go there" into "here is where it goes" by finding the
    /// block whose fields contain the name. That is a single answer only while no two blocks share
    /// a field name; a shared one — 'port' on the container block, say, which the catalog side
    /// already has — would make the answer depend on the order GetProperties() happens to return,
    /// which the CLR does not guarantee.
    /// </remarks>
    [Fact]
    public void Shape_NoFieldNameIsSharedByTwoBlocks()
    {
        var blocks = ServiceDeveloperConfigShape.BlockFields;

        foreach (var (name, fields) in blocks)
        {
            foreach (var (otherName, otherFields) in blocks.Where(other => other.Key != name))
            {
                var shared = fields.Keys.Where(otherFields.ContainsKey).ToArray();

                Assert.True(
                    shared.Length == 0,
                    $"Blocks '{name}' and '{otherName}' both declare {string.Join(", ", shared)}.");
            }
        }
    }

    /// <remarks>
    /// A key that is valid everywhere except in what it was set to. Left to the binder it arrives as
    /// an InvalidOperationException naming a CLR type and a key path, which nothing upstream treats
    /// as a configuration problem, and which this release's premise — a malformed entry reported at
    /// read time, in terms of the entry — does not otherwise leave room for.
    /// </remarks>
    [Fact]
    public void Validate_ValueThatCannotBind_SaysWhatTheFieldTakes()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "port": "abc" } } } }
            """);

        Assert.Contains("'port' in the 'kubernetes' block takes a whole number", ex.Message);
        Assert.Contains("'abc'", ex.Message);
    }

    /// <remarks>
    /// Emptying a key is the only gesture configuration offers for dropping a value a lower layer
    /// set, and whitespace is that gesture missed by a character rather than the gesture itself. It
    /// is refused whatever the field's type — see the string case below, which the binder itself
    /// would have taken — so the one spelling that does work is named rather than guessed at.
    /// </remarks>
    [Fact]
    public void Validate_WhitespaceWhereANumberGoes_NamesTheSpellingThatUnsetsIt()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "port": " " } } } }
            """);

        Assert.Contains("'port' in the 'kubernetes' block", ex.Message);
        Assert.Contains("whitespace rather than a value", ex.Message);
        Assert.Contains("empty value", ex.Message);
    }

    [Fact]
    public void Validate_EmptyValueWhereANumberGoes_LeavesTheFieldUnset()
    {
        var dir = CreateAppHostDirectory("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "context": "dev-west", "port": "" } } } }
            """);

        var builder = TestHelpers.CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Null(config.Kubernetes.Port);
    }

    /// <remarks>
    /// Validation covers entries no AddService call names and keys the file is only the lowest
    /// contributor of, so a message that named the file would send a developer whose environment
    /// carries the stale key to edit the one place the value is not.
    /// </remarks>
    [Fact]
    public void Validate_AnyRejection_NamesTheKeyAndItsEnvironmentSpelling()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "path": "/src/orders" } } }""");

        Assert.Contains("'ServiceSources:Services:orders:path'", ex.Message);
        Assert.Contains("ServiceSources__Services__orders__path", ex.Message);
    }

    /// <remarks>
    /// The likeliest slip of all in moving off the flat shape, because the flat shape's shortest
    /// entry was a source and nothing else: the whole entry gets written as that source's name. It
    /// carries no children, so the walk above never had a key to look at, and the binder answers a
    /// scalar where a service entry goes with null — which the dictionary binder drops, leaving the
    /// service reading downstream as one nobody configured. The check is worth more than the message
    /// it produces: it is the difference between being told the shape is wrong and being told, of a
    /// file that plainly names the service, that it "configures no services".
    /// </remarks>
    [Fact]
    public void Validate_ServiceEntryWrittenAsAValue_IsRejectedRatherThanDropped()
    {
        var ex = Load("""{ "services": { "orders": "local" } }""");

        Assert.Contains("the entry takes a block of settings, not the value 'local'", ex.Message);

        // The value names a source, so the suggestion is the key it belongs under rather than a
        // placeholder the developer has to fill in again.
        Assert.Contains("""{ "source": "local" }""", ex.Message);
        Assert.DoesNotContain("configures no services", ex.Message);
    }

    /// <remarks>
    /// The same shape without a value that happens to name a source: the suggestion falls back to
    /// naming the keys an entry takes, since there is nothing to guess at.
    /// </remarks>
    [Fact]
    public void Validate_ServiceEntryWrittenAsAValueThatIsNotASourceName_NamesTheValidKeys()
    {
        var ex = Load("""{ "services": { "orders": "/src/orders" } }""");

        Assert.Contains("the entry takes a block of settings, not the value '/src/orders'", ex.Message);
        Assert.Contains("""{ "source": "..." }""", ex.Message);
        Assert.Contains("'source'", ex.Message);
        Assert.Contains("'local'", ex.Message);
        Assert.DoesNotContain("configures no services", ex.Message);
    }

    /// <remarks>
    /// Moving an entry off the flat shape misplaces keys in bunches, so every problem is collected
    /// rather than thrown at the first one found: one per run is one failed startup per key, and
    /// the order they surface in belongs to the configuration provider rather than to the file, so
    /// which key a developer was sent to fix first was not even reproducible.
    /// </remarks>
    [Fact]
    public void Validate_SeveralMisplacedKeys_ReportsAllOfThemAtOnce()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "local",
                "path": "/src/orders",
                "ref": "main",
                "context": "dev-west" } } }
            """);

        Assert.Contains("3 problems with the entry", ex.Message);
        Assert.Contains("'path'", ex.Message);
        Assert.Contains("'ref'", ex.Message);
        Assert.Contains("'context'", ex.Message);
    }

    /// <remarks>
    /// The mirror of the numeric case above, and the reason it is not left to the binder: a string
    /// field takes whitespace perfectly well, and the blank-to-absent walk then drops it, so this
    /// override used to vanish and send the service to its managed checkout without a word. Silence
    /// is the one outcome the validator exists to prevent, and the field's type is no reason to
    /// make an exception of it.
    /// </remarks>
    [Fact]
    public void Validate_WhitespaceWhereAStringGoes_IsRefusedRatherThanReadAsAbsent()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "local",
                "local": { "path": " " } } } }
            """);

        Assert.Contains("'path' in the 'local' block", ex.Message);
        Assert.Contains("whitespace rather than a value", ex.Message);
        Assert.Contains("empty value", ex.Message);

        // Not the "takes a <type>" phrasing a bind failure gets: a space *is* a string, so
        // reporting what the field takes against what it was given contradicts itself here.
        Assert.DoesNotContain("takes a string", ex.Message);
    }

    /// <remarks>
    /// The check is IsNullOrWhiteSpace, which a tab, a newline and a non-breaking space all
    /// satisfy, so the message may not call the value a space: someone who typed one of these
    /// would retype a space and meet the identical error. Nor can it echo the character as it
    /// stands, since none of them can be told from a space by looking.
    /// </remarks>
    [Fact]
    public void Validate_NonSpaceWhitespaceValue_NamesTheCharacterRatherThanCallingItASpace()
    {
        // A non-breaking space, the one a copy-paste out of a browser or a document leaves behind.
        var ex = Load("""
            { "services": { "orders": {
                "source": "local",
                "local": { "path": "\u00a0" } } } }
            """);

        Assert.Contains("whitespace rather than a value", ex.Message);
        Assert.Contains("\\u00a0", ex.Message);
        Assert.DoesNotContain("one or more spaces", ex.Message);
    }

    /// <remarks>
    /// The remedy line names an environment variable, and for a key that has to hold a block that
    /// advice cannot be followed: the flat providers carry one leaf each, so no environment
    /// variable can put an object at this key, and reaching for one is how an entry comes to be
    /// written as a value in the first place. The spelling named has to be a field's.
    /// </remarks>
    [Fact]
    public void Validate_BlockKeyRejection_NamesAFieldsEnvironmentSpellingNotTheBlocksOwn()
    {
        var ex = Load("""{ "services": { "orders": { "source": "url", "url": "https://orders.invalid" } } }""");

        Assert.Contains("ServiceSources__Services__orders__url__Url", ex.Message);
    }

    /// <remarks>
    /// The same for the whole entry, whose remedy used to advise setting
    /// <c>ServiceSources__Services__orders</c> — a key that holds an object, so the advice was the
    /// mistake being reported.
    /// </remarks>
    [Fact]
    public void Validate_EntryRejection_NamesAFieldsEnvironmentSpellingNotTheEntrysOwn()
    {
        var ex = Load("""{ "services": { "orders": "local" } }""");

        Assert.Contains("ServiceSources__Services__orders__Source", ex.Message);
    }

    /// <remarks>
    /// A value at the entry key does not mean the entry has no keys to walk. Configuration merges
    /// per key, so a block in the file underneath a higher layer's scalar — the environment setting
    /// ServiceSources__Services__orders over an entry in local.json — yields a section carrying a
    /// value *and* children. Reporting only the shape complaint would hide the misplaced key behind
    /// a description of a shape the developer's own file has not got.
    /// </remarks>
    [Fact]
    public void Validate_EntryCarryingBothAValueAndKeys_ReportsBoth()
    {
        var dir = CreateAppHostDirectory("""
            { "services": { "orders": { "source": "local", "path": "/src/orders" } } }
            """);

        var builder = TestHelpers.CreateBuilder(dir);

        // A higher layer than the file, which the package registers lowest of all.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:Services:orders"] = "local",
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("2 problems with the entry", ex.Message);
        Assert.Contains("'path' is not a valid key here", ex.Message);

        // The entry does have its block of settings, and it binds: the binder finds no string
        // converter for the entry type and falls through to the children. So the fault is the
        // inert value, not the shape — telling this developer that "the entry takes a block of
        // settings" and showing them one would describe a mistake they did not make.
        Assert.Contains("as well as its settings, and that value is inert", ex.Message);
        Assert.DoesNotContain("the entry takes a block of settings", ex.Message);
    }

    /// <remarks>
    /// The entry's own value is echoed back like a field's, so it needs the same escaping: an
    /// entry set to a non-breaking space from some layer would otherwise be reported as the space
    /// it looks like, which is the message telling a developer their value is something other than
    /// what they typed.
    /// </remarks>
    [Fact]
    public void Validate_EntryWrittenAsANonSpaceWhitespaceValue_NamesTheCharacter()
    {
        var ex = Load("""{ "services": { "orders": "\u00a0" } }""");

        Assert.Contains("the entry takes a block of settings", ex.Message);
        Assert.Contains("\\u00a0", ex.Message);
    }

    /// <remarks>
    /// The collecting spans entries, not only the keys within one. A file still to be moved onto
    /// the block shape has every service wrong at once, and reporting a service at a time costs a
    /// failed startup each — the same objection that makes the walk over one entry collect.
    /// </remarks>
    [Fact]
    public void Validate_ProblemsInSeveralEntries_ReportsThemAllTogether()
    {
        var ex = Load("""
            { "services": {
                "orders":   { "source": "local", "path": "/src/orders" },
                "payments": { "source": "local", "ref": "main" } } }
            """);

        Assert.Contains("2 service entries", ex.Message);
        Assert.Contains("Service 'orders'", ex.Message);
        Assert.Contains("Service 'payments'", ex.Message);
        Assert.Contains("'path' is not a valid key here", ex.Message);
        Assert.Contains("'ref' is not a valid key here", ex.Message);
    }

    /// <summary>
    /// A field misspelled at an entry's root names the field it was reaching for, rather than the
    /// keys valid at the root — which cannot include it, since it is a field of a block.
    /// </summary>
    /// <remarks>
    /// The shape of every unmigrated file: before the release that moved fields into blocks, all of
    /// them sat at the entry root, so this is what a developer retyping one gets wrong. Spelled
    /// correctly the same key is walked through the move; one letter off, it used to fall off a
    /// cliff.
    /// </remarks>
    [Fact]
    public void Validate_MisspelledFieldAtEntryRoot_NamesTheFieldAndItsBlock()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "pth": "/src/orders" } } }""");

        Assert.Contains("'pth' is not a valid key here", ex.Message);
        Assert.Contains("Did you mean 'path'", ex.Message);
        Assert.Contains("'local' block", ex.Message);

        // The shape to write, as the exact-match message gives it.
        Assert.Contains("""{ "path": ... }""", ex.Message);

        // The old message, which listed keys that cannot contain the answer.
        Assert.DoesNotContain("Valid keys are", ex.Message);
    }

    /// <remarks>
    /// Two edits, which only a name long enough to afford them gets: <c>namespace</c> is nine
    /// letters, where a doubled or transposed letter is the usual mistake and one edit is stingy.
    /// The companion test below is the other half of that rule.
    /// </remarks>
    [Fact]
    public void Validate_MisspelledLongFieldAtEntryRoot_IsRecognizedAtTwoEdits()
    {
        var ex = Load("""{ "services": { "orders": { "source": "kubernetes", "namspce": "orders" } } }""");

        Assert.Contains("Did you mean 'namespace'", ex.Message);
        Assert.Contains("'kubernetes' block", ex.Message);
    }

    /// <summary>
    /// Two edits from a three-letter field is not a near miss, and is answered with the list rather
    /// than a guess.
    /// </summary>
    /// <remarks>
    /// The tolerance scales with the candidate's length deliberately. Two edits from <c>ref</c> or
    /// <c>tag</c> reaches a large part of the alphabet, so a flat tolerance would confidently
    /// misname fields — which is worse than the list, because the list is at least true.
    /// </remarks>
    [Fact]
    public void Validate_KeyTwoEditsFromAShortField_IsNotGuessedAt()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "rap": "x" } } }""");

        Assert.Contains("'rap' is not a valid key", ex.Message);
        Assert.Contains("Valid keys are", ex.Message);
        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    /// <summary>
    /// A misspelled <em>block</em> name at the root still gets the list, which does contain the
    /// answer — and is not sent to a field it never resembled.
    /// </summary>
    [Fact]
    public void Validate_MisspelledBlockNameAtEntryRoot_ListsTheValidKeys()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "locl": { "path": "/src" } } } }""");

        Assert.Contains("'locl' is not a valid key", ex.Message);
        Assert.Contains("'local'", ex.Message);
        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    /// <summary>
    /// The same misspelling <em>inside</em> a block keeps its own message, which lists the block's
    /// keys instead of guessing.
    /// </summary>
    /// <remarks>
    /// Deliberately not given the near-miss treatment: there are two to four valid keys there and
    /// they are all printed, so a guess adds nothing a reader cannot already see, and would name a
    /// field they did not mean whenever the typo lands closer to a different one.
    /// </remarks>
    [Fact]
    public void Validate_MisspelledFieldInsideItsBlock_ListsTheBlocksKeys()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "local": { "pth": "/src" } } } }""");

        Assert.Contains("'pth' is not a valid key in the 'local' block", ex.Message);
        Assert.Contains("'path'", ex.Message);
        Assert.Contains("'ref'", ex.Message);
        Assert.DoesNotContain("Did you mean", ex.Message);
    }
}
