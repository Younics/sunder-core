#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 5 ]]; then
  printf 'Usage: upload-production-acceptance.sh <release-id> <tag> <run-id> <run-attempt> <evidence-directory>\n' >&2
  exit 2
fi

release_id="$1"
tag="$2"
run_id="$3"
run_attempt="$4"
evidence_directory="$5"
component="${tag%%/*}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
uploader="$script_dir/upload-github-release-assets.sh"

[[ "$release_id" =~ ^[1-9][0-9]*$ && "$run_id" =~ ^[1-9][0-9]*$ && "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release, run, and attempt coordinates must be positive decimal integers.\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected production acceptance tag: %s\n' "$tag" >&2; exit 2; }
[[ -d "$evidence_directory" && ! -L "$evidence_directory" ]] \
  || { printf 'Production acceptance evidence must be a real directory.\n' >&2; exit 1; }
[[ -f "$uploader" && ! -L "$uploader" ]] \
  || { printf 'GitHub release asset uploader is unavailable.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
: "${GH_TOKEN:?GH_TOKEN is required to upload production acceptance.}"
[[ "$GH_REPO" == 'Younics/sunder-core' ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }

for required_command in cp find jq mktemp sort; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to upload production acceptance.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to upload production acceptance.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

decision="$evidence_directory/production-acceptance.json"
manifest="$evidence_directory/production-acceptance-manifest.json"
[[ -f "$decision" && ! -L "$decision" && -f "$manifest" && ! -L "$manifest" ]] \
  || { printf 'Production acceptance decision or manifest is missing.\n' >&2; exit 1; }
jq -e \
  --arg component "$component" \
  --arg repository "$GH_REPO" \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" '
    .schemaVersion == 1
    and .kind == "https://sunderapp.io/acceptance/production/v1"
    and .decision == "accepted"
    and .release.repository == $repository
    and .release.component == $component
    and .release.releaseId == $release_id
    and .release.tag == $tag
    and (.release.sourceSha | test("^[0-9a-f]{40}$"))
    and (.release.publicationRunId | test("^[1-9][0-9]*$"))
    and (.release.publicationRunAttempt | test("^[1-9][0-9]*$"))
    and .invocation.repository == $repository
    and .invocation.workflowPath == ".github/workflows/sunder-production-acceptance.yml"
    and .invocation.workflowRef == "refs/heads/main"
    and (.invocation.workflowSha | test("^[0-9a-f]{40}$"))
    and (.invocation.coordinatorSha256 | test("^[0-9a-f]{64}$"))
    and .invocation.runId == $run_id
    and .invocation.runAttempt == $run_attempt
  ' "$decision" >/dev/null \
  || { printf 'Production acceptance decision is not bound to this release and workflow attempt.\n' >&2; exit 1; }
jq -e '
  .schemaVersion == 1
  and (.files | type == "array" and length >= 2 and length <= 998)
  and ([.files[].path] | length == (unique | length))
  and all(.files[];
    (.path | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
    and .path != "production-acceptance-manifest.json"
    and (.sha256 | test("^[0-9a-f]{64}$"))
    and (.size | type == "number" and floor == . and . >= 0))
  and ([.files[] | select(.path == "production-acceptance.json")] | length == 1)
' "$manifest" >/dev/null \
  || { printf 'Production acceptance manifest is invalid.\n' >&2; exit 1; }

unexpected_entry="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
[[ -z "$unexpected_entry" ]] \
  || { printf 'Production acceptance evidence contains a non-regular entry.\n' >&2; exit 1; }
expected_count="$(( $(jq '.files | length' "$manifest") + 1 ))"
actual_count="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f | wc -l | tr -d '[:space:]')"
[[ "$actual_count" == "$expected_count" ]] \
  || { printf 'Production acceptance evidence contains unmanifested or missing files.\n' >&2; exit 1; }
while IFS=$'\t' read -r logical_name expected_sha expected_size; do
  path="$evidence_directory/$logical_name"
  [[ -f "$path" && ! -L "$path" \
      && "$(hash_file "$path")" == "$expected_sha" \
      && "$(wc -c < "$path" | tr -d '[:space:]')" == "$expected_size" ]] \
    || { printf "Production evidence '%s' does not match its manifest.\n" "$logical_name" >&2; exit 1; }
done < <(jq -r '.files[] | [.path, .sha256, (.size | tostring)] | @tsv' "$manifest")

staging="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-production-acceptance-upload.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
prefix="production-acceptance-$release_id-$run_id-$run_attempt"
files='[]'
upload_paths=()
index=0
while IFS= read -r logical_name; do
  index=$((index + 1))
  asset_name="$(printf '%s-file-%03d' "$prefix" "$index")"
  source_path="$evidence_directory/$logical_name"
  staged_path="$staging/$asset_name"
  cp "$source_path" "$staged_path"
  files="$(jq -cn \
    --argjson files "$files" \
    --arg path "$logical_name" \
    --arg asset_name "$asset_name" \
    --arg sha256 "$(hash_file "$source_path")" \
    --argjson size "$(wc -c < "$source_path" | tr -d '[:space:]')" \
    '$files + [{path:$path,assetName:$asset_name,sha256:$sha256,size:$size}]')"
  upload_paths+=("$staged_path")
done < <(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f -exec basename {} \; | sort)

receipt="$staging/$prefix-receipt.json"
jq -S -n \
  --arg kind 'https://sunderapp.io/acceptance/production-release-assets/v1' \
  --argjson files "$files" \
  --slurpfile decision "$decision" '
    {
      schemaVersion: 1,
      kind: $kind,
      release: $decision[0].release,
      invocation: $decision[0].invocation,
      files: $files
    }
  ' > "$receipt"

# The receipt is uploaded last. Its presence therefore commits a complete attempt,
# while a lost runner can safely retry equal bytes or leave an older attempt intact.
bash "$uploader" "$release_id" "$tag" "${upload_paths[@]}" "$receipt"
printf 'Uploaded immutable production acceptance attempt %s/%s to numeric release %s.\n' \
  "$run_id" "$run_attempt" "$release_id"
