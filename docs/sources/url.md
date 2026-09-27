# The `"url"` source

Point a service at a fixed, already-known URL — e.g. a Kubernetes ingress, a staging
deployment, or any other reachable HTTP(S) endpoint. There's no underlying resource for
Aspire to run; the endpoint resolves straight to the configured URL.

Three consequences follow from the service running out of band. The AppHost's
[`Configure` calls are skipped and logged](../guides/configuration.md#configuring-a-resolved-service). A **container**
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
anything is listening. A `WaitFor` written against a `"repository"` service keeps its full meaning the
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

