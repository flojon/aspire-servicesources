using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
public class BuildGroupKeyTests
{
    private static string Repo(bool gitFile = false)
    {
        var root = TempDirectories.CreateSubdirectory().FullName;

        if (gitFile)
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
        }

        return root;
    }

    private static string Dir(string root, string relative) =>
        Directory.CreateDirectory(Path.Combine(root, relative)).FullName;

    private static string KeyOf(string dir, string? group = null, Func<string?>? configuredRoot = null) =>
        BuildGroupKey.For(dir, group, configuredRoot ?? (() => null));

    [Fact]
    public void For_TwoDirectoriesUnderOneGitRoot_ShareAKey()
    {
        var root = Repo();

        Assert.Equal(KeyOf(Dir(root, "src/a")), KeyOf(Dir(root, "src/b")));
    }

    [Fact]
    public void For_DirectoriesUnderDifferentGitRoots_DifferInKey()
    {
        Assert.NotEqual(KeyOf(Dir(Repo(), "a")), KeyOf(Dir(Repo(), "a")));
    }

    [Fact]
    public void For_GitFileRatherThanDirectory_CountsAsARoot()
    {
        var root = Repo(gitFile: true);

        Assert.Equal(KeyOf(Dir(root, "a")), KeyOf(Dir(root, "b")));
    }

    [Fact]
    public void For_NoGitButConfiguredRootThatContainsTheDirectory_UsesIt()
    {
        var root = TempDirectories.CreateSubdirectory().FullName;

        Assert.Equal(KeyOf(Dir(root, "a"), configuredRoot: () => root), KeyOf(Dir(root, "b"), configuredRoot: () => root));
    }

    [Fact]
    public void For_NoGitAndNoConfiguredRoot_TheServiceDirectoryItselfIsTheKey()
    {
        var root = TempDirectories.CreateSubdirectory().FullName;
        var a = Dir(root, "a");

        Assert.Equal(KeyOf(a), KeyOf(a + Path.DirectorySeparatorChar));
        Assert.NotEqual(KeyOf(a), KeyOf(Dir(root, "b")));
    }

    [Fact]
    public void For_ConfiguredRootThatIsNotAnAncestor_FallsBackToTheServiceDirectory()
    {
        var unrelated = TempDirectories.CreateSubdirectory().FullName;
        var a = TempDirectories.CreateSubdirectory().FullName;
        var b = TempDirectories.CreateSubdirectory().FullName;

        Assert.NotEqual(KeyOf(a, configuredRoot: () => unrelated), KeyOf(b, configuredRoot: () => unrelated));
    }

    [Fact]
    public void For_ConfiguredRootThatThrows_FallsBackToTheServiceDirectory()
    {
        var a = TempDirectories.CreateSubdirectory().FullName;

        var key = KeyOf(a, configuredRoot: () => throw ServiceSourcesConfigurationException.For($"bad root"));

        Assert.Equal(KeyOf(a), key);
    }

    [Fact]
    public void For_ExplicitGroup_BeatsDerivation()
    {
        var a = Dir(Repo(), "a");
        var b = Dir(Repo(), "b");

        Assert.Equal(KeyOf(a, "web"), KeyOf(b, "web"));
        Assert.NotEqual(KeyOf(a, "web"), KeyOf(a));
    }

    [Fact]
    public void For_ExplicitGroupNamesDifferOnCase_AreDifferentKeys()
    {
        var a = Dir(Repo(), "a");

        Assert.False(BuildGroupKey.Comparer.Equals(KeyOf(a, "Web"), KeyOf(a, "web")));
    }

    [Fact]
    public void For_ExplicitGroupWhosNameEqualsAPath_NeverEqualsThePathKey()
    {
        var a = Dir(Repo(), "a");
        var derived = KeyOf(a);

        var explicitKey = KeyOf(a, derived.Substring(2));

        Assert.False(BuildGroupKey.Comparer.Equals(derived, explicitKey));
    }

    [Fact]
    public void Comparer_PathKeys_FollowThePlatformCaseRule()
    {
        var comparer = BuildGroupKey.Comparer;

        Assert.Equal(OperatingSystem.IsWindows(), comparer.Equals("p:C:/Repo", "p:c:/repo"));
        Assert.True(comparer.Equals("p:/repo", "p:/repo"));
        Assert.Equal(comparer.GetHashCode("p:/repo"), comparer.GetHashCode("p:/repo"));
    }
}
