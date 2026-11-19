using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class CheckoutsLocationTests
{
    private const string Key = "ServiceSources:CheckoutsDirectory";

    private static readonly string Drive = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string Abs(params string[] segments) => Path.Combine([Drive, .. segments]);

    private static string AppHost => Abs("work", "apphost");

    [Fact]
    public void Unset_gives_default_layout()
    {
        var location = CheckoutsLocation.Resolve(AppHost, null);

        Assert.Equal(Path.Combine(AppHost, ".servicesources"), location.OwnedDirectory);
        Assert.Equal(Path.Combine(AppHost, ".servicesources", "checkouts"), location.CheckoutsRoot);
        Assert.False(location.IsExternal);
        Assert.Null(location.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_is_unset(string value)
    {
        var location = CheckoutsLocation.Resolve(AppHost, value);

        Assert.Equal(Path.Combine(AppHost, ".servicesources"), location.OwnedDirectory);
        Assert.False(location.IsExternal);
        Assert.Null(location.Problem);
    }

    [Fact]
    public void Absolute_value_gives_hashed_layout()
    {
        var root = Abs("ss");

        var location = CheckoutsLocation.Resolve(AppHost, root);

        var hash = CheckoutsLocation.HashOf(AppHost);
        Assert.Equal(10, hash.Length);
        Assert.Equal(Path.Combine(root, hash), location.OwnedDirectory);
        Assert.Equal(Path.Combine(root, hash, "checkouts"), location.CheckoutsRoot);
        Assert.True(location.IsExternal);
        Assert.Null(location.Problem);
        Assert.Equal(root, location.ConfiguredValue);
    }

    [Fact]
    public void Hash_is_case_and_trailing_separator_insensitive_and_differs_per_apphost()
    {
        var sep = Path.DirectorySeparatorChar;

        Assert.Equal(CheckoutsLocation.HashOf(AppHost), CheckoutsLocation.HashOf(AppHost + sep));
        Assert.Equal(CheckoutsLocation.HashOf(AppHost), CheckoutsLocation.HashOf(AppHost.ToUpperInvariant()));
        Assert.NotEqual(CheckoutsLocation.HashOf(AppHost), CheckoutsLocation.HashOf(Abs("work", "other")));
    }

    [Fact]
    public void Trailing_separator_and_dotdot_segments_normalise()
    {
        var clean = CheckoutsLocation.Resolve(AppHost, Abs("x"));
        var sep = Path.DirectorySeparatorChar;

        var trailing = CheckoutsLocation.Resolve(AppHost, Abs("x") + sep);
        var dotdot = CheckoutsLocation.Resolve(AppHost, Path.Combine(Abs("ss"), "..", "x"));

        Assert.Equal(clean.CheckoutsRoot, trailing.CheckoutsRoot);
        Assert.Equal(clean.CheckoutsRoot, dotdot.CheckoutsRoot);
        Assert.Null(dotdot.Problem);
    }

    [Fact]
    public void Dotdot_cannot_hide_the_apphost()
    {
        var sneaky = Path.Combine(Abs("ss"), "..", "work", "apphost");

        var location = CheckoutsLocation.Resolve(AppHost, sneaky);

        Assert.NotNull(location.Problem);
    }

    public static TheoryData<string> RefusedValues()
    {
        var data = new TheoryData<string>
        {
            "ss",
            Path.Combine("..", "ss"),
            "//server/share/x",
            "//?/ss",
            AppHost,
            Path.GetDirectoryName(AppHost)!,
            Path.Combine(AppHost, ".servicesources", "x"),
            Path.Combine(AppHost, ".servicesources"),
        };

        if (OperatingSystem.IsWindows())
        {
            data.Add(@"\\server\share\x");
            data.Add(@"\\?\C:\ss");
            data.Add(@"\\.\C:\ss");
            data.Add(@"\ss");
            data.Add("C:ss");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedValues))]
    public void Refused_values_give_a_problem(string value)
    {
        var location = CheckoutsLocation.Resolve(AppHost, value);

        Assert.NotNull(location.Problem);
        var problem = location.Problem.Value.ToString();
        Assert.Contains(Key, problem);
        Assert.Contains("which", problem);
        Assert.False(location.IsExternal);
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(location.ThrowIfProblem);
        Assert.Contains(Key, ex.Message);
    }

    [Fact]
    public void A_problem_names_the_value_that_was_written()
    {
        var location = CheckoutsLocation.Resolve(AppHost, "ss");

        Assert.Contains("'ss'", location.Problem.ToString());
    }

    [WindowsFact]
    public void A_case_variant_ancestor_is_still_refused_where_paths_ignore_case()
    {
        var location = CheckoutsLocation.Resolve(AppHost, AppHost.ToUpperInvariant());

        Assert.NotNull(location.Problem);
    }

    [Fact]
    public void A_sibling_directory_of_the_apphost_is_allowed()
    {
        var location = CheckoutsLocation.Resolve(AppHost, Path.Combine(AppHost, "..", "x"));

        Assert.Null(location.Problem);
        Assert.True(location.IsExternal);
    }

    [Fact]
    public void A_directory_inside_the_apphost_other_than_servicesources_is_allowed()
    {
        var location = CheckoutsLocation.Resolve(AppHost, Path.Combine(AppHost, "elsewhere"));

        Assert.Null(location.Problem);
    }

    [Fact]
    public void A_filesystem_root_is_allowed()
    {
        var location = CheckoutsLocation.Resolve(AppHost, Drive);

        Assert.Null(location.Problem);
        Assert.Equal(Path.Combine(Drive, CheckoutsLocation.HashOf(AppHost)), location.OwnedDirectory);
    }

    [Fact]
    public void Resolve_creates_nothing()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\no-such-drive\ss" : "/no-such-root-for-servicesources/ss";

        var location = CheckoutsLocation.Resolve(AppHost, root);

        Assert.Null(location.Problem);
        Assert.False(Directory.Exists(location.OwnedDirectory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(location.OwnedDirectory)));
    }

    [Fact]
    public void Default_keeps_the_managed_checkout_path_shape()
    {
        var location = CheckoutsLocation.Default(AppHost);

        Assert.Equal(
            Path.Combine(AppHost, ".servicesources", "checkouts", "orders"),
            LocalGitCheckout.ManagedRepoRoot(location, "orders"));
    }

    [Fact]
    public void External_location_names_the_checkout_under_root_hash_checkouts()
    {
        var location = CheckoutsLocation.Resolve(AppHost, Abs("ss"));

        Assert.Equal(
            Path.Combine(Abs("ss"), CheckoutsLocation.HashOf(AppHost), "checkouts", "orders"),
            LocalGitCheckout.ManagedRepoRoot(location, "orders"));
    }

    [Fact]
    public void Dotdot_checkout_name_is_still_refused_under_an_external_location()
    {
        var location = CheckoutsLocation.Resolve(AppHost, Abs("ss"));

        Assert.Throws<ServiceSourcesConfigurationException>(() => LocalGitCheckout.ManagedRepoRoot(location, ".."));
    }

    [Fact]
    public void IsColdManagedCheckout_does_not_throw_for_a_problem_location()
    {
        var location = CheckoutsLocation.Resolve(AppHost, "relative");

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(
            location, "orders", new Aspire.Hosting.ServiceSources.Config.ServiceDeveloperConfig { Source = "repository" }));
    }
}
