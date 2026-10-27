# The `"url"` source

Point a service at a fixed, already-known URL — e.g. a Kubernetes ingress, a staging
deployment, or any other reachable HTTP(S) endpoint. There's no underlying resource for
Aspire to run; the endpoint resolves straight to the configured URL.

Three consequences follow from the service running out of band. The AppHost's
[`Configure` calls are skipped and logged](../guides/configuration.md#configuring-a-resolved-service). A **container**
that `WithReference`s it is handed the URL as a literal, `scheme://host[:port]` with a default port
omitted and no path, query or credentials, because there is no resource for Aspire to route
container-to-host traffic through. A loopback URL such as `http://localhost:8765` therefore means
the container itself, since Aspire gives these containers no route to the host. The same literal
replaces the service's endpoint wherever a container's environment or arguments carry it: a
`WithEnvironment` or `WithArgs` value, one set inside an opaque `WithEnvironment(context => ...)`
callback, and a connection string built from the endpoint:

```csharp
var inventory = builder.AddService("inventory");
var connection = builder.AddConnectionString(
    "inventorycs", ReferenceExpression.Create($"Endpoint={inventory.GetEndpoint("https")};Key=x"));

builder.AddContainer("worker", "alpine")
    .WithReference(connection);   // ConnectionStrings__inventorycs=Endpoint=https://...;Key=x
```

Other values in the same connection string, such as a secret parameter, are passed through as
Aspire resolves them. A value the rewrite cannot turn into a literal, such as a conditional
expression over the endpoint, is logged as a warning naming the container, the variable and the
service, and the container may fail to start; pass the endpoint or its `Url` property directly instead.
A callback that a library adds to the container after this package's `BeforeStartEvent` handler has
run is not rewritten.

And a consumer's `WaitFor` on it resolves immediately instead of waiting:

```csharp
var orders = builder.AddService("orders");

builder.AddService("storefront")
    .WaitFor(orders);   // no-op while 'orders' is "url"
```

There is no lifetime to order against — the URL is already up, or it isn't, and nothing this
AppHost starts will change that — so the wait is dropped rather than satisfied. `WaitForCompletion`
goes the same way, since a service running out of band is never going to exit. Every consumer is
covered, containers included: a container that only waits on a url service starts normally, as does one that references it.

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

=== "C#"

    ```csharp
    catalog.AddService("orders")
        .WithUrl("https://orders.example.com");
    ```

=== "YAML"

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

