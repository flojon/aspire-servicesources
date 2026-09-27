# Other sources: `kubernetes`, `url`, `container`, `disabled`

[← Back to README](https://github.com/flojon/aspire-servicesources/blob/main/README.md)

### `"kubernetes"` source

Point a service at an already-running instance in a Kubernetes dev cluster via
`kubectl port-forward`, instead of running it locally at all.

`servicesources.yaml`:
```yaml
services:
  orders:
    kubernetes:
      service: orders-svc
      port: 8080
```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": {
      "source": "kubernetes",
      "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 }
    }
  }
}
```

Requires `kubectl` on `PATH`, authenticated against the named `context`.

Add `scheme: https` if the pod behind that port serves TLS:

```yaml
services:
  orders:
    kubernetes:
      service: orders-svc
      port: 8443
      scheme: https
```

`kubectl port-forward` is a byte-transparent TCP tunnel, so the TLS handshake terminates at the
pod and `https://localhost:<port>` is the URL that actually works — the scheme is what the service
speaks, not a claim about the tunnel. It defaults to `http`, and names the endpoint consumers
reference: with `scheme: https` the service exposes an endpoint named `https`, so
`orders.GetEndpoint("https")` resolves. See
[naming a service's endpoint](configuration.md#naming-a-services-endpoint).

What the tunnel can't fix is certificate hostname validation — the client connects to `localhost`
while the certificate names the in-cluster service — so a consumer that validates certificates
needs the usual dev-certificate handling for that.

Set `scheme` in the developer config to override the catalog for just that developer, alongside a
`port` override:

```json
{
  "services": {
    "orders": {
      "source": "kubernetes",
      "kubernetes": { "context": "dev-west", "port": 8443, "scheme": "https" }
    }
  }
}
```

### `"url"` source

Point a service at a fixed, already-known URL — e.g. a Kubernetes ingress, a staging
deployment, or any other reachable HTTP(S) endpoint. There's no underlying resource for
Aspire to run; the endpoint resolves straight to the configured URL.

Three consequences follow from the service running out of band. The AppHost's
[`Configure` calls are skipped and logged](configuration.md#configuring-a-resolved-service). A **container**
can't `WithReference` it — a project or executable can — which fails with a clear error rather than
a DCP stack trace; see [#58](https://github.com/flojon/aspire-servicesources/issues/58). And a
consumer's `WaitFor` on it resolves immediately instead of waiting:

```csharp
var orders = builder.AddService("orders");

builder.AddProject<Projects.Storefront>("storefront")
    .WaitFor(orders);   // no-op while 'orders' is "url"
```

There is no lifetime to order against — the URL is already up, or it isn't, and nothing this
AppHost starts will change that — so the wait is dropped rather than satisfied. `WaitForCompletion`
goes the same way, since a service running out of band is never going to exit. Every consumer is
covered, containers included: a container that *references* a url service is still refused as
above, but one that only waits on it starts normally.

The drop is **logged**, alongside any `Configure` calls the same service skipped:

```
warn: Aspire.Hosting.ServiceSources
      Service 'orders': skipped WaitFor from 'storefront' because its source is 'url' — it
      resolves to a fixed, already-running URL with no local process to configure. ...
```

The one wait not reported is the `WaitForStart` Aspire adds itself for each resource an
`AddConnectionString` expression references — nobody wrote it, so there is no line to point at.

Note what this does **not** promise: the URL is not fetched, so the consumer starts whether or not
anything is listening. A `WaitFor` written against a `"local"` service keeps its full meaning the
moment the service is switched back, which is the point — a developer choosing `"url"` in their own
`servicesources.local.json` must not hang an AppHost they don't own. See
[#170](https://github.com/flojon/aspire-servicesources/issues/170).

`servicesources.yaml`:
```yaml
services:
  orders:
    url:
      url: https://orders.example.com
```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": { "source": "url" }
  }
}
```

Set `url` in the developer config instead to override the catalog's URL for just that
developer (e.g. pointing at a personal tunnel or local proxy):

```json
{
  "services": {
    "orders": { "source": "url", "url": { "url": "https://orders.dev.internal" } }
  }
}
```

A resolved service implements `IResourceWithServiceDiscovery`, so any consumer that can't use
`HttpClient` directly can still resolve its address with `ServiceEndpointResolver` from
`Microsoft.Extensions.ServiceDiscovery` — the same as it would for a project reference,
regardless of which source resolved it.

### `"container"` source

Run a published container image locally via Aspire's own container-runtime integration —
image pull and lifecycle are managed entirely by Aspire.

`servicesources.yaml`:
```yaml
services:
  orders:
    container:
      image: ghcr.io/company/orders
      port: 8080
      defaultTag: latest
```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": { "source": "container" }
  }
}
```

Set `tag` in the developer config to override the catalog's `defaultTag` for just that
developer:

```json
{
  "services": {
    "orders": { "source": "container", "container": { "tag": "v1.4.2" } }
  }
}
```

Add `scheme: https` if the image serves TLS on `port`. Like `port`, it's catalog-only — the image
decides what it serves, so there's nothing per-developer to override — and it defaults to `http`:

```yaml
services:
  orders:
    container:
      image: ghcr.io/company/orders
      port: 8443
      scheme: https
```

### `"disabled"` source

Turn a service off without deleting its `AddService("orders")` call or its catalog entry. Nothing
runs, nothing is reachable — no endpoint, no process, no container — and `AddService` still hands
back a valid resource builder, so the rest of the AppHost needs no change.

`servicesources.local.json`:
```json
{
  "services": {
    "orders": { "source": "disabled" }
  }
}
```

No catalog block and no builder call are needed — `"disabled"` is chosen entirely from
`servicesources.local.json` (or a higher configuration layer), for a service the catalog already
declares some other way.

Named `"disabled"` rather than leaving `source` blank: a blank or absent `source` already means
something else — the entry is not configured at all — and reports
`'orders' has no source configured`. `"disabled"` is a deliberate, distinct choice.

Like [`"url"`](#url-source), there is no real resource for Aspire to run, so the AppHost's own
[`Configure` calls are skipped and logged](configuration.md#configuring-a-resolved-service), and a consumer's
`WaitFor`/`WaitForCompletion` on it resolves immediately instead of waiting — the same #170
protection `"url"` gets. Unlike `"url"`, no endpoint is registered at all: there is nothing to
point a consumer at, deliberately, so a container that references a disabled service simply gets no
endpoint wiring rather than the DCP failure [`"url"` warns about](#url-source).

### Combining sources on one catalog entry

A single `servicesources.yaml` entry can carry blocks for every source at once — the catalog
just describes *how* each source would resolve the service; each developer's
`servicesources.local.json` picks which one actually applies to them:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    kubernetes:
      service: orders-svc
      port: 8080
    url:
      url: https://orders.example.com
    container:
      image: ghcr.io/example/orders
      port: 8080
      defaultTag: latest
```

A developer editing the service picks `"local"`; one debugging against a shared dev cluster
picks `"kubernetes"`; one who just needs it reachable picks `"url"` or `"container"` — same
catalog entry, same `AddService("orders")` call in the AppHost, no code changes either way.
Each developer's own `servicesources.local.json` just names which source applies to them —
editing `orders` locally:

```json
{ "services": { "orders": { "source": "local" } } }
```

debugging against a shared dev cluster:

```json
{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 } } } }
```

or just needing it reachable, not caring how:

```json
{ "services": { "orders": { "source": "url" } } }
```

or turning it off entirely — no block needed for this one:

```json
{ "services": { "orders": { "source": "disabled" } } }
```

The `source` value is matched without regard to case, so `"local"`, `"Local"` and `"LOCAL"` all name
the same source. A name none of the five has is refused at composition time, naming the ones that
exist. (The `kind` names in `servicesources.yaml` are the exception — those *are* case-sensitive,
because anything may register one and two registrations must not be able to collide by spelling.)

