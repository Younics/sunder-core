#!/usr/bin/env bash
set -euo pipefail

[[ $# -ge 3 ]] || {
  printf 'Usage: upload-github-release-assets.sh <release-id> <tag> <file>...\n' >&2
  exit 2
}
release_id="$1"
tag="$2"
shift 2
[[ "$release_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release ID must be a positive decimal integer.\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected release tag: %s\n' "$tag" >&2; exit 2; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }
: "${GH_TOKEN:?GH_TOKEN is required to upload a GitHub release asset.}"

for required_command in curl gh grep jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to upload GitHub release assets.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to upload GitHub release assets.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

seen_names=''
for file in "$@"; do
  [[ -f "$file" && ! -L "$file" ]] \
    || { printf 'Release upload input must be a regular file: %s\n' "$file" >&2; exit 1; }
  asset_name="$(basename "$file")"
  [[ "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
    || { printf 'Unsafe release asset name: %s\n' "$asset_name" >&2; exit 2; }
  if grep -Fqx "$asset_name" <<< "$seen_names"; then
    printf 'Duplicate release upload asset name: %s\n' "$asset_name" >&2
    exit 2
  fi
  seen_names+="$asset_name"$'\n'

  release_json="$(gh api -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/$GH_REPO/releases/$release_id")"
  jq -e --argjson release_id "$release_id" --arg tag "$tag" '
    .id == $release_id and .tag_name == $tag and .draft == true
  ' <<< "$release_json" >/dev/null \
    || { printf 'Release changed identity or left draft state before asset upload.\n' >&2; exit 1; }
  asset_count="$(jq --arg name "$asset_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")"
  if [[ "$asset_count" == 1 ]]; then
    asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
    temporary="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-existing-asset.XXXXXX")"
    if ! gh api -H 'Accept: application/octet-stream' \
      -H 'X-GitHub-Api-Version: 2022-11-28' \
      "repos/$GH_REPO/releases/assets/$asset_id" > "$temporary"; then
      rm -f "$temporary"
      exit 1
    fi
    if [[ "$(wc -c < "$temporary" | tr -d '[:space:]')" != "$(wc -c < "$file" | tr -d '[:space:]')" ]] \
      || [[ "$(hash_file "$temporary")" != "$(hash_file "$file")" ]]; then
      rm -f "$temporary"
      printf "Existing release asset '%s' differs; immutable draft assets are never replaced.\n" "$asset_name" >&2
      exit 1
    fi
    rm -f "$temporary"
    continue
  fi
  [[ "$asset_count" == 0 ]] \
    || { printf "Release contains duplicate asset name '%s'.\n" "$asset_name" >&2; exit 1; }

  encoded_name="$(jq -rn --arg value "$asset_name" '$value | @uri')"
  response="$(curl --fail --silent --show-error \
    --proto '=https' \
    --tlsv1.2 \
    --max-redirs 0 \
    --request POST \
    -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    -H 'Content-Type: application/octet-stream' \
    -H "Authorization: Bearer $GH_TOKEN" \
    --data-binary "@$file" \
    "https://uploads.github.com/repos/$GH_REPO/releases/$release_id/assets?name=$encoded_name")"
  jq -e --arg name "$asset_name" '
    (.id | type == "number" and . > 0)
    and .name == $name
    and .state == "uploaded"
  ' <<< "$response" >/dev/null \
    || { printf "GitHub returned an invalid upload response for '%s'.\n" "$asset_name" >&2; exit 1; }
done
