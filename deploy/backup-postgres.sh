#!/usr/bin/env bash
# Optional manual backup: bash ~/apps/orbital-sentinel/current/backup-postgres.sh
# The helper can also be sourced after defining compose().
backup_postgres() {
  local backup=${1:?Backup path is required}
  local timeout_seconds=${2:-1800}
  local started=$SECONDS status
  [[ "$timeout_seconds" =~ ^[1-9][0-9]*$ ]] || {
    echo 'Backup timeout must be a positive number of seconds.' >&2
    return 1
  }

  printf 'Saving a PostgreSQL backup (limit: %ss)...\n' "$timeout_seconds"
  # Run the deadline inside the container: timing out only the Docker client can
  # leave pg_dump running. Verbose progress goes to stderr, never into the archive.
  if compose exec -T postgres timeout -s TERM -k 10 "$timeout_seconds" \
    pg_dump --no-password --verbose --lock-wait-timeout=60s \
    -U orbital -d orbital_sentinel -Fc </dev/null > "$backup.partial"; then
    if [[ ! -s "$backup.partial" ]]; then
      echo 'PostgreSQL backup returned an empty archive. Backup not completed.' >&2
      return 1
    fi
    mv -- "$backup.partial" "$backup" || return
    printf 'PostgreSQL backup complete: %s bytes in %ss (%s).\n' \
      "$(wc -c < "$backup")" "$((SECONDS - started))" "$backup"
  else
    status=$?
    printf 'PostgreSQL backup failed (exit %s) after %ss; deadline was %ss.\n' \
      "$status" "$((SECONDS - started))" "$timeout_seconds" >&2
    printf 'Incomplete archive retained at %s.partial; do not use it for recovery.\n' "$backup" >&2
    return "$status"
  fi
}

manual_backup() (
  set -euo pipefail
  umask 077

  # Resolve the physical release directory when invoked through the current symlink.
  release_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
  app_root=$(cd -- "$release_dir/../.." && pwd -P)
  # shellcheck source=deploy/docker-access.sh
  source "$release_dir/docker-access.sh"
  select_docker_command

  # Do not overlap a manual backup with a deployment or another backup.
  exec 9>"$app_root/deploy.lock"
  flock -n 9 || { echo 'Another deployment or manual backup is running.' >&2; exit 1; }

  compose() {
    "${docker_command[@]}" compose \
      --project-name orbital-sentinel-production \
      --env-file "$app_root/.env" --env-file "$release_dir/images.env" \
      -f "$release_dir/compose.production.yaml" "$@"
  }

  compose config --quiet
  mkdir -p "$app_root/backups"
  backup="$app_root/backups/$(date -u +%Y%m%dT%H%M%S%NZ)-manual.dump"
  backup_postgres "$backup"
)

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  manual_backup "$@"
fi
