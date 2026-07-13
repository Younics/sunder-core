#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?Usage: generate-release-evidence.sh <artifact-directory> [name=sha ...]}"
shift
[[ -d "$artifact_dir" ]] || { printf 'Artifact directory not found: %s\n' "$artifact_dir" >&2; exit 1; }

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
    SHA256SUMS|release-provenance.json|release-sbom.spdx.json|toolchain.txt) ;;
    *) artifacts+=("$file") ;;
  esac
done < <(find "$artifact_dir" -type f -print0 | sort -z)
[[ ${#artifacts[@]} -gt 0 ]] || { printf 'No release artifacts found in %s.\n' "$artifact_dir" >&2; exit 1; }

: > "$artifact_dir/SHA256SUMS"
for file in "${artifacts[@]}"; do
  relative="${file#"$artifact_dir"/}"
  printf '%s  %s\n' "$(hash_file "$file")" "$relative" >> "$artifact_dir/SHA256SUMS"
done

{
  printf 'dotnet-version: %s\n' "$(dotnet --version)"
  printf 'dotnet-sdk: %s\n' "$(dotnet --list-sdks | tr '\n' ';')"
  printf 'runner-os: %s\n' "${RUNNER_OS:-unknown}"
  printf 'runner-arch: %s\n' "${RUNNER_ARCH:-unknown}"
  printf 'image-os: %s\n' "${ImageOS:-unknown}"
} > "$artifact_dir/toolchain.txt"

{
  printf '{"schemaVersion":1,"buildType":"https://sunderapp.io/provenance/github-actions/v1","invocation":{"workflow":"%s","runId":"%s","runAttempt":"%s"},"sources":[' \
    "${GITHUB_WORKFLOW:-local}" "${GITHUB_RUN_ID:-local}" "${GITHUB_RUN_ATTEMPT:-1}"
  separator=''
  for source in "$@"; do
    name="${source%%=*}"
    sha="${source#*=}"
    printf '%s{"name":"%s","sha":"%s"}' "$separator" "$name" "$sha"
    separator=','
  done
  printf '],"artifacts":['
  separator=''
  for file in "${artifacts[@]}"; do
    relative="${file#"$artifact_dir"/}"
    printf '%s{"path":"%s","sha256":"%s","size":%s}' "$separator" "$relative" "$(hash_file "$file")" "$(wc -c < "$file" | tr -d ' ')"
    separator=','
  done
  printf ']}\n'
} > "$artifact_dir/release-provenance.json"

namespace_sha="$(hash_file "$artifact_dir/SHA256SUMS")"
{
  printf '{"spdxVersion":"SPDX-2.3","dataLicense":"CC0-1.0","SPDXID":"SPDXRef-DOCUMENT","name":"Sunder release artifacts","documentNamespace":"https://sunderapp.io/spdx/%s","creationInfo":{"created":"%s","creators":["Tool: sunder-release-evidence-1"]},"files":[' \
    "$namespace_sha" "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  separator=''
  index=0
  for file in "${artifacts[@]}"; do
    relative="${file#"$artifact_dir"/}"
    printf '%s{"fileName":"./%s","SPDXID":"SPDXRef-File-%s","checksums":[{"algorithm":"SHA256","checksumValue":"%s"}]}' \
      "$separator" "$relative" "$index" "$(hash_file "$file")"
    separator=','
    index=$((index + 1))
  done
  printf ']}\n'
} > "$artifact_dir/release-sbom.spdx.json"
