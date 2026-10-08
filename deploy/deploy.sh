#!/usr/bin/env bash
set -euo pipefail
umask 077

image_prefix=${1:?Image prefix is required}
image_tag=${2:?Commit SHA is required}
ghcr_user=${3:?GHCR user is required}
release_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
app_root=$(cd -- "$release_dir/../.." && pwd)

[[ "$image_tag" =~ ^[0-9a-f]{40}$ ]] || { echo 'Expected a full commit SHA.' >&2; exit 1; }
[[ "$release_dir" == "$HOME/apps/orbital-sentinel/releases/$image_tag" ]] || {
  echo 'Unexpected release directory.' >&2
  exit 1
}
[[ "$(uname -m)" == aarch64 ]] || { echo 'Production requires a 64-bit ARM host.' >&2; exit 1; }
for command in curl openssl flock; do
  command -v "$command" >/dev/null || { echo "$command is required." >&2; exit 1; }
done
sudo -n docker compose version
sudo -n docker info >/dev/null

# Serialise manual executions too; no other application is stopped or pruned.
exec 9>"$app_root/deploy.lock"
flock -n 9 || { echo 'Another Orbital Sentinel deployment is running.' >&2; exit 1; }

if [[ ! -e "$app_root/.env" ]]; then
  if sudo -n docker volume inspect orbital-sentinel-production_postgres-data >/dev/null 2>&1; then
    echo 'Existing database found without .env. Restore the saved database password before deploying.' >&2
    exit 1
  fi
  database_password=$(openssl rand -hex 32)
  printf 'POSTGRES_PASSWORD=%s\n' "$database_password" > "$app_root/.env"
  unset database_password
fi
chmod 600 "$app_root/.env"
printf 'IMAGE_PREFIX=%s\nIMAGE_TAG=%s\n' "$image_prefix" "$image_tag" > "$release_dir/images.env"

docker_config=$(mktemp -d)
cleanup() {
  sudo -n rm -rf -- "$docker_config"
}
trap cleanup EXIT
IFS= read -r ghcr_token
printf '%s' "$ghcr_token" | sudo -n docker --config "$docker_config" login ghcr.io -u "$ghcr_user" --password-stdin
unset ghcr_token

compose() {
  sudo -n docker --config "$docker_config" compose \
    --project-name orbital-sentinel-production \
    --env-file "$app_root/.env" --env-file "$release_dir/images.env" \
    -f "$release_dir/compose.production.yaml" "$@"
}

show_failure() {
  echo 'Deployment failed. Data volumes are preserved; inspect the logs before retrying.' >&2
  compose ps --all || true
  compose logs --tail=80 backend workers frontend postgres kafka || true
}

compose config --quiet
# Download everything before touching the running stack.
compose pull

# Start the database first so even a stopped existing database is backed up.
if ! compose up -d --wait --wait-timeout 180 postgres; then
  show_failure
  exit 1
fi
mkdir -p "$app_root/backups"
backup="$app_root/backups/$(date -u +%Y%m%dT%H%M%SZ)-$image_tag.dump"
echo 'Saving a PostgreSQL backup before application startup migrations...'
compose exec -T postgres pg_dump -U orbital -d orbital_sentinel -Fc > "$backup.partial"
mv -- "$backup.partial" "$backup"

echo 'Starting PostgreSQL, Kafka, API, worker, and frontend...'
if ! compose up -d --wait --wait-timeout 300; then
  show_failure
  exit 1
fi
if ! bash "$release_dir/smoke-test.sh" http://127.0.0.1:50190; then
  show_failure
  exit 1
fi

# Keep the last successful configuration for inspection/recovery.
ln -sfn -- "$release_dir" "$app_root/current"
echo 'Deployment verified at 127.0.0.1:50190 for orbital-sentinel.igorcouto.com.'
