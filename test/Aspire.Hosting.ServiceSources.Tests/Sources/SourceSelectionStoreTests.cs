using System.Text;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
public class SourceSelectionStoreTests
{
    private static string NewAppHost() => TempDirectories.CreateSubdirectory().FullName;

    private static string SelectionPath(string appHost) =>
        Path.Combine(ToolDirectory.PathIn(appHost), SourceSelectionStore.FileName);

    private static string Plant(string appHost, string content)
    {
        Directory.CreateDirectory(ToolDirectory.PathIn(appHost));
        var path = SelectionPath(appHost);
        File.WriteAllText(path, content);

        return path;
    }

    [Fact]
    public void Read_MissingFile_IsMissingAndEmpty()
    {
        var (decisions, state) = SourceSelectionStore.Read(NewAppHost());

        Assert.Equal(SelectionFileState.Missing, state);
        Assert.Empty(decisions);
    }

    [Fact]
    public void Read_ValidFile_ReturnsDecisions()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 1, "services": { "orders": { "start": true }, "billing": { "start": false } } }""");

        var (decisions, state) = SourceSelectionStore.Read(host);

        Assert.Equal(SelectionFileState.Valid, state);
        Assert.True(decisions["orders"]);
        Assert.False(decisions["billing"]);
    }

    [Theory]
    [InlineData("""{ "version": 1, "services": { "orders": { "start": "true" } } }""")]
    [InlineData("""{ "version": 1, "services": { "orders": { "start": 1 } } }""")]
    [InlineData("""{ "version": 1, "services": { "orders": { "start": null } } }""")]
    [InlineData("""{ "version": 1, "services": { "orders": { } } }""")]
    [InlineData("""{ "version": 1, "services": { "orders": true } }""")]
    [InlineData("""{ "version": 1, "services": [] }""")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "version": "1", "services": { } }""")]
    [InlineData("""{ "version": 1.5, "services": { } }""")]
    [InlineData("""{ "version": 0, "services": { } }""")]
    [InlineData("""{ "services": { } }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Read_MalformedFile_IsInvalidAndEmpty(string content)
    {
        var host = NewAppHost();
        Plant(host, content);

        var (decisions, state) = SourceSelectionStore.Read(host);

        Assert.Equal(SelectionFileState.Invalid, state);
        Assert.Empty(decisions);
    }

    [Fact]
    public void Read_NewerVersion_IsNewerAndEmpty()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 2, "services": { "orders": { "start": true } } }""");

        var (decisions, state) = SourceSelectionStore.Read(host);

        Assert.Equal(SelectionFileState.Newer, state);
        Assert.Empty(decisions);
    }

    [Fact]
    public void Read_DuplicateKeys_LastWins()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 1, "services": { "orders": { "start": true }, "orders": { "start": false } } }""");

        var (decisions, _) = SourceSelectionStore.Read(host);

        Assert.False(decisions["orders"]);
    }

    [Fact]
    public void Read_KeysMatchIgnoringCase()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 1, "services": { "Orders": { "start": false } } }""");

        var (decisions, _) = SourceSelectionStore.Read(host);

        Assert.False(decisions["orders"]);
    }

    [Fact]
    public void Read_UnknownFieldsInAnEntry_AreIgnored()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 1, "services": { "orders": { "start": true, "note": "x" } } }""");

        var (decisions, state) = SourceSelectionStore.Read(host);

        Assert.Equal(SelectionFileState.Valid, state);
        Assert.True(decisions["orders"]);
    }

    [Fact]
    public void Read_FileOverTheSizeLimit_IsInvalid()
    {
        var host = NewAppHost();
        var json = """{ "version": 1, "services": { } }""";
        Plant(host, json + new string(' ', SourceSelectionStore.MaxBytes + 1 - json.Length));

        Assert.Equal(SelectionFileState.Invalid, SourceSelectionStore.Read(host).State);
    }

    [Fact]
    public void Read_FileExactlyAtTheSizeLimit_IsValid()
    {
        var host = NewAppHost();
        var json = """{ "version": 1, "services": { } }""";
        Plant(host, json + new string(' ', SourceSelectionStore.MaxBytes - json.Length));

        Assert.Equal(SelectionFileState.Valid, SourceSelectionStore.Read(host).State);
    }

    [Fact]
    public void Read_DirectoryAtThePath_IsInvalid()
    {
        var host = NewAppHost();
        Directory.CreateDirectory(SelectionPath(host));

        Assert.Equal(SelectionFileState.Invalid, SourceSelectionStore.Read(host).State);
    }

    [Fact]
    public void Read_SymbolicLinkAtThePath_IsInvalid()
    {
        var host = NewAppHost();
        var target = Path.Combine(host, "real.json");
        File.WriteAllText(target, """{ "version": 1, "services": { } }""");
        Directory.CreateDirectory(ToolDirectory.PathIn(host));

        try
        {
            File.CreateSymbolicLink(SelectionPath(host), target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // creating links needs a privilege this machine does not grant
        }

        Assert.Equal(SelectionFileState.Invalid, SourceSelectionStore.Read(host).State);
    }

    [Fact]
    public void TrySave_CreatesTheFileAndTheGitignore()
    {
        var host = NewAppHost();

        var saved = SourceSelectionStore.TrySave(
            host, new Dictionary<string, bool> { ["orders"] = true, ["billing"] = false });

        Assert.True(saved);
        Assert.True(File.Exists(Path.Combine(ToolDirectory.PathIn(host), ".gitignore")));

        var (decisions, state) = SourceSelectionStore.Read(host);
        Assert.Equal(SelectionFileState.Valid, state);
        Assert.True(decisions["orders"]);
        Assert.False(decisions["billing"]);
        Assert.Contains("\"version\": 1", File.ReadAllText(SelectionPath(host)));
    }

    [Fact]
    public void TrySave_MergesWithExistingEntries_AndPreservesUndeclaredNames()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 1, "services": { "gone": { "start": false }, "orders": { "start": false } } }""");

        Assert.True(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = true }));

        var (decisions, _) = SourceSelectionStore.Read(host);
        Assert.True(decisions["orders"]);
        Assert.False(decisions["gone"]);
    }

    [Fact]
    public void TrySave_WritesTheCatalogSpelling_OverADifferentlyCasedKey()
    {
        var host = NewAppHost();
        Plant(host, """{ "version": 1, "services": { "orders": { "start": false } } }""");

        Assert.True(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["Orders"] = true }));

        var text = File.ReadAllText(SelectionPath(host));
        Assert.Contains("\"Orders\"", text);
        Assert.DoesNotContain("\"orders\"", text);
    }

    [Fact]
    public void TrySave_NewerFile_IsNeverOverwritten()
    {
        var host = NewAppHost();
        var path = Plant(host, """{ "version": 2, "services": { "orders": { "start": true } } }""");
        var before = File.ReadAllText(path);

        Assert.False(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = false }));

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void TrySave_InvalidFile_IsReplaced()
    {
        var host = NewAppHost();
        Plant(host, "garbage");

        Assert.True(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = true }));

        var (decisions, state) = SourceSelectionStore.Read(host);
        Assert.Equal(SelectionFileState.Valid, state);
        Assert.True(decisions["orders"]);
    }

    [Fact]
    public void TrySave_ToolDirectoryThatCannotBeCreated_ReturnsFalse()
    {
        var host = NewAppHost();
        File.WriteAllText(ToolDirectory.PathIn(host), "a file where the directory should be");

        Assert.False(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = true }));
    }

    [Fact]
    public void TrySave_DirectoryAtThePath_ReturnsFalseAndLeavesNoScratchFile()
    {
        var host = NewAppHost();
        Directory.CreateDirectory(SelectionPath(host));

        Assert.False(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = true }));

        Assert.Empty(Directory.GetFiles(ToolDirectory.PathIn(host), ".incoming-*"));
    }

    [Fact]
    public void TrySave_TwoSaves_MergeAndTheLastWriterWinsPerKey()
    {
        var host = NewAppHost();

        Assert.True(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = true, ["billing"] = true }));
        Assert.True(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = false }));

        var (decisions, _) = SourceSelectionStore.Read(host);
        Assert.False(decisions["orders"]);
        Assert.True(decisions["billing"]);
    }

    [Fact]
    public void TrySave_WritesUtf8WithoutByteOrderMark()
    {
        var host = NewAppHost();
        Assert.True(SourceSelectionStore.TrySave(host, new Dictionary<string, bool> { ["orders"] = true }));

        var bytes = File.ReadAllBytes(SelectionPath(host));
        Assert.NotEqual(Encoding.UTF8.GetPreamble(), bytes.Take(3).ToArray());
    }
}
