#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
mkdir "$test_root/bin"

# Exercise the release check without a server, credentials, or production changes.
cat > "$test_root/bin/curl" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
url=${!#}
case "$url" in
  */api/status)
    if [[ "$SMOKE_TEST_CASE" == invalid-status ]]; then
      printf '<html>API proxy missing</html>'
    else
      printf '{"stage":"%s","source":"Real"}' "$SMOKE_TEST_STAGE"
    fi
    ;;
  */api/orbital-objects\?*)
    if [[ "$SMOKE_TEST_CASE" == catalog-http-error ]]; then exit 22; fi
    if [[ "$SMOKE_TEST_CASE" == invalid-catalog ]]; then
      printf '{"items":null}'
    else
      printf '{"items":[],"totalCount":0,"page":1,"pageSize":1}'
    fi
    ;;
  */api/conjunctions\?*)
    if [[ "$SMOKE_TEST_CASE" == conjunction-http-error ]]; then exit 22; fi
    if [[ "$SMOKE_TEST_CASE" == invalid-conjunction ]]; then
      printf '<html>Route missing</html>'
    else
      printf '{"items":[],"totalCount":0,"page":1,"pageSize":1}'
    fi
    ;;
  */hubs/traffic/negotiate\?*)
    [[ " $* " == *' -X POST '* ]] || exit 1
    if [[ "$SMOKE_TEST_CASE" == invalid-signalr ]]; then
      printf '{"connectionToken":""}'
    else
      printf '{"connectionToken":"test-connection"}'
    fi
    ;;
  */)
    if [[ "$SMOKE_TEST_CASE" == invalid-dashboard ]]; then
      printf '<title>Wrong application</title>'
    else
      printf '<title>Orbital Sentinel</title>'
    fi
    ;;
  *) exit 1 ;;
esac
MOCK
chmod +x "$test_root/bin/curl"
export PATH="$test_root/bin:$PATH"
export SMOKE_TEST_CASE=healthy
export SMOKE_TEST_STAGE=conjunction-screening

# A healthy new release must not fail merely because its descriptive stage changed.
for stage in conjunction-screening catalog-and-propagation future-stage; do
  export SMOKE_TEST_STAGE="$stage"
  bash "$repo_root/deploy/smoke-test.sh" http://test.local > "$test_root/result.log" 2>&1
  grep -F 'SignalR negotiation passed' "$test_root/result.log" >/dev/null
done

expect_failure() {
  export SMOKE_TEST_CASE=$1
  local expected=$2
  if bash "$repo_root/deploy/smoke-test.sh" http://test.local > "$test_root/result.log" 2>&1; then
    printf 'Smoke check should have failed for %s.\n' "$SMOKE_TEST_CASE" >&2
    exit 1
  fi
  grep -F "Smoke test failed: $expected" "$test_root/result.log" >/dev/null
}

expect_failure invalid-dashboard 'dashboard'
expect_failure invalid-status 'API status'
expect_failure invalid-catalog 'catalog/database read'
expect_failure catalog-http-error 'catalog/database read'
expect_failure invalid-conjunction 'conjunction/database read'
expect_failure conjunction-http-error 'conjunction/database read'
expect_failure invalid-signalr 'SignalR negotiation'

echo 'Smoke check tests passed: changing stages, empty catalogs, HTTP errors, invalid responses, and endpoint diagnostics.'
