using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// A test that needs a real DCP, which is not a package reference here. Skipped unless both variables
/// point at dcp.exe and the dashboard executable (for example from the NuGet cache).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DcpFactAttribute : FactAttribute
{
    public const string CliVariable = "SERVICESOURCES_TEST_DCP_CLI";

    public const string DashboardVariable = "SERVICESOURCES_TEST_DCP_DASHBOARD";

    public DcpFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CliVariable))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DashboardVariable)))
        {
            Skip = $"Set {CliVariable} and {DashboardVariable} to run";
        }
    }
}
