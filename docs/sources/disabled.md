# The `"disabled"` source

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

Like [`"url"`](url.md), there is no real resource for Aspire to run, so the AppHost's own
[`Configure` calls are skipped and logged](../guides/configuration.md#configuring-a-resolved-service), and a consumer's
`WaitFor`/`WaitForCompletion` on it resolves immediately instead of waiting — the same #170
protection `"url"` gets. Unlike `"url"`, no endpoint is registered at all: there is nothing to
point a consumer at, deliberately, so a container that references a disabled service simply gets no
endpoint wiring rather than the DCP failure [`"url"` warns about](url.md).

