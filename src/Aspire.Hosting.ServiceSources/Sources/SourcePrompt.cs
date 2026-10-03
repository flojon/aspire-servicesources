using System.Globalization;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>What one undecided service's checkbox came back as.</summary>
internal enum SourceAnswer
{
    /// <summary>Checked: clone and start it.</summary>
    Start,

    /// <summary>Unchecked: do not clone it.</summary>
    Skip,

    /// <summary>No usable answer for this service; it starts, and the caller warns.</summary>
    StartUnanswered,
}

/// <summary>The dialog <see cref="SourcePrompt.Build"/> describes.</summary>
internal sealed record SourcePromptContent(
    string Title, string Message, IReadOnlyList<InteractionInput> Inputs, InputsDialogInteractionOptions Options);

/// <summary>
/// Builds the "Choose services to check out" dialog and maps its answer back onto services.
/// </summary>
/// <remarks>
/// Every value shown comes from configuration the developer or a catalog author wrote, so it goes
/// through the escaping seam; the dialog is plain text on purpose, because markdown in a repository
/// URL or a service name would otherwise render as links and emphasis.
/// </remarks>
internal static class SourcePrompt
{
    private const int MaxUrlLength = 200;

    private const int MaxRefLength = 64;

    /// <summary>The input name for the service at <paramref name="index"/>; service names are free text.</summary>
    public static string InputName(int index) => $"s{index}";

    public static SourcePromptContent Build(
        IReadOnlyList<DeferredCheckout.Deferred> undecided,
        IReadOnlyDictionary<string, Raw> dependents,
        DateTimeOffset deadline,
        TimeSpan timeout)
    {
        var inputs = new List<InteractionInput>(undecided.Count);

        for (var i = 0; i < undecided.Count; i++)
        {
            var service = undecided[i];
            dependents.TryGetValue(service.ServiceName, out var waiters);

            inputs.Add(new InteractionInput
            {
                Name = InputName(i),
                Label = Raw.Escaped(service.ServiceName).ToString(),
                Description = Describe(service, waiters),
                InputType = InputType.Boolean,
                Value = "true",
                EnableDescriptionMarkdown = false,
            });
        }

        var message =
            $"Choose which of these services to clone and start. The choice is saved in "
            + $"{ToolDirectory.Name}/{SourceSelectionStore.FileName}. "
            + $"Answer within {Minutes(timeout)} (by {Clock(deadline)}); after that all of these start this run "
            + $"and nothing is saved. Closing this dialog does the same.";

        return new SourcePromptContent(
            "Choose services to check out",
            message,
            inputs,
            new InputsDialogInteractionOptions { PrimaryButtonText = "Apply", EnableMessageMarkdown = false });
    }

    /// <summary>
    /// The State column text for a service waiting on the dialog. Names the same deadline as the
    /// dialog message so the two cannot disagree.
    /// </summary>
    public static string AwaitingState(DateTimeOffset deadline) =>
        $"{AwaitingStatePrefix} (starts automatically at {Clock(deadline)})";

    /// <summary>What every awaiting state text starts with, for code that has to recognise one.</summary>
    public const string AwaitingStatePrefix = "Awaiting source selection";

    /// <summary>
    /// One answer per service, in the order of <paramref name="undecided"/>. A missing, renamed or
    /// non-boolean entry is <see cref="SourceAnswer.StartUnanswered"/>: starting is the pre-prompt
    /// behaviour, so an answer nobody can read never silently skips a service.
    /// </summary>
    public static IReadOnlyList<SourceAnswer> Map(
        InteractionResult<InteractionInputCollection> result, IReadOnlyList<DeferredCheckout.Deferred> undecided)
    {
        var answers = new SourceAnswer[undecided.Count];

        for (var i = 0; i < undecided.Count; i++)
        {
            answers[i] = result.Data is { } data
                && data.TryGetByName(InputName(i), out var input)
                && bool.TryParse(input.Value, out var start)
                    ? (start ? SourceAnswer.Start : SourceAnswer.Skip)
                    : SourceAnswer.StartUnanswered;
        }

        return answers;
    }

    private static string Describe(DeferredCheckout.Deferred service, Raw? waiters)
    {
        var url = ShownUrl(service.Definition.Repository.Url);
        var reference = service.RepositoryConfig?.Ref
            ?? service.Config.Repository.Ref
            ?? service.Definition.Repository.DefaultRef;

        var from = reference is null
            ? Raw.Compose($"Clone and start from {Raw.Escaped(Cap(url, MaxUrlLength))}.")
            : Raw.Compose($"Clone and start from {Raw.Escaped(Cap(url, MaxUrlLength))} at {Raw.Escaped(Cap(reference, MaxRefLength))}.");

        return waiters is { } line ? Raw.Compose($"{from} {line}").ToString() : from.ToString();
    }

    /// <summary>
    /// Scheme, host and path only. <see cref="GitUrl.Redact"/> removes userinfo but leaves a query
    /// string, where a token can also sit.
    /// </summary>
    internal static string ShownUrl(string repositoryUrl)
    {
        var redacted = GitUrl.Redact(repositoryUrl);
        var end = redacted.AsSpan().IndexOfAny('?', '#');

        return end < 0 ? redacted : redacted[..end];
    }

    private static string Cap(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut] + "…";
    }

    private static string Clock(DateTimeOffset deadline) =>
        deadline.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan timeout)
    {
        var minutes = (int)Math.Round(timeout.TotalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
