using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

/// <summary>
/// What composition settles about a service's <c>prepare</c> step from configuration alone: the
/// merge of the catalog's block and the developer's, the four modes, the confinement of the command,
/// and the rule that a <c>path</c> checkout declares its own step rather than inheriting one.
/// </summary>
public class PreparePlanTests
{
    private const string ServiceName = "routing";

    private static PrepareMetadata Catalog(
        string[]? command = null, string[]? windowsCommand = null, string? mode = null) =>
        new() { Command = command, WindowsCommand = windowsCommand, Mode = mode };

    private static PrepareDeveloperConfig Developer(
        string[]? command = null, string[]? windowsCommand = null, string? mode = null) =>
        new() { Command = command, WindowsCommand = windowsCommand, Mode = mode };

    private static PreparePlan Plan(
        PrepareMetadata? catalog = null,
        PrepareDeveloperConfig? developer = null,
        bool managedCheckout = true,
        bool windows = false) =>
        PreparePlan.For(ServiceName, catalog, developer, managedCheckout, windows);

    private static ServiceSourcesConfigurationException Rejects(
        PrepareMetadata? catalog = null,
        PrepareDeveloperConfig? developer = null,
        bool managedCheckout = true,
        bool windows = false) =>
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => Plan(catalog, developer, managedCheckout, windows));

    [Fact]
    public void NoBlockAnywhere_IsNoStep()
    {
        var plan = Plan();

        Assert.Null(plan.Step);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    [Fact]
    public void CatalogCommand_Runs()
    {
        var step = Plan(Catalog(["./prepare.sh", "--full"])).Step;

        Assert.NotNull(step);
        Assert.Equal<string[]>(["./prepare.sh", "--full"], [.. step!.Command]);
    }

    [Fact]
    public void UnspecifiedMode_IsOncePerCommit() =>
        Assert.Equal(PrepareMode.OncePerCommit, Plan(Catalog(["./prepare.sh"])).Step!.Mode);

    /// <remarks>
    /// The spellings rather than the enum values, because a public test method cannot take an
    /// internal type as a parameter — and the spelling is what a developer writes anyway.
    /// </remarks>
    [Theory]
    [InlineData("oncePerCommit", "oncePerCommit")]
    [InlineData("ONCEPERCOMMIT", "oncePerCommit")]
    [InlineData("once", "once")]
    [InlineData("Once", "once")]
    [InlineData("always", "always")]
    public void EachMode_Parses(string written, string expected) =>
        Assert.Equal(expected, PrepareModes.Written(Plan(Catalog(["./prepare.sh"], mode: written)).Step!.Mode));

    [Fact]
    public void ModeNever_RunsNothing() =>
        Assert.Null(Plan(Catalog(["./prepare.sh"], mode: "never")).Step);

    [Fact]
    public void UnknownMode_IsRejectedNamingAllFour()
    {
        var ex = Rejects(Catalog(["./prepare.sh"], mode: "sometimes"));

        Assert.Contains($"'{ServiceName}'", ex.Message);
        Assert.Contains("prepare.mode is 'sometimes'", ex.Message);
        Assert.Contains("'oncePerCommit'", ex.Message);
        Assert.Contains("'once'", ex.Message);
        Assert.Contains("'always'", ex.Message);
        Assert.Contains("'never'", ex.Message);
    }

    /// <remarks>
    /// Reported from the file it was written in, so a reader knows which of the two to open.
    /// </remarks>
    [Fact]
    public void UnknownModeInTheDevelopersFile_NamesThatBlock()
    {
        var ex = Rejects(Catalog(["./prepare.sh"]), Developer(mode: "sometimes"));

        Assert.Contains("local.prepare.mode is 'sometimes'", ex.Message);
    }

    [Fact]
    public void EmptyCommand_IsRejected()
    {
        var ex = Rejects(Catalog([]));

        Assert.Contains("prepare.command is an empty list", ex.Message);
    }

    /// <remarks>
    /// A yaml <c>~</c> binds as a null element, and an argv element cannot be one: it reaches
    /// <c>ArgumentList</c> as an <c>ArgumentNullException</c> and <c>Describe</c> as a dereference
    /// of nothing, so this class's own diagnostics escaped as a crash for a mistake in a file.
    /// </remarks>
    [Fact]
    public void ANullArgument_IsRejectedByName()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Plan(new PrepareMetadata { Command = ["./prepare.sh", null!, "--full"] }));

        Assert.Contains($"'{ServiceName}'", ex.Message);
        Assert.Contains("element 2 of prepare.command", ex.Message);
    }

    /// <remarks>
    /// An empty argument is not the same mistake: a command may genuinely take one, so it is passed
    /// through where a null is refused.
    /// </remarks>
    [Fact]
    public void AnEmptyArgument_IsAllowed()
    {
        var step = Plan(Catalog(["./prepare.sh", "", "--full"])).Step;

        Assert.Equal<string[]>(["./prepare.sh", "", "--full"], [.. step!.Command]);
    }

    [Fact]
    public void BlankProgram_IsRejected()
    {
        var ex = Rejects(Catalog(["  ", "bootstrap"]));

        Assert.Contains("first element of prepare.command is blank", ex.Message);
    }

    // ---- platform selection -------------------------------------------------

    [Fact]
    public void OnWindows_WindowsCommandReplacesTheCommand()
    {
        var step = Plan(Catalog(["./prepare.sh"], ["pwsh", "-File", "prepare.ps1"]), windows: true).Step;

        Assert.Equal<string[]>(["pwsh", "-File", "prepare.ps1"], [.. step!.Command]);
    }

    [Fact]
    public void OffWindows_WindowsCommandIsIgnored()
    {
        var step = Plan(Catalog(["./prepare.sh"], ["pwsh", "-File", "prepare.ps1"])).Step;

        Assert.Equal<string[]>(["./prepare.sh"], [.. step!.Command]);
    }

    /// <remarks>
    /// Correct for the many cross-platform cases — <c>npm</c>, <c>make</c>, <c>python</c>,
    /// <c>dotnet</c> — which is why the variant stays optional.
    /// </remarks>
    [Fact]
    public void OnWindows_WithNoVariant_TheCommandRunsUnchanged()
    {
        var step = Plan(Catalog(["make", "bootstrap"]), windows: true).Step;

        Assert.Equal<string[]>(["make", "bootstrap"], [.. step!.Command]);
    }

    /// <remarks>
    /// The hash is over the resolved argv, so a developer who moves from Linux to Windows and picks
    /// up the variant re-runs rather than inheriting a completion recorded for a different command.
    /// </remarks>
    [Fact]
    public void ThePlatformVariant_HashesDifferently()
    {
        var catalog = Catalog(["./prepare.sh"], ["pwsh", "-File", "prepare.ps1"]);

        Assert.NotEqual(
            Plan(catalog).Step!.CommandHash,
            Plan(catalog, windows: true).Step!.CommandHash);
    }

    // ---- the override table -------------------------------------------------

    [Fact]
    public void DeveloperModeNever_DisablesTheCatalogsStep() =>
        Assert.Null(Plan(Catalog(["./prepare.sh"]), Developer(mode: "never")).Step);

    [Fact]
    public void DeveloperModeAlways_KeepsTheCatalogsCommand()
    {
        var step = Plan(Catalog(["./prepare.sh"], mode: "once"), Developer(mode: "always")).Step;

        Assert.Equal(PrepareMode.Always, step!.Mode);
        Assert.Equal<string[]>(["./prepare.sh"], [.. step.Command]);
    }

    /// <remarks>
    /// The command pair is replaced as a unit. Splitting it would run the developer's command on
    /// Linux and the catalog's on Windows, which is a bug wearing the costume of a feature.
    /// </remarks>
    [Fact]
    public void DeveloperCommand_AlsoReplacesTheCatalogsWindowsVariant()
    {
        var catalog = Catalog(["./prepare.sh"], ["pwsh", "-File", "prepare.ps1"], mode: "once");
        var developer = Developer(["make", "bootstrap"]);

        Assert.Equal<string[]>(["make", "bootstrap"], [.. Plan(catalog, developer).Step!.Command]);
        Assert.Equal<string[]>(["make", "bootstrap"], [.. Plan(catalog, developer, windows: true).Step!.Command]);
        // Mode is kept: only the pair travels together.
        Assert.Equal(PrepareMode.Once, Plan(catalog, developer).Step!.Mode);
    }

    [Fact]
    public void DeveloperSupplyingBothHalves_ReplacesBoth()
    {
        var plan = Plan(
            Catalog(["./prepare.sh"], ["pwsh", "-File", "prepare.ps1"]),
            Developer(["make", "bootstrap"], ["make.exe", "bootstrap"]),
            windows: true);

        Assert.Equal<string[]>(["make.exe", "bootstrap"], [.. plan.Step!.Command]);
    }

    [Fact]
    public void NoDeveloperBlock_LeavesTheCatalogsBlockStanding()
    {
        var step = Plan(Catalog(["./prepare.sh"], mode: "once")).Step;

        Assert.Equal(PrepareMode.Once, step!.Mode);
        Assert.Equal<string[]>(["./prepare.sh"], [.. step.Command]);
    }

    [Fact]
    public void DeveloperBlockWithNoCatalogBlock_StandsOnItsOwn()
    {
        var step = Plan(developer: Developer(["make", "bootstrap"], mode: "always")).Step;

        Assert.Equal(PrepareMode.Always, step!.Mode);
        Assert.Equal<string[]>(["make", "bootstrap"], [.. step.Command]);
    }

    /// <remarks>
    /// On a managed checkout a mode by itself is the designed way to disable or force an inherited
    /// step, so one with no step behind it is a developer whose catalog declares none rather than a
    /// mistake to report. The <c>path</c> case below is the one that rejects it.
    /// </remarks>
    [Fact]
    public void DeveloperModeWithNoCommandAnywhere_IsNoStep() =>
        Assert.Null(Plan(developer: Developer(mode: "always")).Step);

    // ---- confinement --------------------------------------------------------

    [Theory]
    [InlineData("../../escape.sh")]
    [InlineData("..\\escape.cmd")]
    [InlineData("scripts/../../escape.sh")]
    public void AProgramClimbingOutOfTheCheckout_IsRejected(string program)
    {
        var ex = Rejects(Catalog([program]));

        Assert.Contains("points outside the service's checkout", ex.Message);
    }

    [Theory]
    [InlineData("/usr/local/bin/bootstrap")]
    [InlineData("C:\\tools\\bootstrap.exe")]
    [InlineData("\\\\server\\share\\bootstrap")]
    public void AnAbsoluteProgram_IsRejected(string program)
    {
        var ex = Rejects(Catalog([program]));

        Assert.Contains("absolute path", ex.Message);
    }

    /// <remarks>
    /// A bare name is how a command reaches a program that is genuinely elsewhere, so it is left
    /// for <c>PATH</c> rather than confined to the checkout.
    /// </remarks>
    [Theory]
    [InlineData("make")]
    [InlineData("npm")]
    [InlineData("bash")]
    public void ABareProgramName_IsLeftForPath(string program) =>
        Assert.Equal(program, Plan(Catalog([program, "bootstrap"])).Step!.Command[0]);

    [Fact]
    public void ARelativeProgram_StaysRelative() =>
        Assert.Equal(
            Path.Combine("scripts", "bootstrap"),
            Plan(Catalog(["scripts/bootstrap"])).Step!.Command[0]);

    /// <remarks>
    /// Only the first element names a program. An argument is the command's own business — a
    /// <c>--config</c> value, a path the script resolves itself — and confining those would reject
    /// working commands for pointing at something the tool has no opinion about.
    /// </remarks>
    [Fact]
    public void AnArgumentIsNotConfined()
    {
        var step = Plan(Catalog(["make", "-C", "/opt/build", "../sibling"])).Step;

        Assert.Equal<string[]>(["make", "-C", "/opt/build", "../sibling"], [.. step!.Command]);
    }

    // ---- path checkouts -----------------------------------------------------

    [Fact]
    public void PathCheckout_DoesNotInheritTheCatalogsStep()
    {
        var plan = Plan(Catalog(["./prepare.sh"]), managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.NotNull(plan.IgnoredCatalogNotice);
        Assert.Contains($"'{ServiceName}'", plan.IgnoredCatalogNotice!);
        // Carried verbatim, so it can be copied into the local file.
        Assert.Contains("\"./prepare.sh\"", plan.IgnoredCatalogNotice);
        Assert.Contains("local.path", plan.IgnoredCatalogNotice);
    }

    /// <summary>
    /// A step the catalog has centrally disabled asks no <c>path</c> developer to copy it in.
    /// </summary>
    /// <remarks>
    /// <c>mode: never</c> in the catalog says nothing should run anywhere. The notice exists to
    /// offer a command the developer could adopt, so offering one the catalog has turned off is
    /// advice against the catalog's own instruction — and it repeated on every start.
    /// </remarks>
    [Fact]
    public void PathCheckout_ACatalogStepDisabledCentrally_SaysNothing()
    {
        var plan = Plan(Catalog(["./prepare.sh"], mode: "never"), managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    /// <summary>
    /// The snippet the notice offers is JSON that parses, whatever the command contains.
    /// </summary>
    /// <remarks>
    /// The whole value of the notice is that it can be pasted into a JSON file. A Windows command
    /// carries backslashes, and wrapping an argument in quotes by hand produces either invalid JSON
    /// or — worse, because it parses — a different string: <c>C:\temp\x</c> written raw contains
    /// <c>\t</c>, which JSON reads as a tab.
    /// </remarks>
    [Fact]
    public void PathCheckout_TheNoticesSnippet_IsValidJson()
    {
        var plan = Plan(
            Catalog(["pwsh", "-File", @"C:\tools\prepare.ps1", "--label", "say \"hi\""]),
            managedCheckout: false);

        var snippet = plan.IgnoredCatalogNotice!;
        var array = snippet[snippet.IndexOf("[", StringComparison.Ordinal)..(snippet.IndexOf("]", StringComparison.Ordinal) + 1)];

        var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(array);

        Assert.Equal<string[]>(
            ["pwsh", "-File", @"C:\tools\prepare.ps1", "--label", "say \"hi\""], parsed!);
    }

    [Fact]
    public void PathCheckout_NoCatalogStepEither_SaysNothing()
    {
        var plan = Plan(managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    [Fact]
    public void PathCheckout_DeveloperDeclaresTheirOwn_RunsItAndSaysNothing()
    {
        var plan = Plan(Catalog(["./prepare.sh"]), Developer(["make", "bootstrap"]), managedCheckout: false);

        Assert.Equal<string[]>(["make", "bootstrap"], [.. plan.Step!.Command]);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    [Fact]
    public void PathCheckout_ModeWithNoCommand_IsRejectedByName()
    {
        var ex = Rejects(Catalog(["./prepare.sh"]), Developer(mode: "always"), managedCheckout: false);

        Assert.Contains($"'{ServiceName}'", ex.Message);
        Assert.Contains("local.prepare.mode", ex.Message);
        Assert.Contains("local.path", ex.Message);
        Assert.Contains("'never'", ex.Message);
    }

    /// <remarks>
    /// The one mode that means something without a command — run nothing — so silence is exactly
    /// what was asked for rather than the failure it is under the other three.
    /// </remarks>
    [Fact]
    public void PathCheckout_ModeNeverAlone_IsAcceptedAndSilencesTheNotice()
    {
        var plan = Plan(Catalog(["./prepare.sh"]), Developer(mode: "never"), managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    /// <summary>
    /// <c>never</c> beside a command of the developer's own still means run nothing.
    /// </summary>
    /// <remarks>
    /// The combination is how a developer disables a step they wrote themselves — the same gesture
    /// that disables an inherited one — and it is the one that has to hold, because the alternative
    /// is a command running in a directory this tool does not own, which is precisely what 'path'
    /// plus <c>never</c> says will not happen.
    /// </remarks>
    [Fact]
    public void PathCheckout_ModeNeverBesideACommand_StillRunsNothing()
    {
        var plan = Plan(
            Catalog(["./prepare.sh"]),
            Developer(["make", "bootstrap"], mode: "never"),
            managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    [Fact]
    public void ManagedCheckout_ModeNeverBesideACommand_StillRunsNothing() =>
        Assert.Null(Plan(Catalog(["./prepare.sh"]), Developer(["make", "bootstrap"], mode: "never")).Step);

    /// <summary>
    /// A <c>path</c> service declaring only the Windows variant does nothing on POSIX, rather than
    /// being refused for a mode the developer never set.
    /// </summary>
    /// <remarks>
    /// The block is a deliberate statement — "on Windows run this, and I have no POSIX command" —
    /// and it is the same statement the managed branch reads silently. The refusal below it is for a
    /// mode with no command <em>anywhere</em> in the block, which is the half that cannot stand
    /// alone.
    /// </remarks>
    [Fact]
    public void PathCheckout_WindowsCommandOnly_DoesNothingOnPosixRatherThanFailing()
    {
        var plan = Plan(
            Catalog(["./prepare.sh"]),
            Developer(windowsCommand: ["pwsh", "-File", "prepare.ps1"]),
            managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.Null(plan.IgnoredCatalogNotice);
    }

    [Fact]
    public void PathCheckout_WindowsCommandOnly_RunsOnWindows()
    {
        var plan = Plan(
            Catalog(["./prepare.sh"]),
            Developer(windowsCommand: ["pwsh", "-File", "prepare.ps1"]),
            managedCheckout: false,
            windows: true);

        Assert.Equal<string[]>(["pwsh", "-File", "prepare.ps1"], [.. plan.Step!.Command]);
    }

    /// <remarks>
    /// An empty block is not a declaration: it cannot be told from an absent one by looking, and it
    /// says nothing about what should run — so it neither inherits nor silences.
    /// </remarks>
    [Fact]
    public void PathCheckout_AnEmptyDeveloperBlock_IsNotADeclaration()
    {
        var plan = Plan(Catalog(["./prepare.sh"]), Developer(), managedCheckout: false);

        Assert.Null(plan.Step);
        Assert.NotNull(plan.IgnoredCatalogNotice);
    }

    /// <remarks>
    /// A value that cannot be a mode is a mistake in a file whichever way resolution goes, so it is
    /// reported rather than silently discarded along with the block a <c>path</c> service ignores.
    /// </remarks>
    [Fact]
    public void PathCheckout_AnUnknownCatalogMode_IsStillRejected()
    {
        var ex = Rejects(Catalog(["./prepare.sh"], mode: "sometimes"), managedCheckout: false);

        Assert.Contains("prepare.mode is 'sometimes'", ex.Message);
    }
}
