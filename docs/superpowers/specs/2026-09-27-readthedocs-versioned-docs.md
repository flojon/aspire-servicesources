# Read the Docs Versioned Documentation

## Objective

Set up the repository as a versioned documentation website using:

- MkDocs
- Material for MkDocs
- Read the Docs

Read the Docs manages builds, hosting, and documentation versions automatically from the
repository's branches and Git tags, using Read the Docs' standard version semantics:

```text
latest → main / development
stable → latest released version (Read the Docs sets this automatically once tags exist)
v0.7.0, v0.7.1, v0.8.0, ... → one Read the Docs version per pushed release tag
```

Do not introduce Mike or a GitHub Pages deployment.

**`v0.7.0` is the first tag published on Read the Docs.** `v0.1.0` through `v0.6.0` predate
`mkdocs.yml`/`.readthedocs.yaml` — those files don't exist in those tags' trees, so Read the
Docs cannot build docs for them. Do not backfill them as numbered versions; the automation
rule (see below) should only pick up `v0.7.0` and later.

## Desired version model

```text
latest
stable
v0.7.0
v0.7.1
v0.8.0
...
```

- **`latest`** represents the current `main` branch. Development documentation; may contain
  unreleased changes.
- **`stable`** represents the latest released version. Read the Docs sets this automatically
  once at least one tag is active — it points at the highest version by semver comparison, not
  necessarily the most recently tagged.
- **Numbered versions are named by their literal git tag** (`v0.7.0`, `v0.7.1`, …) — Read the
  Docs has no built-in mechanism to strip the `v` prefix or collapse patch releases into a
  minor-only version without Mike, which this project deliberately excludes.

## Repository structure

```text
.
├── README.md
├── docs/
│   ├── index.md
│   ├── authoring-in-code.md
│   ├── local-source.md
│   ├── kinds.md
│   ├── other-sources.md
│   ├── configuration.md
│   ├── backing-services.md
│   └── superpowers/          (internal design history — excluded from the site nav)
├── mkdocs.yml
├── requirements-docs.txt
└── .readthedocs.yaml
```

Do not create separate copies of the documentation for each release, and do not invent new
documentation pages beyond `docs/index.md` — the nav is built from the Markdown files that
already exist under `docs/`.

## MkDocs

- Add `mkdocs.yml` using the Material theme.
- Nav mirrors the existing README "Documentation map" order: Home, Authoring in code, Local
  source, Kinds, Other sources, Configuration, Backing services.

## Version selector

Read the Docs injects its own version flyout (the "addons" flyout menu) into every page it
hosts, regardless of static-site generator or theme — this is the mechanism that gives users
the `latest` / `stable` / `v0.7.0` / `v0.7.1` picker, and it needs no Mike and no extra MkDocs
plugin. Material for MkDocs' own built-in version dropdown requires a `mike`-produced
`versions.json`; since Mike is explicitly excluded, that dropdown is not used, and Read the
Docs' flyout is the version selector instead. `mkdocs.yml` documents this with a comment so a
future maintainer does not "fix" the missing dropdown by installing Mike.

## Read the Docs configuration

- `.readthedocs.yaml`, format version 2, `mkdocs.configuration: mkdocs.yml`.
- Use current supported build tooling (Ubuntu 24.04, Python 3.12) rather than pinning to
  whatever was current when this spec was written, if newer defaults exist at implementation
  time.

## Documentation dependencies

- `requirements-docs.txt` pinning `mkdocs` and `mkdocs-material` to specific compatible
  versions for reproducible builds. No `mike`.

## Git branches and tags / stable version

- Read the Docs' project-level **Automation Rules** (not representable in `.readthedocs.yaml`)
  drive activation: `main` → active, "Set as default version"; a rule matching semver tags
  (`v*`) → active, versioned by its literal tag name (Read the Docs does not strip the `v`
  prefix or collapse patches); `stable` is set automatically once at least one tag is active,
  pointing at the highest version by semver comparison.
  These are one-time maintainer setup steps in the Read the Docs project dashboard (or via the
  Read the Docs API), documented in `RELEASING.md` rather than committed as code, since the
  project doesn't exist on Read the Docs until imported.

## README handling

- Keep `README.md` as the GitHub landing page (badges, why, install, quickstart).
- Add `docs/index.md` as the documentation site's homepage, and point `README.md` at the
  hosted docs site instead of duplicating the full doc map inline.

## Local development

- `pip install -r requirements-docs.txt` then `mkdocs serve` previews the site locally.
- `mkdocs build --strict` must succeed — broken internal links or nav entries fail the build
  rather than being silently dropped.

## No GitHub Pages, no Mike

Read the Docs alone builds, hosts, versions, and serves `latest`/`stable`/numbered docs; no
`.github/workflows/docs.yml`, no `mike`, no Mike aliases.
