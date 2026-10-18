using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceSourcesOverloadProbe;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests;

[Trait("IO", "true")]
[Trait("Timing", "true")]
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
            [InteractivePrompts.EnvironmentVariable] = interactive,
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
    [InlineData("", "1", false, false)]
    [InlineData("", null, true, true)]
    [InlineData("true", null, true, false)]
    [InlineData("1", null, true, false)]
    [InlineData("false", null, true, true)]
    [InlineData("0", null, true, true)]
    [InlineData("true", "1", true, true)]
    [InlineData("true", "yes", true, true)]
    [InlineData("", "0", true, false)]
    [InlineData("", "FALSE", true, false)]
    [InlineData("", "maybe", true, true)]
    [InlineData("true", "maybe", true, false)]
    public void WhetherAPlaceholderIsUsed_FollowsTheOptInTheCiVariableAndTheOverride(
        string ci, string? interactive, bool optIn, bool expectPlaceholder)
    {
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
    public void InPublishMode_AnUnconfiguredService_StillThrows_EvenWhenForcedOn()
    {
        var builder = TestHelpers.CreatePublishingBuilder(AppHostDirectory());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CI"] = "",
            [InteractivePrompts.EnvironmentVariable] = "1",
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
    public void ThePlaceholderLogsTheFullNotConfiguredText_WhichTheDashboardReasonOmits()
    {
        var builder = Builder();

        builder.AddService("inventory");

        var notice = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("No service sources are configured", notice);
        Assert.Contains("inventory", notice);
    }

    [Fact]
    public async Task AnEndpointAddedByAGuestAppHost_IsRevertedWithoutAdviceToConfigureWhereItRuns()
    {
        var builder = Builder();
        var inventory = builder.AddService("inventory");

        GuestLanguageEndpointCallbacks.EndpointCallback(inventory, "probe", ("Port", 4242));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Contains(warnings, w => w.Contains("There is no endpoint to change until a source is chosen"));
        Assert.DoesNotContain(warnings, w => w.Contains("where it actually runs"));
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
