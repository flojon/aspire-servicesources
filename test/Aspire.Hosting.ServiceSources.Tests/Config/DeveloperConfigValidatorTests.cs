using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// Keys are checked against the shape of the bound types, so a key that would bind to nothing is
/// reported rather than silently dropped. Every block is checked, not only the one the entry's
/// source names.
/// </summary>
public class DeveloperConfigValidatorTests
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

    /// <summary>
    /// The backing-service half of the same walk, which this file had no way to reach before.
    /// </summary>
    /// <remarks>
    /// The catalog <see cref="CreateAppHostDirectory"/> writes is inert here — a backing service is
    /// declared by the <c>AddBackingService</c> call rather than by a catalog, and
    /// <c>ReadBackingServicesFrom</c> never looks for one — so this reuses that helper rather than
    /// adding a second one that differs only in a file nothing reads.
    /// <para>
    /// Resolved through <c>BackingServicesFor</c> and not <c>ResolveBackingService</c>: the latter
    /// answers a name it does not know with a default instead of failing, so a test built on it
    /// could pass while asserting nothing at all.
    /// </para>
    /// </remarks>
    private static ServiceSourcesConfigurationException LoadBackingService(string json)
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));
        return Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.BackingServicesFor(builder));
    }

    /// <remarks>
    /// A byte-order mark is what a copy-paste out of a Windows-authored file leaves behind, and it
    /// has no glyph at all: echoed as itself it is indistinguishable from the value being correct,
    /// so the reader is shown what looks exactly like what they wrote and told it is wrong. It is
    /// not whitespace, so the arm that spells out a tab does not reach it.
    /// </remarks>
    [Fact]
    public void Validate_ValueCarryingAnInvisibleCharacter_SpellsItOutRatherThanEchoingIt()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "port": "\uFEFF8080" } } } }
            """);

        Assert.Contains("\\ufeff", ex.Message);
        Assert.DoesNotContain("'\ufeff8080'", ex.Message);
    }

    /// <remarks>
    /// The five fields this rule covers, each padded three ways. A theory rather than fifteen tests
    /// because the symmetry is the point of #236: a service and a backing service carry the same
    /// two keys, and a rule that reached one and not the other would be the defect rather than the
    /// fix. The attribute is applied by hand on two unrelated types, so nothing structural keeps
    /// them in step.
    /// <para>
    /// Every row asserts the refusal is <em>this</em> rule's rather than another complaint that
    /// happens to throw: a row naming a field that does not exist would be answered by
    /// <c>NotValidInBlock</c> and would otherwise pass while proving nothing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("services", "orders", "context", " dev-west", "dev-west")]
    [InlineData("services", "orders", "context", "dev-west ", "dev-west")]
    [InlineData("services", "orders", "context", "  dev-west  ", "dev-west")]
    [InlineData("services", "orders", "namespace", " orders", "orders")]
    [InlineData("services", "orders", "namespace", "orders ", "orders")]
    [InlineData("services", "orders", "namespace", " orders ", "orders")]
    [InlineData("backingServices", "orders-db", "context", " dev-west", "dev-west")]
    [InlineData("backingServices", "orders-db", "context", "dev-west ", "dev-west")]
    [InlineData("backingServices", "orders-db", "context", " dev-west ", "dev-west")]
    [InlineData("backingServices", "orders-db", "namespace", " orders", "orders")]
    [InlineData("backingServices", "orders-db", "namespace", "orders ", "orders")]
    [InlineData("backingServices", "orders-db", "namespace", " orders ", "orders")]
    [InlineData("backingServices", "orders-db", "service", " orders-pg", "orders-pg")]
    [InlineData("backingServices", "orders-db", "service", "orders-pg ", "orders-pg")]
    [InlineData("backingServices", "orders-db", "service", " orders-pg ", "orders-pg")]
    public void Validate_KubectlNameWithSurroundingWhitespace_IsRefusedWithTheSpellingThatWorks(
        string section, string entry, string field, string written, string expected)
    {
        var json = $$"""
            { "{{section}}": { "{{entry}}": {
                "source": "kubernetes",
                "kubernetes": { "{{field}}": "{{written}}" } } } }
            """;

        var ex = section == "services" ? Load(json) : LoadBackingService(json);

        Assert.Contains($"'{field}' in the 'kubernetes' block is set to '{written}'", ex.Message);
        Assert.Contains("kubectl", ex.Message);
        Assert.Contains($"Set it to '{expected}'.", ex.Message);

        // This rule's complaint and no other: a row naming a field that does not exist would be
        // answered by NotValidInBlock, and a second problem would switch Failure to its list wording.
        Assert.DoesNotContain("is not a valid key", ex.Message);
        Assert.DoesNotContain("problems with the entry", ex.Message);
    }

    /// <remarks>
    /// A context is the one opted-in field whose padding may have been meant: a kubeconfig context
    /// name is an arbitrary key, and `kubectl config set-context " padded "` succeeds. Telling that
    /// developer to write the trimmed spelling would send them to a context that need not exist, so
    /// the message carries the way out.
    /// </remarks>
    [Fact]
    public void Validate_PaddedContext_NamesTheRenameThatKeepsIt()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "context": " dev-west" } } } }
            """);

        Assert.Contains("kubectl config rename-context", ex.Message);
    }

    /// <remarks>
    /// The backing-service half of the same payload, asserted separately because it is a second
    /// application of the attribute written by hand on an unrelated type — which is exactly the
    /// drift #236 exists to prevent.
    /// </remarks>
    [Fact]
    public void Validate_PaddedContextOnABackingService_AlsoNamesTheRename()
    {
        var ex = LoadBackingService("""
            { "backingServices": { "orders-db": {
                "source": "kubernetes",
                "kubernetes": { "context": " dev-west" } } } }
            """);

        Assert.Contains("kubectl config rename-context", ex.Message);
    }

    /// <remarks>
    /// No other field can be in the context's position, so no other field pays for the sentence.
    /// </remarks>
    [Fact]
    public void Validate_PaddedNamespace_DoesNotOfferTheContextRename()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": " orders" } } } }
            """);

        Assert.DoesNotContain("rename-context", ex.Message);
    }

    /// <remarks>
    /// A tab and a non-breaking space are the two paddings a developer cannot see, and the second is
    /// what a paste out of a browser or a document leaves behind. Both are whitespace, so both fire
    /// this rule — and the message has to spell them out, since echoed as themselves they leave the
    /// reader looking at a value that appears to be precisely what they typed.
    /// <para>
    /// The padding is written as a JSON escape rather than as itself: a literal tab inside a JSON
    /// string is not valid JSON, so the document would fail to parse before the validator saw it.
    /// The two arguments are the same text by coincidence — one is what the file carries, the other
    /// what the message must render — and they are kept apart because that coincidence is not a
    /// rule.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(@"\t", @"\t")]
    [InlineData(@"\u00a0", @"\u00a0")]
    public void Validate_PaddingThatCannotBeSeen_IsSpelledOutInTheMessage(string padding, string spelled)
    {
        var ex = Load($$"""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": "{{padding}}orders" } } } }
            """);

        Assert.Contains(spelled, ex.Message);
        Assert.Contains("Set it to 'orders'.", ex.Message);
    }

    /// <remarks>
    /// The suffix every complaint in this file carries. It is what tells a developer the value need
    /// not have come from the file at all — an environment variable sets the same key — and dropping
    /// it from this one message would pass every other test here.
    /// </remarks>
    [Fact]
    public void Validate_PaddedNamespace_NamesTheConfigurationKeyItCameFrom()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": " orders" } } } }
            """);

        Assert.Contains("ServiceSources:Services:orders:kubernetes:namespace", ex.Message);
    }

    /// <remarks>
    /// Blank runs first and has to: a value of nothing but spaces satisfies both rules, and the one
    /// naming the empty spelling is what the developer reaching for it needs.
    /// </remarks>
    [Fact]
    public void Validate_WhitespaceOnlyContext_KeepsTheBlankComplaint()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "context": "  " } } } }
            """);

        Assert.Contains("whitespace rather than a value", ex.Message);
        Assert.DoesNotContain("Set it to ''", ex.Message);
    }

    /// <remarks>
    /// A context name may contain a space — `kubectl config set-context "my dev ctx"` succeeds — so
    /// a rule about whitespace anywhere in the value would refuse a working configuration. Only the
    /// ends of the value are this package's business.
    /// </remarks>
    [Fact]
    public void Validate_ContextWithInteriorSpace_IsAccepted()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "context": "my dev ctx", "port": 8080 } } } }
            """));

        var resolved = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("my dev ctx", resolved.DeveloperConfig.Kubernetes.Context);
    }

    /// <remarks>
    /// The trap this rule exists to close, re-created by the rule itself if the remedy is computed
    /// by trimming whitespace alone. A byte-order mark is not whitespace, so it survives Trim() —
    /// and ﻿ is a valid JSON escape, so a developer copying the proposed spelling back into
    /// the file writes the same broken value, this time with no whitespace left to trigger any
    /// message at all.
    /// </remarks>
    [Fact]
    public void Validate_PaddingAroundAnInvisibleCharacter_ProposesASpellingThatWorks()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": " ﻿orders" } } } }
            """);

        // What arrived is shown with the mark spelled out...
        Assert.Contains(@"﻿", ex.Message);

        // ...and what to write carries neither the space nor the mark.
        Assert.Contains("Set it to 'orders'.", ex.Message);
    }

    /// <remarks>
    /// Blank does not take this one: the mark is not whitespace, so the value is not
    /// IsNullOrWhiteSpace. Trimming leaves nothing at all, and "Set it to" has no spelling to name —
    /// so the message says what Blank would have said, because that is what the value is.
    /// </remarks>
    [Fact]
    public void Validate_ValueOfNothingButWhitespaceAndInvisibles_NamesTheEmptySpelling()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": " ﻿" } } } }
            """);

        // The distinguishing clause rather than "empty value" alone: Blank's message ends with that
        // same sentence, so asserting on it by itself would pass even if this branch never ran.
        Assert.Contains("characters with no glyph rather than a value", ex.Message);
        Assert.DoesNotContain("Set it to ''", ex.Message);
    }

    /// <remarks>
    /// An invisible in the middle survives the remedy, so the remedy cannot be typed out of the
    /// message. Saying "Set it to" anyway would be the copy-paste trap again, one character further
    /// in.
    /// </remarks>
    [Fact]
    public void Validate_InvisibleInsideTheValue_AsksForItToBeRetypedRatherThanCopied()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": " ord﻿ers" } } } }
            """);

        Assert.Contains("retype", ex.Message);
        Assert.DoesNotContain(@"Set it to 'ord﻿ers'.", ex.Message);
    }

    /// <remarks>
    /// The boundary this rule deliberately stops at: a value padded only with invisible characters
    /// and no whitespace at all is not refused. TrimUnseeable is one edit away from becoming the
    /// trigger rather than the remedy, which would widen the rule past what #236 asked for without
    /// anyone noticing. Pinned so that widening it stays a decision.
    /// </remarks>
    [Fact]
    public void Validate_InvisiblePaddingWithNoWhitespace_IsNotRefused()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "namespace": "﻿orders", "context": "dev", "port": 8080 } } } }
            """));

        var resolved = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("﻿orders", resolved.DeveloperConfig.Kubernetes.Namespace);
    }

    /// <remarks>
    /// The fields this rule deliberately does not reach. Whitespace may be real in a path or in a
    /// command's argument; a connection string may carry it inside a quoted value; and a scheme is
    /// already trimmed where it is read, into a closed set of two values where trimming cannot
    /// resolve to the wrong one. Each is a decision #236 made rather than something the mechanism
    /// guarantees, so each is pinned.
    /// </remarks>
    [Theory]
    [InlineData("""{ "services": { "orders": { "source": "local", "local": { "path": "/src/orders " } } } }""", "path", "/src/orders ")]
    [InlineData("""{ "services": { "orders": { "source": "local", "local": { "path": "/src/o", "prepare": { "command": ["mvn", " -Pprod"] } } } } }""", "command", " -Pprod")]
    [InlineData("""{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev", "port": 8080, "scheme": " https" } } } }""", "scheme", " https")]
    [InlineData("""{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev", "port": " 8080" } } } }""", "port", "8080")]
    public void Validate_FieldThatDidNotOptIn_StillTakesASurroundedValue(
        string json, string field, string expected)
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));

        var resolved = ServiceSourcesConfigCache.ResolveService(builder, "orders").DeveloperConfig;

        // Not merely "it did not throw": the value has to arrive with its whitespace intact.
        // Someone "fixing" an exclusion by trimming it at the point of use would pass a no-throw
        // test, and that is the change this pins against.
        var arrived = field switch
        {
            "path" => resolved.Local.Path,
            "command" => resolved.Local.Prepare.Command![1],
            "scheme" => resolved.Kubernetes.Scheme,
            _ => resolved.Kubernetes.Port?.ToString(CultureInfo.InvariantCulture),
        };

        Assert.Equal(expected, arrived);
    }

    [Fact]
    public void Validate_DirectConnectionString_StillTakesATrailingSpace()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=db;Password=hunter2 " } } } }
            """));

        var configured = ServiceSourcesConfigCache.BackingServicesFor(builder);

        Assert.Equal("Host=db;Password=hunter2 ", configured["orders-db"].Direct.ConnectionString);
    }

    /// <remarks>
    /// The kubernetes block's own connectionString, which sits beside three fields that <em>did</em>
    /// opt in and is therefore the one a later contributor is likeliest to add the attribute to. A
    /// connection string may carry trailing whitespace inside a quoted value, which is why #236
    /// rules it out by name.
    /// </remarks>
    [Fact]
    public void Validate_KubernetesConnectionString_StillTakesATrailingSpace()
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
            { "backingServices": { "orders-db": {
                "source": "kubernetes",
                "kubernetes": { "connectionString": "Host=localhost;Port=${port};Pwd=x " } } } }
            """));

        var configured = ServiceSourcesConfigCache.BackingServicesFor(builder);

        Assert.Equal(
            "Host=localhost;Port=${port};Pwd=x ",
            configured["orders-db"].Kubernetes.ConnectionString);
    }

    /// <remarks>
    /// Two failure modes, and one of them is invisible to a query over <c>BlockFields</c> alone.
    /// That dictionary is built from the <em>entry type's</em> own block properties, so it is
    /// exactly one level deep: <c>local.prepare</c>'s fields are not in it at all. An attribute on
    /// <c>PrepareDeveloperConfig.Mode</c> would be live — <c>CollectBlock</c> recurses into a nested
    /// block — and one on its <c>Command</c> would be inert, and neither would be visible here. So
    /// this walk descends the way the validator does, and only then asserts each carrier is a
    /// scalar the walk actually reaches.
    /// <para>
    /// This cannot guard against a property being moved between block types: attributes travel with
    /// the property. What it guards is a carrier the walk never reaches, and a field quietly losing
    /// the rule.
    /// </para>
    /// </remarks>
    [Fact]
    public void Shape_EveryFieldCarryingTheRuleIsAScalarTheWalkReaches()
    {
        static IEnumerable<PropertyInfo> Leaves(IReadOnlyDictionary<string, PropertyInfo> fields) =>
            fields.Values.SelectMany(field =>
                DeveloperConfigField.BlockFieldsOf(field.PropertyType) is { } nested
                    ? Leaves(nested).Prepend(field)
                    : [field]);

        var carriers = (
            from shape in new[] { DeveloperConfigShape.Service, DeveloperConfigShape.BackingService }
            from block in shape.BlockFields
            from field in Leaves(block.Value)
            where field.GetCustomAttribute<NoSurroundingWhitespaceAttribute>() is not null
            select field).Distinct().ToArray();

        Assert.Equal(
            new[]
            {
                "KubernetesBackingServiceDeveloperConfig.Context",
                "KubernetesBackingServiceDeveloperConfig.Namespace",
                "KubernetesBackingServiceDeveloperConfig.Service",
                "KubernetesDeveloperConfig.Context",
                "KubernetesDeveloperConfig.Namespace",
            },
            carriers.Select(c => $"{c.DeclaringType!.Name}.{c.Name}").Order(StringComparer.Ordinal));

        foreach (var carrier in carriers)
        {
            Assert.False(
                DeveloperConfigField.IsList(carrier.PropertyType),
                $"{carrier.DeclaringType!.Name}.{carrier.Name} is a list, which CollectBlock hands "
                + "to CollectList before the whitespace check is reached.");
            Assert.Null(DeveloperConfigField.BlockFieldsOf(carrier.PropertyType));
        }
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
        var blocks = DeveloperConfigShape.Service.BlockFields;

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

    /// <summary>
    /// A field whose letters were swapped is named too, which is the commonest typo of all and the
    /// one the short fields could not afford while a swap cost two edits.
    /// </summary>
    /// <remarks>
    /// <c>path</c> is four letters, so its tolerance is one edit; plain Levenshtein charges a
    /// swapped pair two, so <c>paht</c> used to print the bare list of root keys while <c>pth</c>
    /// — a dropped letter in the same word — was walked to the answer. Nothing about the rule
    /// explained the difference to whoever hit it.
    /// </remarks>
    [Theory]
    [InlineData("paht", "path", "local")]
    [InlineData("prot", "port", "kubernetes")]
    [InlineData("tga", "tag", "container")]
    [InlineData("rul", "url", "url")]
    public void Validate_TransposedFieldAtEntryRoot_NamesTheFieldAndItsBlock(
        string written, string field, string block)
    {
        var ex = Load($$"""{ "services": { "orders": { "source": "local", "{{written}}": "x" } } }""");

        Assert.Contains($"Did you mean '{field}'", ex.Message);
        Assert.Contains($"'{block}' block", ex.Message);
    }

    /// <remarks>
    /// Two edits, which only a name long enough to afford them gets: <c>namespace</c> is nine
    /// letters, where a longer word leaves more room to go wrong and one edit is stingy. The
    /// companion test below is the other half of that rule.
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

    /// <summary>
    /// The service's developer config as the package actually reads it, for the cases that have to
    /// be <em>accepted</em> — the half of a validator's job no thrown message can show.
    /// </summary>
    private static ServiceDeveloperConfig Resolve(string json)
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));
        return ServiceSourcesConfigCache.ResolveService(builder, "orders").DeveloperConfig;
    }

    [Fact]
    public void Validate_PrepareBlockInsideLocal_Binds()
    {
        var config = Resolve("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": ["./prepare.sh", "--full"], "mode": "once" } } } } }
            """);

        var prepare = config.Local.Prepare;
        Assert.NotNull(prepare);
        Assert.Equal<string[]>(["./prepare.sh", "--full"], prepare!.Command!);
        Assert.Equal("once", prepare.Mode);
        Assert.True(prepare.IsDeclared);
    }

    /// <remarks>
    /// The trap the extra level brings with it. <c>string[]</c> is a class, so a list asked about as
    /// a block is classified as one and answered with "takes a value, not a block of settings" —
    /// about a field whose value is neither. Both halves are asserted here, because the message this
    /// stops producing would have made the correct spelling unwritable.
    /// </remarks>
    [Fact]
    public void Validate_CommandList_IsNotReportedAsABlock()
    {
        var config = Resolve("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": ["make", "bootstrap"] } } } } }
            """);

        Assert.Equal<string[]>(["make", "bootstrap"], config.Local.Prepare!.Command!);
    }

    [Fact]
    public void Validate_UnknownKeyInsidePrepare_NamesTheNestedBlock()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "comand": ["./prepare.sh"] } } } } }
            """);

        Assert.Contains("'comand' is not a valid key in the 'local.prepare' block", ex.Message);
        Assert.Contains("'command'", ex.Message);
        Assert.Contains("'mode'", ex.Message);
        Assert.Contains("'windowsCommand'", ex.Message);

        // The block's rules are expressed in terms of a computed 'IsDeclared', which is a member and
        // not a key: offering it in the one sentence that exists to say what may be written would
        // name something a developer cannot set.
        Assert.DoesNotContain("isdeclared", ex.Message);
    }

    [Fact]
    public void Validate_AComputedMemberOfABlock_IsNotAValidKey()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": ["./prepare.sh"], "isDeclared": true } } } } }
            """);

        Assert.Contains("'isDeclared' is not a valid key in the 'local.prepare' block", ex.Message);
    }

    /// <summary>
    /// A null element in the command is rejected rather than silently dropped.
    /// </summary>
    /// <remarks>
    /// This one has to be caught here, because it does not survive to the reader that catches an
    /// empty list or a climbing first element: the JSON provider records the key with a null value
    /// and the binder then omits it, so the array <em>shortens</em> and every argument after it
    /// shifts down. Measured on the real provider: <c>["./prepare.sh", null, "--full"]</c> bound to
    /// two elements and the plan accepted it, so the command that ran was missing an argument the
    /// developer had written and nothing said so.
    /// </remarks>
    [Fact]
    public void Validate_ANullElementInTheCommand_IsRejected()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": ["./prepare.sh", null, "--full"] } } } } }
            """);

        Assert.Contains("'command' in the 'local.prepare' block has no value at element '1'", ex.Message);
        Assert.Contains("shifts every element after it down a place", ex.Message);
    }

    /// <remarks>
    /// An empty element is a different thing: a command may genuinely take an empty argument, and it
    /// survives the binder intact, so it is accepted where a null is refused.
    /// </remarks>
    [Fact]
    public void Validate_AnEmptyElementInTheCommand_IsAccepted()
    {
        var config = Resolve("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": ["./prepare.sh", "", "--full"] } } } } }
            """);

        Assert.Equal<string[]>(["./prepare.sh", "", "--full"], config.Local.Prepare!.Command!);
    }

    [Fact]
    public void Validate_CommandWrittenAsAScalar_IsRejected()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": "./prepare.sh" } } } } }
            """);

        Assert.Contains("'command' in the 'local.prepare' block takes a list of values", ex.Message);
        Assert.Contains("'./prepare.sh'", ex.Message);
        // The flat layers carry one leaf each, so an element is set through its index.
        Assert.Contains("__prepare__command__0", ex.Message);
    }

    [Fact]
    public void Validate_PrepareWrittenAsAValue_IsRejected()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "local": { "prepare": "./prepare.sh" } } } }
            """);

        Assert.Contains("'prepare' takes a block of settings, not a value", ex.Message);
        Assert.Contains("\"local\": { ..., \"prepare\": { ... } }", ex.Message);
    }

    [Fact]
    public void Validate_WhitespaceModeInsidePrepare_IsRejectedLikeAnyOtherField()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "mode": "   " } } } } }
            """);

        Assert.Contains("'mode' in the 'local.prepare' block is set to", ex.Message);
        Assert.Contains("whitespace rather than a value", ex.Message);
    }

    /// <remarks>
    /// An empty value is how a higher configuration layer drops what the file below set — the one
    /// gesture it has — so it has to reach the mode parse as absent rather than as a value nobody
    /// wrote, one level down as much as at the top.
    /// </remarks>
    [Fact]
    public void Validate_EmptyModeInsidePrepare_ReadsAsAbsent()
    {
        var config = Resolve("""
            { "services": { "orders": { "source": "local", "local": {
                "prepare": { "command": ["./prepare.sh"], "mode": "" } } } } }
            """);

        Assert.Null(config.Local.Prepare!.Mode);
    }

    /// <remarks>
    /// Inert rather than an error, which is the point of the per-source block layout: a
    /// <c>prepare</c> block under a service resolved through another source is a key inside a block
    /// nothing reads, so a higher layer switching the source away cannot leave a step behind it.
    /// </remarks>
    [Fact]
    public void Validate_PrepareUnderANonLocalSource_IsInert()
    {
        var config = Resolve("""
            { "services": { "orders": { "source": "container", "container": { "tag": "1.2.3" },
                "local": { "prepare": { "command": ["./prepare.sh"] } } } } }
            """);

        Assert.Equal("container", config.Source);
    }

    /// <remarks>
    /// The field is now a valid key one level down, so the entry root can point at where it goes
    /// rather than listing keys that cannot contain the word the developer was reaching for.
    /// </remarks>
    [Fact]
    public void Validate_PrepareAtTheEntryRoot_NamesTheBlockItBelongsUnder()
    {
        var ex = Load("""
            { "services": { "orders": { "source": "local", "prepare": { "command": ["./prepare.sh"] } } } }
            """);

        Assert.Contains("'prepare' is not a valid key here", ex.Message);
        Assert.Contains("'local' block", ex.Message);
    }
}
