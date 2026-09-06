using System.Data.Common;
using System.Text;
using Aspire.Hosting.ServiceSources.BackingServices;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// The redaction checked against an independent parser rather than against a list of shapes.
/// </summary>
/// <remarks>
/// Every gap found in this redaction has been a disagreement about where a value ends — which
/// character separates a pair, what a quote spans, what an escape means. Those are questions
/// <see cref="DbConnectionStringBuilder"/> already answers, and answers as the runtime does, so it
/// makes a better judge of them than another hand-written list of examples.
/// <para>
/// It judges rather than redacts because it cannot do the job itself: it mis-parses libpq's
/// space-separated conninfo into a single pair with the password inside the value, throws on a bare
/// <c>redis://</c> URL and on <c>localhost:6379</c>, drops an empty <c>Port=</c> — which is the
/// diagnosis this message exists to deliver — and reports values without the offsets needed to hide
/// one in place. Over the dialect it does parse, though, it is authoritative.
/// </para>
/// </remarks>
public class ConnectionStringRedactionOracleTests
{
    private const string Secret = "hunter2";

    /// <summary>
    /// Keys the redaction prints values under, so the oracle knows which values it may see.
    /// </summary>
    private static readonly string[] Allowlisted =
        ["Host", "Server", "Data Source", "Port", "Database", "Initial Catalog", "User", "User Id", "Uid"];

    private static readonly string[] Unrecognised =
        ["Password", "Rotation Key", "2fa", "Encrypt", "X-Api-Key", "Lösenord", "SharedAccessKey", "Token"];

    private static readonly string[] Values =
        [Secret, $"a;{Secret}", $"a b {Secret}", $"a,{Secret}", $"{Secret}=x", $"a@{Secret}", $"a:b@{Secret}",
         $"'{Secret}'", $"\"{Secret}\"", $"{Secret}#x", $"{Secret}?x", $"{Secret}&x", $"//{Secret}", $"={Secret}"];

    private static readonly string[] Spacings = ["=", " =", "= ", " = "];

    private static readonly string[] Separators = [";", ";;", "; ", " ;"];

    /// <summary>
    /// A value the parser reports under a key nothing recognises never appears in the output.
    /// </summary>
    /// <remarks>
    /// The property, rather than a list of the shapes anyone has thought of so far. Inputs the
    /// parser rejects are skipped: it is being consulted about the dialect it owns, and a string it
    /// cannot read is one it has no opinion about.
    /// </remarks>
    [Fact]
    public void NoValueTheParserReadsUnderAnUnrecognisedKey_SurvivesRedaction()
    {
        var checkedInputs = 0;
        var failures = new StringBuilder();

        foreach (var secretKey in Unrecognised)
        {
            foreach (var value in Values)
            {
                foreach (var spacing in Spacings)
                {
                    foreach (var separator in Separators)
                    {
                        foreach (var leading in Allowlisted)
                        {
                            var input = $"{leading}{spacing}localhost{separator}{secretKey}{spacing}{value}"
                                + $"{separator}Database{spacing}orders";

                            if (!Parses(input, out var pairs))
                            {
                                continue;
                            }

                            // Only where the parser agrees the secret sits under the key that should
                            // hide it — it may have read the string differently than intended.
                            if (!pairs.TryGetValue(secretKey, out var parsed)
                                || !parsed.Contains(Secret, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            checkedInputs++;

                            var redacted = ConnectionStringRedaction.Redact(input);

                            if (redacted.Contains(Secret, StringComparison.Ordinal))
                            {
                                failures.AppendLine($"  {input}\n    -> {redacted}");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(checkedInputs > 500, $"only {checkedInputs} inputs were judged; the corpus is not exercising much");
        Assert.True(failures.Length == 0, $"{failures.ToString().Split('\n').Length / 2} leaks:\n{failures}");
    }

    /// <summary>
    /// Whether <paramref name="input"/> is the dialect the oracle owns, and what it reads in it.
    /// </summary>
    private static bool Parses(string input, out Dictionary<string, string> pairs)
    {
        pairs = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = input };

            foreach (string key in builder.Keys)
            {
                pairs[key] = builder[key]?.ToString() ?? "";
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
