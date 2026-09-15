namespace Aspire.Hosting.ServiceSources.Tests;

public class LocalKindsTests
{
    [Fact]
    public void LocalKinds_IsPublic() =>
        Assert.True(typeof(LocalKinds).IsPublic);
}
