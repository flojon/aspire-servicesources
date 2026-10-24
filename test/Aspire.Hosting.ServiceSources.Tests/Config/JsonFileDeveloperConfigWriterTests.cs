using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class JsonFileDeveloperConfigWriterTests
{
    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

    private static string NewDirectory() => TempDirectories.CreateSubdirectory().FullName;

    private static string FileIn(string dir) => Path.Combine(dir, DeveloperConfiguration.FileName);

    private static JsonObject ReadRoot(string dir) => JsonNode.Parse(File.ReadAllText(FileIn(dir)))!.AsObject();

    private static Task<SaveResult> Save(
        string dir, string service, string source, IReadOnlyDictionary<string, string>? fields = null) =>
        new JsonFileDeveloperConfigWriter(dir).SaveAsync(service, source, fields ?? NoFields, CancellationToken.None);

    [Fact]
    public async Task Save_NoFile_CreatesItWithServicesEntry()
    {
        var dir = NewDirectory();

        var result = await Save(dir, "orders", "repository");

        Assert.True(result.Saved);
        Assert.Null(result.Error);
        Assert.Equal("repository", (string?)ReadRoot(dir)["services"]!["orders"]!["source"]);
    }

    [Fact]
    public async Task Save_ExistingEntries_ArePreserved()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """
            {
              "repositoryRoot": "../..",
              "services": { "billing": { "source": "path", "path": { "path": "../billing" } } },
              "backingServices": { "db": { "source": "container" } },
              "repositories": { "mono": { "ref": "main" } }
            }
            """);

        var result = await Save(dir, "orders", "repository");

        Assert.True(result.Saved);
        var root = ReadRoot(dir);
        Assert.Equal("../..", (string?)root["repositoryRoot"]);
        Assert.Equal("../billing", (string?)root["services"]!["billing"]!["path"]!["path"]);
        Assert.Equal("container", (string?)root["backingServices"]!["db"]!["source"]);
        Assert.Equal("main", (string?)root["repositories"]!["mono"]!["ref"]);
        Assert.Equal("repository", (string?)root["services"]!["orders"]!["source"]);
    }

    [Fact]
    public async Task Save_DifferentlyCasedExistingKey_UpdatesThatEntry()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "services": { "Orders": { "source": "path" } } }""");

        var result = await Save(dir, "orders", "repository");

        Assert.True(result.Saved);
        var services = ReadRoot(dir)["services"]!.AsObject();
        var entry = Assert.Single(services);
        Assert.Equal("Orders", entry.Key);
        Assert.Equal("repository", (string?)entry.Value!["source"]);
    }

    [Fact]
    public async Task Save_FieldsWithColon_BecomeNestedObjectsAndKeepSiblings()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "services": { "orders": { "kubernetes": { "namespace": "ns" } } } }""");

        var result = await Save(dir, "orders", "kubernetes", new Dictionary<string, string> { ["kubernetes:context"] = "x" });

        Assert.True(result.Saved);
        var orders = ReadRoot(dir)["services"]!["orders"]!;
        Assert.Equal("kubernetes", (string?)orders["source"]);
        Assert.Equal("x", (string?)orders["kubernetes"]!["context"]);
        Assert.Equal("ns", (string?)orders["kubernetes"]!["namespace"]);
    }

    [Fact]
    public async Task Save_RoundTrip_ReloadsThroughDeveloperConfigLoader()
    {
        var dir = NewDirectory();

        var result = await Save(dir, "orders", "kubernetes", new Dictionary<string, string> { ["kubernetes:context"] = "x" });

        Assert.True(result.Saved);
        var builder = TestHelpers.CreateBuilder(dir);
        var config = DeveloperConfiguration.ReadFrom(builder, ["orders"], []);
        var entry = config.Services["orders"];
        Assert.Equal("kubernetes", entry.Source);
        Assert.Equal("x", entry.Kubernetes.Context);
    }

    [Fact]
    public async Task Save_MalformedFile_ReturnsErrorAndLeavesFileByteIdentical()
    {
        var dir = NewDirectory();
        var bytes = "{ \"services\": { oops"u8.ToArray();
        File.WriteAllBytes(FileIn(dir), bytes);

        var result = await Save(dir, "orders", "repository");

        Assert.False(result.Saved);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(bytes, File.ReadAllBytes(FileIn(dir)));
    }

    [Fact]
    public async Task Save_RootNotObject_ReturnsError()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), "[1, 2]");

        var result = await Save(dir, "orders", "repository");

        Assert.False(result.Saved);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("[1, 2]", File.ReadAllText(FileIn(dir)));
    }

    [Fact]
    public async Task Save_ServiceNameWithColonAndDot_WrittenAsLiteralKey()
    {
        var dir = NewDirectory();

        var result = await Save(dir, "a:b.c", "repository");

        Assert.True(result.Saved);
        var services = ReadRoot(dir)["services"]!.AsObject();
        Assert.Equal("repository", (string?)services["a:b.c"]!["source"]);
    }

    [Fact]
    public async Task Save_ConcurrentCalls_BothSurvive()
    {
        var dir = NewDirectory();

        // Separate writer instances, as two pickers in one process would have.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => Task.Run(() => Save(dir, $"svc{i}", "repository"))));

        Assert.All(results, r => Assert.True(r.Saved, r.Error));
        var services = ReadRoot(dir)["services"]!.AsObject();
        Assert.Equal(8, services.Count);
    }

    [Theory]
    [InlineData("// note\n{ \"services\": {} }")]
    [InlineData("{ \"services\": { /* keep */ } }")]
    public async Task Save_CommentedFile_ReturnsErrorAndLeavesFileByteIdentical(string content)
    {
        var dir = NewDirectory();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(FileIn(dir), bytes);

        var result = await Save(dir, "orders", "repository");

        Assert.False(result.Saved);
        Assert.Contains(DeveloperConfiguration.FileName, result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(FileIn(dir)));
    }

    [Fact]
    public async Task Save_FileWithBom_Saves()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "repositoryRoot": "x" }""", new System.Text.UTF8Encoding(true));

        var result = await Save(dir, "orders", "repository");

        Assert.True(result.Saved, result.Error);
        var root = ReadRoot(dir);
        Assert.Equal("x", (string?)root["repositoryRoot"]);
        Assert.Equal("repository", (string?)root["services"]!["orders"]!["source"]);
    }

    [Fact]
    public async Task Save_TrailingComma_Saves()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "repositoryRoot": "x", }""");

        var result = await Save(dir, "orders", "repository");

        Assert.True(result.Saved, result.Error);
        Assert.Equal("x", (string?)ReadRoot(dir)["repositoryRoot"]);
    }

    [Fact]
    public async Task Save_ExistingValues_AreNotEscapedAndFileEndsWithNewline()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "repositoryRoot": "åäö <a&b> it's +" }""");

        var result = await Save(dir, "orders", "repository");

        Assert.True(result.Saved, result.Error);
        var text = File.ReadAllText(FileIn(dir));
        Assert.Contains("\"åäö <a&b> it's +\"", text);
        Assert.EndsWith(Environment.NewLine, text);
    }

    [Fact]
    public async Task Save_ServicesNotObject_ReturnsErrorAndLeavesFileUnchanged()
    {
        var dir = NewDirectory();
        const string Content = """{ "services": "oops" }""";
        File.WriteAllText(FileIn(dir), Content);

        var result = await Save(dir, "orders", "repository");

        Assert.False(result.Saved);
        Assert.Equal(Content, File.ReadAllText(FileIn(dir)));
    }

    [Fact]
    public async Task Save_ServiceEntryNotObject_ReturnsError()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "services": { "orders": "oops" } }""");

        var result = await Save(dir, "orders", "repository");

        Assert.False(result.Saved);
    }

    [Fact]
    public async Task Save_ScalarWhereFieldBlockGoes_IsReplacedByTheBlock()
    {
        var dir = NewDirectory();
        File.WriteAllText(FileIn(dir), """{ "services": { "orders": { "kubernetes": "oops" } } }""");

        var result = await Save(dir, "orders", "kubernetes", new Dictionary<string, string> { ["kubernetes:context"] = "x" });

        Assert.True(result.Saved, result.Error);
        Assert.Equal("x", (string?)ReadRoot(dir)["services"]!["orders"]!["kubernetes"]!["context"]);
    }

    [Fact]
    public async Task Save_OversizedFile_ReturnsErrorAndLeavesFileUnchanged()
    {
        var dir = NewDirectory();
        var content = "{ \"repositoryRoot\": \"" + new string('a', JsonFileDeveloperConfigWriter.MaxBytes) + "\" }";
        File.WriteAllText(FileIn(dir), content);

        var result = await Save(dir, "orders", "repository");

        Assert.False(result.Saved);
        Assert.Equal(content, File.ReadAllText(FileIn(dir)));
    }
}
