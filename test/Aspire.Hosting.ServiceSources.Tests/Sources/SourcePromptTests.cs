using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourcePromptTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 10, 3, 14, 5, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, Raw> NoDependents = new Dictionary<string, Raw>();

    private static DeferredCheckout.Deferred Service(
        string name, string? url = null, string? defaultRef = null, string? configRef = null) =>
        new(
            name,
            new ExecutableResource(name, "run", "."),
            [],
            RepoRoot: "",
            Definition: new ServiceMetadata
            {
                Repository = url ?? $"https://example.com/{name}.git", Project = "Service.csproj", DefaultRef = defaultRef,
            }.ToDefinition("servicesources.yaml", name, TestHelpers.EmptyRepositories),
            Config: new ServiceDeveloperConfig { Source = "repository", Repository = new() { Ref = configRef } },
            RepositoryConfig: null,
            AppHostDirectory: "",
            Prefetch: null!,
            GitClient: null!,
            PrepareStep: null,
            PrepareRunner: null!,
            OnCheckoutLanded: static (_, _, _) => { });

    private static SourcePromptContent Build(
        IReadOnlyList<DeferredCheckout.Deferred> services, IReadOnlyDictionary<string, Raw>? dependents = null) =>
        SourcePrompt.Build(services, dependents ?? NoDependents, Deadline, SourcePromptTimeout);

    private static readonly TimeSpan SourcePromptTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void OneCheckedBooleanInputPerService_InCatalogOrder_WithPositionalNames()
    {
        var content = Build([Service("orders"), Service("billing")]);

        Assert.Collection(
            content.Inputs,
            first =>
            {
                Assert.Equal("s0", first.Name);
                Assert.Equal("orders", first.Label);
                Assert.Equal(InputType.Boolean, first.InputType);
                Assert.Equal("true", first.Value);
                Assert.False(first.EnableDescriptionMarkdown);
            },
            second =>
            {
                Assert.Equal("s1", second.Name);
                Assert.Equal("billing", second.Label);
                Assert.Equal("true", second.Value);
            });
    }

    [Fact]
    public void Description_ShowsSchemeHostAndPathOnly()
    {
        var content = Build([Service("orders", url: "https://user:secret@example.com/org/orders.git?token=abc#frag")]);

        var description = content.Inputs[0].Description!;
        Assert.Contains("https://example.com/org/orders.git", description);
        Assert.DoesNotContain("secret", description);
        Assert.DoesNotContain("user", description);
        Assert.DoesNotContain("token", description);
        Assert.DoesNotContain("frag", description);
    }

    [Fact]
    public void Description_NamesTheEffectiveRef()
    {
        Assert.Contains("at main", Build([Service("a", defaultRef: "main")]).Inputs[0].Description);
        Assert.Contains("at feature", Build([Service("a", defaultRef: "main", configRef: "feature")]).Inputs[0].Description);
        Assert.DoesNotContain(" at ", Build([Service("a")]).Inputs[0].Description);
    }

    [Fact]
    public void Description_CapsAVeryLongUrlAndRef()
    {
        var url = "https://example.com/" + new string('p', 500);
        var content = Build([Service("a", url: url, defaultRef: new string('r', 200))]);

        var description = content.Inputs[0].Description!;
        Assert.True(description.Length < 400, description.Length.ToString());
        Assert.Contains("…", description);
    }

    [Fact]
    public void Description_IncludesTheDependentsLine()
    {
        var orders = new ExecutableResource("orders", "run", ".");
        orders.Annotations.Add(new ServiceSourceAnnotation("orders", "repository"));
        var web = new ContainerResource("web");
        web.Annotations.Add(new WaitAnnotation(orders, WaitType.WaitUntilHealthy));
        var service = Service("orders");
        var dependents = SkippedDependents.For([orders, web], [service with { Resource = orders }]);

        var content = Build([service], dependents);

        Assert.Contains("Waited on by: web", content.Inputs[0].Description);
    }

    [Fact]
    public void ServiceNamesWithControlCharactersOrMarkdown_AreEscapedAndNotTruncated()
    {
        var name = new string('n', 100) + "\u001b[31m**bold**\n";

        var content = Build([Service(name)]);

        Assert.Contains(new string('n', 100), content.Inputs[0].Label);
        Assert.DoesNotContain('\u001b', content.Inputs[0].Label);
        Assert.DoesNotContain('\n', content.Inputs[0].Label);
    }

    [Fact]
    public void MarkdownIsDisabledOnTheMessage_AndTheDeadlineIsStated()
    {
        var content = Build([Service("orders")]);

        Assert.False(content.Options.EnableMessageMarkdown);
        Assert.Contains("within 5 minutes", content.Message);
        Assert.Contains($"by {Deadline.ToLocalTime():HH:mm}", content.Message);
        Assert.Contains("Closing this dialog does the same", content.Message);
        Assert.Contains("nothing is saved", content.Message);
    }

    [Fact]
    public void TheWaitIsFormattedFromTheTimeoutNotTypedTwice()
    {
        var content = SourcePrompt.Build([Service("orders")], NoDependents, Deadline, TimeSpan.FromMinutes(12));

        Assert.Contains("within 12 minutes", content.Message);
    }

    [Fact]
    public void AwaitingState_NamesTheSameDeadlineTime()
    {
        Assert.Equal(
            $"Awaiting source selection (starts automatically at {Deadline.ToLocalTime():HH:mm})",
            SourcePrompt.AwaitingState(Deadline));
    }

    private static InteractionResult<InteractionInputCollection> Answered(params (string Name, string? Value)[] values) =>
        InteractionResult.Ok(new InteractionInputCollection(
            values.Select(v => new InteractionInput
            {
                Name = v.Name, Label = v.Name, InputType = InputType.Boolean, Value = v.Value,
            }).ToList()));

    [Fact]
    public void Map_CheckedStarts_UncheckedSkips()
    {
        var services = new[] { Service("orders"), Service("billing") };

        var answers = SourcePrompt.Map(Answered(("s0", "true"), ("s1", "false")), services);

        Assert.Equal([SourceAnswer.Start, SourceAnswer.Skip], answers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    public void Map_NonBooleanAnswer_StartsUnanswered(string? value)
    {
        var answers = SourcePrompt.Map(Answered(("s0", value)), [Service("orders")]);

        Assert.Equal([SourceAnswer.StartUnanswered], answers);
    }

    [Fact]
    public void Map_MissingOrRenamedInput_StartsUnanswered_WithoutAffectingTheOthers()
    {
        var services = new[] { Service("orders"), Service("billing") };

        var answers = SourcePrompt.Map(Answered(("renamed", "false"), ("s1", "false")), services);

        Assert.Equal([SourceAnswer.StartUnanswered, SourceAnswer.Skip], answers);
    }

    [Fact]
    public void Map_NoData_StartsEveryoneUnanswered()
    {
        var answers = SourcePrompt.Map(InteractionResult.Cancel<InteractionInputCollection>(), [Service("orders")]);

        Assert.Equal([SourceAnswer.StartUnanswered], answers);
    }
}
