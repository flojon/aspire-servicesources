using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceRequirementsModelTests
{
    private static readonly FollowUpField UrlField = new("url:url", "Service URL", true, InputType.Text);

    private static SourceProblem Problem(string? missing = null)
    {
        var exception = ServiceSourcesConfigurationException.For($"bad entry");
        return new SourceProblem(missing is null ? exception : exception.AsMissingSetting(missing));
    }

    [Fact]
    public void Resolves_IsTrueOnlyWithoutAProblem()
    {
        Assert.True(new SourceRequirements([], null).Resolves);
        Assert.False(new SourceRequirements([], Problem()).Resolves);
    }

    [Fact]
    public void Offered_WhenNoProblem_IsTrue() =>
        Assert.True(new SourceRequirements([], null).Offered);

    [Fact]
    public void Offered_WhenProblemMissesAFieldTheDialogAsks_IsTrue() =>
        Assert.True(new SourceRequirements([UrlField], Problem("url:url")).Offered);

    [Fact]
    public void Offered_WhenProblemMissesASettingNoFieldCovers_IsFalse() =>
        Assert.False(new SourceRequirements([UrlField], Problem("container:image")).Offered);

    [Fact]
    public void Offered_WhenProblemIsUntagged_IsFalse() =>
        Assert.False(new SourceRequirements([UrlField], Problem()).Offered);

    [Fact]
    public void ResolvesImpliesOffered_OverEveryCombinationOfFieldsAndProblem()
    {
        FollowUpField[][] fields = [[], [UrlField]];
        SourceProblem?[] problems = [null, Problem(), Problem("url:url"), Problem("container:image")];

        foreach (var f in fields)
        {
            foreach (var p in problems)
            {
                var requirements = new SourceRequirements(f, p);

                Assert.True(!requirements.Resolves || requirements.Offered);
            }
        }
    }

    [Fact]
    public void Capture_ReturnsNull_WhenTheCheckReturns() =>
        Assert.Null(SourceProblem.Capture(() => { }));

    [Fact]
    public void Capture_WrapsAConfigurationException_WithItsMessageAndTag()
    {
        var problem = SourceProblem.Capture(
            () => throw ServiceSourcesConfigurationException.For($"missing url").AsMissingSetting("url:url"));

        Assert.NotNull(problem);
        Assert.Equal("missing url", problem.Message);
        Assert.Equal("url:url", problem.MissingSetting);
    }

    [Fact]
    public void Capture_LetsAnyOtherExceptionPropagate() =>
        Assert.Throws<UriFormatException>(() => SourceProblem.Capture(() => throw new UriFormatException("not a uri")));

    [Fact]
    public void Unknown_NeitherResolvesNorIsOffered_AndHasNoFields()
    {
        var unknown = SourceRequirements.Unknown;

        Assert.False(unknown.Resolves);
        Assert.False(unknown.Offered);
        Assert.Empty(unknown.Fields);
    }
}
