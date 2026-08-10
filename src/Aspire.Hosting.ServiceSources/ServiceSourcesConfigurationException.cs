namespace Aspire.Hosting.ServiceSources;

public sealed class ServiceSourcesConfigurationException : Exception
{
    public ServiceSourcesConfigurationException(string message) : base(message)
    {
    }

    public ServiceSourcesConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
