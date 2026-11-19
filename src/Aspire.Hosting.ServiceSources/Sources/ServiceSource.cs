using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// A source that checks its configuration once, in <see cref="Check"/>, and reads that one check for
/// <see cref="Resolve"/>, the picker and the save validator alike, so they cannot disagree.
/// </summary>
/// <typeparam name="TChecked">What <see cref="Check"/> computed that <see cref="Build"/> needs, so nothing is parsed twice.</typeparam>
internal abstract class ServiceSource<TChecked> : IServiceSource
{
    /// <summary>The settings the dialog must still ask for, given the catalog entry.</summary>
    protected abstract IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition);

    /// <summary>
    /// Every configuration check <see cref="Build"/> relies on, in the order its first failure should be reported.
    /// </summary>
    /// <remarks>
    /// Runs whenever the picker opens and inside other sources' error messages, so it has no side effect:
    /// no builder, no port allocation, no network, no process, no git, no write. A problem no dialog
    /// answer can fix is raised before a missing setting the dialog asks for.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">The entry would not resolve.</exception>
    protected abstract TChecked Check(SourceInput input);

    protected abstract IResourceBuilder<ServiceResource> Build(
        IDistributedApplicationBuilder builder, SourceInput input, TChecked checkedValues);

    /// <summary>Whether an error message about another source may suggest switching to this one.</summary>
    public abstract bool IsSwitchRemedy { get; }

    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        var input = new SourceInput(serviceName, definition, config, builder.AppHostDirectory, repositoryConfig);

        return Build(builder, input, Check(input));
    }

    public SourceRequirements Requirements(SourceInput input) =>
        new(FieldsFor(input.Definition), SourceProblem.Capture(() => Check(input)));
}
