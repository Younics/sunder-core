#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?Usage: generate-release-evidence.sh <artifact-directory> [name=sha ...]}"
shift
[[ -d "$artifact_dir" ]] || { printf 'Artifact directory not found: %s\n' "$artifact_dir" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { printf 'jq is required to generate release evidence.\n' >&2; exit 127; }

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

artifacts=()
while IFS= read -r -d '' file; do
  case "$(basename "$file")" in
    SHA256SUMS|release-provenance.json|release-sbom.spdx.json|toolchain.txt|assets.*.json|RELEASES-*) ;;
    *) artifacts+=("$file") ;;
  esac
done < <(find "$artifact_dir" -type f -print0 | sort -z)
[[ ${#artifacts[@]} -gt 0 ]] || { printf 'No release artifacts found in %s.\n' "$artifact_dir" >&2; exit 1; }

artifact_names=()
artifacts_json='[]'
for file in "${artifacts[@]}"; do
  name="$(basename "$file")"
  if [[ ! "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]]; then
    printf 'Release artifact name is not safe for a flat asset manifest: %s\n' "$name" >&2
    exit 1
  fi
  for existing in "${artifact_names[@]-}"; do
    [[ "$existing" != "$name" ]] || {
      printf 'Duplicate flat release asset name: %s\n' "$name" >&2
      exit 1
    }
  done
  artifact_names+=("$name")

  sha256="$(hash_file "$file" | tr '[:upper:]' '[:lower:]')"
  [[ "$sha256" =~ ^[0-9a-f]{64}$ ]] || { printf 'Could not calculate SHA-256 for %s.\n' "$name" >&2; exit 1; }
  size="$(wc -c < "$file" | tr -d '[:space:]')"
  [[ "$size" =~ ^[0-9]+$ ]] || { printf 'Could not calculate size for %s.\n' "$name" >&2; exit 1; }
  artifacts_json="$(jq -cn \
    --argjson artifacts "$artifacts_json" \
    --arg path "$name" \
    --arg sha256 "$sha256" \
    --argjson size "$size" \
    '$artifacts + [{path: $path, sha256: $sha256, size: $size}]')"
done

sources_json='[]'
source_names=()
for source in "$@"; do
  if [[ ! "$source" =~ ^([A-Za-z0-9][A-Za-z0-9._-]*)=([0-9a-fA-F]{40})$ ]]; then
    printf 'Source must use name=<40-character Git SHA>: %s\n' "$source" >&2
    exit 2
  fi
  source_name="${BASH_REMATCH[1]}"
  source_sha="$(printf '%s' "${BASH_REMATCH[2]}" | tr '[:upper:]' '[:lower:]')"
  for existing in "${source_names[@]-}"; do
    [[ "$existing" != "$source_name" ]] || {
      printf 'Duplicate release evidence source name: %s\n' "$source_name" >&2
      exit 2
    }
  done
  source_names+=("$source_name")
  sources_json="$(jq -cn \
    --argjson sources "$sources_json" \
    --arg name "$source_name" \
    --arg sha "$source_sha" \
    '$sources + [{name: $name, sha: $sha}]')"
done

: > "$artifact_dir/SHA256SUMS"
for file in "${artifacts[@]}"; do
  name="$(basename "$file")"
  printf '%s  %s\n' "$(hash_file "$file")" "$name" >> "$artifact_dir/SHA256SUMS"
done

{
  printf 'dotnet-version: %s\n' "$(dotnet --version)"
  printf 'dotnet-sdk: %s\n' "$(dotnet --list-sdks | tr '\n' ';')"
  printf 'runner-os: %s\n' "${RUNNER_OS:-unknown}"
  printf 'runner-arch: %s\n' "${RUNNER_ARCH:-unknown}"
  printf 'image-os: %s\n' "${ImageOS:-unknown}"
} > "$artifact_dir/toolchain.txt"

jq -n \
  --arg workflow "${GITHUB_WORKFLOW:-local}" \
  --arg run_id "${GITHUB_RUN_ID:-local}" \
  --arg run_attempt "${GITHUB_RUN_ATTEMPT:-1}" \
  --argjson sources "$sources_json" \
  --argjson artifacts "$artifacts_json" \
  '{
    schemaVersion: 1,
    buildType: "https://sunderapp.io/provenance/github-actions/v1",
    invocation: {workflow: $workflow, runId: $run_id, runAttempt: $run_attempt},
    sources: $sources,
    artifacts: $artifacts
  }' > "$artifact_dir/release-provenance.json"

namespace_sha="$(hash_file "$artifact_dir/SHA256SUMS")"
namespace_sha="$(printf '%s' "$namespace_sha" | tr '[:upper:]' '[:lower:]')"
[[ "$namespace_sha" =~ ^[0-9a-f]{64}$ ]] || { printf 'Could not calculate the evidence namespace hash.\n' >&2; exit 1; }
jq -n \
  --arg namespace_sha "$namespace_sha" \
  --arg created "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" \
  --argjson artifacts "$artifacts_json" \
  '{
    spdxVersion: "SPDX-2.3",
    dataLicense: "CC0-1.0",
    SPDXID: "SPDXRef-DOCUMENT",
    name: "Sunder release artifacts",
    documentNamespace: ("https://sunderapp.io/spdx/" + $namespace_sha),
    creationInfo: {created: $created, creators: ["Tool: sunder-release-evidence-1"]},
    files: [
      $artifacts | to_entries[] | {
        fileName: ("./" + .value.path),
        SPDXID: ("SPDXRef-File-" + (.key | tostring)),
        checksums: [{algorithm: "SHA256", checksumValue: .value.sha256}]
      }
    ]
  }' > "$artifact_dir/release-sbom.spdx.json"
