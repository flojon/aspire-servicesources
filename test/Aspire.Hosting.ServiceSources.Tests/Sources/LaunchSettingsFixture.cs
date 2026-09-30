namespace Aspire.Hosting.ServiceSources.Tests.Sources;

internal static class LaunchSettingsFixture
{
    /// <summary>Two profiles with different schemes and ports, https first so it is Aspire's default.</summary>
    public const string HttpsThenHttp = """
        {
          "profiles": {
            "https": {
              "commandName": "Project",
              "applicationUrl": "https://localhost:7100",
              "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Production" }
            },
            "http": {
              "commandName": "Project",
              "applicationUrl": "http://localhost:5100",
              "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
            }
          }
        }
        """;

    public static void Write(string projectDirectory, string json)
    {
        var properties = Directory.CreateDirectory(Path.Combine(projectDirectory, "Properties"));
        File.WriteAllText(Path.Combine(properties.FullName, "launchSettings.json"), json);
    }
}
