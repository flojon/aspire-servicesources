namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// A scripted <see cref="IInteractionService"/>. Unavailable by default, which is what a headless
/// run looks like and what every test that does not care about the prompt wants.
/// </summary>
internal sealed class FakeInteractionService : IInteractionService
{
    private readonly TaskCompletionSource<bool> _prompted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _gate = new();

    private readonly List<RecordedPrompt> _prompts = [];

    private readonly List<RecordedNotification> _notifications = [];

    private readonly Dictionary<string, TaskCompletionSource<RecordedPrompt>> _promptedWithTitle = [];

    private int _promptCount;

    public bool IsAvailable { get; set; }

    /// <summary>Answers the prompt. Null leaves it open until its token is cancelled.</summary>
    public Func<IReadOnlyList<InteractionInput>, InteractionResult<InteractionInputCollection>>? Answer { get; set; }

    /// <summary>
    /// Answers a prompt by its title, ahead of <see cref="Answer"/>. Returning null falls through to it.
    /// </summary>
    public Func<string, IReadOnlyList<InteractionInput>, InteractionResult<InteractionInputCollection>?>? AnswerFor { get; set; }

    /// <summary>Throws instead of answering, as the real service does when no dashboard is enabled.</summary>
    public Exception? Throw { get; set; }

    public int PromptCount => Volatile.Read(ref _promptCount);

    public string? Title { get; private set; }

    public string? Message { get; private set; }

    public IReadOnlyList<InteractionInput> Inputs { get; private set; } = [];

    public InputsDialogInteractionOptions? Options { get; private set; }

    public CancellationToken PromptToken { get; private set; }

    /// <summary>Every input prompt shown, in the order it was raised.</summary>
    public IReadOnlyList<RecordedPrompt> Prompts
    {
        get
        {
            lock (_gate)
            {
                return [.. _prompts];
            }
        }
    }

    /// <summary>Every notification shown, in the order it was raised.</summary>
    public IReadOnlyList<RecordedNotification> Notifications
    {
        get
        {
            lock (_gate)
            {
                return [.. _notifications];
            }
        }
    }

    /// <summary>Completes when the prompt has been shown.</summary>
    public Task Prompted => _prompted.Task;

    /// <summary>Completes when a prompt titled <paramref name="title"/> has been shown.</summary>
    public Task<RecordedPrompt> PromptedWithTitle(string title)
    {
        lock (_gate)
        {
            return WaiterFor(title).Task;
        }
    }

    private TaskCompletionSource<RecordedPrompt> WaiterFor(string title)
    {
        if (!_promptedWithTitle.TryGetValue(title, out var waiter))
        {
            waiter = new TaskCompletionSource<RecordedPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
            _promptedWithTitle[title] = waiter;
        }

        return waiter;
    }

    public async Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(
        string title, string? message, IReadOnlyList<InteractionInput> inputs,
        InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _promptCount);
        var recorded = new RecordedPrompt(title, message, inputs, options, cancellationToken);

        TaskCompletionSource<RecordedPrompt> waiter;
        lock (_gate)
        {
            Title = title;
            Message = message;
            Inputs = inputs;
            Options = options;
            PromptToken = cancellationToken;
            _prompts.Add(recorded);
            waiter = WaiterFor(title);
        }

        _prompted.TrySetResult(true);
        waiter.TrySetResult(recorded);

        if (Throw is not null)
        {
            throw Throw;
        }

        if (AnswerFor?.Invoke(title, inputs) is { } scripted)
        {
            return scripted;
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

#pragma warning disable ASPIRETERMINAL001 // experimental too, for the same reason
    public Task<InteractionResult<bool>> PromptTerminalAsync(
        string message, ApplicationModel.AspireTerminal terminal, TerminalInteractionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
#pragma warning restore ASPIRETERMINAL001
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

    /// <summary>
    /// Keeps every notification open until <see cref="ReleaseNotifications"/>, as the real service does
    /// until the user dismisses it.
    /// </summary>
    public bool HoldNotifications { get; set; }

    private readonly TaskCompletionSource<InteractionResult<bool>> _notificationsReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseNotifications() => _notificationsReleased.TrySetResult(InteractionResult.Ok(true));

    public Task<InteractionResult<bool>> PromptNotificationAsync(
        string title, string message, NotificationInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _notifications.Add(new RecordedNotification(title, message, options));
        }

        return HoldNotifications ? _notificationsReleased.Task : Task.FromResult(InteractionResult.Ok(true));
    }

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(
        string title, string? message, string inputLabel, string placeHolder,
        InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(
        string title, string? message, InteractionInput input,
        InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed record RecordedPrompt(
    string Title,
    string? Message,
    IReadOnlyList<InteractionInput> Inputs,
    InputsDialogInteractionOptions? Options,
    CancellationToken Token);

internal sealed record RecordedNotification(string Title, string Message, NotificationInteractionOptions? Options);
