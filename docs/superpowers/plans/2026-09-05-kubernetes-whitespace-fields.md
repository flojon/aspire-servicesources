# Surrounding whitespace on a kubectl-facing field — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refuse a developer-config value whose surrounding whitespace would be handed to `kubectl`
as part of a cluster object's name, naming the key and the spelling that works.

**Architecture:** An opt-in attribute on the config property declares that its value reaches an
external tool verbatim. `DeveloperConfigField.BlockFieldsOf` starts carrying `PropertyInfo` instead
of `Type` so the validator can see that attribute, and `DeveloperConfigValidator.CollectBlock` gains
one branch between its existing `Blank` and `BindsTo` checks. Nothing is trimmed at the point of
use; no value is rewritten anywhere.

**Tech Stack:** C# / .NET (net8.0, net9.0, net10.0), xUnit, `Microsoft.Extensions.Configuration`.

**Spec:** `docs/superpowers/specs/2026-09-05-kubernetes-whitespace-fields-design.md`

## Global Constraints

- The build runs with `-warnaserror`; it is warning-clean today and must stay so.
- Every type here is `internal`. This package's public surface does not change.
- Test framework is xUnit, and the suite is multi-targeted — a test written once runs on net8.0,
  net9.0 and net10.0.
- Comments explain *why*, in the register of the surrounding file. Never write a comment describing
  what a pull request changed.
- Message register: name the entry kind, the key, the escaped value, and the spelling that works;
  end with the `SetAt` suffix. Remedy verb is `Set it to …`.
- The five opted-in fields, and no others:
  | Type | Properties |
  |---|---|
  | `KubernetesDeveloperConfig` | `Context`, `Namespace` |
  | `KubernetesBackingServiceDeveloperConfig` | `Context`, `Namespace`, `Service` |

---

### Task 1: `Escaped` renders a character with no glyph

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs` (the `Escaped`
  helper, near the bottom of the file)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigValidatorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `Escaped(string?)` renders any `char.IsControl` character, or one in Unicode category
  `Format`, as `\uXXXX`. Tasks 3 and 4 rely on this.

**Why first:** it is independently testable through a message that already exists, so it lands
before anything depends on it.

- [ ] **Step 1: Write the failing test**

Append to `DeveloperConfigValidatorTests.cs`:

```csharp
/// <remarks>
/// A byte-order mark is what a copy-paste out of a Windows-authored file leaves behind, and it has
/// no glyph at all: echoed as itself it is indistinguishable from the value being correct. It is
/// not whitespace, so the arm that spells out a tab does not reach it.
/// </remarks>
[Fact]
public void Validate_ValueCarryingAnInvisibleCharacter_SpellsItOutRatherThanEchoingIt()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "port": "\uFEFF8080" } } } }
        """);

    Assert.Contains("\\ufeff", ex.Message);
    Assert.DoesNotContain("'\ufeff8080'", ex.Message);
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter FullyQualifiedName~ValueCarryingAnInvisibleCharacter
```

Expected: FAIL — the message echoes the BOM as itself, so `U+FEFF` is absent.

- [ ] **Step 3: Add the arm**

In `Escaped`, after the existing `_ when char.IsWhiteSpace(c)` arm:

```csharp
// A character with no glyph of its own is worse than one that merely looks like a space: echoed
// as itself it is invisible, so the value reads as though nothing were wrong with it. Control
// characters and Unicode's Format category are the two slices of that this can name without
// reaching a character somebody meant — a combining mark is invisible too, and is a real thing to
// write.
_ when char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
    => $"\\u{(int)c:x4}",
```

Add `using System.Globalization;` if the file does not already have it.

- [ ] **Step 4: Run the test and the whole config suite**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter FullyQualifiedName~Config
```

Expected: PASS, with no other test in `Config` regressing.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Spell out a character with no glyph in an echoed value (#236)"
```

---

### Task 2: `BlockFieldsOf` carries the property, not the type

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigField.cs:38,47`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigShape.cs:104` and its `<remarks>`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs:176,180,190,196,231,233,510,572`

**Interfaces:**
- Consumes: nothing.
- Produces: `DeveloperConfigField.BlockFieldsOf(Type) → IReadOnlyDictionary<string, PropertyInfo>?`
  and `DeveloperConfigShape.BlockFields → IReadOnlyDictionary<string, IReadOnlyDictionary<string,
  PropertyInfo>>`. Task 3 reads the attribute off those `PropertyInfo` values.

**This is a pure refactor with no behavioural change, so it has no new test.** Its gate is that the
existing suite stays green — which is a real gate here, because `BlockFields` feeds every
"valid keys there are …" message in the file.

- [ ] **Step 1: Record the baseline**

```bash
dotnet test -c Release 2>&1 | tail -5
```

Write down the passing count. Nothing about it may change by the end of this task.

- [ ] **Step 2: Change the producer**

`DeveloperConfigField.cs` — the return type and the value selector:

```csharp
public static IReadOnlyDictionary<string, PropertyInfo>? BlockFieldsOf(Type type)
{
    if (!type.IsClass || type == typeof(string) || IsList(type))
    {
        return null;
    }

    return type.GetProperties()
        .Where(IsConfigurable)
        .ToDictionary(field => field.Name, field => field, StringComparer.OrdinalIgnoreCase);
}
```

Update that method's `<remarks>`, which says the dictionary is keyed "name to the type the value has
to bind to": it now carries the property, which is the type *and* what was declared about it.

- [ ] **Step 3: Change the declared types that carry it**

`DeveloperConfigShape.cs:104`:

```csharp
public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PropertyInfo>> BlockFields { get; }
```

and its `<remarks>` sentence "The type travels with the name because …" gains the reason it is now a
property: a key can also be *declared* with a rule the walk has to see.

`DeveloperConfigValidator.cs` — **first add `using System.Reflection;` to the top of the file.** It
has only `Microsoft.Extensions.Configuration`, `System.ComponentModel` and `System.Text`, and
`System.Reflection` is not among this project's global usings, so without it every signature below
is a `CS0246` and Step 5's build fails. Then three signatures, all mechanical:
- `CollectBlock`'s `fields` parameter (line ~176)
- `NotValidInBlock`'s `fields` parameter (line ~510) — uses only `.Keys`
- `BlockExpected`'s `fields` parameter (line ~572) — uses only `.Keys`

- [ ] **Step 4: Change the four use sites inside `CollectBlock`**

```csharp
if (!fields.TryGetValue(field.Key, out var declared))
{
    problems.Add(NotValidInBlock(field, blockPath, fields));
    continue;
}

if (DeveloperConfigField.IsList(declared.PropertyType))
{
    CollectList(problems, field, blockPath);
    continue;
}

if (DeveloperConfigField.BlockFieldsOf(declared.PropertyType) is { } nested)
```

and further down:

```csharp
if (field.Value is { } value && !BindsTo(declared.PropertyType, value))
{
    problems.Add(NotBindable(field, blockPath, declared.PropertyType, value));
}
```

- [ ] **Step 5: Build and run the full suite**

```bash
dotnet build -c Release -warnaserror 2>&1 | tail -4
dotnet test -c Release 2>&1 | tail -5
```

Expected: 0 warnings, and the identical passing count from Step 1.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Carry the declaring property through the config shape (#236)"
```

---

### Task 3: The attribute, and the refusal

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/NoSurroundingWhitespaceAttribute.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/KubernetesDeveloperConfig.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/KubernetesBackingServiceDeveloperConfig.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs` (`CollectBlock`,
  plus a new message builder beside `Blank`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigValidatorTests.cs`

**Interfaces:**
- Consumes: `PropertyInfo`-valued `BlockFields` from Task 2; `Escaped` from Task 1.
- Produces: `NoSurroundingWhitespaceAttribute(string receiver)` with `string? IfDeliberate { get; init; }`;
  a private `SurroundedByWhitespace(IConfigurationSection field, string block, string value,
  NoSurroundingWhitespaceAttribute declared)` message builder. Task 4 refines its remedy; Task 5
  asserts against the attribute's placement.

- [ ] **Step 1: Add the backing-service test seam**

`DeveloperConfigValidatorTests` has no backing-service test at all. Add beside `Load`:

```csharp
/// <summary>
/// The backing-service half of the same walk. Separate from <see cref="Load"/> because the two
/// shapes are validated through different entry points — a backing service is declared by the
/// AddBackingService call rather than by the catalog, so nothing here goes looking for one.
/// </summary>
/// <remarks>
/// Resolved through <c>BackingServicesFor</c> rather than <c>ResolveBackingService</c>: the latter
/// answers a name it does not know with a default instead of failing, so a test built on it could
/// pass while asserting nothing.
/// </remarks>
private static ServiceSourcesConfigurationException LoadBackingService(string json)
{
    var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));
    return Assert.Throws<ServiceSourcesConfigurationException>(
        () => ServiceSourcesConfigCache.BackingServicesFor(builder));
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
/// <remarks>
/// The five fields this rule covers, each padded three ways. Written as a theory rather than as
/// fifteen tests because the symmetry is the point of #236: a service and a backing service carry
/// the same two keys, and a rule that reached one and not the other would be the defect rather
/// than the fix.
/// <para>
/// Every row asserts the refusal is *this* rule's, not another complaint that happens to throw:
/// a row naming a field that does not exist would otherwise trip 'is not a valid key' and pass.
/// </para>
/// </remarks>
[Theory]
[InlineData("services", "orders", "context", " dev-west", "dev-west")]
[InlineData("services", "orders", "context", "dev-west ", "dev-west")]
[InlineData("services", "orders", "context", "  dev-west  ", "dev-west")]
[InlineData("services", "orders", "namespace", " orders", "orders")]
[InlineData("services", "orders", "namespace", "orders ", "orders")]
[InlineData("services", "orders", "namespace", " orders ", "orders")]
[InlineData("backingServices", "orders-db", "context", " dev-west", "dev-west")]
[InlineData("backingServices", "orders-db", "context", "dev-west ", "dev-west")]
[InlineData("backingServices", "orders-db", "context", " dev-west ", "dev-west")]
[InlineData("backingServices", "orders-db", "namespace", " orders", "orders")]
[InlineData("backingServices", "orders-db", "namespace", "orders ", "orders")]
[InlineData("backingServices", "orders-db", "namespace", " orders ", "orders")]
[InlineData("backingServices", "orders-db", "service", " orders-pg", "orders-pg")]
[InlineData("backingServices", "orders-db", "service", "orders-pg ", "orders-pg")]
[InlineData("backingServices", "orders-db", "service", " orders-pg ", "orders-pg")]
public void Validate_KubectlNameWithSurroundingWhitespace_IsRefusedWithTheSpellingThatWorks(
    string section, string entry, string field, string written, string expected)
{
    var json = $$"""
        { "{{section}}": { "{{entry}}": {
            "source": "kubernetes",
            "kubernetes": { "{{field}}": "{{written}}" } } } }
        """;

    var ex = section == "services" ? Load(json) : LoadBackingService(json);

    Assert.Contains($"'{field}' in the 'kubernetes' block is set to '{written}'", ex.Message);
    Assert.Contains("kubectl", ex.Message);
    Assert.Contains($"Set it to '{expected}'.", ex.Message);

    // This rule's complaint and no other: a row naming a field that does not exist would be
    // answered by NotValidInBlock, and a second problem would switch Failure to its list wording.
    Assert.DoesNotContain("is not a valid key", ex.Message);
    Assert.DoesNotContain("problems with the entry", ex.Message);
}

/// <remarks>
/// A context is the one opted-in field whose padding may have been meant — a kubeconfig context
/// name is an arbitrary key and 'kubectl config set-context " padded "' succeeds. Telling that
/// developer to write the trimmed spelling would send them to a context that may not exist, so the
/// message carries the way out. No other field can be in that position, so no other field pays for
/// the sentence.
/// </remarks>
[Fact]
public void Validate_PaddedContext_NamesTheRenameThatKeepsIt()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "context": " dev-west" } } } }
        """);

    Assert.Contains("kubectl config rename-context", ex.Message);
}

/// <remarks>
/// The backing-service half of the same payload. The attribute is applied by hand on each of the
/// two unrelated config types, so nothing structural keeps them in step — which is the drift #236
/// exists to prevent, and the reason this is asserted on both shapes rather than once.
/// </remarks>
[Fact]
public void Validate_PaddedContextOnABackingService_AlsoNamesTheRename()
{
    var ex = LoadBackingService("""
        { "backingServices": { "orders-db": {
            "source": "kubernetes",
            "kubernetes": { "context": " dev-west" } } } }
        """);

    Assert.Contains("kubectl config rename-context", ex.Message);
}

/// <remarks>
/// A tab and a non-breaking space are the two paddings a developer cannot see, and the second is
/// what a paste out of a browser or a document leaves behind. Both are whitespace, so both fire
/// this rule — and the message has to spell them out, since echoed as themselves they would leave
/// the reader looking at a value that appears to be exactly what they wrote.
/// </remarks>
[Theory]
[InlineData("\t", "\\t")]
[InlineData("\u00a0", "\\u00a0")]
public void Validate_PaddingThatCannotBeSeen_IsSpelledOutInTheMessage(string padding, string spelled)
{
    var ex = Load($$"""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": "{{padding}}orders" } } } }
        """);

    Assert.Contains(spelled, ex.Message);
    Assert.Contains("Set it to 'orders'.", ex.Message);
}

/// <remarks>
/// The suffix every complaint in this file carries. It is what tells a developer the value need not
/// have come from the file at all — an environment variable sets the same key — and dropping it
/// from this one message would otherwise pass every other test here.
/// </remarks>
[Fact]
public void Validate_PaddedNamespace_NamesTheConfigurationKeyItCameFrom()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": " orders" } } } }
        """);

    Assert.Contains("ServiceSources:Services:orders:kubernetes:namespace", ex.Message);
}

[Fact]
public void Validate_PaddedNamespace_DoesNotOfferTheContextRename()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": " orders" } } } }
        """);

    Assert.DoesNotContain("rename-context", ex.Message);
}

/// <remarks>
/// Blank runs first and has to: a value of nothing but spaces satisfies both rules, and the one
/// that names the empty spelling is what the developer reaching for it needs.
/// </remarks>
[Fact]
public void Validate_WhitespaceOnlyContext_KeepsTheBlankComplaint()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "context": "  " } } } }
        """);

    Assert.Contains("whitespace rather than a value", ex.Message);
    Assert.DoesNotContain("Set it to ''", ex.Message);
}

/// <remarks>
/// A context name may contain a space — 'kubectl config set-context "my dev ctx"' succeeds — so a
/// rule about whitespace anywhere in the value would refuse a working configuration. Only the ends
/// are the package's business.
/// </remarks>
[Fact]
public void Validate_ContextWithInteriorSpace_IsAccepted()
{
    var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "context": "my dev ctx", "port": 8080 } } } }
        """));

    var resolved = ServiceSourcesConfigCache.ResolveService(builder, "orders");

    Assert.Equal("my dev ctx", resolved.DeveloperConfig.Kubernetes.Context);
}
```

- [ ] **Step 3: Run them and watch them fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~KubectlName|FullyQualifiedName~PaddedContext|FullyQualifiedName~PaddedNamespace|FullyQualifiedName~InteriorSpace|FullyQualifiedName~WhitespaceOnlyContext"
```

Expected: the theory rows and both `Padded…` tests FAIL (no exception is thrown at all — the value
binds today). `ContextWithInteriorSpace` and `WhitespaceOnlyContext` should already PASS; if either
fails, stop and find out why before writing any implementation.

- [ ] **Step 4: Create the attribute**

`src/Aspire.Hosting.ServiceSources/Config/NoSurroundingWhitespaceAttribute.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Marks a developer-config field whose value is handed to something outside this process exactly
/// as written, so that whitespace at either end of it is part of the name being looked up.
/// </summary>
/// <remarks>
/// An opt-in rather than a rule for the whole file, because this file deliberately passes values
/// through as the developer wrote them: whitespace may be real in a <c>local.path</c> or in an
/// argument of a <c>prepare.command</c>, and trimming those would be rewriting what someone meant.
/// It is only for a value that names a thing on the other side of a CLI, where a surrounding space
/// cannot be part of the name in practice.
/// <para>
/// The receiver is required rather than assumed, because it is the fact that justifies the rule —
/// "who gets this value as written?" is the question the next field to carry this has to answer,
/// and the message is built out of the answer rather than hardcoding one tool's name.
/// </para>
/// </remarks>
/// <param name="receiver">
/// What receives the value verbatim, as it is named in the message — <c>kubectl</c>.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class NoSurroundingWhitespaceAttribute(string receiver) : Attribute
{
    /// <inheritdoc cref="NoSurroundingWhitespaceAttribute(string)" path="/param[@name='receiver']"/>
    public string Receiver { get; } = receiver;

    /// <summary>
    /// A sentence appended to the message, for a field where the padding may have been deliberate.
    /// </summary>
    /// <remarks>
    /// Empty for nearly every field: a value that cannot legally carry surrounding whitespace has
    /// nothing to add beyond the spelling that works. It exists for the one field that can — a
    /// kubeconfig context name is an arbitrary key, so the developer reading this may have meant
    /// it, and telling them to write the trimmed spelling would send them to a context that may
    /// not exist.
    /// </remarks>
    public string? IfDeliberate { get; init; }
}
```

- [ ] **Step 5: Apply it to the five properties**

`KubernetesDeveloperConfig.cs`:

```csharp
/// <summary>The kubectl context the port-forward runs against. Required by this source.</summary>
[NoSurroundingWhitespace(
    "kubectl",
    IfDeliberate = "If this context really is named that, rename it with "
        + "'kubectl config rename-context'.")]
public string? Context { get; set; }

/// <summary>The namespace the service lives in. Defaults to <c>default</c>.</summary>
/// <remarks>A namespace is a DNS-1123 label, so a space is not legal anywhere in one.</remarks>
[NoSurroundingWhitespace("kubectl")]
public string? Namespace { get; set; }
```

`KubernetesBackingServiceDeveloperConfig.cs` — the same two attributes on its own `Context` and
`Namespace`, plus:

```csharp
/// <remarks>A Service name is a DNS-1035 label, so a space is not legal anywhere in one.</remarks>
[NoSurroundingWhitespace("kubectl")]
public string? Service { get; set; }
```

Keep each property's existing `<summary>`/`<remarks>` prose; these attributes and the one-line
remarks above are additions to it.

- [ ] **Step 6: Add the branch to `CollectBlock`**

Immediately after the `Blank` branch and before the `BindsTo` check:

```csharp
// After Blank, which takes a value that is *entirely* whitespace: that value satisfies this rule
// too, and the complaint naming the empty spelling is the one its author was reaching for. Before
// BindsTo, so the field's type stays out of the sentence — the same reason Blank is kept apart
// from NotBindable.
if (field.Value is { } verbatim
    && declared.GetCustomAttribute<NoSurroundingWhitespaceAttribute>() is { } receiver
    && verbatim != verbatim.Trim())
{
    problems.Add(SurroundedByWhitespace(field, blockPath, verbatim, receiver));
    continue;
}
```

- [ ] **Step 7: Add the message builder**

Beside `Blank` in the message region:

```csharp
/// <summary>
/// The error for a value whose surrounding whitespace is part of a name something outside this
/// process will look up.
/// </summary>
/// <remarks>
/// It says nothing about what such a name may or may not contain, because that varies by field and
/// getting it wrong prints a false claim at the one developer it is wrong for: a kubeconfig context
/// name really can carry a space. What is true of every field carrying
/// <see cref="NoSurroundingWhitespaceAttribute"/> is that the value travels as written, so that is
/// what the sentence says, with the two spellings side by side — which is what makes a plain space
/// visible, since <see cref="Escaped"/> leaves one as itself.
/// </remarks>
private static string SurroundedByWhitespace(
    IConfigurationSection field, string block, string value, NoSurroundingWhitespaceAttribute declared)
{
    var remedy = value.Trim();

    return $"'{field.Key}' in the '{block}' block is set to {Escaped(value)}, and "
        + $"{declared.Receiver} is given it exactly as written — so {declared.Receiver} looks for "
        + $"{Escaped(value)} and not {Escaped(remedy)}. Set it to {Escaped(remedy)}."
        + (declared.IfDeliberate is { } deliberate ? $" {deliberate}" : "")
        + SetAt(field);
}
```

`using System.Reflection;` is already in the validator from Task 2.

- [ ] **Step 8: Run the tests**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 --filter FullyQualifiedName~Config
```

Expected: PASS, all 15 theory rows included.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "Refuse surrounding whitespace on a kubectl-facing name (#236)"
```

---

### Task 4: A remedy the developer can actually type

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs`
  (`SurroundedByWhitespace`, plus one private helper)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigValidatorTests.cs`

**Interfaces:**
- Consumes: `SurroundedByWhitespace` from Task 3, `Escaped` from Task 1.
- Produces: nothing later tasks depend on.

**Why separate:** Task 3's remedy is `value.Trim()`, which is right for every value made only of
whitespace and text. This task handles the value that keeps an invisible character after trimming —
where `Set it to 'U+FEFForders'` would be advice that cannot be followed, and worse, `U+FEFF` is a
live JSON escape, so copying it back into the file reproduces the identical broken value with no
whitespace left to complain about.

- [ ] **Step 1: Write the failing tests**

```csharp
/// <remarks>
/// The trap this rule exists to close, re-created by the rule itself if the remedy is computed by
/// trimming whitespace alone. A BOM is not whitespace, so it survives Trim() — and 'U+FEFF' is a
/// valid JSON escape, so a developer copying the proposed spelling back into the file writes the
/// same broken value, this time with no whitespace to trigger any message at all.
/// </remarks>
[Fact]
public void Validate_PaddingAroundAnInvisibleCharacter_ProposesASpellingThatWorks()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": " \uFEFForders" } } } }
        """);

    // What arrived is shown with the BOM spelled out...
    Assert.Contains("\\ufeff", ex.Message);

    // ...and what to write carries neither the space nor the BOM.
    Assert.Contains("Set it to 'orders'.", ex.Message);
}

/// <remarks>
/// Blank does not take this one: the BOM is not whitespace, so the value is not
/// IsNullOrWhiteSpace. Trimming leaves nothing at all, and 'Set it to' has no spelling to name —
/// so the message says what Blank would have said, since that is what the value is.
/// </remarks>
[Fact]
public void Validate_ValueOfNothingButWhitespaceAndInvisibles_NamesTheEmptySpelling()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": " \uFEFF" } } } }
        """);

    // The distinguishing clause, not just "empty value" — Blank's message ends with that same
    // sentence, so asserting on it alone would pass even if this branch never ran.
    Assert.Contains("characters with no glyph rather than a value", ex.Message);
    Assert.DoesNotContain("Set it to ''", ex.Message);
}

/// <remarks>
/// The boundary this rule deliberately stops at: a value padded only with invisible characters and
/// no whitespace at all is not refused. TrimUnseeable is one edit away from becoming the trigger
/// rather than the remedy, which would widen the rule past what #236 asked for without anyone
/// noticing. Pinned so that widening it is a decision.
/// </remarks>
[Fact]
public void Validate_InvisiblePaddingWithNoWhitespace_IsNotRefused()
{
    var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": "\uFEFForders", "context": "dev", "port": 8080 } } } }
        """));

    var resolved = ServiceSourcesConfigCache.ResolveService(builder, "orders");

    Assert.Equal("\ufefforders", resolved.DeveloperConfig.Kubernetes.Namespace);
}

/// <remarks>
/// An invisible in the middle survives the remedy, so the remedy cannot be typed from the message.
/// Saying 'Set it to' anyway would be the copy-paste trap again, one character further in.
/// </remarks>
[Fact]
public void Validate_InvisibleInsideTheValue_AsksForItToBeRetypedRatherThanCopied()
{
    var ex = Load("""
        { "services": { "orders": {
            "source": "kubernetes",
            "kubernetes": { "namespace": " ord\uFEFFers" } } } }
        """);

    Assert.Contains("retype", ex.Message);
    Assert.DoesNotContain("Set it to 'ord\\ufeffers'.", ex.Message);
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~PaddingAroundAnInvisible|FullyQualifiedName~NothingButWhitespaceAndInvisibles|FullyQualifiedName~InvisibleInsideTheValue"
```

Expected: FAIL. Be precise about which assertion fails in each, so a wrong failure is not mistaken
for the right one: the first fails on `Set it to 'orders'.` (Task 3's remedy is `value.Trim()`, which
leaves the BOM, so the message proposes `'\ufefforders'`); the second fails on its first assertion,
since trimming whitespace alone leaves `'\ufeff'` rather than nothing; the third fails on `retype`.

- [ ] **Step 3: Add the trimming helper**

```csharp
/// <summary>
/// Whether <paramref name="c"/> is a character a reader cannot see: whitespace, a control
/// character, or one of Unicode's <see cref="UnicodeCategory.Format"/> characters.
/// </summary>
/// <remarks>
/// The same line <see cref="Escaped"/> draws, and for the same reason: these are the characters a
/// developer cannot tell apart from nothing. It stops short of a combining mark, which is invisible
/// too and is a real thing to write — a decomposed accented letter carries one.
/// </remarks>
private static bool IsUnseeable(char c) =>
    char.IsWhiteSpace(c)
    || char.IsControl(c)
    || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

/// <summary>
/// <paramref name="value"/> without the characters at either end that a reader cannot see.
/// </summary>
/// <remarks>
/// Used for the spelling the message proposes rather than for the rule that fires it. The rule stays
/// about whitespace, which is what #236 is about; the remedy has to be a value the developer can
/// type, and one still carrying a byte-order mark is not — the more so because <c>U+FEFF</c> is a
/// JSON escape, so copying it back in reproduces the value the message is complaining about.
/// </remarks>
private static string TrimUnseeable(string value)
{
    var start = 0;
    var end = value.Length;

    while (start < end && IsUnseeable(value[start]))
    {
        start++;
    }

    while (end > start && IsUnseeable(value[end - 1]))
    {
        end--;
    }

    return value[start..end];
}
```

- [ ] **Step 4: Use it in the message**

Replace `SurroundedByWhitespace`'s body:

```csharp
private static string SurroundedByWhitespace(
    IConfigurationSection field, string block, string value, NoSurroundingWhitespaceAttribute declared)
{
    var remedy = TrimUnseeable(value);
    var opening = $"'{field.Key}' in the '{block}' block is set to {Escaped(value)}";

    // Nothing but whitespace and characters with no glyph. Blank did not take it — a byte-order
    // mark is not whitespace — but what it would have said is what this value needs: there is no
    // spelling to propose, and the empty value is the gesture for a field nobody meant to set.
    if (remedy.Length == 0)
    {
        return $"{opening}, which is whitespace and characters with no glyph rather than a value. "
            + "Set it to an empty value to leave the field unset."
            + SetAt(field);
    }

    var mechanism = $", and {declared.Receiver} is given it exactly as written — so "
        + $"{declared.Receiver} looks for {Escaped(value)} and not {Escaped(remedy)}. ";

    // A remedy carrying an invisible character of its own cannot be typed out of this message, and
    // must not be offered as though it could: `U+FEFF` is a JSON escape, so a reader copying it
    // back into the file writes the value being complained about, with no whitespace left for
    // anything to catch.
    var fix = Escaped(remedy) == $"'{remedy}'"
        ? $"Set it to {Escaped(remedy)}."
        : $"{Escaped(remedy)} still carries a character with no glyph of its own, so retype the "
          + "value rather than copying it from here.";

    return opening + mechanism + fix
        + (declared.IfDeliberate is { } deliberate ? $" {deliberate}" : "")
        + SetAt(field);
}
```

- [ ] **Step 5: Run the config suite**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 --filter FullyQualifiedName~Config
```

Expected: PASS, including Task 3's fifteen rows unchanged.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Propose a spelling the developer can type (#236)"
```

---

### Task 5: Pin the exclusions and the attribute's placement

**Files:**
- Test only: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigValidatorTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

**Why:** the *Scope* section of the design is a set of decisions, not a property of the mechanism.
Without these, the next contributor adds the attribute to `connectionString` and nothing objects.

- [ ] **Step 0: Add the using**

`System.Reflection` is not among the test project's implicit usings, and
`PropertyInfo.GetCustomAttribute<T>()` is an extension on `System.Reflection.CustomAttributeExtensions`.
Without it the guard test is a `CS1061`. Add `using System.Reflection;` and `using System.Globalization;`
to the top of `DeveloperConfigValidatorTests.cs`.

- [ ] **Step 1: Write the tests**

```csharp
/// <remarks>
/// The fields this rule deliberately does not reach. Whitespace may be real in a path or in a
/// command's argument, a connection string may carry it inside a quoted value, and a scheme is
/// already trimmed where it is read — a closed set of two values, so trimming cannot resolve to
/// the wrong one. Each is a decision the design made rather than something the mechanism
/// guarantees, so each is pinned.
/// </remarks>
[Theory]
[InlineData("""{ "services": { "orders": { "source": "local", "local": { "path": "/src/orders " } } } }""", "path", "/src/orders ")]
[InlineData("""{ "services": { "orders": { "source": "local", "local": { "path": "/src/o", "prepare": { "command": ["mvn", " -Pprod"] } } } } }""", "command", " -Pprod")]
[InlineData("""{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev", "port": 8080, "scheme": " https" } } } }""", "scheme", " https")]
[InlineData("""{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev", "port": " 8080" } } } }""", "port", "8080")]
public void Validate_FieldThatDidNotOptIn_StillTakesASurroundedValue(string json, string field, string expected)
{
    var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));

    var resolved = ServiceSourcesConfigCache.ResolveService(builder, "orders").DeveloperConfig;

    // Not merely "it did not throw": the value has to arrive with its whitespace intact. Somebody
    // "fixing" an exclusion by trimming it at the point of use would pass a no-throw test, and that
    // is the change this pins against.
    var arrived = field switch
    {
        "path" => resolved.Local.Path,
        "command" => resolved.Local.Prepare.Command![1],
        "scheme" => resolved.Kubernetes.Scheme,
        _ => resolved.Kubernetes.Port?.ToString(CultureInfo.InvariantCulture),
    };

    Assert.Equal(expected, arrived);
}

[Fact]
public void Validate_BackingServiceConnectionString_StillTakesATrailingSpace()
{
    var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
        { "backingServices": { "orders-db": {
            "source": "direct",
            "direct": { "connectionString": "Host=db;Password=hunter2 " } } } }
        """));

    var configured = ServiceSourcesConfigCache.BackingServicesFor(builder);

    Assert.Equal("Host=db;Password=hunter2 ", configured["orders-db"].Direct.ConnectionString);
}

/// <remarks>
/// The kubernetes block's own connectionString, which sits beside three fields that *did* opt in
/// and is the one a later contributor is likeliest to add the attribute to. A connection string may
/// carry trailing whitespace inside a quoted value, which is why #236 rules it out by name.
/// </remarks>
[Fact]
public void Validate_KubernetesConnectionString_StillTakesATrailingSpace()
{
    var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory("""
        { "backingServices": { "orders-db": {
            "source": "kubernetes",
            "kubernetes": { "connectionString": "Host=localhost;Port=${port};Pwd=x " } } } }
        """));

    var configured = ServiceSourcesConfigCache.BackingServicesFor(builder);

    Assert.Equal(
        "Host=localhost;Port=${port};Pwd=x ", configured["orders-db"].Kubernetes.ConnectionString);
}

/// <remarks>
/// Two failure modes, and membership in BlockFields catches only one of them. BlockFieldsOf keys
/// every configurable property — including a list and a nested block — and CollectBlock returns on
/// both before the whitespace check is reached, so an attribute on PrepareDeveloperConfig.Command
/// would be completely inert while still appearing in the shape. The carrier has to be a scalar
/// leaf, and that is what is asserted.
/// <para>
/// This cannot guard against a property being moved between block types: attributes travel with
/// the property. What it guards is a carrier the walk never reaches, and a field quietly losing
/// the rule.
/// </para>
/// </remarks>
[Fact]
public void Shape_EveryFieldCarryingTheRuleIsAScalarTheWalkReaches()
{
    var expected = new[]
    {
        (typeof(KubernetesDeveloperConfig), "Context"),
        (typeof(KubernetesDeveloperConfig), "Namespace"),
        (typeof(KubernetesBackingServiceDeveloperConfig), "Context"),
        (typeof(KubernetesBackingServiceDeveloperConfig), "Namespace"),
        (typeof(KubernetesBackingServiceDeveloperConfig), "Service"),
    };

    // Descends the way CollectBlock does. BlockFields is one level deep — it is built from the
    // entry type's own block properties — so a query over it alone cannot see local.prepare at all:
    // an attribute on prepare.mode would be live and unpinned, and one on prepare.command inert and
    // unpinned. Neither is something this test may be blind to.
    static IEnumerable<PropertyInfo> Leaves(IReadOnlyDictionary<string, PropertyInfo> fields) =>
        fields.Values.SelectMany(field =>
            DeveloperConfigField.BlockFieldsOf(field.PropertyType) is { } nested
                ? Leaves(nested).Prepend(field)
                : [field]);

    var carriers =
        from shape in new[] { DeveloperConfigShape.Service, DeveloperConfigShape.BackingService }
        from block in shape.BlockFields
        from field in Leaves(block.Value)
        where field.GetCustomAttribute<NoSurroundingWhitespaceAttribute>() is not null
        select (field.DeclaringType!, field.Name);

    Assert.Equal(expected.OrderBy(c => $"{c.Item1}.{c.Item2}"), carriers.Distinct().OrderBy(c => $"{c.Item1}.{c.Item2}"));

    foreach (var (declaring, name) in carriers.Distinct())
    {
        var property = declaring.GetProperty(name)!;

        Assert.False(
            DeveloperConfigField.IsList(property.PropertyType),
            $"{declaring.Name}.{name} is a list, which CollectBlock hands to CollectList before the check.");
        Assert.Null(
            DeveloperConfigField.BlockFieldsOf(property.PropertyType));
    }
}
```

- [ ] **Step 2: Run them**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 --filter FullyQualifiedName~Config
```

Expected: PASS. If `Shape_EveryFieldCarryingTheRuleIsAScalarTheWalkReaches` fails, an attribute is
on a property the design did not name — fix the attribute, not the test.

- [ ] **Step 3: Prove the guard bites**

In a **scratch copy of the repo**, not this working tree, add `[NoSurroundingWhitespace("mvn")]` to
`PrepareDeveloperConfig.Command` and re-run the guard test. It must fail — on the set equality, and
on `Assert.False(IsList(...))` once the row is expected. `Command` is a `string[]` two levels down,
so this is precisely the case a `BlockFields`-only query cannot see; if the test passes, the `Leaves`
recursion is wrong and everything the guard claims is worthless.

```bash
SCRATCH=$(mktemp -d) && git worktree add "$SCRATCH" HEAD
# edit PrepareDeveloperConfig.Command there, run the filtered test, expect FAIL
git worktree remove --force "$SCRATCH"
```

Done in a scratch copy because Step 4 commits with `git add -A`: a mutation left behind in `src/`
would be committed silently, and a squash-merge lands it.

- [ ] **Step 4: Commit**

Confirm no production code crept in from Step 3 before committing a test-only task:

```bash
git diff --stat -- src/   # must be empty
git add -A && git commit -m "Pin the fields this rule deliberately does not reach (#236)"
```

---

### Task 6: Changelog

**Files:**
- Modify: `CHANGELOG.md` (under `## [Unreleased]`)

- [ ] **Step 1: Write the entry**

Under `### Changed` in `## [Unreleased]` — creating that heading if it is not there, in the order
the file already uses:

```markdown
- **A `kubernetes` `context`, `namespace` or `service` with surrounding whitespace is now refused**
  ([#236]). These values are handed to `kubectl` exactly as written, so a leading or trailing space
  is part of the name it looks for — `--context " dev-west"` matches no context, and a padded
  namespace is looked up as a namespace that cannot exist. Both sources are covered: a service's
  `kubernetes` block and a backing service's.

  **An AppHost whose file carries one of these will now fail to start where it used to start and
  fail later**, in `kubectl`'s own output in the dashboard. The message names the entry, the key,
  the value with its whitespace spelled out, and the spelling to write instead. Nothing is trimmed
  for you: a kubeconfig context name may legitimately carry surrounding whitespace, and silently
  rewriting one would select a different context — a different cluster and different credentials —
  without saying so. If yours really is named that, `kubectl config rename-context` is the way out.

  `connectionString`, `local.path` and a `prepare.command` argument are untouched, where whitespace
  may be part of the value.
```

Add the `[#236]` link definition beside the others at the foot of the file.

- [ ] **Step 2: Check the surrounding register**

```bash
sed -n '/## \[Unreleased\]/,/^## \[0/p' CHANGELOG.md | head -60
```

Match the heading order and link style already there.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "Note the new refusal in the changelog (#236)"
```

---

### Task 7: Full verification

- [ ] **Step 1: Rebase onto the latest main**

```bash
git fetch origin && git rebase origin/main
```

- [ ] **Step 2: Run every leg CI runs that can run locally**

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 warnings, 0 failures, across net8.0 / net9.0 / net10.0. Paste the real output — a green
claim without it is not a green claim.

- [ ] **Step 3: Name what did not run**

The container, config-layers and local-source smoke tests need Docker or the network;
`typecheck-typescript` and `verify-invariants` do not touch this change. Say so explicitly rather
than letting silence imply they passed.

- [ ] **Step 4: Shape the history for a squash merge**

This repo squashes, and a squash takes the **commit message**, not the PR body. Six per-task commits
would land under whichever message is last — the changelog one — which describes the smallest part
of the work. Either squash the branch into one commit whose message is the thing that should land on
`main`, or make the final commit message the one that describes the change as a whole.

- [ ] **Step 5: Record what the design deferred**

The spec defers two things to a comment on #236 and offers one alternative on the pull request.
Neither is code, and both are promises this branch made:

- comment on #236 with the two deferred questions — whether the catalog's `kubernetes.service`
  should get whitespace diagnostics of its own, and the invisible-character gap (a value padded only
  with characters that are not whitespace is not refused, and the `Format`/`Control` line the
  escaping draws leaves a residue of its own);
- state in the PR body that candidate (1) — trimming in `KubectlPortForward.Args` — remains
  available, that it is roughly two lines, and that it covers the `context`/`namespace` half of this
  rule but not `service`, whose value on the service side comes from the catalog.

---

## Self-Review

**Spec coverage.** *What kubectl actually does* and *The ticket's premise* are findings, not work.
*Decision* → Tasks 3-4. *The message* → Task 3 Step 7, refined in Task 4. *When the remedy is not
something the developer can type* → Task 4. *The opt-in travels on the property* → Task 3 Steps 4-5.
*The dictionary carries the property* → Task 2. *Where the check sits in the walk* → Task 3 Step 6.
*Zero-width characters* → Tasks 1 and 4. *Nothing is trimmed anywhere* → no task, by construction;
Task 5 pins it. *Option injection* → no code, it is a recorded finding. *Scope* → Task 3 Step 5
(in) and Task 5 (out). *Testing* → Tasks 3-5. *Delivery* → Tasks 6-7.

**Placeholders.** None: every step carries the code or the command it needs.

**Type consistency.** `BlockFieldsOf` returns `IReadOnlyDictionary<string, PropertyInfo>?` in Task 2
and is read as `PropertyInfo` in Tasks 3 and 5. `NoSurroundingWhitespaceAttribute(string receiver)`
with `IfDeliberate` is declared in Task 3 Step 4 and used with exactly those names in Steps 5-7 and
in Task 5. `SurroundedByWhitespace`'s four parameters are the same in Task 3 Step 7 and Task 4
Step 4. `TrimUnseeable`/`IsUnseeable` are declared and used only in Task 4.

**Three deviations from the spec, all deliberate and all listed rather than left to be found:**

1. The spec's *Testing* section asks that the remedy be fed back through `Load` and assert it
   resolves. Task 4 asserts the proposed spelling directly (`Set it to 'orders'.`) instead: the
   remedy is a BOM-free literal in the test source, so a remedy that kept the BOM renders
   `Set it to '\ufefforders'.` and fails the assertion. `TrimUnseeable`'s output can never carry
   whitespace at its boundaries, so the round-trip could not fail where this passes. The boundary
   test added to Task 4 covers the case a future edit to `TrimUnseeable` would open.
2. The spec's *Delivery* says one commit; the plan makes six, one per task, so each carries its own
   test cycle and can be rejected on its own. Task 7 Step 4 reconciles that with the squash merge.
3. The spec's guard-test paragraph said an inert attribute on `PrepareDeveloperConfig.Command` would
   "still appear in the shape". It would not — `BlockFields` is one level deep — so the guard here
   recurses through nested blocks, and the spec has been corrected rather than followed.
