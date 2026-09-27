# Read the Docs Versioned Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Host this repo's `docs/` tree on Read the Docs, built with MkDocs + Material, with
Read the Docs' native `latest`/`stable`/numbered-tag versioning — no Mike, no GitHub Pages.

**Architecture:** Add `mkdocs.yml` (Material theme, nav over the existing `docs/*.md` files
plus a new `docs/index.md` homepage), `.readthedocs.yaml` (build config), and
`requirements-docs.txt` (pinned build deps). Versioning itself is Read the Docs project
configuration (Automation Rules), not a file in the repo — it's documented as a one-time
maintainer setup step in `RELEASING.md`. `README.md` gets a small pointer to the hosted docs
site instead of duplicating content into `docs/index.md`.

**Tech Stack:** MkDocs, Material for MkDocs, Read the Docs (`readthedocs.yaml` v2, Ubuntu
24.04, Python 3.12).

**Spec:** [docs/superpowers/specs/2026-09-27-readthedocs-versioned-docs.md](../specs/2026-09-27-readthedocs-versioned-docs.md)

## Global Constraints

- No Mike, no `mike` in `requirements-docs.txt`, no Mike aliases, no `versions.json`.
- No `.github/workflows/docs.yml` or any GitHub Pages deploy step — Read the Docs is the only
  host.
- Do not invent documentation pages beyond `docs/index.md`; the nav lists only files that
  already exist under `docs/` (excluding `docs/superpowers/`, which is internal design
  history, not user-facing reference).
- `latest` must track `main`; `stable` must come from a release tag, never from `main`.
- Repo tags are `vMAJOR.MINOR.PATCH` (MinVer, prefix `v` — see `RELEASING.md`). Read the Docs
  names a version after its literal git tag (`v0.7.0`, `v0.7.1`, `v0.8.0`, …, one version per
  pushed tag) — it has no built-in mechanism to strip the `v` prefix or collapse patches into a
  minor-only version without Mike, which this project deliberately excludes. **`v0.7.0` is the
  first tag Read the Docs builds** — `v0.1.0`…`v0.6.0` predate `mkdocs.yml`/`.readthedocs.yaml`,
  so those files don't exist in those tags' trees and Read the Docs cannot build them; do not
  backfill them as numbered versions.
- Python 3.12 + pip are now installed on this machine (`winget install Python.Python.3.12`,
  confirmed working: `pip 25.0.1`/upgraded to `26.2.1`). The install added the interpreter to
  the user's `PATH` in the registry, but a shell process started before the install (this one)
  doesn't pick that up until it restarts — until then, prefix commands needing Python with:
  `export PATH="/c/Users/flojon/AppData/Local/Programs/Python/Python312:/c/Users/flojon/AppData/Local/Programs/Python/Python312/Scripts:$PATH"`.
  A freshly started terminal needs no prefix. Each task's verification step below can now
  actually run `pip install`/`mkdocs build --strict`; the "manual fallback" wording that
  follows in each step is a belt-and-suspenders note in case a *different* execution
  environment lacks Python, not the expected path here.

---

### Task 1: Documentation site homepage (`docs/index.md`)

**Files:**
- Create: `docs/index.md`

**Interfaces:**
- Consumes: nothing.
- Produces: `docs/index.md`, referenced by `mkdocs.yml`'s `nav` in Task 3 as `Home: index.md`.

- [ ] **Step 1: Write `docs/index.md`**

```markdown
# Aspire.Hosting.ServiceSources

An Aspire AppHost extension that lets `builder.AddService("orders")` resolve to a real,
running resource whose *source* is chosen per developer, not baked into the AppHost.

## Why

`AddProject<T>()` assumes a service lives in the AppHost's own solution. In a real
microservice environment, services live in separate repositories, and different developers
want different things for the same service: clone it locally to edit, run it from an
already-checked-out working copy, reach an instance already running in a shared Kubernetes
dev cluster, hit a fixed URL, or just run a published container image. The AppHost should
only describe *what* it depends on; where that dependency actually comes from is a
per-developer choice, made without ever touching the AppHost's `.csproj`/`.sln`.

`AddService()` is the seam: the AppHost calls it once per service, and a developer-local
config file decides how it's actually resolved — a managed or self-managed local git
checkout (`"local"`), a `kubectl port-forward` against a dev cluster (`"kubernetes"`), a
fixed, already-known URL (`"url"`), a published container image run locally
(`"container"`), or nothing at all (`"disabled"`) — behind one stable return type, so the
AppHost code never has to change when a developer switches sources.

## Install

Published on nuget.org as [`KoalaSoft.Aspire.Hosting.ServiceSources`](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources).

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

Requires .NET 8 or later (net8.0, net9.0, and net10.0 are all supported) and an AppHost
project using the `Aspire.AppHost.Sdk`. A service that isn't a .NET project needs Aspire's
hosting package for its language too — see [Non-.NET local services](kinds.md).

## Getting started

**1. Declare the service in `Program.cs`:**

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(orders);

builder.Build().Run();
```

**2. Add the shared catalog, `servicesources.yaml`, next to the AppHost project (commit this
file):**

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

(A service that isn't a .NET project also takes a `kind` — see
[Non-.NET local services](kinds.md).)

**3. Add your own `servicesources.local.json` next to it (gitignore this file — it's
per-developer):**

```json
{
  "services": {
    "orders": { "source": "local" }
  }
}
```

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

## Documentation map

- [Authoring the catalog in code](authoring-in-code.md) — declare the same catalog in C# or
  TypeScript instead of yaml, via `AddServiceCatalog`.
- [The `"local"` source](local-source.md) — managed git checkouts, the `prepare` bootstrap
  step, why a checkout doesn't inherit your repository's build settings, and grouping several
  services under one repository.
- [Non-.NET local services (`kind`)](kinds.md) — the built-in `javascript` and `java` kinds,
  and how to implement your own.
- [Other sources: `kubernetes`, `url`, `container`, `disabled`](other-sources.md) — a
  `kubectl port-forward` against a dev cluster, a fixed URL, a published container image, or
  turning a service off, plus combining several sources on one catalog entry.
- [Configuring and consuming a resolved service](configuration.md) — overriding
  `servicesources.local.json` from higher configuration layers, `ServiceResource`'s native
  Aspire vocabulary, and naming a service's endpoint portably across sources.
- [Backing services: databases, brokers and caches](backing-services.md) — `AddBackingService()`,
  reaching one through a cluster tunnel, and connection-string placeholders.

## Status

Early stage, evolving fast. `"local"`, `"kubernetes"`, `"url"`, `"container"` and `"disabled"`
sources are all implemented. Changes are recorded in the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md); how a
release is cut is in
[`RELEASING.md`](https://github.com/flojon/aspire-servicesources/blob/main/RELEASING.md); the
trust model a resolved service runs under is in
[`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md).

You're viewing the **`{{ readthedocs.version }}`** build of these docs. Use the version
selector in the corner to switch between `latest` (tracks `main`, may contain unreleased
changes) and `stable`/numbered releases.
```

Note: `{{ readthedocs.version }}` is Read the Docs' own template variable, substituted at
build time — it renders correctly only when built by Read the Docs, and shows the literal
`{{ readthedocs.version }}` text under `mkdocs serve` locally. That's expected.

- [ ] **Step 2: Commit**

```bash
git add docs/index.md
git commit -m "docs: add MkDocs homepage"
```

---

### Task 2: Pinned documentation build dependencies (`requirements-docs.txt`)

**Files:**
- Create: `requirements-docs.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: `requirements-docs.txt`, consumed by `.readthedocs.yaml` (Task 4) and by the local
  `pip install -r requirements-docs.txt` workflow documented in Task 7.

- [ ] **Step 1: Write `requirements-docs.txt`**

```text
mkdocs==1.6.1
mkdocs-material==9.5.44
```

Pinned exact versions for reproducible builds, per the spec's "Documentation dependencies"
section. If `pip install` reports either version yanked or unavailable at execution time,
bump to the nearest later patch of the same minor and note the substitution in the commit
message — do not widen to an unpinned range.

- [ ] **Step 2: Verify the versions install**

Run:

```bash
pip install -r requirements-docs.txt
```

Expected: both packages install with no dependency conflicts. If Python/pip is unavailable in
this environment (see Global Constraints), skip this step and note it was skipped in the
commit message — Task 6's Read the Docs build is the real gate.

- [ ] **Step 3: Commit**

```bash
git add requirements-docs.txt
git commit -m "docs: pin MkDocs build dependencies"
```

---

### Task 3: MkDocs site configuration (`mkdocs.yml`)

**Files:**
- Create: `mkdocs.yml`

**Interfaces:**
- Consumes: `docs/index.md` (Task 1) and the existing `docs/authoring-in-code.md`,
  `docs/local-source.md`, `docs/kinds.md`, `docs/other-sources.md`, `docs/configuration.md`,
  `docs/backing-services.md`.
- Produces: `mkdocs.yml`, consumed by `.readthedocs.yaml`'s `mkdocs.configuration` key
  (Task 4).

- [ ] **Step 1: Write `mkdocs.yml`**

```yaml
site_name: Aspire.Hosting.ServiceSources
site_url: https://aspire-servicesources.readthedocs.io/
repo_url: https://github.com/flojon/aspire-servicesources
repo_name: flojon/aspire-servicesources
edit_uri: edit/main/docs/

theme:
  name: material
  features:
    - navigation.tabs
    - navigation.sections
    - navigation.expand
    - content.code.copy

# No `extra.version` / mike provider here: Material's built-in version dropdown needs a
# mike-produced versions.json, and this project deliberately doesn't use mike. Read the Docs
# injects its own version flyout (latest/stable/numbered tags) into every hosted page
# regardless of theme — that flyout is the version selector.

markdown_extensions:
  - admonition
  - pymdownx.highlight
  - pymdownx.superfences
  - tables

nav:
  - Home: index.md
  - Authoring in code: authoring-in-code.md
  - The "local" source: local-source.md
  - Non-.NET services (kind): kinds.md
  - Other sources: other-sources.md
  - Configuration: configuration.md
  - Backing services: backing-services.md
```

The nav order matches the README's existing "Documentation map" list.

- [ ] **Step 2: Verify the config builds**

Run:

```bash
mkdocs build --strict
```

Expected: `INFO - Documentation built in ...` with no warnings (a `--strict` build fails on
broken internal links or nav entries pointing at missing files). If Python/mkdocs is
unavailable locally, instead manually verify every `nav` path above resolves to a file that
exists under `docs/` (`ls docs/index.md docs/authoring-in-code.md docs/local-source.md
docs/kinds.md docs/other-sources.md docs/configuration.md docs/backing-services.md`) and that
the YAML parses (`python3 -c "import yaml; yaml.safe_load(open('mkdocs.yml'))"` if available,
otherwise a careful manual read) — then rely on Task 6's Read the Docs build to confirm.

- [ ] **Step 3: Commit**

```bash
git add mkdocs.yml
git commit -m "docs: add MkDocs site configuration"
```

---

### Task 4: Read the Docs build configuration (`.readthedocs.yaml`)

**Files:**
- Create: `.readthedocs.yaml`

**Interfaces:**
- Consumes: `mkdocs.yml` (Task 3), `requirements-docs.txt` (Task 2).
- Produces: `.readthedocs.yaml` at repo root, which is what Read the Docs' GitHub integration
  looks for on every build (Task 6).

- [ ] **Step 1: Write `.readthedocs.yaml`**

```yaml
version: 2

build:
  os: ubuntu-24.04
  tools:
    python: "3.12"

python:
  install:
    - requirements: requirements-docs.txt

mkdocs:
  configuration: mkdocs.yml
```

If Read the Docs supports a newer `ubuntu-*` image or Python version by the time this is
implemented, prefer that over `ubuntu-24.04`/`3.12` — those are the current supported values
as of this plan, not a hard requirement to match exactly.

- [ ] **Step 2: Validate the YAML**

Run:

```bash
python3 -c "import yaml, sys; yaml.safe_load(open('.readthedocs.yaml'))" && echo OK
```

If Python is unavailable, manually re-read the file for indentation/YAML validity — it has no
tabs and no duplicate keys.

- [ ] **Step 3: Commit**

```bash
git add .readthedocs.yaml
git commit -m "docs: add Read the Docs build configuration"
```

---

### Task 5: README pointer to the hosted docs site

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: the Read the Docs project URL (`https://aspire-servicesources.readthedocs.io/`,
  matching `site_url` in Task 3).
- Produces: nothing consumed by later tasks — this is the last content change.

`README.md` is also touched by PR #406 (in flight). To minimize conflict surface, this task
makes exactly one small addition near the top and does not touch the "Documentation map"
section's existing links (those keep working as GitHub blob links regardless of Read the
Docs).

- [ ] **Step 1: Add a docs badge/link line**

Insert, immediately after the existing badge line (after `[License: MIT]...` and before the
`An Aspire AppHost extension...` paragraph):

```markdown
[![Docs](https://img.shields.io/readthedocs/aspire-servicesources)](https://aspire-servicesources.readthedocs.io/)
```

- [ ] **Step 2: Add one sentence to the "Documentation map" section's intro**

Change:

```markdown
## Documentation map

The rest of the reference lives alongside this file, split by topic:
```

to:

```markdown
## Documentation map

Full versioned docs: <https://aspire-servicesources.readthedocs.io/>. The same source also
lives alongside this file, split by topic:
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: link README to the hosted Read the Docs site"
```

---

### Task 6: Maintainer setup — Read the Docs project + versioning rules

**Files:**
- Modify: `RELEASING.md`

**Interfaces:**
- Consumes: the tag convention already documented in `RELEASING.md` (`vX.Y.Z`, MinVer,
  prefix `v`).
- Produces: a documented, repeatable manual setup procedure — nothing later in this plan
  depends on it, but it's what makes `stable`/numbered versions real instead of just
  configured-on-paper.

This is genuinely one-time manual configuration in the Read the Docs web UI (or its API) —
none of it is representable in `.readthedocs.yaml`. Document it so it isn't reconstructed from
memory later, matching how this file already treats the release process.

- [ ] **Step 1: Add a "Documentation site" section to `RELEASING.md`**

Insert a new section, after the existing "## Gotchas" section and before the "## Prereleases"
section:

```markdown
## Documentation site

Docs are built by [Read the Docs] from `mkdocs.yml`/`.readthedocs.yaml`, not by any GitHub
Actions workflow. One-time setup, done once in the Read the Docs dashboard after importing
this repository from GitHub:

1. **Import the project** at https://app.readthedocs.org/dashboard/, pointing at
   `flojon/aspire-servicesources`.
2. **Default version:** Admin → Versions → set `main` as the default version, activated. This
   is what `latest` resolves to.
3. **Automation rule for tags:** Admin → Automation Rules → add a rule matching version type
   "Tag", pattern `^v0\.(7|[89]|[1-9]\d+)\.` (regex — matches `v0.7.x` and later, never
   `v0.1.x`–`v0.6.x`), action "Activate version", version scheme "Semantic Versioning". `v0.7.0`
   is the first tag with `mkdocs.yml`/`.readthedocs.yaml` in its tree, so it's the first one
   that can build at all — the pattern just makes that explicit instead of relying on the
   older tags' builds failing silently. Widen the regex once a `v1.x` release ships.
4. **Automation rule for stable:** a second rule, "Set version as STABLE", triggered on new
   tags matching the same pattern — this keeps `stable` pointing at whichever tag is the
   newest release, without a `stable` branch to maintain by hand.
5. Confirm unwanted branches (feature branches, `claude/*` worktree branches) and the
   pre-`v0.7.0` tags are **not** separately activated — only `main` and `v0.7.0`+ tags should
   build.

After this, every `git push origin vX.Y.Z` (the existing [release step](#4-tag-and-release))
picks up a new numbered version and moves `stable` automatically; nothing in the release
process above needs to change.

[Read the Docs]: https://readthedocs.org/
```

- [ ] **Step 2: Commit**

```bash
git add RELEASING.md
git commit -m "docs: document Read the Docs project setup and versioning rules"
```

---

### Task 7: Local docs workflow documentation

**Files:**
- Modify: `RELEASING.md` (append to the "Documentation site" section added in Task 6)

**Interfaces:**
- Consumes: `requirements-docs.txt` (Task 2).
- Produces: nothing consumed elsewhere — this is the last task.

- [ ] **Step 1: Append a "Local preview" subsection**

Immediately after the numbered list added in Task 6, before the `[Read the Docs]:` link
definition, add:

```markdown
### Local preview

```bash
pip install -r requirements-docs.txt
mkdocs serve
```

Serves the site at <http://127.0.0.1:8000/> with live reload. `mkdocs build --strict` is the
same check Read the Docs runs — a broken internal link or a nav entry pointing at a missing
file fails the build instead of shipping a 404.
```

- [ ] **Step 2: Full local verification pass**

Run, from repo root:

```bash
pip install -r requirements-docs.txt
mkdocs build --strict
```

Expected: clean build, `site/` directory produced (already covered by the repo's existing
`.gitignore` pattern only if `site/` is added — see Step 3).

If Python is unavailable in this environment, this step cannot run locally; say so explicitly
rather than claiming it passed, and treat Read the Docs' first build after pushing (out of
scope for this plan, but the natural next action) as the real verification.

- [ ] **Step 3: Ignore MkDocs' build output**

Add to `.gitignore`:

```text
site/
```

- [ ] **Step 4: Commit**

```bash
git add RELEASING.md .gitignore
git commit -m "docs: document local MkDocs preview workflow"
```

---

## Self-Review Notes

- **Spec coverage:** MkDocs+Material config (Task 3), version model via RTD automation rules
  documented not faked with Mike (Task 6), `.readthedocs.yaml` (Task 4), pinned deps with no
  mike (Task 2), README kept as landing page + `docs/index.md` added (Tasks 1 & 5), local dev
  workflow (Task 7), no GitHub Pages workflow created (never introduced in any task), no
  invented pages (nav only references existing files + the one new `index.md`).
- **Python/pip:** installed during planning (Python 3.12 via winget) and confirmed working;
  `pip install`/`mkdocs build --strict` can run for real in this environment. Each task still
  documents a manual fallback in case a different execution environment lacks Python.
