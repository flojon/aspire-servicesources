# Releasing

Cutting a release is: close the changelog, tag, publish a GitHub release. Everything after the
tag is automation. This file exists so none of it has to be reconstructed from the workflow
files again.

## How versioning works

There is no version number anywhere in the repository. [MinVer] computes it from the git tag
at build time, with `MinVerTagPrefix` set to `v` in `Directory.Build.props`, so the tag `v0.3.1`
is what makes the packages `0.3.1`. Consequences worth knowing before you start:

- **The tag is the version.** Nothing to bump in a `.csproj`, and nothing to keep in step.
- **Off a tag, every build is a prerelease** — `0.3.1-alpha.0.4` and so on. That is what the
  preview feed publishes, and it is why some problems only appear at release time (see
  [Gotchas](#gotchas)).
- **One package is published**, one version from one tag.
- **The nearest *reachable* tag wins**, not the newest tag in the repository. A branch off
  `v0.4.0` versions as `0.4.1-alpha.0.N` while `main` is deep into the next minor, and a
  `v0.4.1` tag on that branch packs exactly `0.4.1`. That is the whole mechanism behind
  [Patch releases](#patch-releases), and `MinVerTagPrefix` is repo-wide, so a release branch
  needs no configuration of its own.

Published package:

| Package | Contents |
| --- | --- |
| `KoalaSoft.Aspire.Hosting.ServiceSources` | everything, including the `javascript` and `java` kinds |

The `javascript` and `java` kinds compile against Aspire's hosting packages for those
languages, referenced with `PrivateAssets="all"` so they reach no consumer's nuspec. Those are
not released from here and have their own versions; the minimum each kind needs is enforced by
`src/Aspire.Hosting.ServiceSources/buildTransitive/KoalaSoft.Aspire.Hosting.ServiceSources.targets` and
restated in `GuestLanguagePackages`, which a test keeps in agreement.

Two feeds receive them:

| Feed | What lands there | Published by |
| --- | --- | --- |
| [nuget.org] | stable versions only | `release.yml`, on a published GitHub release |
| GitHub Packages | a prerelease per commit to `main` | `ci.yml`'s `build` job, on push (`main` only — it also runs on push to a release branch, but publishes nothing there) |

## Steps

These cut a release from the tip of `main`: every release so far, and the right path for a
minor. Patching an older minor once `main` has moved past it replaces steps 1 and 3 — see
[Patch releases](#patch-releases).

### 1. Close the changelog

`CHANGELOG.md` is the source of release notes — `Directory.Build.targets` reads the section
matching the version being packed into `PackageReleaseNotes`, so what is written here is what
appears on the nuget.org listing. Open a PR that:

- renames `## [Unreleased]` to `## [X.Y.Z] - <date>` and adds a fresh empty `## [Unreleased]`
  above it,
- adds the `[X.Y.Z]:` compare link and repoints `[Unreleased]:` at the new version,
- adds link definitions for any `[#N]` references used in the new section.

While the version is below `1.0.0`, a breaking change may ship in a minor, so each one needs a
**Breaking** entry saying what breaks and how to migrate. Behavioral changes that still compile
go under **Changed** — those are the ones nothing warns a consumer about.

### 2. Verify before tagging

The tag is the point of no return: a version pushed to nuget.org is immutable and can never be
reused, only unlisted. Run these first, from a clean checkout of the commit you intend to tag:

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release
```

CI covers this on the PR, including a pack of the release shape. Trust it if it is green on the
merge commit — checking `gh run list --branch main --limit 5` is enough.

### 3. Tag and release

```bash
git checkout main && git pull --ff-only
git tag vX.Y.Z
git push origin vX.Y.Z
gh release create vX.Y.Z --title "vX.Y.Z" --notes-file <notes> --verify-tag
```

For the notes, use the changelog section for the version, plus the `[#N]:` link definitions so
the references resolve, plus a `**Full Changelog**` compare link. Publishing the release is what
triggers `release.yml`.

### 4. Watch the publish

```bash
gh run watch $(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId') --exit-status
```

`release.yml` tests, packs, obtains a nuget.org API key over OIDC trusted
publishing, and pushes. It runs `dotnet nuget push` **without** `--skip-duplicate`, deliberately:
every release tag is a new version, so a 409 is a real failure and must not be swallowed.

Then `prune-previews` deletes superseded prereleases from GitHub Packages, keeping the five most
recent.

Finally, confirm the packages are actually on the feed — a push can be accepted and still fail
nuget.org's asynchronous validation, and indexing lags the push by a few minutes either way:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/koalasoft.aspire.hosting.servicesources/index.json
```

## Patch releases

[Steps](#steps) tags `main`. That is right for a minor — the release is the tip of `main`, and
everything on `main` is going out — and it is how every release here has been cut. It is wrong
for a patch. By the time a patch is wanted, `main` carries the next minor's features, and
tagging it `X.Y.Z+1` publishes all of them under a version number that promises none of them.
Nothing warns the consumer either: a patch bump is the one upgrade taken without reading
anything first.

So a patch is a **release branch off the tag being patched**, with the fixes cherry-picked onto
it.

### The branch

`release/X.Y.x` — one per minor, `release/0.4.x` for anything patching `0.4.0`. Cut from the
tag, not from `main`:

```bash
git fetch origin --tags
git switch -c release/0.4.x v0.4.0
git push -u origin release/0.4.x
```

**Keep it after the tag.** A second patch is then a cherry-pick onto a branch that already
exists rather than an archaeology exercise, and the branch is the record of which fixes were
judged worth backporting. A branch costs nothing to keep and deleting it buys nothing.

What the automation does with such a branch — verified rather than inferred, since the failure
mode would be discovered at tag time — the point of no return [step 2](#2-verify-before-tagging)
names:

| Workflow | On a release branch |
| --- | --- |
| `ci.yml` | **Runs twice.** On a PR into it, where two of its checks are **required** to merge (see [Protection](#protection)) — `on: pull_request` is deliberately unfiltered by base branch (its header says why), so a backport PR gets the whole build, test, pack and smoke-test set. And again on the merge commit itself, via its `push` trigger, which covers `release/**` as well as `main`: a backport gets the same post-merge run `main` gets, since the squashed commit is not the one the PR's checks ran against, and on a branch heading for a tag that is the commit that matters. Only the `build` job's publish steps are gated to `push` on `main`: a release branch off `v0.4.0` versions as `0.4.1-alpha.0.N`, the same shape `main` carries until its next tag, and the feed would order the two by version alone. So no preview of a backport is published, which is why [step 3](#3-dry-run-the-release-shape) still exists. |
| `release.yml` | **Publishes normally.** It checks out `github.event.release.tag_name` rather than a branch, so it builds whatever the tag points at, wherever that commit lives. Releasing from a branch needs no workflow change. |
| `prune-previews` | Runs, as on any release. The branch contributes no previews of its own, so there is nothing to configure — but note that publishing a patch does prune `main`'s previews for the minor still in development down to the five most recent. |
| `aspire-matrix.yml`, `net11-preview.yml` | Their scheduled runs only ever fire on the default branch, as GitHub's cron does. The `pull_request` triggers still apply to a backport PR touching the paths they filter on. |

### Protection

A release branch is protected on the same terms as `main`, by a second ruleset — *release
branch protection* — matching `refs/heads/release/**`. It requires a pull request, requires the
two checks `main` requires (`🔨 build, test & pack` and `🐳 container source smoke test`), and
refuses a force push or a deletion. It has no bypass actors, so it binds the maintainer too: a
backport goes in through a PR, which is why [step 1](#1-fix-on-main-first) opens one rather
than pushing the cherry-pick.

Requiring those two checks on a branch cut from an old tag is only safe because both survive
the trip. Both contexts exist under the same names in `ci.yml` at `v0.4.0`, that file's
`pull_request` trigger is unfiltered there too, and neither job is skippable by an `if:` or a
path filter — so they report on a backport PR instead of sitting forever as a required check
that never arrives, which is the failure `ci.yml`'s own header warns about.

One parameter is load-bearing, and worth knowing if the ruleset is ever rebuilt from scratch:
`required_status_checks` carries **`do_not_enforce_on_create: true`**. Without it the ruleset
rejects the push that *creates* the release branch, since that push is asked to satisfy checks
which by definition have never run for the ref:

```
remote: - 2 of 2 required status checks are expected.
 ! [remote rejected] v0.4.0 -> release/0.4.x (push declined due to repository rule violations)
```

`main`'s ruleset leaves the same parameter `false` and never notices, because `main` is not a
branch anyone creates.

Blocking deletion is deliberate, and it is what makes "keep it" above more than advice. A tag
keeps its own commit reachable, but backports sitting on the branch above the last tag are
reachable from nothing else, so deleting the branch would orphan them. Retiring a release line
is a deliberate edit to the ruleset rather than one `git push --delete`.

### 1. Fix on `main` first

Land the fix on `main` through the ordinary PR, then cherry-pick it onto the release branch.
`main` stays the single source of truth, the cherry-pick is the throwaway direction, and there
is no way to end up with a fix that ships in `0.4.1` and regresses in `0.5.0`. Fixing on the
branch first inverts all three.

```bash
git fetch origin
git switch -c backport/0.4.1-<issue> origin/release/0.4.x
git cherry-pick -x <sha-on-main>
git push -u origin backport/0.4.1-<issue>
gh pr create --base release/0.4.x
```

The PR is not a formality. The ruleset refuses a direct push to the release branch with
`Changes must be made through a pull request`, and the PR is what runs CI on the backported
commit in the shape it will actually ship in — the cherry-pick can conflict, or compile against
`main` and not against `0.4.x`. Squash is the only merge method allowed, as on `main`.

`-x` records the source commit in the cherry-pick's message, which is what later answers
whether a given `main` commit was backported.

Take only the fix. A patch carrying a refactor "while we are here" is a minor wearing a patch's
version number.

### 2. Close the changelog on the branch

The branch needs a `## [0.4.1] - <date>` section holding **only** the backported entries, plus
its own `[0.4.1]: …/compare/v0.4.0...v0.4.1` link definition. `Directory.Build.targets` reads
the section matching the version being packed, so what sits under that heading here is what
appears on the nuget.org listing — and `main`'s `[Unreleased]`, already deep into the next
minor, must not be it.

Do not try to reconcile the two files. On the branch `[Unreleased]` is empty and stays empty:
the branch is not where development happens. `main`'s copy is brought up to date after the
release, in [step 5](#5-record-the-release-on-main).

### 3. Dry-run the release shape

`ci.yml`'s `push`-triggered run has built, tested and packed every merge into the branch, but
only in the prerelease shape — that is the situation [Gotchas](#gotchas) opens with, and no amount of
automated packing off an untagged commit escapes it. Pack the stable shape by hand first:

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release
dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj \
  -c Release -o ./artifacts -p:MinVerVersionOverride=0.4.1
```

The override is what makes this worth running. Without it MinVer computes `0.4.1-alpha.0.N`,
and every prerelease-only code path is the one under test. With it, the pack is the one the tag
will produce — and it is the only pre-tag check that catches a missing `## [0.4.1]` section,
because that guard warns for a stable version only and stays silent on the branch no matter how
many times it is built.

Expect the nuspec's `<repository branch=...>` to read `refs/heads/release/0.4.x` rather than
`main`. That is correct — it records where the tagged commit lives — and is the one visible
difference between a package built here and one built from `main`.

### 4. Tag and release

```bash
git tag v0.4.1
git push origin v0.4.1
gh release create v0.4.1 --title "v0.4.1" --notes-file <notes> --verify-tag --latest=false
```

Notes as in [step 3](#3-tag-and-release) of the ordinary path. `--latest=false` belongs here
**whenever the branch is behind the newest released minor** — patching `0.4.x` once `0.5.0`
has shipped must not walk the repository's "Latest release" backwards. `gh` decides that
automatically from date and version by default, so state it rather than trusting the heuristic.
For the first patch of the newest minor, where the patch genuinely is the latest, leave the flag
off.

Then [watch the publish](#4-watch-the-publish); that part is unchanged.

### 5. Record the release on `main`

The patch is out, but `main`'s changelog does not know it happened. Open a PR to `main` that
moves the backported entries out of `[Unreleased]` into a `## [0.4.1] - <date>` section of their
own, adds the `[0.4.1]:` compare link, and repoints `[Unreleased]:` at `v0.4.1...HEAD`.

Both halves matter. `main`'s file is the project's whole history, and `ChangelogUrl` points
every package's release notes at `main`'s copy, so 0.4.1 has to appear there. And entries left
under `[Unreleased]` after they have already shipped get reported a second time in the next
minor's notes.

## Gotchas

**A release build is not the shape CI has been testing.** Every build off a tag is a prerelease,
so until the tag exists, nothing has packed a stable version. In `0.3.0` this shipped a broken
release: the satellites' core dependency was rewritten into a range with a prerelease upper
bound, `[0.3.0, 0.4.0-0)`, which nuget.org rejects at push time with `The package manifest
contains an invalid Version` ([NuGetGallery#6948]) while `pack`, `restore`, the client and
GitHub Packages all accept it. Core published; both satellites did not. Those packages and that
range are gone, but the general hazard is not — any dependency can acquire a prerelease bound —
so CI packs the release shape and scans it, and prefer a fix that makes CI exercise the release
shape over one that only corrects the symptom.

**A spent version cannot be reused.** Once a version is on nuget.org, re-running the workflow
for that tag fails on its 409, and nuget.org will not accept that version again even after an
unlist. The way out is a patch release carrying the fix, which is what `0.3.1` was — cut it as
[Patch releases](#patch-releases) describes, not by tagging `main`.

**Do not delete or move a tag that has published anything.** The packages it produced are
permanent; the tag is the only record of what commit they were built from.

## Prereleases

There is no manual prerelease step. Every commit to `main` publishes one to GitHub Packages
automatically, versioned `X.Y.Z-alpha.0.N`, with release notes taken from the `[Unreleased]`
changelog section. Installing from that feed needs a token with `read:packages` — the README's
Preview builds section has the details.

[MinVer]: https://github.com/adamralph/minver
[nuget.org]: https://www.nuget.org/profiles/flojon
[NuGetGallery#6948]: https://github.com/NuGet/NuGetGallery/issues/6948
