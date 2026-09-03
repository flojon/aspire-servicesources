# Changelog fragments

An entry destined for `CHANGELOG.md` is written here, in a file of its own, and folded into the
changelog when the release is cut. The point is that two PRs never touch the same lines: before
this directory existed, every PR adding an entry inserted at the same place in the same region of
one file, so two of them conflicted on the way in even when the entries had nothing to do with
each other ([#145]).

## Naming

```
changelog.d/<number>-<section>.md
```

`<number>` is the issue the change closes, or the PR itself when there is no issue. `<section>` is
one of the headings the changelog uses, lowercased:

| Section | For |
| --- | --- |
| `breaking` | Something that no longer compiles, or no longer works the way it did, with how to migrate. Below `1.0.0` these ship in minor releases, so each one has to say what breaks. |
| `added` | New API or new capability. |
| `changed` | Behaviour that differs while still compiling — the ones nothing warns a consumer about. |
| `fixed` | A bug that is no longer there. |
| `documentation` | README, this repo's docs, contributor tooling. |

One PR may add several fragments when it does several kinds of thing — `161-breaking.md` and
`161-added.md` alongside each other is the normal shape for a change that adds an API and
retires the old one. A PR that changes nothing a consumer can observe needs no fragment; CI
accepts that only for a PR that leaves `src/` alone, or one carrying the `no-changelog` label.

## Contents

The bullet, exactly as it should read in the changelog, with its links written **inline**:

```markdown
- **`servicesources.local.json` can be overridden without editing it** ([#69](https://github.com/flojon/aspire-servicesources/issues/69)). The per-developer
  source selection is now read through the AppHost's own `IConfiguration` rather than by a loader
  of ours, so a single run can pick a different source without editing the file.
```

Inline rather than the `[#69]` reference style the changelog itself uses, because a reference
needs a definition in one shared, ordered block at the bottom of `CHANGELOG.md` — the second
place concurrent PRs collided. `scripts/collect-changelog.sh` converts these to reference style
and writes the definitions when it folds the fragments in, so the changelog keeps the style it
has and only the release PR ever touches that block. A cross-repository reference works the same
way — write `[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507)` inline
and the fold moves the definition to the block below the numbered one.

**Wrap the paragraph as if the link were already `([#69])`** — the inline URL does not count
towards the line width, so the line carrying it runs long here and the rest wrap to the ~98
columns `CHANGELOG.md` uses. The fold does not reflow anything, so a fragment wrapped to 98
*including* the URL leaves a stub line behind once the link shrinks to its reference form. The
fragment is read once, in review; the changelog is read for years.

Write the entry for someone deciding whether to upgrade: what changed, what it means for them,
and for anything breaking or behavioural, what to do about it. Entries are hand-written on
purpose — nothing here is generated from commit messages or PR titles.

## What happens at release

`scripts/collect-changelog.sh X.Y.Z` groups the fragments by section, folds them under a new
`## [X.Y.Z]` heading in `CHANGELOG.md` together with anything already sitting under
`## [Unreleased]`, writes the compare and reference links, and deletes the fragments. That runs
in the release PR rather than in the release workflow, so the text gets read before it reaches a
nuget.org listing — `Directory.Build.targets` packs the section into `PackageReleaseNotes`.
`RELEASING.md` has the surrounding steps.

Nothing is lost between releases: a preview build packs the fragments sitting here as its release
notes, the same way it used to pack the `## [Unreleased]` section.

[#145]: https://github.com/flojon/aspire-servicesources/issues/145
