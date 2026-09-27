# The `"container"` source

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

