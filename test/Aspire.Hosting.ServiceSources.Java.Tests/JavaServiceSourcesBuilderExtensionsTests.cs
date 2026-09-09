using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaServiceSourcesBuilderExtensionsTests
{
    [Fact]
    public void AsJava_SetsKindNameAndTypedOptions()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080))
            .Build();

        Assert.Equal(JavaLocalResourceKind.KindName, definition.Kind);
        var options = Assert.IsType<JavaKindOptions>(definition.KindOptions);
        Assert.Equal("spring-boot:run", options.MavenGoal);
        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void AsJava_ProducedOptions_ParseSuccessfullyThroughTheExistingValidator()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080))
            .Build();

        var validated = JavaKindOptions.Parse("catalog", definition.KindOptions);

        Assert.Equal(JavaRunModeKind.MavenGoal, validated.RunMode.Kind);
        Assert.Equal("spring-boot:run", validated.RunMode.Value);
        Assert.Equal(8080, validated.Port);
    }

    [Fact]
    public void AsJava_ReturnsTheSameBuilderForFurtherChaining()
    {
        var catalogBuilder = new ServiceCatalogBuilder();
        var chain = catalogBuilder.AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic");

        var result = chain.AsJava(o => o.MavenGoal("spring-boot:run").Port(8080));

        Assert.Same(chain, result);
    }

    [Fact]
    public void AsJava_CalledAfterWithKind_ThrowsAlreadyCalled()
    {
        var chain = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .WithKind("java", new Dictionary<string, object> { ["port"] = 8080 });

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.AsJava(o => o.Port(8080)));
    }
}
