namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The clauses every message about an out-of-band source is built from: why the source runs out of
/// band, the one way back under this AppHost's control, and what a reader whose endpoint write was
/// undone can reach instead.
/// </summary>
/// <remarks>
/// Shared across warnings and exceptions alike, because the offer is conditional and a copy that
/// drops the condition is a dead end rather than a paraphrase.
/// </remarks>
internal static class OutOfBandSourceAdvice
{
    /// <summary>
    /// How to bring the service back under this AppHost's control, as a clause each message leads
    /// into in its own grammar.
    /// </summary>
    /// <remarks>
    /// Conditional rather than an instruction, because the switch is only available where the
    /// catalog already declares that source: <c>'local'</c> without a <c>repository</c>, or
    /// <c>'container'</c> without a <c>container</c> block, throws
    /// <see cref="ServiceSourcesConfigurationException"/> at the next resolve.
    /// <para>
    /// The block, not the individual field, because what a block has to carry varies with the
    /// service: <c>'local'</c> needs a <c>project</c> for the built-in <c>dotnet</c> kind and that
    /// kind's own options for any other, and the error the catalog raises names whichever one is
    /// missing. Naming fields here would be right for the common service and wrong for the rest.
    /// </para>
    /// </remarks>
    internal const string SwitchSource =
        "give it a 'local' or 'container' source in servicesources.local.json — which works only " +
        "where its 'servicesources.yaml' entry already declares that source: a 'repository' or " +
        "'repositoryRef' for 'local', a 'container' block for 'container'";

    /// <summary>
    /// Why a source runs out of band, in the clause each message builds its sentence around.
    /// </summary>
    /// <remarks>
    /// The fallback is deliberately vague: a source outside
    /// <see cref="Sources.Reachability.OutOfBandSources"/> reaches it only through a caller that has
    /// already decided the service runs out of band, so there is nothing specific left to say.
    /// </remarks>
    internal static string SourceDetail(string source) => source switch
    {
        "url" =>
            "it resolves to a fixed, already-running URL with no local process to configure",
        "kubernetes" =>
            "it resolves to a 'kubectl port-forward' in front of an already-running service, so the " +
            "configuration would reach kubectl rather than the service",
        _ => "it runs out of band",
    };

    /// <summary>
    /// Where the endpoint can still be redirected, for the reader whose endpoint write was undone.
    /// </summary>
    /// <remarks>
    /// Its own sentence rather than part of <see cref="SwitchSource"/>: the switch is offered by
    /// messages about configuration and start ordering, which this does not help, and this is the
    /// only lever that works on a url-only catalog entry — the shape the switch's precondition rules
    /// out.
    /// <para>
    /// "Points at" rather than "moves": for <c>kubernetes</c> these settings choose what
    /// <c>kubectl port-forward</c> forwards to, while the local port stays allocated, so promising
    /// the endpoint's own port would be the next dead end.
    /// </para>
    /// <para>
    /// The rename is named because both sources name an endpoint after its scheme, so a reader who
    /// follows the scheme half loses the name their <c>GetEndpoint</c> matches — the failure this
    /// detector exists to report, arrived at by following its own advice.
    /// </para>
    /// <para>
    /// For <c>kubernetes</c> the scheme is not a second redirect: it never reaches
    /// <c>BuildPortForwardArgs</c>, so the forward keeps its target and only the name and the scheme
    /// consumers address move. Offering it as one would cost the rename and buy nothing.
    /// </para>
    /// </remarks>
    internal static string RedirectTheEndpoint(string source) => source switch
    {
        "url" =>
            "To change what this endpoint points at, set 'url.url' for this service in " +
            "servicesources.local.json. Changing its scheme renames the endpoint, which is named " +
            "for it.",
        "kubernetes" =>
            "To change what this endpoint points at, set 'kubernetes.port' for this service in " +
            "servicesources.local.json. 'kubernetes.scheme' changes only the scheme consumers " +
            "address it with — the forward reaches the same service — and renames the endpoint, " +
            "which is named for it.",
        _ => "To change what this endpoint points at, configure its source in servicesources.local.json.",
    };

    /// <summary>
    /// What the service does have, for the reader whose <em>added</em> endpoint was removed.
    /// </summary>
    /// <remarks>
    /// Stands in for <see cref="RedirectTheEndpoint"/> rather than joining it: no setting adds an
    /// endpoint to an out-of-band service, so offering one to a reader who was adding is advice that
    /// cannot be followed. What they lack is the name to reference instead.
    /// </remarks>
    internal static string TheEndpointsItHas(string quotedNames) =>
        $"This service has the endpoints its source registers — {quotedNames} — and no others.";
}
