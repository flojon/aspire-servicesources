#!/usr/bin/env bash
# Manual smoke test for the 'container' ServiceSource: points DemoAppHost's
# container source at a public image, runs the apphost, and verifies that
# ContainerSource's Aspire-managed container (started via AddContainer(),
# with no host port and Aspire/DCP owning port allocation) actually serves
# traffic on the port Aspire allocated for it. Everything is torn down on exit.
#
# Requires: docker (running).
set -euo pipefail

# traefik/whoami needs no arguments and always answers with identifying text,
# unlike hashicorp/http-echo (which requires a -text CLI flag). ContainerSource
# has no mechanism to pass container args, so the image must work with none.
ECHO_IMAGE="traefik/whoami"
ECHO_TAG="latest"
ECHO_PORT="80"
ECHO_TEXT_MARKER="Hostname:"

# Stand-in address for the services this test resolves to the "url" source. The url source
# registers no resource and nothing ever connects, so this is never dialled; `.invalid` is the
# reserved TLD (RFC 2606) that can never resolve, which says outright that it isn't meant to.
UNUSED_URL="http://unused.invalid"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apphost_dir="$repo_root/samples/DemoAppHost"
cache_dir="$repo_root/.smoketest-cache"
mkdir -p "$cache_dir"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

# Once the AppHost has been launched, a failed assertion here usually means the AppHost itself
# exited instead of whatever the assertion was about, and the reason is only in its log. Emit
# the tail on the way out so the cause travels with the failure, rather than sitting in a file
# that a CI runner discards when the job ends.
fail_with_apphost_log() {
  printf '\nFAIL: %s\n' "$*" >&2
  if [[ -s "$cache_dir/apphost.log" ]]; then
    printf '\n--- last 40 lines of apphost.log ---\n' >&2
    tail -n 40 "$cache_dir/apphost.log" >&2
  fi
  exit 1
}

command -v docker >/dev/null 2>&1 || fail "docker is required (and must be running)"
docker info >/dev/null 2>&1 || fail "docker daemon is not reachable"

apphost_pid=""
yaml_backup="$cache_dir/servicesources.yaml.bak"
local_json_backup="$cache_dir/servicesources.local.json.bak"
had_local_json=0

cleanup() {
  log "cleaning up"
  if [[ -n "$apphost_pid" ]] && kill -0 "$apphost_pid" 2>/dev/null; then
    kill "$apphost_pid" 2>/dev/null || true
    wait "$apphost_pid" 2>/dev/null || true
  fi
  # Aspire/DCP doesn't reliably stop containers it started when the apphost is
  # killed rather than shut down cleanly, so sweep any leftover echo containers.
  docker ps -q --filter "ancestor=${ECHO_IMAGE}:${ECHO_TAG}" | xargs -r docker rm -f >/dev/null 2>&1 || true

  cp "$yaml_backup" "$apphost_dir/servicesources.yaml"
  if [[ $had_local_json -eq 1 ]]; then
    cp "$local_json_backup" "$apphost_dir/servicesources.local.json"
  else
    rm -f "$apphost_dir/servicesources.local.json"
  fi
  rm -f "$yaml_backup" "$local_json_backup"
}
trap cleanup EXIT

cp "$apphost_dir/servicesources.yaml" "$yaml_backup"
if [[ -f "$apphost_dir/servicesources.local.json" ]]; then
  had_local_json=1
  cp "$apphost_dir/servicesources.local.json" "$local_json_backup"
fi

log "pointing DemoAppHost's container source at ${ECHO_IMAGE}:${ECHO_TAG}"
# This catalog replaces the sample's own, so it has to declare every service DemoAppHost's
# Program.cs calls AddService for: AddService throws on a name the catalog doesn't carry, and
# that surfaces as the AppHost dying at startup rather than as a config error.
#
# Only `orders` is the subject of this test, so only it gets the echo image. `inventory` and
# `payments` resolve to the "url" source, which registers no resource and starts nothing,
# keeping the run down to the single container being verified — and keeping the sample's real
# entries (a repository clone and a second container image) out of it.
cat > "$apphost_dir/servicesources.yaml" <<EOF
services:
  orders:
    repository: https://github.com/example/orders
    project: SampleService/SampleService.csproj
    container:
      image: ${ECHO_IMAGE}
      port: ${ECHO_PORT}
      defaultTag: ${ECHO_TAG}
  inventory:
    url:
      url: ${UNUSED_URL}
  payments:
    url:
      url: ${UNUSED_URL}
EOF
cat > "$apphost_dir/servicesources.local.json" <<EOF
{
  "services": {
    "orders": { "source": "container" },
    "inventory": { "source": "url" },
    "payments": { "source": "url" }
  }
}
EOF

log "pre-pulling ${ECHO_IMAGE}:${ECHO_TAG}"
# DCP pulls this image itself when it starts the container, so this is not strictly required.
# It is here because an unauthenticated Docker Hub pull is rate-limited per source IP, which
# shared CI egress reaches routinely: pulling up front, with retries, reports that as its own
# failure instead of as a container that mysteriously never appears, and it warms the local
# cache so DCP's pull is a no-op.
pulled=0
for attempt in 1 2 3; do
  if docker pull "${ECHO_IMAGE}:${ECHO_TAG}" >/dev/null 2>&1; then
    pulled=1
    break
  fi
  log "pull attempt ${attempt} failed, retrying"
  sleep 5
done
[[ $pulled -eq 1 ]] || fail "could not pull ${ECHO_IMAGE}:${ECHO_TAG} (Docker Hub rate limit or no network?)"

log "building DemoAppHost"
dotnet build "$apphost_dir/DemoAppHost.csproj" -v quiet

log "running DemoAppHost"
dotnet run --project "$apphost_dir/DemoAppHost.csproj" --no-build \
  > "$cache_dir/apphost.log" 2>&1 &
apphost_pid=$!

log "waiting for Aspire to start the ${ECHO_IMAGE} container"
container_id=""
for _ in $(seq 1 60); do
  container_id="$(docker ps -q --filter "ancestor=${ECHO_IMAGE}:${ECHO_TAG}" | head -n1 || true)"
  [[ -n "$container_id" ]] && break
  sleep 1
done
[[ -n "$container_id" ]] || fail_with_apphost_log "no ${ECHO_IMAGE}:${ECHO_TAG} container appeared"

log "waiting for the container's port to be published"
host_port=""
for _ in $(seq 1 30); do
  mapping="$(docker port "$container_id" "${ECHO_PORT}/tcp" 2>/dev/null | head -n1 || true)"
  if [[ -n "$mapping" ]]; then
    host_port="${mapping##*:}"
    [[ -n "$host_port" ]] && break
  fi
  sleep 1
done
[[ -n "$host_port" ]] || fail_with_apphost_log "container ${container_id} never published port ${ECHO_PORT}"

log "curling the published port ($host_port)"
response="$(curl -fsS --retry 10 --retry-delay 1 --retry-connrefused "http://127.0.0.1:$host_port/")"
[[ "$response" == *"$ECHO_TEXT_MARKER"* ]] || fail "unexpected response: '$response'"

log "PASS: ContainerSource served traffic from the Aspire-managed container correctly"
