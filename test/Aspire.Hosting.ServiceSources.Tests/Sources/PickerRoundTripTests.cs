using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// What the picker saves, read back by the next run: the guarantee its validation exists for.
/// </summary>
[Trait("IO", "true")]
[Trait("Concurrency", "true")]
[Trait("Timing", "true")]
public class PickerRoundTripTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static string CatalogDirectory()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://example.com/orders.git
                project: Service.csproj
                container:
                  image: example/orders
                  port: 8080
              billing:
                repository: https://example.com/billing.git
                project: Service.csproj
              audit:
                repository: https://example.com/audit.git
                project: Service.csproj
            """);
        return dir;
    }

    private static IDistributedApplicationBuilder PromptingBuilder(string dir, FakeInteractionService interaction)
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.Services.AddSingleton<IInteractionService>(interaction);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CI"] = "",
            ["TF_BUILD"] = "",
            ["JENKINS_URL"] = "",
            [InteractivePrompts.EnvironmentVariable] = "true",
        });
        builder.UseInteractiveServiceConfiguration();
        return builder;
    }

    [Fact]
    public async Task SavedAnswers_ResolveOnAFreshBuilder()
    {
        var dir = CatalogDirectory();
        var interaction = new FakeInteractionService
        {
            IsAvailable = true,
            AnswerFor = (title, inputs) =>
            {
                if (title == UnconfiguredPrompt.Title)
                {
                    inputs[0].Value = "container";
                    inputs[1].Value = "url";
                    inputs[2].Value = "disabled";
                }
                else
                {
                    inputs[0].Value = "http://localhost:5050";
                }

                return InteractionResult.Ok(new InteractionInputCollection(inputs));
            },
        };

        var first = PromptingBuilder(dir, interaction);
        first.AddService("orders");
        first.AddService("billing");
        first.AddService("audit");
        await TestHelpers.PublishBeforeStartEventAsync(first);
        await Task.WhenAll(UnconfiguredRegistry.For(first).PromptTasks).WaitAsync(Patience);

        Assert.True(File.Exists(Path.Combine(dir, DeveloperConfiguration.FileName)));

        var next = TestHelpers.CreateBuilderThatCanStart(dir);
        next.AddService("orders");
        var billing = next.AddService("billing");
        next.AddService("audit");

        Assert.Empty(next.Resources.OfType<UnconfiguredServiceResource>());
        Assert.Single(next.Resources.OfType<ServiceContainerResource>(), r => r.Name == "orders");

        var endpoint = Assert.Single(billing.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("localhost", endpoint.TargetHost);
        Assert.Equal(5050, endpoint.Port);
    }
}
