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
- **All three packages release in lockstep**, one version from one tag. A satellite pins core
  to its own minor, so they have to move together.

Published packages:

| Package | Contents |
| --- | --- |
| `KoalaSoft.Aspire.Hosting.ServiceSources` | core |
| `KoalaSoft.Aspire.Hosting.ServiceSources.Java` | `kind: java` satellite |
| `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` | `kind: javascript` satellite |

Two feeds receive them:

| Feed | What lands there | Published by |
| --- | --- | --- |
| [nuget.org] | stable versions, and named prereleases cut on demand | `release.yml`, on a published GitHub release |
| GitHub Packages | a prerelease per commit to `main` | `preview.yml`, on push |

## Steps

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

`release.yml` tests, packs all three packages, obtains a nuget.org API key over OIDC trusted
publishing, and pushes. It runs `dotnet nuget push` **without** `--skip-duplicate`, deliberately:
every release tag is a new version, so a 409 is a real failure and must not be swallowed.

Then `prune-previews` deletes superseded prereleases from GitHub Packages, keeping the five most
recent, one job per package.

Finally, confirm the packages are actually on the feed — a push can be accepted and still fail
nuget.org's asynchronous validation, and indexing lags the push by a few minutes either way:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/koalasoft.aspire.hosting.servicesources/index.json
curl -s https://api.nuget.org/v3-flatcontainer/koalasoft.aspire.hosting.servicesources.java/index.json
curl -s https://api.nuget.org/v3-flatcontainer/koalasoft.aspire.hosting.servicesources.javascript/index.json
```

## Gotchas

**A release build is not the shape CI has been testing.** Every build off a tag is a prerelease,
so until the tag exists, nothing has packed a stable version. In `0.3.0` this shipped a broken
release: `PinCoreDependency` closed the satellites' core range with a prerelease upper bound,
`[0.3.0, 0.4.0-0)`, which nuget.org rejects at push time with `The package manifest contains an
invalid Version` ([NuGetGallery#6948]) while `pack`, `restore`, the client and GitHub Packages
all accept it. Core published; both satellites did not. A prerelease build now pins core exactly
(`[0.4.0-rc.1]`), which has no upper bound to express and so carries no `-0` for the gallery to
reject, and CI packs the release shape too — but the general hazard remains, so prefer a fix that
makes CI exercise the release shape over one that only corrects the symptom.

**A partly-failed release cannot be re-run.** `release.yml` pushes all three packages in one
step, so once core is on nuget.org, re-running the workflow for that tag fails on core's 409
before it retries anything. And the version is spent — nuget.org will not accept it again even
after an unlist. The way out is a patch release carrying the fix, which is what `0.3.1` is.

**Do not delete or move a tag that has published anything.** The packages it produced are
permanent; the tag is the only record of what commit they were built from.

## Prereleases

Two kinds, and they are for different audiences.

**The automatic stream.** Every commit to `main` publishes a prerelease to GitHub Packages,
versioned `X.Y.Z-alpha.0.N`, with release notes taken from the `[Unreleased]` changelog section.
Nothing to do by hand. Installing from that feed needs a classic token with `read:packages` — the
README's Preview builds section has the details — which makes it a poor thing to hand to someone
you are asking for feedback.

**A named prerelease on nuget.org.** For a build you want people to actually reach — an rc before
a release, or a preview of unmerged work you want tried — cut it the same way as a release, with
a prerelease tag:

```bash
git tag v0.4.0-rc.1
git push origin v0.4.0-rc.1
gh release create v0.4.0-rc.1 --title "v0.4.0-rc.1" --notes-file <notes> --verify-tag --prerelease
```

MinVer takes the tag verbatim, so the packages are `0.4.0-rc.1`. A pre-release GitHub release
fires the same `published` event, so `release.yml` runs unchanged and pushes to nuget.org over the
existing trusted-publishing policy — the policy is keyed on the workflow file, not the branch or
tag, so nothing needs registering. `prune-previews` is skipped for a prerelease: the previews
behind it are still the newest builds of unreleased work. Consumers install it with
`--prerelease` and no token at all.

Two things worth knowing before you cut one:

- **The tag does not have to be on `main`.** Tagging a merge of `main` and an open PR branch is a
  legitimate way to get that PR in front of people. The tag is then the only record of what was
  built, which is reason enough not to delete it.
- **A prerelease version is as permanent as a stable one.** nuget.org has no deletion, only
  unlisting, and the version can never be reused. Prereleases are cheap in that they do not
  consume the stable version — `0.4.0-rc.1` does not block `0.4.0` — but each one is a public
  version for good. That is why the per-commit `alpha` stream stays on GitHub Packages, where
  `prune-previews` can bound it.

[MinVer]: https://github.com/adamralph/minver
[nuget.org]: https://www.nuget.org/profiles/flojon
[NuGetGallery#6948]: https://github.com/NuGet/NuGetGallery/issues/6948
