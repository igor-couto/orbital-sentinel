#!/usr/bin/env bash
set -euo pipefail

base_url=${1:-http://127.0.0.1:50190}
domain=orbital-sentinel.igorcouto.com
http=(curl --fail --silent --show-error --max-time 15 --retry 5 --retry-delay 2 --retry-connrefused -H "Host: $domain")

check_response() {
  local name=$1 path=$2 pattern=$3 response
  shift 3
  printf 'Checking %s...\n' "$name"
  if ! response=$("${http[@]}" "$@" "$base_url$path"); then
    printf 'Smoke test failed: %s request to %s failed.\n' "$name" "$path" >&2
    return 1
  fi
  if ! grep -E "$pattern" <<< "$response" >/dev/null; then
    printf 'Smoke test failed: %s returned an unexpected response from %s.\n' "$name" "$path" >&2
    return 1
  fi
}

check_response 'dashboard' '/' '<title>Orbital Sentinel'
# The stage describes feature maturity, not health. Verify the stable API contract instead.
check_response 'API status' '/api/status' '"source"[[:space:]]*:[[:space:]]*"Real"'
check_response 'catalog/database read' '/api/orbital-objects?page=1&pageSize=1' '"items"[[:space:]]*:[[:space:]]*\['
check_response 'conjunction/database read' '/api/conjunctions?page=1&pageSize=1' '"items"[[:space:]]*:[[:space:]]*\['
check_response 'SignalR negotiation' '/hubs/traffic/negotiate?negotiateVersion=1' '"connectionToken"[[:space:]]*:[[:space:]]*"[^"]+"' -X POST

echo 'Dashboard, catalog/conjunction API reads, and SignalR negotiation passed using the public hostname.'
