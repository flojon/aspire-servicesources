using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class ProcessOutputRenderingTests
{
    [Fact]
    public void Output_ShowsPathsAndQuotesAsWritten_AndJoinsLines()
    {
        var rendered = Raw.Output("Cloning into 'D:\\Source\\x'...\r\nremote: done.\nfatal: nope").ToString();

        Assert.Equal("Cloning into 'D:\\Source\\x'... | remote: done. | fatal: nope", rendered);
    }

    [Fact]
    public void Output_StillSpellsOutControlCharacters()
    {
        Assert.Equal("a\\u001b[2Kb", Raw.Output("a\u001b[2Kb").ToString());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("echo \"C:\\temp\\it's ok\"", "\"echo \\\"C:\\temp\\it's ok\\\"\"")]
    [InlineData("C:\\with space\\", "\"C:\\with space\\\\\"")]
    public void Describe_QuotesArgumentsUnambiguously(string argument, string expected)
    {
        var step = PrepareStep.Create(
            Raw.Literal("Service"), ["cmd", "/c", argument], PrepareMode.OncePerCommit, Raw.Literal("prepare"));

        Assert.Equal($"cmd /c {expected}", step.Describe());
    }
}
