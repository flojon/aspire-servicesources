namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// Orders package versions the way NuGet does, for the one question core asks of an installed
/// preview: is it older than the one core was built against. An assembly version cannot answer
/// it, because a prerelease cut before a release carries that release's assembly version.
/// </summary>
internal static class PackageVersionComparison
{
    /// <summary>
    /// Whether <paramref name="installed"/> is older than <paramref name="minimum"/>. Answers
    /// <see langword="false"/> whenever it cannot tell: a version that does not parse, or two
    /// prereleases whose labels differ ("dev" against "preview"), which have no order here. Nothing
    /// should fail an AppHost on a version string this does not understand.
    /// </summary>
    public static bool IsOlder(string installed, string minimum) =>
        TryParse(installed, out var left) && TryParse(minimum, out var right) && Compare(left, right) < 0;

    private static bool TryParse(string text, out (int[] Core, string[] Prerelease) version)
    {
        version = default;

        // Build metadata ("+<commit>") never takes part in precedence.
        var withoutMetadata = text.Split('+')[0];
        var dash = withoutMetadata.IndexOf('-');
        var core = dash < 0 ? withoutMetadata : withoutMetadata[..dash];

        var parts = core.Split('.');
        var numbers = new int[Math.Max(parts.Length, 4)];

        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = (numbers, dash < 0 ? [] : withoutMetadata[(dash + 1)..].Split('.'));
        return true;
    }

    /// <summary>Negative, zero or positive; zero also for "cannot be ordered".</summary>
    private static int Compare((int[] Core, string[] Prerelease) left, (int[] Core, string[] Prerelease) right)
    {
        // Missing components are zero, so 13.6 and 13.6.0.0 are the same version.
        for (var i = 0; i < left.Core.Length; i++)
        {
            var part = left.Core[i].CompareTo(right.Core[i]);

            if (part != 0)
            {
                return part;
            }
        }

        // A release outranks any prerelease of it.
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
        {
            return right.Prerelease.Length.CompareTo(left.Prerelease.Length);
        }

        for (var i = 0; i < Math.Min(left.Prerelease.Length, right.Prerelease.Length); i++)
        {
            var leftNumeric = IsNumeric(left.Prerelease[i]);
            var rightNumeric = IsNumeric(right.Prerelease[i]);

            if (leftNumeric && rightNumeric)
            {
                var number = CompareNumbers(left.Prerelease[i], right.Prerelease[i]);

                if (number != 0)
                {
                    return number;
                }
            }
            else if (!string.Equals(left.Prerelease[i], right.Prerelease[i], StringComparison.OrdinalIgnoreCase))
            {
                // Different labels, or a label against a number: SemVer orders them, but a label
                // this package has never seen is not evidence the install is old.
                return 0;
            }
        }

        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private static bool IsNumeric(string identifier) =>
        identifier.Length > 0 && identifier.All(char.IsAsciiDigit);

    // Digit strings of any length: by length once leading zeros are gone, then by text.
    private static int CompareNumbers(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');

        return left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right);
    }
}
