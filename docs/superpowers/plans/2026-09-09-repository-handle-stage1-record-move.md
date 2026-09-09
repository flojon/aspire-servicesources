# Repository Handle Stage 1 — `RepositoryDefinition` Record Move, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:test-driven-development. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the repository fields their own record — `RepositoryDefinition { Url, DefaultRef,
Prepare, CheckoutName }` — referenced by `ServiceDefinition` instead of inlined into it, with
**every record anonymous** (`CheckoutName` always equal to the owning service's name). No new
public API, no new yaml, **no behaviour change** — this stage only moves where three fields live.
Criterion 6 (an existing AppHost's checkout path is byte-identical) is proved by the existing test
suite passing unmodified in behaviour, recompiled against the new shape.

**Out of scope, deliberately (Stage 2):** `AddRepository`/`RepositoryBuilder`/`WithSharedRepository`,
yaml `repositories:`/`repositoryRef:`, the third `DeveloperConfigShape`, and — the part easiest to
reach for by mistake — re-keying `LocalGitCheckout.ManagedRepoRoot` / `LocalCheckoutPrefetch`'s
dictionaries onto `CheckoutName`. `CheckoutName` is added to the record here and read by nothing
yet; every call site that builds a checkout path keeps passing the raw `serviceName` string it does
today. Re-keying those is what actually closes #66, and it happens in Stage 2 once the field exists
to key on.

**Spec:** `docs/superpowers/specs/2026-09-08-repository-handle-design.md` (Accepted; only the
Stage-2-only naming question is open) — see "The domain type" and the Staging table. Findings:
`docs/superpowers/specs/2026-09-08-repository-handle-findings.md`, finding 1 (identity can't come
from the URL) and finding 2 (the checkout directory name is a compatibility surface — this is why
`CheckoutName` defaults to the service name rather than an opaque id).

**Tech stack:** C# (`Aspire.Hosting.ServiceSources`, `net8.0`/`net9.0`/`net10.0`), xUnit.

## Global constraints

- **`-warnaserror`** on every build, matching `ci.yml`'s `🔨 build, test & pack` job.
- **`RepositoryDefinition.CheckoutName` is set by the producer, never left for a consumer to
  derive.** `ServiceMetadata.ToDefinition` and `ServiceDefinitionBuilder.Build` both already know
  their own service name; that is where `CheckoutName` is minted. Nothing downstream computes it
  from anything else — that keeps Stage 2's grouped case a change to *what a producer passes*,
  never to how a consumer reads the field.
- **The compiler is the sweep.** Once `ServiceDefinition.Repository` changes type, every read site
  — production and test — fails to build. Do not try to enumerate every test call site by hand
  first; change the model (Task 2), then work the build errors to zero (Task 4). Recon for this
  plan found the production sites (Task 3) and the two files with a structural reflection guard
  (Task 2's own scope); the rest of the test sweep is intentionally left to the compiler because a
  hand-built list drifts the moment another PR touches these files.
- **No CHANGELOG entry.** No public API changes, no behaviour changes — nothing a consumer of this
  package can observe. Matches the repo's own rule ("Fixed" covers released bugs; a change that
  behaves differently gets "Changed" — neither applies to an internal, behaviour-preserving move).

---

## Task 1: `RepositoryDefinition`

Implements: design "The domain type".

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/Catalog/RepositoryDefinition.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/RepositoryDefinitionTests.cs`

**Interfaces:**
- Produces: `RepositoryDefinition` (internal sealed class) — `Url` (required string), `DefaultRef`
  (string?), `Prepare` (`PrepareMetadata?`), `CheckoutName` (required string). No behaviour, no
  methods — a plain data holder, exactly like `ServiceDefinition` itself.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/RepositoryDefinitionTests.cs
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Config.Catalog;

public class RepositoryDefinitionTests
{
    [Fact]
    public void Properties_RoundTrip()
    {
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "once" };

        var repository = new RepositoryDefinition
        {
            Url = "https://github.com/example/repo",
            DefaultRef = "main",
            Prepare = prepare,
            CheckoutName = "orders",
        };

        Assert.Equal("https://github.com/example/repo", repository.Url);
        Assert.Equal("main", repository.DefaultRef);
        Assert.Same(prepare, repository.Prepare);
        Assert.Equal("orders", repository.CheckoutName);
    }
}
```

- [ ] **Step 2: Run it, confirm it fails to compile** (the type doesn't exist)

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~RepositoryDefinitionTests"`

- [ ] **Step 3: Implement**

```csharp
// src/Aspire.Hosting.ServiceSources/Config/Catalog/RepositoryDefinition.cs
namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// One repository: the "local" source's block, referenced by every <see cref="ServiceDefinition"/>
/// that names it. Every service has one — an ungrouped service gets its own anonymous instance,
/// minted by whichever producer declared it (<see cref="ServiceMetadata.ToDefinition"/> or
/// <see cref="Catalog.ServiceDefinitionBuilder.Build"/>), so there is no null case downstream.
/// Identity is the instance itself, never a value compared for equality — see design "The domain
/// type".
/// </summary>
internal sealed class RepositoryDefinition
{
    public required string Url { get; init; }

    public string? DefaultRef { get; init; }

    public PrepareMetadata? Prepare { get; init; }

    /// <summary>
    /// The single directory name a managed checkout of this repository is placed under
    /// (<c>checkouts/&lt;CheckoutName&gt;</c>). For an anonymous (ungrouped) record — every record
    /// in this stage — this is always the owning service's name, which is what keeps every existing
    /// checkout path byte-identical (design finding 2, criterion 6). Nothing reads this field yet;
    /// Stage 2 is what re-keys <c>LocalGitCheckout</c>/<c>LocalCheckoutPrefetch</c> onto it.
    /// </summary>
    public required string CheckoutName { get; init; }
}
```

- [ ] **Step 4: Run the test, confirm it passes**

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/Catalog/RepositoryDefinition.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/RepositoryDefinitionTests.cs
git commit -m "Add RepositoryDefinition (#291)"
```

---

## Task 2: Move `Repository`/`DefaultRef`/`Prepare` off `ServiceDefinition`, update both producers

Implements: design "The domain type" ("Three fields move off `ServiceDefinition`").

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` (`ToDefinition` gains a
  `serviceName` parameter — the caller in Task 3 already has it in scope)
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs` (`Build()`)
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs` (the
  reflection drift guard — see Step 4 below, this is the one test that needs a *designed* update
  rather than a mechanical one)

**Interfaces:**
- Changes: `ServiceDefinition.Repository` from `string` to `RepositoryDefinition` (required);
  `ServiceDefinition.DefaultRef` and `ServiceDefinition.Prepare` **removed** (both now live on
  `RepositoryDefinition`).
- Changes: `ServiceMetadata.ToDefinition(string yamlPath)` → `ToDefinition(string yamlPath, string
  serviceName)`.

- [ ] **Step 1: Update `ServiceDefinition`**

```csharp
// src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs
internal sealed class ServiceDefinition
{
    public required RepositoryDefinition Repository { get; init; }

    public required string Project { get; init; }

    public KubernetesMetadata? Kubernetes { get; init; }

    public UrlMetadata? Url { get; init; }

    public ContainerMetadata? Container { get; init; }

    public required string Kind { get; init; }

    public object? KindOptions { get; init; }

    public required CatalogOrigin Origin { get; init; }
}
```

(Drop the `DefaultRef` and `Prepare` properties and the `<summary>` mentions of them being inline —
update the doc comment's second sentence to say the repository fields live on
`RepositoryDefinition` now.)

- [ ] **Step 2: Update `ServiceMetadata.ToDefinition`**

```csharp
// src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs
public ServiceDefinition ToDefinition(string yamlPath, string serviceName) => new()
{
    Repository = new RepositoryDefinition
    {
        Url = Repository,
        DefaultRef = DefaultRef,
        Prepare = Prepare,
        CheckoutName = serviceName,
    },
    Project = Project,
    Kubernetes = Kubernetes,
    Url = Url,
    Container = Container,
    Kind = Kind,
    KindOptions = KindConfig,
    Origin = CatalogOrigin.FromYaml(yamlPath),
};
```

Also update the one production caller, `Config/ServiceSourcesConfigCache.cs:309`
(`merged[name] = metadata.ToDefinition(yamlPath);`, inside a `foreach (var (name, metadata) in
yamlCatalog.Services)` loop) — `name` is already in scope: `metadata.ToDefinition(yamlPath, name)`.
This is the only production call site of `ToDefinition`; every other call site the build will name
in Task 4 is test-only.

- [ ] **Step 3: Update `ServiceDefinitionBuilder.Build()`**

```csharp
internal ServiceDefinition Build() => new()
{
    Repository = new RepositoryDefinition
    {
        Url = _repository ?? "",
        DefaultRef = _defaultRef,
        Prepare = _prepare,
        CheckoutName = _serviceName,
    },
    Project = _project ?? "",
    Url = _url,
    Container = _container,
    Kubernetes = _kubernetes,
    Kind = _kind ?? LocalKinds.Dotnet,
    KindOptions = _kindOptions,
    Origin = CatalogOrigin.Code,
};
```

- [ ] **Step 4: Update the reflection drift guard**

`ServiceDefinitionTests.cs` has a third, non-reflective test
(`ToDefinition_CopiesEveryServiceMetadataProperty`, lines 32–60) with hardcoded assertions
(`definition.Repository`, `definition.DefaultRef`, `definition.Prepare`) — that one just needs the
mechanical `.Repository.Url`/`.Repository.DefaultRef`/`.Repository.Prepare` substitution, no design
decision, and Task 4's compiler sweep will name it. The two reflection-driven tests are the ones
that need a *designed* update: a structural test (`ServiceMetadataProperties_AllHaveMatchingServiceDefinitionProperty`)
and a value-level companion (`ToDefinition_CopiesEveryPropertyValue_ReflectionDriven`) that walk
`ServiceMetadata`'s public properties and expect a same-named, same-typed property directly on
`ServiceDefinition`. `Repository`/`DefaultRef`/`Prepare` no longer satisfy that — they moved one
level down, and `Repository`'s CLR type changed (`string` on `ServiceMetadata`, `RepositoryDefinition`
on `ServiceDefinition`). This needs a designed update, not a rename entry: extend the guard with a
second map for properties that moved onto `Repository` instead of vanishing:

```csharp
/// <summary>
/// Properties that moved onto <see cref="ServiceDefinition.Repository"/> instead of staying
/// top-level, keyed by their <see cref="ServiceMetadata"/> name, valued by the
/// <see cref="RepositoryDefinition"/> property that now holds them. <see cref="ServiceMetadata.Repository"/>
/// (a URL string) becomes <see cref="RepositoryDefinition.Url"/> — the one rename inside the move.
/// </summary>
private static readonly Dictionary<string, string> MovedToRepository = new()
{
    ["Repository"] = "Url",
    ["DefaultRef"] = "DefaultRef",
    ["Prepare"] = "Prepare",
};
```

And branch both the structural test and the value-level test on membership in this map: for a
`metadataProperty.Name` in `MovedToRepository`, look up the property by that name on
`typeof(RepositoryDefinition)` instead of `typeof(ServiceDefinition)`, and read
`definition.Repository` before getting the value. Everything else in both tests is unchanged — the
loop still walks every `ServiceMetadata` property, so a *fourth* property someone moves onto
`Repository` later without adding it to this map still fails loudly, and a property nobody moves is
still caught by the existing `RenamedProperties`/direct-match path.

- [ ] **Step 5: Build — expect it to fail** across every remaining production and test call site
  that reads `.Repository`/`.DefaultRef`/`.Prepare` off a `ServiceDefinition`, or calls
  `ToDefinition(yamlPath)` with one argument, or constructs `new ServiceDefinition { Repository =
  "...", ... }` directly. That is expected — Task 3 fixes the three production consumers, Task 4
  works every remaining error to zero.

- [ ] **Step 6: Commit** (once Tasks 3–4 also build clean — this task's own diff does not build
  alone, so fold its commit into Task 4's if that is easier to review; either is fine as long as the
  three are committed together or in immediate succession).

---

## Task 3: Update the three production consumers

Implements: design "The domain type" ("`definition.Repository.Url` replaces `definition.Repository`,
everywhere").

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs:309` (the
  `ToDefinition` call site — see Task 2 Step 2, folded in there since it's the same signature change)

Recon against the current tree found exactly these `.Repository`/`.DefaultRef`/`.Prepare` read
sites (no others exist in `src/`):

- [ ] `Git/LocalGitCheckout.cs:325` — `config.Local.Ref ?? definition.DefaultRef` →
  `config.Local.Ref ?? definition.Repository.DefaultRef`
- [ ] `Git/LocalGitCheckout.cs:337` — `RepositoryUrlsMatch(existingOrigin, definition.Repository)` →
  `RepositoryUrlsMatch(existingOrigin, definition.Repository.Url)`
- [ ] `Git/LocalGitCheckout.cs:342` — `GitUrl.Redact(definition.Repository)` →
  `GitUrl.Redact(definition.Repository.Url)`
- [ ] `Git/LocalGitCheckout.cs:399` — same substitution (`displayRepository` local in
  `CloneIntoPlace`)
- [ ] `Git/LocalGitCheckout.cs:428` — `gitClient.Clone(definition.Repository, scratch, progress)` →
  `gitClient.Clone(definition.Repository.Url, scratch, progress)`
- [ ] `Git/LocalGitCheckout.cs:585` — same substitution (`displayRepository` local in
  `CheckoutWithFetchRetry`)
- [ ] `Sources/DeferredCheckout.cs:706` — `GitUrl.Redact(deferred.Definition.Repository)` →
  `GitUrl.Redact(deferred.Definition.Repository.Url)`
- [ ] `Sources/LocalProjectSource.cs:61` — `PreparePlan.For(serviceName, definition.Prepare, …)` →
  `PreparePlan.For(serviceName, definition.Repository.Prepare, …)`

None of these change what path, URL or ref is used — `.Repository.Url`/`.DefaultRef`/`.Prepare`
carry exactly the values `.Repository`/`.DefaultRef`/`.Prepare` did before Task 2, because both
producers copy them across unchanged (Task 2, Steps 2–3). This task is a pure rename at each call
site.

- [ ] **Step 1: Make the eight substitutions above**

- [ ] **Step 2: Build** — confirm these three files no longer error (other files still will; that's
  Task 4)

- [ ] **Step 3: Commit together with Task 2** (see Task 2 Step 6)

---

## Task 4: Work every remaining build error to zero

Implements: nothing new — this is the mechanical tail of Tasks 2–3's model change reaching every
test file that constructs or reads a `ServiceDefinition`.

**Files:** whichever test files the build names. Recon found `.ToDefinition(` called with one
argument in ~28 places across:
`ServiceEndpointTests.cs`, `Sources/MissingHostingPackageTests.cs`, `Prepare/PrepareEagerPathTests.cs`,
`Sources/KubernetesSourceTests.cs`, `Sources/UrlSourceTests.cs`, `Sources/LocalProjectSourceTests.cs`,
`Prepare/PrepareDeferredTests.cs`, `Sources/LocalCheckoutPrefetchTests.cs`,
`Sources/DeferredKindCheckoutTests.cs`, `Config/Catalog/ServiceDefinitionTests.cs`,
`Config/DeveloperConfigurationTests.cs`, `Sources/LocalKindValidationTests.cs`,
`ServiceConfigurationExtensionsTests.cs`, `Sources/DeferredCheckoutTests.cs`,
`Sources/ContainerSourceTests.cs`, `ServiceConfigurationExportsTests.cs`,
`JavaScriptPrepareStepTests.cs` (JS test project),
`JavaPrepareStepTests.cs` (Java test project) — plus direct `new ServiceDefinition { Repository =
"...", ... }` construction in `Catalog/CatalogErrorMessageTests.cs` and direct `.Repository`/
`.DefaultRef` reads on a resolved `ServiceDefinition` in `Catalog/ServiceDefinitionBuilderTests.cs`
and `Config/ServiceSourcesConfigCacheTests.cs` (that last one's local variable is misleadingly named
`metadata` — it is `ServiceSourcesConfigCache.ResolveService`'s `Definition` return value, a
`ServiceDefinition`, not a yaml `ServiceMetadata` — confirm the type at that call site rather than
trusting the variable name).

**Do not treat this list as exhaustive or as a checklist to work from blind.** Rebuild after every
batch of fixes and let the compiler name the next site; this list is a head start; a file added or
changed by another PR since this plan was written is still the compiler's to find, not this list's.

- [ ] **Step 1: Build, read the first batch of errors**

Run: `dotnet build -c Release -warnaserror 2>&1 | grep "error CS"`

- [ ] **Step 2: Fix each site by its actual context** — three shapes recur:
  - `SomeMetadata { ... }.ToDefinition("servicesources.yaml")` → add the second argument. Use the
    service name already implied by the test (the string literal passed to `Resolve(...,
    "serviceName", ...)` a few lines below/above, or the test's own `[Fact]` intent) — check each
    site rather than passing a placeholder, since a wrong name here would silently mismatch
    `CheckoutName` against what the rest of the test resolves against. Where a file already has a
    `Definition(name, ...)` local helper (e.g. `LocalProjectSourceTests.cs`,
    `LocalCheckoutPrefetchTests.cs`), thread the helper's own `name` parameter through — it is
    already the right value.
  - `new ServiceDefinition { Repository = "...", Project = "...", ... }` → wrap the repository
    fields: `Repository = new RepositoryDefinition { Url = "...", CheckoutName = "svc" }` (pick
    `CheckoutName` to match whatever service name the surrounding test resolves against, same rule
    as above).
  - `definition.Repository` / `definition.DefaultRef` (read, not construct) → `.Repository.Url` /
    `.Repository.DefaultRef`.

- [ ] **Step 3: Repeat Steps 1–2 until `dotnet build -c Release -warnaserror` succeeds** with `0
  Warning(s)`, `0 Error(s)`.

- [ ] **Step 4: Run the full test project**

Run: `dotnet test -c Release --logger "trx;LogFilePrefix=stage1"`
Expected: every test that passed before this stage still passes, with the same names — this stage
changes no behaviour, so a new failure here is a real regression, not an expected update. A test
whose *assertion* had to change (not just its construction syntax) because it asserted directly on
the old flat shape (`Assert.Equal(url, definition.Repository)`) is fine to update to
`definition.Repository.Url` — that is a syntax follow-on, not a behaviour change; but if any test's
*outcome* changes (a value that used to match no longer does, or vice versa), stop and treat it as a
regression to investigate, not a test to adjust.

- [ ] **Step 5: Commit**

```bash
git add -u
git commit -m "Move Repository/DefaultRef/Prepare onto RepositoryDefinition (#291)"
```

---

## Task 5: A test proving `CheckoutName` defaults to the service name for both producers

Implements: design finding 2 / criterion 6, ahead of Stage 2 actually reading the field.

**Files:**
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs`
  (yaml producer)
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs` (code
  producer)

Nothing downstream reads `CheckoutName` yet (Stage 2 does), so this is not covered by any existing
assertion — worth a direct test now, since it is the property Stage 2's #66 fix depends on, and a
regression here would otherwise surface only once Stage 2 re-keys checkouts onto it.

- [ ] **Step 1: Write the two failing tests**

```csharp
// In ServiceDefinitionTests.cs
[Fact]
public void ToDefinition_CheckoutNameIsTheServiceName()
{
    var definition = new ServiceMetadata { Repository = "https://github.com/example/repo" }
        .ToDefinition("/apphost/servicesources.yaml", "orders");

    Assert.Equal("orders", definition.Repository.CheckoutName);
}
```

```csharp
// In ServiceDefinitionBuilderTests.cs
[Fact]
public void Build_CheckoutNameIsTheServiceName()
{
    var definition = new ServiceCatalogBuilder().AddService("orders")
        .WithRepository("https://github.com/example/repo")
        .Build();

    Assert.Equal("orders", definition.Repository.CheckoutName);
}
```

- [ ] **Step 2: Run — both should already pass**, since Task 2 wired `CheckoutName` from the same
  `serviceName`/`_serviceName` value both producers already carried. If either fails, Task 2's
  producer wiring has a bug — fix it there, not here.

- [ ] **Step 3: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs
git commit -m "Assert CheckoutName defaults to the service name (#291)"
```

---

## Task 6: Full verification and PR

- [ ] **Step 1: Full solution build**

Run: `dotnet restore && dotnet build -c Release --no-restore -warnaserror`
Expected: `Build succeeded`, 0 warnings, 0 errors.

- [ ] **Step 2: Full test suite**

Run: `dotnet test -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results`
Expected: same pass count as `origin/main`, 0 failed.

- [ ] **Step 3: The three smoketest scripts** (container source, config layers, local source) —
  these exercise a real `aspire run` against the shipped samples, so they are the cheapest real
  proof that nothing observable changed:

```bash
./scripts/smoketest-local-source.sh
./scripts/smoketest-config-layers.sh
./scripts/smoketest-container-source.sh
```

- [ ] **Step 4: TypeScript export surface** — no public API changed in this stage, so this is a
  confirmation, not a new risk:

```bash
rm -rf samples/DemoAppHostTypeScript/.aspire samples/DemoAppHostTypeScriptCodeCatalog/.aspire
(cd samples/DemoAppHostTypeScript && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json)
(cd samples/DemoAppHostTypeScriptCodeCatalog && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json)
```

- [ ] **Step 5: Open the draft PR**

```bash
gh pr create --draft --base main \
  --title "Repository handle Stage 1: RepositoryDefinition record move (#291)" \
  --body-file "$NOTES_PR"
```

Body states plainly: no behaviour change, no public API, no CHANGELOG entry, and that Stage 2
(`AddRepository`, yaml `repositories:`, the checkout/prefetch re-keying that actually closes #66) is
a separate follow-up PR against this design.
