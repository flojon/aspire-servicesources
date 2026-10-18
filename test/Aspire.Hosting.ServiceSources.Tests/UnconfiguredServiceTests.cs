using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests;

[Trait("IO", "true")]
public class UnconfiguredServiceTests
{
    private static string AppHostDirectory()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://inventory.example.com
            """);
        return dir;
    }

    // CI is blanked unless a test says otherwise: these tests also run on CI, where it is set.
    private static IDistributedApplicationBuilder Builder(
        string? ci = "", string? interactive = null, bool optIn = true)
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CI"] = ci,
            [InteractiveServiceConfiguration.OverrideKey] = interactive,
        });

        if (optIn)
        {
            builder.UseInteractiveServiceConfiguration();
        }

        return builder;
    }

    [Fact]
    public void WithoutTheOptIn_AnUnconfiguredService_ThrowsAsItAlwaysHas()
    {
        var builder = Builder(optIn: false);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("inventory"));

        Assert.Contains("No service sources are configured", ex.Message);
    }

    [Fact]
    public void WithTheOptIn_AnUnconfiguredService_RegistersAPlaceholderInsteadOfThrowing()
    {
        var builder = Builder();

        builder.AddService("inventory");

        var placeholder = Assert.Single(builder.Resources.OfType<UnconfiguredServiceResource>());
        Assert.Equal("inventory", placeholder.Name);
        Assert.Empty(placeholder.Annotations.OfType<EndpointAnnotation>());

        var snapshot = Assert.Single(placeholder.Annotations.OfType<ResourceSnapshotAnnotation>()).InitialSnapshot;
        Assert.Equal(UnconfiguredSource.StateText, snapshot.State?.Text);
        Assert.Contains(snapshot.Properties, p => p.Name == "Reason");
    }

    [Fact]
    public void ThePlaceholder_IsNotALocalCheckoutCandidate()
    {
        var builder = Builder();

        builder.AddService("inventory");

        Assert.DoesNotContain(
            builder.Resources, r => r.Annotations.OfType<ServiceSourceAnnotation>().Any(a => a.Source == "repository"));
    }

    [Fact]
    public void ACatalogMissingService_StillThrows_EvenWhenInteractive()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("was not found in the service catalog", ex.Message);
    }

    [Theory]
    [InlineData("true", null, false, false)]
    [InlineData("true", "1", false, true)]
    [InlineData("", null, true, true)]
    [InlineData("true", null, true, false)]
    [InlineData("", "0", true, false)]
    [InlineData("", "false", true, false)]
    [InlineData("", "TRUE", false, true)]
    public void WhetherAPlaceholderIsUsed_FollowsTheOptInTheCiVariableAndTheOverride(
        string ci, string? interactive, bool optIn, bool expectPlaceholder)
    {
        // Row 2 is the override forcing it on without an opt-in or against CI.
        var builder = Builder(ci, interactive, optIn);

        if (expectPlaceholder)
        {
            builder.AddService("inventory");
            Assert.Single(builder.Resources.OfType<UnconfiguredServiceResource>());
        }
        else
        {
            Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("inventory"));
        }
    }

    [Fact]
    public void AnOverrideThatIsNeitherTrueNorFalse_IsRejectedRatherThanGuessedAt()
    {
        var builder = Builder(interactive: "maybe");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("inventory"));

        Assert.Contains("SERVICESOURCES_INTERACTIVE", ex.Message);
    }

    [Fact]
    public void InPublishMode_AnUnconfiguredService_StillThrows_EvenWhenForcedOn()
    {
        var builder = TestHelpers.CreatePublishingBuilder(AppHostDirectory());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CI"] = "",
            [InteractiveServiceConfiguration.OverrideKey] = "1",
        });
        builder.UseInteractiveServiceConfiguration();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("inventory"));

        Assert.Contains("No service sources are configured", ex.Message);
    }

    [Fact]
    public void ThePlaceholdersReason_NamesTheKeyToSet_AndNoFilePath()
    {
        var builder = Builder();

        builder.AddService("inventory");

        var placeholder = Assert.Single(builder.Resources.OfType<UnconfiguredServiceResource>());
        var snapshot = Assert.Single(placeholder.Annotations.OfType<ResourceSnapshotAnnotation>()).InitialSnapshot;
        var reason = Assert.Single(snapshot.Properties, p => p.Name == "Reason").Value?.ToString();

        Assert.Contains("ServiceSources:Services:inventory:source", reason);
        Assert.DoesNotContain("servicesources.local.json", reason);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, reason!);
    }

    [Fact]
    public void UnwrapOnAPlaceholder_ExplainsThatNoSourceHasBeenChosen()
    {
        var builder = Builder();
        var inventory = builder.AddService("inventory");

        var ex = Assert.ThrowsAny<Exception>(() => inventory.Unwrap<ProjectResource>());

        Assert.Contains("no source has been chosen", ex.Message);
        Assert.DoesNotContain("runs out of band", ex.Message);
    }

    [Fact]
    public void GetServiceEndpointOnAPlaceholder_ExplainsThatNoSourceHasBeenChosen()
    {
        var builder = Builder();
        var inventory = builder.AddService("inventory");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => inventory.GetServiceEndpoint());

        Assert.Contains("No source has been chosen", ex.Message);
        Assert.DoesNotContain("WithHttpEndpoint", ex.Message);
    }

    [Fact]
    public async Task AConsumerThatWaitsOnAndReferencesAPlaceholder_StillStarts()
    {
        var builder = Builder();

        var inventory = builder.AddService("inventory");
        var worker = builder.AddExecutable("worker", "dotnet", TempDirectories.CreateSubdirectory().FullName)
            .WithReference(inventory)
            .WaitFor(inventory);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
    }
}
