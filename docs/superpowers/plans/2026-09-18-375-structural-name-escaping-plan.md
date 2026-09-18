# Structural Name Escaping — Seam Implementation Plan (#375)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the fail-closed message seam — an interpolated-string handler with no `string` hole overload — together with enough migrated sites to prove it, so that a caller-controlled name interpolated raw into a `ServiceSourcesConfigurationException` message stops compiling.

**Architecture:** A `[InterpolatedStringHandler]` (`ServiceTextHandler`) accepts only `Name` holes (escaped and capped), `Raw` holes (safe by construction) and an enumerated set of primitives. `ServiceSourcesConfigurationException` gains an `internal static For(...)` factory taking that handler and nothing else, so a raw `string` hole, a mixed additive chain, a pre-built `string` variable, a `char` and an `object` are all **compile errors**. `Microsoft.CodeAnalysis.BannedApiAnalyzers` bans the raw public constructor inside this package at **warning** severity, because ~139 unmigrated throws still use it; the step to `error` belongs to the follow-up.

**Tech Stack:** C# / .NET (`net8.0;net9.0;net10.0` multi-target), xunit, `Microsoft.CodeAnalysis.CSharp` (new, test-only, for the negative-compilation test), `Microsoft.CodeAnalysis.BannedApiAnalyzers` (new, src-only).

**Spec:** [`docs/superpowers/specs/2026-09-18-375-structural-name-escaping-design.md`](../specs/2026-09-18-375-structural-name-escaping-design.md)

---

## Global Constraints

- **The enumerated set of ticket sites is 14, never five.** The body names 5, its comment adds 9. No commit message, CHANGELOG entry or PR body may say "five named sites".
- **This PR lands the seam, not the bulk migration.** In scope: the handler, the `For(...)` factory, `BannedApiAnalyzers`, the naming-trap fix, `Describe`'s inner-message flattening, and the 14 enumerated sites. Out of scope: the other ~139 sink-A throws, `ServiceSourcesLog`/sink B closure, `KubernetesSecretException`, the `Git*` types. See Task 11 for what the follow-up must carry.
- **The banned-symbols gate lands at `warning`, not `error`.** It cannot be `error` while unmigrated sites remain.
- **CI builds with `-warnaserror` (`.github/workflows/ci.yml:78`).** A warning is an error there unless carved out explicitly. This is why Task 6 sets `WarningsNotAsErrors`. There is **no `.editorconfig` anywhere in this repo**; do not assume one exists.
- **Verify legs, run every task:** `dotnet build -c Release --no-restore -warnaserror` then `dotnet test -c Release --no-build`. Both cover net8.0/net9.0/net10.0 intrinsically. `-warnaserror` and `-c Release` are not optional — a build without them is a false green.
- **Two legs cannot run locally and must never be claimed green:** the TypeScript export-surface check (no `node`/`npm`, no pinned Aspire CLI) and the `verify-invariants` job (`python`/`python3` resolve to Microsoft Store stubs that print "not found" and **exit 0**, so a `which` check passes and the invariant silently does not run). Name both as CI-only in the PR body.
- **Namespace convention is folder-matching**, with an explicit `using` per file (e.g. `Config/ConfiguredValue.cs` → `Aspire.Hosting.ServiceSources.Config`). The seam goes in `Messages/` → `Aspire.Hosting.ServiceSources.Messages`.
- **Escaping order is load-bearing and is not reopened** (#374 settled it): `\` doubled **first**, then `ConfiguredValue.Bare`, then the quote replacements, then the cap. The ordering comments at `ServiceSourcesWarnings.cs:324-343` move with the code.
- **The cap applies to `Name` and to nothing else.** Capping a path, a joined list or an `ex.Message` at 64 characters would truncate silently past a green build.
- **Sibling PR #376 (#362)** adds message sites in `Sources/LocalProjectSource.cs` and `Sources/LocalCheckoutPrefetch.cs`. It lands first. Do **not** wait for it and do **not** rebase onto it; expect line numbers in those two files to have moved and locate the sites by their text, not by the line numbers quoted here.
- **CHANGELOG:** `[#375]` already has a link definition at `CHANGELOG.md:1734` (added by #372's entry, which references it at `:206`). Do **not** add a second definition.
- **Code comments:** only the non-obvious WHY, one sentence, under ~15 words. No changelogs, no ticket/PR references in code.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `src/Aspire.Hosting.ServiceSources/Messages/Name.cs` | The one escaping entry point: `\` doubling, `ConfiguredValue.Bare`, `'` and `"` neutralisation, 64-char cap. |
| `src/Aspire.Hosting.ServiceSources/Messages/Raw.cs` | Text already safe by construction. No accessible constructor. |
| `src/Aspire.Hosting.ServiceSources/Messages/ServiceTextHandler.cs` | The `[InterpolatedStringHandler]`. Enumerated holes only. |
| `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt` | The banned raw constructor. |
| `test/.../Messages/NameTests.cs` | Escaping, quoting, capping, surrogate and escape-unit behaviour. |
| `test/.../Messages/ServiceTextHandlerTests.cs` | Rendering, `Raw` non-re-escaping, per-hole capping. |
| `test/.../Messages/RawPathDoesNotCompileTests.cs` | Roslyn in-memory compilation: the negative-compilation proof. |
| `test/.../Messages/MigratedSiteEscapingTests.cs` | End-to-end forgery tests at migrated sink-A and composer sites. |

**Modified**

| File | Change |
|---|---|
| `ServiceSourcesConfigurationException.cs` | `internal static For(...)` ×2; `Describe` neutralises inner-message line breaks. |
| `ServiceSourcesWarnings.cs` | `Label` reimplemented over `Name`; `MaxLabelLength` and the cut logic move out. |
| `Prepare/PreparePlan.cs` | `ServiceLabel`/`RepositoryLabel` → `internal`, composed through the seam. |
| `Aspire.Hosting.ServiceSources.csproj` | `BannedApiAnalyzers` `PackageReference`, `AdditionalFiles`, `WarningsNotAsErrors`. |
| `Directory.Packages.props`, `src/Directory.Packages.props` | New `PackageVersion` entries. |
| `test/.../Aspire.Hosting.ServiceSources.Tests.csproj` | `Microsoft.CodeAnalysis.CSharp` `PackageReference`. |
| The 14 enumerated sites (9 sink-A + 5 composers) | Migrated to the seam. |
| `Git/LocalGitCheckout.cs`, `Sources/DeferredCheckout.cs`, `Sources/LocalProjectSource.cs` | `ServiceLabel`/`RepositoryLabel` callers. |
| `CHANGELOG.md` | entry pending a human decision — see Task 10 |

---

## Task 1: `Name` — the one escaping entry point

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Messages/Name.cs`
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Messages/NameTests.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs:305-343`

**Interfaces:**
- Consumes: `Aspire.Hosting.ServiceSources.Config.ConfiguredValue.Bare(string?)`.
- Produces: `internal readonly struct Name` with `internal Name(string? value)` and `public override string ToString()`; `internal const int Name.MaxLength = 64`. `ServiceSourcesWarnings.Label(string?)` keeps its signature and delegates.

**Why `"` is added here:** `Label` neutralises `'` only, and real double-quote-delimited caller-controlled holes exist (`Config/DeveloperConfiguration.cs:391`, `:392`, `:544`; `BackingServices/DirectBackingServiceSource.cs:49`; `BackingServices/KubernetesBackingServiceSource.cs:1145`, `:1169`; `BackingServiceBuilderExtensions.cs:249`, `:251`), several of which render a pasteable JSON snippet. Ticket requirement 6 is currently half-met.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Messages/NameTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class NameTests
{
    private static string Render(string? value) => new Name(value).ToString();

    [Fact]
    public void SingleQuote_IsNeutralised() =>
        Assert.Equal("ord\\'ers", Render("ord'ers"));

    [Fact]
    public void DoubleQuote_IsNeutralised() =>
        Assert.Equal("ord\\\"ers", Render("ord\"ers"));

    [Fact]
    public void Backslash_IsDoubledBeforeTheQuoteEscape() =>
        Assert.Equal("ord\\\\\\'ers", Render("ord\\'ers"));

    [Fact]
    public void Newline_IsSpelledOutRatherThanEmitted()
    {
        var rendered = Render("orders\nFATAL: forged");
        Assert.DoesNotContain("\n", rendered, StringComparison.Ordinal);
        Assert.Contains("\\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_RendersEmpty() => Assert.Equal(string.Empty, Render(null));

    [Fact]
    public void ShortName_IsUnchanged() => Assert.Equal("orders", Render("orders"));

    [Fact]
    public void LongName_IsCappedWithAnEllipsis()
    {
        var rendered = Render(new string('a', 200));
        Assert.EndsWith("…", rendered, StringComparison.Ordinal);
        Assert.Equal(Name.MaxLength + 1, rendered.Length);
    }

    [Fact]
    public void Cap_DoesNotSplitASurrogatePair()
    {
        // 32 astral characters = 64 UTF-16 units, so the cap lands exactly on a pair boundary+1.
        var rendered = Render(string.Concat(Enumerable.Repeat("\U0001F600", 40)));

        Assert.All(rendered[..^1].Chunk(2), pair =>
        {
            Assert.True(char.IsHighSurrogate(pair[0]));
            Assert.True(char.IsLowSurrogate(pair[1]));
        });
    }

    [Fact]
    public void Cap_DoesNotLandInsideAnEscapeUnit()
    {
        // Each \n costs two rendered characters; a cut between them would emit a lone backslash.
        var rendered = Render(new string('\n', 40));

        Assert.DoesNotContain("\\…", rendered, StringComparison.Ordinal);
        Assert.EndsWith("n…", rendered, StringComparison.Ordinal);
    }

    // Pinned against what Label returns on main @ c9301b7, so Step 4's rewrite cannot change the
    // rendering at the 12 existing Label call sites. Asserting Label == Name would be tautological
    // once Label delegates, and would leave exactly that behaviour unguarded.
    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("ord'ers", "ord\\'ers")]
    [InlineData("ord\\ers", "ord\\\\ers")]
    [InlineData("ord\ners", "ord\\ners")]
    [InlineData("ord\ters", "ord\\ters")]
    [InlineData("ord ers", "ord ers")]
    [InlineData("", "")]
    // The one deliberate change: Label left the double quote alone, Name neutralises it.
    [InlineData("ord\"ers", "ord\\\"ers")]
    public void Label_RendersAsItDidBeforeExceptForTheDoubleQuote(string input, string expected)
    {
        Assert.Equal(expected, ServiceSourcesWarnings.Label(input));
        Assert.Equal(expected, Render(input));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~NameTests"`
Expected: FAIL — `Name` does not exist (CS0246).

- [ ] **Step 3: Write `Name`**

Create `src/Aspire.Hosting.ServiceSources/Messages/Name.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Messages;

/// <summary>
/// A caller-controlled name made safe to sit inside the quotes a message delimits it with, and
/// bounded. The only escaping entry point a message hole has.
/// </summary>
internal readonly struct Name
{
    internal const int MaxLength = 64;

    private readonly string? value;

    internal Name(string? value) => this.value = value;

    public override string ToString()
    {
        // The escape character first, or a name's own '\' before a quote un-escapes back to a live one.
        var literal = value?.Replace("\\", "\\\\", StringComparison.Ordinal);

        // Bare must run between the two replaces: its own \t/\n/\uXXXX escapes are synthesized after
        // the doubling, so they are not themselves doubled, and are emitted before the quote escapes,
        // which do not touch '\' and so leave them alone.
        var escaped = ConfiguredValue.Bare(literal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return escaped.Length <= MaxLength ? escaped : escaped[..CutAt(escaped)] + '…';
    }

    /// <summary>
    /// The last boundary at or before the cap. Walked forward in whole units rather than counted
    /// backwards: a cut inside <c>\uXXXX</c> breaks injectivity, and half a surrogate pair is not text.
    /// </summary>
    private static int CutAt(string escaped)
    {
        var last = 0;

        for (var i = 0; i < escaped.Length && i <= MaxLength;)
        {
            last = i;

            if (escaped[i] != '\\')
            {
                i += char.IsHighSurrogate(escaped[i]) && i + 1 < escaped.Length ? 2 : 1;
                continue;
            }

            // Bare's longest unit is \uXXXX; every other escape it emits is two characters.
            i += i + 1 < escaped.Length && escaped[i + 1] == 'u' ? 6 : 2;
        }

        return last;
    }
}
```

> **Why forward rather than backward:** counting trailing backslashes only catches a cut landing immediately after an odd run of `\`. `ConfiguredValue.Bare` emits `\uXXXX`, so a cut can land mid-`\uXX` with an even backslash count. Walking units from the start is the only form that cannot.

- [ ] **Step 4: Reimplement `Label` over `Name`**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs`, delete `MaxLabelLength` (`:306`) and the body of `Label` (`:324-343`), leaving the XML doc in place, and replace the body with a delegation. Add `using Aspire.Hosting.ServiceSources.Messages;` to the file's using block.

```csharp
    internal static string Label(string? name) => new Name(name).ToString();
```

- [ ] **Step 5: Run the full verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS. Existing `Label` tests still pass except any that assert a `"` is left alone — if one does, it asserted the half-met state of requirement 6 and is **updated, not deleted**, with the new expectation.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Messages/Name.cs \
        src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/NameTests.cs
git commit -m "Move name escaping into a Name type and neutralise the double quote"
```

---

## Task 2: `Raw` and `ServiceTextHandler` — the seam

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Messages/Raw.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Messages/ServiceTextHandler.cs`
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Messages/ServiceTextHandlerTests.cs`

**Interfaces:**
- Consumes: `Name` (Task 1).
- Produces: `internal ref struct ServiceTextHandler` with `internal string Text { get; }`; `internal readonly struct Raw` with `internal static Raw Compose(ServiceTextHandler text)`, `internal static Raw Literal([ConstantExpected] string text)`, `internal static Raw Join([ConstantExpected] string separator, IEnumerable<Raw> parts)` and `internal static Raw Origin(CatalogOrigin origin)`.

**The whole trust surface is created here, not later**, so Task 4 can pin its constraints and a reviewer sees it in one place before the compilation proof is committed. No task after this one adds a way to make a `Raw`.

`Raw` and the handler are one task because they are mutually dependent by construction: `Raw.Compose` takes the handler and `AppendFormatted(Raw)` takes a `Raw`. A reviewer cannot accept one without the other.

**Three properties this task exists to establish, each observed in a probe:**

1. **`char` implicitly widens to `int`**, so an enumerated `AppendFormatted(int)` silently accepts `$"a{someChar}b"` — including `'\n'`. The blocking `[Obsolete(..., error: true)] AppendFormatted(char)` overload below turns that into **CS0619**, a hard error at every severity setting. This is a correction to spec §7.4, which assumed enumeration alone refused `char`.
2. **No alignment hole exists anywhere in `src/`.** Verified with `grep -rnE '\{[A-Za-z_][A-Za-z0-9_.()]*,-?[0-9]+[:}]' src/ --include=*.cs`, which returns nothing. That grep tests **alignment only** — it says nothing about format specifiers, so before relying on this, also run `grep -rnE '\{[A-Za-z_][A-Za-z0-9_.()]*:[A-Za-z0-9]+\}' src/ --include=*.cs` and confirm any hits are not name holes. So the overloads spec §7.5 asked for are **not added**: without them both `$"{name,-10}"` and `$"{name:N0}"` are **CS1739** (both re-confirmed by probe). Refusing the forms outright is strictly safer than adding an overload that must be remembered to escape. Spec test 13 accordingly becomes "the alignment and format-specifier forms do not compile" (Task 4).
3. `[Obsolete(..., error: true)]` produces an **error**, not a warning, so it does not interact with the obsoletion carve-out in `aspire-matrix.yml:270` that spec §6.1 warns about.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Messages/ServiceTextHandlerTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class ServiceTextHandlerTests
{
    private static string Compose(ServiceTextHandler text) => Raw.Compose(text).ToString();

    [Fact]
    public void LiteralSegments_AreVerbatim() =>
        Assert.Equal("Service is not configured.", Compose($"Service is not configured."));

    [Fact]
    public void NameHole_IsEscaped() =>
        Assert.Equal("Service 'ord\\'ers' failed.", Compose($"Service '{new Name("ord'ers")}' failed."));

    [Fact]
    public void NameHole_IsCapped()
    {
        var composed = Compose($"'{new Name(new string('a', 200))}'");

        Assert.Equal(Name.MaxLength + 3, composed.Length);
        Assert.EndsWith("…'", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void RawHole_IsNotEscapedAgain() =>
        Assert.Equal("outer 'ord\\'ers' tail", Compose($"outer {Raw.Compose($"'{new Name("ord'ers")}'")} tail"));

    [Fact]
    public void RawHole_IsNotCapped()
    {
        var longPath = Raw.Literal("/a/very/long/path/that/comfortably/exceeds/the/sixty/four/character/name/cap/orders.csproj");

        Assert.Contains("orders.csproj", Compose($"project {longPath}"), StringComparison.Ordinal);
    }

    [Fact]
    public void IntHole_IsRendered() => Assert.Equal("port 8080", Compose($"port {8080}"));

    [Fact]
    public void NullableIntHole_IsRendered() => Assert.Equal("port 8080", Compose($"port {(int?)8080}"));

    [Fact]
    public void DefaultRaw_RendersEmptyRatherThanThrowing() =>
        Assert.Equal("ab", Compose($"a{default(Raw)}b"));

    [Fact]
    public void MultipleNameHoles_AreEachEscapedIndependently() =>
        Assert.Equal("'a\\'1' and 'b\\'2'",
            Compose($"'{new Name("a'1")}' and '{new Name("b'2")}'"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ServiceTextHandlerTests"`
Expected: FAIL — `Raw` and `ServiceTextHandler` do not exist (CS0246).

- [ ] **Step 3: Write `Raw`**

Create `src/Aspire.Hosting.ServiceSources/Messages/Raw.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ServiceSources.Messages;

/// <summary>
/// Message text that is already safe: either composed through the seam, or a compile-time constant.
/// </summary>
/// <remarks>
/// No accessible constructor, deliberately. A wrapper anyone could call would be a one-token bypass
/// spelled identically to its safe use — the property #375 was filed against.
/// </remarks>
internal readonly struct Raw
{
    private readonly string? text;

    private Raw(string? text) => this.text = text;

    /// <summary>Text built through the seam, so every name inside it is already escaped.</summary>
    internal static Raw Compose(ServiceTextHandler text) => new(text.Text);

    /// <summary>A compile-time constant, which cannot carry a runtime value.</summary>
    internal static Raw Literal([ConstantExpected] string text) => new(text);

    /// <summary>
    /// Already-safe fragments joined. Safe by construction: every part is a <see cref="Raw"/>
    /// already and the separator is a constant, so nothing unescaped enters through here.
    /// </summary>
    internal static Raw Join([ConstantExpected] string separator, IEnumerable<Raw> parts) =>
        new(string.Join(separator, parts.Select(part => part.ToString())));

    /// <summary>
    /// Where a catalog entry came from. Takes the origin itself rather than its rendering, so a
    /// name cannot be passed here — and its wording carries quotes that must not be escaped.
    /// </summary>
    internal static Raw Origin(CatalogOrigin origin) => new(origin.Describe());

    /// <summary>Empty rather than null: <c>default(Raw)</c> must render, not throw.</summary>
    public override string ToString() => text ?? string.Empty;
}
```

Add `using Aspire.Hosting.ServiceSources.Config.Catalog;` for `CatalogOrigin`.

**There is deliberately no `Raw.Trusted(string)`, and this is the task's central design constraint.** A `string`-taking factory would be a one-token bypass spelled identically to its safe use — the property the ticket was filed against (spec §7.6, §13) — and guarding it with a grep over a hand-written identifier allowlist would reintroduce exactly the fail-open recogniser the spec rejected the `verify-invariants` option for (§6.2). Every non-name, non-constant hole in Tasks 8–9 is covered without one:

| Case | Covered by | Verified at plan time |
|---|---|---|
| `definition.Origin.Describe()`, `catalogOrigin.Describe()` | `Raw.Origin(origin)` | `Origin` is a `CatalogOrigin` (`Config/Catalog/ServiceDefinition.cs:42`), an `internal sealed record` (`CatalogOrigin.cs:15`) — the parameter type makes a `string` argument a compile error |
| `Redacted(rawUrl)` | `Redacted` rewritten to return `Raw` | it is `private static string` (`UrlSource.cs:325`) used only at `:350` and `:356` — zero ripple |
| `string.Join(", ", described)` over composed fragments | `Raw.Join(", ", parts)` with `parts` an `IEnumerable<Raw>` | every part is already `Raw` |
| `Http`, `Https`, `FileServicesKey`, `FileBackingServicesKey`, `FileName` | `Raw.Literal` | all `const` (`DeveloperConfigFileSource.cs:34`, `:43`; `DeveloperConfiguration.cs:26`) |
| `orphans.Count == 1 ? "it" : "any of them"` | the ternary moves inside: `cond ? Raw.Literal("it") : Raw.Literal("any of them")` | both arms are constants |
| `resource`, `reported`, `elsewhere`, `checkout` locals | the locals become `Raw`, composed through the seam | spec §10.2: rewrite the body, not the signature |

- [ ] **Step 4: Write `ServiceTextHandler`**

Create `src/Aspire.Hosting.ServiceSources/Messages/ServiceTextHandler.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Messages;

/// <summary>
/// The seam every reader-facing message is composed through. A hole is a <see cref="Name"/>, a
/// <see cref="Raw"/>, or one of the primitives enumerated here — anything else does not compile.
/// </summary>
/// <remarks>
/// There is deliberately no open generic <c>AppendFormatted&lt;T&gt;</c> and no <c>string</c>
/// overload: an un-enumerated hole must be refused, not escaped by default.
/// </remarks>
[InterpolatedStringHandler]
internal ref struct ServiceTextHandler
{
    private readonly StringBuilder builder;

    public ServiceTextHandler(int literalLength, int formattedCount) =>
        builder = new StringBuilder(literalLength + (formattedCount * 16));

    /// <summary>The message author's own words.</summary>
    public void AppendLiteral(string value) => builder.Append(value);

    /// <summary>Caller-controlled: escaped, then capped.</summary>
    public void AppendFormatted(Name value) => builder.Append(value.ToString());

    /// <summary>Already safe, and never capped — a path or a joined list must not be truncated.</summary>
    public void AppendFormatted(Raw value) => builder.Append(value.ToString());

    public void AppendFormatted(int value) => builder.Append(value);

    public void AppendFormatted(int? value) => builder.Append(value);

    /// <summary>
    /// Refused because <c>char</c> widens to <c>int</c>, which would otherwise accept '\n' silently.
    /// </summary>
    [Obsolete("A char hole is refused: wrap the value as Name or Raw.", error: true)]
    public void AppendFormatted(char value) => throw new NotSupportedException();

    internal string Text => builder.ToString();
}
```

- [ ] **Step 5: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS, all nine `ServiceTextHandlerTests` green.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Messages/Raw.cs \
        src/Aspire.Hosting.ServiceSources/Messages/ServiceTextHandler.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/ServiceTextHandlerTests.cs
git commit -m "Add the message seam: a handler whose holes must be Name or Raw"
```

---

## Task 3: `ServiceSourcesConfigurationException.For(...)`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs:13-20`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs` (created here, extended in Tasks 7–9)

**Interfaces:**
- Consumes: `ServiceTextHandler` (Task 2).
- Produces: `internal static ServiceSourcesConfigurationException For(ServiceTextHandler message)` and `internal static ServiceSourcesConfigurationException For(ServiceTextHandler message, Exception innerException)`.

The two `public` `string` constructors are **untouched** — consumers see no API change, and the ~139 unmigrated throws keep compiling (at a warning, once Task 6 lands).

- [ ] **Step 1: Write the failing test**

Create `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class MigratedSiteEscapingTests
{
    private const string Forgery = "orders'\nFATAL: everything is fine";

    [Fact]
    public void For_EscapesANameHole()
    {
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name(Forgery)}' failed.");

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("' failed.\nFATAL", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("Service 'orders\\'\\n", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_CarriesAnInnerException()
    {
        var inner = new InvalidOperationException("underneath");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}'.", inner);

        Assert.Same(inner, exception.InnerException);
        Assert.Equal("Service 'orders'.", exception.Message);
    }

    [Fact]
    public void For_AcceptsAMessageWithNoHoles() =>
        Assert.Equal("nothing interpolated here",
            ServiceSourcesConfigurationException.For($"nothing interpolated here").Message);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~MigratedSiteEscapingTests"`
Expected: FAIL — `For` does not exist (CS0117).

- [ ] **Step 3: Add the factory**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs`, add `using Aspire.Hosting.ServiceSources.Messages;` and insert after the second constructor:

```csharp
    // The factory is the one legitimate caller of the constructor Task 6 bans; the suppression is
    // scoped to these two lines so every other use still reports.
#pragma warning disable RS0030
    /// <summary>
    /// The only way this package builds a message: a raw <c>string</c> hole does not compile, so a
    /// caller-controlled name cannot reach a reader unescaped.
    /// </summary>
    internal static ServiceSourcesConfigurationException For(ServiceTextHandler message) =>
        new(message.Text);

    internal static ServiceSourcesConfigurationException For(ServiceTextHandler message, Exception innerException) =>
        new(message.Text, innerException);
#pragma warning restore RS0030
```

The `#pragma` is written now even though RS0030 does not exist until Task 6 — an unknown diagnostic id in a `#pragma` is silently ignored by the compiler, so it is inert until the analyzer lands and correct the moment it does. Without it, `For` would report itself as a banned-constructor use, inflating Task 6's worklist count by one per target framework and making the follow-up's escalation to `error` break the seam.

- [ ] **Step 4: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs
git commit -m "Add a handler-only For factory to ServiceSourcesConfigurationException"
```

---

## Task 4: Prove the raw path does not build

**Files:**
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Messages/RawPathDoesNotCompileTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj`
- Modify: `Directory.Packages.props`

**Interfaces:**
- Consumes: `For` (Task 3), `Name`/`Raw` (Tasks 1–2).
- Produces: nothing other tasks consume.

**This settles spec §12 open question 2.** The spec's fallback was "state the guarantee in the PR body and accept it is unguarded". That fallback is not needed: the property **is** testable in-repo, with no new project and no build that is expected to fail.

The mechanism: compile a snippet **in memory** with `Microsoft.CodeAnalysis.CSharp` and assert on the diagnostics. Three details make it work, all verified by probe:

- **Internals are visible** because `CSharpCompilation.Create`'s `assemblyName` is set to `"Aspire.Hosting.ServiceSources.Tests"`, which is what `AssemblyInfo.cs:3` grants `InternalsVisibleTo` to. There is no strong name, so the name alone is the key.
- **References** come from `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`, which at test runtime holds the running target framework's assemblies plus the package under test — so the test covers net8.0/net9.0/net10.0 intrinsically, with no reference-assembly package.
- **`GetDiagnostics()` only** — nothing is emitted or run.

**Positive controls are mandatory.** A negative-compilation test that only asserts "this fails" passes when the snippet fails for an unrelated reason (a typo, a missing `using`). Every negative case below is paired with positives compiled through the identical harness.

**Observed diagnostic ids** (from the probe, against a handler with the exact overload set of Task 2):

| Written as | Diagnostic |
|---|---|
| `For($"svc '{s}'")` — raw `string` hole | `CS1503` cannot convert from `string` to `Name` |
| `For($"a '{new Name(s)}' " + "plain tail")` — mixed chain | `CS1503` cannot convert from `string` to `ServiceTextHandler` |
| `For(someStringVariable)` | `CS1503` cannot convert from `string` to `ServiceTextHandler` |
| `For($"a{someObject}b")` | `CS1503` cannot convert from `object` to `Name` |
| `For($"a{someChar}b")` | `CS0619` `AppendFormatted(char)` is obsolete |
| `For($"{new Name(s),-10}")` — alignment | `CS1739` no parameter named `alignment` |

Note the spec predicted `CS0453` for the raw hole; that id arises only with an open generic `AppendFormatted<T>` constrained to `struct`. With the enumerated overload set it is `CS1503`. The test asserts **"at least one error, and the expected id is among them"**, so an overload-set change surfaces as a test failure to be re-read rather than a silent pass.

- [ ] **Step 1: Add the Roslyn package reference**

In `Directory.Packages.props`, inside the existing `<ItemGroup>` of test/sample versions, add in alphabetical position:

```xml
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
```

In `test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj`, add to the `PackageReference` `ItemGroup`:

```xml
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
```

- [ ] **Step 2: Write the failing test**

Create `test/Aspire.Hosting.ServiceSources.Tests/Messages/RawPathDoesNotCompileTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

/// <summary>
/// The ticket's deliverable is that the unescaped path stops compiling, so the proof has to be a
/// compilation rather than an assertion about output.
/// </summary>
public class RawPathDoesNotCompileTests
{
    private const string Preamble = """
        using System;
        using Aspire.Hosting.ServiceSources;
        using Aspire.Hosting.ServiceSources.Messages;

        internal static class Probe
        {
            internal static Exception M(string s, object o, char c) =>
        """;

    private static IReadOnlyList<Diagnostic> Compile(string expression)
    {
        var source = $"{Preamble}\n        {expression};\n}}";

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path));

        // The assembly name is what AssemblyInfo.cs grants InternalsVisibleTo, and it is the whole
        // reason an internal seam can be probed from here at all.
        var compilation = CSharpCompilation.Create(
            assemblyName: "Aspire.Hosting.ServiceSources.Tests",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static void AssertCompiles(string expression) =>
        Assert.Empty(Compile(expression));

    private static void AssertRefused(string expression, string expectedId)
    {
        var errors = Compile(expression);

        Assert.NotEmpty(errors);
        Assert.Contains(expectedId, errors.Select(d => d.Id));
    }

    // The controls: without these, every test below would also pass on a typo.

    [Fact]
    public void NameHole_Compiles() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"Service '{new Name(s)}' failed.")""");

    [Fact]
    public void RawHoleAndInt_Compile() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"{Raw.Literal("pre")} port {5}")""");

    [Fact]
    public void ConstantMessage_Compiles() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"no holes at all")""");

    [Fact]
    public void InterpolatedChain_Compiles() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"a '{new Name(s)}' " + $"and more")""");

    // The deliverable.

    [Fact]
    public void RawStringHole_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"Service '{s}' failed.")""", "CS1503");

    [Fact]
    public void MixedChainWithAPlainLiteralTail_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a '{new Name(s)}' " + "plain tail")""", "CS1503");

    [Fact]
    public void PreBuiltStringVariable_DoesNotCompile() =>
        AssertRefused("ServiceSourcesConfigurationException.For(s)", "CS1503");

    [Fact]
    public void ObjectHole_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{o}b")""", "CS1503");

    [Fact]
    public void CharHole_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{c}b")""", "CS0619");

    [Fact]
    public void AlignmentForm_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{new Name(s),-10}b")""", "CS1739");

    [Fact]
    public void FormatSpecifierForm_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{new Name(s):N0}b")""", "CS1739");

    // The trust surface. Raw is the only thing in this design that is believed rather than checked,
    // so every way of making one is pinned here — a new string-taking factory added later fails
    // NoStringTakingRawFactoryExists rather than passing quietly.

    [Fact]
    public void RawConstructor_IsNotAccessible() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{new Raw("x")}b")""", "CS1729");

    [Fact]
    public void RawLiteral_RefusesARuntimeValue() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Literal(s)}b")""", "CS9244");

    [Fact]
    public void RawJoin_RefusesStringFragments() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Join(", ", new[] { s })}b")""", "CS1503");

    [Fact]
    public void RawJoin_RefusesARuntimeSeparator() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Join(s, Array.Empty<Raw>())}b")""", "CS9244");

    [Fact]
    public void RawOrigin_RefusesAString() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Origin(s)}b")""", "CS1503");

    [Fact]
    public void NoStringTakingRawFactoryExists()
    {
        var factories = typeof(Raw)
            .GetMethods(System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(Raw));

        Assert.All(factories, factory =>
        {
            var stringParameters = factory.GetParameters().Where(p => p.ParameterType == typeof(string));

            // A string parameter is only allowed where the compiler forces it to be a constant.
            Assert.All(stringParameters, parameter =>
                Assert.Contains(parameter.GetCustomAttributes(inherit: false),
                    a => a.GetType().Name == "ConstantExpectedAttribute"));
        });
    }
}
```

> **On the pinned diagnostic ids.** `CS1739` for both the alignment and the format-specifier form was re-confirmed by probe after the first draft, because the spec had predicted a different id for the raw-hole case and was wrong (it named `CS0453`, which arises only with an open generic `AppendFormatted<T>` constrained to `struct`; the enumerated overload set gives `CS1503`). `CS1729` (no such constructor) and `CS9244` (`[ConstantExpected]` violation) are the remaining two and are **not** probe-confirmed — if either differs, `AssertRefused` fails loudly with the id it actually got, which is the intended behaviour: correct it to the observed id rather than loosening the assertion to "any error".

- [ ] **Step 3: Establish a real red — mandatory, not optional**

Tasks 1–3 already make every case here pass, so running the suite as-is is implement-then-assert, not TDD. The red has to be manufactured, and it is the only thing that proves these tests can fail at all:

1. Temporarily add `public void AppendFormatted(string value) => builder.Append(value);` to `ServiceTextHandler`.
2. Run: `dotnet test -c Release --filter "FullyQualifiedName~RawPathDoesNotCompileTests"`
3. Expected: `RawStringHole_DoesNotCompile`, `MixedChainWithAPlainLiteralTail_DoesNotCompile` and `PreBuiltStringVariable_DoesNotCompile` all **FAIL** with `Assert.NotEmpty() Failure`, while the four controls still pass. Record that output — it is the evidence the proof is live.
4. Remove the overload.
5. Re-run. Expected: all green.

A test that never failed is not a test. If step 3 shows the controls failing too, the harness is broken rather than the seam, and the references or the assembly name are where to look.

- [ ] **Step 4: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS on all three target frameworks.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props \
        test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/RawPathDoesNotCompileTests.cs
git commit -m "Prove by compilation that a raw name hole is refused"
```

---

## Task 5: `Describe` must not flatten an inner message verbatim

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs:52-65`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`

**Interfaces:** consumes nothing new; produces no new surface.

**Why in this PR and not the follow-up:** `ToString()` is what the runtime prints when an `AddService` call takes the AppHost down, so `Describe` is the package's most-read output. Its loop appends every inner exception's `Message` verbatim after `Environment.NewLine + "  caused by: "`. A newline in a libgit2 message, a `KubernetesSecretException` or an `IOException` path forges a line inside that blob — including a convincing fake `  caused by:` line. No sink fix reaches it, because those messages are not built through this package's seam at all.

The fix **bounds** rather than eliminates: it neutralises line breaks so a cause cannot forge a line. It does not escape quotes or cap length — a cause's message is not a name, and capping it would discard the diagnosis that is the whole reason the line exists. The secondary exception types migrating is the elimination, and belongs to the follow-up.

- [ ] **Step 1: Write the failing test**

Append to `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`:

```csharp
    [Fact]
    public void Describe_CannotForgeACausedByLineFromAnInnerMessage()
    {
        // Environment.NewLine, not "\n": Describe writes the platform separator, so a payload hard-coding
        // "\n" would already be harmless on Windows and the test would pass before the fix.
        var forged = $"authentication failed{Environment.NewLine}  caused by: nothing is wrong, carry on";
        var exception = ServiceSourcesConfigurationException.For(
            $"Service '{new Name("orders")}' failed.", new InvalidOperationException(forged));

        var described = exception.Describe(fullDetail: false);

        // The forged text may survive as characters; what it must not do is start a line. Splitting on
        // the separator + prefix counts real cause lines only, so exactly one wrapped cause means two parts.
        Assert.Equal(2, described.Split(Environment.NewLine + "  caused by: ").Length);
        Assert.Contains("\\n  caused by: nothing is wrong", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_KeepsTheCausesWordingIntactApartFromLineBreaks()
    {
        var inner = new InvalidOperationException("could not read 'origin/main'");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}' failed.", inner);

        Assert.Contains("  caused by: could not read 'origin/main'",
            exception.Describe(fullDetail: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DoesNotCapALongCause()
    {
        var inner = new InvalidOperationException(new string('x', 400));
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}' failed.", inner);

        Assert.Contains(new string('x', 400), exception.Describe(fullDetail: false), StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Describe_CannotForge"`
Expected: FAIL — `Assert.Equal(2, ...)` gets 3, because the inner message's own `  caused by: ` splits too.

- [ ] **Step 3: Neutralise line breaks in the flattened cause**

In `Describe`, change the append to run the cause's message through a line-break neutraliser. The dedupe comparison stays on the **raw** `cause.Message` so the adjacent-repeat collapse is unaffected:

```csharp
            if (cause.Message != previous)
            {
                description.Append(Environment.NewLine).Append("  caused by: ").Append(SingleLine(cause.Message));
                previous = cause.Message;
                wroteACause = true;
            }
```

Add beside `Describe`:

```csharp
    /// <summary>
    /// A cause's own wording cannot forge a line of this summary. Not escaped or capped further: a
    /// cause is a diagnosis, not a name, and truncating it would discard what the line is for.
    /// </summary>
    private static string SingleLine(string message) =>
        message.Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
```

- [ ] **Step 4: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS. Any existing `Describe` test asserting a multi-line inner message is **updated** to the escaped expectation, not relaxed.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs
git commit -m "Stop an inner exception message forging a line in Describe"
```

---

## Task 6: Ban the raw constructor, at warning severity

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt`
- Modify: `src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj`
- Modify: `src/Directory.Packages.props`

**Interfaces:** no code surface.

**The constraint that shapes this task, verified by probe:** `dotnet build -warnaserror` **promotes RS0030 to a hard error** — observed as `error RS0030 ... Build FAILED`. CI runs exactly that command (`ci.yml:78`). With ~139 throws still on the raw constructor, an uncarved ban turns CI red on the day this lands. Adding `<WarningsNotAsErrors>$(WarningsNotAsErrors);RS0030</WarningsNotAsErrors>` was then observed to produce `warning RS0030 ... Build succeeded. 1 Warning(s) 0 Error(s)` under the same command.

So the gate lands **visible and non-fatal**, and the follow-up's step to `error` is the deletion of one line rather than new machinery. There is no `.editorconfig` in this repo and this task does not add one: RS0030's default severity is already `warning`, so the only thing needed is the carve-out.

- [ ] **Step 1: Add the package version**

In `src/Directory.Packages.props`, add a `PackageVersion` in alphabetical position within the existing item group:

```xml
    <PackageVersion Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="4.14.0" />
```

- [ ] **Step 2: Write the banned-symbols file**

Create `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt`:

```
M:Aspire.Hosting.ServiceSources.ServiceSourcesConfigurationException.#ctor(System.String);compose the message through ServiceSourcesConfigurationException.For($"...") so caller-controlled names cannot reach a reader unescaped
M:Aspire.Hosting.ServiceSources.ServiceSourcesConfigurationException.#ctor(System.String,System.Exception);compose the message through ServiceSourcesConfigurationException.For($"...", inner) so caller-controlled names cannot reach a reader unescaped
```

- [ ] **Step 3: Wire it into the project**

In `src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj`, add to the first `PropertyGroup`:

```xml
    <!-- The ban is a worklist, not a gate, until every throw is migrated: CI builds -warnaserror,
         which would otherwise make each unmigrated site fail the build. -->
    <WarningsNotAsErrors>$(WarningsNotAsErrors);RS0030</WarningsNotAsErrors>
```

and a new `ItemGroup`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" PrivateAssets="all" />
    <AdditionalFiles Include="BannedSymbols.txt" />
  </ItemGroup>
```

- [ ] **Step 4: Verify the ban fires and the build still passes**

The count must be **per site, deduplicated across target frameworks** — the build compiles net8.0, net9.0 and net10.0, so a raw `grep -c` triples it. This exact command is the one used in Task 11 and quoted in the PR body; do not use a different one anywhere:

```bash
dotnet restore
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 \
  | grep -oE "[^ ]+\.cs\([0-9]+,[0-9]+\): warning RS0030" | sort -u | wc -l
```

Expected: a per-site count in the low hundreds, and the build **succeeds**.

Run: `dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | tail -5`
Expected: `Build succeeded.` with `0 Error(s)`.

Record that number in the PR body — it is the follow-up's worklist size.

- [ ] **Step 5: Confirm the gate is real, and that the seam is not in the worklist**

Two separate things to establish, because one failure mode looks like the other.

First, that `For` is not itself reported — if the `#pragma` in Task 3 were missing or misplaced, the seam would appear in its own worklist:

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | grep "RS0030" | grep -c "ServiceSourcesConfigurationException.cs"
```

Expected: `0`.

Second, that escalation is real. Temporarily delete the `WarningsNotAsErrors` line and re-run the build:

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | grep "error RS0030" | grep -c "ServiceSourcesConfigurationException.cs"
```

Expected: `0` — every `error RS0030` comes from an unmigrated call site, **none** from the factory. Confirm the build FAILS overall, then restore the line.

That second grep is what makes this evidence rather than noise: without it, a failure caused by the seam suppressing nothing is indistinguishable from the expected failure caused by the ~139 unmigrated sites, and the follow-up would inherit a broken escalation believing it was verified.

- [ ] **Step 6: Run the verify legs and commit**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`

```bash
git add src/Directory.Packages.props \
        src/Aspire.Hosting.ServiceSources/BannedSymbols.txt \
        src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj
git commit -m "Ban the raw exception constructor at warning severity"
```

---

## Task 7: The naming trap

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/PreparePlan.cs:121`, `:128`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs:285`, `:444`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs:831-832`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs:60-61`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`

**Interfaces:**
- Consumes: `Name`, `Raw` (Tasks 1–2).
- Produces: `internal static string ServiceLabel(string serviceName)` and `internal static string RepositoryLabel(string checkoutName)` on `PreparePlan` — **same return type, reduced accessibility**.

**The trap is three incompatible spellings plus one that only looks like escaping.** Each gets a decision here, and each decision is recorded so silence does not imply one:

| Spelling | Sites | Decision |
|---|---|---|
| `ServiceSourcesWarnings.Label` | 12 | **Subsumed.** Task 1 reimplemented it over `Name`; one rule now, two names for it. Its 12 call sites are not migrated in this PR, so they cannot double-escape — nothing turns a `Label(...)` result into a `Name` hole. Deleting the helper and the alias at `EndpointMutationDetector.cs:303` belongs to the follow-up that migrates those messages. |
| `ConfiguredValue.Bare` / `.Escaped` | 52 | **Left alone, deliberately.** A different contract — how a *configured value* is echoed back — and `Name` is already defined in terms of `Bare`. Step 4 checks the one interaction that would be a bug. |
| `.Replace("'", "''")` at `KubernetesBackingServiceSource.cs:473` | 1 | **Left alone, and now justified rather than merely named.** Read at plan time: it is `QuotedConnectionString(string shown)`, used once, at `:1355`, to render a **connection string** — not a name — and its XML remarks give the reason (`DbConnectionStringBuilder`'s own convention for a quote inside a quoted value). Spec §9.4 made the decision conditional on "is it building a message rather than a selector"; the honest answer is "a message, but about a connection-string value, under that value's own convention". It stays. Say so in the PR body so the third spelling is a recorded choice. |
| `PreparePlan.ServiceLabel` / `RepositoryLabel` | 4 src callers, 11 test references | **Fixed here.** Escapes and caps, and becomes `internal`. |

**Why `internal` is safe — and why it is a no-op for the public surface** (the human asked for this checked before planning it):

- **`PreparePlan` is itself `internal`**: `internal sealed record PreparePlan(...)` at `Prepare/PreparePlan.cs:34`. Both helpers are `public static` members of an internal type, so **no consumer has ever been able to reach either one**. This is the finding that makes Task 10's CHANGELOG entry an open question rather than a foregone `### Breaking`.
- `PreparePlan` carries **no `[AspireExport]` attribute**, so it is not on the TypeScript export surface the `ASPIREEXPORT*` analyzers gate.
- Call sites are **4 locations in `src/`** — `LocalGitCheckout.cs:285`, `:444`; `DeferredCheckout.cs:831-832`; `LocalProjectSource.cs:60-61` — none of which need an edit, since the return type is unchanged and all are in the same assembly.
- A further **4 XML-doc `<see cref>` references** exist (`PreparePlan.cs:94`, `:96`; `CheckoutPreparation.cs:76-77`; `RepositoryDefinition.cs:27`). A `<see cref>` to an internal member from inside the same assembly resolves fine, so these need no edit either — but do not mistake them for call sites when counting.
- `test/` holds **11 references** across three files; the test project reaches internals through `InternalsVisibleTo` (`AssemblyInfo.cs:3`), so all 11 keep compiling unchanged.
- No `samples/` hit, no `.ts` hit, no `.json` hit.

**Why they keep returning `string`:** returning `internal Raw` from a method is fine once the method is itself `internal`, but every one of the four callers embeds the result in a further `string`-composed message that this PR does not migrate. Returning `Raw` would force those four messages into the seam too, which is the bulk migration this PR is explicitly split away from. The body composes through the seam and calls `.ToString()`; the follow-up changes the return type when it migrates the callers.

- [ ] **Step 1: Write the failing tests**

Append to `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`:

```csharp
    // Fully qualified deliberately: the test assembly already has an
    // Aspire.Hosting.ServiceSources.Tests.Prepare namespace, so the simple name `Prepare` binds
    // there and lookup stops — `Prepare.PreparePlan` is CS0234, not the production type.
    private static string ServiceLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.ServiceLabel(name);

    private static string RepositoryLabel(string name) =>
        Aspire.Hosting.ServiceSources.Prepare.PreparePlan.RepositoryLabel(name);

    [Fact]
    public void ServiceLabel_EscapesTheName() =>
        Assert.Equal("Service 'ord\\'ers\\nFATAL'", ServiceLabel("ord'ers\nFATAL"));

    [Fact]
    public void ServiceLabel_CapsTheName() =>
        Assert.Equal($"Service '{new string('a', Name.MaxLength)}…'", ServiceLabel(new string('a', 200)));

    [Fact]
    public void RepositoryLabel_EscapesAndCaps()
    {
        Assert.Equal("Repository 'mono\\'repo'", RepositoryLabel("mono'repo"));
        Assert.EndsWith("…'", RepositoryLabel(new string('b', 200)), StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceLabel_EscapesExactlyOnce()
    {
        // The four callers embed this result in a further message that this PR does not migrate, so
        // the guard is that one pass has already happened — not that a second pass is safe. There is
        // deliberately no way to feed a string back into the seam, which is why this asserts directly.
        Assert.Equal("Service 'ord\\'ers'", ServiceLabel("ord'ers"));
        Assert.DoesNotContain("\\\\'", ServiceLabel("ord'ers"), StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ServiceLabel|FullyQualifiedName~RepositoryLabel"`
Expected: FAIL — the labels interpolate raw, so `ServiceLabel_EscapesTheName` gets `Service 'ord'ers\nFATAL'` with a live newline.

- [ ] **Step 3: Compose the labels through the seam**

In `src/Aspire.Hosting.ServiceSources/Prepare/PreparePlan.cs`, add `using Aspire.Hosting.ServiceSources.Messages;` and replace `:121` and `:128`. Keep the existing XML docs above each; only the signature line and body change.

```csharp
    internal static string ServiceLabel(string serviceName) =>
        Raw.Compose($"Service '{new Name(serviceName)}'").ToString();
```

```csharp
    internal static string RepositoryLabel(string checkoutName) =>
        Raw.Compose($"Repository '{new Name(checkoutName)}'").ToString();
```

- [ ] **Step 4: Check the one `ConfiguredValue` interaction**

Run: `grep -rnE "new Name\(.*(ConfiguredValue\.(Bare|Escaped)|Label)\(" src/`
Expected: **no output**. A `Bare(...)` or `Label(...)` result reaching a `Name` hole would escape twice. If this ever returns a line, unwrap the inner call — do not relax `Name`.

- [ ] **Step 5: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS. The four `src/` callers need no edit — the return type is unchanged and they are all inside the same assembly. Existing tests in `PreparePlanTests.cs`, `CheckoutPreparationTests.cs` and `ServiceDefinitionBuilderTests.cs` that pass a plain name still pass, because a plain name escapes to itself. `CheckoutPreparationTests.cs:1117` passes `"../../evil"`, which also escapes to itself.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Prepare/PreparePlan.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs
git commit -m "Escape and cap the prepare labels, and make them internal"
```

---

## Task 8: Migrate the nine enumerated sink-A sites

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs:342-357` (sites 6–8)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs:54-67`, `:83-86` (sites 9–12)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/EndpointScheme.cs:63-65` (site 13)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs:477-481` (site 14)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`

**Interfaces:**
- Consumes: `For` (Task 3), `Name`, `Raw` (Tasks 1–2).
- Produces: no new surface. Every edit is a call-shape change at a `throw`.

**Locate `LocalProjectSource.cs:478` by its text, not its line number** — PR #376 lands first and moves it. The message begins `"Service '{serviceName}': 'project' is required for a 'local' service of kind 'dotnet'."`.

**The four hole kinds in this set, and what each becomes:**

| Hole today | Becomes | Why |
|---|---|---|
| `{serviceName}`, `{configured}`, `{source}` | `{new Name(...)}` | caller-controlled: a catalog key, a developer-written scheme or source value |
| `{definition.Origin.Describe()}` | `{Raw.Origin(definition.Origin)}` | package-composed, carries its own quotes, and must not be capped |
| `{Redacted(rawUrl)}` | `{Redacted(rawUrl)}`, with `Redacted` now returning `Raw` | already redacted; a URL must not be capped at 64 |
| `{Http}`, `{Https}` | `{Raw.Literal(Http)}` | `const` |
| `{remotePort}` | unchanged | binds to `AppendFormatted(int)` / `(int?)` |

Every factory this needs already exists from Task 2. **This task adds no new way to make a `Raw`** — see Task 2 for why a `string`-taking hatch is refused.

- [ ] **Step 0: Make `Redacted` return `Raw`**

In `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs:325`, change the signature and compose the body through the seam. It is `private static` and used only at `:350` and `:356`, both migrated in Step 3, so nothing else moves:

```csharp
    private static Raw Redacted(string url) => Raw.Compose($"…");
```

Keep the existing body's logic; wrap whatever it returns today in `Raw.Compose($"{…}")`, with any caller-controlled fragment inside it as a `Name` hole and the rest as literal segments. Read the current body before rewriting it — it is a redaction routine, and its rules are not this ticket's to change.

*(Every `Raw` factory this task needs already exists from Task 2. This task adds no new way to make a `Raw`.)*

- [ ] **Step 1: Write the failing tests**

Append to `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`. These drive the real resolve paths rather than asserting on a composed string, so they prove the *site* is migrated, not just that the seam works:

```csharp
    // Each case carries its own expected rendering. Asserting only "no newline" would pass on the
    // UNMIGRATED code for every input that contains no newline, which is two of these three.
    [Theory]
    [InlineData("orders'\nFATAL: resolved fine", "orders\\'\\nFATAL: resolved fine")]
    [InlineData("orders\"quoted", "orders\\\"quoted")]
    [InlineData("orders\\", "orders\\\\")]
    public void UrlSource_MissingUrl_EscapesTheServiceName(string serviceName, string expected)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => UrlSourceMissingUrlThrow(serviceName));

        Assert.Contains($"Service '{expected}'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
    }

    // A guard, not a red test: this passes on the unmigrated code too. It exists to catch a
    // migration that reached for a Name hole here and silently truncated the path.
    [Fact]
    public void UrlSource_MissingUrl_DoesNotTruncateTheOrigin()
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => UrlSourceMissingUrlThrow("orders", yamlPath: new string('p', 200)));

        Assert.Contains(new string('p', 200), exception.Message, StringComparison.Ordinal);
    }
```

**The entry point under test, per site** — named here rather than left to the implementer, because the likely mistake is asserting on the seam instead of on the site, which is exactly what these tests exist to rule out:

| Site | Call | Trigger |
|---|---|---|
| `UrlSource.cs:343`, `:350`, `:356` | `UrlSource.Resolve(...)` — the method containing `rawUrl` at `:338` | a `ServiceDefinition` with `Url` null / `"not a url"` / `"ftp://h"` |
| `KubernetesSource.cs:55`, `:60`, `:66` | `KubernetesSource.Resolve(...)` — the method at `:50` | `Kubernetes.Context` null / `Port` null / `Port = 0` |
| `KubernetesSource.cs:84` | same, via `RequireKubernetesBlock` | `definition.Kubernetes` null |
| `LocalProjectSource.cs:478` | the method containing the `project` check | a `local`/`dotnet` definition with `project` blank |

Read the exact signatures before writing the helpers — they are `internal`, reachable through `InternalsVisibleTo`. Build the `ServiceDefinition` with the builder the neighbouring tests in `test/Aspire.Hosting.ServiceSources.Tests/Sources/` already use (`ServiceDefinitionBuilderTests.cs` shows the shape); do not invent a second fixture. `yamlPath` is the `CatalogOrigin`'s `YamlPath`, which is what `Describe()` renders.

Write the matching escaping theory for `KubernetesSource` (missing `kubernetes.context`) and `LocalProjectSource` (missing `project`) using the same `[InlineData]` triple.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~MigratedSiteEscapingTests"`
Expected: the forgery theories FAIL — the raw `{serviceName}` hole emits a live newline.

- [ ] **Step 3: Migrate the three `UrlSource` throws**

```csharp
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}' source is 'url' but no URL is configured — set " +
                $"'url.url' in servicesources.local.json or in {Raw.Origin(definition.Origin)}.");
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'url' value '{Redacted(rawUrl)}' is not a valid absolute URL.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'url' value '{Redacted(rawUrl)}' must use the http or https scheme.");
        }
```

Add `using Aspire.Hosting.ServiceSources.Messages;` to the file.

- [ ] **Step 4: Migrate the four `KubernetesSource` throws**

```csharp
        if (string.IsNullOrWhiteSpace(config.Kubernetes.Context))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': source 'kubernetes' requires 'kubernetes.context' in " +
                $"servicesources.local.json.");
        }

        remotePort = config.Kubernetes.Port ?? kubernetes.Port ?? throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}': no port configured for source 'kubernetes' — set " +
            $"'kubernetes.port' in servicesources.local.json or in {Raw.Origin(definition.Origin)}.");

        if (remotePort is < 1 or > 65535)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': port value '{remotePort}' is not a valid port (must be between 1 and 65535).");
        }
```

Note two shape changes:
- The `??  throw new ...` at `:59` becomes `?? throw ServiceSourcesConfigurationException.For(...)` — **keep the `throw` keyword**; the line above drops it only because the factory returns rather than throws. Write it as `?? throw ServiceSourcesConfigurationException.For(...)`.
- The plain-literal tail `"servicesources.local.json."` gains a `$` prefix. Without it the chain is `CS1503` — which is the seam doing its job (spec §10.4).

And in `RequireKubernetesBlock`:

```csharp
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}' source is 'kubernetes' but {Raw.Origin(definition.Origin)} has no " +
                $"kubernetes.service entry.");
```

- [ ] **Step 5: Migrate `EndpointScheme` and `LocalProjectSource`**

`EndpointScheme.cs:63` — `configured`, `source` and `origin` are all package- or catalog-composed, but `configured` is a **developer-written scheme value**, so it is caller-controlled and takes a `Name`:

```csharp
        throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}': scheme '{new Name(configured)}' is not supported for source '{new Name(source)}' — " +
            $"use '{Raw.Literal(Http)}' or '{Raw.Literal(Https)}'. Set the {new Name(source)}.scheme entry in {origin}.");
```

`LocalProjectSource` — only the first segment has a hole; the three continuation segments each gain a `$`:

```csharp
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'project' is required for a 'local' service of kind 'dotnet'. It names "
                + $"the project file to run, relative to the service's checkout — for example "
                + $"'src/Orders.Api/Orders.Api.csproj'. It belongs on the service's 'servicesources.yaml' entry "
                + $"beside 'repository'; 'servicesources.local.json' chooses the source and carries no 'project'.");
```

- [ ] **Step 6: Confirm no new `Raw` factory was added**

Run: `grep -rnE "static Raw [A-Z][A-Za-z]*\(" src/Aspire.Hosting.ServiceSources/Messages/Raw.cs`
Expected: exactly the four factories from Task 2 — `Compose`, `Literal`, `Join`, `Origin`. None of them can be handed a bare `string`, so there is no identifier allowlist to maintain and nothing for this step to miss.

- [ ] **Step 7: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS. The deduped RS0030 count from Task 6 Step 4 drops by 9 — one per migrated call site, not per target framework. Re-run that exact command to confirm.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Messages/Raw.cs \
        src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs \
        src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs \
        src/Aspire.Hosting.ServiceSources/Sources/EndpointScheme.cs \
        src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs
git commit -m "Migrate the nine enumerated resolve refusals to the seam"
```

---

## Task 9: Migrate the five composers

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceStartupFailureNotices.cs:471-514` (`FailureMessage`, site 1)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceConfigAudit.cs:128-144` (`OrphanedEntriesReason`, site 2)
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServices/BackingServiceConfigAudit.cs:173-192` (`OrphanedEntriesReason`, site 3)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs:555+` (`LaunchProfileEndpointWarning`, site 4)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs:357+` (`FailedCheckoutMessage`, site 5)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`

**Interfaces:**
- Consumes: `Name`, `Raw` (Tasks 1–2).
- Produces: no signature changes. Every composer keeps its current return type.

**Why the signatures do not change.** These five are `string`-returning helpers whose result is handed to `ILogger` as a **structured argument** — all 15 `logger.Log*` calls in this package use a structured template (`logger.LogWarning("{Warning}", warning)`), so nothing done to a sink's constructor reaches them. One of the five, `DeferredCheckout.LaunchProfileEndpointWarning` (`:555`), is `public static string?`; returning `internal Raw` from it would be CS0050. Spec §10.2 is the rule here: **rewrite the body, not the signature.** Each composer becomes `Raw.Compose($"...").ToString()`.

This leaves the composers' `string` return as a laundering point — sink B is not closed by this PR, and the PR body must say so rather than implying the log path is now structurally safe.

**`LocalCheckoutPrefetch.cs` and `DeferredCheckout.cs` move under PR #376.** Locate `FailedCheckoutMessage` and `LaunchProfileEndpointWarning` by name.

**The audit sites need a third shape** (spec §10.3). Sites 2 and 3 quote each name *inside a `Select` lambda* and hand the sink one joined hole. Neither wrapping the joined hole as a `Name` (which caps the whole list at 64) nor as a `Raw` (which launders every name inside it) is correct. **The lambda itself composes.**

- [ ] **Step 1: Write the failing tests**

Append to `test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs`:

```csharp
    [Fact]
    public void OrphanedEntries_EscapeEachNameInTheList()
    {
        var reason = ServiceConfigAuditOrphanReason(
            orphans: ["ord'ers\nFATAL: fine", "payments"],
            catalogNames: ["catalog"]);

        // The escaped orphan must be PRESENT, not merely newline-free: an implementation that
        // dropped the offending name entirely would satisfy a DoesNotContain-only assertion.
        Assert.Contains("'ord\\'ers\\nFATAL: fine'", reason, StringComparison.Ordinal);
        Assert.Contains("'payments'", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OrphanedEntries_DoNotCapTheJoinedList()
    {
        // Ten names of ten characters joined is well over the 64-character name cap; capping the
        // list rather than each name would hide most of what the developer has to fix.
        string[] orphans = [.. Enumerable.Range(0, 10).Select(i => $"service-{i:00}")];

        var reason = ServiceConfigAuditOrphanReason(orphans, catalogNames: ["catalog"]);

        Assert.All(orphans, name => Assert.Contains($"'{name}'", reason, StringComparison.Ordinal));
    }

    [Fact]
    public void FailureMessage_EscapesTheServiceName()
    {
        var message = StartupFailureMessage(serviceName: "ord'ers\nFATAL: running fine");

        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("FATAL: running fine", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureMessage_DoesNotTruncateTheSurroundingAdvice()
    {
        var message = StartupFailureMessage(serviceName: "orders");

        // The advice is the package's own prose; only the name is capped.
        Assert.Contains("This console does not carry", message, StringComparison.Ordinal);
    }
```

**The entry point under test, per composer.** All five composers are `private static` except `LaunchProfileEndpointWarning`, so four of them are reached through the `internal` method that produces the message rather than directly:

| Composer | Reach it through | Trigger |
|---|---|---|
| `ServiceConfigAudit.OrphanedEntriesReason` (`:128`) | `ServiceConfigAudit.ReportNow(...)` with a capturing logger | a developer-config `services` key naming no catalog service |
| `BackingServiceConfigAudit.OrphanedEntriesReason` (`:173`) | same shape on `BackingServiceConfigAudit` | a `backingServices` key no `AddBackingService()` names |
| `ServiceStartupFailureNotices.FailureMessage` (`:471`) | `ServiceStartupFailureNotices`' resource-state handler, which calls it at `:411` | a resource reporting a failed state |
| `DeferredCheckout.LaunchProfileEndpointWarning` (`:555`) | **directly** — it is `public static` | a launch profile declaring an endpoint Aspire does not manage |
| `LocalCheckoutPrefetch.FailedCheckoutMessage` (`:357`) | the prefetch completion path that calls it at `:237` and `:342` | a checkout task whose result carries an exception |

Capture the logger output with a test `ILogger` that records `{Warning}`/`{ServiceSourcesNotice}` arguments — the existing tests for these files already do this; reuse their fake rather than adding one. Assert on the captured message text, **not** on a string you composed through the seam yourself: a test that builds its own expected value through `Raw.Compose` proves the seam works and says nothing about whether the site was migrated.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~OrphanedEntries|FullyQualifiedName~FailureMessage"`
Expected: FAIL — live newlines in both.

- [ ] **Step 3: Migrate the two audit composers**

`Config/ServiceConfigAudit.cs` — the lambda composes, the join is then `Raw`:

```csharp
        var described = orphans.Select(orphan =>
        {
            var closest = NearMiss.Nearest(orphan, candidates, spelling: name => name).FirstOrDefault();

            // Composed per name, not over the joined list: capping the list would hide most of it.
            return closest is null
                ? Raw.Compose($"'{new Name(orphan)}'")
                : Raw.Compose($"'{new Name(orphan)}' (did you mean '{new Name(closest)}'?)");
        });

        var declared = candidates.Select(name => Raw.Compose($"'{new Name(name)}'"));

        return Raw.Compose(
            $"Service configuration that nothing read: {Raw.Join(", ", described)}. No service in "
            + $"this AppHost's catalog is named {(orphans.Count == 1 ? Raw.Literal("it") : Raw.Literal("any of them"))}, so "
            + $"{(orphans.Count == 1 ? Raw.Literal("the entry configures") : Raw.Literal("the entries configure"))} nothing. This AppHost's "
            + $"catalog declares: {Raw.Join(", ", declared)}. Correct the key "
            + $"under \"{Raw.Literal(DeveloperConfigFileSource.FileServicesKey)}\" in '{Raw.Literal(DeveloperConfiguration.FileName)}', or "
            + $"wherever a higher layer set it, or remove the entry if it is deliberately unused.").ToString();
```

Apply the identical shape to `BackingServices/BackingServiceConfigAudit.cs:173-192`, with its own wording and `FileBackingServicesKey`.

`described` and `declared` are now `IEnumerable<Raw>` rather than `IEnumerable<string>`, which is what lets `Raw.Join` take them. `FileServicesKey`, `FileBackingServicesKey` and `FileName` were all confirmed `const` at plan time (`DeveloperConfigFileSource.cs:34`, `:43`; `DeveloperConfiguration.cs:26`), so `Raw.Literal` accepts them.

- [ ] **Step 4: Migrate the three remaining composers**

`ServiceStartupFailureNotices.FailureMessage` — its four locals (`resource`, `reported`, `elsewhere`, `checkout`) are package-composed except where they embed `resourceModel.Name`, `resourceId` or `state`, which are caller-influenced:

```csharp
        return Raw.Compose(
            $"Service '{new Name(serviceName)}' is configured as '{new Name(source)}' and {resource} is not running: it "
            + $"reported {reported}. This console does not carry that resource's output, so nothing here "
            + $"says why — {elsewhere}.{checkout}").ToString();
```

**All four locals change type from `string` to `Raw`**, which is what lets them be holes at all — `resource`, `reported`, `elsewhere` and `checkout`. Each is built from literals and, where it embeds a name, a `Name` hole:

```csharp
        var elsewhere = dashboard
            ? Raw.Literal("its own console in the Aspire dashboard does, at the dashboard URL logged above")
            : Raw.Literal("that resource's own logs do, wherever this host surfaces them — this run has no dashboard");

        var checkout = string.Equals(source, "local", StringComparison.Ordinal)
            ? Raw.Literal(" A 'local' service runs from a checkout rather than from a project added to this "
                + "AppHost, and the build of that checkout writes to those same logs — so a failure "
                + "to compile is reported nowhere else at all.")
            : Raw.Literal("");

        var reported = exitCode is { } code
            ? Raw.Compose($"'{new Name(state)}' with exit code {code}")
            : Raw.Compose($"'{new Name(state)}'");
```

The `checkout` ternary's two arms are both constants, so `Raw.Literal` takes them; string concatenation of literals is still a constant expression, so the multi-line arm is fine. And the two `resource` assignments that embed names become composed too:

```csharp
            : Raw.Compose($"its resource '{new Name(resourceModel.Name)}'").ToString();
```

```csharp
            resource = Raw.Compose($"{resource} replica '{new Name(resourceId)}'");
```

Apply the same treatment to `DeferredCheckout.LaunchProfileEndpointWarning` and `LocalCheckoutPrefetch.FailedCheckoutMessage`: every hole carrying a service name, an endpoint name or a launch-profile name becomes `new Name(...)`; every hole carrying package prose becomes a `Raw` local composed through the seam or a `Raw.Literal` constant; every plain-literal continuation segment gains a `$`.

**`FailedCheckoutMessage` embeds `exception.Message`**, which is neither a name nor package prose — it is a third party's wording, and Task 5 already established the rule for it: neutralise its line breaks, do not cap it. Reuse that by composing it as `Raw.Compose($"{new Name(...)}")`? **No** — that would cap it. Instead extract Task 5's `SingleLine` helper into `Messages/Raw.cs` as `internal static Raw Cause(Exception exception)`, returning `new(SingleLine(exception.Message))`, and have both `Describe` and this composer use it. A typed factory over `Exception`, so no `string` can be passed to it.

- [ ] **Step 5: Confirm no new way to make a `Raw` was added**

Run: `grep -rnE "static Raw [A-Z][A-Za-z]*\(" src/Aspire.Hosting.ServiceSources/Messages/Raw.cs`
Expected: exactly `Compose`, `Literal`, `Join`, `Origin` and `Cause` — the five factories Tasks 2 and 9 sanction, every one of them taking a handler, a `[ConstantExpected]` string, already-`Raw` parts, or a domain type. Any other line is a new trust hatch and must be justified or removed. `NoStringTakingRawFactoryExists` (Task 4) enforces this at test time; this grep is the reviewer's version of the same question.

- [ ] **Step 6: Run the verify legs**

Run: `dotnet build -c Release --no-restore -warnaserror && dotnet test -c Release --no-build`
Expected: PASS. Existing tests asserting these messages' exact text still pass for plain names; any that used a name containing `'`, `"`, `\` or a newline is **updated to the escaped expectation, never relaxed** — those 384 `test/` references to the exception type are the net that catches a message whose shape changed.

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceStartupFailureNotices.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceConfigAudit.cs \
        src/Aspire.Hosting.ServiceSources/BackingServices/BackingServiceConfigAudit.cs \
        src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs \
        src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Messages/MigratedSiteEscapingTests.cs
git commit -m "Compose the five log-reaching message builders through the seam"
```

---

## Task 10: CHANGELOG

**Files:**
- Modify: `CHANGELOG.md` — heading pending a human decision (see below); `### Breaking` and `### Fixed` both already exist under `## [Unreleased]`

**Interfaces:** none.

**`[#375]` already has a link definition at `CHANGELOG.md:1734`**, added when #372's `### Fixed` entry referenced it at `:206`. Do **not** add a second — a duplicate definition is a Markdown error and the link block is sorted.

> **BLOCKED — do not write a `### Breaking` entry without a decision from the human.** The instruction to add one assumed `ServiceLabel`/`RepositoryLabel` were publicly reachable. They are not: `PreparePlan` is declared `internal sealed record PreparePlan(...)` at `src/Aspire.Hosting.ServiceSources/Prepare/PreparePlan.cs:34`, so both are `public static` members of an **internal** type and no consumer has ever been able to call either. Making them `internal` changes nothing a consumer can observe.
>
> A `### Breaking` entry would therefore tell readers to migrate code they could not have written, which is worse than no entry — and the file's own preamble reserves that heading for what actually breaks for a consumer, with `### Fixed` likewise reserved for already-*released* behaviour.
>
> This is a question about what to publish, not a code question, so it goes to the human rather than being decided here. It is carried in the plan's `open_questions`.

- [ ] **Step 1: Add the entry the human chooses**

**Default, if no answer arrives: no CHANGELOG entry at all.** Task 7 is then an internal-only refactor, which the repo's convention does not document — matching how it treats renames and other consumer-invisible changes.

**If the human wants the escaping recorded anyway**, the honest heading is `### Fixed` and the honest subject is the *behaviour*, not the accessibility. Only add this if the last release tag predates the messages it describes — check `git describe --tags --abbrev=0` first, because a bug introduced and fixed inside the same `[Unreleased]` cycle gets no entry:

```markdown
- **A service or repository name can no longer forge a line in a prepare or checkout message**
  ([#375]). The phrase these messages use to name what failed — `Service 'orders'`,
  `Repository 'monorepo'` — interpolated the name exactly as configured, so a name carrying a
  newline or a closing quote could end the line and write a sentence of its own into output a
  reader trusts. The name is now escaped and capped the same way this package already treats
  developer-written text echoed back.
```

Do **not** describe the accessibility change in either case unless the human asks for it.

- [ ] **Step 2: Verify the link definition is not duplicated**

Run: `grep -c '^\[#375\]:' CHANGELOG.md`
Expected: `1`.

- [ ] **Step 3: Verify no artefact says "five named sites"**

Run: `git log origin/main..HEAD --format=%B | grep -in "five .*site"` and `grep -in "five .*site" CHANGELOG.md`

Expected: no output. The enumerated set is 14. Do **not** include the plan or the spec in this grep — both discuss the prohibition by name and would match themselves.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "Record the prepare-label accessibility change"
```

---

## Task 11: Final verification and the follow-up's scope

**Files:** none modified. This task produces the PR body content.

- [ ] **Step 1: Run every leg that runs locally**

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results
dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package
```

Paste the real output into the notes file. No green claim without it.

- [ ] **Step 2: Name the legs that did not run**

Both go in the PR body verbatim, as not-run rather than as passing:

- **TypeScript export surface** (`aspire restore` + `npx tsc --noEmit`, two sample legs) — no `node`/`npm` and no pinned Aspire CLI `13.5.3` on this machine. Left to CI. Relevant here because Task 7 reduces two members' accessibility; `PreparePlan` carries no `[AspireExport]`, which is why this is expected to be a no-op, but expected is not verified.
- **`verify-invariants`** (`ci.yml:513`, three inline `python3` checks) — `python`/`python3` resolve to Microsoft Store stubs that print "not found" and **exit 0**, so a `which` check passes while nothing runs. Left to CI.

- [ ] **Step 3: Record the RS0030 worklist size**

Use the **same command as Task 6 Step 4** — one unit, everywhere, or the number in the PR body means nothing to the follow-up:

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 \
  | grep -oE "[^ ]+\.cs\([0-9]+,[0-9]+\): warning RS0030" | sort -u | wc -l
```

`--no-incremental` is not optional: without it `CoreCompile` is skipped on a project that already built, no analyzer diagnostics are emitted at all, and the count reads `0` — a false "nothing left to migrate".

That number is how many sink-A call sites the follow-up has left, deduplicated across the three target frameworks. It belongs in the PR body and in the follow-up's description.

- [ ] **Step 4: Write the follow-up's scope into the PR body**

**Do not file the issue** — a remote write nobody asked for. Put this in the PR body for the human to file:

The follow-up must cover, in this order:

1. **The remaining `ServiceSourcesConfigurationException` throws** — all of them. Per-sink completeness is the point: the raw constructor can only be banned once nothing uses it. Expect the 84 mixed chains (a `$` prefix on each plain-literal segment, with the compiler pointing at every one), the 17 two-argument throws, and the four `$"…" + Identifier + $"…"` shapes at `UrlSource.cs:124`, `ServiceSourcesWarnings.cs:356`, `ServiceCatalogLoader.cs:122`, `DeveloperConfigValidator.cs:1057`, plus the ternary chain at `DeveloperConfiguration.cs:388–394`, which need restructuring rather than a prefix.
2. **`Config/DeveloperConfigValidator.cs:683–705`** — a `StringBuilder` composition path that binds to .NET's own `AppendInterpolatedStringHandler` and never meets this seam. It must be restructured, not wrapped.
3. **Delete the `WarningsNotAsErrors` carve-out** added in Task 6 and let RS0030 become an error. This is a one-line deletion **only because** Task 3 wraps the `For` factory in `#pragma warning disable RS0030`; without that the sanctioned factory trips its own ban and escalation is impossible. Task 6 Step 5 verifies that separation, so the follow-up inherits a checked claim rather than an assumed one.
4. **Unwrap the 12 `ServiceSourcesWarnings.Label` call sites** as their messages migrate, delete `Label` and the local alias at `EndpointMutationDetector.cs:303`. Until then, do not let a `Label(...)` result reach a `Name` hole — the grep in Task 7 Step 4 is the guard.
5. **Close sink B**, which this PR does not: introduce `ServiceSourcesLog`, ban `LoggerExtensions.Log*` inside this package, and convert the composers' `string` returns to `Raw`. The five composers migrated here have safe bodies but still return `string`, which is a laundering point.
6. **The secondary exception types** — `KubernetesSecretException` (12 throws) and the four `Git*` types (6). `KubernetesBackingServiceSource.cs:1051` labels names with `ConfiguredValue.Bare` alone (no cap, no quote neutralisation) and `:717` embeds `{ex.Message}` raw; both are then flattened into sink A's output by `Describe`. Task 5 bounds that damage; converting these removes it.

- [ ] **Step 5: Report, do not build**

Three findings belong in the PR body as reported-not-built:

- **`Sources/DeferredCheckout.cs:1042`** — `"Service '{ServiceName}': … {Message}"` with `deferred.ServiceName` and `exception.Message` as **structured arguments** to `LogError`. A caller-controlled name reaching a log, and **not among the ticket's 14**. Same shape at `DeferredCheckout.cs:433`, `:653`, `:752`, `:861` and `ServiceStartupFailureNotices.cs:287`. This is the structured sink the ticket excluded; it is reported, not built.
- **`TreatWarningsAsErrors` is not a repo property.** `-warnaserror` is a flag on one CI step (`ci.yml:78`); `aspire-matrix.yml:270` and `net11-preview.yml:77` deliberately omit it, the former with a comment saying a newer Aspire is free to add obsoletions. So a plain local `dotnet build` or an IDE build produces warnings, not errors. Worth a decision independently of this ticket; explicitly not folded in here. This design does not depend on it — CS1503, CS0619, CS1739 and CS0453 are errors at every severity setting, in every workflow and in the IDE.
- **The catalog's own validation of names** stays out of scope. A name that could forge a log line is still an accepted catalog key after this change; it simply cannot forge anything.

- [ ] **Step 6: Note the sibling**

PR #376 (ticket #362) touches `Sources/LocalProjectSource.cs` and `Sources/LocalCheckoutPrefetch.cs`, which Tasks 8 and 9 rewrite. It lands first; this branch rebases over it. Say so in the PR body — whoever lands second rebases, and that is cheap when expected.

---

## Spec coverage

| Spec requirement | Task | Note |
|---|---|---|
| §4 the handler, no `string` hole overload | 2 | |
| §4 `internal static For(...)` | 3 | |
| §4 `BannedApiAnalyzers` | 6 | at **warning**, per the human's split |
| §4 `ServiceSourcesLog` | — | **deferred**: sink B is the follow-up's (Task 11 step 4.5) |
| §7.1 escaping rule moved not respelled | 1 | |
| §7.2 `"` neutralised | 1 | |
| §7.3 cap on `Name` only | 1, 2 | `Raw` and the `int` holes are the never-capped paths; Tasks 2 and 8 test it |
| §7.4 no open generic | 2 | plus the `char` block the spec's enumeration missed |
| §7.5 alignment forms | 2, 4 | **refused** rather than escaped — nothing in `src/` uses them |
| §7.6 `Raw` has no accessible constructor | 2, 9 | `Compose`, `Literal`, `Join`, `Origin`, `Cause` — no `string`-taking factory; pinned by Task 4 |
| §7.7 `Describe` | 5 | |
| §8 migration scope | 8, 9 | the 14 enumerated sites, per the human's split |
| §9.1 `Name` subsumes `Label` | 1, 7 | reimplemented; deletion is the follow-up's |
| §9.2 `ServiceLabel`/`RepositoryLabel` | 7 | `internal`, escapes **and** caps |
| §9.3 `ConfiguredValue` left alone | 7 | with the double-application grep |
| §9.4 `KubernetesBackingServiceSource.cs:473` | 7 | read and decided: a connection-string value, stays |
| §10.1 double-escaping | 7 | **deviation**: the spec unwraps all 12 `Label` sites in this change; the plan defers all 12, because none of their messages migrate here so none can double-escape. Task 7 Step 4 is the standing guard |
| §10.2 rewrite bodies not signatures | 9 | |
| §10.3 the audit sites' third shape | 9 | |
| §10.4 the mixed chains | 8 | for the 9 in scope; the other 84 are the follow-up's |
| §10.5 the `StringBuilder` path | — | **deferred**, named in Task 11 |
| §10.6 cutting inside an escape sequence | 1 | `CutAt` walks whole units |
| §10.7 `test/` fallout | 7, 9 | assertions updated, never relaxed |
| §11 tests 1, 2, 4–6, 8–13 | 1, 2, 5, 7, 8, 9 | |
| §11 test 3 (`"` at the JSON snippet) | 1 | **partly deferred**: the escaping half lands in Task 1; the snippet at `DeveloperConfiguration.cs:388–394` is not migrated here, so it is not asserted end to end |
| §11 test 7 (no double-escape at a `Label` site) | — | **deferred** with the 12 `Label` sites, per the §10.1 deviation above |
| §11 test 14 negative compilation | 4 | **settled**: it is testable in-repo |
| §12 Q1 public API | 7, 10 | **reopened**: `PreparePlan` is itself `internal` (`:34`), so the accessibility change is invisible to consumers and no `### Breaking` entry is truthful. The `internal` half is planned; the CHANGELOG half is in `open_questions` |
| §12 Q2 how test 14 is proved | 4 | **settled**: Roslyn in-memory compilation with positive controls |
| §12 Q3 one PR or split | all | **settled by the human**: seam here, bulk in a follow-up |
| §12 Q4 `TreatWarningsAsErrors` | 11 | reported, not built |
| §12 Q5 sibling #376 | 8, 9, 11 | do not wait, do not rebase onto it |
| §14 out of scope | 11 | reported |
