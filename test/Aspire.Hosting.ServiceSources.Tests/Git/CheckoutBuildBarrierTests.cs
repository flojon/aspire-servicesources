using System.Xml.Linq;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The barrier files stop MSBuild's and NuGet's upward directory walk at the tool-owned
/// <c>.servicesources</c> directory, so a checkout underneath it builds under its own repository's
/// settings instead of the AppHost repository's.
/// </summary>
public class CheckoutBuildBarrierTests
{
    private static string NewToolDirectory()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory().FullName, ".servicesources");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Ensure_WritesAnEmptyDirectoryBuildPropsAndTargets()
    {
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            var document = XDocument.Load(Path.Combine(dir, name));
            Assert.Equal("Project", document.Root!.Name.LocalName);
            Assert.Empty(document.Root.Elements());
        }
    }

    [Fact]
    public void Ensure_WritesADirectoryPackagesPropsThatTurnsOffCentralPackageManagement()
    {
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(Path.Combine(dir, "Directory.Packages.props"));
        var value = document.Root!
            .Elements("PropertyGroup")
            .Elements("ManagePackageVersionsCentrally")
            .Single()
            .Value;
        Assert.Equal("false", value);
    }

    [Fact]
    public void Ensure_WritesANuGetConfigThatClearsTheInheritedPackageSourceMapping()
    {
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(Path.Combine(dir, "nuget.config"));
        Assert.Equal("configuration", document.Root!.Name.LocalName);
        var mapping = document.Root.Elements("packageSourceMapping").Single();
        Assert.Single(mapping.Elements("clear"));
    }

    [Fact]
    public void Ensure_WritesANuGetConfigThatLeavesInheritedPackageSourcesAlone()
    {
        // Clearing the mapping is permissive — it lifts a restriction. Clearing the *sources* would
        // remove feeds the checkout may need, so the barrier must not do it.
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(Path.Combine(dir, "nuget.config"));
        Assert.Empty(document.Root!.Elements("packageSources"));
    }

    [Fact]
    public void Ensure_RunAgainOverItsOwnOutput_DoesNotRewriteTheFiles()
    {
        var dir = NewToolDirectory();
        CheckoutBuildBarrier.Ensure(dir);
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var files = Directory.GetFiles(dir);
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, stamp);
        }

        CheckoutBuildBarrier.Ensure(dir);

        foreach (var file in files)
        {
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        }
    }

    [Fact]
    public void Ensure_StaleContentFromAnEarlierVersion_IsReplaced()
    {
        // Unlike .gitignore, whose content is fixed forever, barrier content can change between
        // versions — "already there, leave it" would strand a checkout on a stale copy.
        var dir = NewToolDirectory();
        var propsPath = Path.Combine(dir, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project />");

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(propsPath);
        Assert.Equal(
            "false",
            document.Root!.Elements("PropertyGroup").Elements("ManagePackageVersionsCentrally").Single().Value);
    }

    [Fact]
    public void Ensure_ConcurrentCalls_AllLeaveCompleteAndReadableFiles()
    {
        var dir = NewToolDirectory();

        Parallel.For(0, 16, _ => CheckoutBuildBarrier.Ensure(dir));

        foreach (var name in new[]
                 {
                     "Directory.Build.props",
                     "Directory.Build.targets",
                     "Directory.Packages.props",
                     "nuget.config",
                 })
        {
            var document = XDocument.Load(Path.Combine(dir, name));
            Assert.NotNull(document.Root);
        }
    }
}
