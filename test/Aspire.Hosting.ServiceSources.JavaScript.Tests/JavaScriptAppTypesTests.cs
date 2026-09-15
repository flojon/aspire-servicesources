namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

public class JavaScriptAppTypesTests
{
    [Fact]
    public void JavaScriptAppTypes_IsPublic() =>
        Assert.True(typeof(JavaScriptAppTypes).IsPublic);
}
