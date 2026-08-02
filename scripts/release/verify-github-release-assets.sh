#!/usr/bin/env bash
set -euo pipefail

tag="${1:?Usage: verify-github-release-assets.sh <tag> <local-directory> <asset-name>...}"
local_directory="${2:?Usage: verify-github-release-assets.sh <tag> <local-directory> <asset-name>...}"
shift 2
[[ $# -gt 0 ]] || { printf 'At least one expected release asset name is required.\n' >&2; exit 2; }
[[ -d "$local_directory" ]] || { printf 'Local release asset directory not found: %s\n' "$local_directory" >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
expected_draft="${EXPECTED_RELEASE_DRAFT:-true}"
[[ "$expected_draft" == "true" || "$expected_draft" == "false" ]] \
  || { printf 'EXPECTED_RELEASE_DRAFT must be true or false.\n' >&2; exit 2; }

for command_name in cmp find gh jq; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required to verify GitHub release assets.\n' "$command_name" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify GitHub release assets.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

encoded_tag="$(jq -rn --arg value "$tag" '$value | @uri')"
release_endpoint="repos/$GH_REPO/releases/tags/$encoded_tag"
release_json=''
for _ in {1..30}; do
  release_json="$(gh api -H 'Accept: application/vnd.github+json' "$release_endpoint")"
  if jq -e --argjson count "$#" --argjson expected_draft "$expected_draft" '
      .draft == $expected_draft
      and (.assets | length) == $count
      and all(.assets[]; .state == "uploaded" and (.digest | type == "string" and startswith("sha256:")))
    ' <<< "$release_json" >/dev/null; then
    break
  fi
  release_json=''
  sleep 2
done
[[ -n "$release_json" ]] || { printf "GitHub did not expose a complete digested release for '%s'.\n" "$tag" >&2; exit 1; }

download_directory="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-github-release.XXXXXX")"
trap 'rm -rf "$download_directory"' EXIT
download_args=(release download "$tag" --repo "$GH_REPO" --dir "$download_directory" --clobber)

for asset_name in "$@"; do
  [[ "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
    || { printf 'Unsafe expected release asset name: %s\n' "$asset_name" >&2; exit 1; }
  local_matches="$(find "$local_directory" -type f -name "$asset_name" -print)"
  [[ "$(printf '%s\n' "$local_matches" | grep -c . || true)" == 1 ]] || {
    printf "Expected exactly one local '%s' under '%s'.\n" "$asset_name" "$local_directory" >&2
    exit 1
  }
  asset_count="$(jq --arg name "$asset_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")"
  [[ "$asset_count" == 1 ]] || { printf "Expected exactly one remote '%s', found %s.\n" "$asset_name" "$asset_count" >&2; exit 1; }

  local_size="$(wc -c < "$local_matches" | tr -d '[:space:]')"
  local_digest="sha256:$(hash_file "$local_matches" | tr '[:upper:]' '[:lower:]')"
  remote_size="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")"
  remote_digest="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .digest' <<< "$release_json")"
  [[ "$remote_size" == "$local_size" ]] || { printf "GitHub size mismatch for '%s'.\n" "$asset_name" >&2; exit 1; }
  [[ "$remote_digest" == "$local_digest" ]] || { printf "GitHub digest mismatch for '%s'.\n" "$asset_name" >&2; exit 1; }
  download_args+=(--pattern "$asset_name")
done

gh "${download_args[@]}"
download_count="$(find "$download_directory" -maxdepth 1 -type f | wc -l | tr -d '[:space:]')"
[[ "$download_count" == "$#" ]] || { printf 'Expected %s downloaded release assets, found %s.\n' "$#" "$download_count" >&2; exit 1; }

for asset_name in "$@"; do
  local_path="$(find "$local_directory" -type f -name "$asset_name" -print)"
  remote_path="$download_directory/$asset_name"
  [[ -f "$remote_path" ]] || { printf 'Downloaded GitHub asset is missing: %s\n' "$asset_name" >&2; exit 1; }
  cmp -s "$local_path" "$remote_path" || { printf "Downloaded GitHub bytes differ for '%s'.\n" "$asset_name" >&2; exit 1; }
  remote_digest="sha256:$(hash_file "$remote_path" | tr '[:upper:]' '[:lower:]')"
  api_digest="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .digest' <<< "$release_json")"
  [[ "$remote_digest" == "$api_digest" ]] || { printf "Downloaded GitHub digest differs for '%s'.\n" "$asset_name" >&2; exit 1; }
done

if [[ -f "$download_directory/SHA256SUMS" ]]; then
  while read -r expected_digest asset_name; do
    [[ "$expected_digest" =~ ^[0-9a-fA-F]{64}$ && "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
      || { printf 'Invalid SHA256SUMS entry.\n' >&2; exit 1; }
    [[ -f "$download_directory/$asset_name" ]] || { printf "SHA256SUMS references missing asset '%s'.\n" "$asset_name" >&2; exit 1; }
    actual_digest="$(hash_file "$download_directory/$asset_name" | tr '[:upper:]' '[:lower:]')"
    [[ "$actual_digest" == "${expected_digest,,}" ]] || { printf "SHA256SUMS mismatch for '%s'.\n" "$asset_name" >&2; exit 1; }
  done < "$download_directory/SHA256SUMS"
fi

printf "Verified %s GitHub release asset byte streams, sizes, and SHA-256 digests for '%s'.\n" "$#" "$tag"
