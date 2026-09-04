namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// Where a <c>prepare</c> step's account of itself goes: why it is running, what it runs, and every
/// line the command writes as it writes it.
/// </summary>
/// <remarks>
/// An interface because the two paths have different reporting surfaces available, and this design
/// uses each one's best rather than reducing both to the console. During composition there is no
/// <c>ILogger</c> yet — both <c>LocalCheckoutPrefetch</c> and <c>ServiceConfigurationWarnings</c>
/// buffer their notices to <c>BeforeStartEvent</c> for that reason — so the eager path writes to the
/// console. A deferred service is the opposite case: its checkout runs on a task that already holds
/// <c>ResourceLoggerService.GetLogger(resource)</c> and publishes resource state, so the same lines
/// reach the service's own resource log and are visible in the dashboard, which is where a
/// country-sized import has to read as an initialization phase rather than as an apparent hang.
/// </remarks>
internal interface IPrepareOutputSink
{
    void Report(string line);
}

/// <summary>
/// The eager path's sink: straight to the console, because composition has no logger to write to.
/// </summary>
/// <remarks>
/// Under <c>dotnet run</c> the lines land in the terminal. Under <c>aspire run</c> they do not
/// appear there at all — the CLI captures the AppHost's standard output and writes it, live and
/// timestamped, into its own log under <c>~/.aspire/logs/</c>. Measured rather than assumed, and it
/// settles the one question this presentation rested on: the stream is not buffered, so a line
/// written while a four-minute bootstrap runs is relayed the moment it is written and nothing here
/// needs a heartbeat to look alive.
/// </remarks>
internal sealed class ConsolePrepareOutputSink : IPrepareOutputSink
{
    public static readonly ConsolePrepareOutputSink Instance = new();

    private ConsolePrepareOutputSink()
    {
    }

    public void Report(string line) => Console.Out.WriteLine(line);
}
