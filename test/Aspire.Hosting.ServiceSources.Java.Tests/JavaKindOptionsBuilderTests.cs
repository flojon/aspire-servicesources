using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaKindOptionsBuilderTests
{
    [Fact]
    public void Build_EveryFieldSet_ProducesMatchingJavaKindOptions()
    {
        var options = new JavaKindOptionsBuilder()
            .WorkingDirectory("services/api")
            .MavenGoal("spring-boot:run")
            .WrapperPath("mvnw")
            .Args(["-Dspring-boot.run.profiles=dev"])
            .Port(8080)
            .Build();

        Assert.Equal("services/api", options.WorkingDirectory);
        Assert.Equal("spring-boot:run", options.MavenGoal);
        Assert.Equal("mvnw", options.WrapperPath);
        Assert.Equal(["-Dspring-boot.run.profiles=dev"], options.Args);
        Assert.Equal(8080, options.Port);
        Assert.Null(options.GradleTask);
        Assert.Null(options.JarPath);
    }

    [Fact]
    public void Build_OnlyGradleTaskAndPortSet_LeavesOthersNull()
    {
        var options = new JavaKindOptionsBuilder()
            .GradleTask("bootRun")
            .Port(8081)
            .Build();

        Assert.Equal("bootRun", options.GradleTask);
        Assert.Equal(8081, options.Port);
        Assert.Null(options.MavenGoal);
        Assert.Null(options.JarPath);
        Assert.Null(options.WorkingDirectory);
    }

    [Fact]
    public void Build_JarPathSet_ProducesMatchingJavaKindOptions()
    {
        var options = new JavaKindOptionsBuilder()
            .JarPath("target/app.jar")
            .Port(8082)
            .Build();

        Assert.Equal("target/app.jar", options.JarPath);
    }

    [Fact]
    public void EveryPublicMethod_IsNonGenericAndReturnsItself()
    {
        var methods = typeof(JavaKindOptionsBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var method in methods)
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.Equal(typeof(JavaKindOptionsBuilder), method.ReturnType);
        }
    }
}
