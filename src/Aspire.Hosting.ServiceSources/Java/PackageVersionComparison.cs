namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// Orders package versions the way NuGet does, for the one question core asks of an installed
/// preview: is it older than the one core was built against. An assembly version cannot answer
/// it, because a prerelease cut before a release carries that release's assembly version.
/// </summary>
internal static class PackageVersionComparison
{
    /// <summary>
    /// Whether <paramref name="installed"/> is older than <paramref name="minimum"/>. A version
    /// that does not parse is never called older: nothing here should fail an AppHost on a
    /// version string it cannot read.
    /// </summary>
    public static bool IsOlder(string installed, string minimum) =>
        TryParse(installed, out var left) && TryParse(minimum, out var right) && Compare(left, right) < 0;

    private static bool TryParse(string text, out (Version Core, string[] Prerelease) version)
    {
        version = default;

        // Build metadata ("+<commit>") never takes part in precedence.
        var withoutMetadata = text.Split('+')[0];
        var dash = withoutMetadata.IndexOf('-');
        var core = dash < 0 ? withoutMetadata : withoutMetadata[..dash];

        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = (parsed, dash < 0 ? [] : withoutMetadata[(dash + 1)..].Split('.'));
        return true;
    }

    private static int Compare((Version Core, string[] Prerelease) left, (Version Core, string[] Prerelease) right)
    {
        var core = left.Core.CompareTo(right.Core);

        if (core != 0)
        {
            return core;
        }

        // A release outranks any prerelease of it.
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
        {
            return right.Prerelease.Length.CompareTo(left.Prerelease.Length);
        }

        for (var i = 0; i < Math.Min(left.Prerelease.Length, right.Prerelease.Length); i++)
        {
            var identifier = CompareIdentifier(left.Prerelease[i], right.Prerelease[i]);

            if (identifier != 0)
            {
                return identifier;
            }
        }

        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = long.TryParse(left, out var leftNumber);
        var rightNumeric = long.TryParse(right, out var rightNumber);

        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            // Numeric identifiers rank below alphanumeric ones.
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }
}
