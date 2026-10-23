using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The placeholder's <c>Configure</c> command: the startup dialog, for this one service, on demand.
/// </summary>
internal sealed class UnconfiguredConfigureCommand(Unconfigured service, IDeveloperConfigWriter writer)
{
    public const string Name = "configure";

    public const string DisplayName = "Configure";

    private int _open;

    public Task<ExecuteCommandResult> ExecuteAsync(ExecuteCommandContext context) =>
        ExecuteAsync(context.Services, context.Logger, context.CancellationToken);

    public async Task<ExecuteCommandResult> ExecuteAsync(
        IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        // Deliberately not gated on InteractivePrompts: that silences the unprompted startup dialog on CI,
        // but a click is a person asking.
        var interaction = services.GetService<IInteractionService>();
        if (interaction is null || !interaction.IsAvailable)
        {
            return Failure("There is no dashboard to ask in, so the source cannot be chosen here.");
        }

        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
        {
            return Failure($"The source dialog for '{service.ServiceName}' is already open.");
        }

        try
        {
            await UnconfiguredPrompt.RunAsync(services, [service], writer, logger, cancellationToken).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Failure("The source dialog was cancelled.")
                : new ExecuteCommandResult { Success = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("The source dialog was cancelled.");
        }
        catch (Exception ex)
        {
            var message = Raw.Compose(
                $"The source dialog for '{new Name(service.ServiceName)}' failed: {Raw.Cause(ex)} Nothing more was saved.");
            ServiceSourcesLog.Warning(logger, $"{message}");
            return Failure(message.ToString());
        }
        finally
        {
            Volatile.Write(ref _open, 0);
        }
    }

    private static ExecuteCommandResult Failure(string message) =>
        new() { Success = false, Message = message };
}
