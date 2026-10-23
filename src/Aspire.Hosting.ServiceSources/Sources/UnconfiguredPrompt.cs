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
                + $"Set '{Raw.Literal(DeveloperConfiguration.ServicesKey)}:<name>:source' to configure them.");
            return;
        }

        var saved = new List<Raw>();
        var failed = new List<Raw>();

        for (var i = 0; i < unconfigured.Count; i++)
        {
            if (!data.TryGetByName(InputName(i), out var input) || string.IsNullOrWhiteSpace(input.Value))
            {
                continue;
            }

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

        await NotifyAsync(interaction, saved, failed, token).ConfigureAwait(false);
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

    private static async Task NotifyAsync(
        IInteractionService interaction, List<Raw> saved, List<Raw> failed, CancellationToken token)
    {
        if (saved.Count > 0)
        {
            await interaction.PromptNotificationAsync(
                    "Source saved",
                    Raw.Compose($"Saved {Raw.Join("; ", saved)} to {Raw.Literal(DeveloperConfiguration.FileName)}. "
                        + $"This run is unchanged; the choice takes effect the next time the AppHost starts.").ToString(),
                    new NotificationInteractionOptions { Intent = MessageIntent.Success, EnableMessageMarkdown = false },
                    token)
                .ConfigureAwait(false);
        }

        if (failed.Count > 0)
        {
            await interaction.PromptNotificationAsync(
                    "Source not saved",
                    Raw.Compose($"Not saved: {Raw.Join("; ", failed)}").ToString(),
                    new NotificationInteractionOptions { Intent = MessageIntent.Error, EnableMessageMarkdown = false },
                    token)
                .ConfigureAwait(false);
        }
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

    private static Raw Names(IReadOnlyList<Unconfigured> unconfigured) =>
        Raw.Join(", ", unconfigured.Select(service => Raw.Compose($"'{new Name(service.ServiceName)}'")));
}
