# Samples

The repository carries two runnable AppHosts under
[`samples/`](https://github.com/flojon/aspire-servicesources/tree/main/samples).

## C# AppHost: `samples/DemoAppHost`

A minimal working AppHost demonstrating all three easily-runnable sources: `orders` via a real
managed `"repository"` git checkout (a small project cloned from
[`dotnet/aspire-samples`](https://github.com/dotnet/aspire-samples)), `inventory` via the
`"url"` source (pointing at [httpbin.org](https://httpbin.org), a live public test API), and
`payments` via the `"container"` source (the `nginxdemos/hello` hello-world image) — run it to
see the whole flow end to end. (`"kubernetes"` isn't demoed here since it needs a real cluster
and `kubectl`; see [its page](../sources/kubernetes.md).)

```bash
cd samples/DemoAppHost
cp servicesources.local.json.example servicesources.local.json
aspire run
```

It also carries a `catalog` service showing `kind: java` — a `"repository"` checkout of
[Spring PetClinic](https://github.com/spring-projects/spring-petclinic) run with its own Maven
wrapper; `java` being a built-in kind, no `Program.cs` registration is needed. `AddService("catalog")`
is commented out and the service is left out of `servicesources.local.json.example`, since unlike
the three above it needs a JDK. To run it, do both: uncomment the call and add
`"catalog": { "source": "repository" }` to your `servicesources.local.json`.

## TypeScript AppHost: `samples/DemoAppHostTypeScript`

A TypeScript AppHost equivalent — proving `AddService()` is correctly exported and registers with
Aspire's Type System from a guest language, and that a resolved service can be
[configured from TypeScript](../guides/configuration.md#from-a-guest-language-apphost).

```bash
cd samples/DemoAppHostTypeScript
npm install
cp servicesources.local.json.example servicesources.local.json
aspire restore
aspire run
```

Both of its services use the `"container"` source so that `payments` can
`withReference(inventory)`: a `"url"` service runs out of band, and a container consumer of one is
[rejected up front](../sources/url.md). A third resource, the `probe` executable, hands the same
`inventory` handle to Aspire's *own* `withReference()` and to `getServiceEndpoint()`, and prints
what each injected — so it shows as *Exited*, not Running, and those two log lines are where you
see both the native service-discovery path and the
[portable endpoint accessor](../guides/configuration.md#naming-a-services-endpoint) working.

!!! note "Requires Aspire CLI 13.5.3+"
    The CLI pins its own Aspire version for the host project it generates, so a CLI older than this
    package's Aspire floor (13.5.2) fails `aspire restore` with
    `NU1605: Detected package downgrade: Aspire.Hosting from 13.5.2 to 13.5.1` before codegen even
    runs. 13.5.3 is the first release that pins high enough. On it, the generated SDK type-checks
    clean under strict `tsc` and the sample runs end-to-end — `withReference()` on the
    `addService()` result injects the resolved service's discovery variables into the consuming
    resource, e.g. `services__inventory__http__0=http://inventory.dev.internal:80` pointing at the
    running `inventory` container.

Switching between CLI builds can leave a stale code generator under `.aspire/`, so remove that
directory before regenerating:
[microsoft/aspire#19603](https://github.com/microsoft/aspire/issues/19603).

### Background: why this no longer needs Aspire 13.6

This sample used to require an unreleased 13.6.0, and that requirement is gone — first because the
ten configuration shims' bare-interface receiver stopped being what carried the codegen wrapper
pair, and now because `AddService()` returns `ServiceResource`, a concrete, sealed class, which was
never affected by the bare-interface issue in the first place.

Aspire's TypeScript codegen did not emit a `*Promise`/`*PromiseImpl` wrapper pair for a bare Aspire
interface (`IResourceBuilder<IResourceWithServiceDiscovery>`, which is what `AddService` used to
return), so the generated SDK referenced an undeclared `ResourceWithServiceDiscoveryPromise` and
failed with six `TS2552` errors — reported as
[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507) and fixed upstream by
[microsoft/aspire#19577](https://github.com/microsoft/aspire/pull/19577) under the 13.6 milestone.
That upstream fix was not what made this work even before `ServiceResource` existed: the generator
emits the wrapper pair when the bare interface appears as an extension-method **receiver** rather
than only as a return type, and the ten (now-retired) `[AspireExport]` configuration shims declared
exactly that receiver — so they carried the wrapper pair for `addService` too. The measurement is
in
[`docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md`](https://github.com/flojon/aspire-servicesources/blob/main/docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md).
