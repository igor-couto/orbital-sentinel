#!/usr/bin/env bash
# Source this file, then call select_docker_command before authenticating.
# The caller uses the selected array for all subsequent Docker commands.
# shellcheck disable=SC2034
select_docker_command() {
  docker_command=(docker)
  if docker info </dev/null >/dev/null 2>&1; then
    printf 'Using Docker directly as %s.\n' "$(id -un)"
    return 0
  fi

  if command -v sudo >/dev/null && sudo -n docker info </dev/null >/dev/null 2>&1; then
    docker_command=(sudo -n docker)
    printf 'Using Docker through passwordless sudo as %s.\n' "$(id -un)"
    return 0
  fi

  printf 'Docker is unavailable to this job account:\n' >&2
  id >&2
  printf 'Neither direct Docker access nor passwordless sudo works. Check Docker is running and this account has Docker access, then restart its runner service after any group change.\n' >&2
  docker version >&2 || true
  return 1
}
