#!/usr/bin/env bash
set -euo pipefail

[[ $# -ge 3 ]] || {
  printf 'Usage: download-github-release-assets.sh <release-id> <tag> <destination> [asset-name ...]\n' >&2
  exit 2
}
release_id="$1"
tag="$2"
destination="$3"
shift 3
[[ "$release_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release ID must be a positive decimal integer.\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected release tag: %s\n' "$tag" >&2; exit 2; }
[[ -d "$destination" && ! -L "$destination" ]] \
  || { printf 'Release asset destination must be a real directory: %s\n' "$destination" >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }

for required_command in gh grep jq mktemp sort; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to download GitHub release assets.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to download GitHub release assets.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/$release_id")"
jq -e --argjson release_id "$release_id" --arg tag "$tag" '
  .id == $release_id
  and .tag_name == $tag
  and (.assets | type == "array")
  and all(.assets[];
    (.id | type == "number" and . > 0 and floor == .)
    and (.name | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
    and .state == "uploaded"
    and (.size | type == "number" and . >= 0 and floor == .)
    and (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$")))
' <<< "$release_json" >/dev/null \
  || { printf 'Numeric GitHub release state is invalid for asset download.\n' >&2; exit 1; }

asset_names=("$@")
if [[ ${#asset_names[@]} -eq 0 ]]; then
  while IFS= read -r asset_name; do
    asset_names+=("$asset_name")
  done < <(jq -r '.assets[].name' <<< "$release_json" | sort)
fi
[[ ${#asset_names[@]} -gt 0 ]] || { printf 'GitHub release contains no assets.\n' >&2; exit 1; }

seen_names=''
for asset_name in "${asset_names[@]}"; do
  [[ "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
    || { printf 'Unsafe release asset name: %s\n' "$asset_name" >&2; exit 2; }
  if grep -Fqx "$asset_name" <<< "$seen_names"; then
    printf 'Duplicate requested release asset name: %s\n' "$asset_name" >&2
    exit 2
  fi
  seen_names+="$asset_name"$'\n'
  asset_count="$(jq --arg name "$asset_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")"
  [[ "$asset_count" == 1 ]] \
    || { printf "Expected exactly one release asset '%s', found %s.\n" "$asset_name" "$asset_count" >&2; exit 1; }
  asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
  expected_size="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")"
  expected_digest="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .digest' <<< "$release_json")"
  temporary="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-release-asset.XXXXXX")"
  if ! gh api -H 'Accept: application/octet-stream' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/$GH_REPO/releases/assets/$asset_id" > "$temporary"; then
    rm -f "$temporary"
    exit 1
  fi
  actual_size="$(wc -c < "$temporary" | tr -d '[:space:]')"
  actual_digest="sha256:$(hash_file "$temporary" | tr '[:upper:]' '[:lower:]')"
  [[ "$actual_size" == "$expected_size" && "$actual_digest" == "$expected_digest" ]] \
    || { rm -f "$temporary"; printf "Downloaded bytes do not match GitHub metadata for '%s'.\n" "$asset_name" >&2; exit 1; }
  mv "$temporary" "$destination/$asset_name"
done
