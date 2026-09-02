using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

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
    /// Blanking a key is the only gesture configuration offers for dropping a value a lower layer
    /// set, and it is whitespace-tolerant for every string field. The binder does not extend that to
    /// a number, so the one spelling that does work is named rather than left to be guessed at.
    /// </remarks>
    [Fact]
    public void Validate_WhitespaceWhereANumberGoes_NamesTheSpellingThatUnsetsIt()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "kubernetes",
                "kubernetes": { "port": " " } } } }
            """);

        Assert.Contains("takes a whole number", ex.Message);
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
}
