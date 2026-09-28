using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Dotnet;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Adds <c>dotnet</c> launch profile options to code-declared services.
/// </summary>
public static class DotnetServiceSourcesBuilderExtensions
{
    /// <summary>
    /// Configures how this code-declared <c>dotnet</c> service picks its launch profile: a named
    /// <c>launchSettings.json</c> profile, or none at all. The code counterpart of yaml's
    /// <c>dotnet:</c> block. Unlike <c>AsJava</c> this does not call <c>WithKind</c>, because
    /// <c>dotnet</c> is already the default kind.
    /// </summary>
    /// <example>
    /// <code>
    /// catalog.AddService("orders")
    ///     .WithRepository("https://github.com/company/orders")
    ///     .WithProject("src/Orders.Api/Orders.Api.csproj")
    ///     .AsDotnet(o => o.WithLaunchProfileName("http"));
    /// </code>
    /// </example>
    [AspireExport]
    public static ServiceDefinitionBuilder AsDotnet(
        this ServiceDefinitionBuilder builder, Action<DotnetKindOptionsBuilder> configure)
    {
        var options = new DotnetKindOptionsBuilder(builder.ServiceName);
        configure(options);
        return builder.WithDotnet(options.Build());
    }
}
