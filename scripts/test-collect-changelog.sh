#!/usr/bin/env bash
#
# Tests for collect-changelog.sh.
#
# The script it covers runs about four times a year, edits CHANGELOG.md in place, and produces
# the text that goes on a nuget.org listing which cannot be edited afterwards. Left untested it
# would be exercised for the first time in the middle of a release, which is the worst moment to
# find out it mangles the link block.
#
# The last test asserts the property the whole fragment scheme exists for: two branches each
# adding a fragment merge without a conflict, where two branches each adding a changelog entry
# the old way do not.
#
#   scripts/test-collect-changelog.sh

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
collect=$script_dir/collect-changelog.sh

passed=0
failed=0

ok() { printf '  \033[32mok\033[0m   %s\n' "$1"; passed=$((passed + 1)); }
no() {
  printf '  \033[31mFAIL\033[0m %s\n' "$1"
  shift
  while (($#)); do printf '       %s\n' "$1"; shift; done
  failed=$((failed + 1))
}

assert_contains() {
  local desc=$1 haystack=$2 needle=$3
  if [[ $haystack == *"$needle"* ]]; then ok "$desc"; else no "$desc" "expected to find: $needle"; fi
}

assert_lacks() {
  local desc=$1 haystack=$2 needle=$3
  if [[ $haystack != *"$needle"* ]]; then ok "$desc"; else no "$desc" "expected not to find: $needle"; fi
}

assert_eq() {
  local desc=$1 expected=$2 actual=$3
  if [[ $expected == "$actual" ]]; then
    ok "$desc"
  else
    no "$desc" "expected: $(printf '%q' "$expected")" "actual:   $(printf '%q' "$actual")"
  fi
}

assert_fails() {
  local desc=$1
  shift
  if "$@" >/dev/null 2>&1; then no "$desc" "expected a non-zero exit"; else ok "$desc"; fi
}

# ---------------------------------------------------------------------------- fixture

workdir=$(mktemp -d)
trap 'rm -rf "$workdir"' EXIT

# A changelog with the three things the script has to preserve: an entry already written under
# [Unreleased], a released section below it, and a link block holding all three kinds of
# definition (compare links, numbered references, and a cross-repository reference).
write_fixture() {
  local dir=$1
  mkdir -p "$dir/changelog.d"

  # The real directory holds one too, and it is what keeps the directory in the tree once the
  # fragments are folded away - which the concurrent-branch test below depends on.
  echo "# Changelog fragments" > "$dir/changelog.d/README.md"

  cat > "$dir/CHANGELOG.md" <<'EOF'
# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Fixed

- **Written by hand before the fragments existed** ([#10]). Two paragraphs' worth of prose,
  wrapped the way the rest of the file is.

## [0.1.0] - 2026-01-01

### Added

- The first release.

[Unreleased]: https://github.com/flojon/aspire-servicesources/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/flojon/aspire-servicesources/releases/tag/v0.1.0

[#10]: https://github.com/flojon/aspire-servicesources/issues/10

[NuGetGallery#6948]: https://github.com/NuGet/NuGetGallery/issues/6948
EOF

  cat > "$dir/changelog.d/20-breaking.md" <<'EOF'
- **Twenty breaks something** ([#20](https://github.com/flojon/aspire-servicesources/issues/20)).
  How to migrate.
EOF

  cat > "$dir/changelog.d/7-added.md" <<'EOF'
- **Seven adds something** ([#7](https://github.com/flojon/aspire-servicesources/issues/7)).
EOF

  cat > "$dir/changelog.d/12-fixed.md" <<'EOF'
- **Twelve is fixed** ([#12](https://github.com/flojon/aspire-servicesources/pull/12)).
EOF

  cat > "$dir/changelog.d/3-fixed.md" <<'EOF'
- **Three is fixed** ([#3](https://github.com/flojon/aspire-servicesources/issues/3)).
EOF
}

run() {
  local dir=$1
  shift
  CHANGELOG_FILE=$dir/CHANGELOG.md FRAGMENT_DIR=$dir/changelog.d "$collect" "$@"
}

# ---------------------------------------------------------------------------- render

printf '\nrender\n'

fixture=$workdir/render
write_fixture "$fixture"
rendered=$(run "$fixture" --render)

assert_eq "sections come out in canonical order, not alphabetical" \
  "### Breaking
### Added
### Fixed" \
  "$(printf '%s\n' "$rendered" | grep '^### ')"

assert_eq "fragments within a section are ordered numerically, not by string" \
  "3
12" \
  "$(printf '%s\n' "$rendered" | grep -oE '^- \*\*(Three|Twelve)' | sed 's/- \*\*Three/3/;s/- \*\*Twelve/12/')"

assert_lacks "--render leaves out sections with no fragments" "$rendered" "### Changed"
assert_lacks "--render does not fold in the existing [Unreleased] entry" "$rendered" "before the fragments existed"
assert_contains "--render keeps links inline" "$rendered" "([#7](https://github.com/flojon/aspire-servicesources/issues/7))"

# ---------------------------------------------------------------------------- close

printf '\nclose\n'

fixture=$workdir/close
write_fixture "$fixture"
run "$fixture" 0.4.0 --date 2026-09-03 >/dev/null
closed=$(cat "$fixture/CHANGELOG.md")

assert_contains "the new version heading carries the date given" "$closed" "## [0.4.0] - 2026-09-03"
assert_contains "[Unreleased] is left in place and empty" "$closed" "## [Unreleased]

## [0.4.0] - 2026-09-03"
assert_contains "the previously released section survives" "$closed" "## [0.1.0] - 2026-01-01"

assert_eq "headings under the new version are in canonical order" \
  "## [Unreleased]
## [0.4.0] - 2026-09-03
### Breaking
### Added
### Fixed
## [0.1.0] - 2026-01-01
### Added" \
  "$(printf '%s\n' "$closed" | grep -E '^#{2,3} ')"

# The hand-written entry and the fragments both belong under Fixed, and have to end up under one
# heading rather than producing two - the corruption that made `merge=union` the wrong fix.
assert_eq "a hand-written entry and fragments merge under a single heading" \
  "1" \
  "$(printf '%s\n' "$closed" | grep -c '^### Fixed')"

assert_contains "the hand-written entry comes before the fragments" "$closed" "before the fragments existed"
assert_eq "hand-written first, then fragments in numeric order" \
  "hand
3
12" \
  "$(printf '%s\n' "$closed" | grep -oE '^- \*\*(Written by hand|Three|Twelve)' |
     sed 's/- \*\*Written by hand/hand/;s/- \*\*Three/3/;s/- \*\*Twelve/12/')"

assert_lacks "inline links are converted to the reference style" "$closed" "]("
assert_contains "a converted reference keeps its number" "$closed" "([#20])."

assert_eq "definitions are merged and numerically sorted" \
  "[#3]: https://github.com/flojon/aspire-servicesources/issues/3
[#7]: https://github.com/flojon/aspire-servicesources/issues/7
[#10]: https://github.com/flojon/aspire-servicesources/issues/10
[#12]: https://github.com/flojon/aspire-servicesources/pull/12
[#20]: https://github.com/flojon/aspire-servicesources/issues/20" \
  "$(printf '%s\n' "$closed" | grep -E '^\[#[0-9]+\]: ')"

assert_eq "compare links are repointed at the new version" \
  "[Unreleased]: https://github.com/flojon/aspire-servicesources/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/flojon/aspire-servicesources/compare/v0.1.0...v0.4.0
[0.1.0]: https://github.com/flojon/aspire-servicesources/releases/tag/v0.1.0" \
  "$(printf '%s\n' "$closed" | grep -E '^\[(Unreleased|[0-9]+\.[0-9]+\.[0-9]+)\]: ')"

assert_eq "a cross-repository reference stays, below the numbered block" \
  "[NuGetGallery#6948]: https://github.com/NuGet/NuGetGallery/issues/6948" \
  "$(printf '%s\n' "$closed" | tail -1)"

assert_eq "the fragments are deleted, leaving only the README" \
  "README.md" \
  "$(ls "$fixture/changelog.d")"

# ------------------------------------------------------------- cross-repository references

# A fragment is the only route a cross-repository reference has into the changelog now, and it
# writes it inline like every other link. Left unconverted it would sit in the file as the one
# entry written in a style the rest of it does not use, and its definition would never reach the
# block at the bottom. Its own fixture, so the ordering assertions above keep their exact
# expected output.
printf '\ncross-repository references\n'

fixture=$workdir/xrepo
write_fixture "$fixture"
cat > "$fixture/changelog.d/30-fixed.md" <<'EOF'
- **Worked around an upstream bug** ([#30](https://github.com/flojon/aspire-servicesources/issues/30)).
  Caused by [microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507), and by
  [NuGetGallery#6948](https://github.com/NuGet/NuGetGallery/issues/6948), both still open. The
  `[AspireExport]` attribute is unaffected.
EOF
run "$fixture" 0.4.0 --date 2026-09-03 >/dev/null
xrepo=$(cat "$fixture/CHANGELOG.md")

assert_contains "a cross-repository reference is converted to the reference style" \
  "$xrepo" "[microsoft/aspire#19507], and by"
assert_lacks "its inline URL does not survive in the entry" \
  "$xrepo" "[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507)"
assert_contains "a prefix without a slash is converted too" \
  "$xrepo" "[NuGetGallery#6948], both still open"

# The label the file already defines must not gain a second definition, and the new one has to
# be there - both below the numbered block, which is what a reader of this file expects.
assert_eq "its definition joins the named block, existing ones kept once" \
  "[NuGetGallery#6948]: https://github.com/NuGet/NuGetGallery/issues/6948
[microsoft/aspire#19507]: https://github.com/microsoft/aspire/issues/19507" \
  "$(printf '%s\n' "$xrepo" | grep -E '^\[[^]]*#[0-9]+\]: ' | grep -vE '^\[#[0-9]+\]: ')"

assert_contains "the local reference in the same entry still lands in the numbered block" \
  "$xrepo" "[#30]: https://github.com/flojon/aspire-servicesources/issues/30"

# A bracketed literal that is not a reference at all. The conversion keys on a label ending in
# "#<digits>", so an attribute name in an entry has to come through untouched.
assert_contains "a bracketed literal that is not a reference is left alone" \
  "$xrepo" '`[AspireExport]` attribute is unaffected'

# ---------------------------------------------------------------------------- refusals

printf '\nrefusals\n'

fixture=$workdir/dryrun
write_fixture "$fixture"
before=$(cat "$fixture/CHANGELOG.md")
run "$fixture" 0.4.0 --date 2026-09-03 --dry-run >/dev/null
assert_eq "--dry-run writes nothing" "$before" "$(cat "$fixture/CHANGELOG.md")"
assert_eq "--dry-run keeps the fragments" "4" "$(ls "$fixture/changelog.d"/[0-9]*.md | wc -l)"

fixture=$workdir/already
write_fixture "$fixture"
assert_fails "refuses a version the changelog already has" run "$fixture" 0.1.0

fixture=$workdir/empty
write_fixture "$fixture"
rm "$fixture"/changelog.d/[0-9]*.md
printf '# Changelog\n\n## [Unreleased]\n\n[Unreleased]: https://github.com/flojon/aspire-servicesources/compare/v0.1.0...HEAD\n' \
  > "$fixture/CHANGELOG.md"
assert_fails "refuses a release with nothing in it" run "$fixture" 0.4.0

fixture=$workdir/badname
write_fixture "$fixture"
touch "$fixture/changelog.d/notes.md"
assert_fails "rejects a fragment that is not <number>-<section>.md" run "$fixture" --lint

fixture=$workdir/badsection
write_fixture "$fixture"
echo "- entry" > "$fixture/changelog.d/9-improved.md"
assert_fails "rejects a section name the changelog does not use" run "$fixture" --lint

fixture=$workdir/emptyfrag
write_fixture "$fixture"
touch "$fixture/changelog.d/9-added.md"
assert_fails "rejects an empty fragment" run "$fixture" --lint

# ---------------------------------------------------------------------------- the real file

printf '\nthe repository changelog\n'

repo_root=$(cd -- "$script_dir/.." && pwd)
assert_eq "the fragments checked in are named correctly" "" "$("$collect" --lint 2>&1)"

# Folding the real changelog has to leave every released section byte-identical: the script
# rewrites the link block, and a released section is a historical record.
#
# Against the real CHANGELOG.md, but with a fragment directory of this test's own. The checked-in
# changelog.d/ is empty of fragments for most of the release cycle - closing a release folds them
# all away - and a fold with nothing to release refuses, by design. Reading the real directory
# here would therefore abort the whole suite under `set -e`, with no summary printed, on the
# first PR after every release, for a reason having nothing to do with that PR.
real_fragments=$workdir/real-fragments
mkdir -p "$real_fragments"
cat > "$real_fragments/9-fixed.md" <<'EOF'
- **A synthetic entry, so that this test does not depend on what `changelog.d/` happens to hold**
  ([#9](https://github.com/flojon/aspire-servicesources/issues/9)).
EOF
real=$(CHANGELOG_FILE=$repo_root/CHANGELOG.md FRAGMENT_DIR=$real_fragments \
  "$collect" 9.9.9 --date 2026-09-03 --dry-run)
released_before=$(awk '/^## \[0/ { inside = 1 } /^\[/ { exit } inside' "$repo_root/CHANGELOG.md")
released_after=$(printf '%s\n' "$real" | awk '/^## \[0/ { inside = 1 } /^\[/ { exit } inside')
assert_eq "released sections are untouched by a fold" "$released_before" "$released_after"

assert_eq "every reference the folded file cites is defined" "" \
  "$(printf '%s\n' "$real" | grep -oE '\[#[0-9]+\]' | sort -u | tr -d '[]' |
     while read -r ref; do
       printf '%s\n' "$real" | grep -qE "^\[$ref\]: " || printf '%s undefined\n' "$ref"
     done)"

# ---------------------------------------------------------------------------- the point

printf '\nconcurrent branches\n'

# The claim the whole scheme rests on, asserted rather than assumed: two PRs adding an entry to
# the same section merge cleanly as fragments, and conflict as changelog edits.
scratch=$workdir/scratch
mkdir -p "$scratch"
git() { command git -c user.email=test@example.com -c user.name=test -C "$scratch" "$@"; }

write_fixture "$scratch"
rm "$scratch"/changelog.d/[0-9]*.md
git init -q -b main
git add -A
git commit -qm base

for branch in a b; do
  git checkout -q -b "$branch" main
  case $branch in
    a) n=101 ;;
    b) n=102 ;;
  esac
  printf -- '- **Entry %s** ([#%s](https://example.com/%s)).\n' "$n" "$n" "$n" \
    > "$scratch/changelog.d/$n-fixed.md"
  git add -A
  git commit -qm "$branch"
done

git checkout -q a
if git merge -q --no-edit b >/dev/null 2>&1; then
  ok "two branches adding a fragment to the same section merge cleanly"
else
  no "two branches adding a fragment to the same section merge cleanly"
fi
assert_eq "both entries survive the merge" "2" "$(ls "$scratch"/changelog.d/10[12]-fixed.md | wc -l)"

# The control: the same two entries written the old way, into the same section of CHANGELOG.md.
git checkout -q -b old-a main
for branch in old-a old-b; do
  git checkout -q "$branch" 2>/dev/null || git checkout -q -b "$branch" main
  case $branch in
    old-a) n=201 ;;
    old-b) n=202 ;;
  esac
  awk -v line="- **Entry $n** ([#$n])." '
    { print }
    /^### Fixed/ && !done { print ""; print line; done = 1 }
  ' "$scratch/CHANGELOG.md" > "$scratch/CHANGELOG.new"
  mv "$scratch/CHANGELOG.new" "$scratch/CHANGELOG.md"
  git add -A
  git commit -qm "$branch"
done

git checkout -q old-a
if git merge --no-edit old-b >/dev/null 2>&1; then
  no "the old way still conflicts" "the control merged cleanly, so this test proves nothing"
else
  ok "the control confirms the old way conflicts on the same lines"
fi
git merge --abort >/dev/null 2>&1 || true

unset -f git

# ----------------------------------------------------------------------------

printf '\n%d passed, %d failed\n' "$passed" "$failed"
((failed == 0))
