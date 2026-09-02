#!/usr/bin/env bash
# Manual smoke test for the 'kubernetes' ServiceSource: spins up a kind cluster,
# deploys a trivial echo service, points DemoAppHost's kubernetes source at it,
# and verifies that KubernetesSource's `kubectl port-forward` executable resource
# actually proxies traffic end-to-end. Everything is torn down on exit.
#
# Requires: docker, kubectl. Downloads a pinned `kind` locally if not on PATH.
set -euo pipefail

KIND_VERSION="v0.27.0"
CLUSTER_NAME="servicesources-kubernetessource-smoketest"
ECHO_TEXT="hello from cluster"
UNUSED_URL="http://unused.invalid"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apphost_dir="$repo_root/samples/DemoAppHost"
cache_dir="$repo_root/.smoketest-cache"
mkdir -p "$cache_dir"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || fail "docker is required (and must be running)"
command -v kubectl >/dev/null 2>&1 || fail "kubectl is required"

if command -v kind >/dev/null 2>&1; then
  KIND=kind
else
  mkdir -p "$cache_dir/bin"
  KIND="$cache_dir/bin/kind"
  if [[ ! -x "$KIND" ]]; then
    os="$(uname -s | tr '[:upper:]' '[:lower:]')"
    arch="$(uname -m)"
    case "$arch" in
      x86_64) arch="amd64" ;;
      aarch64|arm64) arch="arm64" ;;
      *) fail "unsupported architecture: $arch" ;;
    esac
    log "kind not found on PATH; downloading kind $KIND_VERSION ($os/$arch) to $KIND"
    curl -fsSL -o "$KIND" "https://kind.sigs.k8s.io/dl/${KIND_VERSION}/kind-${os}-${arch}"
    chmod +x "$KIND"
  fi
fi

apphost_pid=""
cluster_created=""
yaml_backup="$cache_dir/servicesources.yaml.bak"
local_json_backup="$cache_dir/servicesources.local.json.bak"
had_local_json=0

cleanup() {
  log "cleaning up"
  if [[ -n "$apphost_pid" ]] && kill -0 "$apphost_pid" 2>/dev/null; then
    kill "$apphost_pid" 2>/dev/null || true
    wait "$apphost_pid" 2>/dev/null || true
  fi
  pkill -f "kubectl port-forward svc/echo .* --context kind-${CLUSTER_NAME}" 2>/dev/null || true

  if [[ -n "$cluster_created" ]]; then
    "$KIND" delete cluster --name "$CLUSTER_NAME" >/dev/null 2>&1 || true
  fi

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

log "creating kind cluster '$CLUSTER_NAME'"
"$KIND" create cluster --name "$CLUSTER_NAME"
cluster_created=1
kctx="kind-${CLUSTER_NAME}"

log "deploying echo service into the cluster"
kubectl --context "$kctx" apply -f - <<EOF
apiVersion: apps/v1
kind: Deployment
metadata:
  name: echo
  labels:
    app: echo
spec:
  replicas: 1
  selector:
    matchLabels:
      app: echo
  template:
    metadata:
      labels:
        app: echo
    spec:
      containers:
        - name: http-echo
          image: hashicorp/http-echo
          args:
            - "-text=${ECHO_TEXT}"
            - "-listen=:5678"
---
apiVersion: v1
kind: Service
metadata:
  name: echo
spec:
  selector:
    app: echo
  ports:
    - port: 5678
      targetPort: 5678
EOF
kubectl --context "$kctx" rollout status deployment/echo --timeout=120s

log "pointing DemoAppHost's kubernetes source at the echo service"
# This catalog replaces the sample's own, so it has to declare every service DemoAppHost's
# Program.cs calls AddService for: AddService throws on a name the catalog doesn't carry, and
# that surfaces as the AppHost dying at startup rather than as a config error.
#
# Only `orders` is the subject of this test, so only it gets the kubernetes block. `inventory`
# and `payments` resolve to the "url" source, which registers no resource and starts nothing,
# keeping the run down to the single port-forward being verified — and keeping the sample's real
# entries (a repository clone and a container image) out of it.
cat > "$apphost_dir/servicesources.yaml" <<EOF
services:
  orders:
    repository: https://github.com/example/orders
    project: SampleService/SampleService.csproj
    kubernetes:
      service: echo
      port: 5678
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
    "orders": {
      "source": "kubernetes",
      "kubernetes": { "context": "$kctx", "namespace": "default" }
    },
    "inventory": { "source": "url" },
    "payments": { "source": "url" }
  }
}
EOF

log "building DemoAppHost"
dotnet build "$apphost_dir/DemoAppHost.csproj" -v quiet

log "running DemoAppHost"
dotnet run --project "$apphost_dir/DemoAppHost.csproj" --no-build \
  > "$cache_dir/apphost.log" 2>&1 &
apphost_pid=$!

log "waiting for KubernetesSource's kubectl port-forward to come up"
local_port=""
for _ in $(seq 1 60); do
  line="$(pgrep -af "kubectl port-forward svc/echo .* --context $kctx" | head -n1 || true)"
  if [[ -n "$line" ]]; then
    local_port="$(printf '%s' "$line" | grep -oE '[0-9]+:5678' | head -n1 | cut -d: -f1)"
    [[ -n "$local_port" ]] && break
  fi
  sleep 1
done
[[ -n "$local_port" ]] || fail "kubectl port-forward for orders never started (see $cache_dir/apphost.log)"

log "curling the forwarded port ($local_port)"
response="$(curl -fsS --retry 10 --retry-delay 1 --retry-connrefused "http://127.0.0.1:$local_port/")"
[[ "$response" == "$ECHO_TEXT" ]] || fail "unexpected response: '$response'"

log "PASS: KubernetesSource port-forward proxied traffic from the kind cluster correctly"
