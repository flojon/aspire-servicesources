using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AtsCallbackExportTests
{
    // A synchronous delegate invoked on the RPC thread of a guest-language AppHost deadlocks: the
    // guest's calls back into the host can't be served, so the callback times out after 60s.
    [Fact]
    public void ExportsTakingASynchronousCallback_RunOnABackgroundThread()
    {
        var offenders = typeof(ServiceSourcesBuilderExtensions).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<AspireExportAttribute>() is not null)
            .Where(m => m.GetParameters().Any(p => IsSynchronousDelegate(p.ParameterType)))
            .Where(m => !m.GetCustomAttribute<AspireExportAttribute>()!.RunSyncOnBackgroundThread)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(typeof(ServiceSourcesBuilderExtensions), nameof(ServiceSourcesBuilderExtensions.AddServiceCatalog))]
    [InlineData(typeof(JavaServiceSourcesBuilderExtensions), nameof(JavaServiceSourcesBuilderExtensions.AsJava))]
    [InlineData(typeof(JavaScriptServiceSourcesBuilderExtensions), nameof(JavaScriptServiceSourcesBuilderExtensions.AsJavaScript))]
    public void NestedCallbackExports_AreStillExported(Type type, string methodName)
    {
        var method = type.GetMethod(methodName)!;

        Assert.True(method.GetCustomAttribute<AspireExportAttribute>()!.RunSyncOnBackgroundThread);
    }

    private static bool IsSynchronousDelegate(Type type) =>
        typeof(Delegate).IsAssignableFrom(type)
        && !typeof(Task).IsAssignableFrom(type.GetMethod("Invoke")!.ReturnType);
}
