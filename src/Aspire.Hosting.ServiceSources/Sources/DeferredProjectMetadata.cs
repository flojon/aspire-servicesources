namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The <see cref="IProjectMetadata"/> a deferred <c>"local"</c> service is registered with: a
/// <c>.csproj</c> path that does not exist yet, plus just enough launch-settings cover to get the
/// resource through composition without one.
/// </summary>
/// <remarks>
/// <para>
/// Aspire reads a project's <c>launchSettings.json</c> during composition
/// (<c>WithProjectDefaults</c> calls <c>GetEffectiveLaunchProfile(throwIfNotFound: true)</c>), and
/// <c>LaunchProfileExtensions.GetLaunchSettings</c> throws outright when the <c>.csproj</c> is not
/// on disk. That is what makes <c>AddProject(name, missingPath)</c> fatal, and it is the single
/// thing this type exists to get past.
/// </para>
/// <para>
/// It gets past it by answering <see cref="LaunchSettings"/> itself while the checkout is missing:
/// a non-null value short-circuits the file read, and an <em>empty</em> profile set makes every
/// launch-profile selector decline, so <c>GetEffectiveLaunchProfile</c> returns null rather than
/// throwing for a profile that isn't there. Once the checkout lands this returns null again and
/// Aspire reads the repository's real <c>launchSettings.json</c> — which it does at start time, in
/// <c>ExecutableCreator.CreateObjectAsync</c>, and therefore after the clone for a resource held
/// back by <c>WithExplicitStart()</c>.
/// </para>
/// <para>
/// The alternative — <c>ProjectResourceOptions.ExcludeLaunchProfile</c>, which also survives
/// composition — is worse for the same case: it stamps an <c>ExcludeLaunchProfileAnnotation</c> on
/// the resource, so the launch profile stays discarded at start too, and the service loses its
/// profile arguments and environment permanently rather than for the cold run only.
/// </para>
/// <para>
/// What is lost either way is endpoints. Those are synthesised from <c>applicationUrl</c> during
/// composition, when the repository is not on disk, and nothing re-runs that step later — which is
/// why <see cref="DeferredCheckout"/> requires a deferred service to declare its endpoints in the
/// AppHost.
/// </para>
/// </remarks>
internal sealed class DeferredProjectMetadata(string projectPath) : IProjectMetadata
{
    public string ProjectPath { get; } = projectPath;

    /// <summary>
    /// A placeholder while the checkout is missing, and <see langword="null"/> — "read the real
    /// file" — once it has landed.
    /// </summary>
    /// <remarks>
    /// Probed on every read rather than latched, because the whole point is that the answer changes
    /// underneath Aspire: composition asks while the repository is still being cloned, and the
    /// start path asks again afterwards. A fresh instance each time, rather than one shared
    /// placeholder, because <see cref="Aspire.Hosting.LaunchSettings.Profiles"/> is a mutable
    /// dictionary on a type this package does not own.
    /// </remarks>
    public LaunchSettings? LaunchSettings => File.Exists(ProjectPath) ? null : new LaunchSettings();
}
