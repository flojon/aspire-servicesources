using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaKindOptionsBuilderTests
{
    [Fact]
    public void Build_EveryFieldSet_ProducesMatchingJavaKindOptions()
    {
        var options = new JavaKindOptionsBuilder()
            .WithWorkingDirectory("services/api")
            .WithMavenGoal("spring-boot:run")
            .WithWrapperPath("mvnw")
            .WithArgs(["-Dspring-boot.run.profiles=dev"])
            .WithPort(8080)
            .Build();

        Assert.Equal("services/api", options.WorkingDirectory);
        Assert.Equal("spring-boot:run", options.MavenGoal);
        Assert.Equal("mvnw", options.WrapperPath);
        Assert.Equal(["-Dspring-boot.run.profiles=dev"], options.Args!);
        Assert.Equal(8080, options.Port);
        Assert.Null(options.GradleTask);
        Assert.Null(options.JarPath);
    }

    [Fact]
    public void Build_OnlyGradleTaskAndPortSet_LeavesOthersNull()
    {
        var options = new JavaKindOptionsBuilder()
            .WithGradleTask("bootRun")
            .WithPort(8081)
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
            .WithJarPath("target/app.jar")
            .WithPort(8082)
            .Build();

        Assert.Equal("target/app.jar", options.JarPath);
    }

    [Fact]
    public void Build_SchemeSet_ProducesMatchingJavaKindOptions()
    {
        var options = new JavaKindOptionsBuilder()
            .WithMavenGoal("spring-boot:run")
            .WithPort(8443)
            .WithScheme("https")
            .Build();

        Assert.Equal("https", options.Scheme);
    }

    [Fact]
    public void Build_SchemeNotSet_LeavesItNull()
    {
        var options = new JavaKindOptionsBuilder()
            .WithMavenGoal("spring-boot:run")
            .WithPort(8080)
            .Build();

        Assert.Null(options.Scheme);
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
