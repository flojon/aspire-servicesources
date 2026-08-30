# Can a code-authored service catalog cross ATS? — measured findings

**Status:** Finding, for [issue #71](https://github.com/flojon/aspire-servicesources/issues/71)
**Date:** 2026-08-30
**Measured against:** Aspire CLI **13.5.1** (the installed CLI; 13.5.3 is the newest release),
`Aspire.Hosting` **13.5.1** in the probe assembly — the CLI pins that version for the generated
host project, so a probe on the repo's 13.5.2 floor fails restore with NU1605.

## Verdict

**The ATS objection in #71 does not hold.** A code-authoring catalog API — fluent, lambda-based,
DTO-based, or all three — crosses ATS today and reaches a TypeScript AppHost. This was measured
by generating an SDK from a probe assembly and running the result end to end, not reasoned about.

**It did not change in 13.5.** Every mechanism this relies on has been in the `Aspire.Hosting`
contract since **13.2.0**, the version ATS shipped in — before PR #62 was written. The blocker was
never a version gap. #71 generalised two narrow, correct measurements from PR #62 into a broad
claim ("a fluent or lambda-based catalog API very likely cannot cross that boundary") that is
false. #71 itself flagged that generalisation as unverified and asked for a measurement; this is it.

So the ATS constraint should be **struck from the decision entirely**.

**And the decision goes the other way: the catalog should become authorable in code.** Removing the
need for yaml is a goal of Aspire itself — the app model is code in the AppHost's own language,
which is what `AddProject`/`AddContainer` are for and what ATS extends to TypeScript and Python.
A yaml catalog in the middle of that works against the framework this package extends. With the
technical objection measured away, alignment with that direction is the deciding argument.

The sharing argument that was left standing does not survive examination either. `servicesources.yaml`
is not shared today — every AppHost repository carries its own copy, so it is *duplicated*, not
shared, and a second AppHost wanting the same service re-types it. Real cross-repository sharing is
what #11's registry is for, and a registry fetch is asynchronous — which also crosses ATS cleanly
(shapes 7a/7b). The yaml was not buying the property it was credited with.

## What was measured

A probe assembly (`AtsProbe`) exporting eleven candidate shapes, consumed by a TypeScript AppHost
via `aspire restore`. Full source in the appendix.

| # | C# shape | Generated TypeScript | Crosses? |
|---|---|---|---|
| 1a | `AddProbeDefinition(ProbeServiceDefinition)` — `[AspireDto]` | `addProbeDefinition(definition: ProbeServiceDefinition)` | ✅ |
| 1b | `AddProbeCatalogList(IReadOnlyList<Dto>)` | `addProbeCatalogList(definitions: ProbeServiceDefinition[])` | ✅ |
| 2a | `AddProbeCatalog(Action<ProbeCatalogBuilder>)` — lambda over an exported **class** | `addProbeCatalog(configure: (obj: ProbeCatalogBuilder) => Promise<void>)` | ✅ |
| 2b | `AddProbeCatalogViaInterface(Action<IProbeAnnotatedBuilder>)` — lambda over an exported **interface** | `addProbeCatalogViaInterface(configure: (obj: ProbeAnnotatedBuilder) => Promise<void>)` | ✅ |
| 2c | `ProbeCatalog()` returning the exported handle | `probeCatalog(): ProbeCatalogBuilderPromise` — chainable | ✅ |
| 3 | Returns an **unannotated** custom interface | *(nothing emitted)* | ❌ |
| 4a | `ProbeGeneric<T>(Action<T>) where T : class` | *(nothing emitted — dropped entirely)* | ❌ |
| 4b | `ProbeGenericResource<T>(Action<IResourceBuilder<T>>) where T : IResource` | `probeGenericResource(configure: (obj: Resource) => Promise<void>)` — **T erased to its constraint** | ⚠️ |
| 5 | Two overloads, disambiguated via `[AspireExport("id")]` + `MethodName` | `probeOverloadByName(value: string)` and `probeOverloadInt(value: number)` | ✅ |
| 6 | `[AspireUnion(typeof(string), typeof(Dto))] object` | `probeUnion(source: string \| ProbeServiceDefinition)` | ✅ |
| 7a | `Task<ProbeServiceDefinition>` return | `probeFetchAsync(name: string): Promise<ProbeServiceDefinition>` | ✅ |
| 7b | `Task<ProbeCatalogBuilder>` return | `probeCatalogAsync(): ProbeCatalogBuilderPromise` | ✅ |
| 8 | DTO with a **callback property** over a live builder | `bind?: (obj: ResourceWithEnvironment) => Promise<void>` | ✅ |

`aspire restore` reported no diagnostics for any of these, and the build raised no analyzer
warnings. Strict `tsc --noEmit` over the generated SDK plus the AppHost: **clean**.

### The end-to-end run

Codegen alone proves only that signatures appear. The fluent shape was also run:

```typescript
await builder.addProbeCatalog(async (catalog) => {
  await catalog.addNamed('orders', ProbeSourceKind.Local);
  await catalog.addDefinition({
    name: 'payments',
    kind: ProbeSourceKind.Container,
    repository: { repository: 'https://example.invalid/payments.git', defaultRef: 'main' },
    tags: ['billing', 'critical'],
    env: { DEMO: 'true' },
  });
});
const count = await builder.probeCatalogCount();
```

`aspire run --detach` produced:

```
[guest] host observed catalog count = 2

│ orders   │ Executable │ Finished │
│ payments │ Executable │ Finished │

[payments] probe-resource name=payments kind=Container tags=billing,critical
[orders]   probe-resource name=orders kind=Local tags=
```

So: the guest lambda executed, each fluent call inside it re-entered .NET on a handle to the C#
builder, the host observed the declarations, **two real Aspire resources were created from a
catalog authored entirely in TypeScript**, and the nested DTO's enum and string list arrived
intact. This is the whole loop, not a signature check.

### Why this works — the mechanism #71 was missing

`[AspireExport]` applies to **types**, not just methods. A type carrying it becomes an ATS *handle*:
the generated SDK emits a wrapper class holding an opaque handle and proxying method calls back to
.NET. With `ExposeMethods = true` / `ExposeProperties = true`, its instance members become
capabilities automatically. `AspireExportAttribute.RunSyncOnBackgroundThread` exists precisely for
this shape — its own documentation says it is for "exports that may invoke synchronous callback
delegates which in turn re-enter the remote host through ATS."

That is a fluent configuration lambda, described in Aspire's own API docs as a supported pattern.

PR #62's finding that a custom `IServiceBuilder` return type "is not generated at all" is
reproduced here as shape 3 — and shape 2b shows the cause: the type was not marked `[AspireExport]`.
Annotated, the same shape generates fine.

## Corrections to what the repo currently asserts

Both claims in `ServiceConfigurationExports.cs` and the README are *true as stated for the shapes
#62 tested*, but are recorded as general ATS limits, which they are not:

- **"Generic methods lose their type parameter."** True in effect, and the non-generic shims remain
  correct — but the mechanism is *erasure to the constraint*, not blanket erasure (shape 4b). An
  unconstrained generic is dropped outright (4a). `Configure<T>` still cannot cross, because `T`
  carries the *requested capability*, and erasing it to `IResource` destroys exactly that meaning.
  The shims were the right call; the reason is narrower than written.
- **"Overloads are silently dropped."** Only when they share a generated name. `[AspireExport("id")]`
  is documented for precisely this case ("use this overload only when disambiguation is needed, e.g.
  multiple overloads of the same method"), and `MethodName` renames the projection. Two same-named
  C# overloads both crossed (shape 5). The distinctly-named shims this package ships are still a
  reasonable design; they are not the only option.

Neither correction changes any shipped behaviour. Both are worth fixing in the docs so the next
decision is not made on an overstated constraint — as this one nearly was.

## Answers to #71's questions

**Can any code-authoring API cross ATS?** Yes — measured above, including the end-to-end run. And
notably, *better* than what ships today: none of these shapes trip
[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507), the missing
`*Promise` wrapper for a bare-interface return that forces the current `AddService` to require CLI
13.6.0+. A DTO- or handle-based catalog API type-checks clean on **13.5.1**. A code-authoring API
would have wider CLI reach than the API this package already exports.

**Does splitting the DTO from the domain type remove `IsReservedKindName` and the reflection-derived
validation?** **No** — and this is the finding that most changes #71's cost/benefit.

The reserved-name rule is a consequence of the **document layout**, not of `ServiceMetadata` doing
double duty. A kind's options block is a sibling key named by the kind:

```yaml
catalog:
  kind: java
  java:            # sibling to url:, container:, kubernetes:
    mavenGoal: spring-boot:run
```

Any DTO with typed `url`/`container`/`kubernetes` properties collides with a kind named `url`,
whatever domain type sits behind it. Splitting the DTO out changes nothing here.

What *does* remove it is nesting the options under a fixed key:

```yaml
catalog:
  kind: java
  options:         # fixed key; kind names collide with nothing
    mavenGoal: spring-boot:run
```

Then `RawServiceCatalog` fishes a known key rather than a kind-named one, `IsReservedKindName`
deletes, and `LocalKindRegistry.Register` loses a rejection rule. That is a **yaml format change** —
cheap, mechanical, and completely independent of any typed-model or code-authoring work. It should
not be bundled with either.

The reflection-derived unknown-key validation should stay regardless: deriving a format's valid keys
from the type that binds it is correct, self-maintaining, and the comment in `ServiceCatalogLoader`
explains why it was built that way. It is only a smell while that type is also the domain type.

**Is there a use case yaml genuinely cannot serve?** Only live-binding targets (#6) — and even those
are not C#-only, which #71 assumed. A `[AspireDto]` may carry a **callback property** typed over a
live resource builder (shape 8: `bind?: (obj: ResourceWithEnvironment) => Promise<void>`). So a
TypeScript AppHost could author a requirement whose binding is a real builder callback. The
declaration still belongs in yaml; the binding still needs code — in either language.

**If both a code API and yaml exist, which wins?** No measurement needed, but the issue rightly
rules out silent precedence. Recommendation: a service declared in both is an **error naming both
sources**, not a merge and not a precedence rule. Code-authored entries and yaml entries would
occupy one namespace, and a duplicate is far more likely a mistake than an intentional override.
Per-developer overrides already have a home — `servicesources.local.json` — and that layering
should not be duplicated at the catalog level.

**Does a typed catalog help or hinder #11?** Neither, and the async/sync timing problem is smaller
than #71 feared. `Task<T>` capabilities cross ATS cleanly (shapes 7a/7b), and every generated
TypeScript call is awaited anyway, so a guest AppHost could await a remote registry fetch natively.
The synchronous-composition constraint is a **C#-side** problem only, it exists regardless of
authoring format, and #71 is right that it should not be smuggled into this decision.

## Recommendation

**Make the catalog authorable in code, and make yaml optional.** "Optional" rather than "deleted":
the goal is removing the *need* for yaml, which is what Aspire's own no-yaml positioning means. The
loader stays for AppHosts that already have a `servicesources.yaml`, and for the case yaml genuinely
serves — editing the catalog without rebuilding the AppHost.

1. **Add a code-authoring API as the primary surface**, in the AppHost's own language. Measured to
   work from both C# and TypeScript; sketch below.
2. **Keep the yaml loader as one catalog provider**, not the definition of the format. A service
   declared in both is an **error naming both sources** — not a merge, not silent precedence.
   Per-developer source selection stays where it is, in `servicesources.local.json` (#69).
3. **Let #11's registry be the sharing story**, which is what it was always for. Async fetch crosses
   ATS, so guest AppHosts are not excluded from it.
4. **Re-scope #133 (kind-options nesting) in light of this.** Kind options authored in code have no
   name collision at all — there are no yaml keys to collide with — so #133 shrinks to "stop the
   flat layout getting worse" rather than a full deprecation window with a migration. If code
   authoring lands first, the cheapest resolution may be to freeze the flat form and let
   `IsReservedKindName` apply only to yaml-declared kinds.
5. **The DTO/domain split** still does not deliver what #71 claims for it (see above), but a code
   API needs a domain type that is not a yaml DTO — so the split now happens as a *consequence* of
   (1) rather than as a standalone cleanup. That is the right order: build the domain type the code
   API needs, and let the yaml DTO become one way to populate it.
6. **Correct the two overstated ATS constraints** in `ServiceConfigurationExports.cs`, its tests and
   the README — applied alongside this finding, since the shims they justify are unchanged.
7. **Keep `AddServiceCatalog` separate from `AddService`**, rather than growing an optional inline
   definition on the latter. This refines (1). "What a service *is*" and "I depend on this service"
   being distinct statements is the seam this package exists to provide: `AddProject<T>()` conflates
   them, and that conflation is precisely why it cannot express a service in another repository. An
   inline form would re-couple the two at the one point the package is built to separate. Three
   consequences follow. A second AppHost consuming the same service would have nowhere to put a
   shared definition, making the per-repository duplication structural rather than incidental — and
   leaving #11's registry with no catalog object to return. Ordering becomes per-service rather than
   one rule about one call, so the "declared after the first `AddService`" error gets harder to
   phrase and easier to trip. And the costs are asymmetric: inline sugar can be layered over a
   separate catalog later without a break, while inline-only to separate cannot. The ergonomic
   objection — two calls to declare one service in the single-AppHost case — is real, and is exactly
   what that later sugar can address if the friction proves real rather than theoretical.

### Sketch, for the implementation issue to argue with

```csharp
builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .FromRepository("https://github.com/dotnet/aspire-samples", defaultRef: "main")
        .WithProject("samples/health-checks-ui/HealthChecksUI.ApiService/HealthChecksUI.ApiService.csproj");

    catalog.AddService("inventory").FromUrl("https://httpbin.org");
    catalog.AddService("payments").FromContainer("nginxdemos/hello", port: 80);
});
```

The satellite packages extend the same builder, which is where the kind-name problem disappears —
`.FromRepository(...).AsJava(o => o.MavenGoal("spring-boot:run"))` has no namespace to collide with.

The TypeScript equivalent is the shape measured end to end above:

```typescript
await builder.addServiceCatalog(async (catalog) => {
  await catalog.addService('payments').fromContainer('nginxdemos/hello', 80);
});
```

Open questions for that issue: whether the catalog must be declared before the first `AddService`
(it must, given eager resolution — see #62), and whether yaml is targeted for removal at 1.0 or kept
indefinitely.

Separate-versus-inline is answered in (7) above, with one caveat about the evidence for it: **every
shape measured in this finding uses the separate form.** `builder.addServiceCatalog(async (catalog)
=> ...)` is what ran end to end. An inline `AddService("payments", s => s.FromContainer(...))` would
put a configuration lambda in the same exported method as `AddService`'s bare-interface
`IResourceBuilder<T>` return, and that combination is not among the eleven shapes probed here. (7)
argues against inline on design grounds that stand on their own, but if it is revisited on
ergonomics, that combination needs measuring first — to the same standard as the rest of this
document.

Follow-ups: the kind-options nesting is filed as #133 and wants re-scoping per (4). #71 stays open as
the parent of the code-authoring work rather than being closed as "not proceeding".

## Appendix — reproducing

Probe assembly targeting `net10.0` with `<PackageReference Include="Aspire.Hosting" Version="13.5.1" />`
(match the CLI's version or restore fails NU1605), plus a TypeScript AppHost whose
`aspire.config.json` references it by path:

```json
{ "appHost": { "path": "apphost.mts" }, "packages": { "AtsProbe": "../lib/AtsProbe.csproj" } }
```

The shapes that matter, condensed:

```csharp
public enum ProbeSourceKind { Local, Container, Url, Kubernetes }

[AspireDto]
public class ProbeRepositoryInfo
{
    public string Repository { get; set; } = "";
    public string? DefaultRef { get; set; }
}

[AspireDto]
public class ProbeServiceDefinition
{
    public string Name { get; set; } = "";
    public ProbeSourceKind Kind { get; set; }
    public ProbeRepositoryInfo? Repository { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public IReadOnlyDictionary<string, string> Env { get; set; } = new Dictionary<string, string>();
}

// The fluent catalog builder, exported as an ATS handle type.
[AspireExport(ExposeMethods = true, ExposeProperties = true)]
public class ProbeCatalogBuilder
{
    private readonly List<ProbeServiceDefinition> _services = [];
    public int Count => _services.Count;
    public ProbeCatalogBuilder AddDefinition(ProbeServiceDefinition definition) { _services.Add(definition); return this; }
    public ProbeCatalogBuilder AddNamed(string name, ProbeSourceKind kind) { _services.Add(new() { Name = name, Kind = kind }); return this; }
    public IReadOnlyList<ProbeServiceDefinition> ToDefinitions() => _services;
}

public static class ProbeExports
{
    // The fluent catalog shape #71 assumed could not cross.
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IDistributedApplicationBuilder AddProbeCatalog(
        this IDistributedApplicationBuilder builder, Action<ProbeCatalogBuilder> configure)
    {
        var catalog = new ProbeCatalogBuilder();
        configure(catalog);
        foreach (var d in catalog.ToDefinitions())
        {
            builder.AddExecutable(d.Name, "/bin/echo", ".", $"probe-resource name={d.Name} kind={d.Kind}");
        }
        return builder;
    }

    // Overloads, disambiguated — both survive codegen.
    [AspireExport("probeOverloadByName")]
    public static IDistributedApplicationBuilder ProbeOverload(this IDistributedApplicationBuilder b, string v) => b;

    [AspireExport("probeOverloadByNameInt", MethodName = "probeOverloadInt")]
    public static IDistributedApplicationBuilder ProbeOverload(this IDistributedApplicationBuilder b, int v) => b;

    // Union parameter, async return, and a DTO carrying a live-binding callback.
    [AspireExport]
    public static IDistributedApplicationBuilder ProbeUnion(
        this IDistributedApplicationBuilder b,
        [AspireUnion(typeof(string), typeof(ProbeServiceDefinition))] object source) => b;

    [AspireExport]
    public static Task<ProbeServiceDefinition> ProbeFetchAsync(this IDistributedApplicationBuilder b, string name) =>
        Task.FromResult(new ProbeServiceDefinition { Name = name, Kind = ProbeSourceKind.Url });
}
```

Then `aspire restore`, read `.aspire/modules/aspire.mts`, `npx tsc --noEmit -p tsconfig.apphost.json`,
and `aspire run --detach --non-interactive --format Json`. Switching CLI builds can leave a stale
generator behind — `rm -rf .aspire` before regenerating
([microsoft/aspire#19603](https://github.com/microsoft/aspire/issues/19603)).

### Two nuances worth knowing before designing on this

- **`required` does not project.** A C# `required string Name` generates `name?: string`. Every DTO
  property is optional in TypeScript, so required-ness has to be validated host-side and reported as
  a runtime error. A DTO-authored catalog inherits the same validation burden the yaml loader
  already carries — it does not get schema enforcement for free.
- **`ExposeProperties` projects a property as a function**, not a field: `catalog.count` is
  `() => Promise<number>`, so it reads `await catalog.count()`. Easy to get wrong, and `tsc` will
  not catch it when the value is only interpolated into a string.
