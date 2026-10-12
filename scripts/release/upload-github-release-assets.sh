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

for required_command in cmp curl gh grep jq mktemp; do
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

fetch_release() {
  local attempt
  RELEASE_JSON=''
  for attempt in 1 2 3 4; do
    if RELEASE_JSON="$(gh api -H 'Accept: application/vnd.github+json' \
        -H 'X-GitHub-Api-Version: 2022-11-28' \
        "repos/$GH_REPO/releases/$release_id")" \
      && jq -e --argjson release_id "$release_id" --arg tag "$tag" '
        .id == $release_id
        and .tag_name == $tag
        and (.draft | type == "boolean")
        and (.assets | type == "array")
        and ([.assets[].name] | length == (unique | length))
        and ([.assets[].id] | length == (unique | length))
      ' <<< "$RELEASE_JSON" >/dev/null; then
      return 0
    fi
    RELEASE_JSON=''
    [[ "$attempt" -eq 4 ]] || sleep 1
  done
  printf 'Could not read the exact numeric GitHub release destination.\n' >&2
  return 1
}

# Returns 0 for exact immutable bytes, 3 when absent, and 1 for any conflict.
reconcile_asset() {
  local file="$1"
  local asset_name="$2"
  local expected_sha256="$3"
  local expected_size="$4"
  local asset_count
  asset_count="$(jq --arg name "$asset_name" '[.assets[] | select(.name == $name)] | length' <<< "$RELEASE_JSON")"
  if [[ "$asset_count" == 0 ]]; then
    return 3
  fi
  if [[ "$asset_count" != 1 ]]; then
    printf "Release contains duplicate asset name '%s'.\n" "$asset_name" >&2
    return 1
  fi

  local asset_id
  local remote_digest
  local remote_size
  asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$RELEASE_JSON")"
  remote_digest="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .digest' <<< "$RELEASE_JSON")"
  remote_size="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .size' <<< "$RELEASE_JSON")"
  if ! jq -e --arg name "$asset_name" --arg digest "sha256:$expected_sha256" --argjson size "$expected_size" '
      [.assets[] | select(.name == $name)] as $matches
      | ($matches | length) == 1
      and ($matches[0].id | type == "number" and floor == . and . > 0)
      and $matches[0].state == "uploaded"
      and $matches[0].size == $size
      and $matches[0].digest == $digest
    ' <<< "$RELEASE_JSON" >/dev/null; then
    printf "Existing release asset '%s' differs; immutable draft assets are never replaced.\n" "$asset_name" >&2
    return 1
  fi

  local temporary
  temporary="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-existing-asset.XXXXXX")"
  if ! gh api -H 'Accept: application/octet-stream' \
      -H 'X-GitHub-Api-Version: 2022-11-28' \
      "repos/$GH_REPO/releases/assets/$asset_id" > "$temporary"; then
    rm -f "$temporary"
    return 1
  fi
  if [[ "$(wc -c < "$temporary" | tr -d '[:space:]')" != "$remote_size" \
        || "sha256:$(hash_file "$temporary")" != "$remote_digest" ]] \
      || ! cmp -s "$temporary" "$file"; then
    rm -f "$temporary"
    printf "Existing release asset '%s' differs; immutable draft assets are never replaced.\n" "$asset_name" >&2
    return 1
  fi
  rm -f "$temporary"
  return 0
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
  expected_sha256="$(hash_file "$file")"
  expected_size="$(wc -c < "$file" | tr -d '[:space:]')"
  encoded_name="$(jq -rn --arg value "$asset_name" '$value | @uri')"
  uploaded=false
  for attempt in 1 2 3 4; do
    fetch_release
    [[ "$(jq -r '.draft' <<< "$RELEASE_JSON")" == true ]] \
      || { printf 'Release changed identity or left draft state before asset upload.\n' >&2; exit 1; }
    if reconcile_asset "$file" "$asset_name" "$expected_sha256" "$expected_size"; then
      uploaded=true
      break
    else
      reconcile_status=$?
    fi
    [[ "$reconcile_status" == 3 ]] || exit 1

    response_file="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-upload-response.XXXXXX")"
    curl --fail --silent --show-error \
      --proto '=https' \
      --tlsv1.2 \
      --max-redirs 0 \
      --request POST \
      -H 'Accept: application/vnd.github+json' \
      -H 'X-GitHub-Api-Version: 2022-11-28' \
      -H 'Content-Type: application/octet-stream' \
      -H "Authorization: Bearer $GH_TOKEN" \
      --data-binary "@$file" \
      "https://uploads.github.com/repos/$GH_REPO/releases/$release_id/assets?name=$encoded_name" \
      > "$response_file" || true
    rm -f "$response_file"

    for observation in 1 2 3 4; do
      if fetch_release; then
        [[ "$(jq -r '.draft' <<< "$RELEASE_JSON")" == true ]] \
          || { printf 'Release changed identity or left draft state during asset upload.\n' >&2; exit 1; }
        if reconcile_asset "$file" "$asset_name" "$expected_sha256" "$expected_size"; then
          uploaded=true
          break
        else
          reconcile_status=$?
        fi
        [[ "$reconcile_status" == 3 ]] || exit 1
      fi
      [[ "$observation" -eq 4 ]] || sleep 1
    done
    [[ "$uploaded" != true && "$attempt" -lt 4 ]] || break
  done
  [[ "$uploaded" == true ]] \
    || { printf "GitHub asset '%s' did not become durably observable after bounded retries.\n" "$asset_name" >&2; exit 1; }
  [[ "$(hash_file "$file")" == "$expected_sha256" \
      && "$(wc -c < "$file" | tr -d '[:space:]')" == "$expected_size" ]] \
    || { printf "Release upload input changed during publication: '%s'.\n" "$file" >&2; exit 1; }
done
