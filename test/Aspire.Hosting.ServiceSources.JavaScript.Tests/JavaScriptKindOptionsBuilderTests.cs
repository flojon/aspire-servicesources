using System.Reflection;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

public class JavaScriptKindOptionsBuilderTests
{
    [Fact]
    public void Build_EveryFieldSet_ProducesMatchingJavaScriptKindOptions()
    {
        var options = new JavaScriptKindOptionsBuilder()
            .AppType(JavaScriptAppTypes.Vite)
            .AppDirectory("apps/web")
            .RunScript("dev")
            .PackageManager(JavaScriptPackageManagers.Pnpm)
            .Port(3000)
            .TargetPort(5173)
            .PortEnv("VITE_PORT")
            .Build();

        Assert.Equal(JavaScriptAppTypes.Vite, options.AppType);
        Assert.Equal("apps/web", options.AppDirectory);
        Assert.Equal("dev", options.RunScript);
        Assert.Equal(JavaScriptPackageManagers.Pnpm, options.PackageManager);
        Assert.Equal(3000, options.Port);
        Assert.Equal(5173, options.TargetPort);
        Assert.Equal("VITE_PORT", options.PortEnv);
        Assert.Null(options.ScriptPath);
    }

    [Fact]
    public void Build_NodeAppWithScriptPath_ProducesMatchingJavaScriptKindOptions()
    {
        var options = new JavaScriptKindOptionsBuilder()
            .AppType(JavaScriptAppTypes.Node)
            .ScriptPath("server.js")
            .Build();

        Assert.Equal(JavaScriptAppTypes.Node, options.AppType);
        Assert.Equal("server.js", options.ScriptPath);
    }

    [Fact]
    public void EveryPublicMethod_IsNonGenericAndReturnsItself()
    {
        var methods = typeof(JavaScriptKindOptionsBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var method in methods)
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.Equal(typeof(JavaScriptKindOptionsBuilder), method.ReturnType);
        }
    }
}
