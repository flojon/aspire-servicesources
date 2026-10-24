using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests.Worktrees;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
[Trait("Timing", "true")]
public class UnconfiguredPromptTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static ServiceDefinition Definition(string name, bool container = true, bool kubernetes = true) =>
        new ServiceMetadata
        {
            Repository = $"https://example.com/{name}.git",
            Project = "Service.csproj",
            Container = container ? new ContainerMetadata { Image = $"example/{name}", Port = 8080 } : null,
            Kubernetes = kubernetes ? new KubernetesMetadata { Service = name } : null,
        }.ToDefinition("servicesources.yaml", name, TestHelpers.EmptyRepositories);

    private static Unconfigured Orders() => new("orders", Definition("orders"));

    // No container or kubernetes block, so neither is offered.
    private static Unconfigured Billing() => new("billing", Definition("billing", container: false, kubernetes: false));

    private static IServiceProvider Services(FakeInteractionService interaction, IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInteractionService>(interaction);
        services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static InteractionResult<InteractionInputCollection> Fill(
        IReadOnlyList<InteractionInput> inputs, params string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            inputs[i].Value = values[i];
        }

        return InteractionResult.Ok(new InteractionInputCollection(inputs));
    }

    private static FakeInteractionService Answering(
        string?[] choices, Func<string, IReadOnlyList<InteractionInput>, InteractionResult<InteractionInputCollection>?>? followUp = null) =>
        new()
        {
            IsAvailable = true,
            AnswerFor = (title, inputs) => title == UnconfiguredPrompt.Title
                ? Fill(inputs, choices)
                : followUp?.Invoke(title, inputs),
        };

    private static async Task<(string Dir, ListLogger Logger)> RunAsync(
        FakeInteractionService interaction, IReadOnlyList<Unconfigured> services, IConfiguration? configuration = null)
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var logger = new ListLogger();

        await UnconfiguredPrompt.RunAsync(
                Services(interaction, configuration), services, new JsonFileDeveloperConfigWriter(dir), dir, logger,
                CancellationToken.None)
            .WaitAsync(Patience);

        return (dir, logger);
    }

    private static string LocalJson(string dir) => Path.Combine(dir, DeveloperConfiguration.FileName);

    private static JsonNode? SavedEntry(string dir, string service) =>
        File.Exists(LocalJson(dir)) ? JsonNode.Parse(File.ReadAllText(LocalJson(dir)))?["services"]?[service] : null;

    [Fact]
    public void Build_OneChoiceInputPerService_WithFilteredOptions_PlainText()
    {
        var services = new[] { new Unconfigured("**orders**", Definition("orders")), Billing() };

        var content = UnconfiguredPrompt.Build(services);

        Assert.Equal("Choose a source for unconfigured services", content.Title);
        Assert.NotEqual("Choose services to check out", content.Title);
        Assert.False(content.Options.EnableMessageMarkdown);
        Assert.Equal(2, content.Inputs.Count);
        Assert.All(content.Inputs, input =>
        {
            Assert.Equal(InputType.Choice, input.InputType);
            Assert.False(input.EnableDescriptionMarkdown);
        });

        Assert.Equal("**orders**", content.Inputs[0].Label);
        Assert.Equal("billing", content.Inputs[1].Label);
        Assert.Equal(SourceChoices.For(services[0].Definition), content.Inputs[0].Options!.Select(o => o.Key));
        Assert.Equal(SourceChoices.For(services[1].Definition), content.Inputs[1].Options!.Select(o => o.Key));
        Assert.DoesNotContain(content.Inputs[1].Options!, o => o.Key is "container" or "kubernetes");
    }

    [Fact]
    public async Task Run_Answers_SavedAndSuccessNotificationSaysNextRun()
    {
        var interaction = Answering(["container", "repository"]);

        var (dir, _) = await RunAsync(interaction, [Orders(), Billing()]);

        Assert.Equal("container", SavedEntry(dir, "orders")?["source"]?.GetValue<string>());
        Assert.Equal("repository", SavedEntry(dir, "billing")?["source"]?.GetValue<string>());

        var notification = Assert.Single(interaction.Notifications);
        Assert.Equal(MessageIntent.Success, notification.Options?.Intent);
        Assert.Contains("orders", notification.Message);
        Assert.Contains("billing", notification.Message);
        Assert.Contains("next time the AppHost starts", notification.Message);
    }

    [Fact]
    public async Task Run_KubernetesAnswer_RaisesFollowUpForContext()
    {
        var interaction = Answering(["kubernetes"], (_, inputs) => Fill(inputs, "dev", "8080"));

        var (dir, _) = await RunAsync(interaction, [Orders()]);

        Assert.Equal(2, interaction.PromptCount);
        var followUp = interaction.Prompts[1];
        Assert.NotEqual(UnconfiguredPrompt.Title, followUp.Title);
        Assert.Contains("orders", followUp.Title);
        Assert.Equal(["Kubernetes context", "Kubernetes service port"], followUp.Inputs.Take(2).Select(i => i.Label));

        var entry = SavedEntry(dir, "orders");
        Assert.Equal("kubernetes", entry?["source"]?.GetValue<string>());
        Assert.Equal("dev", entry?["kubernetes"]?["context"]?.GetValue<string>());
        Assert.Equal("8080", entry?["kubernetes"]?["port"]?.GetValue<string>());
    }

    [Fact]
    public async Task Run_ContainerAnswer_RaisesNoFollowUp()
    {
        var interaction = Answering(["container"]);

        await RunAsync(interaction, [Orders()]);

        Assert.Equal(1, interaction.PromptCount);
    }

    [Fact]
    public async Task Run_UnofferedChoice_RejectedNothingWritten()
    {
        var interaction = Answering(["container"]);

        var (dir, _) = await RunAsync(interaction, [Billing()]);

        Assert.False(File.Exists(LocalJson(dir)));
        var notification = Assert.Single(interaction.Notifications);
        Assert.Equal(MessageIntent.Error, notification.Options?.Intent);
        Assert.Contains("billing", notification.Message);
        Assert.Contains("not available", notification.Message);
    }

    [Fact]
    public async Task Run_OneInvalidFollowUp_OtherServiceStillSaved_FailureReported()
    {
        // A context with surrounding whitespace is refused by the developer-config validator.
        var interaction = Answering(["kubernetes", "repository"], (_, inputs) => Fill(inputs, " dev ", "80"));

        var (dir, logger) = await RunAsync(interaction, [Orders(), Billing()]);

        Assert.Null(SavedEntry(dir, "orders"));
        Assert.Equal("repository", SavedEntry(dir, "billing")?["source"]?.GetValue<string>());

        var success = Assert.Single(interaction.Notifications, n => n.Options?.Intent == MessageIntent.Success);
        Assert.Contains("billing", success.Message);
        Assert.DoesNotContain("orders", success.Message);

        var failure = Assert.Single(interaction.Notifications, n => n.Options?.Intent == MessageIntent.Error);
        Assert.Contains("orders", failure.Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("orders"));
    }

    [Fact]
    public async Task Run_NotificationsNotDismissed_RunCompletes_BothRaisedInOrder()
    {
        var interaction = Answering(["kubernetes", "repository"], (_, inputs) => Fill(inputs, " dev ", "80"));
        interaction.HoldNotifications = true;

        try
        {
            await RunAsync(interaction, [Orders(), Billing()]);

            Assert.Equal(
                [MessageIntent.Success, MessageIntent.Error],
                interaction.Notifications.Select(n => n.Options?.Intent));
        }
        finally
        {
            interaction.ReleaseNotifications();
        }
    }

    [Fact]
    public async Task Run_EveryServiceLeftBlank_LogsOneLineNamingThem_NoNotification()
    {
        var interaction = Answering([null, " "]);

        var (dir, logger) = await RunAsync(interaction, [Orders(), Billing()]);

        Assert.False(File.Exists(LocalJson(dir)));
        Assert.Empty(interaction.Notifications);

        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("orders", line.Message);
        Assert.Contains("billing", line.Message);
    }

    [Fact]
    public async Task Run_DialogClosed_WritesNothing_LogsOneLineNamingPlaceholders_NotReRaised()
    {
        var interaction = new FakeInteractionService
        {
            IsAvailable = true,
            Answer = _ => InteractionResult.Cancel<InteractionInputCollection>(),
        };

        var (dir, logger) = await RunAsync(interaction, [Orders(), Billing()]);

        Assert.Equal(1, interaction.PromptCount);
        Assert.False(File.Exists(LocalJson(dir)));
        Assert.Empty(interaction.Notifications);

        var line = Assert.Single(logger.Entries);
        Assert.Contains("orders", line.Message);
        Assert.Contains("billing", line.Message);
    }

    [Fact]
    public async Task Run_HostStopping_StartsNothing()
    {
        var interaction = new FakeInteractionService { IsAvailable = true };
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var logger = new ListLogger();
        using var stopping = new CancellationTokenSource();

        var run = UnconfiguredPrompt.RunAsync(
            Services(interaction), [Orders()], new JsonFileDeveloperConfigWriter(dir), dir, logger, stopping.Token);

        await interaction.Prompted.WaitAsync(Patience);

        // No deadline: the dialog stays open until the host stops.
        Assert.False(interaction.PromptToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);

        await stopping.CancelAsync();
        await run.WaitAsync(Patience);

        Assert.Equal(1, interaction.PromptCount);
        Assert.False(File.Exists(LocalJson(dir)));
        Assert.Empty(interaction.Notifications);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Run_InteractionUnavailable_LogsOneWarningNamingServicesAndKeys()
    {
        var interaction = new FakeInteractionService { IsAvailable = false };

        var (dir, logger) = await RunAsync(interaction, [Orders(), Billing()]);

        Assert.Equal(0, interaction.PromptCount);
        Assert.False(File.Exists(LocalJson(dir)));

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("ServiceSources:Services:orders:source", warning.Message);
        Assert.Contains("ServiceSources:Services:billing:source", warning.Message);
    }

    [Fact]
    public async Task Run_ShadowedByHigherLayer_StillSaves_NotificationNamesProvider()
    {
        var configuration = new ConfigurationBuilder()
            .Add(new DeveloperConfigFileSource.FileLayerSource())
            .AddJsonStream(new MemoryStream("""{ "ServiceSources": { "Services": { "orders": { "source": "" } } } }"""u8.ToArray()))
            .Build();
        var interaction = Answering(["container"]);

        var (dir, _) = await RunAsync(interaction, [Orders()], configuration);

        Assert.Equal("container", SavedEntry(dir, "orders")?["source"]?.GetValue<string>());

        var notification = Assert.Single(interaction.Notifications);
        Assert.Contains("JsonStreamConfigurationProvider", notification.Message);
        Assert.Contains("will not take effect", notification.Message);
    }

    private static string CatalogDirectory(string extraLocalJson = "")
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://inventory.example.com
              stock:
                url:
                  url: https://stock.example.com
              orders:
                repository: https://example.com/orders.git
                project: Service.csproj
            """);

        if (extraLocalJson.Length > 0)
        {
            File.WriteAllText(Path.Combine(dir, DeveloperConfiguration.FileName), extraLocalJson);
        }

        return dir;
    }

    private static IDistributedApplicationBuilder Builder(string dir, FakeInteractionService interaction)
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.Services.AddSingleton<IInteractionService>(interaction);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CI"] = "",
            ["TF_BUILD"] = "",
            ["JENKINS_URL"] = "",
            [InteractivePrompts.EnvironmentVariable] = "true",
        });
        builder.UseInteractiveServiceConfiguration();
        return builder;
    }

    private static async Task<IServiceProvider> PublishBeforeStartAsync(IDistributedApplicationBuilder builder)
    {
        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        return services;
    }

    [Fact]
    public async Task Subscription_PlaceholdersExist_RaisesPromptOnce_NoPlaceholders_RaisesNone()
    {
        var withPlaceholder = new FakeInteractionService
        {
            IsAvailable = true,
            Answer = _ => InteractionResult.Cancel<InteractionInputCollection>(),
        };
        var builder = Builder(CatalogDirectory(), withPlaceholder);
        builder.AddService("inventory");
        builder.AddService("stock");

        await PublishBeforeStartAsync(builder);
        await Task.WhenAll(UnconfiguredRegistry.For(builder).PromptTasks).WaitAsync(Patience);

        _ = Assert.Single(UnconfiguredRegistry.For(builder).PromptTasks);
        Assert.Equal(1, withPlaceholder.PromptCount);
        Assert.Equal(UnconfiguredPrompt.Title, withPlaceholder.Title);
        Assert.Equal(["inventory", "stock"], withPlaceholder.Inputs.Select(i => i.Label));

        var withoutPlaceholder = new FakeInteractionService { IsAvailable = true };
        var configured = Builder(
            CatalogDirectory("""{ "services": { "inventory": { "source": "url" } } }"""), withoutPlaceholder);
        configured.AddService("inventory");

        await PublishBeforeStartAsync(configured);

        Assert.Empty(UnconfiguredRegistry.For(configured).PromptTasks);
        Assert.Equal(0, withoutPlaceholder.PromptCount);

        // Turned off after composition, so the placeholder exists but nobody is to be asked.
        var switchedOff = new FakeInteractionService { IsAvailable = true };
        var silenced = Builder(CatalogDirectory(), switchedOff);
        silenced.AddService("inventory");
        silenced.Configuration[InteractivePrompts.EnvironmentVariable] = "false";
        var log = TestHelpers.CaptureServiceSourcesLog(silenced);

        await PublishBeforeStartAsync(silenced);

        Assert.Empty(UnconfiguredRegistry.For(silenced).PromptTasks);
        Assert.Equal(0, switchedOff.PromptCount);
        Assert.Contains(log, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("ServiceSources:Services:inventory:source"));
    }

    [Fact]
    public void Resolve_DuplicateNameThatThrows_IsRecordedOnce()
    {
        var builder = Builder(CatalogDirectory(), new FakeInteractionService());
        var definition = Definition("inventory");

        UnconfiguredSource.Resolve(builder, "inventory", definition);
        Assert.ThrowsAny<Exception>(() => UnconfiguredSource.Resolve(builder, "inventory", definition));

        Assert.Single(UnconfiguredRegistry.For(builder).Services);
    }

    [Fact]
    public async Task WaitFor_Placeholder_ConsumerStartsAndPromptStillRaised()
    {
        var interaction = new FakeInteractionService { IsAvailable = true };
        var builder = Builder(CatalogDirectory(), interaction);

        var inventory = builder.AddService("inventory");
        var worker = builder.AddExecutable("worker", "dotnet", TempDirectories.CreateSubdirectory().FullName)
            .WithReference(inventory)
            .WaitFor(inventory);

        var services = await PublishBeforeStartAsync(builder);

        try
        {
            using var cts = new CancellationTokenSource(Patience);
            await services.GetRequiredService<ResourceNotificationService>()
                .WaitForDependenciesAsync(worker.Resource, cts.Token);

            await interaction.Prompted.WaitAsync(Patience);
            Assert.Equal(UnconfiguredPrompt.Title, interaction.Title);
        }
        finally
        {
            services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await Task.WhenAll(UnconfiguredRegistry.For(builder).PromptTasks).WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task TwoPrompts_PartlyConfigured_BothRaisedForTheirOwnServices()
    {
        var interaction = new FakeInteractionService { IsAvailable = true };
        // Orders is configured in code rather than in the file, so no speculative prefetch reaches real git.
        var builder = Builder(CatalogDirectory(), interaction);
        builder.SetCheckoutTiming(CheckoutTiming.Deferred);

        new LocalProjectSource(new ScriptedCloneGitClient())
            .Resolve(builder, "orders", Definition("orders"), new ServiceDeveloperConfig { Source = "repository" })
            .WithHttpEndpoint();
        builder.AddService("inventory");

        var services = await PublishBeforeStartAsync(builder);

        try
        {
            var unconfigured = await interaction.PromptedWithTitle(UnconfiguredPrompt.Title).WaitAsync(Patience);
            var sourcePick = await interaction.PromptedWithTitle("Choose services to check out").WaitAsync(Patience);

            Assert.Equal(["inventory"], unconfigured.Inputs.Select(i => i.Label));
            Assert.Equal(["orders"], sourcePick.Inputs.Select(i => i.Label));

            // Measured against the fake only: both PromptInputsAsync calls were outstanding at the same
            // time, each started from its own BeforeStartEvent handler on its own task. Whether the real
            // dashboard shows them together or queues one behind the other is not measured here.
            Assert.Equal(2, interaction.PromptCount);
            Assert.False(unconfigured.Token.IsCancellationRequested);
            Assert.False(sourcePick.Token.IsCancellationRequested);
        }
        finally
        {
            services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await Task.WhenAll(UnconfiguredRegistry.For(builder).PromptTasks).WaitAsync(Patience);
            await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(Patience);
        }
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
