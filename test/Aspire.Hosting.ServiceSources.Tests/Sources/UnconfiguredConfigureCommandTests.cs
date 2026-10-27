using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
[Trait("Timing", "true")]
public class UnconfiguredConfigureCommandTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string CatalogWithUrl = """
        services:
          inventory:
            url:
              url: https://inventory.example.com
        """;

    // No URL, so choosing 'url' asks for one in the details dialog.
    private const string CatalogWithoutUrl = """
        services:
          inventory:
            container:
              image: ghcr.io/company/inventory
        """;

    private static (IDistributedApplicationBuilder Builder, string Dir) Builder(
        string catalog = CatalogWithUrl, string? localJson = null)
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), catalog);

        if (localJson is not null)
        {
            File.WriteAllText(LocalJson(dir), localJson);
        }

        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CI"] = "",
            ["TF_BUILD"] = "",
            ["JENKINS_URL"] = "",
        });
        builder.UseInteractiveServiceConfiguration();
        builder.AddService("inventory");
        return (builder, dir);
    }

    private static ResourceCommandAnnotation Command(IDistributedApplicationBuilder builder) =>
        Assert.Single(
            Assert.Single(builder.Resources.OfType<UnconfiguredServiceResource>()).Annotations.OfType<ResourceCommandAnnotation>(),
            a => a.Name == "configure");

    private static ServiceProvider Services(FakeInteractionService interaction, IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInteractionService>(interaction);
        services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static Task<ExecuteCommandResult> Execute(
        ResourceCommandAnnotation command, IServiceProvider services, ILogger? logger = null,
        CancellationToken token = default) =>
        command.ExecuteCommand(new ExecuteCommandContext
        {
            Services = services,
            ResourceName = "inventory",
            CancellationToken = token,
            Logger = logger ?? NullLogger.Instance,
            Arguments = new InteractionInputCollection([]),
        });

    private static FakeInteractionService Choosing(string choice) =>
        new()
        {
            IsAvailable = true,
            AnswerFor = (title, inputs) =>
            {
                if (title != UnconfiguredPrompt.Title)
                {
                    return null;
                }

                inputs[0].Value = choice;
                return InteractionResult.Ok(new InteractionInputCollection(inputs));
            },
        };

    private static string LocalJson(string dir) => Path.Combine(dir, DeveloperConfiguration.FileName);

    private static string? SavedSource(string dir) =>
        File.Exists(LocalJson(dir))
            ? JsonNode.Parse(File.ReadAllText(LocalJson(dir)))?["services"]?["inventory"]?["source"]?.GetValue<string>()
            : null;

    [Fact]
    public void Configure_Command_Exists_OnPlaceholder()
    {
        var (builder, _) = Builder();

        var command = Command(builder);

        Assert.Equal("Configure", command.DisplayName);
    }

    [Fact]
    public async Task Configure_Command_RunsSamePromptAndSave_AsPushPath()
    {
        var (builder, dir) = Builder();
        var (pushBuilder, pushDir) = Builder();
        var interaction = Choosing("url");

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        await UnconfiguredPrompt.RunAsync(
                Services(Choosing("url")), UnconfiguredRegistry.For(pushBuilder).Services,
                UnconfiguredRegistry.CreateWriter(pushDir), pushDir, NullLogger.Instance, CancellationToken.None)
            .WaitAsync(Patience);

        Assert.True(result.Success);
        Assert.Equal("url", SavedSource(dir));
        Assert.Equal(File.ReadAllText(LocalJson(pushDir)), File.ReadAllText(LocalJson(dir)));
        var prompt = Assert.Single(interaction.Prompts);
        Assert.Equal(UnconfiguredPrompt.Title, prompt.Title);
        Assert.Single(prompt.Inputs);
    }

    [Fact]
    public async Task Configure_Command_Works_WhenInteractivePromptsAreOffForCi()
    {
        var (builder, dir) = Builder();
        var ci = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CI"] = "true" })
            .Build();

        var result = await Execute(Command(builder), Services(Choosing("url"), ci)).WaitAsync(Patience);

        Assert.True(result.Success);
        Assert.Equal("url", SavedSource(dir));
    }

    [Fact]
    public async Task Configure_Command_FailsGracefully_WhenInteractionUnavailable()
    {
        var (builder, dir) = Builder();
        var interaction = new FakeInteractionService { IsAvailable = false };

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.Contains("dashboard", result.Message);
        Assert.Equal(0, interaction.PromptCount);
        Assert.Null(SavedSource(dir));
    }

    [Fact]
    public async Task Configure_Command_FailsGracefully_WhenNoInteractionServiceIsRegistered()
    {
        var (builder, _) = Builder();

        var result = await Execute(Command(builder), new ServiceCollection().BuildServiceProvider()).WaitAsync(Patience);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Configure_Command_ReturnsLoggedFailure_WhenThePromptThrows()
    {
        var (builder, dir) = Builder();
        var interaction = new FakeInteractionService { IsAvailable = true, Throw = new InvalidOperationException("boom") };
        var logger = new ListLogger();

        var result = await Execute(Command(builder), Services(interaction), logger).WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("boom"));
        Assert.Null(SavedSource(dir));
    }

    [Fact]
    public async Task Configure_Command_Cancelled_ReturnsWithoutThrowing()
    {
        var (builder, dir) = Builder();
        var interaction = new FakeInteractionService { IsAvailable = true };
        using var cts = new CancellationTokenSource();

        var run = Execute(Command(builder), Services(interaction), token: cts.Token);
        await interaction.Prompted.WaitAsync(Patience);
        await cts.CancelAsync();

        var result = await run.WaitAsync(Patience);

        Assert.Null(SavedSource(dir));
        Assert.False(result.Success);
        Assert.True(result.Canceled);
    }

    [Fact]
    public async Task Configure_Command_Saved_ReportsSuccess_AndTheSavedNotification()
    {
        var (builder, dir) = Builder();
        var interaction = Choosing("url");

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        Assert.True(result.Success);
        Assert.Equal("url", SavedSource(dir));
        Assert.Equal(MessageIntent.Success, Assert.Single(interaction.Notifications).Options?.Intent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Configure_Command_DialogClosedOrNothingChosen_ReportsCanceled(string? choice)
    {
        var (builder, dir) = Builder();
        var interaction = choice is null
            ? new FakeInteractionService
            {
                IsAvailable = true,
                Answer = _ => InteractionResult.Cancel<InteractionInputCollection>(),
            }
            : Choosing(choice);

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(SavedSource(dir));
        Assert.Empty(interaction.Notifications);
    }

    [Fact]
    public async Task Configure_Command_DetailsDialogClosed_ReportsCanceled()
    {
        var (builder, dir) = Builder(CatalogWithoutUrl);
        var interaction = Choosing("url");
        interaction.Answer = _ => InteractionResult.Cancel<InteractionInputCollection>();

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        Assert.True(result.Canceled);
        Assert.Equal(2, interaction.PromptCount);
        Assert.Null(SavedSource(dir));
    }

    [Fact]
    public async Task Configure_Command_AnswerRejected_ReportsFailure_InPlaceOfTheNotSavedNotification()
    {
        var (builder, dir) = Builder(CatalogWithoutUrl);
        var interaction = new FakeInteractionService
        {
            IsAvailable = true,
            AnswerFor = (title, inputs) =>
            {
                inputs[0].Value = title == UnconfiguredPrompt.Title ? "url" : "ftp://example.com";
                return InteractionResult.Ok(new InteractionInputCollection(inputs));
            },
        };

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.StartsWith("Not saved: 'inventory':", result.Message);
        Assert.Contains("must use the http or https scheme", result.Message);
        Assert.Null(SavedSource(dir));
        Assert.Empty(interaction.Notifications);
    }

    [Fact]
    public async Task Configure_Command_SaveRefusedForComments_ReportsFailure_AndLeavesTheFile()
    {
        const string existing = """
            {
              // kept by hand
              "services": {}
            }
            """;
        var (builder, dir) = Builder(localJson: existing);
        var interaction = Choosing("url");

        var result = await Execute(Command(builder), Services(interaction)).WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.StartsWith("Not saved: 'inventory':", result.Message);
        Assert.Contains("contains comments, which saving would remove", result.Message);
        Assert.Equal(existing, File.ReadAllText(LocalJson(dir)));
        Assert.Empty(interaction.Notifications);
    }

    [Fact]
    public async Task Configure_Command_SecondClick_WhileDialogOpen_IsRefused_AndTheNextWorksOnceItCloses()
    {
        var (builder, dir) = Builder();
        var command = Command(builder);
        var open = new FakeInteractionService { IsAvailable = true };
        var other = Choosing("url");
        using var cts = new CancellationTokenSource();

        var first = Execute(command, Services(open), token: cts.Token);
        await open.Prompted.WaitAsync(Patience);

        var second = await Execute(command, Services(other)).WaitAsync(Patience);

        Assert.False(second.Success);
        Assert.Contains("already open", second.Message);
        Assert.Equal(0, other.PromptCount);

        await cts.CancelAsync();
        await first.WaitAsync(Patience);

        var third = await Execute(command, Services(other)).WaitAsync(Patience);

        Assert.True(third.Success);
        Assert.Equal("url", SavedSource(dir));
    }

    [Fact]
    public async Task DialogClosedLine_PointsAtTheConfigureCommand_AndTheConfigKey()
    {
        var interaction = new FakeInteractionService
        {
            IsAvailable = true,
            Answer = _ => InteractionResult.Cancel<InteractionInputCollection>(),
        };
        var (builder, dir) = Builder();
        var logger = new ListLogger();

        await UnconfiguredPrompt.RunAsync(
                Services(interaction), UnconfiguredRegistry.For(builder).Services,
                UnconfiguredRegistry.CreateWriter(dir), dir, logger, CancellationToken.None)
            .WaitAsync(Patience);

        var line = Assert.Single(logger.Entries).Message;
        Assert.Contains("Configure", line);
        Assert.Contains("'inventory'", line);
        Assert.Contains(":<name>:source", line);
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
