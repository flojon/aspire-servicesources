namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// A scripted <see cref="IInteractionService"/>. Unavailable by default, which is what a headless
/// run looks like and what every test that does not care about the prompt wants.
/// </summary>
internal sealed class FakeInteractionService : IInteractionService
{
    private readonly TaskCompletionSource<bool> _prompted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsAvailable { get; set; }

    /// <summary>Answers the prompt. Null leaves it open until its token is cancelled.</summary>
    public Func<IReadOnlyList<InteractionInput>, InteractionResult<InteractionInputCollection>>? Answer { get; set; }

    /// <summary>Throws instead of answering, as the real service does when no dashboard is enabled.</summary>
    public Exception? Throw { get; set; }

    public int PromptCount { get; private set; }

    public string? Title { get; private set; }

    public string? Message { get; private set; }

    public IReadOnlyList<InteractionInput> Inputs { get; private set; } = [];

    public InputsDialogInteractionOptions? Options { get; private set; }

    public CancellationToken PromptToken { get; private set; }

    /// <summary>Completes when the prompt has been shown.</summary>
    public Task Prompted => _prompted.Task;

    public async Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(
        string title, string? message, IReadOnlyList<InteractionInput> inputs,
        InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        PromptCount++;
        Title = title;
        Message = message;
        Inputs = inputs;
        Options = options;
        PromptToken = cancellationToken;
        _prompted.TrySetResult(true);

        if (Throw is not null)
        {
            throw Throw;
        }

        if (Answer is not null)
        {
            return Answer(inputs);
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return InteractionResult.Cancel<InteractionInputCollection>();
    }

#pragma warning disable ASPIREINTERACTION001 // the interface member itself is experimental; a fake has to name it
    public Task<InteractionResult<bool>> PromptProgressAsync(
        string title, ProgressInteractionOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
#pragma warning restore ASPIREINTERACTION001

    /// <summary>Answers a confirmation. Null leaves it open until its token is cancelled.</summary>
    public Func<InteractionResult<bool>>? Confirm { get; set; }

    public int ConfirmCount { get; private set; }

    private readonly TaskCompletionSource<bool> _confirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a confirmation has been shown.</summary>
    public Task WaitForConfirmAsync() => _confirmed.Task;

    public string? ConfirmMessage { get; private set; }

    public MessageBoxInteractionOptions? ConfirmOptions { get; private set; }

    public async Task<InteractionResult<bool>> PromptConfirmationAsync(
        string title, string message, MessageBoxInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ConfirmCount++;
        _confirmed.TrySetResult(true);
        ConfirmMessage = message;
        ConfirmOptions = options;

        if (Throw is not null)
        {
            throw Throw;
        }

        if (Confirm is not null)
        {
            return Confirm();
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return InteractionResult.Cancel<bool>();
    }

    public Task<InteractionResult<bool>> PromptMessageBoxAsync(
        string title, string message, MessageBoxInteractionOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<InteractionResult<bool>> PromptNotificationAsync(
        string title, string message, NotificationInteractionOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(
        string title, string? message, string inputLabel, string placeHolder,
        InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(
        string title, string? message, InteractionInput input,
        InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
