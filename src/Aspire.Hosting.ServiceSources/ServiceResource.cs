using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

public sealed class ServiceResource : Resource, IResourceWithServiceDiscovery
{
    internal ServiceResource(string name) : base(name)
    {
    }

    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateFacade(
        IDistributedApplicationBuilder builder, string name, IResourceBuilder<ProjectResource> realResource)
    {
        var facade = builder.CreateResourceBuilder(new ServiceResource(name));

        foreach (var endpoint in realResource.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            facade.Resource.Annotations.Add(endpoint);
        }

        return facade;
    }
}
