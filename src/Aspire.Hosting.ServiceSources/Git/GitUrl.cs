namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// The one parser for the repository URL forms this package accepts. SSH detection, credential
/// host lookup, and repository-identity comparison all read from it, so they can't drift apart on
/// the edge cases (scp-like syntax, userinfo, explicit ports, Windows drive paths).
/// </summary>
internal sealed record GitUrl
{
    private GitUrl(string? scheme, string? host, string path, bool isScpSyntax)
    {
        Scheme = scheme;
        Host = host;
        Path = path;
        IsScpSyntax = isScpSyntax;
    }

    /// <summary>Lowercased scheme, or <see langword="null"/> for scp-like syntax and local paths.</summary>
    public string? Scheme { get; }

    /// <summary>
    /// Host including any explicit port (which is how <c>git credential</c> expects it), or
    /// <see langword="null"/> for a local filesystem path.
    /// </summary>
    public string? Host { get; }

    /// <summary>Repository path, with any trailing '/' and '.git' suffix removed.</summary>
    public string Path { get; }

    /// <summary>Whether this was written as scp-like <c>[user@]host:path</c>.</summary>
    public bool IsScpSyntax { get; }

    public bool IsSsh => IsScpSyntax || Scheme is "ssh" or "git+ssh";

    public bool IsHttp => Scheme is "http" or "https";

    /// <summary>
    /// Host-and-path identity used to decide whether two URLs name the same repository, so an
    /// HTTPS remote and the equivalent SSH remote compare equal.
    /// </summary>
    public string Identity => Host is null ? Path : $"{Host}/{Path}";

    /// <summary>
    /// The URL with any userinfo removed, for use in messages. A repository URL may legitimately
    /// carry a personal access token — as the password, or as the username with no password at all —
    /// and an exception message reaches the console and every log sink the AppHost is wired to, so
    /// the whole userinfo goes rather than only the part after a ':'. Everything else is left
    /// byte-for-byte as the developer wrote it, so the message still names a URL they recognize.
    /// This is what git itself shows when a remote operation fails.
    /// </summary>
    public static string Redact(string repositoryUrl)
    {
        // Only the "scheme://[userinfo@]host/path" form has a userinfo component to remove: scp-like
        // syntax has no password syntax at all (its user is an SSH account name, not a secret), and a
        // local filesystem path has no authority to carry one.
        var schemeIndex = repositoryUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return repositoryUrl;
        }

        var authorityStart = schemeIndex + 3;
        var authorityEnd = repositoryUrl.IndexOf('/', authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = repositoryUrl.Length;
        }

        // The last '@' within the authority, for the same reason StripUserInfo uses it: a token can
        // itself contain '@'. An '@' past the authority belongs to the path and is left alone.
        var atIndex = repositoryUrl.AsSpan(authorityStart, authorityEnd - authorityStart).LastIndexOf('@');
        return atIndex < 0
            ? repositoryUrl
            : string.Concat(
                repositoryUrl.AsSpan(0, authorityStart),
                repositoryUrl.AsSpan(authorityStart + atIndex + 1));
    }

    public static GitUrl Parse(string repositoryUrl)
    {
        var trimmed = TrimSuffixes(repositoryUrl);

        var schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            var scheme = trimmed[..schemeIndex].ToLowerInvariant();
            var rest = StripUserInfo(trimmed[(schemeIndex + 3)..]);
            var slashIndex = rest.IndexOf('/');
            return slashIndex >= 0
                ? new GitUrl(scheme, rest[..slashIndex], rest[(slashIndex + 1)..], isScpSyntax: false)
                : new GitUrl(scheme, rest, path: "", isScpSyntax: false);
        }

        if (TryFindScpColon(trimmed, out var colonIndex))
        {
            return new GitUrl(
                scheme: null,
                StripUserInfo(trimmed[..colonIndex]),
                trimmed[(colonIndex + 1)..],
                isScpSyntax: true);
        }

        // A local filesystem path, including a Windows drive path such as "C:\repos\orders".
        return new GitUrl(scheme: null, host: null, trimmed, isScpSyntax: false);
    }

    private static string TrimSuffixes(string repositoryUrl)
    {
        var trimmed = repositoryUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].TrimEnd('/');
        }

        return trimmed;
    }

    private static string StripUserInfo(string hostAndPath)
    {
        var slashIndex = hostAndPath.IndexOf('/');
        var authority = slashIndex < 0 ? hostAndPath : hostAndPath[..slashIndex];

        // The last '@' in the authority, not the first: a personal access token pasted straight into
        // the URL can itself contain '@', and everything up to the final one is userinfo. Splitting
        // on the first would leave the tail of the token in the host, which asks `git credential`
        // about a host that doesn't exist and misses the cache entry for the real one. An '@' after
        // the first '/' belongs to the path and is left alone.
        var atIndex = authority.LastIndexOf('@');
        return atIndex >= 0 ? hostAndPath[(atIndex + 1)..] : hostAndPath;
    }

    /// <summary>
    /// Finds the colon separating host from path in scp-like syntax (<c>[user@]host:path</c>, e.g.
    /// <c>git@github.com:example/orders</c>). Unlike requiring a literal '@' this also recognizes
    /// the implicit-user form (<c>host:path</c>) while still rejecting a Windows drive path, whose
    /// single-character prefix before the colon can never be a hostname.
    /// </summary>
    private static bool TryFindScpColon(string candidate, out int colonIndex)
    {
        colonIndex = candidate.IndexOf(':');
        if (colonIndex <= 1)
        {
            return false;
        }

        // A separator before the colon means the colon is inside a path segment, not a host/path
        // delimiter (e.g. "/mnt/my:dir/repo").
        return !candidate.AsSpan()[..colonIndex].ContainsAny('/', '\\');
    }
}
