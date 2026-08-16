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

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apphost_dir="$repo_root/samples/DemoAppHost"
cache_dir="$repo_root/.smoketest-cache"
mkdir -p "$cache_dir"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

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
cat > "$apphost_dir/servicesources.yaml" <<EOF
services:
  orders:
    repository: https://github.com/example/orders
    project: SampleService/SampleService.csproj
    container:
      image: ${ECHO_IMAGE}
      port: ${ECHO_PORT}
      defaultTag: ${ECHO_TAG}
EOF
cat > "$apphost_dir/servicesources.local.json" <<EOF
{
  "services": {
    "orders": { "source": "container" }
  }
}
EOF

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
[[ -n "$container_id" ]] || fail "no ${ECHO_IMAGE}:${ECHO_TAG} container appeared (see $cache_dir/apphost.log)"

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
[[ -n "$host_port" ]] || fail "container ${container_id} never published port ${ECHO_PORT} (see $cache_dir/apphost.log)"

log "curling the published port ($host_port)"
response="$(curl -fsS --retry 10 --retry-delay 1 --retry-connrefused "http://127.0.0.1:$host_port/")"
[[ "$response" == *"$ECHO_TEXT_MARKER"* ]] || fail "unexpected response: '$response'"

log "PASS: ContainerSource served traffic from the Aspire-managed container correctly"
