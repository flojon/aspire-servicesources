# Configuring and consuming a resolved service

[← Back to README](https://github.com/flojon/aspire-servicesources/blob/main/README.md)

### Overriding `servicesources.local.json`

The file is read through the AppHost's own `IConfiguration`, as the **lowest**-precedence source in
the standard provider chain, under the key `ServiceSources:Services:<service>`. It is still the
place a developer normally writes a source selection, and a `.NET` or TypeScript AppHost authors it
identically — but every provider above it can override an entry without the file being touched. A
catalog's own `defaultSource` (see above, under [Getting started](https://github.com/flojon/aspire-servicesources/blob/main/README.md#getting-started)) sits below
even this file — see the row above the base:

| Layer | Overrides the file? |
| --- | --- |
| A catalog's `defaultSource` | no — it sits *below* the file; used only when neither the file nor anything above it sets the service's `source` |
| `servicesources.local.json` | — (the base) |
| `appsettings.json` | yes |
| `appsettings.{Environment}.json` | yes |
| User secrets | yes (requires a `UserSecretsId` in the AppHost csproj; without one the layer is simply absent) |
| Environment variables | yes |
| Command-line arguments | yes |

> **The `appsettings` layers need the file in the AppHost's output directory.** An AppHost project
> ships no `appsettings.json`, so unlike a web project it has no item copying that pattern to
> `bin/`, and a file placed beside the `.csproj` is silently never found — there is no error,
> the layer is simply absent. Add it explicitly:
>
> ```xml
> <ItemGroup>
>   <Content Include="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
> </ItemGroup>
> ```

> **The `ServiceSources:*` keys reach the AppHost's own `IConfiguration` on its first ServiceSources
> call, not before.** `servicesources.local.json` is a file of ours, read from the AppHost directory
> and re-keyed into the chain by whichever ServiceSources method the AppHost calls first — a call
> like `UseDeferredCheckout()` or `AddLocalKind()`, or the first `AddService()`. A read placed
> *above* all of them sees the chain without that layer, so a selection written only in the file
> comes back `null`, silently, since a missing key is not an error:
>
> ```csharp
> // null — nothing of ours has been called yet, so the file is not in the chain.
> var source = builder.Configuration["ServiceSources:Services:orders:source"];
>
> builder.UseDeferredCheckout();
>
> // "repository" — the file joined the chain on the line above.
> source = builder.Configuration["ServiceSources:Services:orders:source"];
> ```
>
> Reading these keys from an AppHost should be rare. Scoping a declaration to one source is what
> sends an AppHost looking for them, and
> [`ServiceResource`'s native vocabulary](#configuring-a-resolved-service) already does that
> scoping for you.

The immediate payoff is a **single run** with a different source and no edit to a file you'd have to
remember to change back. `source` itself isn't nested under a block, so this still works verbatim:

```bash
ServiceSources__Services__orders__Source=url dotnet run
```

Overriding a *field* works the same way, but gains its source's block segment —
`ServiceSources__Services__orders__Local__Ref`, `ServiceSources__Services__orders__Container__Tag`,
and so on. (`__` is the .NET configuration separator for `:`, and is what you want on every
platform.) Setting one of these to a blank value *unsets* the field, rather than setting it to an
empty string — `ServiceSources__Services__orders__Local__Path=` leaves the service with no `path`
at all, even one `servicesources.local.json` (or a layer in between) configured. It does not fall
back to that lower layer's value: configuration merges the layers *before* this package sees them,
so the blank is what arrives and the field ends up absent — which for `path` means the service gets
its managed checkout, exactly as if no layer had ever named one.

Blank means *empty*, exactly, whatever the field's type. A value of one or more spaces is refused
rather than read as either an unset field or a value of its own:
`ServiceSources__Services__orders__Kubernetes__Port=` drops the port, and
`ServiceSources__Services__orders__Local__Path=" "` is an error naming the spelling that works
rather than an override silently discarded — which is what a stray space surviving a CI variable
used to cost, leaving the service on its managed checkout with nothing said.

A service whose name contains a hyphen — `order-service`, say — makes a variable name a shell won't
accept as an inline assignment, so pass it through `env` instead:

```bash
env 'ServiceSources__Services__order-service__Source=url' dotnet run
```

The key itself is fine either way; it's only the one-line `NAME=value command` form that needs this.

The nesting is what makes this override story work at all: switching `source` from a higher layer
leaves the previous source's block sitting in the file, unread rather than removed. Nothing has to
be deleted from `servicesources.local.json` to switch a service away from the source it names there
— the fields for every other source can sit in the file unused, ready for the next switch back.

CI is the other case. A build agent has no developer to pick sources for it, and cloning every
service to run one test is waste, so pin them from the environment and ship no file at all:

```yaml
env:
  ServiceSources__Services__orders__Source: container
  ServiceSources__Services__payments__Source: container
```

**Named profiles** fall out of the same mechanism. Put the cluster-facing selection in
`appsettings.Cluster.json` next to the AppHost:

```json
{
  "ServiceSources": {
    "Services": {
      "orders": {
        "source": "kubernetes",
        "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 }
      }
    }
  }
}
```

and choose it per run by passing the environment as an argument to the AppHost:

```bash
aspire run -- --environment Cluster     # everything after -- goes to the AppHost
dotnet run -- --environment Cluster     # or launching the AppHost directly
```

**`DOTNET_ENVIRONMENT=Cluster` does not work under `aspire run`.** The CLI sets
`ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` to `Development` itself when it launches the
AppHost, so a value exported in your shell is overwritten and the profile is silently not
selected — you get the base file's selection with no indication that the profile was ignored.
The variable route only works when you run the AppHost yourself with `dotnet run`. The
command-line form above works in both.

Note the extra `ServiceSources` root: inside the AppHost's shared configuration the entries are namespaced, while `servicesources.local.json` keeps its bare
`services` root because it is a file of ours, read from the AppHost directory and re-keyed as it
joins the chain.

**A grouped repository ([above](local-source.md#several-services-from-one-repository)) has its own sibling root,
`repositories`**, keyed by the repository's name rather than by any one member service — a
developer overriding a group's ref writes it once, for every member, instead of naming the service
that happens to be first in the group:

```json
{
  "repositories": {
    "monorepo": { "ref": "feature/checkout-redesign" }
  }
}
```

`ServiceSources__Repositories__monorepo__Ref` is the environment-variable spelling, the same
pattern `ServiceSources__Services__<service>__Local__Ref` uses. `path` exists on the shape but is
reserved rather than implemented — a group's shared checkout is not yet redirectable in one
setting, so a non-null value is a configuration error naming the repository; use a member's own
`local.path` to split it out individually instead (see above).

Two failures are reported differently on purpose, because a typo in a configuration key produces an
empty section rather than an error:

- **Nothing configured anywhere** — `ServiceSources:Services` is empty in every source. The message
  says so, names the `servicesources.local.json` path it looked for and whether it was found, and
  lists every source consulted.
- **This one service isn't configured** — other services resolved, this one has no entry. The
  message names `ServiceSources:Services:<service>:source` and the environment variable that would
  set it.

## Configuring a resolved service

`AddService()` returns an `IResourceBuilder<ServiceResource>` — a facade that dual-writes
configuration to the real resource Aspire runs — so the AppHost can inject its own configuration
directly, with Aspire's own extension methods: connection strings, generated secrets, a sibling's
endpoint, wait ordering. Values like these come from the AppHost's own graph and can't be written
into `servicesources.yaml`/`servicesources.local.json`.

`ServiceResource` implements `IResourceWithServiceDiscovery`, `IResourceWithEnvironment`,
`IResourceWithArgs`, `IResourceWithEndpoints` and `IResourceWithWaitSupport`, so
`WithEnvironment`, `WithReference`, `WithArgs`,
`WithEndpoint`/`WithHttpEndpoint`/`WithHttpsEndpoint`/`WithExternalHttpEndpoints` and
`WaitFor`/`WaitForCompletion` all bind directly — no capability-naming wrapper needed. `WithCommand`
binds too, on Aspire's `IResource` constraint rather than any of those five:

```csharp
var backend = builder.AddService("backend")
    .WithReference(ordersDb)
    .WithEnvironment("DBPASSWORD", postgres.Resource.PasswordParameter)
    .WithEnvironment("ENCRYPTIONKEY", builder.AddParameter("EncryptionKey", new GenerateParameterDefault(), secret: true))
    .WithEnvironment("Services__CommonAuth", commonAuth.GetServiceEndpoint())
    .WaitForCompletion(migrationService);
```

`Unwrap<T>(configure)` reaches past the facade to the real, source-specific resource, for anything
native vocabulary doesn't cover — a non-`dotnet` local kind's own extension methods, for example:

```csharp
backend.Unwrap<JavaScriptAppResource>(js => js.WithRunScript("dev"));
```

It returns `backend` itself, so native calls chain after it. When `backend` doesn't resolve to a
`JavaScriptAppResource`, the delegate is skipped and logged rather than failing — see below.

**Native calls are skipped for the `"url"`, `"kubernetes"` and `"disabled"` sources**, and the skip
is logged at startup. None of the three has a local process this AppHost's own configuration should
reach: a `"url"` service is already running elsewhere, a `"kubernetes"` service is a `kubectl
port-forward` in front of something already running elsewhere, and a `"disabled"` service is not
running at all, deliberately — so environment variables applied here would configure `kubectl`
rather than the service, or configure nothing anyone will ever see. A `"url"`/`"kubernetes"`
service is expected to be configured wherever it actually runs; a `"disabled"` one is expected to
be switched back on first. This includes `WithHttpHealthCheck` — a `"kubernetes"` service's
port-forward has a real local endpoint, but the call still dual-writes onto the `kubectl` process
rather than the service behind it, so it's skipped the same as any other configuration call rather
than treated as an exception alongside wait ordering.

The one exception is **wait ordering on a `"kubernetes"` service**, which still applies:
`WaitFor`/`WaitForCompletion` reach a real, registered `kubectl port-forward` executable, and
holding *that* back until a migration finishes is exactly what the AppHost asked for. Only
configuration that would land on the wrong process is dropped. A `"url"` or `"disabled"` service
skips wait ordering too, since neither has a registered resource for Aspire to hold back.

That is this service waiting for something else. The other direction — something else waiting for
*this* service, `consumer.WaitFor(service)` — is dropped for `"url"` and `"disabled"` and honoured
for every other source, including `"kubernetes"`. That drop is reported in the same message as the
service's skipped calls. See [the `"url"` source](other-sources.md#url-source) and
[the `"disabled"` source](other-sources.md#disabled-source).

Skipping rather than failing is deliberate: a developer switching a service to a remote source in
their own `servicesources.local.json` must not break a `Program.cs` they don't own. You'll see:

```
warn: Aspire.Hosting.ServiceSources
      Service 'backend': skipped WithEnvironment because its source is 'kubernetes' — it resolves
      to a 'kubectl port-forward' in front of an already-running service, so the configuration
      would reach kubectl rather than the service. ...
```

`Unwrap<T>(configure)` skips the same way. On `"url"`, `"kubernetes"` and `"disabled"` the delegate
never runs and the skip joins the service's message above. It also skips when a reachable source
resolves to something other than a `T` — a `java` service a developer switched to `"container"`
resolves to a container, not a `JavaAppExecutableResource` — and logs what the service resolved to
instead. It follows the same wait-ordering exception: `Unwrap<IResourceWithWaitSupport>(...)` on a
`"kubernetes"` service still runs, against the port-forward.

That skip is only for a mismatch some source switch could undo. When **no** source the service's
catalog entry declares could ever resolve to a `T` — `Unwrap<ProjectResource>(...)` on a
`kind: java` service with a `container:` block, say — the call can never apply for anyone, so it
throws at startup, naming each declared source and the type it resolves to. A local kind that
doesn't declare its resource type (see [implementing a kind](kinds.md#implementing-a-kind)) is
assumed to match anything, so it never causes this throw.

The parameterless `Unwrap<T>()` **throws** in every one of those cases instead — it has to return
a builder, and handing back the `kubectl` executable would silently configure the wrong process.
Use it only where the AppHost genuinely requires that resource type, or needs the builder itself as
a value; use the delegate form for anything that should survive a source switch.

### From a guest-language AppHost

`ServiceResource`'s declared shape is what Aspire's Type System reads to generate a handle, so its
native vocabulary — `withEnvironment`, `withReference`, `withArgs`, `withEndpoint`,
`withHttpEndpoint`/`withHttpsEndpoint`/`withExternalHttpEndpoints`, `withCommand`, `waitFor`,
`waitForCompletion`, whether
Aspire's own or shadowed in this package — projects onto the generated `addService(...)` handle
exactly as it does in C#:

```typescript
const payments = await builder
  .addService('payments')
  .withEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withReference(inventory);
```

Out-of-band sources (`"url"`, `"kubernetes"`, `"disabled"`) are skipped and logged exactly as on the C# side,
including the wait-ordering exception for `waitFor`/`waitForCompletion` against a `"kubernetes"`
service. `Unwrap<T>()` itself has no TypeScript equivalent — it is a generic method, and Aspire's
Type System erases a generic method's type parameter to its constraint, which here is exactly the
resource type being requested — so reaching a non-`dotnet` kind's own vocabulary from a guest
language needs a native Aspire integration for that kind, the same as it would without this
package.

## Naming a service's endpoint

A consumer that wants the service's URL asks for an endpoint. Aspire names endpoints, and
`GetEndpoint("https")` looks like the obvious spelling — but the endpoint *name* a resolved service
exposes is decided by whichever source resolved it:

| Source | Endpoint name |
|---|---|
| `"repository"`/`"path"`, `kind: dotnet` | whatever the launch profile's `applicationUrl` declares (`http`, `https`, or both) |
| `"repository"`/`"path"`, `kind: javascript` | `http` |
| `"repository"`/`"path"`, `kind: java` | the configured `java.scheme`, `http` unless set |
| `"url"` | the configured URL's scheme |
| `"kubernetes"`, `"container"` | the configured `scheme`, `http` unless set |
| `"disabled"` | *(none — no endpoint at all)* |

`"path"` shares every row with `"repository"` here — the endpoint comes from the same kind
machinery reading the same checkout shape, whether that checkout was cloned or was already there.

So naming a scheme resolves only while the service happens to be on a source that produces it.
Switch that service and the consumer breaks — and it breaks *late*: composition succeeds, and the
throw comes from Aspire's `ExpressionResolver` when the consumer's environment is gathered, so it
surfaces as a `FailedToStart` on the **consumer** naming a service the consumer never changed:

```
System.InvalidOperationException: The endpoint `https` is not defined for the resource
`common-auth`. Available endpoints: `http`.
```

`GetServiceEndpoint()` is the portable spelling. It asks for *the* endpoint the service exposes and
survives a source switch:

```csharp
var commonAuth = builder.AddService("common-auth");

builder.AddProject<Projects.Web>("web")
    .WithEnvironment("Services__CommonAuth", commonAuth.GetServiceEndpoint());
```

It resolves to the endpoint named `https` if there is one, else `http`, else the service's only
endpoint whatever it's named — the same order Aspire's own service discovery resolves
`"https+http://"` in, so a service exposing both hands back the endpoint Aspire would have picked
itself. It throws at composition time, naming the service and its source, if the service exposes no
endpoint at all or exposes several with none named `http` or `https`; in that last case there's no
single endpoint to mean, so name the one you want with `GetEndpoint("<name>")`.

The endpoint is chosen when you call it, so call it after any `WithHttpEndpoint`/`WithHttpsEndpoint`
call that adds one. The `EndpointReference` it returns is lazy in the usual way — the URL resolves
once Aspire has allocated the port.

`WithReference(service)` plus service discovery is portable too, and is the better fit when the
consumer speaks service discovery: it injects every endpoint the service has under
`services__<name>__<scheme>__<index>`, and a client resolving `https+http://common-auth` picks
whichever is there. `GetServiceEndpoint()` is for the case a plain URL in a plain environment
variable is what the consumer reads.

`GetEndpoint("<scheme>")` still has its place — a service you know will never move off `"repository"`,
or an endpoint you added yourself through `WithHttpEndpoint`/`WithHttpsEndpoint`. Just don't reach
for it across a service whose source a developer chooses.

From a guest-language AppHost it's `getServiceEndpoint()`, and the value flows into Aspire's own
`withEnvironment`:

```typescript
await builder
  .addExecutable('probe', process.execPath, '.', ['-e', probeScript])
  .withEnvironment('INVENTORY_URL', inventory.getServiceEndpoint());
```

