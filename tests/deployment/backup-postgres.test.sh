#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT

# Mock Compose, never a real Docker daemon or production database. Binary output
# models pg_dump's archive stream; progress must remain on a separate channel.
compose() {
  printf '%s\n' "$@" > "$test_root/arguments"
  printf 'pg_dump: dumping contents of table orbital_objects\n' >&2
  [[ "$test_case" != empty ]] || return 0
  printf 'PGDMP\000\377\001test archive\n'
  return "$dump_status"
}

# shellcheck source=deploy/backup-postgres.sh
source "$repo_root/deploy/backup-postgres.sh"

test_case=success
dump_status=0
backup="$test_root/success.dump"
backup_postgres "$backup" > "$test_root/stdout" 2> "$test_root/stderr"
printf 'PGDMP\000\377\001test archive\n' > "$test_root/expected.dump"
cmp "$test_root/expected.dump" "$backup"
[[ ! -e "$backup.partial" ]]
grep -F 'PostgreSQL backup complete:' "$test_root/stdout" >/dev/null
grep -F 'pg_dump: dumping contents' "$test_root/stderr" >/dev/null
if grep -q 'pg_dump:' "$test_root/stdout"; then
  echo 'Backup progress must not contaminate archive or normal output.' >&2
  exit 1
fi

# The timeout must execute in PostgreSQL's container, not wrap the Docker client.
cat > "$test_root/expected-arguments" <<'ARGS'
exec
-T
postgres
timeout
-s
TERM
-k
10
1800
pg_dump
--no-password
--verbose
--lock-wait-timeout=60s
-U
orbital
-d
orbital_sentinel
-Fc
ARGS
cmp "$test_root/expected-arguments" "$test_root/arguments"

backup_postgres "$test_root/custom.dump" 42 > "$test_root/stdout" 2> "$test_root/stderr"
[[ "$(sed -n '9p' "$test_root/arguments")" == 42 ]]

# Any dump error, including common timeout/termination exit codes, must remain a
# failure. An existing completed archive cannot be overwritten by partial data.
for dump_status in 1 124 137 143; do
  backup="$test_root/failure-$dump_status.dump"
  printf 'previous completed backup\n' > "$backup"
  cp "$backup" "$test_root/previous.dump"
  if backup_postgres "$backup" > "$test_root/stdout" 2> "$test_root/stderr"; then
    echo "Backup unexpectedly succeeded after dump exit $dump_status." >&2
    exit 1
  else
    status=$?
    [[ "$status" == "$dump_status" ]]
  fi
  cmp "$backup" "$test_root/previous.dump"
  cmp "$backup.partial" "$test_root/expected.dump"
  grep -F "PostgreSQL backup failed (exit $dump_status)" "$test_root/stderr" >/dev/null
  grep -F 'Incomplete archive retained' "$test_root/stderr" >/dev/null
  if grep -q 'backup complete' "$test_root/stdout"; then exit 1; fi
done

test_case=empty
dump_status=0
backup="$test_root/empty.dump"
if backup_postgres "$backup" > "$test_root/stdout" 2> "$test_root/stderr"; then
  echo 'An empty dump must not count as a completed backup.' >&2
  exit 1
fi
[[ ! -e "$backup" && -f "$backup.partial" && ! -s "$backup.partial" ]]
grep -F 'empty archive' "$test_root/stderr" >/dev/null

test_case=success
for invalid_timeout in 0 -1 1.5 invalid '10; echo unsafe'; do
  rm -f -- "$test_root/arguments"
  backup="$test_root/invalid.dump"
  if backup_postgres "$backup" "$invalid_timeout" > "$test_root/stdout" 2> "$test_root/stderr"; then
    echo 'An invalid timeout must fail before executing Compose.' >&2
    exit 1
  fi
  [[ ! -e "$test_root/arguments" && ! -e "$backup.partial" ]]
  grep -F 'Backup timeout must be a positive number' "$test_root/stderr" >/dev/null
done

# Promotion failure must also be observable by the caller; never claim success.
mv() { return 73; }
backup="$test_root/rename-failure.dump"
if backup_postgres "$backup" > "$test_root/stdout" 2> "$test_root/stderr"; then
  echo 'Failed archive promotion must fail the backup.' >&2
  exit 1
else
  [[ "$?" == 73 ]]
fi
[[ ! -e "$backup" && -s "$backup.partial" ]]
if grep -q 'backup complete' "$test_root/stdout"; then exit 1; fi
unset -f mv

# Exercise the installed manual command through the same current -> releases/...
# symlink used in production. All Docker calls are intercepted; no services run.
mkdir "$test_root/bin"
cat > "$test_root/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$BACKUP_TEST_CALLS"
case "$1" in
  info) [[ "$BACKUP_TEST_MODE" != unavailable ]] ;;
  version) exit 1 ;;
  compose)
    # After project, environment and file options, Compose validates configuration
    # before invoking pg_dump. A failed dump must not fail this earlier check.
    if [[ "${10:-}" == config ]]; then exit 0; fi
    [[ "${10:-}" == exec ]] || exit 1
    printf '%s\n' "$@" > "$BACKUP_TEST_ARGUMENTS"
    printf 'pg_dump: dumping contents of table orbital_objects\n' >&2
    printf 'PGDMP\000\377\001test archive\n'
    [[ "$BACKUP_TEST_MODE" != failure ]]
    ;;
  *) echo "Unexpected Docker command: $1" >&2; exit 1 ;;
esac
MOCK
cat > "$test_root/bin/sudo" <<'MOCK'
#!/usr/bin/env bash
exit 1
MOCK
chmod +x "$test_root/bin/docker" "$test_root/bin/sudo"
export PATH="$test_root/bin:$PATH"
export BACKUP_TEST_CALLS="$test_root/docker-calls"
export BACKUP_TEST_ARGUMENTS="$test_root/compose-arguments"
export BACKUP_TEST_MODE=success

prepare_manual_installation() {
  app_root="$test_root/$1/app with spaces"
  release="$app_root/releases/test-release"
  mkdir -p "$release"
  cp "$repo_root/deploy/backup-postgres.sh" "$release/backup-postgres.sh"
  cp "$repo_root/deploy/docker-access.sh" "$release/docker-access.sh"
  touch "$app_root/.env" "$release/images.env" "$release/compose.production.yaml"
  ln -s "$release" "$app_root/current"
  : > "$BACKUP_TEST_CALLS"
  rm -f "$BACKUP_TEST_ARGUMENTS"
}

assert_no_service_changes() {
  if grep -Eq '(^| )(up|start|stop|restart|down|login|pull)( |$)' "$BACKUP_TEST_CALLS"; then
    echo 'A manual backup must not change services or authenticate with a registry.' >&2
    exit 1
  fi
}

prepare_manual_installation success
# An unrelated working directory must not affect release or database resolution.
(cd "$test_root" && bash "$app_root/current/backup-postgres.sh") \
  > "$test_root/manual-stdout" 2> "$test_root/manual-stderr"
archives=("$app_root/backups/"*-manual.dump)
[[ "${#archives[@]}" == 1 && -f "${archives[0]}" ]]
cmp "$test_root/expected.dump" "${archives[0]}"
[[ ! -e "${archives[0]}.partial" ]]
grep -F 'PostgreSQL backup complete:' "$test_root/manual-stdout" >/dev/null
# Exact arguments ensure the real release, application environment and production
# project are selected instead of the invoking directory or the symlink parent.
cat > "$test_root/expected-compose-arguments" <<ARGS
compose
--project-name
orbital-sentinel-production
--env-file
$app_root/.env
--env-file
$release/images.env
-f
$release/compose.production.yaml
exec
-T
postgres
timeout
-s
TERM
-k
10
1800
pg_dump
--no-password
--verbose
--lock-wait-timeout=60s
-U
orbital
-d
orbital_sentinel
-Fc
ARGS
cmp "$test_root/expected-compose-arguments" "$BACKUP_TEST_ARGUMENTS"
assert_no_service_changes

prepare_manual_installation unavailable
export BACKUP_TEST_MODE=unavailable
if bash "$app_root/current/backup-postgres.sh" > "$test_root/manual-stdout" 2> "$test_root/manual-stderr"; then
  echo 'Unavailable Docker must fail the manual backup.' >&2
  exit 1
fi
[[ ! -e "$BACKUP_TEST_ARGUMENTS" ]]
grep -F 'Neither direct Docker access nor passwordless sudo works' "$test_root/manual-stderr" >/dev/null
assert_no_service_changes

prepare_manual_installation failure
export BACKUP_TEST_MODE=failure
if bash "$app_root/current/backup-postgres.sh" > "$test_root/manual-stdout" 2> "$test_root/manual-stderr"; then
  echo 'A failed manual dump must return a failing exit status.' >&2
  exit 1
fi
partials=("$app_root/backups/"*-manual.dump.partial)
[[ "${#partials[@]}" == 1 && -f "${partials[0]}" ]]
[[ ! -e "${partials[0]%.partial}" ]]
grep -F 'PostgreSQL backup failed' "$test_root/manual-stderr" >/dev/null
assert_no_service_changes

prepare_manual_installation locked
export BACKUP_TEST_MODE=success
(
  flock -n 9
  if bash "$app_root/current/backup-postgres.sh" > "$test_root/manual-stdout" 2> "$test_root/manual-stderr"; then
    echo 'A manual backup must not overlap a deployment holding deploy.lock.' >&2
    exit 1
  fi
) 9> "$app_root/deploy.lock"
[[ ! -e "$BACKUP_TEST_ARGUMENTS" ]]
assert_no_service_changes

echo 'PostgreSQL backup tests passed: archive integrity, timeouts, failures, manual symlink invocation, Docker access, shared deployment lock, and no service changes.'
