# Code Catalog Stage 2 — `WithPrepare`, `AsJava`/`AsJavaScript` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the last gap between the code-authored catalog (Stage 1, `#134`, merged as `#299`)
and `servicesources.yaml`: a code-declared service can already use every *source*, but a `"local"`
service on a non-`dotnet` kind can only pass a raw `Dictionary<string, object>` through `WithKind`,
and there is no code-authoring surface for the `prepare:` block at all. This stage adds
`WithPrepare` to `ServiceDefinitionBuilder`, and typed `AsJava`/`AsJavaScript` fluent handles that
wrap the already-`internal` `JavaKindOptions`/`JavaScriptKindOptions` classes — reaching design
acceptance criteria 1 and 2 **in full** (parity with the yaml loader, for both C# and TypeScript
AppHosts).

**Architecture:** Both additions are pure surface — no new runtime behaviour, no new validation.
`WithPrepare` builds a `PrepareMetadata` (unchanged type, already a field on `ServiceDefinition`
and already read origin-agnostically by `Prepare/PreparePlan.cs`) exactly the way `WithContainer`
builds a `ContainerMetadata` today: assign, no validation at build time. `AsJava`/`AsJavaScript` are
sugar over `WithKind`: each is an extension method that hands the AppHost author a small fluent
options handle (`JavaKindOptionsBuilder`/`JavaScriptKindOptionsBuilder`, both new, both
`[AspireExport(ExposeMethods = true)]`, both writing straight into a `JavaKindOptions`/
`JavaScriptKindOptions` instance they own), then calls `WithKind(kindName, builder.Build())` — the
already-typed instance flows through `LocalKindConfig.Parse<T>`'s existing branch 1 (an
already-typed value is returned unchanged) with no new code on that path at all. Every field these
handles expose is validated exactly where it is today: `JavaKindOptions.Parse` /
`JavaScriptKindOptions`'s own resolution, at `Resolve`/`Validate`/`ResolveDeferred` time — the
builders are dumb property bags with a fluent face, not a second copy of that logic.

**Tech Stack:** C# (`Aspire.Hosting.ServiceSources`, `net8.0`/`net9.0`/`net10.0`), Aspire's ATS
export attributes (`[AspireExport]`), xUnit (`test/Aspire.Hosting.ServiceSources.Tests`,
`test/Aspire.Hosting.ServiceSources.Java.Tests`, `test/Aspire.Hosting.ServiceSources.JavaScript.Tests`).

**Spec:** `docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md` — Staging table
Stage 2 row, and the "Kind options" section (the `JavaKindOptionsBuilder` sketch). The `prepare:`
mechanism itself is not being built here; it already shipped — see
`docs/superpowers/specs/2026-08-28-servicesources-prepare-step-design.md`. Stage 1's own plan,
`docs/superpowers/plans/2026-09-07-code-catalog-stage1-core.md`, is the file-layout and
capability-id precedent this plan follows throughout.

## Global Constraints

- **No new validation logic anywhere in this stage.** `WithPrepare` and the two typed handles are
  thin — they assign fields and return `this`/`Build()`. Every accepted value, every rejection
  message, is the one the yaml path already produces, reached through the same downstream code
  (`Prepare/PreparePlan.cs`'s `ParseOptional`, `JavaKindOptions.Parse`,
  `JavaScriptLocalKind`'s own resolution). If a task adds a new `throw` anywhere outside
  `ServiceDefinitionBuilder`'s existing `RequireUnset` pattern, stop and re-read this constraint.
- **`JavaKindOptions` and `JavaScriptKindOptions` stay `internal`.** Design "Kind options": making
  them public freezes 274 lines of yaml-bound properties as API. The two new builder classes are
  the only public surface; they write into these types from inside the same assembly.
- **Capability ids.** A method exposed only via its declaring type's `[AspireExport(ExposeMethods =
  true)]` (no attribute of its own) resolves to `{TypeName}.{camelCaseMethodName}` —
  `ServiceDefinitionBuilder.withPrepare`, `JavaKindOptionsBuilder.mavenGoal`, and so on. A method
  carrying its own bare `[AspireExport]` (an extension method — `AsJava`, `AsJavaScript`) resolves
  to the bare camelCase name — `asJava`, `asJavaScript`. Neither collides with anything shipped
  (`useJava`, `useJavaScript`, `addService`, `addServiceToCatalog`, `addServiceCatalog` are the only
  other top-level ids). Verified per-task in Task 6 by building the TypeScript export surface and
  checking for `ASPIREEXPORT013` — do not skip that check even though no collision is expected; it's
  exactly the failure mode Stage 0 found by surprise (`docs/superpowers/specs/2026-09-07-code-catalog-stage0-ats-probe-findings.md`).
- **`$(AspireVersion)` floor is `13.5.2`** (`Directory.Build.props:74`). The CLI on `PATH` in this
  environment is `13.5.1` (fails `aspire restore` with NU1605). Before Task 8 (TypeScript sample),
  install one at or above the floor to this job's own scratch directory — it will not survive to a
  future job, so re-check first:
  ```bash
  dotnet tool install --tool-path "$CLAUDE_JOB_DIR/tmp/aspire-cli" aspire.cli --version 13.5.3
  ```
  then prefix TypeScript verification commands with that path.
- **`-warnaserror` on every build** (`dotnet build -c Release -warnaserror`, matching `ci.yml`'s
  `🔨 build, test & pack` job) — every task's build step must be warning-clean.
- **Namespace placement matches the sibling type, not a rule.** `JavaKindOptions` lives in
  `Aspire.Hosting.ServiceSources.Java` → `JavaKindOptionsBuilder` goes there too.
  `JavaScriptKindOptions` lives in the flat `Aspire.Hosting.ServiceSources` → `JavaScriptKindOptionsBuilder`
  goes there too (this codebase does *not* nest JavaScript types under a `.JavaScript` sub-namespace,
  unlike Java — checked directly against the existing files, not assumed).
- **Samples demonstrate `AsJava` only, not `AsJavaScript`.** Neither existing code-catalog sample
  (`samples/DemoAppHostCodeCatalog`, `samples/DemoAppHostTypeScriptCodeCatalog`) declares a
  JavaScript-kind service today (both only exercise the `java` kind, for the same "catalog" entry).
  `AsJavaScript` is covered by its own unit tests only in this stage — do not invent a new sample
  service to exercise it; that widens scope past what parity requires.
- **A pre-existing CHANGELOG issue, not this stage's to fix:** Stage 1's `#134` entries landed under
  the already-tagged `## [0.5.1]` section instead of `## [Unreleased]` (v0.5.1 was released
  2026-09-07, Stage 1 merged 2026-09-08 — `git log`/`gh release list` confirm the ordering). This
  stage's own CHANGELOG entries go under the existing, currently-empty `## [Unreleased]` header
  (`CHANGELOG.md:17`) — the correct section for unreleased work. Leave the misplaced Stage 1 entries
  alone; reconciling already-released changelog history is a separate decision for a human, not
  something to fold into this plan.

---

## Task 1: `WithPrepare` on `ServiceDefinitionBuilder`

Implements: Staging table Stage 2, first item.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`

**Interfaces:**
- Consumes: `PrepareMetadata` (`Aspire.Hosting.ServiceSources.Config`, unchanged — `Command`,
  `WindowsCommand`, `Mode` all `string`/`string[]`, all nullable).
- Produces: `ServiceDefinitionBuilder WithPrepare(string[] command, string[]? windowsCommand = null,
  string? mode = null)`. Task 6 (exports guard), Task 7 (C# sample), Task 8 (TypeScript sample) and
  Task 9 (README) all call it — match this signature exactly.

- [ ] **Step 1: Write the failing tests**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs` (append
near the `WithKind` tests at the bottom of the file):

```csharp
    [Fact]
    public void WithPrepare_SetsCommandWindowsCommandAndMode()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["./prepare.sh"], windowsCommand: ["prepare.cmd"], mode: "once")
            .Build();

        Assert.NotNull(definition.Prepare);
        Assert.Equal(["./prepare.sh"], definition.Prepare.Command);
        Assert.Equal(["prepare.cmd"], definition.Prepare.WindowsCommand);
        Assert.Equal("once", definition.Prepare.Mode);
    }

    [Fact]
    public void WithPrepare_WindowsCommandAndModeOmitted_StayNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["./prepare.sh"])
            .Build();

        Assert.Null(definition.Prepare!.WindowsCommand);
        Assert.Null(definition.Prepare.Mode);
    }

    [Fact]
    public void WithPrepare_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["a.sh"]);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithPrepare(["b.sh"]));

        Assert.Contains("catalog", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithPrepare", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPrepare_NotCalled_PrepareStaysNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/orders")
            .Build();

        Assert.Null(definition.Prepare);
    }
```

- [ ] **Step 2: Run the tests, confirm they fail to compile** (`WithPrepare` doesn't exist yet)

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~ServiceDefinitionBuilderTests"`
Expected: build error, `'ServiceDefinitionBuilder' does not contain a definition for 'WithPrepare'`

- [ ] **Step 3: Implement `WithPrepare`**

In `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`, add a field alongside
the existing ones:

```csharp
    private PrepareMetadata? _prepare;
```

Add the method after `WithKubernetes` (before `WithKind`, keeping source order matching the
design's method table — repository/url/container/kubernetes/prepare/kind):

```csharp
    /// <summary>
    /// Declares a bootstrap command the <c>"local"</c> source runs inside the materialized checkout
    /// before the kind is allowed to judge it — the code-authoring equivalent of yaml's
    /// <c>prepare:</c> block. See the "prepare" step design
    /// (<c>docs/superpowers/specs/2026-08-28-servicesources-prepare-step-design.md</c>) for what it
    /// runs and when.
    /// </summary>
    /// <param name="command">
    /// The command, as argv rather than a shell string — no quoting or word-splitting rules to get
    /// wrong. A first element that looks like a path is resolved against the checkout and confined
    /// to it; a bare name goes through <c>PATH</c>.
    /// </param>
    /// <param name="windowsCommand">
    /// Replaces <paramref name="command"/> on Windows. Left unset, <paramref name="command"/> runs
    /// there too — correct for a program that is a real executable on every platform, wrong for one
    /// that is a <c>.cmd</c>/<c>.bat</c> shim there (<c>npm</c> is the case to know: there is no
    /// <c>npm.exe</c>).
    /// </param>
    /// <param name="mode">
    /// How often the step runs: <c>"oncePerCommit"</c> (the default when left unset), <c>"once"</c>,
    /// <c>"always"</c>, or <c>"never"</c>.
    /// </param>
    public ServiceDefinitionBuilder WithPrepare(string[] command, string[]? windowsCommand = null, string? mode = null)
    {
        RequireUnset(_prepare, nameof(WithPrepare));
        _prepare = new PrepareMetadata { Command = command, WindowsCommand = windowsCommand, Mode = mode };
        return this;
    }
```

In the same file's `Build()`, add `Prepare = _prepare,` alongside the other assignments (order
matches `ServiceDefinition`'s own property order — after `Kubernetes`/`Container`, before `Kind`):

```csharp
    internal ServiceDefinition Build() => new()
    {
        Repository = _repository ?? "",
        Project = _project ?? "",
        DefaultRef = _defaultRef,
        Url = _url,
        Container = _container,
        Kubernetes = _kubernetes,
        Prepare = _prepare,
        Kind = _kind ?? LocalKinds.Dotnet,
        KindOptions = _kindOptions,
        Origin = CatalogOrigin.Code,
    };
```

- [ ] **Step 4: Run the tests, confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~ServiceDefinitionBuilderTests"`
Expected: PASS, all `WithPrepare_*` tests green.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs
git commit -m "feat: add WithPrepare to ServiceDefinitionBuilder"
```

---

## Task 2: `JavaKindOptionsBuilder`

Implements: Design "Kind options" — the `JavaKindOptionsBuilder` sketch.

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Java/JavaKindOptionsBuilder.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Java.Tests/JavaKindOptionsBuilderTests.cs`

**Interfaces:**
- Consumes: `JavaKindOptions` (`Aspire.Hosting.ServiceSources.Java`, internal, unchanged — 7
  settable properties: `WorkingDirectory`, `MavenGoal`, `GradleTask`, `JarPath`, `WrapperPath`,
  `Args`, `Port`).
- Produces: `JavaKindOptionsBuilder` (sealed, `[AspireExport(ExposeMethods = true)]`, `internal`
  constructor), seven fluent `With`-less setters each returning `JavaKindOptionsBuilder`
  (`WorkingDirectory(string)`, `MavenGoal(string)`, `GradleTask(string)`, `JarPath(string)`,
  `WrapperPath(string)`, `Args(string[])`, `Port(int)`), and `internal JavaKindOptions Build()`.
  Task 3's `AsJava` consumes exactly this shape.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Java.Tests/JavaKindOptionsBuilderTests.cs`:

```csharp
using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaKindOptionsBuilderTests
{
    [Fact]
    public void Build_EveryFieldSet_ProducesMatchingJavaKindOptions()
    {
        var options = new JavaKindOptionsBuilder()
            .WorkingDirectory("services/api")
            .MavenGoal("spring-boot:run")
            .WrapperPath("mvnw")
            .Args(["-Dspring-boot.run.profiles=dev"])
            .Port(8080)
            .Build();

        Assert.Equal("services/api", options.WorkingDirectory);
        Assert.Equal("spring-boot:run", options.MavenGoal);
        Assert.Equal("mvnw", options.WrapperPath);
        Assert.Equal(["-Dspring-boot.run.profiles=dev"], options.Args);
        Assert.Equal(8080, options.Port);
        Assert.Null(options.GradleTask);
        Assert.Null(options.JarPath);
    }

    [Fact]
    public void Build_OnlyGradleTaskAndPortSet_LeavesOthersNull()
    {
        var options = new JavaKindOptionsBuilder()
            .GradleTask("bootRun")
            .Port(8081)
            .Build();

        Assert.Equal("bootRun", options.GradleTask);
        Assert.Equal(8081, options.Port);
        Assert.Null(options.MavenGoal);
        Assert.Null(options.JarPath);
        Assert.Null(options.WorkingDirectory);
    }

    [Fact]
    public void Build_JarPathSet_ProducesMatchingJavaKindOptions()
    {
        var options = new JavaKindOptionsBuilder()
            .JarPath("target/app.jar")
            .Port(8082)
            .Build();

        Assert.Equal("target/app.jar", options.JarPath);
    }

    [Fact]
    public void EveryPublicMethod_IsNonGenericAndReturnsItself()
    {
        var methods = typeof(JavaKindOptionsBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var method in methods)
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.Equal(typeof(JavaKindOptionsBuilder), method.ReturnType);
        }
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail to compile**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Java.Tests -c Release --filter "FullyQualifiedName~JavaKindOptionsBuilderTests"`
Expected: build error, `The type or namespace name 'JavaKindOptionsBuilder' could not be found`

- [ ] **Step 3: Implement `JavaKindOptionsBuilder`**

Create `src/Aspire.Hosting.ServiceSources/Java/JavaKindOptionsBuilder.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Java;

/// <summary>
/// A fluent handle over a service's <c>java:</c> options — the typed alternative to passing a
/// <c>Dictionary&lt;string, object&gt;</c> to <c>WithKind("java", …)</c>. Writes directly into an
/// internal <see cref="JavaKindOptions"/> instance; kept as a handle rather than making
/// <see cref="JavaKindOptions"/> itself public, so its yaml-bound shape can keep changing without
/// that being a breaking change here. See design "Kind options".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class JavaKindOptionsBuilder
{
    private readonly JavaKindOptions _options = new();

    internal JavaKindOptionsBuilder()
    {
    }

    /// <summary>Where in the checkout the Java project lives, relative to the repository root.</summary>
    public JavaKindOptionsBuilder WorkingDirectory(string workingDirectory)
    {
        _options.WorkingDirectory = workingDirectory;
        return this;
    }

    /// <summary>The Maven goal to run the app with, e.g. <c>spring-boot:run</c>.</summary>
    public JavaKindOptionsBuilder MavenGoal(string goal)
    {
        _options.MavenGoal = goal;
        return this;
    }

    /// <summary>The Gradle task to run the app with, e.g. <c>bootRun</c>.</summary>
    public JavaKindOptionsBuilder GradleTask(string task)
    {
        _options.GradleTask = task;
        return this;
    }

    /// <summary>A pre-built jar to run with <c>java -jar</c>, relative to <see cref="WorkingDirectory"/>.</summary>
    public JavaKindOptionsBuilder JarPath(string jarPath)
    {
        _options.JarPath = jarPath;
        return this;
    }

    /// <summary>Where the <c>mvnw</c>/<c>gradlew</c> wrapper script lives, relative to the repository root.</summary>
    public JavaKindOptionsBuilder WrapperPath(string wrapperPath)
    {
        _options.WrapperPath = wrapperPath;
        return this;
    }

    /// <summary>Extra arguments for whichever run mode is configured.</summary>
    public JavaKindOptionsBuilder Args(string[] args)
    {
        _options.Args = args;
        return this;
    }

    /// <summary>The port the Java app listens on.</summary>
    public JavaKindOptionsBuilder Port(int port)
    {
        _options.Port = port;
        return this;
    }

    /// <summary>
    /// The <see cref="JavaKindOptions"/> this handle has been writing into. Validated the same way
    /// a yaml <c>java:</c> block is — by <see cref="JavaKindOptions.Parse"/>, at
    /// <c>Resolve</c>/<c>Validate</c>/<c>ResolveDeferred</c> time — not here.
    /// </summary>
    internal JavaKindOptions Build() => _options;
}
```

- [ ] **Step 4: Run the tests, confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Java.Tests -c Release --filter "FullyQualifiedName~JavaKindOptionsBuilderTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Java/JavaKindOptionsBuilder.cs test/Aspire.Hosting.ServiceSources.Java.Tests/JavaKindOptionsBuilderTests.cs
git commit -m "feat: add JavaKindOptionsBuilder"
```

---

## Task 3: `AsJava` extension on `ServiceDefinitionBuilder`

Implements: Design "Kind options" — "`AsJava`/`AsJavaScript` are sugar over [`WithKind`]".

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Java/JavaServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Java.Tests/JavaServiceSourcesBuilderExtensionsTests.cs` (new file)

**Interfaces:**
- Consumes: `JavaKindOptionsBuilder` (Task 2), `ServiceDefinitionBuilder.WithKind(string, object?)`
  (Stage 1, unchanged), `JavaLocalResourceKind.KindName` (existing constant, `"java"`).
- Produces: `ServiceDefinitionBuilder AsJava(this ServiceDefinitionBuilder builder,
  Action<JavaKindOptionsBuilder> configure)`. Task 6 (exports guard) and Task 7 (C# sample) both
  consume this exact signature.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Java.Tests/JavaServiceSourcesBuilderExtensionsTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaServiceSourcesBuilderExtensionsTests
{
    [Fact]
    public void AsJava_SetsKindNameAndTypedOptions()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080))
            .Build();

        Assert.Equal(JavaLocalResourceKind.KindName, definition.Kind);
        var options = Assert.IsType<JavaKindOptions>(definition.KindOptions);
        Assert.Equal("spring-boot:run", options.MavenGoal);
        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void AsJava_ProducedOptions_ParseSuccessfullyThroughTheExistingValidator()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080))
            .Build();

        var validated = JavaKindOptions.Parse("catalog", definition.KindOptions);

        Assert.Equal(JavaRunModeKind.MavenGoal, validated.RunMode.Kind);
        Assert.Equal("spring-boot:run", validated.RunMode.Value);
        Assert.Equal(8080, validated.Port);
    }

    [Fact]
    public void AsJava_ReturnsTheSameBuilderForFurtherChaining()
    {
        var catalogBuilder = new ServiceCatalogBuilder();
        var chain = catalogBuilder.AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic");

        var result = chain.AsJava(o => o.MavenGoal("spring-boot:run").Port(8080));

        Assert.Same(chain, result);
    }

    [Fact]
    public void AsJava_CalledAfterWithKind_ThrowsAlreadyCalled()
    {
        var chain = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .WithKind("java", new Dictionary<string, object> { ["port"] = 8080 });

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.AsJava(o => o.Port(8080)));
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail to compile**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Java.Tests -c Release --filter "FullyQualifiedName~JavaServiceSourcesBuilderExtensionsTests"`
Expected: build error, `'ServiceDefinitionBuilder' does not contain a definition for 'AsJava'`

- [ ] **Step 3: Implement `AsJava`**

In `src/Aspire.Hosting.ServiceSources/Java/JavaServiceSourcesBuilderExtensions.cs`, add
`using Aspire.Hosting.ServiceSources.Catalog;` to the existing usings, then add the method to the
existing `JavaServiceSourcesBuilderExtensions` class, after `UseJava`:

```csharp
    /// <summary>
    /// Configures this code-declared service to run as a <c>java</c>-kind <c>"local"</c> service,
    /// through a typed options handle instead of a raw dictionary. Sugar over
    /// <c>WithKind("java", …)</c> — calling this after <c>WithKind</c> (on either) throws the same
    /// "already called" error <c>WithKind</c> itself would, since this <em>is</em> that call.
    /// </summary>
    /// <example>
    /// <code>
    /// catalog.AddService("catalog")
    ///     .WithRepository("https://github.com/spring-projects/spring-petclinic")
    ///     .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080));
    /// </code>
    /// </example>
    [AspireExport]
    public static ServiceDefinitionBuilder AsJava(
        this ServiceDefinitionBuilder builder, Action<JavaKindOptionsBuilder> configure)
    {
        var options = new JavaKindOptionsBuilder();
        configure(options);
        return builder.WithKind(JavaLocalResourceKind.KindName, options.Build());
    }
```

- [ ] **Step 4: Run the tests, confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Java.Tests -c Release --filter "FullyQualifiedName~JavaServiceSourcesBuilderExtensionsTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Java/JavaServiceSourcesBuilderExtensions.cs test/Aspire.Hosting.ServiceSources.Java.Tests/JavaServiceSourcesBuilderExtensionsTests.cs
git commit -m "feat: add AsJava sugar over WithKind"
```

---

## Task 4: `JavaScriptKindOptionsBuilder`

Implements: Design "Kind options", JavaScript side. Mirrors Task 2 exactly — see that task for the
rationale each choice below repeats.

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptKindOptionsBuilder.cs`
- Test: `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptKindOptionsBuilderTests.cs`

**Interfaces:**
- Consumes: `JavaScriptKindOptions` (flat `Aspire.Hosting.ServiceSources` namespace, internal,
  unchanged — 8 settable properties: `AppType`, `AppDirectory`, `RunScript`, `ScriptPath`,
  `PackageManager`, `Port`, `TargetPort`, `PortEnv`).
- Produces: `JavaScriptKindOptionsBuilder` (sealed, `[AspireExport(ExposeMethods = true)]`,
  `internal` constructor), eight fluent setters (`AppType(string)`, `AppDirectory(string)`,
  `RunScript(string)`, `ScriptPath(string)`, `PackageManager(string)`, `Port(int)`,
  `TargetPort(int)`, `PortEnv(string)`), and `internal JavaScriptKindOptions Build()`. Task 5's
  `AsJavaScript` consumes exactly this shape.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptKindOptionsBuilderTests.cs`:

```csharp
using System.Reflection;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

public class JavaScriptKindOptionsBuilderTests
{
    [Fact]
    public void Build_EveryFieldSet_ProducesMatchingJavaScriptKindOptions()
    {
        var options = new JavaScriptKindOptionsBuilder()
            .AppType(JavaScriptAppTypes.Vite)
            .AppDirectory("apps/web")
            .RunScript("dev")
            .PackageManager(JavaScriptPackageManagers.Pnpm)
            .Port(3000)
            .TargetPort(5173)
            .PortEnv("VITE_PORT")
            .Build();

        Assert.Equal(JavaScriptAppTypes.Vite, options.AppType);
        Assert.Equal("apps/web", options.AppDirectory);
        Assert.Equal("dev", options.RunScript);
        Assert.Equal(JavaScriptPackageManagers.Pnpm, options.PackageManager);
        Assert.Equal(3000, options.Port);
        Assert.Equal(5173, options.TargetPort);
        Assert.Equal("VITE_PORT", options.PortEnv);
        Assert.Null(options.ScriptPath);
    }

    [Fact]
    public void Build_NodeAppWithScriptPath_ProducesMatchingJavaScriptKindOptions()
    {
        var options = new JavaScriptKindOptionsBuilder()
            .AppType(JavaScriptAppTypes.Node)
            .ScriptPath("server.js")
            .Build();

        Assert.Equal(JavaScriptAppTypes.Node, options.AppType);
        Assert.Equal("server.js", options.ScriptPath);
    }

    [Fact]
    public void EveryPublicMethod_IsNonGenericAndReturnsItself()
    {
        var methods = typeof(JavaScriptKindOptionsBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var method in methods)
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.Equal(typeof(JavaScriptKindOptionsBuilder), method.ReturnType);
        }
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail to compile**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.JavaScript.Tests -c Release --filter "FullyQualifiedName~JavaScriptKindOptionsBuilderTests"`
Expected: build error, `The type or namespace name 'JavaScriptKindOptionsBuilder' could not be found`

- [ ] **Step 3: Implement `JavaScriptKindOptionsBuilder`**

Create `src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptKindOptionsBuilder.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// A fluent handle over a service's <c>javascript:</c> options — the typed alternative to passing a
/// <c>Dictionary&lt;string, object&gt;</c> to <c>WithKind("javascript", …)</c>. Writes directly into
/// an internal <see cref="JavaScriptKindOptions"/> instance; kept as a handle rather than making
/// <see cref="JavaScriptKindOptions"/> itself public, for the same reason
/// <see cref="Java.JavaKindOptionsBuilder"/> is — see design "Kind options".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class JavaScriptKindOptionsBuilder
{
    private readonly JavaScriptKindOptions _options = new();

    internal JavaScriptKindOptionsBuilder()
    {
    }

    /// <summary>
    /// Which <c>Aspire.Hosting.JavaScript</c> integration runs the app — see
    /// <see cref="JavaScriptAppTypes"/> for the accepted values (<c>javascript</c>, <c>vite</c>,
    /// <c>nextjs</c>, <c>node</c>, <c>bun</c>).
    /// </summary>
    public JavaScriptKindOptionsBuilder AppType(string appType)
    {
        _options.AppType = appType;
        return this;
    }

    /// <summary>The directory holding the app's <c>package.json</c>, relative to the repository root.</summary>
    public JavaScriptKindOptionsBuilder AppDirectory(string appDirectory)
    {
        _options.AppDirectory = appDirectory;
        return this;
    }

    /// <summary>The <c>package.json</c> script to run.</summary>
    public JavaScriptKindOptionsBuilder RunScript(string runScript)
    {
        _options.RunScript = runScript;
        return this;
    }

    /// <summary>
    /// The entry-point file to run directly, relative to <see cref="AppDirectory"/> — for the
    /// <c>node</c>/<c>bun</c> app types.
    /// </summary>
    public JavaScriptKindOptionsBuilder ScriptPath(string scriptPath)
    {
        _options.ScriptPath = scriptPath;
        return this;
    }

    /// <summary>
    /// The package manager used to install dependencies — see
    /// <see cref="JavaScriptPackageManagers"/> for the accepted values.
    /// </summary>
    public JavaScriptKindOptionsBuilder PackageManager(string packageManager)
    {
        _options.PackageManager = packageManager;
        return this;
    }

    /// <summary>The port consumers reach the service on.</summary>
    public JavaScriptKindOptionsBuilder Port(int port)
    {
        _options.Port = port;
        return this;
    }

    /// <summary>The port the app itself listens on, when fixed rather than read from <see cref="PortEnv"/>.</summary>
    public JavaScriptKindOptionsBuilder TargetPort(int targetPort)
    {
        _options.TargetPort = targetPort;
        return this;
    }

    /// <summary>The environment variable the app reads its listen port from.</summary>
    public JavaScriptKindOptionsBuilder PortEnv(string portEnv)
    {
        _options.PortEnv = portEnv;
        return this;
    }

    /// <summary>
    /// The <see cref="JavaScriptKindOptions"/> this handle has been writing into. Validated the same
    /// way a yaml <c>javascript:</c> block is, not here.
    /// </summary>
    internal JavaScriptKindOptions Build() => _options;
}
```

- [ ] **Step 4: Run the tests, confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.JavaScript.Tests -c Release --filter "FullyQualifiedName~JavaScriptKindOptionsBuilderTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptKindOptionsBuilder.cs test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptKindOptionsBuilderTests.cs
git commit -m "feat: add JavaScriptKindOptionsBuilder"
```

---

## Task 5: `AsJavaScript` extension on `ServiceDefinitionBuilder`

Implements: Design "Kind options", JavaScript side. Mirrors Task 3 exactly.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptServiceSourcesBuilderExtensionsTests.cs` (new file)

**Interfaces:**
- Consumes: `JavaScriptKindOptionsBuilder` (Task 4), `ServiceDefinitionBuilder.WithKind(string,
  object?)`, `JavaScriptLocalKind.KindName` (existing constant, `"javascript"`).
- Produces: `ServiceDefinitionBuilder AsJavaScript(this ServiceDefinitionBuilder builder,
  Action<JavaScriptKindOptionsBuilder> configure)`. Task 6 consumes this exact signature.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptServiceSourcesBuilderExtensionsTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

public class JavaScriptServiceSourcesBuilderExtensionsTests
{
    [Fact]
    public void AsJavaScript_SetsKindNameAndTypedOptions()
    {
        var definition = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend")
            .AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite).Port(3000))
            .Build();

        Assert.Equal(JavaScriptLocalKind.KindName, definition.Kind);
        var options = Assert.IsType<JavaScriptKindOptions>(definition.KindOptions);
        Assert.Equal(JavaScriptAppTypes.Vite, options.AppType);
        Assert.Equal(3000, options.Port);
    }

    [Fact]
    public void AsJavaScript_ReturnsTheSameBuilderForFurtherChaining()
    {
        var chain = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend");

        var result = chain.AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite));

        Assert.Same(chain, result);
    }

    [Fact]
    public void AsJavaScript_CalledAfterWithKind_ThrowsAlreadyCalled()
    {
        var chain = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend")
            .WithKind("javascript", new Dictionary<string, object> { ["appType"] = "vite" });

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite)));
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail to compile**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.JavaScript.Tests -c Release --filter "FullyQualifiedName~JavaScriptServiceSourcesBuilderExtensionsTests"`
Expected: build error, `'ServiceDefinitionBuilder' does not contain a definition for 'AsJavaScript'`

- [ ] **Step 3: Implement `AsJavaScript`**

In `src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptServiceSourcesBuilderExtensions.cs`, add
`using Aspire.Hosting.ServiceSources.Catalog;` to the existing `using Aspire.Hosting;` line, then
add the method to the existing `JavaScriptServiceSourcesBuilderExtensions` class, after `UseJavaScript`:

```csharp
    /// <summary>
    /// Configures this code-declared service to run as a <c>javascript</c>-kind <c>"local"</c>
    /// service, through a typed options handle instead of a raw dictionary. Sugar over
    /// <c>WithKind("javascript", …)</c> — calling this after <c>WithKind</c> (on either) throws the
    /// same "already called" error <c>WithKind</c> itself would, since this <em>is</em> that call.
    /// </summary>
    /// <example>
    /// <code>
    /// catalog.AddService("frontend")
    ///     .WithRepository("https://github.com/example/frontend")
    ///     .AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite).Port(3000));
    /// </code>
    /// </example>
    [AspireExport]
    public static ServiceDefinitionBuilder AsJavaScript(
        this ServiceDefinitionBuilder builder, Action<JavaScriptKindOptionsBuilder> configure)
    {
        var options = new JavaScriptKindOptionsBuilder();
        configure(options);
        return builder.WithKind(JavaScriptLocalKind.KindName, options.Build());
    }
```

- [ ] **Step 4: Run the tests, confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.JavaScript.Tests -c Release --filter "FullyQualifiedName~JavaScriptServiceSourcesBuilderExtensionsTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptServiceSourcesBuilderExtensions.cs test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptServiceSourcesBuilderExtensionsTests.cs
git commit -m "feat: add AsJavaScript sugar over WithKind"
```

---

## Task 6: Extend the finding-10 reflection guard (`CatalogExportsTests`)

Implements: Global Constraints' capability-id note; keeps the finding-10 guard (design section on
the public-API-with-no-ApiCompat risk) honest about the surface this stage just grew.

**Files:**
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`

**Interfaces:**
- Consumes: every method added in Tasks 1, 2, 3, 4, 5 — this task adds no new production code, only
  extends an existing test's expectations and (for the two new builder types) its coverage.

- [ ] **Step 1: Run the existing test to see it fail first**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~CatalogExportsTests"`
Expected: `ExportedIds_MatchTheKnownSurface` FAILS — the live assembly now has `asJava`,
`asJavaScript`, `ServiceDefinitionBuilder.withPrepare`, and the seven/eight
`JavaKindOptionsBuilder.*`/`JavaScriptKindOptionsBuilder.*` ids the hardcoded `expected` array
doesn't know about yet. `NoTwoExportedMethodsInTheAssembly_ShareAGeneratedCapabilityId` should still
PASS (no collision) — if it doesn't, stop and read its failure message before touching anything
else; that is the real ATS-collision guard, not a fixture to edit around.

- [ ] **Step 2: Extend the known-surface list**

In `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`, add to the `expected`
array in `ExportedIds_MatchTheKnownSurface` (after the existing `ServiceDefinitionBuilder.*` rows):

```csharp
            "asJava", "asJavaScript",
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithPrepare))}",
            "JavaKindOptionsBuilder.workingDirectory", "JavaKindOptionsBuilder.mavenGoal",
            "JavaKindOptionsBuilder.gradleTask", "JavaKindOptionsBuilder.jarPath",
            "JavaKindOptionsBuilder.wrapperPath", "JavaKindOptionsBuilder.args", "JavaKindOptionsBuilder.port",
            "JavaScriptKindOptionsBuilder.appType", "JavaScriptKindOptionsBuilder.appDirectory",
            "JavaScriptKindOptionsBuilder.runScript", "JavaScriptKindOptionsBuilder.scriptPath",
            "JavaScriptKindOptionsBuilder.packageManager", "JavaScriptKindOptionsBuilder.port",
            "JavaScriptKindOptionsBuilder.targetPort", "JavaScriptKindOptionsBuilder.portEnv",
```

Note: `useJava` and `useJavaScript` are already in the array from Stage 1 — leave those untouched.

- [ ] **Step 3: Run the test, confirm it passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~CatalogExportsTests"`
Expected: PASS, all four facts green.

- [ ] **Step 4: Build the whole solution with `-warnaserror` and check for `ASPIREEXPORT013`**

Run: `dotnet build ServiceSources.slnx -c Release -warnaserror`
Expected: succeeds with no `ASPIREEXPORT013` warning anywhere in the output — confirms no real
capability-id collision independent of the reflection test's own (necessarily approximate) model.

- [ ] **Step 5: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs
git commit -m "test: extend the exported-surface guard for Stage 2's new methods"
```

---

## Task 7: C# sample — `AsJava` and `WithPrepare`

Implements: Design's "C# and TypeScript samples" acceptance line, updated for Stage 2's new surface.

**Files:**
- Modify: `samples/DemoAppHostCodeCatalog/Program.cs`

**Interfaces:**
- Consumes: `AsJava` (Task 3), `WithPrepare` (Task 1).

- [ ] **Step 1: Replace the `catalog` service's raw `WithKind` dictionary with `AsJava`, and add a `WithPrepare` call**

In `samples/DemoAppHostCodeCatalog/Program.cs`, replace the `catalog` service block (the comment and
the `AddService("catalog")` chain) with:

```csharp
    // "catalog" (kind: java) is left uncommented here, unlike the yaml sample, because
    // AddServiceCatalog costs nothing extra to declare it — it's servicesources.local.json (below)
    // that decides whether it actually clones anything, exactly as in the yaml sample.
    //
    // AsJava is the typed alternative to WithKind("java", <dictionary>) — Stage 1 shipped only the
    // dictionary form because JavaKindOptions was still internal with no public handle over it.
    // WithPrepare demonstrates the code-authoring equivalent of yaml's prepare: block; it's declared
    // here for the same reason the "catalog" service itself is never actually run below — this repo
    // (spring-petclinic) doesn't need a prepare step, so this exists purely to show the call.
    catalog.AddService("catalog")
        .WithRepository("https://github.com/spring-projects/spring-petclinic", defaultRef: "main")
        .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080))
        .WithPrepare(["./mvnw", "-q", "dependency:go-offline"], mode: "once");
```

- [ ] **Step 2: Update the comment above `AddServiceCatalog` block-local text that referenced the dictionary shape, if any, so it matches**

Check the comment directly preceding the `catalog.AddService("catalog")` block (the one moved in
Step 1) reads consistently with the new code — it already does after Step 1's replacement; no
separate edit needed elsewhere in the file (the `WithKind` three-line README-style example higher up
in this file's own comments, if present, does not exist in `Program.cs` — that text lives in
`README.md`, handled in Task 9).

- [ ] **Step 3: Restore/verify the installed CLI, then actually run the sample**

```bash
dotnet tool install --tool-path "$CLAUDE_JOB_DIR/tmp/aspire-cli" aspire.cli --version 13.5.3 2>/dev/null || true
export PATH="$CLAUDE_JOB_DIR/tmp/aspire-cli:$PATH"
cd samples/DemoAppHostCodeCatalog
cp servicesources.local.json.example servicesources.local.json
dotnet run --project DemoAppHostCodeCatalog.csproj &
sleep 20
curl -sf http://localhost:15888/ >/dev/null && echo "dashboard reachable"
kill %1
```

Expected: the AppHost starts without a `ServiceSourcesConfigurationException` (the catalog composes
and resolves `orders`/`inventory`/`payments` exactly as before Stage 2 — `catalog` is still never
added to the app model, so nothing needs to clone Spring PetClinic or have a JDK installed). Compare
against Stage 1's own verification bar: the sample must *run*, not just typecheck.

- [ ] **Step 4: Clean up the local override file this step created**

```bash
rm -f samples/DemoAppHostCodeCatalog/servicesources.local.json
```

(It's gitignored — this is a courtesy, not required for `git status` to stay clean, but keeps the
worktree tidy for the next task's diff review.)

- [ ] **Step 5: Commit**

```bash
git add samples/DemoAppHostCodeCatalog/Program.cs
git commit -m "docs: demonstrate AsJava and WithPrepare in the C# code-catalog sample"
```

---

## Task 8: TypeScript sample — `asJava` and `withPrepare`

Implements: Design's "C# and TypeScript samples" acceptance line, TypeScript side. Verified by the
existing CI job `📘 typescript export surface (samples/DemoAppHostTypeScriptCodeCatalog,
code-catalog)` — this task reproduces that check locally.

**Files:**
- Modify: `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`

**Interfaces:**
- Consumes: `asJava` (Task 3, ATS-projected), `withPrepare` (Task 1, ATS-projected).

- [ ] **Step 1: Replace the `catalogService` block's raw `withKind` call with `asJava`, and add a `withPrepare` call**

In `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`, replace:

```typescript
  const catalogService = await catalog.addServiceToCatalog('catalog');
  await catalogService.withRepository('https://github.com/spring-projects/spring-petclinic', {
    defaultRef: 'main',
  });
  await catalogService.withKind('java', { options: { mavenGoal: 'spring-boot:run', port: 8080 } });
```

with:

```typescript
  const catalogService = await catalog.addServiceToCatalog('catalog');
  await catalogService.withRepository('https://github.com/spring-projects/spring-petclinic', {
    defaultRef: 'main',
  });
  // asJava is the typed alternative to withKind('java', { options: {...} }) — Stage 1 shipped only
  // the untyped bag because JavaKindOptions had no public handle yet. The lambda nested inside the
  // addServiceCatalog lambda is exactly the shape Stage 0 measured crossing ATS.
  await catalogService.asJava(async (o) => {
    await o.mavenGoal('spring-boot:run');
    await o.port(8080);
  });
  await catalogService.withPrepare(['./mvnw', '-q', 'dependency:go-offline'], { mode: 'once' });
```

- [ ] **Step 2: Update the comment above this block that described the untyped-bag limitation**

The comment currently above `catalogService` (starting `// Declared only — never passed to
builder.addService()…`) already explains *why* `catalog` is never run — leave that part. Its second
paragraph (`withKind's options parameter collapses to an untyped { options: any } bag here:
JavaKindOptions stays internal until a later stage's AsJava lands…`) is now stale — replace that
paragraph with:

```typescript
  // asJava/withPrepare are Stage 2's typed alternatives to the untyped withKind bag Stage 1 shipped
  // — see samples/DemoAppHostCodeCatalog/Program.cs for the identical shape in C#.
```

- [ ] **Step 3: Ensure a CLI at or above the version floor is on `PATH`, then run strict `tsc` — the same check `ci.yml`'s `📘 typescript export surface` job runs**

```bash
dotnet tool install --tool-path "$CLAUDE_JOB_DIR/tmp/aspire-cli" aspire.cli --version 13.5.3 2>/dev/null || true
export PATH="$CLAUDE_JOB_DIR/tmp/aspire-cli:$PATH"
cd samples/DemoAppHostTypeScriptCodeCatalog
aspire restore
npm ci
npx tsc --noEmit -p tsconfig.apphost.json
```

Expected: `aspire restore` succeeds (confirms the CLI floor is actually in effect — NU1605 means the
wrong CLI is still on `PATH`), `npx tsc` reports zero errors. A type error here means `asJava`'s
nested-lambda shape or `withPrepare`'s optional-object-parameter shape didn't project the way Task 3
assumed — stop and re-check the ATS export attributes on the C# side rather than fighting the
generated `.d.ts`.

- [ ] **Step 4: Commit**

```bash
git add samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts
git commit -m "docs: demonstrate asJava and withPrepare in the TypeScript code-catalog sample"
```

---

## Task 9: README and CHANGELOG

Implements: Design "Documentation".

**Files:**
- Modify: `README.md`
- Modify: `CHANGELOG.md`

**Interfaces:**
- None — documentation only, describing Tasks 1–8's shipped surface.

- [ ] **Step 1: Update the "Authoring the catalog in code" section's typed-handle caveat**

In `README.md`, the paragraph at (currently) lines 208–211 reads:

```
`myOptions` can be a plain `Dictionary<string, object>` — the same shape yaml's `<kind>:`
block produces — and today that's also the only shape available for the built-in `java`
kind from code: typed option handles (`AsJava`, `AsJavaScript`) are a later addition, not
yet shipped.
```

Replace it with:

```
`myOptions` can be a plain `Dictionary<string, object>` — the same shape yaml's `<kind>:`
block produces — which is the only shape available for an out-of-tree kind. The two
built-in kinds also have a typed alternative: `AsJava`/`AsJavaScript` hand the lambda a
fluent options handle instead —

```csharp
catalog.AddService("catalog")
    .WithRepository("https://github.com/spring-projects/spring-petclinic")
    .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080));
```

— which is sugar over `WithKind("java", …)`: calling it twice, or calling it after a plain
`WithKind` call for the same service, throws the same "already called" error `WithKind`
itself would.
```

- [ ] **Step 2: Add a `WithPrepare` paragraph**

Immediately after the method table (the one listing `WithRepository`/`WithUrl`/`WithContainer`/
`WithKubernetes` and the `"source"` each enables — currently around line 184), add:

```

A `"local"` service can also declare a `prepare:`-equivalent bootstrap command:

```csharp
catalog.AddService("catalog")
    .WithRepository("https://github.com/spring-projects/spring-petclinic")
    .WithKind("java", options)
    .WithPrepare(["./mvnw", "-q", "dependency:go-offline"], mode: "once");
```

`WithPrepare(command, windowsCommand: null, mode: null)` is the code-authoring equivalent of
yaml's `prepare:` block — see "`prepare`: a checkout that has to bootstrap itself" (under
`"local"` source options below) for what it runs and when. `mode` accepts the same four
spellings yaml does: `"oncePerCommit"` (the default), `"once"`, `"always"`, `"never"`.
```

(Prose reference, not a markdown link — `README.md:337`'s heading contains backticks and a
colon, and this repo's other cross-references in this section use prose pointers rather than
guessing GitHub's anchor-slug algorithm; e.g. the existing `container.scheme`/`kubernetes.scheme`
paragraph a few lines above points at "documented under the `"container"` and `"kubernetes"`
source sections below" the same way.)

- [ ] **Step 3: CHANGELOG — add to the existing `## [Unreleased]` section**

In `CHANGELOG.md`, under the existing empty `## [Unreleased]` header (`CHANGELOG.md:17`, directly
above `## [0.5.1]`), add:

```markdown
### Added

- **`WithPrepare` on the code-authored catalog** ([#134]). The code-authoring equivalent of yaml's
  `prepare:` block — a bootstrap command the `"local"` source runs inside the materialized checkout
  before the kind is allowed to judge it.
- **Typed `AsJava`/`AsJavaScript` handles** ([#134]). Sugar over `WithKind("java"/"javascript", …)`:
  each hands the caller a fluent options handle (`JavaKindOptionsBuilder`/
  `JavaScriptKindOptionsBuilder`) instead of a raw `Dictionary<string, object>`. The two shipped
  kinds' options classes (`JavaKindOptions`/`JavaScriptKindOptions`) stay `internal` — only the
  handles are public.
```

Confirm the `[#134]:` link definition already exists further down the file (it does — added by
Stage 1, at the line the earlier `grep -n "\[#134\]:" CHANGELOG.md` in this plan's own recon found);
no new link line is needed.

- [ ] **Step 4: Commit**

```bash
git add README.md CHANGELOG.md
git commit -m "docs: document WithPrepare, AsJava and AsJavaScript"
```

---

## Task 10: Whole-branch verification

Implements: nothing new — confirms Tasks 1–9 hold together, matching Stage 1's own final-review gate
before its PR opened.

**Files:** none (verification only).

- [ ] **Step 1: Full solution build, warnings as errors**

```bash
dotnet build ServiceSources.slnx -c Release -warnaserror
```

Expected: succeeds, zero warnings, including zero `ASPIREEXPORT013`.

- [ ] **Step 2: Full test suite, all three target frameworks**

```bash
dotnet test ServiceSources.slnx -c Release
```

Expected: PASS across `net8.0`, `net9.0`, `net10.0` — including every new test from Tasks 1–6 and
the full pre-existing suite (Stage 1's ~60 files' worth of tests must still be green; this stage
touched none of their production code, but confirm rather than assume).

- [ ] **Step 3: Both samples, run for real (not just typecheck) — repeat Tasks 7 and 8's Step 3 together in one pass**

```bash
export PATH="$CLAUDE_JOB_DIR/tmp/aspire-cli:$PATH"

cd samples/DemoAppHostCodeCatalog
cp servicesources.local.json.example servicesources.local.json
dotnet run --project DemoAppHostCodeCatalog.csproj &
CSHARP_PID=$!
sleep 20
curl -sf http://localhost:15888/ >/dev/null && echo "C# sample: dashboard reachable"
kill $CSHARP_PID
rm -f servicesources.local.json
cd ../..

cd samples/DemoAppHostTypeScriptCodeCatalog
aspire restore
npm ci
npx tsc --noEmit -p tsconfig.apphost.json && echo "TypeScript sample: tsc clean"
cd ../..
```

- [ ] **Step 4: `git log` sanity check — confirm every task's commit is present and in order**

```bash
git log --oneline main..HEAD
```

Expected: ten commits (one per task above), in Task-1-through-Task-9 order (Task 10 makes none).

- [ ] **Step 5: Open the PR**

```bash
git push -u origin HEAD
gh pr create --title "Author the service catalog in code, Stage 2: WithPrepare, AsJava/AsJavaScript (#134)" --body "$(cat <<'EOF'
## Summary

Stage 2 of #134's accepted design (`docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md`,
Staging table). Closes the last gap Stage 1 (#299) left open:

- `WithPrepare` on `ServiceDefinitionBuilder` — the code-authoring equivalent of yaml's `prepare:`
  block.
- Typed `AsJava`/`AsJavaScript` handles (`JavaKindOptionsBuilder`/`JavaScriptKindOptionsBuilder`) —
  sugar over `WithKind`, replacing the raw-dictionary-only path Stage 1 shipped for the two built-in
  kinds. `JavaKindOptions`/`JavaScriptKindOptions` stay `internal`.
- Both code-catalog samples (C# and TypeScript) updated to demonstrate the new surface.

No new validation logic anywhere — both additions route into exactly the code the yaml path already
validates through (`Prepare/PreparePlan.cs`, `JavaKindOptions.Parse`, `JavaScriptLocalKind`'s own
resolution).

## Acceptance criteria reached (#134)

1 and 2 now **in full** — parity with the yaml loader, for both C# and TypeScript AppHosts, closing
the gap #134's second comment raised. 3 and 4 were already full as of Stage 1.

## Test plan

- [ ] `dotnet build ServiceSources.slnx -c Release -warnaserror` — zero warnings
- [ ] `dotnet test ServiceSources.slnx -c Release` — net8/9/10 all green
- [ ] C# sample (`DemoAppHostCodeCatalog`) runs for real, dashboard reachable
- [ ] TypeScript sample strict-`tsc`s clean via the `📘 typescript export surface` CI job
EOF
)"
```

- [ ] **Step 6: Note the pre-existing CHANGELOG placement issue in the PR, don't fix it here**

If the PR reviewer (or CI) flags that Stage 1's `#134` entries sit under the already-released
`## [0.5.1]` instead of `## [Unreleased]`, point at this plan's Global Constraints note — it's a
known, separate issue from before this stage, not something this PR introduced or should silently
fix by rewriting release history.
