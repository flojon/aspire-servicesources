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


## Readiness and a dead tunnel

The `kubectl port-forward` is a resource named after the service, and it is not treated as up just
because the process started. Three things follow from that.

**`WaitFor` holds until the tunnel works.** The service carries a built-in health check that opens a
TCP connection to the local port the tunnel forwards. A consumer that `WaitFor`s the service starts
once that connection is accepted, whether or not the AppHost adds a health check of its own. The probe
repeats while the service runs, and `kubectl` logs each one as `Handling connection for <port>` in the
service's console; that is the probe, not traffic. The probe opens and closes a connection without
sending anything, so the pod behind the tunnel may see an empty connection each interval and a
database may log it as an aborted or EOF connection.

**A tunnel that fails to open is `FailedToStart`.** A Service name that does not exist in the
namespace, a context that cannot authenticate, or a port the Service does not expose makes `kubectl`
exit straight away. The service is then marked `FailedToStart`, so a consumer that `WaitFor`s it never
starts, instead of starting against a port nothing listens on. It stays in `Waiting` in the dashboard:
Aspire holds the dependent and does not fail it, so look at the service when a consumer never starts. The AppHost log carries the
last line `kubectl` wrote (for example `services "orders-svc" not found`), and the full output is in the
service's console in the dashboard. This is not retried: the causes are configuration, and a retry
would fail the same way. Fix the Service name, namespace, context or port, and restart the resource.

**A tunnel that dies after it was up is restarted, within limits.** When the pod behind the tunnel is
replaced, a rollout happens or the connection drops, `kubectl port-forward` exits. The same resource is
started again, so it keeps its name and its local port, after a wait of 1s, 2s, 4s and so on, never
more than 30s. Each attempt is logged as a warning naming the attempt (`attempt 3 of 8`). After 8
restarts in a row the tunnel is left stopped and an error is logged; start the resource from the
dashboard once the cluster is reachable. A tunnel that stays up for 30s resets the count, and so does
starting the resource yourself after it gave up. Starting it while a restart is waiting cancels that restart once it is up.

A tunnel that is running but has not accepted a connection for 60s (typically a Service with no ready
pods behind it, which leaves `kubectl` running with the port closed) is logged once as a warning with
`kubectl`'s last line. It is not restarted, and a consumer that `WaitFor`s the service stays held.

**Health checks.** `WithHttpHealthCheck` and `WithHealthCheck` on a `"kubernetes"` service apply to the
port-forward's resource and probe the service through the tunnel. `WaitFor` holds until the built-in TCP
check **and** your check both pass. A failing check of yours shows in the dashboard health badge; it does
not restart the tunnel, does not mark it `FailedToStart`, and does not raise the "no connection" warning,
which follow only the tunnel's own check. The tunnel's report is read once it has a result, so that
warning can come one probe interval later than the tunnel failing, and when Aspire runs a resource's
checks as one batch a slow check of yours may delay the tunnel's report. `WithAnnotation(..., Replace)` of
a health check is treated as an append, so the tunnel's own check is never removed. Things to know:

- A checked-in `WithHttpHealthCheck` sends real GET requests to whichever context
  `servicesources.local.json` selects, with no credentials.
- The tunnel's endpoint is named after its scheme, so on an `https` service pass the endpoint name:
  `WithHttpHealthCheck("/health", endpointName: "https")`. The certificate is the pod's and the host name
  is `localhost`, so the probe is `Unhealthy` unless that certificate is trusted for `localhost`, and
  `WaitFor` then holds. Certificate validation is not relaxed. Remove the check or trust the certificate
  to release a held `WaitFor`.
- Aspire's default redirect handling applies, so a workload that answers the probe with a redirect can
  send it elsewhere (each hop is a GET whose response is ignored). Keep checks on a path you trust. That
  residual risk is accepted.
- The probe goes to a loopback port. If a foreign process holds that port, it answers the probe, and a
  `Healthy` check does not prove the tunnel is yours. Restart the resource if you suspect a port clash.

This is deliberately modest:

- Nothing is restarted when the developer stops the resource, when the AppHost is shutting down, or
  when `kubectl` could not be launched at all.
- A restart does not carry over connections. Whatever was open through the old tunnel is gone, and a
  consumer has to reconnect. A consumer that `WaitFor`s the service is not restarted or held back by
  it going down; `WaitFor` only gates the first start.
- The restart is Aspire's own Start command on the resource. A restart that is refused is logged and
  counts against the 8.

The retry limits are not configurable.
