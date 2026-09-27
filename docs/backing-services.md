# Backing services: databases, brokers and caches

[← Back to README](../README.md)

A service usually depends on a database or a broker, and a developer wants the same choice for it
that they have for the service: run it locally, or connect to the one already running in the shared
dev cluster. `AddBackingService()` is that choice.

```csharp
var ordersDb = builder.AddBackingService("orders-db",
    local: () => builder.AddPostgres("orders-pg").AddDatabase("orders-db", "orders"));

builder.AddService("orders")
    .WithReference(ordersDb)
    .WaitFor(ordersDb);
```

> **`WaitFor` stops meaning anything under `"direct"`.** The `WaitFor` above waits properly under
> `"local"`, where a real database resource sits behind it, and under `"kubernetes"`, where a health
> check on the forwarded port holds the consumer back until the tunnel is actually listening. Switch
> that backing service to `"direct"` and it is satisfied immediately instead: the resource is a
> connection string, and Aspire marks it running as soon as that string is available — which is at
> once, without the instance you pointed at having been checked. Nothing fails and nothing hangs;
> the consumer simply starts earlier than you asked it to. Tracked as
> [#220](https://github.com/flojon/aspire-servicesources/issues/220).

Two things are different from `AddService()`:

- **There is no catalog.** A backing service is declared by the `AddBackingService()` call itself,
  so nothing goes in `servicesources.yaml` — an AppHost that only connects to a database needs no
  catalog file at all.
- **The local case is your code, not ours.** Provisioning a database locally is already expressed
  perfectly well as `builder.AddPostgres(...)`, by the person who knows what it should be, so the
  `"local"` source runs that factory and returns its result unchanged. Re-expressing image and
  version as catalog fields would be a worse version of Aspire's own integrations.

Because the local case is a factory, this works for anything with a connection string —
`AddSqlServer(...).AddDatabase(...)`, `AddRabbitMQ(...)`, `AddRedis(...)` — with no support needed
per backend.

### Sources

Configured under a new `backingServices:` section of `servicesources.local.json`, alongside
`services:` and read through the same configuration layers, so every override in
[Overriding `servicesources.local.json`](configuration.md#overriding-servicesourceslocaljson) applies here too:

```jsonc
{
  "services": { "orders": { "source": "local" } },
  "backingServices": {
    "orders-db": {
      "source": "direct",
      "direct": { "connectionString": "Host=localhost;Port=5432;Database=orders;Username=dev" }
    }
  }
}
```

| `source` | Meaning |
| --- | --- |
| `"local"` | Run the `local` factory. **The default** — a backing service with no entry resolves here, so an AppHost nobody has configured runs as it reads. |
| `"direct"` | Connect to `direct.connectionString`. Covers a database the developer started by hand *and* a cluster database published through an ingress: from the AppHost's side both are an address to connect to, with no process to manage. |
| `"kubernetes"` | Open a `kubectl port-forward` to a Service in a dev cluster, and connect to the local end of it. See [Reaching a backing service in a cluster](#reaching-a-backing-service-in-a-cluster). |

`"direct"` is named for the one thing that distinguishes it — nothing in the way — rather than for
where the database runs. It is not `"remote"`, because the common case is a `localhost` the
developer started themselves, and not `"external"`, which Aspire already uses for an external
*HTTP* service.

Each source's settings live in a block named for it, exactly as a service entry's do, so a higher
configuration layer can switch `source` without a field from the source you switched away from
being read alongside it.

> **Write the address as reached from outside Aspire.** `"direct"` hands the connection string to
> consumers as given — nothing about it is rewritten, because there is nothing for this AppHost to
> manage. That is worth spelling out for the case a developer reaches for while experimenting:
> pointing `"direct"` at a container the *same* AppHost runs. The host and port shown for a
> container endpoint in the dashboard, and by `aspire describe`, are Aspire's
> [endpoint proxy](https://learn.microsoft.com/dotnet/aspire/fundamentals/networking-overview) —
> not the container's own published port. The proxy listens only while that AppHost is running, and
> a fresh run assigns it a new port, so a connection string written against it stops working as
> soon as either changes. Take the real published port from your container runtime
> (`docker port` / `podman port`) — or keep `"local"`, which is what "a database this AppHost runs"
> already means.

### Reaching a backing service in a cluster

`"kubernetes"` connects to a database, broker or cache running in a dev cluster, through a
`kubectl port-forward` this AppHost opens and Aspire manages for the life of the run:

```jsonc
{
  "backingServices": {
    "orders-db": {
      "source": "kubernetes",
      "kubernetes": {
        "service": "orders-pg-rw",         // the Kubernetes Service to forward to
        "port": 5432,                       // the port it listens on inside the cluster; a block
                                            // of named ports forwards several — see below
        "context": "dev-west",              // the kubectl context to forward through
        "namespace": "orders",              // optional; "default" when omitted
        "connectionString": "Host=localhost;Port=${port};Database=orders;Username=dev;Password=hunter2"
      }
    }
  }
}
```

**Write `${port}`, not a number.** The local end of the tunnel is allocated when the AppHost starts,
so that two backing services forwarded at once cannot collide — which means it is not a number you
can write down. `${port}` is replaced with it before any consumer sees the string. A connection
string that names no `${port}` is refused at startup rather than run, because the alternative fails
silently: `Port=5432` copied out of a manifest addresses port 5432 on *your* machine, where your own
database container may well be listening, and the AppHost would connect to the wrong database with
every resource reporting healthy.

Two resources appear in the dashboard: the backing service itself, and the `kubectl` process
underneath it as `orders-db-tunnel`. `kubectl`'s own output — a bad context, a Service that does not
exist, an expired credential — lands in that resource's logs.

**The health badge is on the backing service, not on the tunnel.** For the few seconds before the
forward is up, the tunnel shows as running while the backing service above it shows as unhealthy;
that is the right way round, because the backing service is what consumers wait for. The check is
deliberately not duplicated onto the tunnel: Aspire probes once per resource carrying it, and every
probe is a connection `kubectl` logs — which would bury the output you go there to read. A `kubectl`
that exits outright shows up as a failed resource regardless.

- **A Service, not a pod.** A pod name carries a replica-set suffix that changes on every rollout;
  `kubectl port-forward` against a Service picks a backing pod itself.
- **`context` is required**, and deliberately not defaulted to whatever `kubectl` is currently
  pointed at. Defaulting would make the AppHost's behaviour depend on a shell you may not have
  opened today, and the failure would be a connection to the wrong cluster rather than an error.
- **`namespace` defaults to `default`**, which is *not* `kubectl`'s own default — `kubectl` uses the
  namespace configured on the context. Same reasoning: what the AppHost does should not depend on a
  `kubectl config set-context --current --namespace=…` nobody recorded.
- **`kubectl` must be on `PATH`.** Nothing is bundled, and this source runs the same binary you do.

#### Several ports through one tunnel

A broker usually wants two: the one the application speaks, and a management port you open in a
browser. Write `port` as a block that names each one, and reach them as `${port:<name>}`:

```jsonc
{
  "backingServices": {
    "orders-events": {
      "source": "kubernetes",
      "kubernetes": {
        "service": "rabbitmq",
        "port": { "amqp": 5672, "management": 15672 },
        "context": "dev-west",
        "connectionString": "amqp://dev:hunter2@localhost:${port:amqp}/"
      }
    }
  }
}
```

**One `kubectl` process carries every pair**, because `kubectl port-forward` accepts several against
one Service — two entries would mean two processes and two tunnels to the same Service. Each
forwarded port gets its own health check, all of them on the backing service, so a `WaitFor` waits
for the whole tunnel and the dashboard says which half is missing while it comes up.

Not every forwarded port has to appear in the connection string: the management port above is
forwarded so you can open it, and nothing dials it from the app.

**`${port}` and `${port:<name>}` do not mix.** A `port` written as a number forwards one unnamed
port and takes `${port}`; a block that names its ports takes `${port:<name>}` for each. Writing the
other one is refused at startup, naming the ports this backing service actually forwards — including
a "did you mean" when the name is close to one of them.

> Two spellings of one port name that differ only in case — `amqp` and `AMQP` — are the same
> configuration key. In a single `servicesources.local.json` that is a duplicate key, and the JSON
> parser refuses the **whole file**; spread across two layers they merge instead, and the casing you
> see is whichever layer wrote last.

Reading credentials out of a Kubernetes secret is covered under
[Connection-string placeholders](#connection-string-placeholders), with `${secret:<name>:<key>}`.

### The local factory's resource must be named after the backing service

Aspire's `WithReference(...)` keys the connection string on the **referenced resource's own name**,
and under `"local"` that resource is whatever your factory built. So the names have to agree, and
`AddBackingService` refuses them when they do not:

```csharp
// Good — one name everywhere. Switching source changes the value and nothing else.
builder.AddBackingService("orders-db", () => builder.AddPostgres("pg").AddDatabase("orders-db", "orders"));
// → ConnectionStrings__orders-db, under every source

// Refused at startup, naming both names.
builder.AddBackingService("orders-db", () => builder.AddPostgres("pg").AddDatabase("orders"));
// → would be ConnectionStrings__orders under "local", ConnectionStrings__orders-db under "direct"
```

`AddDatabase("orders-db", "orders")` names the Aspire resource and the actual database separately,
which is what to reach for when the two want different names. **Casing counts.** .NET folds it when
reading configuration, so a .NET consumer would not notice — but the environment variable itself
differs, and a JavaScript or Java service reads `process.env` / `System.getenv` case-sensitively.

This is a rule rather than advice because the alternative remedy is not available everywhere. In
C# a consumer can pin the key from its own side:

```csharp
builder.AddService("orders")
    .WithReference(ordersDb, "OrdersDb");
// → ConnectionStrings__OrdersDb, under every source
```

`WithReference`'s second argument overrides the source resource's name for the connection string.
Reach for it when the app already reads a particular name. This used to be C#-only
([#209](https://github.com/flojon/aspire-servicesources/issues/209)), because the package's own
guest-language shim took the source alone; now that `ServiceResource.withReference` is Aspire's own
generated method rather than a package-authored one, a TypeScript AppHost gets the same
`{ connectionName }` second argument any other resource's `withReference` does. Naming the
factory's resource after the backing service is still the one answer every AppHost can give
regardless of language, which is why it is the one enforced.

**If the resource is not yours to rename** — a shared helper, or one handed to you — return a
connection string of your own that forwards it:

```csharp
builder.AddBackingService("orders-db", () =>
{
    var shared = SharedHelpers.AddOrdersDatabase(builder);   // names its resource whatever it likes
    return builder.AddConnectionString("orders-db", ReferenceExpression.Create($"{shared}"));
});
```

The forwarding resource carries the same value under the name the rule wants, so the key stays put
across a source switch. This used to be the case `WithReference(db, connectionName)` covered; the
rule makes that unreachable for a backing service, since the throw happens first, so the wrap is
what replaces it.

> **The wait mostly survives the wrap, but loses its health check.** What `AddBackingService` hands
> back is the forwarding `ConnectionStringResource` rather than the database the factory built —
> and Aspire does follow the reference: the wrapper itself sits in `Waiting` until the database it
> forwards is running, so a consumer's `WaitFor(ordersDb)` still holds back for Postgres to start.
>
> What it stops honouring is the database's *health check*. Waiting on the database directly waits
> for it to be healthy; waiting on the wrapper is satisfied once the database is merely running.
> Measured on a live host with a real Postgres — the consumer waiting on the wrapper started about
> seven seconds before the one waiting on the database, while a consumer waiting on nothing started
> three seconds before either.
>
> So **rename the resource wherever you can**; the wrap is a smaller loss than it looks, but it is
> not free. Tracked as [#220](https://github.com/flojon/aspire-servicesources/issues/220), together
> with the `"direct"` case, where the connection string references nothing and the wait is therefore
> satisfied at once.

### Connection-string placeholders

A `connectionString` is normally a literal. **Braces reserve nothing** — `Driver={PostgreSQL}`,
`Server={host}\instance` and `PWD={secret}` all pass through exactly as written, doubled braces
included, so ODBC values keep their own doubling rule intact (`PWD={pa}}ss}` is the password
`pa}ss`, and stays that).

Placeholders open on `${`, which no connection-string dialect uses. Two are recognised and reserved
for the sources that can resolve them; a source that cannot rejects one with a message saying why:

- `${port}` — the local end of the tunnel, under `"kubernetes"`, where it is **required** unless the
  whole connection string is one `${secret:…}` (below), which carries a port already.
  `"direct"` forwards nothing, so there write the port the backing service already listens on.
- `${port:<name>}` — one of several ports forwarded through the one tunnel, where `port` is written
  as a block that names each. See
  [Several ports through one tunnel](#several-ports-through-one-tunnel).
- `${secret:<name>:<key>}` — a value read from a Kubernetes secret, under `"kubernetes"`. The fetch
  is deferred: the placeholder becomes a parameter Aspire resolves when something first asks for the
  value, so an unreachable cluster costs one failed parameter rather than an AppHost that will not
  start. The value is marked secret, so the dashboard masks it, and reading the same placeholder
  twice fetches once. `"direct"` has no cluster to resolve one against and refuses it, naming
  `"kubernetes"` as the source that does.

  A secret's name and its keys are letters, digits, `-`, `.` and `_`, with the name starting with a
  letter or a digit — the cluster's own rule, checked here so that nothing else can be smuggled into
  the `kubectl` command that reads it.

  **A secret holding the whole connection string** works too, which is the shape a hand-authored
  Sealed Secret usually has. Write the template as exactly one placeholder:

  ```jsonc
  {
    "backingServices": {
      "orders-db": {
        "source": "kubernetes",
        "kubernetes": {
          "service": "orders-pg-rw",
          "port": 5432,
          "context": "dev-west",
          "namespace": "orders",
          "connectionString": "${secret:orders-cs:connectionString}"
        }
      }
    }
  }
  ```

  Then the port-forward listens on the same port `port` names rather than an allocated one — there
  is nothing in the template to substitute a local port into — and the in-cluster host the secret
  was written against is rewritten to `localhost`, in any of the four forms a pod resolves
  (`orders-pg-rw`, `.orders`, `.svc`, `.svc.cluster.local`), wherever a connection string can put a
  host. Because the allocated port is given up, a local port already in use is refused up front — and
  for the same reason, this mode takes a single `port` rather than a block that names several: with
  only the one number to match against, give it a single port instead. So is a secret whose own port
  is not the one being forwarded, and one that names the service in no form this can rewrite.
  Per-field placeholders stay preferred wherever the secret offers them.

A malformed placeholder — `${secret:orders-creds}`, with no key — fails when the AppHost starts,
naming the backing service and the configuration key, rather than reaching the app as text.

**A `${` begins a placeholder only when the word after it — up to the first `:` or `}`, or to the
end — is *exactly* `port` or `secret`, in any casing.** Equality, not a prefix, so everything else
is text: `${portal}`, `${secretariat}`, `${secrets:a}` and `${DB_PASS}` all pass through untouched —
which is what keeps a connection string working when something else in your toolchain is the one
expanding `${…}`.

The remaining cost is that `${port}` and `${secret:…}` themselves cannot be written as literal text
in any casing: a keyword-shaped token that this package cannot read fails at startup rather than
passing through, and there is no escape. Nothing has wanted one. `$` is not otherwise special, so
`$${port}` is available as an escape if that ever changes — it is a literal `$` followed by a
placeholder today.

> The syntax was `{port}` during development and moved before release
> ([#207](https://github.com/flojon/aspire-servicesources/issues/207)). Reserving a shape inside
> braces left `PWD={secret}` — ODBC for a password that happens to be the word — impossible to
> write. Escaping could not fix it: doubling is the syntax ODBC already uses, and collapsing it
> silently corrupted working connection strings in both directions.

#### Setting a template from a shell: quote it with single quotes

`${…}` is also what a POSIX shell, docker-compose and a GitHub Actions `run:` block use for their
own variables, so a template set through an environment variable can be expanded away before the
AppHost ever sees it. **Double quotes do not help** — they protect the `;` and not the `${`:

```bash
# Wrong: double quotes, so the shell substitutes ${port} (unset) and the AppHost gets "Host=db;Port="
env "ServiceSources__BackingServices__orders-db__Direct__ConnectionString=Host=db;Port=${port}" aspire run

# Right: single quotes, so ${port} reaches the AppHost intact
env 'ServiceSources__BackingServices__orders-db__Direct__ConnectionString=Host=db;Port=${port}' aspire run
```

`env` rather than `export`, because a backing service whose name contains a hyphen — `orders-db`
here — makes an environment variable name that is not a valid shell identifier, and both
`export NAME=…` and the `NAME=… command` prefix refuse it outright. `env 'NAME=value' command`
accepts any name, and so do docker-compose's `environment:`, `launchSettings.json` and a workflow's
`env:` block, none of which put the *name* through a shell.

That is a separate question from the value. `launchSettings.json` leaves a value alone entirely;
docker-compose does its own `${…}` interpolation, so escape it there as `$${port}`; and a workflow's
`run:` block is a shell, so it needs the single quotes above.

Under `"direct"` nothing reports the mangled case, because what arrives is a valid template that
simply has no placeholder in it — which is also what someone writing a literal port produces, and
that is a perfectly good `"direct"` connection string. **Under `"kubernetes"` it is reported**, since
a `${port}` is required there: the error names the shell alongside the spelling, because a template
that lost its placeholder and one that never had it arrive looking the same.

This does not apply to `servicesources.local.json`, `appsettings.json` or user secrets, where `$` is
an ordinary character — which is where a template normally lives.

### Configuration that nothing reads is reported

A backing service with no entry legitimately runs from its `local` factory, so an entry whose key
matches no `AddBackingService()` call cannot be told apart at read time from one that was never
written:

```jsonc
"backingServices": {
  "orders_db": { "source": "direct", "direct": { "connectionString": "…" } }  // note the underscore
}
```

`orders-db` would revert to `"local"` and start the container you were trying to avoid. Once the
AppHost is composed the set of names `AddBackingService()` was called with is known, so this is
reported as a warning at startup, naming the entry and the declared name it resembles. A misspelled
`backingServices` root key — which does the same to every backing service at once — is reported the
same way.

Both are warnings rather than errors, because a shared `servicesources.local.json` may legitimately
carry entries for backing services only some configurations add. There is no way to switch them off;
if you find yourself wanting one, say so on
[#206](https://github.com/flojon/aspire-servicesources/issues/206).

### From a guest-language AppHost (backing services)

`addBackingService` is exported, and the local factory crosses the boundary as an ordinary callback:

```typescript
const ordersDb = await builder.addBackingService('orders-db',
    async () => builder.addPostgres('orders-pg').addDatabase('orders-db', 'orders'));
```

