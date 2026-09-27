# Aspire.Hosting.ServiceSources — Rename `local` to `repository`, and a New `path` Source

**Date:** 2026-09-26
**Status:** Draft — my own proposal, arrived at through discussion, not yet reviewed by a maintainer.
No GitHub issue exists for this (checked via `search_issues` against the repo on 2026-09-26); open one
if this direction is accepted. All four open questions from the prior revision have since been
investigated against the codebase, CHANGELOG and issue tracker — see "Open questions" below; one of
them (combining `path` with `repository`) turned up a real modeling error in this draft, now fixed.
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

**F8 — `repository:`/`url:`/`container:`/`kubernetes:` already combine freely on one entry, by
design.** README "Combining sources on one catalog entry" (`README.md:1561-1608`) shows all four
blocks on a single `orders:` entry at once — "the catalog just describes *how* each source would
resolve the service; each developer's `servicesources.local.json` picks which one actually applies to
them." Nothing about `path:` is special enough to be excluded from that pattern; a catalog offering
both `repository:` (clone it) and `path:` (it's already here) for the same service, letting different
developers pick per their own situation, is exactly the shape this feature already exists to support.
This directly overturns the first draft of this design, which put `WithPath` in the same
mutually-exclusive `_repositorySource` slot as `WithRepository`/`WithSharedRepository`
(`Catalog/ServiceDefinitionBuilder.cs:20-23`) — that slot exists because `WithRepository` and
`WithSharedRepository` are two spellings of *one* block (a service's own repository, or a shared
one), not because "repository" and "path" compete for one slot the way `url`/`container`/`kubernetes`
don't. `path:` belongs beside them as its own independent, freely-combinable block instead.

**F9 — this project's own precedent for deprecating something is "mark it, don't schedule its
removal."** `UseJava()`/`UseJavaScript()` were deprecated in 0.6.0 (#350) via `[Obsolete("...")]`
plus a CHANGELOG `### Deprecated` entry (`CHANGELOG.md:69-81`,
`Java/JavaServiceSourcesBuilderExtensions.cs:30-34`) — a clear message naming the replacement, and no
committed removal version at all; the issue's own resolution is "marked `[Obsolete]` rather than
renamed" with nothing about when they go away. They still compile, with a warning, on `main` today.
`local.path` is data (a yaml/json field), not code a compiler can flag, so its equivalent is a
runtime notice rather than `[Obsolete]` — but the *policy* transfers directly: deprecate with a clear
message and a CHANGELOG entry, and don't commit to a removal release up front.

**F10 — the closed vocabulary of source names lives in exactly two lists, kept in sync by a test —
plus a third site that compares against the literal string directly, outside either list:**
- `ServiceSourcesBuilderExtensions.Sources` (`ServiceSourcesBuilderExtensions.cs:35-41`) — the actual
  runtime dispatch: `["local"] = new LocalProjectSource(...)`, alongside `"kubernetes"`, `"url"`,
  `"container"`. This is what a developer's own `source:` value in `servicesources.local.json` is
  looked up against (`Sources.TryGetValue(developerConfig.Source, ...)`,
  `ServiceSourcesBuilderExtensions.cs:97`).
- `DeveloperConfigShape.Service.SourceNames` (`Config/DeveloperConfigShape.cs:25-26`) — a second,
  separately-declared list, `["local", "url", "kubernetes", "container"]`, used by
  `ValidateSourceName` (`:200-207`) for a catalog's `defaultSource:`/`WithDefaultSource(...)` — its
  own doc comment confirms this is "the one check both a yaml `defaultSource:` entry... and its code
  twin... run" — and reused by `DeveloperConfigValidator`'s bare-value suggestion
  (`DeveloperConfigValidator.cs:896`) to render `{ "source": "..." }` correctly.
- A comment on `DeveloperConfigShape` (`:97-98`) already notes "the dispatch tables remain the
  authority; a test asserts these agree with them" — so the two lists cannot drift silently, and
  that test's expected values need updating alongside both lists.
- A third site compares a developer's resolved source against the literal string `"local"` directly
  rather than through either list: the "two services, one repository, ungrouped" startup warning
  (`Config/ServiceSourcesConfigCache.cs:528`, `string.Equals(devConfig.Source, "local", ...)`). Easy
  to miss because it's neither dispatch nor validation — it's a diagnostic that silently stops firing
  (not a compile error, not a thrown exception) if left matching the retired name after the rename,
  which is the worst way for this kind of bug to surface.

This closes a real gap in the previous draft, which asserted a config naming `source: "local"`
"must fail with a specific, named error" without saying where that check lives. It lives in **two**
places, not one: the dispatch miss at `ServiceSourcesBuilderExtensions.cs:97-104` (today's generic
"not implemented yet"-avoiding message, which already *dynamically* lists `Sources.Keys` — so once
`"local"` is removed and `"repository"`/`"path"` added there, every *other* bad value's error message
is automatically correct with no further work) needs a special case ahead of that generic fallback
for exactly `"local"`; and `ValidateSourceName` (`DeveloperConfigShape.cs:200-207`) needs the
identical special case for a catalog's `defaultSource: "local"`/`.WithDefaultSource("local")`, which
otherwise would fail only with the generic "not a valid source" message instead of naming the
rename. Both sites, not just the developer-config one, need the loud, specific error — there's no
reason a catalog author's mistake should be explained worse than a developer's own.

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

The identical message is owed in **both** places a source name is checked (F10), not only where a
developer's own `source:` selection is dispatched: a catalog's `defaultSource: "local"` or
`.WithDefaultSource("local")` goes through the same `ValidateSourceName` check
(`Config/DeveloperConfigShape.cs:200-207`) and deserves the same named rename error rather than
falling through to "not a valid source. Expected one of: ...".

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

**`WithPath` combines freely with `WithRepository`/`WithSharedRepository`, `WithUrl`,
`WithContainer` and `WithKubernetes`** — it does not share the `_repositorySource` slot those first
two use to guard against each other (F8). It gets its own field and its own `RequireUnset` guard, the
same as `WithUrl`/`WithContainer`/`WithKubernetes` already do: a *second* `WithPath` call on one
service is the additive "block already set" error, but a `WithPath` next to a `WithRepository` on the
same service is exactly the "combining sources" pattern the README already documents — one developer
picks `source: "repository"` to clone it, another picks `source: "path"` to use the copy they already
have. The single top-level `project:`/`WithProject` field still applies to both: the project's path
relative to the service's root is the same code either way, only how that root was reached differs.

Two small, free consequences of adding `path:` as a plain property on `ServiceMetadata`, worth
recording so they don't look like oversights later:
- **`"path"` becomes a reserved kind name automatically.** `IsReservedKindName` derives its set from
  `ServiceMetadata`'s own properties by reflection (`Config/ServiceCatalogLoader.cs:17,39`), so a
  yaml `kind: path` collides with the new top-level field the same way `kind: repository`/`kind: url`
  already collide with theirs — no separate registration needed, and nothing to remember to add.
- **The "no source configured" error needs `path` added to its list of remedies.**
  `ServiceCatalogLoader.cs:288-299` currently reads "Expected a non-empty 'repository', a
  'repositoryRef', or a 'url'/'container'/'kubernetes' block" for a service with nothing resolvable
  declared at all; a bare `path:` must also satisfy that check, and the message's list of
  alternatives should name it.

**Mechanics — the types this actually adds, named rather than left implicit:**
- `ServiceMetadata.Path` (`string?`, yaml `path:`), mirroring `ServiceMetadata.Repository`
  (`Config/ServiceMetadata.cs:8`) — a plain scalar, not a nested block, since (unlike
  `url:`/`container:`/`kubernetes:`) it has no sibling options of its own to hold.
- `ServiceDefinition.Path` (`string?`), carried through `ToDefinition()` the same way `Repository`
  is (`Config/ServiceMetadata.cs:78-98`).
- `WithPath(string path)` on `ServiceDefinitionBuilder`, its own field and its own `RequireUnset`
  guard (F8) — not sharing `_repositorySource`.
- A new `PathSource : IServiceSource`, sibling to `UrlSource`/`ContainerSource`/`KubernetesSource`
  (all in `Sources/`) — confinement check, directory-exists check, `prepare` (stage 3), then the
  existing `dotnet` project-resolution path or the existing kind-handler dispatch (F1), reusing both
  unchanged. Registered as `["path"] = new PathSource()` in `ServiceSourcesBuilderExtensions.Sources`
  (`:35-41`) alongside the renamed `["repository"] = new LocalProjectSource(...)`.

**The missing-directory error**, drafted rather than left as "reported by name":

> Service 'orders': path 'services/orders' does not exist under '<AppHostDirectory>'. A 'path'
> service names a directory that should already be checked out beside the AppHost — there is
> nothing here to clone. If this service actually lives in a separate repository, give it a
> 'repository:' instead (or add one alongside 'path:' so each developer can pick).

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
  change meaning under the old spelling, so it's safe to leave functional indefinitely, only a
  warning is owed:

  > Service 'orders': 'local.path' is deprecated. Set 'source': 'path' and 'path':
  > '../services/orders' instead — same behavior (no clone, no ref), as a first-class source rather
  > than a hidden mode of 'repository'.
- Following this project's own precedent (F9): a CHANGELOG `### Deprecated` entry naming the
  replacement, no committed removal version. `UseJava()`/`UseJavaScript()` were deprecated the same
  way in 0.6.0 and are still present, still working, on `main` today — nothing here should promise a
  tighter timeline for `local.path` than that precedent sets. Revisit removal only if it becomes a
  real maintenance burden, not on a schedule decided now.

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

## Documentation

Gathered here rather than left scattered through the sections above, matching how prior designs in
this repo close out (e.g. the code-catalog design's own "Documentation" section):

- **README:**
  - "`\"local\"` source options" section retitled/reworded around `"repository"`, with a short note
    at the top on the rename and the migration error.
  - New section for `"path"`, sized like the existing per-source sections, covering: the field,
    confinement (catalog vs. developer, per "Confinement" above), `prepare` mode restrictions,
    `defaultSource: path` being close to free, and no `UseDeferredCheckout()` interaction.
  - "Combining sources on one catalog entry" (`README.md:1561-1608`) gets a `path:` block added to
    its worked example, since F8 makes it a first-class member of that pattern, not a footnote.
  - "The `source` value is matched without regard to case... A name none of the four has is refused"
    (`README.md:1605-1606`) — **"four" becomes "five"** once `path` exists alongside
    `repository`/`url`/`container`/`kubernetes`. Small, easy to miss, wrong the moment either change
    ships without it.
  - "Several services from one repository" and the `prepare`/deferred-checkout sections each get the
    one-line cross-references noted inline above (no `UseDeferredCheckout()` effect; no repository
    grouping needed for `path`).
- **CHANGELOG**, both under `## [Unreleased]`:
  - `### Breaking` for the `"local"`→`"repository"` rename, naming the migration (mirroring the
    `UseDeferredCheckout()` late-call entry's shape, `CHANGELOG.md:23-30`).
  - `### Added` for `"path"`.
  - `### Deprecated` for `local.path`, in the same style as the `UseJava()`/`UseJavaScript()` entry
    (F9, `CHANGELOG.md:69-81`) — message, replacement, no removal version promised.
- **SECURITY.md** — the two additions above.

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

## Open questions — investigated 2026-09-26

The four questions from the previous draft, resolved or narrowed against the actual codebase, issue
tracker and CHANGELOG rather than left as guesses:

1. **Deprecation window for `local.path`.** ~~Needs a maintainer call.~~ **Resolved by precedent
   (F9):** mark it, cite the replacement, don't schedule removal. That's what #350 did for
   `UseJava()`/`UseJavaScript()`, and they're still on `main`, still working, nine-plus releases
   later with no removal date ever set. Nothing here should invent a stricter policy than the one
   already in use.
2. **Combining `path` with `repository` on one catalog entry.** ~~Deserves an explicit yes/no.~~
   **Resolved: yes, and it needs no special-case at all.** F8 — the README already documents and
   encourages exactly this shape for `repository`/`url`/`container`/`kubernetes`; `path` joining that
   list is the default, not an exception carved out for it. The only actual design error this caught
   was in the *first* draft, which wrongly modeled `WithPath` as sharing `WithRepository`'s
   mutual-exclusion slot — fixed above.
3. **Should a catalog-declared `path:` default to `"."`** when the service is the AppHost's own
   project? **Investigated, no evidence of need — decided against, not merely "leaning."** Searched
   the samples directory and this repo's issue titles for any existing self-referencing-AppHost
   request; found none. That's exactly the shape of speculative feature this codebase's own culture
   avoids (see, e.g., the central-registry issue's "no format is committed to... the minimum seam
   needed" reasoning). A service that *is* the AppHost's own project is already `AddProject`; giving
   `AddService` a mode that points it at itself solves a problem nobody has raised. Left out.
4. **Naming collision with a future central registry (#11).** **Checked directly: nothing to
   collide with yet.** Issue #11's own body, unedited since it was filed on 2026-08-13, ends "no
   further detail captured yet — genuinely open." There is no shape, no vocabulary, and no schema
   draft for that registry to collide with `"path"`/`"repository"` against. Nothing to resolve now;
   a one-line pointer on #11 when someone actually designs the registry is the right amount of
   anticipation, not a naming reservation made here.

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
  source" message and not silent success; the identical check for `defaultSource: "local"`/
  `.WithDefaultSource("local")` (F10); every existing `"local"`-source test updated to `"repository"`
  rather than duplicated; the "two services, one repository, ungrouped" warning
  (`ServiceSourcesConfigCache.cs:528`) still fires under `source: "repository"`, since F10 flags this
  as the one site a rename could silently stop reaching; and the dispatch-table/`SourceNames` sync
  test (F10) updated to the new five-name vocabulary rather than left asserting the old four.
- **Catalog loader:** `path:` required for the `"path"` source, confined (absolute/`..`/unusable
  segment rejected with the existing `CheckoutRelativePath` messages), and — per F8 — freely
  combinable with `repository`/`repositoryRef`/`url`/`container`/`kubernetes` on one entry, with a
  test asserting that combination resolves correctly under each `source` selection; `kind: path` in
  yaml rejected as a reserved name, matching `kind: repository`/`kind: url` today; a service with
  none of `repository`/`repositoryRef`/`path`/`url`/`container`/`kubernetes` still gets the "no source
  configured" error, now naming `path` among the alternatives.
- **Resolution:** missing directory at composition time produces the drafted message above (not a
  generic clone-failure message — `PathSource` never touches git); `dotnet`/`java`/`javascript` kinds
  all resolve against a `path` `repoRoot` unchanged from their existing checkout-based tests
  (parametrizing existing kind tests over the source rather than writing new ones).
- `ref`/`local.ref`-equivalent rejected on a `path` service, naming why.
- `prepare`: `oncePerCommit` rejected with the F4 message; `once`/`always` markers keyed on path
  reused correctly; two services sharing one resolved path serialize their `prepare` under one lock.
- `defaultSource: path` resolves with no `servicesources.local.json` entry at all.
- `UseDeferredCheckout()` present has no effect on a `path` service (asserted so the exemption can't
  silently regress).
- `local.path` still resolves exactly as before, plus emits the deprecation notice once per start.
