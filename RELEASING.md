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
| [nuget.org] | stable versions only | `release.yml`, on a published GitHub release |
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
all accept it. Core published; both satellites did not. The bound is now chosen per build and CI
packs the release shape too — but the general hazard remains, so prefer a fix that makes CI
exercise the release shape over one that only corrects the symptom.

**A partly-failed release cannot be re-run.** `release.yml` pushes all three packages in one
step, so once core is on nuget.org, re-running the workflow for that tag fails on core's 409
before it retries anything. And the version is spent — nuget.org will not accept it again even
after an unlist. The way out is a patch release carrying the fix, which is what `0.3.1` is.

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
