# Aspire.Hosting.ServiceSources — A `"workspace"` Source for Services Living in the AppHost's Own Repo

**Date:** 2026-09-26
**Status:** Draft — my own proposal, not yet reviewed. No GitHub issue exists for this (checked via
`search_issues` against the repo on 2026-09-26); open one if this direction is accepted.
**Resolves:** nothing filed yet. Motivated by a direct ask: "make ServiceSources work better in a
repo with multiple services and an AppHost together."

---

## Motivation

Every existing source assumes the service's code is *not already sitting next to the AppHost*:

- `"local"` clones a separate repository into `.servicesources/checkouts/<name>/` and reconciles it
  onto a `ref`.
- `"url"`/`"container"`/`"kubernetes"` reach something that isn't source at all.

None of them fits the shape of a genuine monorepo — one repository holding the AppHost *and* the
services it depends on, all checked out together, all on the same commit, by construction. Today that
shape is handled only as a workaround: every developer writes an identical `local.path` override in
their own gitignored `servicesources.local.json` —

```json
{ "services": { "orders": { "source": "local", "local": { "path": "../services/orders" } } } }
```

— for a path that is not personal at all. It's the same path for every developer and for CI, checked
into the repository the instant the service moves. Three concrete costs of that workaround:

1. **It's duplicated N times instead of declared once.** `local.path` exists so a developer can point
   at *their own* out-of-tree clone (`README.md:379`); using it for a fact that's actually a property
   of the shared catalog inverts what the file is for.
2. **`servicesources.yaml` still requires a `repository:`** for a `"local"` service — confirmed at
   `Config/ServiceCatalogLoader.cs:288-299` and enforced again at
   `Sources/LocalProjectSource.cs:256-274` (`RequireRepositoryToCheckOut`). A service that's already
   in this repository has no URL to give it; the only way past that check today *is* the `local.path`
   exemption, which is per-developer by design.
3. **`defaultSource: local` is scary for the wrong reason here.** The README's own warning —
   `defaultSource: local` means every developer, and CI, clones and builds that repository by default
   (`README.md:319-322`) — doesn't apply to a monorepo sibling at all: there's nothing to clone. But
   nothing today lets a catalog author say "default to running this in place" without also saying
   "and clone it," because those two are the same source.

## What's already solved, so this doesn't re-solve it

- **A service's own repo being a monorepo** (multiple services share one *external* repository) is
  `repositories:`/`AddRepository`+`WithSharedRepository` (README "Several services from one
  repository", `README.md:905-953`). Unrelated to this proposal — that's about not cloning the same
  external repo twice; this is about not cloning at all.
- **The AppHost's own repo holding config for several AppHosts** (directory walk-up for
  `servicesources.yaml`) is a separately deferred, separately scoped idea
  (`docs/superpowers/specs/2026-08-09-servicesources-phase2-future-work.md:31-33`). Orthogonal to this
  proposal: that's about *finding* the catalog file; this is about one catalog entry's *resolution
  strategy*.

## Findings that constrain the design

Read off `main` on 2026-09-26.

**F1 — kind handlers are already checkout-shape-agnostic.** `ILocalResourceKind.Resolve`/`Validate`/
`ResolveDeferred` (`ILocalResourceKind.cs:18-22,59-61,132-136`) take a plain `repoRoot` string and an
opaque `rawConfig`. Nothing about `java`/`javascript`'s handlers, or the built-in `dotnet` path's
`ResolveProjectFile`/`ConfineProject` (`Sources/LocalProjectSource.cs:494-530`), cares whether
`repoRoot` came from a clone or was always there. A no-clone source can reuse every kind handler
unchanged.

**F2 — the `path`-override machinery already does everything a no-clone resolution needs, just as a
per-developer escape hatch rather than a catalog-level default:**
- No clone, no ref, no reconciliation — `LocalGitCheckout.PrepareRepoRoot` returns the directory as-is
  when `config.Local.Path is not null` (`Git/LocalGitCheckout.cs:320-346`).
- `RequireRepositoryToCheckOut` already exempts it — "A `local.path` override is exempt: it points at
  a checkout the developer already has" (`Sources/LocalProjectSource.cs:244-246`).
- A `prepare` block resolves against a marker keyed on the resolved path rather than on a commit,
  living at `<AppHostDirectory>/.servicesources/prepare/<service>.json` rather than inside a `.git/`
  the checkout doesn't privately own (README "A `path` checkout declares its own step",
  `README.md:546-577`). The catalog's own `prepare:` block is *ignored* for a `path` override, with a
  once-per-start notice, precisely because the override is personal and the catalog block is the
  team's (`README.md:567-586`).

**F3 — `ref` has no meaning for a sibling directory.** `ConfiguredReference`
(`Git/LocalGitCheckout.cs:423-425`) resolves to `repositoryConfig?.Ref ?? config.Local.Ref ??
definition.Repository.DefaultRef` — all three are "which commit should this *separate* checkout be
on," which presupposes the checkout can be on a different commit than the AppHost. A workspace
directory is on whatever commit the whole repository — AppHost included — is on. There is no second
commit to name.

**F4 — `prepare`'s default mode is keyed on a concept that doesn't exist here.** `oncePerCommit`
re-runs "when the checkout moves to another commit" (README "Choosing a mode",
`README.md:484-491`), tracked via a marker holding "a hash of the resolved command and the commit it
ran against" (`README.md:534-540`). A workspace directory doesn't move to another commit
independently of the AppHost — the whole repository does, on every commit, including ones that touch
nothing under the workspace path. Keying on the AppHost's own commit would re-run the step on every
unrelated commit; keying on nothing would silently never re-run when the sibling service's own files
changed. Neither reading is `oncePerCommit`'s.

**F5 — `UseDeferredCheckout()` is explicitly scoped away from anything with nothing to clone**
(README "Scoped deliberately narrowly", `README.md:773-784`): "the other sources — `url`,
`kubernetes` and `container` — never clone a repository, so they have nothing to defer." A workspace
source belongs in that list, not in the `"local"` deferral story: there's no cold-start network wait
to move off the `AddService()` thread, so deferral has nothing to buy here and the deferred
endpoint/launch-profile caveats (`README.md:730-771`) don't apply — a workspace service always gets
full eager-path fidelity.

**F6 — the security story is strictly simpler, not merely different.** `SECURITY.md` (referenced from
`README.md:377-378`) frames `"local"`'s risk as: a resolved service "can build and run code the
checkout's own repository controls," and `ref` is how a team turns "whatever's at the tip" into "a
reviewed checkout." A workspace-sourced service has no second repository and no second trust
boundary at all — it's the same commit, same review, same CI as the AppHost itself. There is nothing
to pin, because there is nothing that can drift out from under the AppHost's own review process.

## Proposed shape

### A fifth source value: `"workspace"`

Alongside `"local"`/`"url"`/`"container"`/`"kubernetes"`, add `"workspace"`: a service that lives at a
fixed, repository-relative path next to the AppHost, resolved with zero git activity.

```yaml
services:
  orders:
    workspace:
      path: services/orders          # relative to the AppHost directory; required
    project: src/Orders.Api/Orders.Api.csproj   # relative to workspace.path — same field, same rules as local's project
    defaultSource: workspace          # safe here, unlike defaultSource: local — see below
```

```json
{ "services": { "orders": { "source": "workspace" } } }
```

— or nothing at all in `servicesources.local.json`, once `defaultSource: workspace` is set, since
resolving it costs a developer nothing they don't already have.

Non-dotnet kinds work exactly as under `"local"` — `kind:`/`<kind>:` unchanged, because F1 means the
handler never learns the checkout wasn't cloned:

```yaml
services:
  frontend:
    workspace:
      path: services/frontend
    kind: javascript
    javascript:
      appDirectory: .
      runScript: dev
```

In code (mirroring `WithRepository`, `Catalog/ServiceDefinitionBuilder.cs:49-63`):

```csharp
catalog.AddService("orders")
    .WithWorkspace("services/orders")
    .WithProject("src/Orders.Api/Orders.Api.csproj");
```

`WithWorkspace` and `WithRepository`/`WithSharedRepository` fill the same "where does this service's
code come from" slot and are mutually exclusive the same way those two already are with each other
(`ServiceDefinitionBuilder`'s `_repositorySource` guard, `Catalog/ServiceDefinitionBuilder.cs:20-23`)
— a service naming both is the additive "block already set" error, not a merge.

**Why a new source value instead of teaching `"local"` to work without a `repository:`.** `"local"`'s
error messages, README sections and mental model are all built on "this clones something"; a `"local"`
service with no repository silently meaning "look next to the AppHost instead" would be a second,
unrelated behavior hiding behind one string. A separate `"workspace"` value keeps `RequireRepositoryToCheckOut`
(`Sources/LocalProjectSource.cs:256-274`) exactly as it is for `"local"`, and gives the new resolution
path a name that describes what it actually does — and a `source: workspace` typo in
`servicesources.local.json` reads immediately, where `source: local` with no `repository:` reads as
"forgot the url."

### Catalog field: `workspace.path`, not a second meaning for `path`

Nested under a `workspace:` block — following the shape of `url:`/`container:`/`kubernetes:` — rather
than a bare top-level `path:` field, deliberately: developer-config already has `local.path` meaning
"a directory *I personally* point this at, outside the tool's management." A top-level catalog
`path:` field would be a second, opposite meaning (a fact the *team* declares) one edit-distance away
from the first, in the same document family. `workspace.path` can't be confused with it.

`workspace.path` is confined to the AppHost directory the same way `project`/`java.jarPath` are
confined to a checkout (`Sources/LocalProjectSource.cs:525-577`, `CheckoutRelativePath`) — the catalog
is shared, cloned configuration; an absolute or `..`-climbing `workspace.path` would have the AppHost
build something outside the repository the catalog describes. It must resolve to a directory that
exists at composition time — there's no clone to wait for, so a missing directory is reported
immediately, distinctly from "clone failed":

> Service 'orders': workspace path 'services/orders' does not exist under
> '/path/to/apphost'. A workspace service names a directory already checked out beside the AppHost —
> there's nothing to clone here. If this service actually lives in a separate repository, use
> WithRepository(...)/'repository:' instead.

### `ref` is not offered

No `workspace.ref`, no `defaultRef` equivalent, no `local.ref` equivalent for a `"workspace"` service
(F3). If a developer's `servicesources.local.json` sets `local.ref` on a workspace-sourced service,
that's a configuration error naming the service and explaining why — the same shape as the existing
"grouped service, `local.ref` cannot be set" check (`Git/LocalGitCheckout.cs:308-312`) reuses for a
reason that isn't grouping.

### `prepare`: `once`/`always`/`never` only, no `oncePerCommit`

F4 rules out the default mode outright. `WithPrepare`/`prepare:` on a `workspace` block accepts
`PrepareMode.Once`, `.Always`, `.Never` and rejects `.OncePerCommit` (or its yaml spelling,
`"oncePerCommit"`) with:

> Service 'orders': prepare.mode 'oncePerCommit' does not apply to a 'workspace' service — there is no
> separate commit for this directory to move to independently of the AppHost's own repository. Use
> 'once' (re-run only when the command itself changes) or 'always' (an incremental script that decides
> its own work, per README's "Choosing a mode") instead.

The marker reuses the `path`-override home exactly (F2): `<AppHostDirectory>/.servicesources/prepare/
<service>.json`, keyed on the resolved workspace path and the command — not on a commit, because
there isn't one to key on. Unlike the `path`-override case, the catalog's own `prepare:` block is
**not** ignored here: there is no "someone else's directory" the way a developer's own `local.path`
is — `workspace.path` *is* the catalog's declaration, so its `prepare:` block is the one that runs,
the same as `"local"`'s catalog `prepare:` runs for a managed checkout.

Two services naming the same `workspace.path` (unusual, but possible — e.g. two entry points into one
JS app directory) run `prepare` under the same lock discipline `CheckoutNameLock` gives managed
checkouts (`Git/LocalGitCheckout.cs:157`), keyed on the resolved absolute path instead of a
`CheckoutName`, so two such services can't race the same bootstrap command.

### `defaultSource: workspace` is safe to set

Unlike `defaultSource: local`'s documented warning (`README.md:319-322`), `defaultSource: workspace`
costs a developer or CI nothing they don't already pay to have the AppHost itself: the directory is
already in the checkout, by construction. The README section on `defaultSource` should say this
explicitly, as the one case where committing a default source is close to free — the direct
counterpart to the existing warning rather than a silent exception to it.

### Interaction with `UseDeferredCheckout()`

None (F5). A `"workspace"` service is always resolved eagerly, with full launch-profile fidelity,
regardless of whether `UseDeferredCheckout()` is on — the same as `url`/`container`/`kubernetes`
today. Worth one line in that section of the README so a reader doesn't go looking for why a workspace
service never shows a "Preparing" clone state.

### Interaction with `repositories:` grouping

None needed. Grouping (`repositoryRef`/`WithSharedRepository`) exists to avoid cloning one external
repository twice; a workspace service has nothing to clone at all, so there's no shared-checkout
concept to opt into. Two services naming the same `workspace.path` (see `prepare` above) are simply
two entries pointing at one directory — no `repositories:`-style entry is needed to declare that,
because there's no checkout identity to key a lock or a clone on beyond the path itself.

### SECURITY.md

One short addition: a `"workspace"` service carries none of `"local"`'s external-repository risk (F6)
— it is the AppHost's own commit, reviewed the same way the AppHost is. `ref`/SHA-pinning guidance
doesn't apply and should say so, rather than leaving a reader to wonder why it's missing.

## What this deliberately does not do

- **It does not change `ILocalResourceKind`.** F1 — no interface member moves, no kind package needs
  to change to support it.
- **It does not give workspace services a `ref`, a managed checkout, or a `.servicesources/checkouts/`
  entry.** There is nothing to manage; the directory is the developer's own working tree, already
  under the AppHost's own version control.
- **It does not support deferred checkout.** F5 — there's nothing to defer.
- **It does not replace `local.path`.** `local.path` still exists for the case it was built for — a
  developer's own out-of-tree clone of a genuinely separate repository. `"workspace"` is for the
  in-repo case specifically; a `"local"` service can still be redirected with `local.path` exactly as
  today.
- **It does not add repository grouping for workspace services.** Not needed — see above.

## Open questions (mine, not yet decided)

1. **Method name.** `WithWorkspace(path)` reads well beside `WithRepository(url, defaultRef:)`, but
   `WithPath` was considered and rejected — it reads as the same concept as `local.path` one layer up
   the wrong way. Worth a second opinion before freezing the public name.
2. **Should `workspace.path` default to `"."`** (the AppHost's own directory) when omitted, for the
   degenerate case of a service that's the AppHost's own project reached through `AddService` for
   uniformity with services that vary per developer? Leaning no — that's just `AddProject`, and
   `AddService` should not grow a mode that makes it point at itself.
3. **Combining `workspace` with `repository`/`local` on one entry** (the existing "every source on one
   entry" pattern, README "Combining sources on one catalog entry"). Nothing here forbids it — a
   catalog could offer both `workspace:` (for someone inside the monorepo) and `repository:` (for
   someone consuming the service from outside it, if the service is ever split out) — but it's an
   unusual enough shape that it deserves an explicit yes/no rather than falling out by accident.
4. **Naming collision with a future central registry (#11).** `"workspace"` is a reasonable word for a
   registry entry to mean something else entirely later. Worth a note in that issue if this ships
   first, not a blocker here.

## Staging (sketch, not committed to)

| Stage | Contents |
| --- | --- |
| **1** | `"workspace"` source end to end for the `dotnet` kind: catalog field (yaml + code), resolution (no clone, confinement, missing-directory error), `defaultSource` interaction, README + SECURITY.md updates, tests |
| **2** | `prepare` support (`once`/`always`/`never`, marker reuse, the `oncePerCommit` rejection message) |
| **3** | Non-dotnet kinds — should fall out of F1 almost for free, but wants its own test pass against `java`/`javascript` handlers specifically |

## Testing (sketch)

- Catalog loader: `workspace.path` required, confined (absolute/`..`/unusable segment rejected with
  the existing `CheckoutRelativePath` messages), mutually exclusive with `repository`/`repositoryRef`.
- Resolution: missing directory at composition time reported by name, distinct from a clone failure;
  `dotnet`/`java`/`javascript` kinds all resolve against a workspace `repoRoot` unchanged from their
  existing checkout-based tests (same handler, different `repoRoot` source — largely a parametrization
  of existing kind tests rather than new ones).
- `ref`/`local.ref` rejected on a workspace service, naming why.
- `prepare`: `oncePerCommit` rejected with the F4 message; `once`/`always` markers keyed on path
  reused correctly; two services sharing one `workspace.path` serialize their `prepare` under one lock.
- `defaultSource: workspace` resolves with no `servicesources.local.json` entry at all.
- `UseDeferredCheckout()` present has no effect on a workspace service (asserted, so the exemption
  can't silently regress the way F5 documents it should never need to).
