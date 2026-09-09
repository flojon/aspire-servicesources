# Aspire.Hosting.ServiceSources — Implementing #291 (repository as a first-class handle)

**Date:** 2026-09-08
**Status:** Investigation. Answers "how do we implement #291 once #134 Stage 2 lands"; not itself a
design. The design it feeds is
[2026-09-08-repository-handle-design.md](2026-09-08-repository-handle-design.md), which cites these
findings rather than re-deriving them.
**Finding 11 is superseded** — it asserted an ATS capability-id collision that
[the question-3 probe](2026-09-08-repository-handle-q3-ats-probe-findings.md) measured as not
occurring for instance methods. See the note on it.
**Investigates:** #291 (`AddRepository` returning a shared handle), which also closes #66 (two
services in one repository clone it twice).
**Read out of the code at** `origin/main` = `b2ccca4` (#134 Stage 1, "core split +
`AddServiceCatalog`"), with `file:line` evidence. Line numbers are that commit's.
**Builds on:** [the code-catalog design](2026-09-05-servicesources-code-catalog-design.md)
(accepted; reviewer decision 3 deferred this deliberately) and
[the Stage-0 ATS probe findings](2026-09-07-code-catalog-stage0-ats-probe-findings.md).

---

## Where this lands in the #134 sequence

Stage 1 is merged (`b2ccca4`). Stage 2 — `WithPrepare`, and the typed `AsJava`/`AsJavaScript`
handles — is "purely additive" by the design's own Staging table: it adds methods to
`ServiceDefinitionBuilder` and promotes two internal options classes. It touches neither the
repository fields on the domain type nor the checkout keying, so **#291 does not conflict with
Stage 2 and does not need to wait on it for any code reason.**

Two reasons it should still land after, both about sequencing rather than dependency:

1. **`WithPrepare` is the one Stage 2 method whose receiver #291 changes.** Finding 5 below shows
   `prepare` belongs on the repository, not the service, once checkouts are shared. Landing
   `WithPrepare` on `ServiceDefinitionBuilder` first and then moving it is a public-surface break in
   a package with **no ApiCompat tooling** (code-catalog design finding 10). So either Stage 2 puts
   `WithPrepare` on the service and #291 adds a repository-level twin (recommended — see
   [Prepare](#3-prepare-moves-to-the-repository-and-the-marker-already-proves-it)), or Stage 2's
   `WithPrepare` waits for #291. The first is cheaper and keeps Stage 2 shippable.
2. **The domain type is the shared surface.** #291 reopens `ServiceDefinition`, which Stage 2 also
   extends. Sequencing them avoids two concurrent rewrites of the same record.

**#66 is marked `blocked-by: #134`, and that is now satisfied for the part that mattered** — the
domain type exists, and every checkout consumer already reads it rather than the yaml DTO.

## What Stage 1 already paid for

Three things #291 would otherwise have had to do first, all already done:

- **`ServiceDefinition` exists and is the only thing downstream reads.**
  `Config/Catalog/ServiceDefinition.cs:11`, with `Repository`/`Project`/`DefaultRef` inline at
  `:13`, `:15`, `:17`. Adding a `Repository` *record* is now an edit to one internal record and its
  two producers, not the twelve-file sweep the design's blast-radius table priced.
- **`LocalGitCheckout` already takes `ServiceDefinition`.** It was the surprise in that table (seven
  `ServiceMetadata` sites); today `PrepareRepoRoot`/`ReconcileRepoRoot` take the domain type
  (`Git/LocalGitCheckout.cs:232`, `:305`). Nothing in the git layer reads yaml.
- **The yaml DTO is a pure binding type with a single conversion point.**
  `ServiceMetadata.ToDefinition(yamlPath)` at `Config/ServiceMetadata.cs:42` is where yaml becomes
  domain. `repositories:` support is one new root type plus a change to that method's inputs.

Reviewer decision 4 called this exactly: *"Question 3's repository-handle follow-up is about to
propose exactly such a field — better to land the clean split now than retrofit it onto the DTO
under more pressure later."* That bet paid off.

---

## Findings that constrain the implementation

### 1. Repository identity cannot be derived from the URL

#291 is explicit: *"two services naming the same URL **without** an explicit group stay independent,
as today."* So the shared checkout is a property of the **declaration** (`AddRepository` /
`repositories:`), not of the string. A URL-slug key would silently merge two ungrouped services'
checkouts — a behaviour change for every existing yaml AppHost, against acceptance criterion 3 of
#134 ("an existing yaml-based AppHost keeps working unchanged").

**Consequence:** the record needs an identity assigned at declaration time. Each service reaches its
repository through a reference, and an ungrouped service gets its own anonymous record.

### 2. The checkout directory name is a compatibility surface, not an internal detail

`LocalGitCheckout.ManagedRepoRoot` is a pure function of the AppHost directory and one name:

```csharp
// Git/LocalGitCheckout.cs:43-53
public static string ManagedRepoRoot(string appHostDirectory, string serviceName)
    => Path.Combine(ToolDirectory.PathIn(appHostDirectory), "checkouts", serviceName);
```

Those directories are **working trees that hold developers' uncommitted work** — `PrepareRepoRoot`
deliberately leaves an existing one alone (`:276`), and `ReconcileRepoRoot` refuses to move one
that has uncommitted changes onto another ref (`:356`). Keying by an *opaque generated id*, as
#291's sketch phrases it (`checkouts/<repo-id>`), renames every existing checkout, so every existing
AppHost re-clones from scratch on first run and every in-flight change is orphaned in a directory
nothing looks at any more.

**Recommendation — the anonymous repository keeps the service's name.** Give the record a
`CheckoutName`:

| declaration | `CheckoutName` | directory |
| --- | --- | --- |
| ungrouped (`WithRepository`, or inline yaml `repository:`) | the service's name | `checkouts/<service>` — **byte-identical to today** |
| grouped (`AddRepository` / `repositories: monorepo`) | the repository's name | `checkouts/monorepo` |

No migration, no re-clone, and the readable-directory property survives — a developer `cd`s into
`checkouts/monorepo`, not `checkouts/a3f9c1`.

**Where the grouped name comes from** is a decision, since #291's sketch passes no name:
`AddRepository("https://github.com/example/monorepo", defaultRef: "main")`. Recommended: derive it
from the URL's last path segment with `.git` stripped (`monorepo`), with an optional `name:`
parameter to override; reject a derived name that collides (finding 8) and ask for the explicit one.
Yaml needs no derivation — the `repositories:` key *is* the name.

### 3. Prepare moves to the repository, and the marker already proves it

For a managed checkout the prepare marker is stored **inside the checkout**, one per directory:

```csharp
// Prepare/PrepareMarker.cs:66-70
if (managedCheckout)
{
    return Path.Combine(repoRoot, ".git", FileNameInGitDirectory);
}
```

The unmanaged (`local.path`) branch is the one that keys per service (`:81-82`,
`prepare/<serviceName>.json`) precisely so that *"two services pointed at the same directory keep
independent markers"* (`:51`).

So the moment two services share a managed checkout:

- **Same prepare command (the monorepo case — one `prepare.sh` at the tree root):** the shared marker
  is exactly right. The step runs once for the tree instead of once per service. This is the
  behaviour #291 wants, and it already works.
- **Different prepare commands:** they thrash. `ReasonToRun` compares the marker's `CommandHash`
  against the step's (`Prepare/CheckoutPreparation.cs:241`, *"its prepare command has changed
  since it last succeeded"*), so each service's resolution invalidates the other's marker and both
  steps re-run on **every** start, forever, with the last writer's hash recorded.

That is not a new bug this introduces — it is the existing bug that #291's third "left open" item
suspects, made reachable. `mode: oncePerCommit` (the default) silently stops working for grouped
services.

**Recommendation:** `Prepare` moves onto the repository record. Yaml's inline `prepare:` on a
service parses onto that service's anonymous record (unchanged behaviour, since anonymous =
one-to-one); a service inside a named group that sets `prepare:` is a configuration error naming the
service and pointing at the `repositories:` entry. For Stage 2, keep `WithPrepare` on
`ServiceDefinitionBuilder` — it is the anonymous-record setter, so it stays honest — and #291 adds
`WithPrepare` to the repository handle, rejecting the service-level call for a grouped service. No
public break.

### 4. `local.path` already *is* the split-back-out opt-out

#291 leaves open *"whether the split-back-out opt-out is a field on the service or a
repository-level list."* It needs to be neither:

```csharp
// Git/LocalGitCheckout.cs:159
public static bool IsManagedCheckout(ServiceDeveloperConfig config) => config.Local.Path is null;
```

A `local.path` service is by definition **not** in a managed checkout — `PrepareRepoRoot` returns
that directory as-is, *"no clone, no checkout, no fetch, ever"* (`:266`), and `ShouldDefer` refuses
it (`Sources/DeferredCheckout.cs:200`). So a developer who wants one service of a group in its own
tree already has the gesture, it already means what they want, and #66's own text noticed the
converse (*"the `path` override already lets a developer do option A by hand today"*).

**Recommendation:** no new field. Document `local.path` as the per-service escape from a group.

### 5. `local.ref` on a grouped service is #66's conflict, and it is per-service today

`local.ref` lives on the service (`Config/LocalDeveloperConfig.cs:13`) and is read per service
(`ConfiguredReference(definition, config)`, `LocalGitCheckout.cs:291`, `:314`). Two grouped services
configured onto different refs are the *"named configuration error rather than a silent
last-writer-wins"* #66 option A requires — and today they would silently race, because both call
`CheckoutWithFetchRetry` against the same directory.

**Recommendation:** `ref` for a grouped repository is configured at
`ServiceSources:Repositories:<name>:ref`. A `local.ref` on a service belonging to a group is a
configuration error naming the service, the repository, and where to set it instead. On an ungrouped
service `local.ref` keeps working exactly as today (its anonymous record is the only reader).
Ungrouped-service `defaultRef` likewise stays on `WithRepository` — it sets the anonymous record's
ref, so #291's "a service can no longer carry its own `defaultRef`" is true of the *record*, not of
the sugar.

### 6. The prefetch keys four collections by service, and only some of them move

```csharp
// Sources/LocalCheckoutPrefetch.cs:93,100
private readonly Dictionary<string, Task<CheckoutResult>> _checkouts = new(StringComparer.Ordinal);
private readonly Dictionary<string, CheckoutProgress> _progress = new(StringComparer.Ordinal);
```

plus `_requested` and `_resolved` (`:317`, `:397`).

- `_checkouts` **must** re-key to `CheckoutName` — that is the whole of the #66 fix. One task per
  checkout, so the `Barrier(2)` test #66 cites inverts: N grouped services await one task.
- `_progress` follows `_checkouts` (progress is a property of the download).
- `_requested` / `_resolved` stay **per service** — they answer "did the AppHost really ask for
  this", which is a question about the service. `WatchCheckout`/`MarkResolved` (`:355`, `:391`) then
  need the service→checkout mapping to close the right stream, and a checkout is only fully resolved
  once every requested service on it is.
- The candidate filter (`:523-553`) currently selects per developer-config service entry and
  de-duplicates by dictionary insertion at `:562`. Grouped services collapse there naturally —
  but its `IsContainedCheckoutDirectoryName` and `IsColdManagedCheckout` calls (`:540`, `:548`) must
  be asked about `CheckoutName`, not `entry.Key`.
- `FailedCheckoutMessage` (`:288-293`) names one service and tells the developer to clear that
  service's `source`. For a shared checkout it should name the services on it.

### 7. Five call sites derive the checkout path, two of which need a signature change

`ManagedRepoRoot` / `IsColdManagedCheckout` callers:

| site | has a `ServiceDefinition` in scope? |
| --- | --- |
| `Git/LocalGitCheckout.cs:194` (`IsColdManagedCheckout`) | no — takes `serviceName` + config |
| `Git/LocalGitCheckout.cs:271` (`PrepareRepoRoot`) | **yes** |
| `Sources/DeferredCheckout.cs:200` (`ShouldDefer`) | **no** — `(builder, serviceName, config)` |
| `Sources/DeferredCheckout.cs:217` (`Register`) | **yes** |
| `Sources/DeferredCheckout.cs:274` | **yes** |

So `IsColdManagedCheckout` and `ShouldDefer` grow a parameter (the definition, or the resolved
`CheckoutName`). Both callers of `ShouldDefer` already hold the definition —
`LocalProjectSource.Resolve` and `LocalCheckoutPrefetch.WouldBeDeferredIfAdded`
(`LocalCheckoutPrefetch.cs:551-552`) — so it is a widening, not a plumbing problem.

`ManagedRepoRoot` itself needs **no** signature change: it stays
`(appHostDirectory, checkoutName)`. What changes is what each caller passes. That keeps
`DeferredCheckout`'s guarantee that the path is *"computable from the committed config"*
(`DeferredCheckout.cs:49`) intact, since `CheckoutName` is derived at catalog-composition time.

### 8. #224's containment check moves onto the repository name, and the namespace is now shared

`IsContainedCheckoutDirectoryName` (`LocalGitCheckout.cs:120-144`) refuses a name that is not a
single contained directory name — the #224 traversal guard. It must now be asked about
`CheckoutName`. Two consequences:

- A **derived** grouped name is generated by us, so it is nearly always clean — but a URL ending in
  `/..` or a `repositories:` key of `../evil` is developer input and must go through the same check.
- **Anonymous and grouped checkouts share one directory namespace.** A service `orders` (anonymous,
  `checkouts/orders`) and a repository named `orders` (from `github.com/x/orders.git`) both want
  `checkouts/orders` while pointing at possibly different URLs. `ReconcileRepoRoot` would eventually
  catch it as an origin-URL mismatch (`:337-342`), but that is a confusing runtime error for a
  statically detectable collision. **Validate `CheckoutName` uniqueness across both kinds at
  composition**, naming both declarations.

### 9. Yaml is additive by construction for the root key, and quietly narrowing for the service key

`KnownRootProperties = YamlPropertyNames(typeof(ServiceCatalog))` (`ServiceCatalogLoader.cs:19`),
enforced at `:67-75`. **Adding `Repositories` to `ServiceCatalog` makes `repositories:` a legal root
key with no other change** — the reflection the design flagged as a trap is, here, exactly the
right mechanism.

The service-level key is the trap. `KnownTopLevelProperties = YamlPropertyNames(typeof(ServiceMetadata))`
(`:17`) also backs `IsReservedKindName` (`:27`), consumed by `Sources/LocalKindRegistry.cs:28`. So
adding `RepositoryRef` to `ServiceMetadata`:

1. widens the accepted per-service yaml schema (wanted), and
2. **removes `repositoryRef` from the set of names a local kind may register** (incidental, and
   almost certainly fine — but it is a silent public-behaviour change and belongs in the changelog).

`ServiceCatalog.Services` also needs its null-coercing setter pattern (`ServiceCatalog.cs:14-18`)
repeated for `Repositories`: `repositories:` with nothing under it binds null.

### 10. The developer-config side is cheap — a third shape

`DeveloperConfigShape` derives everything by reflection off the entry type
(`Config/DeveloperConfigShape.cs:45-59`), and there are already two:

```csharp
// :24-32
public static DeveloperConfigShape Service { get; } =
    Of<ServiceDeveloperConfig>("Service", "service", ["local", "url", "kubernetes", "container"]);
public static DeveloperConfigShape BackingService { get; } = …
```

Its own doc says the second kind *"inherited the whole of the first one's diagnostics."* A third —
`Repository`, over a new `RepositoryDeveloperConfig { Path, Ref, Prepare }` under
`ServiceSources:Repositories` — is a new type, one `Of<>` line, and one `ValidateAll` call beside
`DeveloperConfiguration.cs:107`. Near-miss diagnostics, blank-normalization and layered-override
behaviour all come for free.

Note `DeveloperConfiguration.CanonicalizeToCatalog` (`:116`) re-keys service entries to the
catalog's spelling; repository entries need the same treatment against the declared repository
names, for the same reason.

### 11. ATS: a third `addService` receiver collides on capability id

> **Superseded 2026-09-08 — this finding is wrong, and measurably so.** It reasoned from Stage 0's
> finding 5 without checking that the shape transferred, and it does not: Stage 0 measured an
> *extension* method (keyed `{Assembly}/{method}`), whereas every `AddService` at issue here is an
> *instance* method on an `ExposeMethods = true` type, whose capability id is **receiver-qualified**
> (`…Catalog/ServiceCatalogBuilder.addService`). Two receivers therefore cannot collide. Measured in
> [the question-3 ATS probe](2026-09-08-repository-handle-q3-ats-probe-findings.md), findings 1 and 4,
> which also show that removing Stage 1's `addServiceToCatalog` id builds with `0 Warning(s)` and
> projects `addService` on both receivers with nothing dropped. **No explicit id is needed**, and
> `addServiceToRepository` below should be read as plain `AddService` → `addService`. The rest of this
> finding — the shapes already measured, and the CI job that guards them — still holds. Kept
> unedited below so the mistake and its correction are both legible.

Stage 0's finding 5 measured this and it is the one row that failed: two receivers exposing a method
that generates the capability id `…/addService` collide, silently, and `MethodName` does **not** fix
it — only an explicit `[AspireExport("…")]` id does. Stage 1 shipped
`[AspireExport("addServiceToCatalog")]` on `ServiceCatalogBuilder.AddService`
(`Catalog/ServiceCatalogBuilder.cs:25`).

`RepositoryBuilder.AddService` is a **third** receiver for that name and needs its own explicit id
(`addServiceToRepository`). Everything else in #291's shape is already measured: a handle returned
from a method on a handle is what `catalog.addServiceToCatalog(…)` → `ServiceDefinitionBuilder`
already does end to end (`samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts:8-12`), and optional
parameter bags (`{ defaultRef: 'main' }`) cross. `ci.yml`'s `📘 typescript export surface` job is
what catches a regression here.

TypeScript shape, following the sample's actual idiom (every call awaited):

```typescript
const monorepo = await catalog.addRepository('https://github.com/example/monorepo', { defaultRef: 'main' });
const orders = await monorepo.addServiceToRepository('orders');
await orders.withProject('src/Orders.Api/Orders.Api.csproj');
```

### 12. In code there is no repository *name* lookup at all

#291's first "left open" item — *"`repositoryRef` naming a repository that doesn't exist"* — is a
**yaml-only** validation. In C#/TypeScript the AppHost holds the handle object, so a dangling
reference is unrepresentable; that is the whole point of the proposal. Both malformed cases
(`repositoryRef` unresolved; an entry setting both `repository:` and `repositoryRef:`) are checks in
the yaml loader, alongside the existing unknown-property checks, and neither has a code-side twin.

Cross-catalog references (a code service naming a yaml `repositories:` entry) should be **refused**
rather than resolved: a `repositoryRef` resolves only within the yaml file that declared it. That
keeps the merge in `ServiceSourcesConfigCache.LoadedConfig.Load` (`:250-305`) unchanged in shape —
it merges composed definitions, and repository resolution happens *before* the merge, per catalog.
Repository names then also need their own duplicate check across the two catalogs, mirroring the
service one at `:292-300`.

---

## The shape this adds up to

```
                    ┌─ Repository record ─────────────────┐
                    │ Url · DefaultRef · Prepare          │
                    │ CheckoutName  (service name if      │
                    │                anonymous)           │
                    └──────────────▲──────────────────────┘
                                   │ reference (object identity in code,
                                   │ name resolution in yaml)
  ServiceDefinition ───────────────┘
    Project · Url · Container · Kubernetes · Kind · KindOptions · Origin
    (Repository/DefaultRef/Prepare no longer inline)
```

Everything that used to ask for `serviceName` to build a path asks
`definition.Repository.CheckoutName` instead. Ungrouped services get an anonymous record whose
`CheckoutName` is the service name — so every path, marker and directory is unchanged for every
AppHost that does not use the new feature.

### Decisions this investigation recommends, and why

| #291's open item | recommendation | because |
| --- | --- | --- |
| split-back-out opt-out: service field or repo list? | **neither** — `local.path` already is it | finding 4 |
| where does `prepare` live? | **the repository**, with the service-level `WithPrepare` kept as the anonymous-record setter | finding 3 (the marker is already per-checkout) |
| dangling `repositoryRef` / both keys set | **yaml-loader validation only** | finding 12 |
| (new) checkout directory name | anonymous → service name; grouped → repository name, URL-derived by default | finding 2 |
| (new) grouped `ref` | `ServiceSources:Repositories:<name>:ref`; per-service `local.ref` on a grouped service is an error | finding 5 |

## Implementation route

Six tasks, in dependency order. Tasks 1–3 are the domain change; 4 is the #66 payoff; 5–6 close it out.

1. **The record, with everything ungrouped.** Add the repository record and `CheckoutName`; move
   `Repository`/`DefaultRef`/`Prepare` off `ServiceDefinition` onto it; both producers
   (`ServiceMetadata.ToDefinition`, `ServiceDefinitionBuilder.Build`) mint an anonymous record per
   service. Update the ~8 consumer files to read through it. **No behaviour change, and the existing
   test suite is the proof** — this is the task that must go in green with nothing else in it.
2. **`AddRepository` + `RepositoryBuilder`** (explicit ATS id per finding 11), `AddService` on the
   handle, name derivation and the uniqueness/containment validation of finding 8. Repository-level
   `WithPrepare`, and the error for a service-level one inside a group.
3. **Yaml `repositories:` + `repositoryRef:`** — the root type with the null-coercing setter, the two
   malformed-case errors, and per-catalog resolution before the merge (finding 12).
4. **Re-key the checkout.** Every `ManagedRepoRoot`/`IsColdManagedCheckout` caller onto
   `CheckoutName` (finding 7's two signature widenings), and the prefetch's `_checkouts`/`_progress`
   onto it while `_requested`/`_resolved` stay per service (finding 6). **This is where #66 closes**,
   and where `FirstAddService_TwoServicesInOneRepository_DownloadsItTwiceConcurrently` inverts.
5. **Developer config**: `RepositoryDeveloperConfig` + the third `DeveloperConfigShape`, the
   canonicalization, and the grouped-`local.ref` error (findings 5, 10).
6. **Samples, README, changelog** — the monorepo shape in both code-catalog samples and a
   `repositories:`/`repositoryRef:` yaml sample; the `IsReservedKindName` narrowing from finding 9
   noted in the changelog.

**Test focus, beyond the mechanical mirrors:** two grouped services produce one clone and one
directory (#66's acceptance); a grouped service's prepare step runs **once** per tree per commit
(finding 3); an ungrouped AppHost's checkout path is byte-identical before and after (finding 2 — a
regression here silently orphans working trees, and nothing else will catch it); a repository name
colliding with an ungrouped service's name is refused at composition (finding 8); `local.path` on one
member of a group splits only that service out (finding 4).

## Open, and worth settling before the design doc

1. **Does `AddRepository` take a name, or derive one?** Recommendation is derive-with-override
   (finding 2), which keeps #291's sketch verbatim. Deriving makes the checkout directory a function
   of the URL, which is a small implicit contract.
2. **Is a repository allowed to carry no service?** `catalog.AddRepository(…)` with nothing added to
   it is representable. Harmless (nothing reads it) or an error worth reporting? Cheap either way.
3. **Does the repository record want to be public?** Everything here keeps it internal, with only
   the *builders* public — matching Stage 1. Nothing in #291 needs otherwise, and #11's registry
   would serialize the record, not expose it.
4. **`GitUrl` normalization for URL matching.** `ReconcileRepoRoot` compares origin URLs with
   `RepositoryUrlsMatch` (`LocalGitCheckout.cs:337`). If two `AddRepository` calls name the same repo
   in different spellings (`.git` suffix, ssh vs https), they stay two records with two checkouts and
   one will hit that mismatch error against the other's directory *only if the names also collide*.
   Finding 8's uniqueness check makes that unreachable, but a warning for "two repositories, same
   upstream" may be worth it.
