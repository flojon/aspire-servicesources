using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaKindOptionsTests
{
    [Fact]
    public void Parse_MavenGoalBlock_ResolvesRunModeAndDefaults()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Equal(JavaRunModeKind.MavenGoal, options.RunMode.Kind);
        Assert.Equal("spring-boot:run", options.RunMode.Value);
        Assert.Equal(8080, options.Port);
        Assert.Equal(".", options.WorkingDirectory);
        Assert.Empty(options.Args);
    }

    [Fact]
    public void Parse_GradleTaskBlock_ResolvesGradleRunMode()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("gradleTask", "bootRun"),
            ("port", 8080)));

        Assert.Equal(JavaRunModeKind.GradleTask, options.RunMode.Kind);
        Assert.Equal("bootRun", options.RunMode.Value);
    }

    [Fact]
    public void Parse_JarPathBlock_ResolvesJarRunMode()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("jarPath", "target/app.jar"),
            ("port", 8080)));

        Assert.Equal(JavaRunModeKind.Jar, options.RunMode.Kind);
        Assert.Equal("target/app.jar", options.RunMode.Value);
    }

    [Fact]
    public void Parse_AllFieldsSet_CarriesThemThrough()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("workingDirectory", "services/api"),
            ("mavenGoal", "spring-boot:run"),
            ("args", new List<object> { "-Dspring-boot.run.profiles=dev", "-q" }),
            ("port", 9000)));

        Assert.Equal("services/api", options.WorkingDirectory);
        Assert.Equal(["-Dspring-boot.run.profiles=dev", "-q"], options.Args);
        Assert.Equal(9000, options.Port);
    }

    [Fact]
    public void Parse_NoBlockAtAll_ThrowsNamingServiceAndBlock()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", null));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("'java:'", ex.Message);
    }

    [Fact]
    public void Parse_UnknownProperty_ThrowsSoTyposArentSilentlyIgnored()
    {
        // The kind block is opaque to core's own unknown-property checks, so this is the only place
        // a typo can be caught — see LocalKindConfig.Parse.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("mavenGaol", "spring-boot:run"),
                ("port", 8080))));

        Assert.Contains("java-api", ex.Message);
    }

    [Fact]
    public void Parse_NoRunMode_ThrowsNamingAllThreeOptions()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(("port", 8080))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("mavenGoal", ex.Message);
        Assert.Contains("gradleTask", ex.Message);
        Assert.Contains("jarPath", ex.Message);
    }

    [Theory]
    [InlineData("mavenGoal", "spring-boot:run", "gradleTask", "bootRun")]
    [InlineData("mavenGoal", "spring-boot:run", "jarPath", "target/app.jar")]
    [InlineData("gradleTask", "bootRun", "jarPath", "target/app.jar")]
    public void Parse_TwoRunModes_ThrowsNamingBothConflictingFields(
        string firstField, string firstValue, string secondField, string secondValue)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                (firstField, firstValue),
                (secondField, secondValue),
                ("port", 8080))));

        Assert.Contains(firstField, ex.Message);
        Assert.Contains(secondField, ex.Message);
    }

    [Fact]
    public void Parse_WhitespaceOnlyRunMode_CountsAsAbsent()
    {
        // "mavenGoal:   " is a half-finished edit, not an empty goal — reporting "name exactly one
        // run mode" beats letting AddJavaApp throw an ArgumentException about a blank argument.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("mavenGoal", "   "),
                ("port", 8080))));

        Assert.Contains("exactly one", ex.Message);
    }

    [Fact]
    public void Parse_NoPort_ThrowsNamingPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(("mavenGoal", "spring-boot:run"))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("port", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Parse_PortOutOfRange_Throws(int port)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("mavenGoal", "spring-boot:run"),
                ("port", port))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains(port.ToString(), ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void Parse_PortAtBounds_IsAccepted(int port)
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("mavenGoal", "spring-boot:run"),
            ("port", port)));

        Assert.Equal(port, options.Port);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../sibling")]
    [InlineData("services/../../escape")]
    [InlineData("..\\sibling")]
    public void Parse_WorkingDirectoryEscapingTheCheckout_Throws(string workingDirectory)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("workingDirectory", workingDirectory),
                ("mavenGoal", "spring-boot:run"),
                ("port", 8080))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("outside", ex.Message);
    }

    [Theory]
    [InlineData("services/api/../api")]
    [InlineData("./services/api")]
    public void Parse_WorkingDirectoryStayingInsideTheCheckout_IsAccepted(string workingDirectory)
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("workingDirectory", workingDirectory),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Equal(workingDirectory, options.WorkingDirectory);
    }

    [Fact]
    public void Parse_AbsoluteWorkingDirectory_ThrowsPointingAtThePathOverrideInstead()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "java-api"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("workingDirectory", absolute),
                ("mavenGoal", "spring-boot:run"),
                ("port", 8080))));

        Assert.Contains("absolute", ex.Message);
        Assert.Contains("path", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankWorkingDirectory_FallsBackToTheRepositoryRoot(string workingDirectory)
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("workingDirectory", workingDirectory),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Equal(".", options.WorkingDirectory);
    }

    [Fact]
    public void Parse_BlockThatIsNotAMapping_ThrowsFromLocalKindConfig()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", "spring-boot:run"));

        Assert.Contains("java-api", ex.Message);
    }
}
