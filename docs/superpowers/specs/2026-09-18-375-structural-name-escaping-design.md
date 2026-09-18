# Making the escaping of caller-controlled names structural (#375)

**Date:** 2026-09-18
**Status:** Accepted — implemented by PR #381. See *What changed during implementation* at the end;
where this document and the shipped code disagree, the code is what shipped and the note says why.
**Resolves:** #375 (choose a design, and the migration scope, for escaping caller-controlled names in
reader-facing messages).
**Starts from:** [`2026-09-17-372-endpoint-mutation-detector-design.md`](2026-09-17-372-endpoint-mutation-detector-design.md)
(#372) and its PR #374, which escaped four sentence-siblings and deliberately stopped there. #375 is
the residue of that stop. Nothing in #372's design changes here.

**Measured against** `origin/main` @ `c9301b7`, `src/` only, `obj/` excluded. Every count below was
re-derived on that commit and then independently re-derived by three reviewers; where the two
disagreed, the reviewer's number is the one recorded and the method is named. Where a number is not
reproducible from a stated command, it says so rather than being asserted.

**The overload-resolution claims in §5 were run, not reasoned** — two standalone `net8.0` probes,
compiled and executed, one of them checking that the *negative* cases are compile errors. The
observed output is quoted verbatim, and the second probe is what decided the design.

---

## 1. What this document settles

1. **Which design** — choke point, helper + fail-closed build check, or helper alone (§4–§6).
2. **Migration scope** — the 14 enumerated sites, or all 124 (§8).
3. **The naming trap** — which turns out to be three incompatible escaping spellings, not two
   similarly-named helpers (§9).

It does not settle the order of edits; that is the plan's job. §12 lists what it deliberately leaves
open.

**Two of the ticket's framings are wrong against the code, and correcting them changes the answer.**

- **The choke point is not "one seam".** There are two sinks with different properties: an exception
  type this package owns, and `ILogger`, which it does not. A design that closes the first says
  nothing about the second, and the ticket's headline threat — a forged second log entry — lives in
  the second. §3.
- **"124 sites" is a regex hit count, not a population.** It is disjoint from the sites that are
  already escaped, it contains at least two false positives, and it spans both sinks. It is not a
  unit anything can be migrated in. §2.

## 2. The measurement, re-derived

| Claim | Re-derived | Verdict |
|---|---|---|
| 124 sites interpolate a name into a message | `grep -rnE "\$\".*'\{(serviceName\|name\|ServiceName)" src/` → **124**, across **28** files | holds as a grep result |
| …and is a count of unescaped sites | **no.** The set is *disjoint* from the `ServiceSourcesWarnings.Label` call sites (a `'{Label(x)}'` hole cannot match the pattern), and at least two members are already escaped — `ServiceConfigurationExtensions.cs:131` and `:134` interpolate a local assigned from `Label(...)` at `:127` | **corrected** |
| `Label` is applied at 8 sites | **12** call sites: `ServiceSourcesWarnings.cs:302`, `:374`, `:400`; `ServiceConfigurationExtensions.cs:127`; `Sources/UrlSource.cs:120`; and seven in `EndpointMutationDetector.cs` (`:79`, `:93`, `:134`, `:138`, `:141`, `:145`, `:147`) through the local alias at `:303` | **corrected — `UrlSource.cs:120` is named by the ticket and was missing from this document's first draft** |
| the real name-interpolating population | ≈124 unescaped + 12 escaped ≈ **136**, minus the false positives | derived |
| `PreparePlan.ServiceLabel` does not escape | `Prepare/PreparePlan.cs:121` → `public static string ServiceLabel(string serviceName) => $"Service '{serviceName}'";` | holds |
| its callers | `Git/LocalGitCheckout.cs:285`, `:444`, `Sources/DeferredCheckout.cs:831`, `Sources/LocalProjectSource.cs:60` — exactly four | holds |

The ticket's body names **5** log-reaching sites; its comment of 2026-09-18 adds **9** more. **The
enumerated set is 14.** No artefact of this ticket may describe it as five.

| # | Site | Sink |
|---|---|---|
| 1 | `ServiceStartupFailureNotices.cs:512` | composer → `logger.LogError` |
| 2 | `Config/ServiceConfigAudit.cs:142` | composer → `ReportNow` → `logger.LogWarning` |
| 3 | `BackingServices/BackingServiceConfigAudit.cs:189` | same path |
| 4 | `Sources/DeferredCheckout.cs:466` | composer → `logger.LogWarning` |
| 5 | `Sources/LocalCheckoutPrefetch.cs:341` | composer → `logger.LogWarning` |
| 6–8 | `Sources/UrlSource.cs:343`, `:350`, `:356` | `ServiceSourcesConfigurationException` |
| 9–12 | `Sources/KubernetesSource.cs:55`, `:60`, `:66`, `:84` | `ServiceSourcesConfigurationException` |
| 13 | `Sources/EndpointScheme.cs:64` | `ServiceSourcesConfigurationException` |
| 14 | `Sources/LocalProjectSource.cs:478` | `ServiceSourcesConfigurationException` |

**Sites 1–5 are all composers** — `string`-returning helpers whose result is handed to a logger as a
structured argument. Nothing done to a sink's constructor reaches them. That is §3's point, and it is
why five of the ticket's own fourteen are the hard half.

## 3. The two sinks

```
throw new ServiceSourcesConfigurationException   153        .LogInformation  8
throw new KubernetesSecretException               12        .LogWarning      3
throw new InvalidOperationException               10        .LogError        2
throw new PrepareLaunchException                   2        .LogDebug        2
throw new GitUnavailableException                  2
throw new GitCommandFailedException                2        (no LoggerMessage source-gen,
throw new GitAuthenticationFailedException         1         no bare .Log( calls)
throw new FormatException                          1
```

### 3.1 Sink A — `ServiceSourcesConfigurationException`, 153 throws

`public sealed`, two constructors, both `string`. This package owns it, so a new entry point can be
added to it and the old one refused **in this repo** without touching consumers. This is the sink a
choke point can actually close.

Shape of the 153 message arguments, classified with a hand-written C# lexer (a naive scanner
miscounts `string.Join(", ", …)` as a plain segment):

| Shape | Count |
|---|---|
| every segment interpolated — `$"…" + $"…"` | **52** |
| mixed — `$"…" + "…"` | **84** |
| no top-level interpolated literal | **17**, of which only **6** are genuinely constant messages; the other 11 pass a helper call, a ternary or a variable (`LocalProjectSource.cs:280`/`:358`/`:402`, `ServiceConfigurationExtensions.cs:63`/`:76`, `ServiceEndpointExtensions.cs:88`, `CheckoutPreparation.cs:147`/`:320`, `LocalGitCheckout.cs:515`/`:691`, `GitCliClient.cs:100`) |

Two further shapes exist inside those buckets and no `$` prefix fixes either: `$"…" + Identifier +
$"…"` (`UrlSource.cs:124`, `ServiceSourcesWarnings.cs:356`, `ServiceCatalogLoader.cs:122`,
`DeveloperConfigValidator.cs:1057`) and `$"…" + (ternary) + $"…"`
(`DeveloperConfiguration.cs:388–394`). **17** of the 153 are two-argument throws carrying an inner
exception.

### 3.2 Sink B — `ILogger`, 15 calls

**Every one of the 15 uses a structured template with a named placeholder**, and the text is a
structured *argument*: `logger.LogWarning("{Warning}", warning)`,
`logger?.LogWarning("{ServiceSourcesWarning}", message)`, `LogInformation("{PrepareOutput}", line)`.
There is no interpolated string at these call sites for a handler to intercept, and the argument was
composed upstream by a `string`-returning helper.

Some of them pass the **name itself** as a structured argument, not just a composed sentence:
`Sources/DeferredCheckout.cs:1042` is `"Service '{ServiceName}': … {Message}"` with
`deferred.ServiceName` and `exception.Message` as arguments — a caller-controlled name reaching
`LogError`, and **not among the ticket's 14**. Same shape at `DeferredCheckout.cs:433`, `:653`,
`:752`, `:861` and `ServiceStartupFailureNotices.cs:287`.

`ILogger.LogWarning` is an extension method in `Microsoft.Extensions.Logging`. This package cannot
change its signature, cannot deprecate it, and cannot make an interpolated-string handler apply to
it. **Sink B cannot be closed by a constructor.** It can only be closed by refusing direct use of
`ILogger` in this package and routing every call through a reporter that takes a safe type — which
needs a rule the compiler enforces from outside the language, i.e. an analyzer. §6.

### 3.3 Sink C — `Exception.ToString()`, which is neither

`ServiceSourcesConfigurationException.Describe` (`:52–65`) flattens **every inner exception's
`Message` verbatim**, each preceded by `Environment.NewLine + "  caused by: "`. `ToString()` is the
primary sink for an `AddService` call that takes the AppHost down, so this is the single most-read
output this package produces.

An inner exception's message is not built through any seam: libgit2's own wording,
`KubernetesSecretException`, an `IOException` carrying a path. A newline in one of those forges a
line inside the blob — including a convincing fake `  caused by:` line. Closing sink A does nothing
about this. It is in scope (§8); it was missed entirely by this document's first draft.

## 4. Decision 1 — the design

**Decision: the choke point, in the strictest available form — a message handler with no
`string` hole overload at all.** Every hole must be `Name(x)` (escaped and capped) or `Raw(x)`
(verbatim, by construction). A raw `string` hole does not render unescaped; **it does not compile.**

The seam stays `internal`. The exception's public `string` constructors are untouched, so consumers
see no API change; the new entry point is an internal static factory. Direct use of the old
constructor and of `ILogger` inside this package is refused by
`Microsoft.CodeAnalysis.BannedApiAnalyzers`, at `error` severity pinned in `.editorconfig`.

```csharp
[InterpolatedStringHandler]
internal ref struct ServiceTextHandler
{
    public void AppendLiteral(string value);            // the author's own words — verbatim
    public void AppendFormatted(Name value);            // caller-controlled — escaped, then capped
    public void AppendFormatted(Raw value);             // already safe by construction
    public void AppendFormatted(int value);             // and the other primitives actually in use,
    public void AppendFormatted(int? value);            // enumerated — NOT an open generic (§7.4)
    // plus the alignment/format-specifier forms of each (§7.5)
}

internal readonly struct Name  { internal Name(string? value); }   // the only escaping entry point

internal readonly struct Raw                                        // no accessible constructor
{
    internal static Raw Compose(ServiceTextHandler text);           // built through the seam
    internal static Raw Literal([ConstantExpected] string text);    // compile-time constants only
}

// Sink A's new entry point. There is no string overload.
internal static ServiceSourcesConfigurationException For(ServiceTextHandler message);
internal static ServiceSourcesConfigurationException For(ServiceTextHandler message, Exception inner);

// Sink B's. Direct ILogger use is banned in this package.
internal static class ServiceSourcesLog { internal static void Warning(ILogger?, Raw message); /* … */ }
```

## 5. Why this form — the probe that decided it

Two `net8.0` probes were compiled and run. The first asked what happens when a handler-typed
overload sits *beside* a `string` one (the design this document proposed in its first draft). The
second asked what happens when there is **no** `string` overload.

**Probe 1 — handler constructor alongside the `string` constructor.** All six rows reproduced under
independent re-running by a reviewer:

| Written as | Bound to |
|---|---|
| `$"Service '{name}' failed."` | handler |
| `$"… '{name}' a " + $"and '{name}' b"` | handler |
| `$"… '{name}' a " + "literal tail"` | **`string` — silently** |
| `var s = $"…{name}…"; new Ex(s)` | **`string` — silently** |
| `"constant only"` / `$"constant only"` | `string` |
| `$"port {n} for '{name}'"` | handler |

**84 of the 153 throws are the third shape.** A handler placed beside a `string` overload therefore
produces a seam that looks total and silently covers a third of its own sink — the worst outcome
available, and the reason the first draft of this document was wrong.

**Probe 2 — a handler-only factory, no `string` overload.** Positive cases:

```
A: Service '[NAME:svc'\nforged]' failed.          $"Service '{name}' failed."
B: constant only, no holes                        $"constant only, no holes"   <- binds to the handler
C: a '[NAME:…]' and '[NAME:…]' b                  $"a '{name}' " + $"and '{name}' b"
D: port [NUM:5] for '[NAME:…]'                    $"port {5} for '{name}'"
E: pre-composed 'text'                            $"{Raw("pre-composed 'text'")}"
```

Negative cases, compiled:

```
error CS1503: cannot convert from 'string' to 'MsgHandler'   $"a '{n}' " + "plain literal tail"
error CS1503: cannot convert from 'string' to 'MsgHandler'   For(someStringVariable)
error CS0453: 'string' must be a non-nullable value type…    $"name '{s}'"   (a raw string hole)
```

Four consequences, and together they are the whole argument for this form:

1. **A mixed chain is a compile error, not a silent fallback.** The 84 become a compiler worklist.
2. **A raw `string` hole is a compile error.** There is no "escaped by default" and therefore no
   silent over-escaping and no silent truncation — the two hazards the first draft carried.
3. **A hole-free constant message binds to the handler** (row B). The 6 genuinely constant messages
   need no escape hatch, and the first draft's blocking open question about them dissolves.
4. **No `[Obsolete]`, no `-warnaserror` dependency, no public API change.** The gate is plain overload
   resolution emitting hard CS errors. §6.1 explains why that matters more than it sounds.

## 6. The other two options, priced

### 6.1 The gate the first draft assumed does not exist

`-warnaserror` is a **command-line flag on one CI step** (`.github/workflows/ci.yml:78`). There is no
`TreatWarningsAsErrors` in `Directory.Build.props`, `Directory.Build.targets` or any `.csproj`, so a
plain local `dotnet build` or an IDE build produces warnings, not errors. Worse for any
`[Obsolete]`-based design: `aspire-matrix.yml:270` and `net11-preview.yml:77` **deliberately omit the
flag**, the former with a comment saying a newer Aspire is free to add obsoletions. The repo has an
explicit standing policy of not failing on obsoletion warnings.

Any design whose fail-closed guarantee is "a warning, escalated by a flag" is therefore weaker here
than it looks. §4's design does not depend on it: CS1503 and CS0453 are errors at every severity
setting, in every workflow, and in the IDE.

### 6.2 The middle option — helper plus a build check

The ticket prices "an analyzer" and "a `verify-invariants` check" as one option. They are not one
option, and the repo is not in the state the ticket assumes.

**The repo already runs Roslyn analyzers.** `Directory.Build.props` sets
`EnableAspireIntegrationAnalyzers=true` repo-wide, with per-project opt-out machinery in
`Directory.Build.targets`, so Aspire's `ASPIREEXPORT*` rules already gate the build. "There is no
analyzer infrastructure here" is false; what is true is that there is no analyzer *project* — `src/`
holds one project and nothing references `Microsoft.CodeAnalysis`.

- **Authoring a custom analyzer** still means a new project, its own multi-targeting against
  `net8.0;net9.0;net10.0`, its own tests and a packaging decision. Larger than the seam it was
  offered as the cheap alternative to. **Rejected as the primary mechanism.**
- **`Microsoft.CodeAnalysis.BannedApiAnalyzers`** needs no project — a `PackageReference` and a
  `BannedSymbols.txt`. It bans *symbols*, not syntax, so it cannot see an unescaped name inside an
  interpolated string. The first draft rejected it on that ground. **That rejection was wrong in the
  presence of an internal seam:** once the only safe path is `For(...)` and `ServiceSourcesLog.*`,
  "ban `ServiceSourcesConfigurationException..ctor(string)` and `LoggerExtensions.Log*`" is exactly
  the rule that needs enforcing, and RS0030's severity can be pinned to `error` in `.editorconfig`
  independently of `-warnaserror`. **Adopted as the complement to §4, and it is what closes sink B
  at all.**
- **A `verify-invariants` Python check** is the cheapest thing that could be built — the job exists
  (`ci.yml:513`) and the marginal cost is ~50 lines. **Rejected**, on three grounds, of which only
  the second and third are strong:
  1. It cannot be run before pushing. On this machine `python` and `python3` resolve to Microsoft
     Store execution-alias stubs that print "Python was not found" and **exit 0** — so a
     `which python3` check passes and the invariant silently does not run. That is an observation
     about this environment, not a repo property, but it is the environment the work happens in.
  2. **It is a recogniser over an input space larger than itself — the exact failure the ticket
     diagnoses.** It would have to flag a single-quoted identifier hole not wrapped in `Label(`,
     while the real messages are full of single-quoted holes that are not names: `'{source}'`,
     `'{Http}'`, `'{remotePort}'`, `'{configured}'`, `'{Redacted(rawUrl)}'`,
     `'{ConfiguredValue.Bare(field.Key)}'`. Each needs an allowlist entry, and the allowlist is a
     second hand-maintained fail-open surface. Asked the ticket's own two questions: it passes
     through what it does not understand, and it would have to own the whole of C# interpolation
     syntax to be right.
  3. **It enforces spelling, not safety.** `Label(x)` being present is all it can see — and
     `EndpointMutationDetector.cs:303` already shadows `Label` behind a local alias.

### 6.3 The helper alone

Foreclosed by the ticket's requirement 2: fail-open is the defect. Recorded as considered. If §4 is
rejected on cost, the honest outcome is "no design change", said plainly — **not** a half-migration,
which is worse than the status quo because it manufactures the appearance of a closed class.

### 6.4 Why not "names as a distinct type", as the ticket phrases it

The ticket says "a builder that takes names as a distinct type rather than as `string`". §4 **is**
that, applied at the hole rather than at the parameter. The distinction matters, because the reading
that migrates the *parameters* — all **152** `string serviceName` / `string name` declarations across
**38** files, reaching the public `AddService(string name, …)` — costs a package-wide type migration
and still leaves a plain `string` from elsewhere raw. Wrapping at the hole costs an edit per hole,
touches no signature, and leaves nothing raw because raw does not compile.

## 7. What the seam must do

1. **`Name` escaping is `ServiceSourcesWarnings.Label`'s existing rule, moved, not respelled** —
   `\` doubled first, then `ConfiguredValue.Bare`, then `'` → `\'`, then a 64-character cap with a
   surrogate-safe cut and an ellipsis (`ServiceSourcesWarnings.cs`: `MaxLabelLength` at `:306`,
   `Label` at `:324–343`). The ordering comments there are load-bearing and move with the code. #374
   settled this rule; #375 does not reopen it, with one exception:
2. **`Name` must also neutralise `"`.** `Label` neutralises `'` only, and there are real
   double-quote-delimited caller-controlled holes: `Config/DeveloperConfiguration.cs:391`, `:392`,
   `:544`; `BackingServices/DirectBackingServiceSource.cs:49`;
   `BackingServices/KubernetesBackingServiceSource.cs:1145`, `:1169`;
   `BackingServiceBuilderExtensions.cs:249`, `:251`. Several of those render a pasteable JSON
   snippet, so a `"` in a name breaks the delimiter *and* the snippet. Requirement 6 is currently
   half-met; this is the half that was missing.
3. **The cap applies to `Name` and to nothing else.** This is the property the first draft got wrong.
   Capping every string hole would truncate `{repoRoot}`, `{CatalogOrigin.Describe()}`,
   `{ex.Message}`, `{string.Join(", ", quoted)}` and the JSON snippet at
   `DeveloperConfiguration.cs:388–394` at 64 characters — silently, past a green build, and past a
   test suite whose fixtures use short names. Escaping and capping are separate concerns and only
   names get both.
4. **No open generic `AppendFormatted<T>`.** An open generic passes through anything that is not a
   `Name` or a `Raw` — including `char` (which can be `'\n'`), `object`, and any record whose
   generated `ToString()` embeds a name. Enumerate the primitive overloads actually needed; anything
   not enumerated is then a compile error rather than a silent pass-through. That is the difference
   between "escape by default" and "refuse by default", and refusing is the whole point.
5. **The alignment and format-specifier forms must escape identically.** `{name,-10}` and `{name:N0}`
   route to `AppendFormatted(Name, int, string?)`. An overload that forgot to escape would be a
   bypass spelled almost identically to the safe form. A test pins it.
6. **`Raw` has no accessible constructor.** `Raw.Compose($"…")` runs the seam, so a composer's body is
   escaped by construction rather than whitelisted by its return type; `Raw.Literal` takes a
   `[ConstantExpected]` string. This is what stops `Raw` from becoming "remember not to write `Raw`"
   — a one-token bypass spelled the same as its safe use, which is the property the ticket filed
   against. `default(Raw)` must render as empty, not throw.
7. **`Describe` must not flatten an inner message verbatim** (§3.3). The minimum is that each
   `cause.Message` has its line breaks neutralised before it is appended under `  caused by: `.

## 8. Decision 2 — migration scope

**Decision: neither of the two options as posed. The unit is the sink, and this ticket takes sink A
whole, plus the three cross-cutting fixes that sink A's own 14 sites depend on.**

The human's question was "the 14, or all 124". Both units are wrong, and saying so is part of the
answer:

- **The 14 is wrong** because the conversion of fail-open into fail-closed happens when the raw path
  stops compiling, and it stops compiling per *sink*, not per site. 14 sites migrated while the raw
  constructor stays reachable is the status quo with a new class in it.
- **The 124 is wrong** because it is a regex hit count that is disjoint from the already-escaped
  sites, contains false positives, and spans both sinks (§2). Nothing can be "migrated to 124".

**In scope:**

- the seam — `ServiceTextHandler`, `Name`, `Raw`, `ServiceSourcesLog`
- `BannedApiAnalyzers` + `BannedSymbols.txt` + `.editorconfig` severity, banning the raw constructor
  and direct `LoggerExtensions.Log*` in this package (§6.2)
- **all 153** `ServiceSourcesConfigurationException` throws, including the 17 two-argument ones
- the **12** existing `Label(...)` call sites, unwrapped (§10.1)
- the **composers** behind the ticket's sites 1–5, and the log call sites they feed — without these,
  5 of the 14 are untouched (§3.2)
- `Describe`'s inner-message flattening (§3.3, §7.7)
- the naming trap (§9)

**Named residue, for follow-up issues the human files:**

- `KubernetesSecretException` (12 throws) and the four `Git*` types (6). These matter more than
  "secondary": `KubernetesBackingServiceSource.cs:1051` labels names with `ConfiguredValue.Bare`
  alone — no cap, no quote neutralisation — and `:717` embeds `{ex.Message}` raw, and both are then
  flattened into sink A's output by `Describe`. §7.7 bounds the damage; converting them removes it.
- The structured-argument log sites that pass a name as an argument rather than inside a composed
  sentence (`DeferredCheckout.cs:1042`, `:433`, `:653`, `:752`, `:861`,
  `ServiceStartupFailureNotices.cs:287`). `DeferredCheckout.cs:1042` is a caller-controlled name
  reaching `LogError` and is **not** among the ticket's 14.
- `InvalidOperationException` (10) and `FormatException` (1) — programmer-error paths, not
  reader-facing diagnostics. Probably correctly left alone; the judgement should be written down
  rather than implied by silence.

**On "a partial migration delivers nothing":** that claim, as the first draft made it, overreached —
this very scope leaves 28 throws on the raw path. The defensible claim is narrower and is the one
this section makes: **per-sink completeness**, because a sink's raw path can only be banned once
nothing in the repo uses it. Scope may stop at a sink boundary. It may not stop inside one.

**If the diff proves unreviewable**, split at the sink boundary — seam + sink A first, then the
composers, sink B and `Describe`. Never split by "the enumerated 14 first": that is the shape §6.3
rejects, and it leaves sink A's constructor reachable, which means nothing is banned and nothing is
closed.

## 9. Decision 3 — the naming trap

**The trap is wider than the ticket's two helpers: this package currently has three mutually
incompatible ways to make a name safe, and a fourth way that only looks like one.**

| Spelling | Sites | What it does |
|---|---|---|
| `ServiceSourcesWarnings.Label` | 12 | `\` doubled, `ConfiguredValue.Bare`, `'` → `\'`, capped at 64 |
| `ConfiguredValue.Bare` / `.Escaped` | 52 | invisibles spelled out; **no quote neutralisation, no cap** |
| `.Replace("'", "''")` at `KubernetesBackingServiceSource.cs:473` | 1 | SQL-style doubling — **incompatible** with `Label`'s `\'` |
| `PreparePlan.ServiceLabel` / `RepositoryLabel` | 4 callers | **nothing.** `$"Service '{serviceName}'"` |

**Decisions:**

1. **`Name` subsumes `Label`.** After the migration `ServiceSourcesWarnings.Label` has no caller
   outside the seam; it becomes the seam's private escape and the alias at
   `EndpointMutationDetector.cs:303` is deleted. (Note it is already `internal`, so "only one *public*
   thing named `Label`" was never the property at issue — reachability is.)
2. **`PreparePlan.ServiceLabel` and `RepositoryLabel` compose through the seam and return `Raw`,
   via `Raw.Compose($"Service '{new Name(serviceName)}'")`.** They must escape **and cap**: they are
   the label their four callers embed in further messages, so a `Raw` that skipped the cap would
   reopen requirement 5 on exactly the path this section is fixing.
   **They are `public static string` today.** Returning an `internal` type is CS0050, so this is a
   public API change however it is done. §12 open question 1.
3. **`ConfiguredValue.Bare`/`.Escaped` stay as they are.** They are a different, deliberate contract —
   how a *configured value* is echoed back — and `Label` is already defined in terms of `Bare`. What
   must not happen is double application: a `Bare(...)` result used as a `Name` escapes twice. The 52
   sites are not migrated; they are checked for that one interaction.
4. **`KubernetesBackingServiceSource.cs:473`'s `''` doubling is named and left alone** unless the plan
   finds it is building a message rather than a selector — in which case it is a third escaping rule
   inside one package and must go. The plan reads it; this document does not guess.

## 10. The migration's traps

### 10.1 Double-escaping, at 12 sites and possibly 52 more

`$"'{new Name(Label(x))}'"` escapes twice. All **12** `Label(...)` call sites (§2 — including
`UrlSource.cs:120`) must be unwrapped in the same change that introduces the seam. The **52**
`ConfiguredValue.Bare`/`.Escaped` call sites carry the identical hazard wherever their result would
become a `Name` hole (§9.3). A test asserting the exact rendering of a name containing `'`, `"`, `\`
and a newline, at one newly-migrated site and one previously `Label`-wrapped site, is the guard.

### 10.2 `Raw.Compose` does not make a composer's body safe

Converting a composer's return type is not the fix; rewriting its body through the seam is. Several
bodies are unsafe today: `Prepare/CheckoutPreparation.cs:365` (`LaunchFailedMessage` embeds
`{ex.Message}` raw), `:347`, and `Config/DeveloperConfiguration.cs:393`
(`EnvironmentVariableFor(serviceName)` — undelimited and raw). Because `Raw` has no accessible
constructor (§7.6), the compiler forces each body through `Raw.Compose`, which is the point of that
choice. Budget the body rewrite, not the signature.

### 10.3 The two audit sites need a third shape

`Config/ServiceConfigAudit.cs:142` and `BackingServices/BackingServiceConfigAudit.cs:189` — sites 2
and 3 of the 14 — quote each name **inside a `Select` lambda** (`$"'{orphan}'"`) and hand the sink one
joined hole. Neither wrapping the joined hole as a `Name` (which would cap the whole list at 64) nor
as a `Raw` (which launders every name inside it) is correct. The lambda itself must compose:
`Raw.Compose($"'{new Name(orphan)}'")`, joined, then embedded as a `Raw`. The first draft of this
document picked the laundering branch without noticing.

### 10.4 The 84 mixed chains, and the shapes a `$` does not fix

A `$` prefix on each plain-literal segment covers most of the 84, and the compiler points at every
one. Watch for: a segment containing `{`/`}` needing them doubled once interpolated; a segment that
is a `const` reference (`OutOfBandSourceAdvice.SwitchSource`) becoming a hole and therefore a `Raw`;
and the two shapes in §3.1 that need restructuring rather than a prefix.

### 10.5 A `StringBuilder` composition path

`Config/DeveloperConfigValidator.cs:683–705` builds a multi-line message with `.Append($"…")`, which
binds to .NET's own `AppendInterpolatedStringHandler`, then `.ToString()`. It never meets this seam
and must be restructured, not wrapped.

### 10.6 Cutting inside an escape sequence

`ServiceSourcesWarnings.cs:337–341` always appends the closing `…` after the cut, so a truncated
`\\`, `\'` or `\uXXXX` cannot leave a lone `\` beside the delimiter — **it is not a forgery hole.** It
is an injectivity break: a truncated `\u20` is indistinguishable from a literal one, which is the
property `ConfiguredValue.PrintsAsItself` exists to protect. Cut on escape-unit boundaries.

### 10.7 In-repo fallout

`test/` holds 17 `new ServiceSourcesConfigurationException(...)` sites and 11 references to
`ServiceLabel`/`RepositoryLabel`, all built under the same rules. The 384 `test/` references to the
exception type are the blast radius **and** the net that catches a message whose shape changed — so
this migration must not be accompanied by any loosening of those assertions.

## 11. Tests

1. A name containing `\n` cannot produce a line it controls, at a migrated sink-A site and at a
   migrated sink-B site. (Not "cannot produce a second line": `Describe` emits `Environment.NewLine`
   unconditionally, so these messages are already multi-line by design.)
2. A name containing `'` cannot close the delimiting quote.
3. A name containing `"` cannot close a double-quoted delimiter or break the JSON snippet at
   `DeveloperConfiguration.cs:388–394` (§7.2).
4. A name containing `\` immediately before a quote cannot un-escape it.
5. A name over 64 characters is capped with the ellipsis **on the exception path** — requirement 5,
   which today's `Label` tests cover only on the warning path.
6. A cap landing mid-surrogate-pair does not split it; a cap does not land inside an escape unit
   (§10.6).
7. A previously `Label`-wrapped site does not escape twice (§10.1).
8. A long path, a long joined list and a long `ex.Message` are **not** truncated (§7.3) — the
   regression test for the first draft's worst defect.
9. A `Raw` fragment is not re-escaped when embedded in an outer message.
10. `PreparePlan.ServiceLabel` escapes **and caps**, and its result is not escaped again at its four
    callers (§9.2).
11. The two audit sites escape each name inside the list without capping the list (§10.3).
12. An inner exception whose `Message` contains a newline cannot forge a `  caused by:` line through
    `Describe` (§3.3).
13. The alignment and format-specifier forms escape identically to the plain one (§7.5).
14. **Negative-compilation proof that the raw path does not build.** This is the deliverable; §12
    open question 2 is how it is proved.

## 12. Open Questions

1. **`PreparePlan.ServiceLabel`/`RepositoryLabel` are `public static string`.** Returning an internal
   `Raw` is CS0050. Options: make them `internal` (a breaking API change, `### Breaking`, and they are
   plausibly not used outside this package — the plan should check the `[AspireExport]` surface and
   the samples); keep them `public string` and have them escape and cap internally, accepting that a
   `string` result can be re-escaped at a `Name` hole by mistake; or make `Raw` public, which puts a
   trust wrapper on the public surface. **Needs a decision before the plan.** It is the only public
   API question left in this design — the rest of the seam is internal by construction.
2. **How is test 14 proved?** Negative-compilation tests have no established pattern in this repo, and
   a project expected to fail to build sits awkwardly in a `dotnet test` run. `BannedApiAnalyzers`
   partly answers it — an RS0030 at `error` severity in `.editorconfig` is itself checked in and
   reviewable — but that covers the banned symbols, not the CS0453 that refuses a raw hole. Fallback:
   state the guarantee in the PR body with the build output that demonstrates it, and accept that it
   is unguarded against future removal.
3. **Is the sink-A migration one PR?** 153 throws, their holes, 12 unwrappings, the composers, and
   `test/` fallout is a large diff even though every edit is compiler-pointed. If it is not, §8 names
   the only acceptable split. The human should decide whether to pre-agree the split rather than
   discover it at review.
4. **Should `TreatWarningsAsErrors` move into `Directory.Build.props`?** §4's design does not need it —
   that is the point of §6.1 — but the repo currently has a flag on one CI step and an explicit
   carve-out in two other workflows, which is worth knowing about independently of this ticket. Out of
   scope here unless the human wants it folded in.
5. **Sibling PR #376 (#362)** adds message sites in `Sources/LocalProjectSource.cs` and
   `Sources/LocalCheckoutPrefetch.cs`, both of which this migration rewrites. The recorded decision is
   that #376 lands first and #375 rebases over it, with unescaped names in those files fixed here.
   Worth reconfirming at implementation time, because once the seam lands the conflict surface is the
   whole file.

## 13. Attack surface

- **What is newly trusted: `Raw`, and only through two constructors it controls.** `Raw.Compose` runs
  the seam, so a composer cannot launder a raw name through it; `Raw.Literal` takes a
  `[ConstantExpected]` string, so it cannot carry a runtime value. This is deliberately stricter than
  the first draft's positional record struct, which was a one-token bypass spelled identically to its
  safe use — the very property the ticket filed against. `internal` is not itself a boundary here
  (three `InternalsVisibleTo` test assemblies); the constructor discipline is.
- **What the design refuses rather than escapes.** A raw `string` hole, a mixed additive chain, an
  un-enumerated hole type: all compile errors. The residual pass-through is the enumerated primitive
  overloads (§7.4), which the plan enumerates from actual usage.
- **What it does not close, stated plainly.** Sink B is closed by a banned-symbol rule, not by the
  type system — someone who removes the `BannedSymbols.txt` entry reopens it silently. `Describe`'s
  inner messages are bounded (§7.7), not eliminated, until the secondary exception types migrate
  (§8). Both are honest limits, not oversights.
- **It executes nothing and crosses no boundary.** No shell, path, URL, deserializer or network call;
  the handler appends to a `StringBuilder`. `null` names already render empty via
  `ConfiguredValue.Bare(null)`.
- **Cost of escaping unbounded input.** `Bare` builds a `StringBuilder` over the whole value with up
  to ~6x expansion per invisible character *before* the cap. Under this design that runs only at
  `Name` holes, not at every hole — another consequence of §7.3.
- **No public API surface is added** beyond whatever open question 1 decides. No `[Obsolete]`, so no
  consumer-visible diagnostic and no interaction with the obsoletion carve-out in
  `aspire-matrix.yml:270`.
- **The cap is truncation, not validation.** A long name renders elided. Unchanged from #374, stated
  so nobody reads the cap as name validation, which is explicitly out of scope (§14).

## 14. Out of scope

- **The catalog's own validation of names.** A name that could forge a log line is still an accepted
  catalog key after this change; it simply cannot forge anything.
- **Structured logging as a redesign.** The ticket excludes "log injection into a structured sink",
  and this document keeps that exclusion — but **its stated premise is false and must not be repeated**:
  every one of the 15 log calls uses a structured template, and six of them pass a name or an
  exception message as a structured argument (§3.2). The exclusion stands because converting this
  package to structured fields is a different piece of work, not because the package logs plain
  strings.

## 15. Disposition

| Ticket requirement | Where | Settled? |
|---|---|---|
| 1 — a chosen design, priced against all three options | §4, §6 | yes |
| 2 — fail-open becomes fail-closed | §4, §5 (compile errors, not warnings), §6.1 | yes for sink A by the type system; for sink B by a banned-symbol rule, §13 states the limit |
| 3 — the messages must keep naming the service | §4, §7 | yes |
| 4 — a newline cannot forge an entry | §7.1, tests 1, 12 | yes |
| 5 — an exception message's name is capped | §7.3, §9.2, test 5 | yes |
| 6 — the delimiting quote is neutralised | §7.2, tests 2, 3 | yes — and the `"` half was missing before |
| 7 — the naming trap resolved | §9, tests 7, 10 | decided; §12 open question 1 is its remaining public-API choice |
| 8–12 — the body's five enumerated sites | §2 (all composers), §8 (composers in scope), §10.2 | yes |
| 13–16 — the comment's nine | §8 (inside sink A's 153) | yes |
| 17 — structured-sink injection out of scope | §14 | kept, premise corrected |
| 18 — catalog name validation out of scope | §14 | kept |
| Human decision A — which design | §4 | recommended: strict choke point, no `string` hole overload |
| Human decision B — migration scope | §8 | recommended: sink A whole + the composers behind sites 1–5 + `Describe`; neither "the 14" nor "all 124", and §8 says why both units are wrong |

## What changed during implementation

This section is the difference between the design above and PR #381 as it shipped. It is written
because the two diverge in ways a reader of the spec alone would get wrong.

- **`Raw` has six factories, not two.** The design describes a trust surface of "two constructors it
  controls". What shipped is `Compose(ServiceTextHandler)`, `Literal([ConstantExpected] string)`,
  `Escaped(string?)`, `Join([ConstantExpected] string, IEnumerable<Raw>)`, `Origin(CatalogOrigin)`
  and `Cause(Exception)`. One of them, `Escaped`, does take a runtime `string` — it escapes it by the
  `Name` rule rather than trusting it, which is what separates it from a bypass, and
  `EveryStringTakingRawFactoryEscapesItsArgument` enforces that behaviourally rather than against a
  name allowlist.

- **Alignment and format-specifier overloads were not added.** The design asks for them so the forms
  can be refused explicitly. In practice their absence already refuses them, as `CS1739`, which is a
  better refusal than an overload that throws: it is a compile error rather than a runtime one.
  `RawPathDoesNotCompileTests` pins both forms.

- **`ServiceSourcesLog` does not exist.** The design declares it as sink B's counterpart. Sink B is
  not closed by this PR at all — all fifteen `logger.Log*` calls pass their text as a structured
  argument, so nothing done to an exception constructor reaches them. It is item 5 of the follow-up.

- **The apostrophe is escaped as its unicode escape, not as a backslashed apostrophe.** Found in
  review: the audit message tells the reader to paste the name back into `servicesources.local.json`,
  and a backslashed apostrophe is not a legal JSON escape — it makes the whole file unparseable,
  which is worse than the unescaped name was. The unicode escape round-trips through both JSON and
  C#. This is the same reason `ConfiguredValue.Bare` already spells invisibles that way.

- **`Raw.Origin` escapes the yaml path.** The design treats a catalog origin as safe because it is
  not a name. It is a developer-chosen filesystem path rendered inside the message's own quotes, so
  a directory with an apostrophe in it closes them.

- **The launch-profile warning's URLs go through `Raw.Escaped`, not `Name`.** A `Name` hole capped
  each URL at 64 characters and dropped the port — the one fact that warning exists to report.

- **`BannedApiAnalyzers` bans only the exception constructor.** Banning `ServiceTextHandler`'s
  constructor and `AppendLiteral` was measured and rejected: RS0030 also fires on the compiler's own
  lowering of every `$"…"`, taking the deduplicated worklist from 154 to 1842. The hand-built-handler
  bypass is closed by `[ConstantExpected]` on `AppendLiteral` plus the CA1857 escalation instead.
