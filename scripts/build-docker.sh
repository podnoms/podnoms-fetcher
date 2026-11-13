#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/build-docker.sh [TAG] [--dry-run]

Build the current checkout and push it directly, without the GitHub Actions
workflow's tests. TAG defaults to latest. A custom tag is
pushed on its own, without updating latest.

Environment:
  IMAGE_NAME  Repository without a tag (default: ghcr.io/podnoms/podnoms-fetcher)
  PLATFORM    Build platform (default: linux/amd64, matching GitHub Actions)

Authenticate first with: docker login ghcr.io -u YOUR_GITHUB_USERNAME
Use a personal access token (classic) with write:packages as the password.

Examples:
  scripts/build-docker.sh --dry-run
  scripts/build-docker.sh
  scripts/build-docker.sh v0.1.0
EOF
}

fail() { echo "Error: $*" >&2; exit 1; }

tag=
dry_run=false
for argument in "$@"; do
  case "$argument" in
    --help|-h) usage; exit 0 ;;
    --dry-run) dry_run=true ;;
    -*) fail "Unknown option: $argument. Use --help for usage." ;;
    *)
      [[ -z "$tag" ]] || fail 'Supply only one image tag.'
      [[ "$argument" =~ ^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$ ]] ||
        fail "Invalid Docker tag: $argument."
      tag=$argument ;;
  esac
done
tag=${tag:-latest}

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
image="${IMAGE_NAME:-ghcr.io/podnoms/podnoms-fetcher}:$tag"
platform=${PLATFORM:-linux/amd64}

# Keep the local builder's cache between runs. The workflow's gha cache is CI-only.
# The source label links the package to its GitHub repository.
build=(docker buildx build
  --file "$repo_root/Dockerfile"
  --platform "$platform"
  --tag "$image"
  --label 'org.opencontainers.image.source=https://github.com/podnoms/podnoms-fetcher'
  --push
  "$repo_root")

printf 'Image: %s\nPlatform: %s\nContext: %s\n' "$image" "$platform" "$repo_root"
echo 'Builds local changes allowed by .dockerignore; skips the tests.'
if "$dry_run"; then
  printf 'Would run:'
  printf ' %q' "${build[@]}"
  printf '\n'
  exit 0
fi

command -v docker >/dev/null 2>&1 || fail 'Install Docker with the Buildx plugin first.'
docker buildx version >/dev/null 2>&1 || fail 'Install the Docker Buildx plugin first.'
docker info >/dev/null 2>&1 || fail 'Docker is unavailable. Start Docker and check your access to the daemon.'

"${build[@]}"
printf 'Published %s\n' "$image"
