namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The <c>prepare:</c> block of a service's <c>servicesources.yaml</c> entry — a command run inside
/// the materialized checkout, once, before the kind turns that directory into a resource.
/// </summary>
/// <remarks>
/// Declared as a class in this namespace rather than as loose properties on
/// <see cref="ServiceMetadata"/>, which is what makes two existing mechanisms pick it up unchanged:
/// <see cref="ServiceCatalogLoader"/> rejects an unknown key <em>inside</em> the block because the
/// block's type is a nested one, and <see cref="Sources.LocalKindRegistry"/> starts refusing a kind
/// named <c>prepare</c> because the name is now a well-known top-level key — it must, since such a
/// kind's options block would be bound as this typed property and validated against this schema.
/// </remarks>
internal sealed class PrepareMetadata
{
    /// <summary>
    /// The command, as a list rather than a string: there is no shell, so there are no quoting or
    /// word-splitting rules to get wrong and an argument containing spaces needs no escaping. A
    /// first element that looks like a path is resolved against the checkout and confined to it; a
    /// bare name goes through <c>PATH</c>.
    /// </summary>
    public string[]? Command { get; set; }

    /// <summary>
    /// Replaces <see cref="Command"/> on Windows. Optional: with none set, <see cref="Command"/>
    /// runs there too, which is correct for a program that exists as an executable on every platform
    /// — <c>make</c>, <c>python</c>, <c>dotnet</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A program that is a <c>.cmd</c> or <c>.bat</c> shim on Windows is <em>not</em> one of those,
    /// and <c>npm</c> is the case to know about: there is no <c>npm.exe</c>, only <c>npm.cmd</c>.
    /// Nothing here goes through a shell, and Windows resolves a bare name on <c>PATH</c> by
    /// appending <c>.exe</c> rather than by walking <c>PATHEXT</c>, so <c>["npm", "ci"]</c> fails to
    /// start there. Such a command needs the variant — <c>["npm.cmd", "ci"]</c> — and the same goes
    /// for <c>yarn</c>, <c>pnpm</c> and <c>tsc</c>. The launch failure names this as the likely
    /// cause when it happens.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// It exists because one <c>servicesources.yaml</c> is committed and shared by a team on every
    /// platform, so each value can only be spelled one way, and <c>./prepare.sh</c> is not
    /// executable on Windows. The <c>java</c> kind solves the same problem by convention
    /// (<c>mvnw</c> → <c>mvnw.cmd</c>) because those wrapper names are fixed and known; an arbitrary
    /// bootstrap command has no canonical Windows counterpart, so it has to be explicit.
    /// <para>
    /// Named <c>windowsCommand</c> rather than <c>windows</c> because <c>command</c>/<c>windows</c>
    /// are not parallel — one names <em>what</em> to run and the other <em>when</em> — leaving a
    /// reader to guess whether it replaces the command, adds to it, or implies the command was the
    /// POSIX one all along. There is deliberately no Linux or macOS variant: the only distinction
    /// that has ever mattered here is POSIX versus Windows, and a script that must differ between
    /// Linux and macOS can branch internally.
    /// </para>
    /// </remarks>
    public string[]? WindowsCommand { get; set; }

    /// <summary>
    /// How often the step runs: <c>oncePerCommit</c> (the default), <c>once</c>, <c>always</c> or
    /// <c>never</c>. See <see cref="Prepare.PrepareMode"/>.
    /// </summary>
    /// <remarks>
    /// A <see cref="string"/> rather than the enum, so an unknown value is rejected by name with all
    /// four accepted spellings listed. Bound as an enum it would fail inside YamlDotNet here and
    /// inside the configuration binder in <c>servicesources.local.json</c>, in two different
    /// wordings, neither of them naming the service.
    /// </remarks>
    public string? Mode { get; set; }
}
