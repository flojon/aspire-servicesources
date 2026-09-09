# Aspire.Hosting.ServiceSources — Repository as a First-Class Handle

**Date:** 2026-09-08
**Status:** **Accepted** — the original six open questions answered 2026-09-08, recorded under
[Reviewer decisions](#reviewer-decisions); ready for an implementation plan, with one cosmetic naming
call ([question 7](#open-questions)) outstanding that blocks nothing. Question 3 was settled by
measurement, not judgement: see
[the question-3 ATS probe findings](2026-09-08-repository-handle-q3-ats-probe-findings.md).
Revised again the same day, after review objected that `monorepo.AddService(…)` inverts ownership:
services stay on the catalog and the repository handle is **passed**, as
`WithSharedRepository(monorepo)`. That is a real simplification — one service builder instead of two,
no generic base, no explicit ATS id anywhere, and no silent failure mode to guard.
Revised 2026-09-08, same day: the first draft treated the Stage-1 authoring surface as frozen and
argued three decisions from "no ApiCompat tooling, so a break is uncatchable". That premise was
wrong — **Stage 1 is in no release tag** (see [What is frozen](#what-is-frozen-and-what-is-not)), so
the C# surface is free. One decision changes as a result (`WithRepository`'s `project:` parameter is
dropped), the sequencing constraint against #134 Stage 2 disappears, and one alternative that only
breakage makes available — splitting the builder types — was opened up, adopted once the question-3
probe measured its cost away, and then dropped again as unnecessary once the API stopped inverting
ownership. **The architecture is unchanged** throughout all three revisions, because the constraints
that shaped it are released on-disk state and file formats, not signatures.
**Resolves:** #291 (`AddRepository` returning a shared handle, so several services can name one
repository and one ref).
**Closes:** #66 (two services in one repository clone it twice) — not as a special case, but because
the shared checkout falls out of the shared record. #66 is marked `blocked-by: #134`, and the part
that blocked it (a domain type to hang a repository record on) shipped in Stage 1. **Closes it for
catalogs that adopt grouping**, not for every catalog — #66's own option A would have done the
latter, at the cost of migrating every existing checkout; see the alternative under
[`CheckoutName`](#checkoutname-what-makes-criterion-6-hold). That scope is **accepted**: developers
opt in by switching to the new syntax ([Reviewer decisions](#reviewer-decisions)).
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

    catalog.AddService("orders")
        .WithSharedRepository(monorepo)
        .WithProject("src/Orders.Api/Orders.Api.csproj");

    // Still additive across sources: the repository is the "local" source's block, no more.
    catalog.AddService("payments")
        .WithSharedRepository(monorepo)
        .WithProject("src/Payments.Api/Payments.Api.csproj")
        .WithContainer("payments", port: 8080);

    // Unchanged for the common case: one service, one repository.
    catalog.AddService("inventory")
        .WithRepository("https://github.com/example/inventory", defaultRef: "main")
        .WithProject("Inventory.Api.csproj");
});
```

**This is not the shape #291 sketched, and the difference is deliberate.** #291 proposed
`monorepo.AddService("orders")` — services added *to* a repository. That inverts ownership twice
over. The catalog owns services; and a repository is one source's worth of one service's
configuration, sitting beside `WithUrl`, `WithContainer` and `WithKubernetes`, which the code-catalog
design's finding 4 established a service may carry **all of at once**. Under the inverted shape a
service's very identity is created inside a repository that becomes irrelevant the moment a developer
sets `source: url` in `servicesources.local.json` — and `inventory` in the shipped sample does
exactly that, declaring both a `url:` and a `container:` so the source can be chosen per developer.
Passing the handle keeps every service declared in one place and keeps the repository what it is: a
block, not a parent. [Finding 6 of the question-3 probe](2026-09-08-repository-handle-q3-ats-probe-findings.md)
measured that a handle crosses ATS as a parameter, which is what makes this available at all.

## What is frozen, and what is not

The latest release is `v0.5.1`, and the split runs straight through the middle of this design:

| | in `v0.5.1` | so a change is |
| --- | --- | --- |
| `Catalog/ServiceCatalogBuilder.cs`, `Catalog/ServiceDefinitionBuilder.cs`, `AddServiceCatalog` | **absent** — the whole directory | **free** |
| `ServiceDefinition`, `RepositoryDefinition`, `CodeServiceCatalog` | absent, and `internal` regardless | free |
| `checkouts/<serviceName>` as the managed checkout path | **present** (`LocalGitCheckout.cs:52` there) | a **migration of developers' working trees** |
| the `servicesources.yaml` format | present | a break for every existing catalog |
| the `servicesources.local.json` schema | present | a break for every existing developer |

So "the API is not released yet" frees exactly one thing: **the C# and TypeScript authoring surface
this design adds to.** It frees none of the four constraints that actually shaped the design, because
those are on-disk state and committed file formats. In particular it does **not** license renaming
the checkout directory: that is not a signature anyone recompiles against, it is a working tree
holding uncommitted work, and criterion 6 below stands whatever the API policy is.

Where an argument below turns on compatibility, it now says which of these two it means.

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
5. **ATS: a handle crosses as a parameter; an overload of one would silently lose a half**
   ([question-3 probe](2026-09-08-repository-handle-q3-ats-probe-findings.md), findings 6–9). The
   first is what lets services stay on the catalog. The second is why the handle form is a
   *distinctly named* method rather than an overload — an overload needs an explicit id whose own
   `MethodName` then forces a second TypeScript name anyway, so it buys nothing and costs a silent
   failure mode. Relatedly, ids for `ExposeMethods` *instance* methods are receiver-qualified, so
   findings document 11 and Stage 0's finding 5 — both reading as though a third `AddService` would
   collide — do not apply; that was an *extension* method, keyed by assembly.

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

> **Alternative, considered and rejected — key *every* checkout by a URL-derived name.** This is
> **#66's own recommended option A** (*"`checkouts/<slug-of-repository>`, shared by every service that
> names it"*), and it disagrees with #291, which requires that two services naming one URL without a
> group stay independent (criterion 2). It is worth stating plainly which one this design follows and
> what that costs, because the two close #66 for different populations.
>
> URL-keying for everyone is the stronger fix on one axis: it collapses the duplicate clone for
> **existing** catalogs, including the yaml monorepo user who is cloning five times today and who,
> under this design, keeps doing so until they rewrite their catalog into `repositories:`/`repositoryRef:`.
> That is a real limitation of the choice made here — **#66 closes for adopters, not for everyone.**
>
> **Rejected on three counts, none of which "the API is unreleased" relieves.** (a) It renames every
> existing `checkouts/<service>` directory — a migration of working trees holding uncommitted work,
> which is on-disk state, not a signature. (b) It makes the ref conflict *newly reachable* for people
> who never asked for grouping: two services on one URL with different `local.ref` values work today
> and would become a configuration error. (c) It makes every checkout directory an implicit function
> of a URL string, so editing a URL re-clones. Declaration-based sharing costs the undeclared monorepo
> user a catalog edit; URL-keying costs every existing user a migration they did not ask for.
>
> **The mitigation, and why question 5 mattered more than it looked.** Two
> ungrouped services naming one upstream is exactly the shape that wants grouping and has not been
> told so. A warning there — "these two services name one repository and are cloned twice; group them
> under `repositories:` to share one tree" — turns this design's limitation into a prompt, at the cost
> of one notice. That is the cheap half of option A's benefit with none of its migration, and
> question 5 was **decided "warn"** on exactly that reasoning.
>
> **Settled:** the adopters-only scope is accepted — see
> [Reviewer decisions](#is-closing-66-for-adopters-only-acceptable). This alternative is kept so it is
> not re-proposed, not as a live option.

**One namespace, checked once.** Anonymous names (service names) and grouped names share the
`checkouts/` directory namespace, so a service `orders` and a repository derived to `orders` from
`github.com/x/orders.git` collide on a directory while naming different URLs.
`ReconcileRepoRoot` would eventually catch that as an origin-URL mismatch, which is a confusing
runtime error for a statically detectable clash. **Validated at catalog composition**, naming both
declarations and suggesting `name:`.

### The authoring API

One public type added, two methods added, one method narrowed:

| | Change |
| --- | --- |
| `RepositoryBuilder` | **new**, exported — returned by `AddRepository`; carries `WithPrepare(…)`, and nothing else. **No `AddService`.** |
| `ServiceCatalogBuilder.AddRepository(string url, string? name = null, string? defaultRef = null)` | **new** |
| `ServiceDefinitionBuilder.WithSharedRepository(RepositoryBuilder repository)` | **new** — a distinct name, not an overload; see below |
| `ServiceDefinitionBuilder.WithProject(string project)` | **new** |
| `ServiceDefinitionBuilder.WithRepository(string url, string? defaultRef = null)` | **narrowed** — loses `project:`; see below |

**One service builder, not two.** An earlier revision of this design split
`ServiceDefinitionBuilder` into a grouped and an ungrouped type over a generic base, so that
`WithRepository` on a grouped service would not compile. With the handle passed as an argument there
is nothing to split: both spellings of `WithRepository` fill the **same** block, so a service naming
both a handle and a URL is caught by the additive `RequireUnset` guard the design already has for
every other block — the same error, the same wording, no new machinery. Findings 2, 3 and 5 of the
probe (inherited projection, and a generic self-typed base keeping fluent returns) are therefore
measured and **unused**; they are recorded there in case a later design wants them.

**`RepositoryBuilder`, not `RepositoryDefinitionBuilder`** (open question 6). The "Definition" infix
earns its place in `ServiceDefinitionBuilder` because there is a `ServiceCatalogBuilder` beside it to
be distinguished from; there is no second repository builder. The domain record stays
`RepositoryDefinition`.

**`WithRepository` loses its `project:` parameter**, becoming `WithRepository(string url, string?
defaultRef = null)`. Stage 1 folded `project:` in on the code-catalog design's own instruction — *"the
plan should try folding them into it… since that also removes a chain"* — but the chain comes back
regardless the moment a service names a repository by handle and still needs a project. Keeping both
leaves two ways to say one thing, which would then have to agree about which wins. `WithProject`
becomes the only way to set one, and the additive rule covers setting it twice.

This is a **source break against Stage 1**, which is free: Stage 1 is in no release tag, so nothing
outside this repository's own two samples compiles against the three-parameter form.

**Two distinct method names, deliberately not an overload.**

```csharp
public ServiceDefinitionBuilder WithRepository(string url, string? defaultRef = null) { … }

public ServiceDefinitionBuilder WithSharedRepository(RepositoryBuilder repository) { … }
```

A C# overload pair reads better in C#, and a revision of this design used one. It is the wrong trade,
for three reasons the probe measured:

- **An exported overload silently loses one half** (probe finding 7). No `ASPIREEXPORT013`, no error —
  the second overload is simply absent from the generated SDK. It is quieter than the collision stage
  0 found, which at least warned.
- **The fix cannot deliver the thing the overload was for.** Finding 8's rescue is
  `[AspireExport("withSharedRepository", MethodName = "withSharedRepository")]`, and `MethodName` is
  precisely what forces a *second consumer-visible name*. So TypeScript sees two names either way;
  the overload buys single-naming in C# only, in a package whose premise is that these are one API in
  two languages.
- **It would be the design's only explicit id**, and finding 9 says an explicit id is
  **namespace-scoped** rather than receiver-qualified — opting out of the protection implicit ids get
  for free, permanently reserving a namespace-wide name.

With distinct names all three vanish: two implicit, receiver-qualified ids, nothing to reserve, and
no silent failure mode to guard. Measured clean by **probe finding 6**, which is exactly this shape —
a distinctly-named instance method taking a handle, projecting with an implicit id and no warnings.

The honest argument the other way is that `WithRepository` is conceptually right for both, since they
fill the same block, and two names imply they differ. That argument mostly dissolves once TypeScript
shows two names regardless.

**ATS ids everywhere else: none.** An earlier draft budgeted an explicit
`[AspireExport("addServiceToRepository")]`, reading Stage 0's finding 5 as meaning a third
`AddService` would collide. It would not, and there is no longer a third `AddService` at all. Probe
finding 1: ids for `ExposeMethods` **instance** methods are receiver-qualified, so two receivers
cannot collide on a method name; Stage 0's collision was an *extension* method, keyed by assembly,
and does not generalise.

TypeScript, following the sample's actual idiom — and note the handle needs no `await`, because it
arrives as `Awaitable<RepositoryBuilder>` (probe finding 6):

```typescript
const monorepo = catalog.addRepository('https://github.com/example/monorepo', { defaultRef: 'main' });

const orders = await catalog.addService('orders');
await orders.withSharedRepository(monorepo);        // implicit id, no [AspireExport] needed
await orders.withProject('src/Orders.Api/Orders.Api.csproj');
```

(`catalog.addService` rather than `addServiceToCatalog` assumes **#309** lands — the #134
correction dropping an explicit capability id that was never needed. Without it the first call keeps
Stage 1's name and nothing else here changes.)

**Errors on the chain.** `WithRepository` and `WithSharedRepository` on one service is the existing
additive error, naming the service and the repository block — they fill the same block, so the
`RequireUnset` guard already covers it. `WithPrepare` on a service whose repository came
from a handle is a configuration error pointing at the repository — a runtime error, not a compile
one: with a single builder type there is no type to withhold the method from. That is the one thing
the split-builder revision would have caught at compile time, and it is judged not worth two public
types, a generic base, and an export-surface assertion to keep them honest.

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
  anonymous-record setter, and is added to `RepositoryBuilder`. On a service whose repository came
  from a handle it is a configuration error pointing at the repository, the same as the yaml case.
  Keeping it on the service builder at all is an **ergonomic** choice: putting it only on the
  repository handle would force `AddRepository` on a single-service AppHost whose only sin is needing
  a bootstrap command — verbosity #291 explicitly set out to avoid for the one-service case.

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
either direction, and — Stage 1 being unreleased — no compatibility constraint either.** The two are
simply independent; either can land first.

The first draft argued a constraint here: that Stage 2 must ship `WithPrepare` on the service builder
and gain a repository twin rather than have it *moved* later, because with no ApiCompat tooling a
move is an uncatchable break. The premise is void — nothing outside this repository compiles against
Stage 2's surface either. `WithPrepare` still ends up on both builders, but now for the reason in
[the prepare section](#prepare-moves-to-the-repository) alone: an ungrouped service should not have
to call `AddRepository` just to declare a bootstrap command.

Stage 2 first is still mildly preferred, because it is smaller and already specified — a scheduling
preference, not a dependency.

## What this deliberately does not do

- **It does not unify two ungrouped services naming one URL.** That is criterion 2, and it is the
  line between "the developer said these are one repository" and "these strings happen to match".
  It is also the design's main self-imposed limit — see the rejected URL-keying alternative under
  [`CheckoutName`](#checkoutname-what-makes-criterion-6-hold).
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
  unreachable, so the cost is a duplicate clone the developer asked for by writing it twice — which
  the notice decided under question 5 is what points out.
- **A grouped repository's ref is a single value for the whole AppHost run.** That is the point, and
  #66 argues it is the coherent model ("a monorepo pinned to two different commits in one AppHost run
  is arguably already a mistake"), but it is a capability today's shape technically has.
- **One more reserved kind name** (`repositoryRef`), per the yaml section.

## Staging

Two stages, matching the repo's delivery shape:

| Stage | Contents | Acceptance reached |
| --- | --- | --- |
| **1** | `RepositoryDefinition` + `CheckoutName`, with **every** record anonymous. Both producers mint one per service; the ~8 consumer files read through it. No new public API, no new yaml, no behaviour change. | None — but criterion 6 is proved here, by the existing suite passing untouched. |
| **2** | `AddRepository`/`RepositoryBuilder`/`WithProject`/the `WithRepository(RepositoryBuilder)` overload with its explicit ATS id, and `WithRepository(url, …)` narrowed; yaml `repositories:`/`repositoryRef:`; the third developer-config shape; the checkout and prefetch re-keying; prepare moved; samples, README, changelog. | All six. **#66 closes here, for catalogs that group.** |

Stage 1 is deliberately a no-behaviour-change refactor that must go in green with nothing else in
it — it is the stage that can silently break criterion 6, and the only way to see that it has not is
for it to contain nothing else. It also does not depend on #134 Stage 2 at all, so it can land in
parallel.

## Testing

Mirroring the repo's layout, `Method_Condition_ExpectedOutcome`:

- `Catalog/RepositoryHandleTests.cs` — `AddRepository` derives its name from the URL and strips
  `.git`; an explicit `name:` wins; a derived name colliding with another repository is refused
  asking for `name:`; a name that is not a contained directory name is refused (#224);
  `WithProject` sets the project, and called twice is the additive error; `WithRepository` and
  `WithSharedRepository` on one service is that same additive error, naming the repository block. The
  four existing `WithRepository` test files move off the dropped `project:` parameter.
- A sample must actually call `withSharedRepository` from TypeScript, so
  `📘 typescript export surface` exercises the handle-as-parameter path. With distinct names there is
  no silent-drop failure mode left to assert against (that guard was only needed for the rejected
  overload), but the path itself is still worth covering end to end.
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

- README: the monorepo shape, in the code and yaml catalog sections, presented as **the** way to
  express several services in one repository rather than as an advanced variant — the opt-in is only
  real if the docs push toward it (see [Reviewer decisions](#reviewer-decisions)). Plus `local.path`
  documented as the per-service escape from a group, and the `repositories:` block in the
  `servicesources.local.json` reference.
- CHANGELOG should say plainly that an existing catalog keeps its per-service checkouts until it is
  rewritten, so the #66 fix is not read as automatic.
- Both code-catalog samples gain a grouped repository; `DemoAppHostTypeScript/servicesources.yaml`
  gains a `repositories:`/`repositoryRef:` pair.
- Both code-catalog samples also **must** be updated for the narrowed `WithRepository`: they are the
  only callers of its `project:` parameter outside the tests
  (`DemoAppHostCodeCatalog/Program.cs`, and `withRepository(url, { project: … })` in
  `DemoAppHostTypeScriptCodeCatalog/apphost.mts`). The `📘 typescript export surface` CI job builds
  the latter, so a missed update fails the build rather than shipping.
- CHANGELOG: the feature, `#66` fixed, and the `repositoryRef` kind-name narrowing.

## Open questions

**One, and it is a naming call.** The original six were answered on 2026-09-08 and are recorded under
[Reviewer decisions](#reviewer-decisions) below, question 3 having been settled by measurement rather
than judgement — see
[the question-3 ATS probe findings](2026-09-08-repository-handle-q3-ats-probe-findings.md). The
seventh arose from the ownership decision that followed:

7. **`WithSharedRepository` or `WithRepositoryRef`?** The overload question is settled (see the
   decision above); what is left is the spelling, and it now applies to **both** languages rather than
   only the generated SDK. `WithSharedRepository` is accurate, though "shared" is a small lie when one
   service uses the handle. `WithRepositoryRef` echoes yaml's `repositoryRef:` key exactly, which is a
   real argument for it given the two surfaces otherwise mirror each other. Purely cosmetic, and free
   to change until the 0.6.0 tag.

The design is ready for an implementation plan. One item is deliberately left for #134 rather than
folded in here: reverting Stage 1's `addServiceToCatalog` capability id to plain `AddService`, now
that the collision it was invented to dodge is measured not to exist. Filed as **#309**.

## Reviewer decisions

Recorded as they arrive. Each question is kept as it was asked, with the decision beneath it.

### Does `AddService` belong on the repository handle?

Raised 2026-09-08 against #291's own sketch: *"it feels backwards to call AddService(xx) on the
repo"*, followed by *"and what about the other sources?"*

**Decided: it does not. Services stay on the catalog, and the repository handle is passed to
`WithRepository`.** The second question is what settles it beyond taste. A service may carry every
source at once (code-catalog design finding 4), and the shipped sample's `inventory` does — a `url:`
and a `container:`, so `servicesources.local.json` can choose. Under `monorepo.AddService("orders")` a
service's *identity* is minted inside a repository that becomes irrelevant the moment a developer
selects a non-`local` source, which makes the repository look like a parent when it is one block
among four. `catalog.AddService("orders").WithRepository(monorepo)` keeps it a block.

This was available only because [probe finding 6](2026-09-08-repository-handle-q3-ats-probe-findings.md)
measured that a handle crosses ATS as a parameter (as `Awaitable<T>`, so TypeScript need not await
it). It **simplifies** the design: one service builder instead of two, no generic base, and the
"service names both a handle and a URL" case collapses into the additive `RequireUnset` error that
already exists for every other block.

It costs one thing, recorded where it lands: `WithPrepare` on a grouped service goes back to being a
runtime error rather than a compile error, since with a single builder type there is no type to
withhold it from. The second cost this decision briefly carried — an exported overload that vanishes
silently without an explicit ATS id — was removed by giving the handle form a distinct name instead;
see [the authoring API](#the-authoring-api).

### Overload or distinct method name for the handle form?

Raised 2026-09-08, on the revision that spelled the handle form as a C# overload of `WithRepository`:
*"the cost for the overload is just extra code in the extension?"*

**Decided: distinct names — `WithRepository(url, …)` and `WithSharedRepository(handle)`.** The
attribute really is one line, so if that were the cost the overload would win. It is not the cost:

- The overload's rescue is an explicit id whose `MethodName` argument exists **precisely to force a
  second consumer-visible name**, so TypeScript sees two names either way (measured). The overload
  buys single-naming in C# only, in a package whose premise is that these are one API in two
  languages — so the name *count* would differ between them.
- It would be the design's only explicit id, and probe finding 9 says an explicit id is
  namespace-scoped rather than receiver-qualified: opting out of a protection implicit ids get free,
  and permanently reserving a namespace-wide name.
- It opts into a failure mode that is **silent** (finding 7) and pays a test to watch it. Distinct
  names have nothing to watch.

Probe finding 6 already measured the chosen shape clean — a distinctly named instance method taking a
handle, implicit id, no warnings — which is worth noting because it means the safe shape was measured
*before* the risky one was proposed.

### The six open questions

Answered 2026-09-08. Five were accepted as the design already had them; one was measured.

1. **Does `AddRepository` derive its name, or require one?**
   **Decided: derive it**, from the URL's last path segment with `.git` stripped, overridable with
   `name:`. Accepted as drafted, with the implication acknowledged: the checkout directory is a
   function of the URL, so editing a URL renames the tree and the next start re-clones. The
   `CheckoutName` uniqueness check is what keeps a derived name from colliding silently.
2. **Is a repository with no services an error?**
   **Decided: leave it unreported.** Nothing reads it; there is no failure to explain.
3. **Worth measuring whether ATS projects inherited instance methods?**
   **Decided: measure it.** See [the probe findings](2026-09-08-repository-handle-q3-ats-probe-findings.md).
   The answer is yes — inherited methods project from an unexported base onto both derived handles,
   the base stays out of the generated SDK, and a generic self-typed base keeps fluent chains intact.
   The design briefly adopted the split builder types on the strength of it, then **dropped them
   again** when the ownership decision below removed the need; findings 2, 3 and 5 are recorded as
   measured-and-unused. What survives from this probe and does change the design is finding 1 —
   capability ids for `ExposeMethods` instance methods are receiver-qualified, so the planned
   `addServiceToRepository` id is unnecessary, Stage 0's finding 5 is specific to *extension* methods,
   and this branch's own finding 11 is wrong and marked so. Reverting Stage 1's `addServiceToCatalog`
   is **#309**.
4. **Should `defaultRef` on the handle be required?**
   **Decided: optional**, matching today's nullable `DefaultRef` — "whatever the clone's default
   branch is" is a real answer, and a common one for a monorepo.
5. **Two ungrouped services, one upstream — warn or stay silent?**
   **Decided: warn**, as a suppressible notice rather than an error. It is the mechanism by which the
   opt-in below is discoverable, so it is not optional polish; but it must not fail a start, because
   two trees can be deliberate.
6. **Is `RepositoryDefinitionBuilder` the right name?**
   **Decided: `RepositoryBuilder`.** Reasoning in [The authoring API](#the-authoring-api) — the type's
   primary method is `AddService`, so it is a grouping handle rather than a builder of one record, and
   there is no second repository builder for a "Definition" infix to disambiguate from. The domain
   record stays `RepositoryDefinition`.

### Is closing #66 for adopters only acceptable?

Raised 2026-09-08 against the rejected URL-keying alternative under
[`CheckoutName`](#checkoutname-what-makes-criterion-6-hold): this design shares a checkout only where
the catalog *declares* a shared repository, so an existing monorepo catalog keeps cloning N times
until it is rewritten into `repositories:`/`repositoryRef:`. #66's own recommended option A would
have fixed those catalogs too, by keying every checkout on a URL slug.

**Decided: acceptable — developers opt in by switching to the new syntax.** So declaration-based
sharing stands, criterion 2 stands, and no existing checkout is migrated. This closes the fork; the
alternative stays recorded above so it is not re-proposed, not as a live option.

Two consequences worth carrying into the plan rather than rediscovering:

- **The fix has to be discoverable, or the opt-in is theoretical.** A developer cloning five times
  today has no signal that a rewrite would stop it — nothing in the current output mentions that two
  services share an upstream. That made question 5's notice the *mechanism* of the opt-in rather than
  a nicety, and it was **decided "warn"** on that basis — a suppressible notice, never an error,
  since two trees can be deliberate.
- **The README carries the burden either way.** The monorepo shape has to be shown as the recommended
  way to express several services in one repository, not as an advanced variant, or the syntax nobody
  is pushed toward is the syntax nobody adopts.
