- **Changelog entries are written as per-PR fragments** ([#145](https://github.com/flojon/aspire-servicesources/issues/145)). A change now records its entry
  in a file of its own under `changelog.d/`, and `scripts/collect-changelog.sh` folds them into
  this file when a release is cut. Every PR used to insert at the same point in the same region of
  `CHANGELOG.md` and append to one shared link block at the bottom, so two unrelated entries
  conflicted on the way in; the resolution was always "keep both". Nothing changes for a reader of
  the changelog — the entries are still hand-written, and still end up here.
