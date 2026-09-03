#!/usr/bin/env bash
#
# Fold the changelog.d/ fragments into CHANGELOG.md under a new version heading.
#
# Every PR writes its changelog entry to a file of its own under changelog.d/, so that two PRs
# never edit the same lines (see changelog.d/README.md, and #145 for why). This script is the
# other half of that: at release time it collects the fragments, groups them by section, folds
# them in under "## [X.Y.Z]" alongside anything already written under "## [Unreleased]", writes
# the compare and reference links, and deletes the fragments.
#
# It runs in the release PR rather than in the release workflow, deliberately. The section it
# produces is what Directory.Build.targets packs into PackageReleaseNotes, so it reaches a
# nuget.org listing that cannot be edited afterwards. A reviewable diff is worth a manual step.
#
# CHANGELOG_FILE and FRAGMENT_DIR override the paths, which is how the tests drive it against a
# fixture rather than against the real changelog.
#
# Only POSIX awk features are used: the runner's /usr/bin/awk is mawk, which has neither gawk's
# three-argument match() nor gensub().

set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
CHANGELOG_FILE=${CHANGELOG_FILE:-$repo_root/CHANGELOG.md}
FRAGMENT_DIR=${FRAGMENT_DIR:-$repo_root/changelog.d}

# The order sections appear in under a version heading. Breaking first because it is the one a
# reader upgrading cannot afford to skip; Documentation last because it never blocks an upgrade.
SECTIONS=(Breaking Added Changed Fixed Documentation)

die() { printf 'collect-changelog: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Fold changelog.d/ fragments into CHANGELOG.md under a new version heading.

  scripts/collect-changelog.sh 0.4.0              fold, rewrite CHANGELOG.md, delete fragments
  scripts/collect-changelog.sh 0.4.0 --dry-run    print the resulting file, change nothing
  scripts/collect-changelog.sh 0.4.0 --date D     date the section D instead of today (UTC)
  scripts/collect-changelog.sh --render           print just the fragments, grouped by section
  scripts/collect-changelog.sh --lint             check fragment filenames and exit

Fragment naming and contents are documented in changelog.d/README.md.
EOF
}

# ---------------------------------------------------------------------------- fragments

# Fragment paths for one lowercased section, ordered by the number they lead with, so the result
# depends on neither the locale's collation nor the order the filesystem hands them back. Prints
# nothing when the section has no fragments.
fragment_files() {
  local section=$1 files=() name
  shopt -s nullglob
  files=("$FRAGMENT_DIR"/[0-9]*-"$section".md)
  shopt -u nullglob
  ((${#files[@]})) || return 0
  printf '%s\n' "${files[@]##*/}" | sort -n | sed "s#^#$FRAGMENT_DIR/#"
}

# A filename that does not match the convention is a fragment that gets silently skipped at
# release time: the entry is written, reviewed, merged, and then never appears anywhere. Fail on
# it while the PR that introduced it is still open.
lint() {
  local bad=0 path name
  [[ -d $FRAGMENT_DIR ]] || return 0

  shopt -s nullglob
  for path in "$FRAGMENT_DIR"/*; do
    name=${path##*/}
    if [[ $name == README.md ]]; then
      continue
    elif [[ ! $name =~ ^[0-9]+-(breaking|added|changed|fixed|documentation)\.md$ ]]; then
      printf 'changelog.d/%s: expected <number>-<section>.md, section being one of breaking, added, changed, fixed, documentation\n' "$name" >&2
      bad=1
    elif [[ ! -s $path ]]; then
      printf 'changelog.d/%s: empty\n' "$name" >&2
      bad=1
    fi
  done
  shopt -u nullglob

  if ((bad)); then
    die "fragment naming is documented in changelog.d/README.md"
  fi
}

# ---------------------------------------------------------------------------- changelog parts

# Body of "### <heading>" within the text on stdin, up to the next "### " or the end. Used to
# pull apart whatever is already sitting under "## [Unreleased]", so that a release mixing
# hand-written entries with fragments merges them under one heading rather than emitting two.
section_body() {
  awk -v want="### $1" '
    $0 == want { inside = 1; next }
    inside && /^### / { exit }
    inside { print }
  '
}

# Everything between "## [Unreleased]" and whatever ends it: the next version heading, or the
# reference-link block Keep a Changelog puts at the end of the file.
unreleased_body() {
  awk '
    /^## \[Unreleased\]/ { inside = 1; next }
    inside && (/^## / || /^\[/) { exit }
    inside { print }
  ' "$CHANGELOG_FILE"
}

# Strip leading and trailing blank lines, leaving the interior alone.
trim_blank_lines() {
  awk '
    NF { if (pending) { for (i = 0; i < pending; i++) print ""; pending = 0 } started = 1 }
    !NF { if (started) pending++; next }
    started { print }
  '
}

# ---------------------------------------------------------------------------- rendering

# The sections for the new version: for each heading in canonical order, whatever was already
# under it in [Unreleased] followed by that section's fragments. A heading with neither is
# skipped, so an untouched section never reaches PackageReleaseNotes as an empty heading.
#
# $1 is "yes" to fold in the existing [Unreleased] body, "no" to render the fragments alone.
render_sections() {
  local include_unreleased=$1 unreleased="" section lower existing frags path first=1

  if [[ $include_unreleased == yes ]]; then
    unreleased=$(unreleased_body)
  fi

  for section in "${SECTIONS[@]}"; do
    lower=${section,,}
    existing=$(printf '%s\n' "$unreleased" | section_body "$section" | trim_blank_lines)

    frags=""
    while IFS= read -r path; do
      [[ -n $path ]] || continue
      frags+=$(trim_blank_lines < "$path")
      frags+=$'\n\n'
    done < <(fragment_files "$lower")
    frags=$(printf '%s' "$frags" | trim_blank_lines)

    if [[ -z $existing && -z $frags ]]; then
      continue
    fi

    if ((first == 0)); then
      printf '\n'
    fi
    first=0

    printf '### %s\n\n' "$section"
    if [[ -n $existing ]]; then
      printf '%s\n' "$existing"
      if [[ -n $frags ]]; then
        printf '\n'
      fi
    fi
    if [[ -n $frags ]]; then
      printf '%s\n' "$frags"
    fi
  done
}

# ---------------------------------------------------------------------------- links

# Fragments carry their links inline so that nothing has to be appended to the one shared,
# ordered definition block at the bottom of the file - that block was the second place
# concurrent PRs collided. Folding is the moment that stops mattering, since only the release PR
# touches it, so convert back to the reference style the rest of the changelog is written in.
to_reference_links() {
  sed -E 's/\[(#[0-9]+)\]\([^()]*\)/[\1]/g'
}

# "<number> <url>" for every inline link in the text on stdin.
inline_link_definitions() {
  grep -oE '\[#[0-9]+\]\([^()]*\)' | sed -E 's/^\[#([0-9]+)\]\((.*)\)$/\1 \2/'
}

# ---------------------------------------------------------------------------- close

close() {
  local version=$1 date=$2 dry_run=$3

  [[ -f $CHANGELOG_FILE ]] || die "no changelog at $CHANGELOG_FILE"
  if grep -q "^## \[$version\]" "$CHANGELOG_FILE"; then
    die "CHANGELOG.md already has a [$version] section"
  fi

  local unrel_line link_line next_h2
  unrel_line=$(grep -n '^## \[Unreleased\]' "$CHANGELOG_FILE" | head -1 | cut -d: -f1)
  [[ -n $unrel_line ]] || die "no '## [Unreleased]' heading in $CHANGELOG_FILE"
  link_line=$(grep -n '^\[Unreleased\]: ' "$CHANGELOG_FILE" | head -1 | cut -d: -f1)
  [[ -n $link_line ]] || die "no '[Unreleased]:' compare link in $CHANGELOG_FILE"

  # The heading that ends the [Unreleased] section. Absent on a changelog whose first release
  # this is, in which case the link block ends it.
  next_h2=$(awk -v start="$unrel_line" 'NR > start && /^## / { print NR; exit }' "$CHANGELOG_FILE")
  [[ -n $next_h2 ]] || next_h2=$link_line

  local body
  body=$(render_sections yes)
  [[ -n $body ]] || die "nothing to release: no fragments, and nothing under [Unreleased]"

  # Every [#N] the new section cites, with the URL its fragment gave it. Definitions already in
  # the file win, so re-citing an issue an earlier release mentioned changes nothing.
  local new_defs
  new_defs=$(printf '%s\n' "$body" | inline_link_definitions || true)
  body=$(printf '%s\n' "$body" | to_reference_links)

  local base prev existing_defs version_links named
  base=$(sed -n "${link_line}p" "$CHANGELOG_FILE" | sed -E 's#^\[Unreleased\]: (.*)/compare/.*#\1#')
  [[ $base == http* ]] || die "cannot read the repository URL out of the [Unreleased] link"

  prev=$(grep -oE '^\[[0-9]+\.[0-9]+\.[0-9]+\]: ' "$CHANGELOG_FILE" | head -1 |
    sed -E 's/^\[(.+)\]: $/\1/' || true)

  # The link block, split into the three kinds of definition it holds.
  version_links=$(sed -n "${link_line},\$p" "$CHANGELOG_FILE" |
    grep -E '^\[[0-9]+\.[0-9]+\.[0-9]+\]: ' || true)
  existing_defs=$(sed -n "${link_line},\$p" "$CHANGELOG_FILE" | grep -E '^\[#[0-9]+\]: ' || true)
  named=$(sed -n "${link_line},\$p" "$CHANGELOG_FILE" | grep -E '^\[[^]]+\]: ' |
    grep -vE '^\[(#[0-9]+|Unreleased|[0-9]+\.[0-9]+\.[0-9]+)\]: ' || true)

  # Existing definitions first so that, deduplicated by a stable sort, they win over a fragment
  # that cites the same number with a different URL.
  local merged_defs
  merged_defs=$(
    {
      printf '%s\n' "$existing_defs" | sed -E 's/^\[#([0-9]+)\]: /\1 /'
      printf '%s\n' "$new_defs"
    } | grep -E '^[0-9]+ ' | sort -s -k1,1n |
      awk '!seen[$1]++ { printf "[#%s]: %s\n", $1, $2 }'
  )

  local out
  out=$(mktemp)

  {
    # Everything above [Unreleased], then an [Unreleased] left empty for the next cycle.
    sed -n "1,$((unrel_line - 1))p" "$CHANGELOG_FILE"
    printf '## [Unreleased]\n\n'
    printf '## [%s] - %s\n\n' "$version" "$date"
    printf '%s\n\n' "$body"

    # The previously released sections, untouched.
    if ((next_h2 < link_line)); then
      sed -n "${next_h2},$((link_line - 1))p" "$CHANGELOG_FILE"
    fi

    # Compare links: [Unreleased] now starts at this version, and this version compares against
    # the one below it - or, with nothing below it, points at its own tag.
    printf '[Unreleased]: %s/compare/v%s...HEAD\n' "$base" "$version"
    if [[ -n $prev ]]; then
      printf '[%s]: %s/compare/v%s...v%s\n' "$version" "$base" "$prev" "$version"
    else
      printf '[%s]: %s/releases/tag/v%s\n' "$version" "$base" "$version"
    fi
    [[ -n $version_links ]] && printf '%s\n' "$version_links"

    # Issue references, existing plus whatever the fragments brought, numerically ordered. The
    # block on disk drifted out of order over time precisely because every PR appended to its
    # end to miss a conflict; nothing appends to it any more, so normalise it.
    printf '\n%s\n' "$merged_defs"

    # Anything else the file defines - cross-repository references such as [NuGetGallery#6948] -
    # kept below the numbered block, in the order they were written.
    [[ -n $named ]] && printf '\n%s\n' "$named"

    true
  } > "$out"

  if [[ $dry_run == yes ]]; then
    cat "$out"
    rm -f "$out"
    return 0
  fi

  cat "$out" > "$CHANGELOG_FILE"
  rm -f "$out"

  local section path removed=0
  for section in "${SECTIONS[@]}"; do
    while IFS= read -r path; do
      [[ -n $path ]] || continue
      rm -f "$path"
      removed=$((removed + 1))
    done < <(fragment_files "${section,,}")
  done

  printf 'Closed [%s] - %s in %s, folding in %d fragment(s).\n' \
    "$version" "$date" "${CHANGELOG_FILE##*/}" "$removed"
  printf 'Read the diff before committing: this section becomes the nuget.org release notes.\n'
}

# ---------------------------------------------------------------------------- entry point

version=""
date=$(date -u +%Y-%m-%d)
dry_run=no
mode=close

while (($#)); do
  case $1 in
    --render) mode=render ;;
    --lint) mode=lint ;;
    --dry-run) dry_run=yes ;;
    --date)
      [[ $# -ge 2 ]] || die "--date needs a value"
      date=$2
      shift
      ;;
    -h|--help) usage; exit 0 ;;
    -*) die "unknown option $1" ;;
    *)
      [[ -z $version ]] || die "expected one version, got '$version' and '$1'"
      version=$1
      ;;
  esac
  shift
done

lint

case $mode in
  lint) ;;
  render) render_sections no ;;
  close)
    [[ -n $version ]] || { usage >&2; exit 2; }
    [[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] ||
      die "expected a version like 0.4.0, got '$version'"
    [[ $date =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] ||
      die "expected a date like 2026-09-03, got '$date'"
    close "$version" "$date" "$dry_run"
    ;;
esac
