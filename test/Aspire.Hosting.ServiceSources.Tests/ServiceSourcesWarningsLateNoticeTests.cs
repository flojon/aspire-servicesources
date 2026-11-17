using Aspire.Hosting.ServiceSources.Messages;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceSourcesWarningsLateNoticeTests
{
    [Fact]
    public async Task Notice_AddedAfterTheFlush_IsWrittenOnceAtOnce()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        var log = TestHelpers.CaptureServiceSourcesLog(builder);
        var warnings = ServiceSourcesWarnings.For(builder);

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        warnings.AddNotice(Raw.Compose($"late notice"));
        warnings.AddNotice(Raw.Compose($"late notice"));

        lock (log)
        {
            Assert.Single(log, entry => entry.Message.Contains("late notice"));
        }
    }
}
