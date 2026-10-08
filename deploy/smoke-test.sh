#!/usr/bin/env bash
set -euo pipefail

base_url=${1:-http://127.0.0.1:50190}
domain=orbital-sentinel.igorcouto.com
http=(curl --fail --silent --show-error --max-time 15 --retry 5 --retry-delay 2 --retry-connrefused -H "Host: $domain")

"${http[@]}" "$base_url/" | grep -F '<title>Orbital Sentinel' >/dev/null
"${http[@]}" "$base_url/api/status" | grep -F 'catalog-and-propagation' >/dev/null
"${http[@]}" "$base_url/api/orbital-objects?page=1&pageSize=1" | grep -F 'items' >/dev/null
"${http[@]}" -X POST "$base_url/hubs/traffic/negotiate?negotiateVersion=1" | grep -F 'connectionToken' >/dev/null
echo 'Dashboard, API/database reads, and SignalR negotiation passed using the public hostname.'
