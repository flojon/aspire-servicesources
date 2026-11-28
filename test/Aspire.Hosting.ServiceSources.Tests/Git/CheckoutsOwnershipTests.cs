using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class CheckoutsOwnershipTests
{
    private static (string Root, string AppHost, CheckoutsLocation Location) NewLocation()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var root = Path.Combine(temp, "ss");
        var appHost = Path.Combine(temp, "apphost");
        return (root, appHost, CheckoutsLocation.Resolve(appHost, root));
    }

    private static string RecordFor(string appHost) =>
        System.Text.Json.JsonSerializer.Serialize(new { appHost });

    /// <summary>Makes a directory link, or returns false where this account may not.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void Absent_owned_directory_is_created_with_a_record()
    {
        var (_, appHost, location) = NewLocation();

        var owned = location.EnsureOwned();

        Assert.Equal(location.OwnedDirectory, owned);
        var record = File.ReadAllText(Path.Combine(owned, CheckoutsLocation.OwnerFileName));
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(appHost).Trim('"'), record);
    }

    [Fact]
    public void Directory_with_this_apphosts_record_is_used()
    {
        var (_, appHost, location) = NewLocation();
        Directory.CreateDirectory(location.OwnedDirectory);
        File.WriteAllText(Path.Combine(location.OwnedDirectory, CheckoutsLocation.OwnerFileName), RecordFor(appHost));
        File.WriteAllText(Path.Combine(location.OwnedDirectory, "keep.txt"), "x");

        Assert.Equal(location.OwnedDirectory, location.EnsureOwned());
        Assert.True(File.Exists(Path.Combine(location.OwnedDirectory, "keep.txt")));
    }

    [Fact]
    public void File_at_the_hash_path_is_refused_and_untouched()
    {
        var (root, _, location) = NewLocation();
        Directory.CreateDirectory(root);
        File.WriteAllText(location.OwnedDirectory, "not a directory");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => location.EnsureOwned());

        Assert.Contains("ServiceSources:CheckoutsDirectory", ex.Message);
        Assert.Equal("not a directory", File.ReadAllText(location.OwnedDirectory));
    }

    [Fact]
    public void Reparse_point_is_refused()
    {
        var (root, appHost, location) = NewLocation();
        var target = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, CheckoutsLocation.OwnerFileName), RecordFor(appHost));
        Directory.CreateDirectory(root);
        if (!TryLink(location.OwnedDirectory, target))
        {
            return;
        }

        Assert.Throws<ServiceSourcesConfigurationException>(() => location.EnsureOwned());

        Assert.Equal([CheckoutsLocation.OwnerFileName], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void Directory_without_a_record_is_refused_and_nothing_written_into_it()
    {
        var (_, _, location) = NewLocation();
        Directory.CreateDirectory(Path.Combine(location.OwnedDirectory, "checkouts", "orders", ".git"));

        Assert.Throws<ServiceSourcesConfigurationException>(() => location.EnsureOwned());

        Assert.Equal(["checkouts"], Directory.GetFileSystemEntries(location.OwnedDirectory).Select(Path.GetFileName));
        Assert.True(Directory.Exists(Path.Combine(location.OwnedDirectory, "checkouts", "orders", ".git")));
    }

    [Fact]
    public void Directory_with_another_apphosts_record_is_refused()
    {
        var (_, appHost, location) = NewLocation();
        Directory.CreateDirectory(location.OwnedDirectory);
        File.WriteAllText(
            Path.Combine(location.OwnedDirectory, CheckoutsLocation.OwnerFileName), RecordFor(appHost + "-other"));

        Assert.Throws<ServiceSourcesConfigurationException>(() => location.EnsureOwned());

        Assert.Equal([CheckoutsLocation.OwnerFileName], Directory.GetFileSystemEntries(location.OwnedDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public void Root_that_is_a_file_gives_a_configuration_error_naming_the_key()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var root = Path.Combine(temp, "ss");
        File.WriteAllText(root, "x");
        var location = CheckoutsLocation.Resolve(Path.Combine(temp, "apphost"), root);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => location.EnsureOwned());

        Assert.Contains("ServiceSources:CheckoutsDirectory", ex.Message);
    }

    [Fact]
    public void Gitignore_and_barrier_files_land_in_the_owned_directory_not_the_root()
    {
        var (root, _, location) = NewLocation();

        location.EnsureOwned();

        Assert.True(File.Exists(Path.Combine(location.OwnedDirectory, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(location.OwnedDirectory, "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(root, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(root, "Directory.Build.props")));
        Assert.Equal([CheckoutsLocation.HashOf(location.AppHostDirectory)], Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
    }

    [Fact]
    public void Default_location_still_writes_gitignore_under_servicesources()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        var location = CheckoutsLocation.Default(appHost);

        var owned = location.EnsureOwned();

        Assert.Equal(Path.Combine(appHost, ".servicesources"), owned);
        Assert.True(File.Exists(Path.Combine(owned, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(owned, CheckoutsLocation.OwnerFileName)));
    }

    [Fact]
    public void Crash_leftover_tmp_directory_does_not_block_use()
    {
        var (root, _, location) = NewLocation();
        Directory.CreateDirectory(Path.Combine(root, CheckoutsLocation.HashOf(location.AppHostDirectory) + ".tmp-deadbeef"));

        Assert.Equal(location.OwnedDirectory, location.EnsureOwned());
    }

    [Fact]
    public void A_problem_location_throws_before_anything_is_written()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var location = CheckoutsLocation.Resolve(Path.Combine(temp, "apphost"), "relative");

        Assert.Throws<ServiceSourcesConfigurationException>(() => location.EnsureOwned());

        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }
}

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class CheckoutsOwnershipConcurrencyTests
{
    [Fact]
    public async Task Parallel_first_use_of_one_apphost_never_refuses()
    {
        for (var round = 0; round < 10; round++)
        {
            var temp = TempDirectories.CreateSubdirectory().FullName;
            var location = CheckoutsLocation.Resolve(Path.Combine(temp, "apphost"), Path.Combine(temp, "ss"));
            using var gate = new ManualResetEventSlim(false);

            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                gate.Wait();
                return location.EnsureOwned();
            })).ToArray();
            gate.Set();

            var owned = await Task.WhenAll(tasks);

            Assert.All(owned, o => Assert.Equal(location.OwnedDirectory, o));
        }
    }
}
