# Aspire.Hosting.ServiceSources — Rename `local` to `repository`, and a New `path` Source

**Date:** 2026-09-26
**Status:** Draft — my own proposal, arrived at through discussion, not yet reviewed by a maintainer.
No GitHub issue exists for this (checked via `search_issues` against the repo on 2026-09-26); open one
if this direction is accepted.
**Resolves:** nothing filed yet. Motivated by a direct ask: "make ServiceSources work better in a
repo with multiple services and an AppHost together."
**Revised same day:** the first draft proposed a new, fifth source called `"workspace"`, added
alongside the existing four. Discussion surfaced two things that draft got wrong:

1. `"local"` (clone-and-manage) and the case this design actually wants to add (no clone, ever) are
   both, in the everyday sense, "runs on your machine, from source" — `"local"` doesn't uniquely fit
   the clone-based case, and calling only that one `"local"` reads backwards once a genuinely more
   local (no network, already-on-disk) option exists.
2. **The no-clone case already exists in the codebase**, as `local.path` — a developer-config override
   nested inside `"local"` that skips cloning entirely (`Git/LocalGitCheckout.cs:320-346`). A new
   top-level source for "no clone, existing directory" is that mechanism promoted to first-class,
   not a new mechanism — so it should be named after what it already is, `"path"`, rather than
   `"workspace"`.

Once those two land, `"local"` is left as the odd one out — it's the only source whose `source` value
doesn't match its own catalog block name (`url`↔`url:`, `container`↔`container:`,
`kubernetes`↔`kubernetes:`, but `local`↔`repository:`), and it collides in spelling, though not in
meaning, with `servicesources.local.json` — a different "local" (per-developer settings) entirely.
Renaming `"local"` to `"repository"` fixes both at once and finishes the pattern.

---

## Motivation

Every existing source assumes the service's code is *not already sitting next to the AppHost*:

- `"local"` clones a separate repository into `.servicesources/checkouts/<name>/` and reconciles it
  onto a `ref`.
- `"url"`/`"container"`/`"kubernetes"` reach something that isn't source at all.

None of them fits a genuine monorepo — one repository holding the AppHost *and* the services it
depends on, all checked out together, on the same commit, by construction. Today that shape is
handled only as a workaround: every developer writes an identical `local.path` override in their own
gitignored `servicesources.local.json` —

```json
{ "services": { "orders": { "source": "local", "local": { "path": "../services/orders" } } } }
```

— for a path that is not personal at all. It's the same path for every developer and for CI, the
instant the service moves. Three concrete costs:

1. **Duplicated N times instead of declared once.** `local.path` exists so a developer can point at
   *their own* out-of-tree clone (`README.md:379`); using it for a fact about the shared catalog
   inverts what the field is for.
2. **`servicesources.yaml` still requires a `repository:`** for a `"local"` service — enforced at
   `Config/ServiceCatalogLoader.cs:288-299` and again at `Sources/LocalProjectSource.cs:256-274`
   (`RequireRepositoryToCheckOut`). A service already in this repository has no URL to give it; the
   only way past that check today *is* the `local.path` exemption, which is per-developer by design.
3. **`defaultSource: local` carries a warning that doesn't apply here.** The README's own caution —
   `defaultSource: local` means every developer, and CI, clones and builds that repository by default
   (`README.md:319-322`) — is about the clone. A monorepo sibling has nothing to clone, so there is no
   equivalent danger in committing a default for it, but nothing today lets a catalog say so.

## What's already solved, so this doesn't re-solve it

- **A service's own repo being a monorepo** (several services sharing one *external* repository) is
  `repositories:`/`AddRepository`+`WithSharedRepository` (README "Several services from one
  repository", `README.md:905-953`). About not cloning one external repo twice; unrelated to this.
- **The AppHost's own repo holding config for several AppHosts** (directory walk-up for
  `servicesources.yaml`) is a separately deferred idea
  (`docs/superpowers/specs/2026-08-09-servicesources-phase2-future-work.md:31-33`). About *finding*
  the catalog file, not about one entry's resolution strategy — orthogonal to this.

## Findings that constrain the design

Read off `main` on 2026-09-26.

**F1 — kind handlers are already checkout-shape-agnostic.** `ILocalResourceKind.Resolve`/`Validate`/
`ResolveDeferred` (`ILocalResourceKind.cs:18-22,59-61,132-136`) take a plain `repoRoot` string and an
opaque `rawConfig`. Nothing about `java`/`javascript`'s handlers, or the built-in `dotnet` path's
`ResolveProjectFile`/`ConfineProject` (`Sources/LocalProjectSource.cs:494-530`), cares whether
`repoRoot` came from a clone or was always there. A no-clone source reuses every kind handler
unchanged — and this is also why the *kind* layer (`ILocalResourceKind`, `AddLocalKind`,
`LocalKindRegistry`) keeps the name "local": it correctly names "resolved to a directory and run by a
registered kind," a concept both `"repository"` and `"path"` share. Only the per-service `source`
vocabulary is being renamed, not that layer.

**F2 — `local.path` already implements the whole no-clone mechanism, just as a hidden per-developer
override rather than a catalog-level default:**
- No clone, no ref, no reconciliation — `PrepareRepoRoot` returns the directory as-is when
  `config.Local.Path is not null` (`Git/LocalGitCheckout.cs:320-346`).
- Already exempt from needing a `repository:` at all — "A `local.path` override is exempt: it points
  at a checkout the developer already has" (`Sources/LocalProjectSource.cs:244-246`). This is the
  proof that a service can resolve through this mechanism with *nothing* catalog-declared, which is
  exactly what promoting it to `source: "path"` needs to keep true.
- A `prepare` block resolves against a marker keyed on the resolved path, not a commit, at
  `<AppHostDirectory>/.servicesources/prepare/<service>.json` — not inside a `.git/` the checkout
  doesn't privately own (README "A `path` checkout declares its own step", `README.md:546-577`).
- Explicitly **not confined** to inside the repository — "Set `path` to point at a checkout you
  manage yourself... a relative path is anchored to the AppHost directory" (`README.md:379`), no
  containment check the way `project`/`java.jarPath` get. Correct for a *personal* override (any
  directory on the developer's machine); this asymmetry has to be kept, not flattened, once `path`
  also has a *catalog-declared* form — see "Confinement" below.

**F3 — `ref` has no meaning for a directory that isn't a separate checkout.** `ConfiguredReference`
(`Git/LocalGitCheckout.cs:423-425`) resolves "which commit should this checkout be on" — which
presupposes the checkout can be on a different commit than the AppHost. A `path`-resolved directory,
in-repo or not, is either on whatever commit the whole repository is on (in-repo case) or is the
developer's own working tree they manage by hand (redirect case) — neither has a second commit for
this tool to name.

**F4 — `prepare`'s default mode is keyed on a concept `path` doesn't have.** `oncePerCommit` re-runs
"when the checkout moves to another commit" (README "Choosing a mode", `README.md:484-491`), tracked
via a marker holding a hash of the command and the commit it ran against (`README.md:534-540`). A
`path`-resolved directory doesn't move to another commit independently of whatever repository it's
actually in (the AppHost's, for the in-repo case; the developer's own, unwatched, for the redirect
case). Already true for `local.path` today: **the catalog's `prepare:` block is *ignored* for a `path`
override**, with a once-per-start notice, precisely because the override is personal
(`README.md:567-586`). A catalog-declared `path:` is different — there's no "someone else's
directory" to protect — so its own `prepare:` block should run, but still can't use `oncePerCommit`.

**F5 — `UseDeferredCheckout()` is explicitly scoped away from anything with nothing to clone**
(README "Scoped deliberately narrowly", `README.md:773-784`): "the other sources — `url`,
`kubernetes` and `container` — never clone a repository, so they have nothing to defer." `"path"`
belongs in that list. No cold-start wait to move off the `AddService()` thread, so deferral buys
nothing — a `"path"` service is always resolved eagerly, with full launch-profile fidelity.

**F6 — the security story for a `path` service is strictly simpler than `"local"`'s, and simpler
still for the in-repo case specifically.** `SECURITY.md` frames `"local"`'s risk as: a resolved
service "can build and run code the checkout's own repository controls," and `ref` is how a team
turns "whatever's at the tip" into "a reviewed checkout." A catalog-declared, in-repo `path:` has no
second repository and no second trust boundary at all — same commit, same review, same CI as the
AppHost. A developer-redirected `path` (today's `local.path` case) keeps whatever trust properties
that developer's own directory already had — unchanged by this proposal.

**F7 — `"local"` is the one source whose name doesn't match its own catalog block**, and the only
one that collides in spelling with something else entirely. Every other source's `source` value is
its catalog block's name: `"url"`↔`url:`, `"container"`↔`container:`, `"kubernetes"`↔`kubernetes:`.
`"local"`'s catalog block is `repository:` (`ServiceMetadata.Repository`,
`Config/ServiceMetadata.cs:8`) — never `local:`. Separately, `servicesources.local.json`
(`Config/DeveloperConfiguration.cs`, `FileName`) already uses "local" to mean "per-developer machine
settings" — a real, different, useful sense of the word that a `source: "local"` value sits right
next to without meaning the same thing. Two senses of "local" in one document family is confusing on
its own; introducing a third (`"path"` almost being called "workspace" to dodge the collision, per
the original draft) is a symptom of the same root problem, not a fix for it.

## Proposed shape

### Rename: `"local"` → `"repository"`

Same behavior, new name, matching its catalog block (F7):

```yaml
services:
  orders:
    repository: https://github.com/example/orders   # unchanged
    project: src/Orders.Api/Orders.Api.csproj        # unchanged
```

```json
{ "services": { "orders": { "source": "repository" } } }
```

**`"local"` is retired, not aliased.** A config still naming `source: "local"` must fail with a
specific, named error rather than either (a) silently keep resolving as before, which would make the
rename invisible and pointless, or (b) fall through to a generic "unrecognized source" message, which
sends a reader hunting for a typo they didn't make:

> Service 'orders': source 'local' was renamed to 'repository' — same behavior (clone the catalog's
> 'repository:' url, reconcile onto 'ref'), new name, so it doesn't read as the same word
> 'servicesources.local.json' uses for something else. Change 'source' to 'repository' in
> servicesources.local.json (or wherever this is set). If this service is meant to resolve from a
> directory that's already there with no clone at all, use 'source': 'path' instead.

This is a genuine breaking change, acceptable pre-1.0 per the README's own policy ("while the version
is below `1.0.0`, a breaking change can ship in a minor release") — but it must be *loud*, per the
above, and recorded in the CHANGELOG the way every other breaking change here already is.

Internal type names are **not** part of this rename. `LocalProjectSource`, `LocalGitCheckout`,
`LocalDeveloperConfig`, `LocalKinds`, `ILocalResourceKind`, `AddLocalKind`, `LocalKindRegistry` all
stay — F1 explains why the kind-plugin layer's "local" is a different, correctly-named concept
("resolved to a directory, run by a registered kind"), shared by both `"repository"` and `"path"`.
Only the public per-service `source` string, and the messages that quote it, change.

### New source: `"path"`

```yaml
services:
  orders:
    path: services/orders                          # relative to the AppHost directory; catalog-declared
    project: src/Orders.Api/Orders.Api.csproj       # relative to path, same field/rules as today
    defaultSource: path                             # safe here — see below
```

```json
{ "services": { "orders": { "source": "path" } } }
```

— or nothing in `servicesources.local.json` at all once `defaultSource: path` is set, since resolving
it costs a developer nothing they don't already have.

Non-dotnet kinds are unchanged, because F1 means the handler never learns the directory wasn't
cloned:

```yaml
services:
  frontend:
    path: services/frontend
    kind: javascript
    javascript:
      appDirectory: .
      runScript: dev
```

In code, mirroring `WithRepository` (`Catalog/ServiceDefinitionBuilder.cs:49-63`):

```csharp
catalog.AddService("orders")
    .WithPath("services/orders")
    .WithProject("src/Orders.Api/Orders.Api.csproj");
```

`WithPath` and `WithRepository`/`WithSharedRepository` fill the same "where does this service's code
come from" slot and are mutually exclusive the same way `WithRepository`/`WithSharedRepository`
already are with each other (`_repositorySource` guard, `Catalog/ServiceDefinitionBuilder.cs:20-23`)
— a service naming both is the additive "block already set" error, not a merge or a precedence rule.

### Confinement differs by who wrote the value — same asymmetry that already exists

- **Catalog-declared `path:`** (committed to `servicesources.yaml`, or via code's `WithPath`) is
  **confined to inside the repository** — the same rule `project:`/`java.jarPath` already follow
  (`CheckoutRelativePath`: no absolute path, no climbing out with `..`). The catalog is shared,
  cloned configuration; a committed field that could silently send every developer's build outside
  the repository is exactly what confinement exists to prevent elsewhere, and there's no reason
  `path:` should be the exception.
- **A developer's own override**, in their personal `servicesources.local.json`, stays **unconfined**
  — exactly like `local.path` today (F2), which can point anywhere on disk. This is what continues to
  serve the case `local.path` was originally built for: a developer's own out-of-tree clone of a
  genuinely separate repository.

So the rule attaches to *who wrote the value*, not to the source itself — `project:` (catalog,
confined) next to `local.path` (developer, unconfined) is the same pattern already in production;
`path:` just gets a catalog-declared form to sit alongside the developer-declared one it already had.

### `ref` is not offered

No `path.ref`, no `defaultRef` equivalent (F3). A developer's `servicesources.local.json` setting a
ref on a `"path"` service is a configuration error naming the service, the same shape as the existing
"grouped service, `local.ref` cannot be set" check (`Git/LocalGitCheckout.cs:308-312`) reuses for a
different reason.

### `prepare`: `once`/`always`/`never` only, no `oncePerCommit`

F4 rules out the default mode. `WithPrepare`/`prepare:` on a `path`-sourced service accepts
`PrepareMode.Once`, `.Always`, `.Never` and rejects `.OncePerCommit` (yaml: `"oncePerCommit"`):

> Service 'orders': prepare.mode 'oncePerCommit' does not apply to a 'path' service — there is no
> separate commit for this directory to move to on its own. Use 'once' (re-run only when the command
> itself changes) or 'always' (an incremental script that decides its own work) instead.

The marker reuses the existing `local.path` home (F2): `<AppHostDirectory>/.servicesources/prepare/
<service>.json`, keyed on the resolved path and the command. Unlike a developer's personal
*override*, a **catalog-declared** `path:`'s own `prepare:` block is not ignored — there is no
"someone else's directory" to protect, so it runs the same way `"repository"`'s catalog `prepare:`
runs for a managed checkout. Two services sharing one resolved path serialize their `prepare` under a
lock keyed on that absolute path, the same discipline `CheckoutNameLock` gives managed checkouts
today (`Git/LocalGitCheckout.cs:157`).

### `local.path` is deprecated, not removed yet

Once `source: "path"` exists, `local.path` is the identical mechanism (F2) reachable a second, less
discoverable way — nested under a source whose other machinery (clone, ref reconciliation) it never
touches. Keeping both is two spellings for one behavior, which is exactly the confusion this whole
proposal exists to remove. So:

- `local.path` **keeps working**, unlike the `"local"`→`"repository"` rename above — it doesn't
  change meaning under the old spelling, so it's safe to leave functional during a deprecation
  window, only a warning is owed:

  > Service 'orders': 'local.path' is deprecated. Set 'source': 'path' and 'path':
  > '../services/orders' instead — same behavior (no clone, no ref), as a first-class source rather
  > than a hidden mode of 'repository'. 'local.path' will be removed in a future release; see the
  > CHANGELOG.
- Removed in a later, documented release once the warning has had a real deprecation window —
  timing is a maintainer call, not part of this design.

### `defaultSource: path` is close to free

Unlike `defaultSource: repository`'s documented warning (formerly `defaultSource: local`,
`README.md:319-322`), `defaultSource: path` costs a developer or CI nothing extra when the path is
in-repo: the directory is already in the checkout, by construction. Worth stating in the README as
the direct counterpart to that warning, not a silent exception to it. (A `defaultSource: path` whose
catalog `path:` is missing — a service meant to be *only* developer-redirected — still requires every
developer to supply their own `path` override, exactly as an ungrouped `"repository"` service with no
catalog `repository:` requires every developer to supply `local.path` today.)

### Interaction with `UseDeferredCheckout()`

None (F5). A `"path"` service is always resolved eagerly, full launch-profile fidelity, regardless of
`UseDeferredCheckout()` — same as `url`/`container`/`kubernetes` today. Worth one line in that README
section so a reader doesn't go looking for a "Preparing" clone state that will never appear.

### Interaction with `repositories:` grouping

None needed. Grouping exists to avoid cloning one external repository twice; a `"path"` service has
nothing to clone, so there's no shared-checkout identity to opt into. Two services naming the same
resolved path are just two entries pointing at one directory (see `prepare` above) — no
`repositories:`-style entry needed, because there's no clone identity to key beyond the path itself.

### SECURITY.md

Two short additions: a catalog-declared, in-repo `path:` service carries none of `"repository"`'s
external-repository risk (F6) — same commit, same review as the AppHost, nothing to pin. A
developer-redirected `path` (today's `local.path`) is unchanged from today's story and should say so,
so a reader doesn't wonder why the guidance moved.

## What this deliberately does not do

- **It does not change `ILocalResourceKind`.** F1 — no interface member moves, no kind package needs
  to change.
- **It does not give a `path` service a managed checkout, a `ref`, or a `.servicesources/checkouts/`
  entry.** In the in-repo case there's nothing to manage; in the redirect case it's the developer's
  own working tree, exactly as `local.path` treats it today.
- **It does not support deferred checkout.** F5 — nothing to defer.
- **It does not remove `local.path` immediately.** Deprecated with a working grace period, not a
  breaking cut — unlike the `"local"`→`"repository"` rename, which is a breaking cut because leaving
  the old spelling *working* would be the actual problem (silent meaning-drift is worse than a loud
  break).
- **It does not add repository grouping for `path` services.** Not needed — see above.

## Open questions (mine, not yet decided)

1. **Deprecation window for `local.path`.** One release with a warning? Several? Tied to a version
   milestone (1.0)? Needs a maintainer call, not a default from me.
2. **Combining `path` with `repository` on one catalog entry** (the existing "every source on one
   entry" pattern, README "Combining sources on one catalog entry"). Nothing here forbids a catalog
   offering both — `path:` for someone inside the monorepo, `repository:` for someone consuming the
   service from outside it if it's ever split out — but it's unusual enough to deserve an explicit
   yes/no rather than falling out by accident.
3. **Should a catalog-declared `path:` default to `"."`** (the AppHost's own directory) when the
   service *is* the AppHost's own project? Leaning no — that's just `AddProject`, and `AddService`
   shouldn't grow a mode that points it at itself.
4. **Naming collision with a future central registry (#11).** `"path"` and `"repository"` are both
   reasonable words for a registry entry to mean something else later. Worth a note in that issue if
   this ships first, not a blocker here.

## Staging (sketch, not committed to)

| Stage | Contents |
| --- | --- |
| **1** | Rename `"local"`→`"repository"` everywhere it's user-visible (source vocabulary, error messages, README, CHANGELOG), with the hard, named migration error for the old value |
| **2** | `"path"` source end to end for the `dotnet` kind: catalog field (yaml + code), resolution (no clone, confinement, missing-directory error), `defaultSource` interaction, README + SECURITY.md updates |
| **3** | `prepare` support for `path` (`once`/`always`/`never`, marker reuse, the `oncePerCommit` rejection message); `local.path` deprecation warning |
| **4** | Non-dotnet kinds for `path` — should fall out of F1 almost for free, but wants its own test pass against `java`/`javascript` handlers specifically |

Stage 1 can ship alone and already delivers value (fixes F7's inconsistency); stages 2-4 are additive
on top of it and could in principle ship in either order relative to 1, but doing the rename first
avoids ever having `"path"` exist beside a still-current `"local"` name that visibly doesn't match its
own catalog block.

## Testing (sketch)

- **Rename:** old `source: "local"` produces the named migration error, not a generic "unrecognized
  source" message and not silent success; every existing `"local"`-source test is updated to
  `"repository"` rather than duplicated.
- **Catalog loader:** `path:` required for the `"path"` source, confined (absolute/`..`/unusable
  segment rejected with the existing `CheckoutRelativePath` messages), mutually exclusive with
  `repository`/`repositoryRef`.
- **Resolution:** missing directory at composition time reported by name, distinct from a clone
  failure; `dotnet`/`java`/`javascript` kinds all resolve against a `path` `repoRoot` unchanged from
  their existing checkout-based tests (parametrizing existing kind tests over the source rather than
  writing new ones).
- `ref`/`local.ref`-equivalent rejected on a `path` service, naming why.
- `prepare`: `oncePerCommit` rejected with the F4 message; `once`/`always` markers keyed on path
  reused correctly; two services sharing one resolved path serialize their `prepare` under one lock.
- `defaultSource: path` resolves with no `servicesources.local.json` entry at all.
- `UseDeferredCheckout()` present has no effect on a `path` service (asserted so the exemption can't
  silently regress).
- `local.path` still resolves exactly as before, plus emits the deprecation notice once per start.
