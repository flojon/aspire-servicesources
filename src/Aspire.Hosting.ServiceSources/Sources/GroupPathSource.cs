using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// A <c>repositories.&lt;name&gt;</c> entry with <c>"source": "path"</c>: wherever a grouped member
/// would use the group's checkout, it uses this directory instead.
/// </summary>
/// <remarks>
/// Decides which members the entry reaches. Every reader of "does this member run from a managed
/// clone" — <c>AddService</c>'s source selection and the speculative prefetch — asks here, so they
/// cannot disagree about it.
/// </remarks>
internal static class GroupPathSource
{
    internal const string PathSourceName = "path";

    private const string RepositorySourceName = "repository";

    /// <summary>
    /// Whether <paramref name="serviceName"/> is resolved by <see cref="PathSource"/> against the
    /// group's directory, whatever its own source says between <c>repository</c> and <c>path</c>.
    /// </summary>
    /// <remarks>
    /// Developers list every member as <c>"repository"</c> because nothing else configures them, so a
    /// member's own source cannot beat the group's or the override would never apply. A member that
    /// chose <c>url</c>, <c>container</c>, <c>kubernetes</c> or <c>disabled</c> is left alone, and a
    /// member's own directory (<c>path.path</c>, <c>repository.path</c>) wins over the group's.
    /// </remarks>
    public static bool Redirects(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config,
        RepositoryDeveloperConfig? repositoryConfig)
    {
        if (repositoryConfig is null
            || !string.Equals(repositoryConfig.Source, PathSourceName, StringComparison.OrdinalIgnoreCase)
            || !LocalGitCheckout.IsGrouped(definition, serviceName))
        {
            return false;
        }

        if (string.Equals(config.Source, RepositorySourceName, StringComparison.OrdinalIgnoreCase))
        {
            return config.Repository.Path is null;
        }

        return string.Equals(config.Source, PathSourceName, StringComparison.OrdinalIgnoreCase)
            && config.Path.Path is null;
    }

    /// <summary>The group's directory, resolved like a service's <c>path.path</c> and required to exist.</summary>
    public static string ResolveDirectory(
        ServiceDefinition definition, RepositoryDeveloperConfig repositoryConfig, string appHostDirectory)
    {
        var checkoutName = definition.Repository.CheckoutName;
        var key = $"{DeveloperConfiguration.RepositoriesKey}:{checkoutName}:path:path";
        var subject = Raw.Compose($"Repository '{new Name(checkoutName)}'");

        if (repositoryConfig.Path?.Path is not { } path)
        {
            throw ServiceSourcesConfigurationException.For(
                $"{subject}: source is 'path' but no directory is set. Set '{Raw.Escaped(key)}' to a directory you already have on disk, or set the repository's 'source' to 'repository' to use the managed checkout.");
        }

        return LocalGitCheckout.ResolveDeveloperDirectory(subject, key, path, appHostDirectory);
    }
}
