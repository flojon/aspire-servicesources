using Aspire.Hosting.ServiceSources.BackingServices;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// Randomised coverage of dialect styles the fixed-example tests in
/// <see cref="ConnectionStringRedactionTests"/> and the parser-judged
/// <see cref="ConnectionStringRedactionOracleTests"/> do not enumerate.
/// </summary>
/// <remarks>
/// Deterministic (a fixed seed per style), so a failure reproduces without needing the seed logged.
/// Each style embeds <see cref="Marker"/> — a string that cannot match any of the five value shapes,
/// because of the <c>!</c> in it — somewhere a leak would put it, then asserts it never survives.
/// </remarks>
public class ConnectionStringRedactionFuzzTests
{
    private const string Marker = "L3aked!Marker9";

    private const int IterationsPerStyle = 4_000;

    private static readonly string[] SecretKeys =
        ["Password", "Pwd", "Secret", "Token", "2fa", "Rotation Key", "X-Api-Key", "SharedAccessKey"];

    private static readonly string[] AllowlistedKeys =
        ["Host", "Server", "Data Source", "Port", "Database", "Initial Catalog", "User", "User Id", "Uid", "Driver"];

    private static readonly string[] Separators = [";", "&", "?", ",", "; ", " ;", ";;"];

    private static readonly string[] Spacings = ["=", " =", "= ", " = "];

    private static readonly Func<string, string>[] Quotings =
        [v => v, v => $"'{v}'", v => $"\"{v}\"", v => $" {v}"];

    /// <summary>Key=value dialects, fuzzed across separator, spacing and quoting choices.</summary>
    [Fact]
    public void KeyValueDialect_NeverLeaksTheMarker()
    {
        var rng = new Random(20260915_1);

        for (var i = 0; i < IterationsPerStyle; i++)
        {
            var allowKey = Pick(rng, AllowlistedKeys);
            var secretKey = Pick(rng, SecretKeys);
            var sep = Pick(rng, Separators);
            var spacing = Pick(rng, Spacings);
            var quoted = Pick(rng, Quotings)(Marker);

            var input = $"{allowKey}{spacing}localhost{sep}{secretKey}{spacing}{quoted}{sep}Database{spacing}orders";

            AssertNoLeak(input);
        }
    }

    private static readonly string[] Schemes =
        ["postgres", "postgresql", "mysql", "redis", "mongodb", "amqp", "sqlserver", "kafka",
         "jdbc:postgresql", "jdbc:mysql"];

    /// <summary>URI authorities across several schemes, with the marker in userinfo, query or fragment.</summary>
    [Fact]
    public void UriAuthority_NeverLeaksTheMarkerFromUserinfoQueryOrFragment()
    {
        var rng = new Random(20260915_2);

        for (var i = 0; i < IterationsPerStyle; i++)
        {
            var scheme = Pick(rng, Schemes);

            var input = rng.Next(3) switch
            {
                0 => $"{scheme}://user:{Marker}@host:5432/db",
                1 => $"{scheme}://host:5432/db?token={Marker}",
                _ => $"{scheme}://host:5432/db#{Marker}",
            };

            AssertNoLeak(input);
        }
    }

    private static readonly string[] HostShapedKeys = ["Data Source", "Server", "Host"];

    /// <summary>
    /// Oracle's slash-form credential, both the TNS and the host:port:sid spellings, under a
    /// host-shaped key — the exotic-value case <see cref="ConnectionStringRedaction"/>'s own remarks
    /// name explicitly, so this exercises the host shape's rejection of it rather than the unrelated
    /// bare-prefix fallback a scheme-less, colon-laden descriptor would otherwise fall through to.
    /// </summary>
    [Fact]
    public void OracleSlashFormCredential_NeverLeaksTheMarker()
    {
        var rng = new Random(20260915_3);
        var users = new[] { "scott", "orders_app", "svc_user" };

        for (var i = 0; i < IterationsPerStyle; i++)
        {
            var user = Pick(rng, users);
            var key = Pick(rng, HostShapedKeys);

            var descriptor = rng.Next(2) == 0
                ? $"{user}/{Marker}@//host:1521/service"
                : $"{user}/{Marker}@host:1521:sid";

            AssertNoLeak($"{key}={descriptor}");
        }
    }

    /// <summary>
    /// libpq's space-separated conninfo, pairs fuzzed in order, spacing and quoting. The secret sits
    /// under <c>2fa</c> rather than <c>password</c>, and <c>host</c> is pinned first: a literal
    /// <c>password=</c> is masked by the keyword backstop before this reaches the shape/nested-pair
    /// engine at all, and a shuffle that put an unrecognised key first would do the same by masking
    /// the whole value without descending into it — either way leaving the engine this style exists
    /// to fuzz untested.
    /// </summary>
    [Fact]
    public void LibpqConninfo_NeverLeaksTheMarker()
    {
        var rng = new Random(20260915_4);

        for (var i = 0; i < IterationsPerStyle; i++)
        {
            var tail = new List<string>
            {
                "port=5432",
                $"2fa={Pick(rng, Quotings)(Marker)}",
                "dbname=orders",
            };

            Shuffle(rng, tail);

            AssertNoLeak(string.Join(' ', new[] { "host=db.internal" }.Concat(tail)));
        }
    }

    /// <summary>
    /// A pair nested one level inside a value already recognised as safe — plus the value under an
    /// allowlisted key directly holding the marker, which breaks that key's own shape.
    /// </summary>
    [Fact]
    public void NestedUnrecognisedKey_NeverLeaksTheMarker()
    {
        var rng = new Random(20260915_5);
        var nestedKeys = new[] { "custom", "extra", "2fa", "note" };

        for (var i = 0; i < IterationsPerStyle; i++)
        {
            var allowKey = Pick(rng, AllowlistedKeys);
            var nestedKey = Pick(rng, nestedKeys);

            // Must match allowKey's own shape, or the head check fails before the nested pair is ever
            // reached — Port is the one allowlisted key "db.internal" cannot stand in for.
            var head = allowKey == "Port" ? "5432" : "db.internal";

            var input = rng.Next(2) == 0
                ? $"{allowKey}={head} {nestedKey}={Marker}"
                : $"{allowKey}={Marker}";

            AssertNoLeak(input);
        }
    }

    /// <summary>
    /// A value of nothing but words and spaces inside <c>Driver=</c>/<c>Provider=</c> braces matches
    /// <see cref="ConnectionStringRedaction"/>'s driver shape and prints whole — the one style the
    /// original ad hoc fuzz run found leaking, kept here as a known-failing case rather than a
    /// silently-passing one, so a future tightening of the shape is what this test notices.
    /// </summary>
    [Fact]
    public void PlainWordsUnderDriverOrProvider_IsTheDocumentedResidual()
    {
        const string wordsOnlyMarker = "leaked driver secret words";

        var redacted = ConnectionStringRedaction.Redact($"Driver={{{wordsOnlyMarker}}}");

        Assert.Contains(wordsOnlyMarker, redacted, StringComparison.Ordinal);
    }

    private static void AssertNoLeak(string input)
    {
        var redacted = ConnectionStringRedaction.Redact(input);

        Assert.DoesNotContain(Marker, redacted, StringComparison.Ordinal);
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> options) => options[rng.Next(options.Count)];

    private static void Shuffle<T>(Random rng, IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
