#!/usr/bin/env bash
set -euo pipefail

release_id="${1:?Usage: verify-github-release-assets.sh <release-id> <tag> <local-directory> <asset-name>...}"
tag="${2:?Usage: verify-github-release-assets.sh <release-id> <tag> <local-directory> <asset-name>...}"
local_directory="${3:?Usage: verify-github-release-assets.sh <release-id> <tag> <local-directory> <asset-name>...}"
shift 3
[[ "$release_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release ID must be a positive decimal integer.\n' >&2; exit 2; }
[[ $# -gt 0 ]] || { printf 'At least one expected release asset name is required.\n' >&2; exit 2; }
[[ -d "$local_directory" ]] || { printf 'Local release asset directory not found: %s\n' "$local_directory" >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }
expected_draft="${EXPECTED_RELEASE_DRAFT:-true}"
[[ "$expected_draft" == "true" || "$expected_draft" == "false" ]] \
  || { printf 'EXPECTED_RELEASE_DRAFT must be true or false.\n' >&2; exit 2; }

for command_name in cmp find gh grep jq; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required to verify GitHub release assets.\n' "$command_name" >&2; exit 127; }
done

expected_asset_names=("$@")
if [[ -n "${SUNDER_ADDITIONAL_RELEASE_ASSETS:-}" ]]; then
  while IFS= read -r asset_name; do
    [[ -n "$asset_name" ]] || continue
    expected_asset_names+=("$asset_name")
  done <<< "$SUNDER_ADDITIONAL_RELEASE_ASSETS"
fi
seen_expected_names=''
for asset_name in "${expected_asset_names[@]}"; do
  [[ "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
    || { printf 'Unsafe expected release asset name: %s\n' "$asset_name" >&2; exit 1; }
  if grep -Fqx "$asset_name" <<< "$seen_expected_names"; then
    printf 'Duplicate expected release asset name: %s\n' "$asset_name" >&2
    exit 2
  fi
  seen_expected_names+="$asset_name"$'\n'
done
expected_names_json="$(printf '%s\n' "${expected_asset_names[@]}" | jq -R . | jq -s 'sort')"
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

release_endpoint="repos/$GH_REPO/releases/$release_id"
release_json=''
for _ in {1..30}; do
  if release_json="$(gh api -H 'Accept: application/vnd.github+json' "$release_endpoint")" \
    && jq -e --argjson release_id "$release_id" --arg tag "$tag" --argjson expected_names "$expected_names_json" --argjson expected_draft "$expected_draft" '
      .id == $release_id
      and .tag_name == $tag
      and .draft == $expected_draft
      and ([.assets[].name] | sort) == $expected_names
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
done

for asset_name in "$@"; do
  asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
  gh api -H 'Accept: application/octet-stream' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/$GH_REPO/releases/assets/$asset_id" > "$download_directory/$asset_name"
done
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

if [[ -f "$download_directory/SHA256SUMS" \
      || -f "$download_directory/release-provenance.json" \
      || -f "$download_directory/release-sbom.spdx.json" ]]; then
  for evidence_name in SHA256SUMS release-provenance.json release-sbom.spdx.json; do
    [[ -f "$download_directory/$evidence_name" ]] \
      || { printf "Release evidence set is incomplete; missing '%s'.\n" "$evidence_name" >&2; exit 1; }
  done

  jq -e --argjson release_id "$release_id" '
    .schemaVersion == 1
    and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
    and (.invocation.repository | type == "string" and length > 0)
    and (.invocation.workflow | type == "string" and length > 0)
    and (.invocation.workflowRef | type == "string" and length > 0)
    and (.invocation.runId | type == "string" and length > 0)
    and (.invocation.runAttempt | type == "string" and length > 0)
    and .invocation.releaseId == $release_id
    and (.invocation.ref | type == "string" and length > 0)
    and (.invocation.refName | type == "string" and length > 0)
    and (.invocation.sha | type == "string" and length > 0)
    and (.sources | type == "array")
    and (.artifacts | type == "array" and length > 0)
    and ([.artifacts[].path] | length == (unique | length))
    and all(.artifacts[];
      (.path | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
      and (.sha256 | test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and . >= 0 and floor == .))
  ' "$download_directory/release-provenance.json" >/dev/null \
    || { printf 'Release provenance artifact inventory is invalid.\n' >&2; exit 1; }

  provenance_count="$(jq '.artifacts | length' "$download_directory/release-provenance.json")"
  checksum_count="$(grep -c . "$download_directory/SHA256SUMS" || true)"
  [[ "$checksum_count" == "$provenance_count" ]] \
    || { printf 'SHA256SUMS and release provenance inventory counts differ.\n' >&2; exit 1; }

  while read -r expected_digest asset_name extra; do
    [[ "$expected_digest" =~ ^[0-9a-fA-F]{64}$ && "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] \
      || { printf 'Invalid SHA256SUMS entry.\n' >&2; exit 1; }
    [[ -z "${extra:-}" ]] || { printf 'Invalid trailing SHA256SUMS data for %s.\n' "$asset_name" >&2; exit 1; }
    [[ -f "$download_directory/$asset_name" ]] || { printf "SHA256SUMS references missing asset '%s'.\n" "$asset_name" >&2; exit 1; }
    actual_digest="$(hash_file "$download_directory/$asset_name" | tr '[:upper:]' '[:lower:]')"
    expected_digest_lower="$(printf '%s' "$expected_digest" | tr '[:upper:]' '[:lower:]')"
    [[ "$actual_digest" == "$expected_digest_lower" ]] || { printf "SHA256SUMS mismatch for '%s'.\n" "$asset_name" >&2; exit 1; }
    provenance_matches="$(jq --arg name "$asset_name" --arg digest "$expected_digest_lower" \
      '[.artifacts[] | select(.path == $name and .sha256 == $digest)] | length' \
      "$download_directory/release-provenance.json")"
    [[ "$provenance_matches" == 1 ]] \
      || { printf "SHA256SUMS entry for '%s' is not uniquely bound by release provenance.\n" "$asset_name" >&2; exit 1; }
  done < "$download_directory/SHA256SUMS"

  while IFS=$'\t' read -r asset_name expected_digest expected_size; do
    asset_path="$download_directory/$asset_name"
    [[ -f "$asset_path" ]] || { printf "Release provenance references missing asset '%s'.\n" "$asset_name" >&2; exit 1; }
    actual_digest="$(hash_file "$asset_path" | tr '[:upper:]' '[:lower:]')"
    actual_size="$(wc -c < "$asset_path" | tr -d '[:space:]')"
    [[ "$actual_digest" == "$expected_digest" && "$actual_size" == "$expected_size" ]] \
      || { printf "Release provenance digest or size mismatch for '%s'.\n" "$asset_name" >&2; exit 1; }
    checksum_matches="$(grep -Fxc "$expected_digest  $asset_name" "$download_directory/SHA256SUMS" || true)"
    [[ "$checksum_matches" == 1 ]] \
      || { printf "Release provenance asset '%s' is not uniquely listed in SHA256SUMS.\n" "$asset_name" >&2; exit 1; }
  done < <(jq -r '.artifacts[] | [.path, .sha256, (.size | tostring)] | @tsv' "$download_directory/release-provenance.json")

  for asset_name in "$@"; do
    case "$asset_name" in
      SHA256SUMS|release-provenance.json|release-sbom.spdx.json|toolchain.txt) ;;
      *)
        provenance_matches="$(jq --arg name "$asset_name" '[.artifacts[] | select(.path == $name)] | length' \
          "$download_directory/release-provenance.json")"
        [[ "$provenance_matches" == 1 ]] \
          || { printf "Expected release asset '%s' is absent from release provenance.\n" "$asset_name" >&2; exit 1; }
        ;;
    esac
  done

  jq -e --slurpfile provenance "$download_directory/release-provenance.json" '
    . as $sbom
    | .spdxVersion == "SPDX-2.3"
    and .dataLicense == "CC0-1.0"
    and (.files | length) == ($provenance[0].artifacts | length)
    and all($provenance[0].artifacts[];
      . as $artifact
      | [$sbom.files[] | select(
          .fileName == ("./" + $artifact.path)
          and ([.checksums[] | select(.algorithm == "SHA256" and .checksumValue == $artifact.sha256)] | length) == 1
        )] | length == 1)
  ' "$download_directory/release-sbom.spdx.json" >/dev/null \
    || { printf 'Release SBOM does not exactly bind the provenance artifact inventory.\n' >&2; exit 1; }
fi

printf "Verified %s GitHub release asset byte streams, sizes, and SHA-256 digests for '%s'.\n" "$#" "$tag"
