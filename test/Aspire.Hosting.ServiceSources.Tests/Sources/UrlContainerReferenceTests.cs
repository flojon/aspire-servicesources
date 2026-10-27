using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
public class UrlContainerReferenceTests
{
    private const string Literal = "https://httpbin.org";

    private static string AppHostDirectory(string source = "url")
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                repository: https://github.com/company/inventory
                project: Inventory.csproj
                url:
                  url: https://httpbin.org
                container:
                  image: nginxdemos/hello
                  port: 8080
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $$"""{ "services": { "inventory": { "source": "{{source}}" } } }""");
        return dir;
    }

    private static (IDistributedApplicationBuilder Builder, IResourceBuilder<ServiceResource> Inventory, IResourceBuilder<ContainerResource> Storefront) Arrange(
        string source = "url")
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory(source));
        var inventory = builder.AddService("inventory");
        var storefront = builder.AddContainer("storefront", "nginx:alpine");
        return (builder, inventory, storefront);
    }

    [Fact]
    public async Task CallbackRunner_RunsAPlainEnvironmentCallback()
    {
        var (_, _, storefront) = Arrange();
        storefront.WithEnvironment("X", "y");

        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal("y", env["X"]);
    }

    [Fact]
    public async Task WithReference_InjectsBothEnvironmentVariablesAsLiterals()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront.WithReference(inventory);

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal(Literal, env["INVENTORY_HTTPS"]);
        Assert.Equal(Literal, env["services__inventory__https__0"]);
    }

    [Fact]
    public async Task WithReference_RemovesTheShapeAnnotations()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront.WithReference(inventory);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.DoesNotContain(storefront.Resource.Annotations, a => a is EndpointReferenceAnnotation);
        Assert.DoesNotContain(
            storefront.Resource.Annotations,
            a => a is ResourceRelationshipAnnotation { Type: "Reference" } r && ReferenceEquals(r.Resource, inventory.Resource));
    }

    [Fact]
    public async Task WithEnvironment_EndpointReference_BecomesLiteral()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront.WithEnvironment("A", inventory.GetEndpoint("https"));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal(Literal, env["A"]);
    }

    [Fact]
    public async Task WithEnvironment_ReferenceExpression_KeepsSuffix()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        storefront.WithEnvironment("B", ReferenceExpression.Create($"{endpoint}/x"));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal($"{Literal}/x", env["B"]);
    }

    [Fact]
    public async Task WithEnvironment_EndpointProperty_Url_BecomesLiteral()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront.WithEnvironment("E", inventory.GetEndpoint("https").Property(EndpointProperty.Url));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal(Literal, env["E"]);
    }

    [Fact]
    public async Task WithEnvironment_EachEndpointProperty_MapsPerTheSpec()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        storefront
            .WithEnvironment("P_SCHEME", endpoint.Property(EndpointProperty.Scheme))
            .WithEnvironment("P_HOST", endpoint.Property(EndpointProperty.Host))
            .WithEnvironment("P_IPV4", endpoint.Property(EndpointProperty.IPV4Host))
            .WithEnvironment("P_PORT", endpoint.Property(EndpointProperty.Port))
            .WithEnvironment("P_TARGET", endpoint.Property(EndpointProperty.TargetPort))
            .WithEnvironment("P_HOSTPORT", endpoint.Property(EndpointProperty.HostAndPort));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal("https", env["P_SCHEME"]);
        Assert.Equal("httpbin.org", env["P_HOST"]);
        Assert.Equal("httpbin.org", env["P_IPV4"]);
        Assert.Equal("443", env["P_PORT"]);
        Assert.Equal("443", env["P_TARGET"]);
        Assert.Equal("httpbin.org:443", env["P_HOSTPORT"]);
    }

    [Fact]
    public async Task WithArgs_EndpointReference_BecomesLiteral()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        storefront.WithEnvironment("A", endpoint).WithArgs(ctx => ctx.Args.Add(endpoint));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var args = await CallbackRunner.ArgsAsync(storefront.Resource);

        Assert.Contains(Literal, args);
    }

    [Fact]
    public async Task WithEnvironment_MixedReferenceExpression_KeepsTheForeignProviderAsAProvider()
    {
        var (builder, inventory, storefront) = Arrange();
        var secret = builder.AddParameter("api-key", "s3cret", secret: true);
        var endpoint = inventory.GetEndpoint("https");
        storefront.WithEnvironment("M", ReferenceExpression.Create($"{endpoint}/?k={secret.Resource}"));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        var rewritten = Assert.IsType<ReferenceExpression>(env["M"]);
        Assert.Same(secret.Resource, Assert.Single(rewritten.ValueProviders));
        Assert.Equal($"{Literal}/?k={{0}}", rewritten.Format);
    }

    [Fact]
    public async Task WithEnvironment_UnrelatedValue_IsUntouched()
    {
        var (builder, inventory, storefront) = Arrange();
        var other = builder.AddContainer("other", "nginx:alpine").WithHttpEndpoint(targetPort: 80, name: "http");
        var otherEndpoint = other.GetEndpoint("http");
        storefront
            .WithReference(inventory)
            .WithEnvironment("PLAIN", "value")
            .WithEnvironment("OTHER", otherEndpoint);

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        Assert.Equal("value", env["PLAIN"]);
        Assert.Same(otherEndpoint.Resource, Assert.IsType<EndpointReference>(env["OTHER"]).Resource);
    }

    [Fact]
    public async Task WithEnvironment_OpaqueLambdaAlone_IsNotRewritten()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        var before = storefront.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();
        storefront.WithEnvironment(ctx => ctx.EnvironmentVariables["C"] = endpoint);
        // The kept gap: nothing in the shape names the service, so the consumer is not matched.
        // (WithEnvironment(lambda) leaves no EndpointReferenceAnnotation and no relationship.)
        var annotations = storefront.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray();

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Equal(annotations, storefront.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Equal(before + 1, annotations.Length);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);
        Assert.IsType<EndpointReference>(env["C"]);
    }

    [Fact]
    public async Task RunningThePreflightTwice_LeavesOneWrapperPerCallback()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront.WithReference(inventory);

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var after = storefront.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray();
        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Equal(after, storefront.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);
        Assert.Equal(Literal, env["INVENTORY_HTTPS"]);
    }

    [Fact]
    public async Task AContainerReferencingAContainerSourcedService_IsUntouched()
    {
        var (builder, inventory, storefront) = Arrange("container");
        storefront.WithReference(inventory);
        // Aspire adds its own bookkeeping annotation while the event runs; only the ones this rewrite
        // could touch are compared.
        IResourceAnnotation[] Touchable() => storefront.Resource.Annotations
            .Where(a => a is EndpointReferenceAnnotation or ResourceRelationshipAnnotation
                or EnvironmentCallbackAnnotation or CommandLineArgsCallbackAnnotation)
            .ToArray();
        var before = Touchable();

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Equal(before, Touchable());
    }

    [Fact]
    public async Task AnEndpointPropertyWithNoLiteralForm_FailsFastAtStartup()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        storefront
            .WithReference(inventory)
            .WithEnvironment("T", endpoint.Property(EndpointProperty.TlsEnabled));

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("storefront", error.Message);
        Assert.Contains("'T'", error.Message);
        Assert.Contains("inventory", error.Message);
        Assert.Contains("TlsEnabled", error.Message);
        Assert.DoesNotContain("httpbin", error.Message);
    }

    [Fact]
    public async Task AConditionalExpressionOverTheFacade_FailsFastAtStartup()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        storefront
            .WithReference(inventory)
            .WithEnvironment("COND", ReferenceExpression.CreateConditional(
                endpoint.Property(EndpointProperty.Url), "x", ReferenceExpression.Create($"a"), ReferenceExpression.Create($"b")));

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("storefront", error.Message);
        Assert.Contains("'COND'", error.Message);
        Assert.Contains("inventory", error.Message);
        Assert.Contains("conditional", error.Message);
        Assert.DoesNotContain("httpbin", error.Message);
    }

    [Fact]
    public async Task AFormatSpecifierOnTheFacade_FailsFastAtStartup()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        var builderExpression = new ReferenceExpressionBuilder();
        builderExpression.AppendLiteral("u=");
        builderExpression.AppendValueProvider(endpoint, "uri");
        storefront.WithReference(inventory).WithEnvironment("F", builderExpression.Build());

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("'F'", error.Message);
        Assert.Contains("format specifier", error.Message);
        Assert.DoesNotContain("httpbin", error.Message);
    }

    [Fact]
    public async Task AnArgumentTheRewriteCannotHandle_FailsFastAtStartup()
    {
        var (builder, inventory, storefront) = Arrange();
        var endpoint = inventory.GetEndpoint("https");
        storefront
            .WithReference(inventory)
            .WithArgs(context => context.Args.Add(endpoint.Property(EndpointProperty.TlsEnabled)));

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("storefront", error.Message);
        Assert.Contains("argument 0", error.Message);
        Assert.DoesNotContain("httpbin", error.Message);
    }

    [Fact]
    public async Task TheFailFastError_OffersTheWayOut()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront
            .WithReference(inventory)
            .WithEnvironment("T", inventory.GetEndpoint("https").Property(EndpointProperty.TlsEnabled));

        var error = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Contains("servicesources.local.json", error.Message);
    }

    [Fact]
    public async Task TheDryRun_NeverResolvesAForeignProvider()
    {
        var (builder, inventory, storefront) = Arrange();
        var foreign = new CountingProvider();
        storefront.WithReference(inventory).WithEnvironment("F", ReferenceExpression.Create($"{foreign}"));

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Equal(0, foreign.Resolutions);
    }

    [Fact]
    public async Task ACallbackThatThrowsDuringTheDryRun_DoesNotFailThePreflight()
    {
        var (builder, inventory, storefront) = Arrange();
        storefront
            .WithReference(inventory)
            .WithEnvironment(_ => throw new InvalidOperationException("not ready yet"));

        // The callback fails at real evaluation too; the pre-flight only has to stay out of its way.
        await TestHelpers.PublishBeforeStartEventAsync(builder);

    }

    [Fact]
    public async Task AFacadeFreeConditionalExpression_IsNotRefused()
    {
        var (builder, inventory, storefront) = Arrange();
        var secret = builder.AddParameter("api-key", "s3cret", secret: true);
        storefront
            .WithReference(inventory)
            .WithEnvironment("COND", ReferenceExpression.CreateConditional(
                ReferenceExpression.Create($"{secret.Resource}"), "x", ReferenceExpression.Create($"a"), ReferenceExpression.Create($"b")));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
    }

    private sealed class CountingProvider : IValueProvider, IManifestExpressionProvider
    {
        public int Resolutions;

        public string ValueExpression => "{counting.value}";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            Resolutions++;
            return new ValueTask<string?>("v");
        }
    }

    [Fact]
    public async Task WithEnvironment_MixedReferenceExpression_KeepsEscapedBracesLiteral()
    {
        var (builder, inventory, storefront) = Arrange();
        var secret = builder.AddParameter("api-key", "s3cret", secret: true);
        var endpoint = inventory.GetEndpoint("https");
        storefront.WithEnvironment("J", ReferenceExpression.Create($"{{json}}{endpoint}{secret.Resource}"));

        await TestHelpers.PublishBeforeStartEventAsync(builder);
        var env = await CallbackRunner.EnvironmentAsync(storefront.Resource);

        var rewritten = Assert.IsType<ReferenceExpression>(env["J"]);
        Assert.Equal($"{{{{json}}}}{Literal}{{0}}", rewritten.Format);
        Assert.Same(secret.Resource, Assert.Single(rewritten.ValueProviders));
    }
}
