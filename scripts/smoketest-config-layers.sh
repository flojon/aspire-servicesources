#!/usr/bin/env bash
# Manual smoke test for the developer-config layering (#69): runs DemoAppHost repeatedly with
# `source` set from a different configuration layer each time and checks that the layer which
# should win does. Then checks the part that is not about precedence at all — that the
# appsettings layers are read from the process's CONTENT ROOT, so an appsettings file sitting
# beside the project does nothing until it is copied to the output directory.
#
# Why this is not a unit test: DeveloperConfigurationTests covers the same ladder in-process, but
# it builds its host with `ProjectDirectory = dir` and `Args = ["--contentRoot", dir, ...]`, which
# points the content root at the source directory and hands `--environment` straight to the
# builder. Both of the ways a developer actually loses this are invisible from there — the file
# not being where the running process looks, and the environment being chosen by the launcher.
#
# Requires: dotnet. No container runtime, and no network: every run fails fast at composition,
# because the winning layer names a source that does not exist and AddService says so.
set -euo pipefail

# Each layer claims the service for a source name none of the four implementations has. That turns
# "which layer won?" into a single deterministic line of output — `has unknown source 'X'` — with
# nothing started, nothing cloned and nothing to tear down. A real source name would need a real
# backing service to prove the same point.
FILE_SENTINEL="layer-file"
APPSETTINGS_SENTINEL="layer-appsettings"
PROFILE_SENTINEL="layer-appsettings-environment"
ENVIRONMENT_SENTINEL="layer-environment"
COMMANDLINE_SENTINEL="layer-commandline"

# The named profile these runs select. Anything but Development, which is what an AppHost falls
# back to and so would not prove the profile was read.
PROFILE="Smoke"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apphost_dir="$repo_root/samples/DemoAppHost"
cache_dir="$repo_root/.smoketest-cache"
mkdir -p "$cache_dir"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || fail "dotnet is required"

yaml_backup="$cache_dir/config-layers-servicesources.yaml.bak"
local_json_backup="$cache_dir/config-layers-servicesources.local.json.bak"
had_local_json=0
output_dir=""

# Everything this test writes is either one of the two files it backs up, or a file it creates
# under a name the sample does not use; the latter are removed by name rather than by wildcard so
# a developer's own appsettings.json is never in scope.
cleanup() {
  log "cleaning up"
  cp "$yaml_backup" "$apphost_dir/servicesources.yaml" 2>/dev/null || true
  if [[ $had_local_json -eq 1 ]]; then
    cp "$local_json_backup" "$apphost_dir/servicesources.local.json"
  else
    rm -f "$apphost_dir/servicesources.local.json"
  fi
  rm -f "$yaml_backup" "$local_json_backup"
  rm -f "$apphost_dir/appsettings.json" "$apphost_dir/appsettings.$PROFILE.json"
  if [[ -n "$output_dir" ]]; then
    rm -f "$output_dir/appsettings.json" "$output_dir/appsettings.$PROFILE.json"
  fi
}
trap cleanup EXIT

cp "$apphost_dir/servicesources.yaml" "$yaml_backup"
if [[ -f "$apphost_dir/servicesources.local.json" ]]; then
  had_local_json=1
  cp "$apphost_dir/servicesources.local.json" "$local_json_backup"
fi

# Replaces the sample's catalog, so it must still declare every service Program.cs calls
# AddService for: a name the catalog does not carry throws, which would end the run before the
# layer under test had been consulted. `orders` is the subject; the other two resolve to "url",
# which starts nothing.
#
# `orders` keeps a repository and project so the entry is well formed for the "local" source, but
# no run here ever reaches a clone: every one of them is refused at AddService for naming a source
# that does not exist.
log "installing a catalog whose 'orders' entry is the subject"
cat > "$apphost_dir/servicesources.yaml" <<'EOF'
services:
  orders:
    repository: https://github.com/example/orders
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
  cat > "$apphost_dir/servicesources.local.json" <<EOF
{
  "services": {
    "orders": { "source": "$1" },
    "inventory": { "source": "url" },
    "payments": { "source": "url" }
  }
}
EOF
}

write_appsettings() {
  # $1 = directory, $2 = file name, $3 = source value
  cat > "$1/$2" <<EOF
{ "ServiceSources": { "Services": { "orders": { "source": "$3" } } } }
EOF
}

# Runs the AppHost and returns the source name the failure reported. The run is expected to fail,
# so its exit status is deliberately ignored: what is under test is which value reached AddService,
# and a zero exit would itself be the failure (it would mean no layer named an unknown source).
#
# $1 = a label for the log, $2 = "project" or "output" for where to run from, $3 = one
# NAME=VALUE for the run's environment or "-" for none, rest = extra args passed to the AppHost.
#
# The environment is an argument rather than a variable because this function is called inside
# `$( )`, which is a subshell: anything it assigns is discarded, so a global would keep whatever
# the previous caller set and silently apply it to the next run.
resolved_source() {
  local label="$1" where="$2" extra_env="$3"
  shift 3
  local run_log="$cache_dir/config-layers-$label.log"
  local -a env_prefix=()
  [[ "$extra_env" != "-" ]] && env_prefix=("$extra_env")

  if [[ "$where" == "project" ]]; then
    # cwd is the project directory, so the appsettings files beside the project are on the chain.
    ( cd "$apphost_dir" && env "${env_prefix[@]+"${env_prefix[@]}"}" \
        dotnet run --project DemoAppHost.csproj --no-build -- "$@" ) \
      > "$run_log" 2>&1 || true
  else
    # cwd is the output directory, which is what the content root becomes when an AppHost is
    # launched from its build output rather than through `dotnet run`. Only files copied there
    # are on the chain — the point of the second half of this test.
    ( cd "$output_dir" && env "${env_prefix[@]+"${env_prefix[@]}"}" ./DemoAppHost "$@" ) > "$run_log" 2>&1 || true
  fi


  # The message is "Service 'orders' has unknown source 'X'. ..." — take X.
  sed -n "s/.*has unknown source '\([^']*\)'.*/\1/p" "$run_log" | head -n1
}

expect_source() {
  # $1 = expected, $2 = actual, $3 = what was being proven
  if [[ "$2" != "$1" ]]; then
    fail "$3: expected the winning layer to be '$1', got '${2:-<no unknown-source report at all>}'"
  fi
  printf '    %s won, as expected\n' "$1"
}

log "building DemoAppHost"
dotnet build "$apphost_dir/DemoAppHost.csproj" -c Debug -v quiet

# MSBuild is asked where it put the binary rather than the tree being searched for one: this
# sample has more than one configuration's output under bin/, and picking the wrong one means
# running a stale copy of the package against fresh configuration - which reads as a
# configuration failure and is not one. -c Debug above pins what is being asked about.
target_path="$(dotnet msbuild "$apphost_dir/DemoAppHost.csproj" \
  -getProperty:TargetPath -p:Configuration=Debug -nologo 2>/dev/null | tr -d '\r' | tail -n1)"
output_dir="$(dirname "$target_path")"
[[ -n "$target_path" && -x "$output_dir/DemoAppHost" ]] \
  || fail "could not locate the built AppHost (TargetPath='$target_path')"
[[ "$output_dir/DemoAppHost.dll" -nt "$apphost_dir/Program.cs" ]] \
  || fail "$output_dir looks stale relative to Program.cs - build it before running this"

log "1. servicesources.local.json alone"
write_local_json "$FILE_SENTINEL"
rm -f "$apphost_dir/appsettings.json" "$apphost_dir/appsettings.$PROFILE.json"
expect_source "$FILE_SENTINEL" "$(resolved_source file project -)" \
  "the file is the base layer"

log "2. appsettings.json over the file"
write_appsettings "$apphost_dir" appsettings.json "$APPSETTINGS_SENTINEL"
expect_source "$APPSETTINGS_SENTINEL" "$(resolved_source appsettings project -)" \
  "appsettings.json outranks servicesources.local.json"

log "3. appsettings.$PROFILE.json over appsettings.json, with --environment $PROFILE"
write_appsettings "$apphost_dir" "appsettings.$PROFILE.json" "$PROFILE_SENTINEL"
expect_source "$PROFILE_SENTINEL" \
  "$(resolved_source profile project - --environment "$PROFILE")" \
  "the environment-specific layer is what makes named profiles work"

log "4. an environment variable over every file"
# Exported for the child only. The double underscore is configuration's separator for ':'.
expect_source "$ENVIRONMENT_SENTINEL" \
  "$(resolved_source environment project \
       "ServiceSources__Services__orders__Source=$ENVIRONMENT_SENTINEL" \
       --environment "$PROFILE")" \
  "the environment outranks the files"

log "5. the command line over the environment"
expect_source "$COMMANDLINE_SENTINEL" \
  "$(resolved_source commandline project \
       "ServiceSources__Services__orders__Source=$ENVIRONMENT_SENTINEL" \
       --environment "$PROFILE" \
       --ServiceSources:Services:orders:source="$COMMANDLINE_SENTINEL")" \
  "the command line is the top layer"

# The appsettings layers are read from the content root, while servicesources.local.json and
# servicesources.yaml are read from the AppHost's own directory. So launching from the build
# output splits them: the ServiceSources file is still found, and the appsettings beside the
# project are not. That asymmetry is why an `appsettings.{Environment}.json` that is not copied to
# the output directory silently does nothing under a launcher that runs the AppHost from there —
# `aspire run` being the one developers meet.
log "6. appsettings beside the project, AppHost launched from its output directory"
[[ -f "$output_dir/appsettings.json" ]] \
  && fail "the build copied appsettings.json to the output directory, so this test cannot tell the two locations apart"
expect_source "$FILE_SENTINEL" "$(resolved_source uncopied output - --environment "$PROFILE")" \
  "an appsettings file that was not copied to the output directory must not take effect"

log "7. the same files copied to the output directory"
cp "$apphost_dir/appsettings.json" "$output_dir/appsettings.json"
cp "$apphost_dir/appsettings.$PROFILE.json" "$output_dir/appsettings.$PROFILE.json"
expect_source "$PROFILE_SENTINEL" "$(resolved_source copied output - --environment "$PROFILE")" \
  "the same file does take effect once it is where the running process looks"

log "PASS: every configuration layer won in its turn, and the appsettings layers were read from the content root"
