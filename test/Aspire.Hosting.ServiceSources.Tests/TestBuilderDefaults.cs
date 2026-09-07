namespace Aspire.Hosting.ServiceSources.Tests;

/// <remarks>
/// Linked into the Java and JavaScript test projects rather than duplicated — see their
/// <c>.csproj</c> files.
/// </remarks>
internal static class TestBuilderDefaults
{
    /// <summary>
    /// Disables the Generic Host's default <c>reloadOnChange</c> watch on <c>appsettings.json</c>.
    /// A test AppHost never outlives its own method, so nothing needs to see a live edit to that
    /// file — and without this, every builder leaves behind a <c>FileSystemWatcher</c> (one inotify
    /// instance on Linux) that isn't collectible until the builder itself becomes unreachable. See
    /// https://github.com/flojon/aspire-servicesources/issues/287.
    /// </summary>
    internal const string DisableConfigReloadArg = "--hostBuilder:reloadConfigOnChange=false";
}
