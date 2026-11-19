using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>The path-length refusals say what to change, which depends on where checkouts live.</summary>
public class CloneLayoutMessageTests
{
    private static readonly Raw Label = Raw.Literal("Service 'orders'");

    private static readonly string Drive = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static readonly CheckoutsLocation Default = CheckoutsLocation.Default(Path.Combine(Drive, "apphost"));

    private static CheckoutsLocation External(int rootLength) =>
        CheckoutsLocation.Resolve(Path.Combine(Drive, "apphost"), Drive + new string('s', rootLength - Drive.Length));

    private static string PathOfLength(int length) => Drive + new string('a', length - Drive.Length);

    private static CloneLayoutOptions Usable => new("gitroot", IsWindows: true);

    private static CloneLayoutOptions NoGitRoot => new(null, IsWindows: true);

    private static string Refusal(string root, CloneLayoutOptions options, CheckoutsLocation location) =>
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, options, location)).Message;

    [Fact]
    public void Default_location_refusals_suggest_CheckoutsDirectory_and_its_env_var_and_C_ss()
    {
        var tooDeep = Refusal(PathOfLength(GitPathLimit.MaxWorkingTreeRootLength + 1), Usable, Default);
        var noGitRoot = Refusal(PathOfLength(GitPathLimit.MaxInPlaceRootLength + 1), NoGitRoot, Default);

        Assert.All(new[] { tooDeep, noGitRoot }, message =>
        {
            Assert.Contains("ServiceSources:CheckoutsDirectory", message);
            Assert.Contains("ServiceSources__CheckoutsDirectory", message);
            Assert.Contains(@"C:\ss", message);
            Assert.Contains("Move the AppHost", message);
        });
    }

    [Fact]
    public void External_location_refusals_say_shorten_CheckoutsDirectory_and_do_not_say_move_the_AppHost()
    {
        var location = External(240);
        var tooDeep = Refusal(PathOfLength(GitPathLimit.MaxWorkingTreeRootLength + 1), Usable, location);
        var noGitRoot = Refusal(PathOfLength(GitPathLimit.MaxInPlaceRootLength + 1), NoGitRoot, location);

        Assert.All(new[] { tooDeep, noGitRoot }, message =>
        {
            Assert.Contains("Shorten 'ServiceSources:CheckoutsDirectory'", message);
            Assert.DoesNotContain("Move the AppHost", message);
        });
        Assert.All(new[] { tooDeep, noGitRoot }, message => Assert.Contains("the tool appends", message));
    }

    [Fact]
    public void Staging_adds_nineteen_characters_to_a_checkouts_directory()
    {
        Assert.Equal(19, GitPathLimit.StagingInCheckoutsLength);
        Assert.Equal(216, GitPathLimit.MaxCheckoutsRoot(GitPathLimit.MaxWorkingTreeRootLength));
    }

    [Theory]
    [InlineData(215, true)]
    [InlineData(216, false)]
    [InlineData(235, false)]
    public void Long_external_root_decides_separate_or_refused_by_its_real_length(int length, bool inPlace)
    {
        var root = PathOfLength(length);

        Assert.Equal(inPlace ? CloneLayoutKind.InPlace : CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, Usable, External(100)));
    }

    [Fact]
    public void Long_external_root_over_the_working_tree_limit_is_refused()
    {
        var root = PathOfLength(236);

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Usable, External(100)));
    }
}
