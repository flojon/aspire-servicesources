using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>A declared service no configuration layer gave a source, as its placeholder recorded it.</summary>
internal sealed record Unconfigured(string ServiceName, ServiceDefinition Definition);

/// <summary>
/// Asks for a source for each unconfigured service, then saves the valid answers to
/// <c>servicesources.local.json</c> for the next run.
/// </summary>
/// <remarks>
/// Plain text throughout, for the reason <see cref="SourcePrompt"/> gives: service names come from
/// configuration and would otherwise render as markdown.
/// </remarks>
internal static class UnconfiguredPrompt
{
    public const string Title = "Choose a source for unconfigured services";

    public static string InputName(int index) => $"s{index}";

    private static string FieldName(int index) => $"f{index}";

    public static SourcePromptContent Build(IReadOnlyList<Unconfigured> services)
    {
        var inputs = new List<InteractionInput>(services.Count);

        for (var i = 0; i < services.Count; i++)
        {
            inputs.Add(new InteractionInput
            {
                Name = InputName(i),
                Label = Raw.Escaped(services[i].ServiceName).ToString(),
                InputType = InputType.Choice,
                Options = [.. SourceChoices.For(services[i].Definition).Select(source => KeyValuePair.Create(source, source))],
                EnableDescriptionMarkdown = false,
            });
        }

        var message =
            $"These services have no source, so they are not running. Choose one for each; the choice is saved in "
            + $"{DeveloperConfiguration.FileName} and takes effect the next time the AppHost starts. Leave a service "
            + $"blank to decide later.";

        return new SourcePromptContent(
            Title,
            message,
            inputs,
            new InputsDialogInteractionOptions { PrimaryButtonText = "Save", EnableMessageMarkdown = false });
    }

    /// <summary>
    /// The whole flow: the dialog, a follow-up per answer that needs more, validation, the save and the
    /// notification. Only <paramref name="cancellationToken"/> or the host stopping ends the wait.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        IReadOnlyList<Unconfigured> unconfigured,
        IDeveloperConfigWriter writer,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (unconfigured.Count == 0)
        {
            return;
        }

        var interaction = services.GetService<IInteractionService>();
        if (interaction is null || !interaction.IsAvailable)
        {
            WarnNotAsked(logger, unconfigured, Raw.Literal("there is no dashboard to ask in"));
            return;
        }

        var stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        var token = linked.Token;

        var content = Build(unconfigured);
        InteractionResult<InteractionInputCollection> result;

        try
        {
            result = await interaction
                .PromptInputsAsync(content.Title, content.Message, content.Inputs, content.Options, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (result.Canceled || result.Data is not { } data)
        {
            ServiceSourcesLog.Information(
                logger,
                $"The source dialog was closed, so nothing was saved and {Names(unconfigured)} stay unconfigured this run. "
                + $"{HowToConfigure}");
            return;
        }

        var saved = new List<Raw>();
        var failed = new List<Raw>();
        var answered = 0;

        for (var i = 0; i < unconfigured.Count; i++)
        {
            if (!data.TryGetByName(InputName(i), out var input) || string.IsNullOrWhiteSpace(input.Value))
            {
                continue;
            }

            answered++;

            var outcome = await SaveOneAsync(
                interaction, services, unconfigured[i], input.Value.Trim(), writer, logger, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (outcome is { Saved: true } ok)
            {
                saved.Add(ok.Line);
            }
            else if (outcome is { } failure)
            {
                failed.Add(failure.Line);
            }
        }

        if (answered == 0)
        {
            ServiceSourcesLog.Information(
                logger,
                $"No source was chosen in the source dialog, so nothing was saved and {Names(unconfigured)} stay unconfigured this run. "
                + $"{HowToConfigure}");
            return;
        }

        Notify(interaction, saved, failed, logger, token);
    }

    private sealed record Outcome(bool Saved, Raw Line);

    /// <returns><see langword="null"/> when the developer closed this service's follow-up.</returns>
    private static async Task<Outcome?> SaveOneAsync(
        IInteractionService interaction,
        IServiceProvider services,
        Unconfigured service,
        string answer,
        IDeveloperConfigWriter writer,
        ILogger logger,
        CancellationToken token)
    {
        var name = new Name(service.ServiceName);

        // The sources' own spelling, since the follow-up rules and the file both match on it.
        var source = SourceChoices.For(service.Definition)
            .FirstOrDefault(offered => string.Equals(offered, answer, StringComparison.OrdinalIgnoreCase)) ?? answer;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var followUp = SourceFollowUp.FieldsFor(source, service.Definition);

        if (followUp.Count > 0)
        {
            var answered = await AskFollowUpAsync(interaction, service, source, followUp, token).ConfigureAwait(false);
            if (answered is null)
            {
                if (!token.IsCancellationRequested)
                {
                    ServiceSourcesLog.Information(
                        logger, $"The details dialog for service '{name}' was closed, so its source was not saved.");
                }

                return null;
            }

            fields = answered;
        }

        if (PickerAnswers.Validate(service.ServiceName, source, fields, service.Definition) is { } invalid)
        {
            return Failed(logger, name, Raw.Output(invalid));
        }

        var shadowedBy = services.GetService<IConfiguration>() is IConfigurationRoot root
            ? PickerAnswers.ShadowingProvider(root, service.ServiceName)
            : null;

        var result = await writer.SaveAsync(service.ServiceName, source, fields, token).ConfigureAwait(false);
        if (!result.Saved)
        {
            return Failed(logger, name, Raw.Output(result.Error));
        }

        var line = shadowedBy is null
            ? Raw.Compose($"'{name}' ({Raw.Escaped(source)})")
            : Raw.Compose($"'{name}' ({Raw.Escaped(source)}), which will not take effect until {Raw.Output(shadowedBy)} "
                + $"stops setting '{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{Raw.Escaped(service.ServiceName)}:source' to blank");

        ServiceSourcesLog.Information(logger, $"Saved source {line} to {Raw.Literal(DeveloperConfiguration.FileName)}.");

        return new Outcome(true, line);
    }

    private static Outcome Failed(ILogger logger, Name name, Raw reason)
    {
        ServiceSourcesLog.Warning(logger, $"The source chosen for service '{name}' was not saved: {reason}");
        return new Outcome(false, Raw.Compose($"'{name}': {reason}"));
    }

    private static async Task<Dictionary<string, string>?> AskFollowUpAsync(
        IInteractionService interaction,
        Unconfigured service,
        string source,
        IReadOnlyList<FollowUpField> fields,
        CancellationToken token)
    {
        var inputs = fields.Select((field, j) => new InteractionInput
        {
            Name = FieldName(j),
            Label = field.Label,
            InputType = field.InputType,
            Required = field.Required,
            EnableDescriptionMarkdown = false,
        }).ToList();

        InteractionResult<InteractionInputCollection> result;

        try
        {
            result = await interaction.PromptInputsAsync(
                    Raw.Compose($"Details for '{new Name(service.ServiceName)}' ({Raw.Escaped(source)})").ToString(),
                    Raw.Compose($"Source '{Raw.Escaped(source)}' needs these answers before it can be saved.").ToString(),
                    inputs,
                    new InputsDialogInteractionOptions { PrimaryButtonText = "Save", EnableMessageMarkdown = false },
                    token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }

        if (result.Canceled || result.Data is not { } data)
        {
            return null;
        }

        var answers = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var j = 0; j < fields.Count; j++)
        {
            if (data.TryGetByName(FieldName(j), out var input) && !string.IsNullOrWhiteSpace(input.Value))
            {
                // Untrimmed, so the validator sees and refuses stray whitespace rather than it being hidden.
                answers[fields[j].Key] = input.Value;
            }
        }

        return answers;
    }

    // Raised, not awaited: a notification completes only when dismissed, and nothing here depends on that.
    private static void Notify(
        IInteractionService interaction, List<Raw> saved, List<Raw> failed, ILogger logger, CancellationToken token)
    {
        if (saved.Count > 0)
        {
            Raise(
                interaction,
                "Source saved",
                Raw.Compose($"Saved {Raw.Join("; ", saved)} to {Raw.Literal(DeveloperConfiguration.FileName)}. "
                    + $"This run is unchanged; the choice takes effect the next time the AppHost starts."),
                MessageIntent.Success,
                logger,
                token);
        }

        if (failed.Count > 0)
        {
            Raise(
                interaction, "Source not saved", Raw.Compose($"Not saved: {Raw.Join("; ", failed)}"),
                MessageIntent.Error, logger, token);
        }
    }

    private static void Raise(
        IInteractionService interaction, string title, Raw message, MessageIntent intent, ILogger logger,
        CancellationToken token)
    {
        Task shown;

        try
        {
            shown = interaction.PromptNotificationAsync(
                title,
                message.ToString(),
                new NotificationInteractionOptions { Intent = intent, EnableMessageMarkdown = false },
                token);
        }
        catch (Exception ex)
        {
            shown = Task.FromException(ex);
        }

        _ = shown.ContinueWith(
            static (task, state) =>
            {
                try
                {
                    ServiceSourcesLog.Warning(
                        (ILogger)state!, $"A source notification could not be shown: {Raw.Cause(task.Exception!.GetBaseException())}");
                }
                catch (Exception)
                {
                    // Logging is already gone; the host is going down.
                }
            },
            logger,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>One warning naming every service and the key that would configure it.</summary>
    internal static void WarnNotAsked(ILogger logger, IReadOnlyList<Unconfigured> unconfigured, Raw because)
    {
        var keys = Raw.Join(", ", unconfigured.Select(service => Raw.Compose(
            $"'{new Name(service.ServiceName)}' (set '{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{Raw.Escaped(service.ServiceName)}:source')")));

        ServiceSourcesLog.Warning(
            logger,
            $"No source was asked for, because {because}, so {unconfigured.Count} service(s) stay unconfigured: {keys}.");
    }

    // The command is the way back in once the dialog is gone; the key is the way for CI and scripts.
    private static Raw HowToConfigure =>
        Raw.Compose($"Use Configure on the service's row in the dashboard, or set "
            + $"'{Raw.Literal(DeveloperConfiguration.ServicesKey)}:<name>:source'.");

    private static Raw Names(IReadOnlyList<Unconfigured> unconfigured) =>
        Raw.Join(", ", unconfigured.Select(service => Raw.Compose($"'{new Name(service.ServiceName)}'")));
}
