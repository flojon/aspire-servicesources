using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Runs a resource's environment and argument callbacks the way DCP does and returns the raw values
/// they produced, unresolved, so a test can tell a literal from an Aspire value provider.
/// </summary>
internal static class CallbackRunner
{
    public static async Task<Dictionary<string, object>> EnvironmentAsync(IResource resource)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray())
        {
            await annotation.Callback(context);
        }

        return context.EnvironmentVariables;
    }

    public static async Task<IList<object>> ArgsAsync(IResource resource)
    {
        var context = new CommandLineArgsCallbackContext([], resource);

        foreach (var annotation in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToArray())
        {
            await annotation.Callback(context);
        }

        return context.Args;
    }
}
