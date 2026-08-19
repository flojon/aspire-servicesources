# TypeScript AppHost Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AddService()` callable from a TypeScript Aspire AppHost by exporting it through
Aspire's Type System (ATS), and prove it works end-to-end with a TypeScript sample.

**Architecture:** Annotate the existing `AddService` extension method with `[AspireExport]` /
`[ResourceName]`; its return type (`IResourceBuilder<IResourceWithServiceDiscovery>`) is already
a built-in ATS handle type, so no other C# changes are needed. Add a TypeScript sample AppHost
that references this project locally and exercises `AddService` against the `"url"` source.

**Tech Stack:** .NET 8/9/10, `Aspire.Hosting` 13.4.6 (already referenced — ships ATS + the
`Aspire.Hosting.Integration.Analyzers` transitively), Node.js + TypeScript for the sample
AppHost, `aspire` CLI.

**Spec:** [docs/superpowers/specs/2026-08-19-servicesources-typescript-apphost-export-design.md](../specs/2026-08-19-servicesources-typescript-apphost-export-design.md)

## Global Constraints

- No `Aspire.Hosting` version bump — already on 13.4.6, which ships ATS GA.
- No new `PackageReference` for analyzer support — `Aspire.Hosting.Integration.Analyzers` ships
  transitively via `Aspire.Hosting`'s `buildTransitive` targets.
- `servicesources.yaml` / `servicesources.local.json` schemas and loaders are unchanged.
- The `ServiceResource` facade class stays internal / unexported — only the `AddService` method
  signature is annotated.
- Multi-target `net8.0;net9.0;net10.0` must still build clean after the attribute addition.

---

### Task 1: Export `AddService` via ATS attributes

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:48-60`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` (new test appended)

**Interfaces:**
- Consumes: existing `AddService(this IDistributedApplicationBuilder builder, string name)` at
  `ServiceSourcesBuilderExtensions.cs:48`, unchanged in behavior.
- Produces: the same method, now decorated with `[Aspire.Hosting.AspireExport]` on the method and
  `[Aspire.Hosting.ResourceName]` on the `name` parameter. No new public types or members.

This task is attribute-only — `AddService`'s runtime behavior does not change, so there is no
new *runtime* behavior to unit test. Instead, the "test" for this task is a compile-time
assembly-reflection check that the attributes are actually present with the expected shape,
which is the only way to unit-test "is this method correctly annotated" without an actual `aspire`
CLI invocation.

- [ ] **Step 1: Write the failing test**

Append to `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`:

```csharp
    [Fact]
    public void AddService_IsExportedToAts()
    {
        var method = typeof(ServiceSourcesBuilderExtensions).GetMethod(nameof(ServiceSourcesBuilderExtensions.AddService));
        Assert.NotNull(method);

        var exportAttribute = method!.GetCustomAttributes(typeof(AspireExportAttribute), inherit: false);
        Assert.Single(exportAttribute);

        var nameParameter = method.GetParameters().Single(p => p.Name == "name");
        var resourceNameAttribute = nameParameter.GetCustomAttributes(typeof(ResourceNameAttribute), inherit: false);
        Assert.Single(resourceNameAttribute);
    }
```

Add `using System.Linq;` to the top of the file if not already present.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter AddService_IsExportedToAts`
Expected: FAIL — either a compile error (`AspireExportAttribute`/`ResourceNameAttribute` not
found, since nothing references them yet in a `using` the test can see — they're in
`Aspire.Hosting`, already referenced transitively by the test project via the main project) or
an assertion failure (`Assert.Single` on an empty array) once it compiles.

- [ ] **Step 3: Annotate `AddService`**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, change:

```csharp
    public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
        this IDistributedApplicationBuilder builder, string name)
```

to:

```csharp
    [AspireExport]
    public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
```

No new `using` is needed — `AspireExportAttribute` and `ResourceNameAttribute` live directly in
the `Aspire.Hosting` namespace, already `using`'d at the top of this file (`using Aspire.Hosting.ApplicationModel;`
is present but the file is itself inside `namespace Aspire.Hosting.ServiceSources`; if the
attributes don't resolve, add `using Aspire.Hosting;` at the top of the file).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter AddService_IsExportedToAts`
Expected: PASS

- [ ] **Step 5: Run the full existing test suite to confirm no regression**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS (all existing tests, including `AddServiceTests.cs`'s other cases, unaffected)

- [ ] **Step 6: Build all target frameworks**

Run: `dotnet build src/Aspire.Hosting.ServiceSources -f net8.0 && dotnet build src/Aspire.Hosting.ServiceSources -f net9.0 && dotnet build src/Aspire.Hosting.ServiceSources -f net10.0`
Expected: all three succeed with no analyzer diagnostics from `Aspire.Hosting.Integration.Analyzers`
(watch the build output for any `ASPIRE***`-prefixed warnings/errors — there should be none for
a correctly-shaped export).

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs
git commit -m "Export AddService to Aspire's Type System for guest-language AppHosts"
```

---

### Task 2: TypeScript sample AppHost proving `AddService` works from TypeScript

**Files:**
- Create: `samples/DemoAppHostTypeScript/package.json`
- Create: `samples/DemoAppHostTypeScript/tsconfig.apphost.json`
- Create: `samples/DemoAppHostTypeScript/aspire.config.json`
- Create: `samples/DemoAppHostTypeScript/apphost.mts`
- Create: `samples/DemoAppHostTypeScript/servicesources.yaml`
- Create: `samples/DemoAppHostTypeScript/servicesources.local.json.example`
- Create: `samples/DemoAppHostTypeScript/.gitignore`
- Modify: `README.md` (Sample section, `README.md:252-266`)

**Interfaces:**
- Consumes: the exported `AddService` from Task 1, referenced via `.aspire/modules/aspire.mjs`
  after CLI codegen (generated file, not committed).
- Produces: nothing consumed by later tasks — this is a leaf/terminal verification task.

This task can't be driven by an automated `dotnet test`/`pytest` cycle — it exercises the
`aspire` CLI's own codegen and Node.js runtime, which aren't part of this repo's existing test
harness. Steps are manual-verification steps instead of automated test steps; each is still a
single, checkable action.

- [ ] **Step 1: Scaffold the project layout**

Create `samples/DemoAppHostTypeScript/package.json`:

```json
{
  "name": "demo-apphost-typescript",
  "private": true,
  "type": "module",
  "devDependencies": {
    "tsx": "^4.19.0",
    "typescript": "^5.7.0"
  }
}
```

Create `samples/DemoAppHostTypeScript/tsconfig.apphost.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "strict": true,
    "skipLibCheck": true,
    "outDir": "obj/typescript"
  },
  "include": ["apphost.mts", ".aspire/modules/**/*.mts"]
}
```

Create `samples/DemoAppHostTypeScript/.gitignore`:

```
node_modules/
obj/
.aspire/modules/
servicesources.local.json
```

The `.aspire/modules/` directory is CLI-generated output (per the multi-language integration
authoring docs — "do not edit these files"), so it's gitignored like `obj/`/`bin/` elsewhere in
this repo, not committed.

- [ ] **Step 2: Declare the local package reference**

Create `samples/DemoAppHostTypeScript/aspire.config.json`:

```json
{
  "appHost": {
    "path": "apphost.mts"
  },
  "packages": {
    "KoalaSoft.Aspire.Hosting.ServiceSources": {
      "path": "../../src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj"
    }
  }
}
```

This mirrors the existing C# sample's project-reference approach
(`samples/DemoAppHost/DemoAppHost.csproj`'s `<ProjectReference>`), but through `aspire.config.json`'s
path-based package declaration, per the multi-language integration authoring docs' testing
pattern ("reference your integration via `.csproj` path in `aspire.config.json`, not a version
number").

- [ ] **Step 3: Write the AppHost entry point**

Create `samples/DemoAppHostTypeScript/apphost.mts`. Reuse the same `"url"` source as the C#
sample (`inventory` pointing at httpbin.org) since it needs no local git checkout or container
runtime:

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const inventory = await builder.addService('inventory');

await builder.build().run();
```

The exact generated method name/casing (`addService` vs. `add_service`) and whether it returns
a plain value or a `Promise` won't be known until Step 5 generates `.aspire/modules/aspire.d.ts`
— adjust this file to match whatever the generator actually produces before running.

- [ ] **Step 4: Write the catalog**

Create `samples/DemoAppHostTypeScript/servicesources.yaml`:

```yaml
services:
  inventory:
    url:
      url: https://httpbin.org
```

Create `samples/DemoAppHostTypeScript/servicesources.local.json.example`:

```json
{
  "services": {
    "inventory": { "source": "url" }
  }
}
```

This is the same catalog/local-config shape the C# sample uses for its `inventory` service
(`samples/DemoAppHost/servicesources.yaml`), copied as-is to prove the config layer is
language-agnostic.

- [ ] **Step 5: Generate the TypeScript SDK and inspect it**

Run:
```bash
cd samples/DemoAppHostTypeScript
npm install
aspire add KoalaSoft.Aspire.Hosting.ServiceSources
```

Expected: `.aspire/modules/aspire.d.ts` is generated, and grepping it
(`grep -n "addService" .aspire/modules/aspire.d.ts`) shows a generated method whose parameter is
`string` and whose return type wraps `ResourceBuilder<ResourceWithServiceDiscovery>` (exact
generated names TBD by the actual codegen output — this is why Step 3's `apphost.mts` is
written to be adjusted afterward). Confirm no diagnostics were printed by `aspire add` about
`ServiceSourcesBuilderExtensions.AddService` specifically.

- [ ] **Step 6: Adjust `apphost.mts` to match the generated signature**

Update `apphost.mts` from Step 3 if the generated method name, casing, or async shape differs
from the placeholder written there (e.g. add `await` if not already present, fix the method
name).

- [ ] **Step 7: Run the AppHost end to end**

```bash
cp servicesources.local.json.example servicesources.local.json
aspire run
```

Expected: the Aspire dashboard starts and shows an `inventory` resource resolved to
`https://httpbin.org`, matching the equivalent `inventory` resource's behavior in the C# sample
(`samples/DemoAppHost`) when run the same way.

- [ ] **Step 8: Document the sample in the README**

In `README.md`, extend the existing `## Sample` section (`README.md:252-266`) with a short
paragraph pointing at `samples/DemoAppHostTypeScript` as the TypeScript equivalent, e.g. after
the existing `aspire run` code block:

```markdown
A TypeScript AppHost equivalent — proving `AddService()` works the same way from a guest
language via Aspire's Type System — lives in `samples/DemoAppHostTypeScript`:

\`\`\`bash
cd samples/DemoAppHostTypeScript
npm install
cp servicesources.local.json.example servicesources.local.json
aspire run
\`\`\`
```

- [ ] **Step 9: Commit**

```bash
git add samples/DemoAppHostTypeScript README.md
git commit -m "Add TypeScript sample AppHost proving AddService works from guest languages"
```

---

## Self-Review Notes

- **Spec coverage:** Task 1 covers the design doc's "Architecture" attribute-export bullets and
  the "no analyzer package needed" claim (verified at build time in Step 6). Task 2 covers the
  design doc's "Add a `samples/DemoAppHostTypeScript`..." bullet and the full "Verification
  Plan" section (SDK inspection + end-to-end smoke test). The design doc's "Explicitly Out of
  Scope" items (per-source exports, Python/Go samples, config schema changes, `aspire publish`)
  are intentionally not tasked here.
- **Placeholder scan:** Task 2's generated-method-name uncertainty is called out explicitly
  (not hidden as a TODO) because it depends on live `aspire` CLI codegen output this plan can't
  pre-compute — Steps 5–6 exist specifically to resolve it with a concrete grep-and-adjust
  action, not a "figure it out later" placeholder.
- **Type consistency:** `AddService`'s signature (`IDistributedApplicationBuilder`, `string
  name`, return `IResourceBuilder<IResourceWithServiceDiscovery>`) is used identically in both
  the design doc and both tasks.
