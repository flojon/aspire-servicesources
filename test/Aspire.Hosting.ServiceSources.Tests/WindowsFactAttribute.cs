using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>A test about behaviour only Windows has, such as MAX_PATH. Skipped everywhere else.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}
