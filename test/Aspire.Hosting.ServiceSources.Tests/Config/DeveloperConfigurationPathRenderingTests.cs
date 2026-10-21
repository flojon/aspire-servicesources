using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// The configuration file's path in the "not configured" messages reads as the path on disk. The
/// configuration is built directly with a Windows-shaped path so the pin holds on every platform.
/// </summary>
public class DeveloperConfigurationPathRenderingTests
{
    private const string WindowsPath = @"C:\src\apphost\servicesources.local.json";

    private static DeveloperConfiguration Configuration(
        bool fileFound, string? nearMissRootKey = null, bool withService = true) =>
        new()
        {
            Services = withService
                ? new Dictionary<string, ServiceDeveloperConfig> { ["other"] = new() { Source = "path" } }
                : new Dictionary<string, ServiceDeveloperConfig>(),
            Repositories = new Dictionary<string, RepositoryDeveloperConfig>(),
            FilePath = WindowsPath,
            FileFound = fileFound,
            NearMissRootKey = nearMissRootKey,
            UndeclaredNames = [],
            CatalogNames = [],
            UndeclaredRepositoryNames = [],
            RepositoryNames = [],
        };

    private static void AssertShowsPathAsWritten(string message)
    {
        Assert.Contains(WindowsPath, message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\\src", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NotConfiguredError_ShowsTheFilePathAsWritten(bool fileFound) =>
        AssertShowsPathAsWritten(Configuration(fileFound).NotConfiguredError("orders").Message);

    [Fact]
    public void NotConfiguredError_WithAMisspelledRootKey_ShowsTheFilePathAsWritten() =>
        AssertShowsPathAsWritten(Configuration(true, "servces").NotConfiguredError("orders").Message);

    [Theory]
    [InlineData(null)]
    [InlineData("servces")]
    public void NotConfiguredError_NothingConfigured_ShowsTheFilePathAsWritten(string? nearMissRootKey) =>
        AssertShowsPathAsWritten(
            Configuration(false, nearMissRootKey, withService: false).NotConfiguredError("orders").Message);
}
