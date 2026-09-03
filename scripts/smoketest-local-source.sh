#!/usr/bin/env bash
# Manual smoke test for the 'local' ServiceSource: points DemoAppHost's `orders` service at a git
# repository this script creates, and checks that the package clones it, checks out the ref it was
# told to, builds the project inside the clone and runs it as an Aspire resource — then that a
# `local.ref` override moves the checkout, and that a `local.path` override uses a directory the
# developer manages and clones nothing.
#
# Why this exists: `local` is the default source and carries the most machinery of the four
# (clone, ref reconciliation, the build barrier, AddProject), and it is the only source with no
# end-to-end coverage — `container` and `kubernetes` each have one. The unit tests fake the git
# client, so nothing else in the repository proves a real clone builds and runs.
#
# The repository is created locally rather than fetched: it keeps the test offline and
# deterministic, and GitUrl accepts a filesystem path as one of its documented forms. The seeded
# project has no PackageReference of its own, so the checkout restores without network too.
#
# Requires: dotnet, git. No container runtime.
set -euo pipefail

# The seeded project writes this beside its own binary once it starts. That is how the test knows
# the clone was not merely made but built and run: a console project has no port to curl, and a
# child process's stdout goes to Aspire's log pipeline rather than to this script's.
MARKER_FILE="servicesources-smoketest-ran.txt"
MAIN_MARKER="from-main"
BRANCH_MARKER="from-branch"
OTHER_BRANCH="feature/other-ref"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apphost_dir="$repo_root/samples/DemoAppHost"
cache_dir="$repo_root/.smoketest-cache"
mkdir -p "$cache_dir"

service_sources_dir="$apphost_dir/.servicesources"
had_service_sources=0
origin_repo="$cache_dir/local-source-origin.git"
seed_tree="$cache_dir/local-source-seed"
self_managed="$cache_dir/local-source-self-managed"
checkout_dir="$apphost_dir/.servicesources/checkouts/orders"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

fail_with_apphost_log() {
  printf '\nFAIL: %s\n' "$*" >&2
  if [[ -s "$cache_dir/local-source-apphost.log" ]]; then
    printf '\n--- last 40 lines of apphost.log ---\n' >&2
    tail -n 40 "$cache_dir/local-source-apphost.log" >&2
  fi
  exit 1
}

command -v dotnet >/dev/null 2>&1 || fail "dotnet is required"
command -v git >/dev/null 2>&1 || fail "git is required (2.7 or newer)"

apphost_pid=""
yaml_backup="$cache_dir/local-source-servicesources.yaml.bak"
local_json_backup="$cache_dir/local-source-servicesources.local.json.bak"
had_local_json=0

cleanup() {
  log "cleaning up"
  if [[ -n "$apphost_pid" ]] && kill -0 "$apphost_pid" 2>/dev/null; then
    kill "$apphost_pid" 2>/dev/null || true
    wait "$apphost_pid" 2>/dev/null || true
  fi
  cp "$yaml_backup" "$apphost_dir/servicesources.yaml" 2>/dev/null || true
  if [[ $had_local_json -eq 1 ]]; then
    cp "$local_json_backup" "$apphost_dir/servicesources.local.json"
  else
    rm -f "$apphost_dir/servicesources.local.json"
  fi
  rm -f "$yaml_backup" "$local_json_backup"
  # Only this service's managed checkout, so a developer's other checkouts survive a run — and
  # the whole .servicesources directory only when the run is what created it. The barrier files
  # in there are generated, but its .gitignore un-ignores itself on purpose, so a leftover
  # directory shows up as an untracked file in this repository after every run.
  rm -rf "$checkout_dir"
  if [[ $had_service_sources -eq 0 ]]; then
    rm -rf "$service_sources_dir"
  fi
  rm -rf "$origin_repo" "$seed_tree" "$self_managed"
}
trap cleanup EXIT

[[ -d "$service_sources_dir" ]] && had_service_sources=1

cp "$apphost_dir/servicesources.yaml" "$yaml_backup"
if [[ -f "$apphost_dir/servicesources.local.json" ]]; then
  had_local_json=1
  cp "$apphost_dir/servicesources.local.json" "$local_json_backup"
fi

# ---------------------------------------------------------------------------
# A repository to clone
# ---------------------------------------------------------------------------

# Two branches carrying different marker text, so a `ref` override is observable in what the
# running process produced rather than only in git's own output.
log "creating a git repository with two branches"
rm -rf "$origin_repo" "$seed_tree"
mkdir -p "$seed_tree/SampleService"

cat > "$seed_tree/SampleService/SampleService.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Off, with the usings written out in Program.cs: what this project must not depend on is
         an SDK default that could change, since a compile failure here reads as the local source
         having failed to run the project. -->
    <ImplicitUsings>disable</ImplicitUsings>
    <!-- No PackageReference anywhere in here, deliberately: the checkout then restores with
         nothing to download, which keeps this test offline and independent of whatever feeds
         the build barrier leaves configured inside a managed checkout. -->
  </PropertyGroup>
</Project>
EOF

write_program() {
  # $1 = marker text this branch's build should leave behind
  cat > "$seed_tree/SampleService/Program.cs" <<EOF
using System;
using System.IO;
using System.Threading;

// Writes a marker beside its own binary, then stays alive so Aspire sees a running resource.
// Beside the binary rather than in the working directory: AppContext.BaseDirectory is inside the
// checkout wherever the process is launched from, so the test can find it without assuming what
// working directory Aspire chose.
File.WriteAllText(
    Path.Combine(AppContext.BaseDirectory, "$MARKER_FILE"), "$1");

Console.WriteLine("SampleService started ($1)");
Thread.Sleep(Timeout.Infinite);
EOF
}

# -b main so the bare repository's HEAD names a branch that exists once the pushes land;
# the default (master) leaves `git clone` warning about a nonexistent HEAD.
git init -q --bare -b main "$origin_repo"
(
  cd "$seed_tree"
  git init -q -b main .
  git config user.email smoketest@example.invalid
  git config user.name "ServiceSources smoke test"
  write_program "$MAIN_MARKER"
  git add -A
  git commit -q -m "SampleService on main"
  git checkout -q -b "$OTHER_BRANCH"
  write_program "$BRANCH_MARKER"
  git add -A
  git commit -q -m "SampleService on $OTHER_BRANCH"
  git checkout -q main
  git remote add origin "$origin_repo"
  git push -q origin main "$OTHER_BRANCH"
)

# ---------------------------------------------------------------------------
# Catalog and developer config
# ---------------------------------------------------------------------------

# Replaces the sample's catalog, so it still has to declare every service Program.cs calls
# AddService for. `orders` is the subject; the other two resolve to "url", which starts nothing.
log "pointing DemoAppHost's 'orders' service at $origin_repo"
cat > "$apphost_dir/servicesources.yaml" <<EOF
services:
  orders:
    repository: $origin_repo
    project: SampleService/SampleService.csproj
    defaultRef: main
  inventory:
    url:
      url: http://unused.invalid
  payments:
    url:
      url: http://unused.invalid
EOF

write_local_json() {
  # $1 = the body of orders' "local" block, or omitted for an entry with no block at all.
  #
  # Spelled out rather than folded into a ${1:+...} expansion: that form ends at the first
  # unmatched '}', so the braces a JSON block needs escape the expansion and land in the file.
  local orders='    "orders": { "source": "local" },'
  if [[ -n "${1:-}" ]]; then
    orders="    \"orders\": { \"source\": \"local\", \"local\": { $1 } },"
  fi
  cat > "$apphost_dir/servicesources.local.json" <<EOF
{
  "services": {
$orders
    "inventory": { "source": "url" },
    "payments": { "source": "url" }
  }
}
EOF
}

# Runs the AppHost until the marker file appears under $2, then stops it. $1 labels the log.
# The marker is what proves the checkout was built and started; the AppHost is killed as soon as
# it appears, because nothing after that is under test and a run left going would hold ports.
run_until_marker() {
  local label="$1" search_root="$2"
  shift 2
  local run_log="$cache_dir/local-source-apphost.log"

  ( cd "$apphost_dir" && env "$@" dotnet run --project DemoAppHost.csproj --no-build ) \
    > "$run_log" 2>&1 &
  apphost_pid=$!

  local found=""
  # Generous: a cold run clones, restores and builds the checkout before anything starts.
  for _ in $(seq 1 180); do
    if [[ -d "$search_root" ]]; then
      found="$(find "$search_root" -name "$MARKER_FILE" -print 2>/dev/null | head -n1 || true)"
      [[ -n "$found" ]] && break
    fi
    # A run that has already died will never produce the marker; stop waiting for it.
    kill -0 "$apphost_pid" 2>/dev/null || break
    sleep 1
  done

  kill "$apphost_pid" 2>/dev/null || true
  wait "$apphost_pid" 2>/dev/null || true
  apphost_pid=""

  if [[ -z "$found" ]]; then
    # Almost always the checkout failing to restore or build. Aspire surfaces that on the
    # resource, not on its own stdout, so reproduce it here where the output can be seen.
    if [[ -d "$search_root" ]]; then
      printf '\n--- %s exists; building its project the way Aspire would ---\n' "$search_root" >&2
      dotnet build "$search_root/SampleService/SampleService.csproj" -v quiet 2>&1 \
        | tail -n 20 >&2 || true
    else
      printf '\n--- %s does not exist: nothing was cloned ---\n' "$search_root" >&2
    fi
    fail_with_apphost_log "$label: no $MARKER_FILE appeared under $search_root"
  fi
  tr -d '\r\n' < "$found"
}

log "building DemoAppHost"
dotnet build "$apphost_dir/DemoAppHost.csproj" -c Debug -v quiet

# ---------------------------------------------------------------------------
# 1. A cold managed checkout: clone, check out defaultRef, build, run
# ---------------------------------------------------------------------------
log "1. cold run: the checkout does not exist yet"
rm -rf "$checkout_dir"
write_local_json ""
[[ ! -d "$checkout_dir" ]] || fail "the checkout directory should not exist before the cold run"

marker="$(run_until_marker "cold run" "$checkout_dir")"
[[ "$marker" == "$MAIN_MARKER" ]] \
  || fail "cold run: expected the marker from defaultRef ('$MAIN_MARKER'), got '$marker'"
printf '    cloned, built and ran the project from defaultRef\n'

git -C "$checkout_dir" rev-parse --verify HEAD >/dev/null 2>&1 \
  || fail "the checkout is not a git repository"
on_branch="$(git -C "$checkout_dir" rev-parse --abbrev-ref HEAD)"
[[ "$on_branch" == "main" ]] || fail "expected the checkout on 'main', found '$on_branch'"
printf '    checkout is a git repository on main\n'

# ---------------------------------------------------------------------------
# 2. A local.ref override moves the existing checkout
# ---------------------------------------------------------------------------
# Also the nested developer-config spelling from #161: a field inside a source's block carries the
# block name, so this is __Local__Ref rather than a flat __Ref.
log "2. local.ref override, from the environment, on the warm checkout"
find "$checkout_dir" -name "$MARKER_FILE" -delete
marker="$(run_until_marker "ref override" "$checkout_dir" \
  "ServiceSources__Services__orders__Local__Ref=$OTHER_BRANCH")"
[[ "$marker" == "$BRANCH_MARKER" ]] \
  || fail "ref override: expected '$BRANCH_MARKER', got '$marker' (the checkout did not move)"
on_branch="$(git -C "$checkout_dir" rev-parse --abbrev-ref HEAD)"
[[ "$on_branch" == "$OTHER_BRANCH" ]] \
  || fail "expected the checkout reconciled to '$OTHER_BRANCH', found '$on_branch'"
printf '    checkout reconciled to %s and ran the code from that branch\n' "$OTHER_BRANCH"

# ---------------------------------------------------------------------------
# 3. A local.path override runs a checkout the developer manages, and clones nothing
# ---------------------------------------------------------------------------
log "3. local.path override pointing outside the AppHost"
rm -rf "$self_managed"
git clone -q "$origin_repo" "$self_managed"
git -C "$self_managed" checkout -q main
rm -rf "$checkout_dir"

marker="$(run_until_marker "path override" "$self_managed" \
  "ServiceSources__Services__orders__Local__Path=$self_managed")"
[[ "$marker" == "$MAIN_MARKER" ]] \
  || fail "path override: expected '$MAIN_MARKER' from the self-managed clone, got '$marker'"
[[ ! -d "$checkout_dir" ]] \
  || fail "a 'local.path' override must not create a managed checkout, but $checkout_dir exists"
printf '    ran from the self-managed clone, and cloned nothing into .servicesources\n'

log "PASS: the local source cloned, reconciled a ref, honoured a path override, and ran the project in every case"
