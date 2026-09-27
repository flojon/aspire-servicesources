# Authoring the catalog in code

[← Back to README](https://github.com/flojon/aspire-servicesources/blob/main/README.md)

Everything `servicesources.yaml` declares can be written in the AppHost's own language
instead — C# directly, or TypeScript (and any other Aspire guest language) through
Aspire's Type System — alongside yaml or in place of it. `AddServiceCatalog` takes a
catalog builder and covers all four sources:

```csharp
builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .WithRepository("https://github.com/example/orders", defaultRef: "main")
        .WithProject("src/Orders.Api/Orders.Api.csproj");

    catalog.AddService("inventory")
        .WithUrl("https://httpbin.org");

    catalog.AddService("payments")
        .WithContainer("nginxdemos/hello", port: 80, defaultTag: "latest")
        .WithKubernetes("payments", port: 8080, scheme: "https");
});

var orders = builder.AddService("orders");
```

The same idea from a TypeScript AppHost, through Aspire's Type System (the C# method
itself is still `AddService`, and the generated TypeScript name is `addService` too —
the catalog builder's `addService` and the AppHost builder's own `addService` (from the
separate `AddService` extension method) are distinct capabilities, so the two never
collide):

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addServiceCatalog(async (catalog) => {
  const orders = await catalog.addService('orders');
  await orders.withRepository('https://github.com/example/orders', { defaultRef: 'main' });
  await orders.withProject('src/Orders.Api/Orders.Api.csproj');

  const inventory = await catalog.addService('inventory');
  await inventory.withUrl('https://httpbin.org');
});
```

`AddServiceCatalog` must be called before the first `AddService(...)` call anywhere in the
AppHost — yaml-based `AddService` calls included — since a service is resolved as soon as
it's added; calling it after throws, naming the ordering problem. Call it near the top of
the AppHost, next to `UseDeferredCheckout()`. It can be called more than once — a helper
method can contribute its own entries — and calls append rather than replace.

**Which builder method enables which `source`**, since the method names are not the five
values you type into `servicesources.local.json`:

| Builder method | yaml it replaces | `"source"` it enables |
| --- | --- | --- |
| `WithRepository` + `WithProject` + `WithKind` | `repository:`, `project:`, `defaultRef:`, `kind:` | `"local"` |
| `WithUrl` | `url:` | `"url"` |
| `WithContainer` | `container:` | `"container"` |
| `WithKubernetes` | `kubernetes:` | `"kubernetes"` |
| *(none)* | *(none)* | `"disabled"` |

`"disabled"` needs no builder call and no yaml block of its own — a developer just writes
`"source": "disabled"` in their own `servicesources.local.json` for a service the catalog already
declares some other way. See [the `"disabled"` source](other-sources.md#disabled-source) below.

`WithDefaultSource(source)` is a separate call, not a `source` value of its own — it names which of
the five a developer gets when nothing configures this service explicitly, the code-authoring
equivalent of yaml's `defaultSource:`:

```csharp
catalog.AddService("orders")
    .WithRepository("https://github.com/example/orders", defaultRef: "main")
    .WithProject("src/Orders.Api/Orders.Api.csproj")
    .WithDefaultSource("local");
```

The same caveat as the yaml field applies: `WithDefaultSource("local")` means every developer, and
CI, clones by default unless CI pins its own source.

`WithContainer`/`WithKubernetes` both take a `scheme` parameter — the code-authoring
equivalent of yaml's `container.scheme`/`kubernetes.scheme` (documented under the
`"container"` and `"kubernetes"` source sections below). A service declaring both sources
sets them independently:

```csharp
catalog.AddService("payments")
    .WithContainer("nginxdemos/hello", port: 80, scheme: "http")
    .WithKubernetes("payments", port: 8080, scheme: "https");
```

Left unset on either call, `scheme` defaults to `http`, same as yaml.

A `"local"` service can also declare a `prepare:`-equivalent bootstrap command:

```csharp
catalog.AddService("catalog")
    .WithRepository("https://github.com/spring-projects/spring-petclinic")
    .AsJava(o => o.WithMavenGoal("spring-boot:run").WithPort(8080))
    .WithPrepare(["./mvnw", "-q", "dependency:go-offline"], mode: PrepareMode.Once);
```

`WithPrepare(command, windowsCommand: null, mode: PrepareMode.OncePerCommit)` is the
code-authoring equivalent of yaml's `prepare:` block — see "`prepare`: a checkout that has to
bootstrap itself" (under `"local"` source options below) for what it runs and when. `mode`
takes a `PrepareMode` value — `PrepareMode.OncePerCommit` (the default), `.Once`, `.Always`,
`.Never` — the enum behind yaml's four `mode` spellings (`"oncePerCommit"`, `"once"`,
`"always"`, `"never"`).

`WithPrepare`'s `command` parameter is required, so it has no code-authoring equivalent for a
Windows-only `prepare:` block — one that gives `windowsCommand` but no `command` at all, which
yaml can express and which runs on Windows only. A prepare step declared in code always has a
non-Windows command.

Calls are additive — `WithContainer` and `WithKubernetes` above both configure `payments`,
the same as two separate yaml keys would. A second call to the *same* method for one
service is a configuration error naming the service and the block, not a silent overwrite.
A service declared in both the code catalog and `servicesources.yaml` is an error too,
naming both sources — not a merge, and not a silent precedence rule.

A `"local"` service that isn't the built-in `dotnet` kind takes its options through
`WithKind`, the same primitive a third-party kind package builds on. Registering and using
one is three lines:

```csharp
builder.AddLocalKind("mykind", new MyKindHandler());
// ...
catalog.AddService("x").WithKind("mykind", myOptions);
```

`myOptions` can be a plain `Dictionary<string, object>` — the same shape yaml's `<kind>:`
block produces — which is the only shape available for an out-of-tree kind. The two
built-in kinds also have a typed alternative: `AsJava`/`AsJavaScript` hand the lambda a
fluent options handle instead —

```csharp
catalog.AddService("catalog")
    .WithRepository("https://github.com/spring-projects/spring-petclinic")
    .AsJava(o => o.WithMavenGoal("spring-boot:run").WithPort(8080));
```

— which is sugar over `WithKind("java", …)`: calling it twice, or calling it after a plain
`WithKind` call for the same service, throws the same "already called" error `WithKind`
itself would.

**A code-declared catalog still needs `servicesources.local.json`.** `AddServiceCatalog`
says what a service *is* — its repository, its URL, its container image — the same job
`servicesources.yaml` does. It does not decide how to resolve it for you personally: that
is still `servicesources.local.json`'s job, per developer, per service, with the same
`"source"` values (`"local"`/`"url"`/`"container"`/`"kubernetes"`/`"disabled"`) it always took. A
service declared only in code and never given a `servicesources.local.json` entry fails to
resolve exactly as a yaml-declared one would.

