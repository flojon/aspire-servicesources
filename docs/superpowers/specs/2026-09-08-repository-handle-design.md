# Aspire.Hosting.ServiceSources — Repository as a First-Class Handle

**Date:** 2026-09-08
**Status:** Draft — needs the six [open questions](#open-questions) answered before an implementation
plan is written.
**Resolves:** #291 (`AddRepository` returning a shared handle, so several services can name one
repository and one ref).
**Closes:** #66 (two services in one repository clone it twice) — not as a special case, but because
the shared checkout falls out of the shared record. #66 is marked `blocked-by: #134`, and the part
that blocked it (a domain type to hang a repository record on) shipped in Stage 1.
**Builds on:** [the repository-handle findings](2026-09-08-repository-handle-findings.md), read out
of `b2ccca4` — every `file:line` claim below is evidenced there and is not repeated;
[the code-catalog design](2026-09-05-servicesources-code-catalog-design.md), whose reviewer decision
3 deferred this deliberately; and [the Stage-0 ATS findings](2026-09-07-code-catalog-stage0-ats-probe-findings.md).
**Sequenced against:** #134 Stage 2 (`WithPrepare`, `AsJava`/`AsJavaScript`). No code dependency in
either direction; see [Sequencing](#sequencing-against-134-stage-2).
**Leaves a seam for:** #11 (the central registry) — see [Consequences](#consequences-accepted).

---

## Motivation

Today a repository is not a thing the catalog can name. It is a URL repeated on each service, with a
`defaultRef` repeated beside it, so the monorepo shape — several services that are several `project:`
paths inside one repository — is expressed by saying the same thing N times and hoping the N copies
agree. Two consequences follow, and both are already filed as bugs:

- **They need not agree.** Two services naming one repository with different `defaultRef` values is
  representable, and #66 has to detect and reject it at runtime.
- **They are cloned separately.** Checkouts are keyed by service name, so a monorepo with five
  `"local"` services is cloned five times — five working trees, five downloads racing each other for
  bandwidth, and an edit to a shared library under `checkouts/orders` invisible from
  `checkouts/billing` even though upstream they are one file.

Making the repository its own record makes the disagreement **unrepresentable** rather than rejected,
and makes the shared tree the default rather than an option. The authoring shape #291 proposes:

```csharp
builder.AddServiceCatalog(catalog =>
{
    var monorepo = catalog.AddRepository("https://github.com/example/monorepo", defaultRef: "main");

    monorepo.AddService("orders").WithProject("src/Orders.Api/Orders.Api.csproj");
    monorepo.AddService("payments").WithProject("src/Payments.Api/Payments.Api.csproj");

    // Unchanged for the common case: one service, one repository.
    catalog.AddService("inventory")
        .WithRepository("https://github.com/example/inventory", defaultRef: "main")
        .WithProject("Inventory.Api.csproj");
});
```

## The acceptance criteria, quoted

From #291 and #66, cited throughout, so stated once:

1. Two services naming the same repository **through one handle** produce one clone and one working
   tree.
2. Two services naming the same URL **without** an explicit group stay independent, as today.
3. `LocalCheckoutPrefetchTests.FirstAddService_TwoServicesInOneRepository_DownloadsItTwiceConcurrently`
   is replaced by its inverse — one download for two services.
4. Conflicting refs across a shared repository fail with a message naming both services, the
   repository, and the two refs; or become unrepresentable.
5. The existing per-service reconciliation rules — origin-URL mismatch, uncommitted-changes-and-wrong-ref
   — are restated in terms of the shared checkout, since they now decide for several services at once.
6. An existing AppHost, yaml or code, keeps working unchanged. (Inherited from #134's criterion 3, and
   the tightest constraint here — see finding 2.)

---

## Findings that constrain this design

The [findings document](2026-09-08-repository-handle-findings.md) has twelve with evidence. The five
that decide the shape below, in one line each:

1. **Identity cannot come from the URL** (finding 1). Criterion 2 requires that two ungrouped
   services naming one URL stay independent, so sharing is a property of the *declaration*, never of
   the string.
2. **Checkout directory names are a compatibility surface** (finding 2). Those directories hold
   developers' uncommitted work, which `PrepareRepoRoot` and `ReconcileRepoRoot` go out of their way
   to protect. #291's `checkouts/<repo-id>` phrasing, taken literally, renames every existing
   checkout — every AppHost re-clones and every in-flight change is orphaned.
3. **The prepare marker is already one per checkout directory** (finding 3). Grouped services with
   one shared command already get what #291 wants; grouped services with *different* commands
   invalidate each other's marker and re-run both steps on every start, so `mode: oncePerCommit`
   silently stops working. `prepare` has to move to the repository.
4. **`local.path` already means "not a managed checkout"** (finding 4). The split-back-out opt-out
   #291 leaves open already exists and already means what it should.
5. **`RepositoryDefinitionBuilder.AddService` is a third receiver for a colliding ATS capability id**
   (finding 11). Stage 0 measured that collision as silent, and measured `MethodName` as not fixing it.

---

## Architecture

### The domain type

`ServiceDefinition` stops carrying its repository inline and carries a reference instead:

```
                    ┌─ RepositoryDefinition (internal, sealed) ────────────┐
                    │ Url · DefaultRef · Prepare · CheckoutName · Origin   │
                    └──────────────────────▲───────────────────────────────┘
                                           │  reference — object identity in code,
                                           │  name resolution within one yaml file
  ServiceDefinition ───────────────────────┘
    Project · Url · Container · Kubernetes · Kind · KindOptions · Origin
```

Three fields move off `ServiceDefinition`: `Repository`, `DefaultRef` and `Prepare`.

**Identity is the record instance, not an id.** #291 proposes "an opaque id assigned at
`AddRepository`/parse time"; this design does not add one. In code the AppHost holds the handle, so
the reference *is* the identity; in yaml the `repositories:` key resolves to exactly one record
within that file, before any merge. Nothing needs to compare two identities for equality, and an id
that nothing compares is a field to keep consistent for no reader. `CheckoutName` below is the only
externally visible key, and it is human-readable, which an opaque id is not.

**Every service still has a repository record** — an ungrouped service gets an anonymous one of its
own, minted by whichever producer declared it. There is no null case and no "grouped or not" branch
downstream: `definition.Repository.Url` replaces `definition.Repository`, everywhere, and the
grouping is invisible except in what two records happen to be the same instance.

### `CheckoutName`: what makes criterion 6 hold

`LocalGitCheckout.ManagedRepoRoot(appHostDirectory, name)` keeps its signature. What changes is what
each caller passes — `definition.Repository.CheckoutName` instead of `serviceName`:

| declaration | `CheckoutName` | directory |
| --- | --- | --- |
| ungrouped — `WithRepository`, or inline yaml `repository:` | **the service's name** | `checkouts/<service>` — byte-identical to today |
| grouped — `AddRepository` / `repositories: monorepo` | the repository's name | `checkouts/monorepo` |

So an AppHost that does not use the feature has every checkout path, every prepare marker and every
deferred-resolution path unchanged. That is criterion 6, and it is the one property no reviewer
should let slip: a regression here does not fail a test, it silently orphans a developer's
uncommitted work in a directory nothing looks at again.

**The grouped name** is derived from the URL's last path segment with any `.git` suffix stripped
(`https://github.com/example/monorepo` → `monorepo`), overridable with an explicit `name:`. Yaml
needs no derivation: the `repositories:` key *is* the name. Every name — derived, explicit or yaml —
goes through `LocalGitCheckout.IsContainedCheckoutDirectoryName`, the #224 traversal guard, because
a URL ending in `/..` and a `repositories:` key of `../evil` are both developer input.

**One namespace, checked once.** Anonymous names (service names) and grouped names share the
`checkouts/` directory namespace, so a service `orders` and a repository derived to `orders` from
`github.com/x/orders.git` collide on a directory while naming different URLs.
`ReconcileRepoRoot` would eventually catch that as an origin-URL mismatch, which is a confusing
runtime error for a statically detectable clash. **Validated at catalog composition**, naming both
declarations and suggesting `name:`.

### The authoring API

Two public types are added, and one method:

| Type | Role |
| --- | --- |
| `RepositoryDefinitionBuilder` | `AddService(string)` → a `ServiceDefinitionBuilder` bound to this repository; `WithPrepare(…)` |
| — | `ServiceCatalogBuilder.AddRepository(string url, string? name = null, string? defaultRef = null)` |
| — | `ServiceDefinitionBuilder.WithProject(string project)` |

`WithProject` is new and necessary: Stage 1 has no project-only setter — `WithRepository(string url,
string? project = null, string? defaultRef = null)` is the only way to set one, and a grouped service
must not restate the URL. It becomes the primitive, with `WithRepository`'s `project:` parameter
delegating to it, so the existing additive rule (`RequireUnset` per block) covers setting the project
twice by either route.

**ATS ids.** `AddRepository` generates a capability id nothing else claims. `AddService` is the
problem: `ServiceSourcesBuilderExtensions.AddService` (shipped) and
`ServiceCatalogBuilder.AddService` (Stage 1, explicit id `addServiceToCatalog`) already had to be
separated, and this is a third. It gets `[AspireExport("addServiceToRepository")]`. Everything else
in the shape is measured: a handle returned from a method on a handle is what
`catalog.addServiceToCatalog(…)` already does end to end, and optional parameter bags cross.

```typescript
const monorepo = await catalog.addRepository('https://github.com/example/monorepo', { defaultRef: 'main' });
const orders = await monorepo.addServiceToRepository('orders');
await orders.withProject('src/Orders.Api/Orders.Api.csproj');
```

**Errors on the chain.** A service reached through `monorepo.AddService(…)` has its repository
decided, so `WithRepository` on it is a configuration error naming both the service and the
repository. The reverse — `WithProject` on an ungrouped service — is fine: it sets the project on that
service's anonymous record.

### Yaml, additively

```yaml
repositories:
  monorepo:
    repository: https://github.com/example/monorepo
    defaultRef: main
    prepare:
      command: ["./prepare.sh"]

services:
  orders:
    repositoryRef: monorepo
    project: src/Orders.Api/Orders.Api.csproj
  payments:
    repositoryRef: monorepo
    project: src/Payments.Api/Payments.Api.csproj

  # Unchanged, and still parses to its own anonymous record.
  inventory:
    repository: https://github.com/example/inventory
    defaultRef: main
    project: Inventory.Api.csproj
```

Three mechanical pieces, two of which the loader's existing reflection gives away:

- **`ServiceCatalog.Repositories`** — a `Dictionary<string, RepositoryMetadata>` with the same
  null-coercing setter `Services` has, since `repositories:` with nothing under it binds null.
  `KnownRootProperties` is `YamlPropertyNames(typeof(ServiceCatalog))`, so `repositories:` becomes a
  legal root key with **no** change to the root-key check.
- **`RepositoryMetadata`** — `Repository`, `DefaultRef`, `Prepare`, declared in the `Config`
  namespace like every other block. Its entries need their own unknown-key validation, because the
  loader's `KnownNestedProperties` is derived from `ServiceMetadata`'s properties and knows nothing
  about a second root map; `RawServiceCatalog` gains a matching raw `Repositories` map to check
  against.
- **`ServiceMetadata.RepositoryRef`** — a string. This widens the accepted per-service schema
  (wanted) and, because `KnownTopLevelProperties` also backs `IsReservedKindName`, **narrows the set
  of names a local kind may register** by one: `repositoryRef`. That is the same mechanism
  `PrepareMetadata` already documents for `prepare`, it is almost certainly harmless, and it is a
  silent public-behaviour change that belongs in the changelog.

**Two malformed cases**, both yaml-only (in code a dangling reference is unrepresentable — the
AppHost holds the object):

- `repositoryRef` naming no `repositories:` entry — an error naming the service, the reference, and
  the declared repository names.
- An entry setting both `repository:` and `repositoryRef:` — mutually exclusive, an error naming both
  keys. `defaultRef:` beside `repositoryRef:` is the same error, since the ref now belongs to the
  repository.

### `prepare` moves to the repository

The step runs once per checkout, and finding 3 shows the marker already assumes exactly that.
`PrepareMetadata` moves from the service onto `RepositoryDefinition`:

- **Yaml `prepare:` on a service** with an inline `repository:` parses onto that service's anonymous
  record — unchanged behaviour, because anonymous is one-to-one.
- **Yaml `prepare:` on a service carrying `repositoryRef:`** is an error pointing at the
  `repositories:` entry. Two members of one group cannot each declare a step for the one tree.
- **`WithPrepare`** stays on `ServiceDefinitionBuilder` (where #134 Stage 2 puts it) as the
  anonymous-record setter, and is added to `RepositoryDefinitionBuilder`. Called on a grouped
  service it is the same error as the yaml case. **No public break in either direction**, which is
  why Stage 2 need not wait.

`PreparePlan.For(serviceName, catalog, developer, managedCheckout, windows)` has one call site
(`LocalProjectSource.cs:60`) and already branches on `managedCheckout` — which turns out to answer
the per-developer half exactly:

| checkout | catalog block | developer block |
| --- | --- | --- |
| managed (shared) | the repository's | the **repository's** entry in `servicesources.local.json` |
| `local.path` (split out) | none — inherits nothing, as today | the **service's** `local.prepare`, as today |

So `local.prepare` needs no new rule: a service with `local.path` owns its tree and keeps writing its
own block, and a service in a managed group has no tree of its own to write one for. `For`'s first
parameter becomes a display label ("service 'orders'" / "repository 'monorepo'") so its errors name
whichever declared the block.

### The developer config

A third `DeveloperConfigShape`, over a new `RepositoryDeveloperConfig { Path, Ref, Prepare }` under
`ServiceSources:Repositories`:

```jsonc
{
  "services":     { "orders": { "source": "local" }, "payments": { "source": "local" } },
  "repositories": { "monorepo": { "ref": "feature/x" } }
}
```

The shape type derives everything by reflection off the entry type and its own doc notes that the
second kind "inherited the whole of the first one's diagnostics" — so near-miss suggestions,
blank-normalization and layered overrides all come free. Repository entries get the same
canonicalization to declared spelling that `CanonicalizeToCatalog` does for services, for the same
reason.

**`ref` for a group is configured here, and only here.** A `local.ref` on a service belonging to a
group is a configuration error naming the service, the repository and where to set it instead —
criterion 4, made a named error rather than the silent race two services calling
`CheckoutWithFetchRetry` against one directory would be today. On an ungrouped service `local.ref`
is untouched: its anonymous record is the only reader. Likewise `defaultRef` stays on
`WithRepository` — #291's "a service can no longer carry its own `defaultRef`" is true of the
*record*, not of the sugar that mints one.

**`local.path` is the documented escape from a group** (finding 4). No new field, no repository-level
list of exceptions.

### Composition, and the checkout

**Repositories resolve per catalog, before the merge.** A `repositoryRef` resolves only within the
yaml file that declared it; a code service can never name a yaml `repositories:` entry, and vice
versa. That keeps `LoadedConfig.Load`'s merge unchanged in shape — it merges composed definitions —
and adds two checks beside the existing duplicate-service one: a repository name declared in both
catalogs, and the `CheckoutName` uniqueness of the section above.

**The checkout re-keying** is where #66 closes:

- Every `ManagedRepoRoot` / `IsColdManagedCheckout` caller passes `CheckoutName`. Two of the five
  (`IsColdManagedCheckout` itself, and `DeferredCheckout.ShouldDefer`) grow a parameter; both of
  `ShouldDefer`'s callers already hold the definition, so it is a widening rather than plumbing.
  `ManagedRepoRoot` stays a pure function, preserving `DeferredCheckout`'s guarantee that the path is
  computable from committed config.
- `LocalCheckoutPrefetch._checkouts` and `_progress` re-key to `CheckoutName` — one task and one
  progress stream per checkout, which is the whole of criterion 1 and 3. `_requested` and `_resolved`
  stay **per service**: they answer "did the AppHost really ask for this", which is a question about
  a service. A checkout is fully resolved once every requested service on it is.
- The speculative candidate filter collapses grouped services naturally (it already de-duplicates by
  dictionary insertion), but its containment and cold-checkout predicates must be asked about
  `CheckoutName`.
- `FailedCheckoutMessage` names one service and tells the developer to clear that service's `source`.
  For a shared checkout it names the services on it — criterion 5, which is otherwise the easiest
  half of this to forget.

---

## Sequencing against #134 Stage 2

Stage 2 adds `WithPrepare` and the typed `AsJava`/`AsJavaScript` handles. It touches neither the
repository fields on the domain type nor the checkout keying, so **there is no code dependency in
either direction.** What matters is only that `WithPrepare` ships on the service builder and gains a
repository-level twin here, rather than shipping on the service and being *moved*: this package has
no ApiCompat tooling (code-catalog design finding 10), so a move is a break nothing would catch.
Either order works if that holds. Stage 2 first is preferred, because it is smaller and already
specified.

## What this deliberately does not do

- **It does not unify two ungrouped services naming one URL.** That is criterion 2, and it is the
  line between "the developer said these are one repository" and "these strings happen to match".
- **It does not implement #66's option B** (one tree per distinct ref). Option A is chosen, as #66
  recommends; B stays the fallback if a real use case for two refs in one AppHost run turns up.
- **It does not add per-repository *kinds* or projects.** A repository groups a URL, a ref and a
  prepare step. Everything else stays per service, because everything else is per service.
- **It does not touch backing services.** They read no catalog at all.
- **It does not serialize anything.** #11's seam is unchanged.

## Consequences accepted

- **`ServiceDefinition` stops being a self-contained record.** Reviewer decision 6 left it "plainly
  serializable" for #11; serializing services independently now duplicates the repository. If #11
  ever commits to a format, `CheckoutName` is the natural key to serialize a reference as. No format
  is committed here.
- **Two `AddRepository` calls naming the same upstream in different spellings** (`.git` suffix, ssh
  vs https) stay two records with two checkouts. `CheckoutName` uniqueness makes a directory clash
  unreachable, so the cost is a duplicate clone the developer asked for by writing it twice. See
  open question 5.
- **A grouped repository's ref is a single value for the whole AppHost run.** That is the point, and
  #66 argues it is the coherent model ("a monorepo pinned to two different commits in one AppHost run
  is arguably already a mistake"), but it is a capability today's shape technically has.
- **One more reserved kind name** (`repositoryRef`), per the yaml section.

## Staging

Two stages, matching the repo's delivery shape:

| Stage | Contents | Acceptance reached |
| --- | --- | --- |
| **1** | `RepositoryDefinition` + `CheckoutName`, with **every** record anonymous. Both producers mint one per service; the ~8 consumer files read through it. No new public API, no new yaml, no behaviour change. | None — but criterion 6 is proved here, by the existing suite passing untouched. |
| **2** | `AddRepository`/`RepositoryDefinitionBuilder`/`WithProject`; yaml `repositories:`/`repositoryRef:`; the third developer-config shape; the checkout and prefetch re-keying; prepare moved; samples, README, changelog. | All six. **#66 closes here.** |

Stage 1 is deliberately a no-behaviour-change refactor that must go in green with nothing else in
it — it is the stage that can silently break criterion 6, and the only way to see that it has not is
for it to contain nothing else. It also does not depend on #134 Stage 2 at all, so it can land in
parallel.

## Testing

Mirroring the repo's layout, `Method_Condition_ExpectedOutcome`:

- `Catalog/RepositoryHandleTests.cs` — `AddRepository` derives its name from the URL and strips
  `.git`; an explicit `name:` wins; a derived name colliding with another repository is refused
  asking for `name:`; a name that is not a contained directory name is refused (#224);
  `WithRepository` on a grouped service is an error naming both; `WithProject` sets the project by
  either route and twice is the additive error.
- `Catalog/CatalogCompositionTests.cs` — extended: a repository name colliding with an ungrouped
  service's name is refused at composition, naming both; a repository name declared in both catalogs
  is the duplicate error; a code service cannot reach a yaml repository.
- `Config/ServiceCatalogLoaderTests.cs` — extended: `repositories:` with nothing under it;
  an unknown key inside a `repositories:` entry; `repositoryRef` naming nothing; `repository:` and
  `repositoryRef:` together; `defaultRef:` beside `repositoryRef:`; `prepare:` on a service carrying
  `repositoryRef:`.
- `Git/LocalGitCheckoutTests.cs` — **the criterion-6 guard:** an ungrouped service's managed repo
  root is exactly `checkouts/<service>` before and after. Cheap, and the only thing that will catch a
  regression that otherwise orphans working trees silently.
- `Sources/LocalCheckoutPrefetchTests.cs` — `FirstAddService_TwoServicesInOneRepository_…` **inverts**
  to one download for two grouped services (criterion 1 and 3), and the `Barrier(2)` reasoning is
  dropped with it; two *ungrouped* services naming one URL still download twice (criterion 2); a
  failed shared checkout's notice names every service on it (criterion 5).
- `Prepare/CheckoutPreparationTests.cs` — two grouped services run the step **once** per tree per
  commit (finding 3, the bug this closes); a `local.path` member of a group runs its own block and
  inherits nothing.
- `Config/DeveloperConfigValidatorTests.cs` — the repository shape's near-miss and unknown-key
  diagnostics; `local.ref` on a grouped service is the named error (criterion 4).

## Documentation

- README: the monorepo shape, in the code and yaml catalog sections; `local.path` documented as the
  per-service escape from a group; the `repositories:` block in the `servicesources.local.json`
  reference.
- Both code-catalog samples gain a grouped repository; `DemoAppHostTypeScript/servicesources.yaml`
  gains a `repositories:`/`repositoryRef:` pair.
- CHANGELOG: the feature, `#66` fixed, and the `repositoryRef` kind-name narrowing.

## Open questions

1. **Does `AddRepository` derive its name, or require one?** This design derives it from the URL with
   an optional `name:` override, which keeps #291's sketch verbatim and gives a readable
   `checkouts/monorepo`. The cost is that the checkout directory becomes an implicit function of the
   URL, so a URL edit renames the tree — and a renamed tree is a re-clone. Requiring the name makes
   that explicit at the cost of the sketch's ergonomics.
2. **Is a repository with no services an error?** `catalog.AddRepository(…)` with nothing added to it
   is representable and harmless (nothing reads it). Report it, or leave it?
3. **Do `WithRepository`'s `project:` and `defaultRef:` parameters survive?** Now that `WithProject`
   exists and the ref belongs to the record, `WithRepository(url, project:, defaultRef:)` is three
   ways to say two things. Keeping them is source-compatible with Stage 1 and better for the
   single-service case; dropping either is a break, and Stage 1 has shipped.
4. **Should `defaultRef` on the handle be required?** #291's sketch passes it every time. Optional
   matches today's `ServiceMetadata.DefaultRef` being nullable (meaning "whatever the clone's default
   branch is"), which is a real and common answer for a monorepo.
5. **Two repositories, one upstream — warn or stay silent?** `RepositoryUrlsMatch` already knows how
   to compare two spellings. A warning would catch a developer who meant to group and did not; it
   would also fire on someone who deliberately wants two trees.
6. **Is `RepositoryDefinitionBuilder` the right name?** It is symmetric with
   `ServiceDefinition`/`ServiceDefinitionBuilder`, and long. `RepositoryBuilder` is shorter and less
   precise. Public type, no ApiCompat, so it is worth one minute now.

Questions 1 and 3 change the public surface rather than its details; the rest are local.
