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
    public void Parse_NoBlockAtAll_ThrowsSayingTheBlockIsMissingOrEmpty()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", null));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("'java:'", ex.Message);

        // A 'java:' key with nothing under it arrives here as the same null the loader passes for an
        // absent key, so the message can't claim the block isn't there - see
        // UseJavaTests.AddService_EmptyJavaBlock_DoesNotClaimTheBlockIsAbsent.
        Assert.Contains("missing or empty", ex.Message);
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

    [Theory]
    [InlineData("C:\\repos\\api")]
    [InlineData("C:/repos/api")]
    [InlineData("\\\\server\\share\\api")]
    [InlineData("\\repos\\api")]
    public void Parse_WindowsStyleAbsoluteWorkingDirectory_IsRejectedOnEveryPlatform(string workingDirectory)
    {
        // Path.IsPathRooted alone lets these through on Linux/macOS, where the value then resolves to
        // '<checkout>/C:\repos\api' and is reported as a missing directory rather than as the
        // absolute path it is.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("workingDirectory", workingDirectory),
                ("mavenGoal", "spring-boot:run"),
                ("port", 8080))));

        Assert.Contains("absolute", ex.Message);
        Assert.Contains("path", ex.Message);
    }

    [Fact]
    public void Parse_WrapperPath_CarriesThrough()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("workingDirectory", "services/catalog"),
            ("gradleTask", "bootRun"),
            ("wrapperPath", "gradlew"),
            ("port", 8080)));

        // Relative to the repository root, like workingDirectory - which is the whole point: in a
        // monorepo the wrapper sits at the root and the project doesn't.
        Assert.Equal("gradlew", options.WrapperPath);
    }

    [Fact]
    public void Parse_NoWrapperPath_LeavesItUnset()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Null(options.WrapperPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankWrapperPath_CountsAsAbsent(string wrapperPath)
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("mavenGoal", "spring-boot:run"),
            ("wrapperPath", wrapperPath),
            ("port", 8080)));

        Assert.Null(options.WrapperPath);
    }

    [Fact]
    public void Parse_WrapperPathWithJarPath_ThrowsBecauseAJarRunsNoWrapper()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("jarPath", "target/app.jar"),
                ("wrapperPath", "mvnw"),
                ("port", 8080))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("wrapperPath", ex.Message);
        Assert.Contains("jarPath", ex.Message);
    }

    [Theory]
    [InlineData("../gradlew")]
    [InlineData("..\\gradlew")]
    [InlineData("services/../../gradlew")]
    public void Parse_WrapperPathEscapingTheCheckout_Throws(string wrapperPath)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("gradleTask", "bootRun"),
                ("wrapperPath", wrapperPath),
                ("port", 8080))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("outside", ex.Message);
    }

    [Theory]
    [InlineData("C:\\tools\\mvnw")]
    [InlineData("/usr/local/bin/mvn")]
    public void Parse_AbsoluteWrapperPath_IsRejectedOnEveryPlatform(string wrapperPath)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("mavenGoal", "spring-boot:run"),
                ("wrapperPath", wrapperPath),
                ("port", 8080))));

        Assert.Contains("absolute", ex.Message);
    }

    [Theory]
    [InlineData("/opt/prebuilt/app.jar")]
    [InlineData("C:\\builds\\app.jar")]
    [InlineData("\\\\server\\share\\app.jar")]
    public void Parse_AbsoluteJarPath_IsRejectedOnEveryPlatform(string jarPath)
    {
        // Checked for the same reason workingDirectory and wrapperPath are: servicesources.yaml is
        // shared team config a developer clones rather than writes, so a jar named outside the
        // checkout is one nobody reading the catalog agreed to run.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("jarPath", jarPath),
                ("port", 8080))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("absolute", ex.Message);
    }

    [Theory]
    [InlineData(".", "../app.jar")]
    [InlineData(".", "..\\app.jar")]
    [InlineData("services/api", "../../../escape/app.jar")]
    public void Parse_JarPathEscapingTheCheckout_Throws(string workingDirectory, string jarPath)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", Block(
                ("workingDirectory", workingDirectory),
                ("jarPath", jarPath),
                ("port", 8080))));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("outside", ex.Message);
    }

    [Fact]
    public void Parse_JarPathClimbingNoHigherThanTheRepositoryRoot_IsAccepted()
    {
        // jarPath is read relative to workingDirectory, so climbing out of the project directory is
        // fine as long as it stays in the checkout — the monorepo case where one build output
        // directory serves several projects.
        var options = JavaKindOptions.Parse("java-api", Block(
            ("workingDirectory", "services/api"),
            ("jarPath", "../../build/libs/app.jar"),
            ("port", 8080)));

        Assert.Equal(JavaRunModeKind.Jar, options.RunMode.Kind);
        Assert.Equal("../../build/libs/app.jar", options.RunMode.Value);
    }

    [Fact]
    public void Parse_WindowsStyleWorkingDirectory_IsNormalizedForThisPlatform()
    {
        // The validation counts both separators, so this value is accepted; it is then handed to
        // Path.Combine, where leaving it verbatim resolves to '<checkout>/services\catalog' on
        // Linux/macOS and is reported as a directory missing from the checkout.
        var options = JavaKindOptions.Parse("java-api", Block(
            ("workingDirectory", "services\\catalog"),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Equal(Path.Combine("services", "catalog"), options.WorkingDirectory);
    }

    [Fact]
    public void Parse_WindowsStyleWrapperPath_IsNormalizedForThisPlatform()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("gradleTask", "bootRun"),
            ("wrapperPath", "tools\\gradlew"),
            ("port", 8080)));

        Assert.Equal(Path.Combine("tools", "gradlew"), options.WrapperPath);
    }

    [Fact]
    public void Parse_WindowsStyleJarPath_IsNormalizedForThisPlatform()
    {
        var options = JavaKindOptions.Parse("java-api", Block(
            ("jarPath", "target\\app.jar"),
            ("port", 8080)));

        Assert.Equal(Path.Combine("target", "app.jar"), options.RunMode.Value);
    }

    [Fact]
    public void Parse_BlockThatIsNotAMapping_ThrowsFromLocalKindConfig()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => JavaKindOptions.Parse("java-api", "spring-boot:run"));

        Assert.Contains("java-api", ex.Message);
    }
}
