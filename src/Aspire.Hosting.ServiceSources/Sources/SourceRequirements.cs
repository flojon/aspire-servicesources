using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>One extra answer the interactive picker asks for; <paramref name="Key"/> is the config path under the service entry.</summary>
internal sealed record FollowUpField(string Key, string Label, bool Required, InputType InputType);

/// <summary>What a source is asked about: <c>Resolve</c>'s own inputs, minus the builder.</summary>
internal sealed record SourceInput(
    string ServiceName, ServiceDefinition Definition, ServiceDeveloperConfig Config,
    string AppHostDirectory, RepositoryDeveloperConfig? RepositoryConfig = null);

/// <summary>What a source whose check has nothing to hand to its build computes.</summary>
internal readonly record struct NoValues;

/// <summary>
/// What a source needs before it resolves: the settings the dialog must still ask for, and the first
/// reason <c>Resolve</c> would refuse the entry as it stands.
/// </summary>
internal sealed record SourceRequirements(IReadOnlyList<FollowUpField> Fields, SourceProblem? Problem)
{
    /// <summary>Works as it stands.</summary>
    public bool Resolves => Problem is null;

    /// <summary>
    /// The picker may offer it: it resolves, or the only thing wrong is a setting the dialog asks for.
    /// </summary>
    public bool Offered =>
        Problem is null
        || (Problem.MissingSetting is { } key && Fields.Any(f => f.Key == key));

    /// <summary>The answer when a source's check itself failed unexpectedly: neither resolves nor offered.</summary>
    public static SourceRequirements Unknown { get; } = new(
        [], new SourceProblem(ServiceSourcesConfigurationException.For($"The source's requirements could not be determined.")));
}

/// <summary>The first reason a source would refuse an entry, with the setting it is missing if that is the reason.</summary>
internal sealed record SourceProblem(ServiceSourcesConfigurationException Exception)
{
    public string Message => Exception.Message;

    public string? MissingSetting => Exception.MissingSetting;

    /// <summary>
    /// Runs <paramref name="check"/> and returns what it refuses with, or <see langword="null"/> when it
    /// returns. Only a configuration exception is a problem; anything else is a bug and propagates.
    /// </summary>
    public static SourceProblem? Capture(Action check)
    {
        try
        {
            check();
            return null;
        }
        catch (ServiceSourcesConfigurationException ex)
        {
            return new SourceProblem(ex);
        }
    }
}
