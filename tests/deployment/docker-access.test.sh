#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
mkdir "$test_root/bin"
export DOCKER_ACCESS_TEST_LOG="$test_root/calls"

# No Docker daemon, credentials, or sudo policy is changed by these tests.
cat > "$test_root/bin/docker" <<'MOCK'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >> "$DOCKER_ACCESS_TEST_LOG"
[[ "$DOCKER_ACCESS_TEST_MODE" == direct ]] || exit 1
if [[ "$1" == info ]]; then cat >/dev/null; fi
MOCK
cat > "$test_root/bin/sudo" <<'MOCK'
#!/usr/bin/env bash
printf 'sudo %s\n' "$*" >> "$DOCKER_ACCESS_TEST_LOG"
[[ "$DOCKER_ACCESS_TEST_MODE" == sudo ]] || exit 1
[[ "$1" == -n && "$2" == docker ]] || exit 1
if [[ "$3" == info ]]; then cat >/dev/null; fi
MOCK
chmod +x "$test_root/bin/docker" "$test_root/bin/sudo"
export PATH="$test_root/bin:$PATH"
# shellcheck source=deploy/docker-access.sh
source "$repo_root/deploy/docker-access.sh"

export DOCKER_ACCESS_TEST_MODE=direct
: > "$DOCKER_ACCESS_TEST_LOG"
select_docker_command
"${docker_command[@]}" build --target api .
if grep -q '^sudo ' "$DOCKER_ACCESS_TEST_LOG"; then
  echo 'Direct Docker access must not require sudo.' >&2
  exit 1
fi
grep -Fx 'docker build --target api .' "$DOCKER_ACCESS_TEST_LOG" >/dev/null

export DOCKER_ACCESS_TEST_MODE=sudo
: > "$DOCKER_ACCESS_TEST_LOG"
select_docker_command
"${docker_command[@]}" compose version
grep -Fx 'sudo -n docker compose version' "$DOCKER_ACCESS_TEST_LOG" >/dev/null

export DOCKER_ACCESS_TEST_MODE=denied
if select_docker_command >"$test_root/failure.log" 2>&1; then
  echo 'No Docker access must fail before registry authentication.' >&2
  exit 1
fi
grep -F 'Neither direct Docker access nor passwordless sudo works' "$test_root/failure.log" >/dev/null

# Access probes must not consume the registry token piped into the SSH script.
for mode in direct sudo; do
  export DOCKER_ACCESS_TEST_MODE="$mode"
  {
    select_docker_command
    IFS= read -r remaining_input
    [[ "$remaining_input" == test-input-preserved ]]
  } <<< 'test-input-preserved'
done
echo 'Docker access tests passed: direct, sudo, denied, and preserved stdin.'
