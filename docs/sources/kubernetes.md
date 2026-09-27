# The `"kubernetes"` source

Point a service at an already-running instance in a Kubernetes dev cluster via
`kubectl port-forward`, instead of running it locally at all.

=== "C#"

    ```csharp
    catalog.AddService("orders")
        .WithKubernetes("orders-svc", port: 8080);
    ```

=== "YAML"

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

=== "C#"

    ```csharp
    catalog.AddService("orders")
        .WithKubernetes("orders-svc", port: 8443, scheme: "https");
    ```

=== "YAML"

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
[naming a service's endpoint](../guides/configuration.md#naming-a-services-endpoint).

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

