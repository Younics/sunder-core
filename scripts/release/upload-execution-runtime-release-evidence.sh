#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 5 ]]; then
  printf 'Usage: upload-execution-runtime-release-evidence.sh <release-id> <runtime-tag> <run-id> <run-attempt> <evidence-directory>\n' >&2
  exit 2
fi

release_id="$1"
tag="$2"
run_id="$3"
run_attempt="$4"
evidence_directory="$5"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
uploader="$script_dir/upload-github-release-assets.sh"
downloader="$script_dir/download-github-release-assets.sh"

[[ "$release_id" =~ ^[1-9][0-9]*$ && "$run_id" =~ ^[1-9][0-9]*$ && "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release, run, and attempt coordinates must be positive decimal integers.\n' >&2; exit 2; }
[[ "$tag" =~ ^runtime/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Execution-runtime evidence tag must use the runtime/v* stream.\n' >&2; exit 2; }
[[ -d "$evidence_directory" && ! -L "$evidence_directory" ]] \
  || { printf 'Execution-runtime release evidence must be a real directory.\n' >&2; exit 1; }
[[ -f "$uploader" && ! -L "$uploader" && -f "$downloader" && ! -L "$downloader" ]] \
  || { printf 'GitHub release asset upload helpers are unavailable.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
: "${GH_TOKEN:?GH_TOKEN is required to archive execution-runtime evidence.}"
[[ "$GH_REPO" == 'Younics/sunder-core' ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }

for required_command in cmp cp find gh jq mktemp sort; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to archive execution-runtime evidence.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to archive execution-runtime evidence.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

manifest="$evidence_directory/runtime-release-evidence.v1.json"
[[ -f "$manifest" && ! -L "$manifest" ]] \
  || { printf 'Execution-runtime release evidence manifest is missing.\n' >&2; exit 1; }
expected_paths="$(printf '%s\n' \
  linux-arm64.json linux-x64.json osx-arm64.json osx-x64.json \
  production.lock.json registry-publication.v1.json release-drivers.v1.json release-set.v1.json \
  win-arm64.json win-x64.json | jq -R . | jq -s 'sort')"
jq -e \
  --arg repository "$GH_REPO" \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" \
  --argjson expected_paths "$expected_paths" '
    type == "object"
    and keys == ["destination","files","invocation","kind","nativeOutputReceipts","registryPublication","releaseDrivers","releaseSet","runtimeLock","schemaVersion","source"]
    and .schemaVersion == 1
    and .kind == "https://sunderapp.io/acceptance/runtime-release-evidence/v1"
    and .destination == {repository:$repository,releaseId:$release_id,tag:$tag}
    and .source.repository == $repository
    and (.source.sha | test("^[0-9a-f]{40}$"))
    and .source.artifactSourceCommit == .source.sha
    and (.source.publicationEvidenceCommit | test("^[0-9a-f]{40}$"))
    and .invocation == {
      workflowPath:".github/workflows/sunder-execution-runtime-publication.yml",
      workflowRef:"refs/heads/main",
      workflowSha:.invocation.workflowSha,
      runId:$run_id,
      runAttempt:$run_attempt
    }
    and (.invocation.workflowSha | test("^[0-9a-f]{40}$"))
    and .runtimeLock.path == "production.lock.json"
    and (.runtimeLock.sha256 | test("^[0-9a-f]{64}$"))
    and .releaseDrivers.path == "release-drivers.v1.json"
    and (.releaseDrivers.sha256 | test("^[0-9a-f]{64}$"))
    and (.releaseDrivers.drivers | length) == 6
    and ([.releaseDrivers.drivers[].rid]) == ["win-x64","win-arm64","linux-x64","linux-arm64","osx-x64","osx-arm64"]
    and all(.releaseDrivers.drivers[]; (.sha256 | test("^[0-9a-f]{64}$")))
    and (.nativeOutputReceipts | length) == 6
    and ([.nativeOutputReceipts[].rid]) == ["win-x64","win-arm64","linux-x64","linux-arm64","osx-x64","osx-arm64"]
    and all(.nativeOutputReceipts[];
      .receiptFile == (.rid + ".json")
      and (.receiptSha256 | test("^[0-9a-f]{64}$"))
      and (.releaseDriverSha256 | test("^[0-9a-f]{64}$")))
    and ([.nativeOutputReceipts[] | {rid,sha256:.releaseDriverSha256}]) == .releaseDrivers.drivers
    and .releaseSet.path == "release-set.v1.json"
    and (.releaseSet.sha256 | test("^[0-9a-f]{64}$"))
    and .registryPublication.path == "registry-publication.v1.json"
    and (.registryPublication.sha256 | test("^[0-9a-f]{64}$"))
    and (.files | type == "array" and length == 10)
    and ([.files[].path] | sort) == $expected_paths
    and ([.files[].path] | length) == ([.files[].path] | unique | length)
    and all(.files[];
      (.sha256 | test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . > 0))
  ' "$manifest" >/dev/null \
  || { printf 'Execution-runtime release evidence manifest is invalid or belongs to another destination.\n' >&2; exit 1; }

unexpected_entry="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
[[ -z "$unexpected_entry" ]] \
  || { printf 'Execution-runtime release evidence contains a non-regular entry.\n' >&2; exit 1; }
actual_count="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f | wc -l | tr -d '[:space:]')"
[[ "$actual_count" == 11 ]] \
  || { printf 'Execution-runtime release evidence contains unmanifested or missing files.\n' >&2; exit 1; }
while IFS=$'\t' read -r logical_name expected_sha expected_size; do
  path="$evidence_directory/$logical_name"
  [[ -f "$path" && ! -L "$path" \
      && "$(hash_file "$path")" == "$expected_sha" \
      && "$(wc -c < "$path" | tr -d '[:space:]')" == "$expected_size" ]] \
    || { printf "Execution-runtime evidence '%s' does not match its manifest.\n" "$logical_name" >&2; exit 1; }
done < <(jq -r '.files[] | [.path,.sha256,(.size | tostring)] | @tsv' "$manifest")

lock_sha="$(jq -r '.runtimeLock.sha256' "$manifest")"
drivers_sha="$(jq -r '.releaseDrivers.sha256' "$manifest")"
release_set_sha="$(jq -r '.releaseSet.sha256' "$manifest")"
publication_sha="$(jq -r '.registryPublication.sha256' "$manifest")"
[[ "$(hash_file "$evidence_directory/production.lock.json")" == "$lock_sha" \
    && "$(hash_file "$evidence_directory/release-drivers.v1.json")" == "$drivers_sha" \
    && "$(hash_file "$evidence_directory/release-set.v1.json")" == "$release_set_sha" \
    && "$(hash_file "$evidence_directory/registry-publication.v1.json")" == "$publication_sha" ]] \
  || { printf 'Execution-runtime aggregate evidence hashes changed before upload.\n' >&2; exit 1; }
jq -e --slurpfile evidence "$manifest" '
  .schemaVersion == 1
  and .kind == "https://sunderapp.io/acceptance/runtime-release-drivers/v1"
  and .drivers == $evidence[0].releaseDrivers.drivers
' "$evidence_directory/release-drivers.v1.json" >/dev/null \
  || { printf 'Release-driver identity file differs from durable evidence.\n' >&2; exit 1; }
jq -e --slurpfile evidence "$manifest" '
  .schemaVersion == 1
  and .kind == "https://sunderapp.io/acceptance/runtime-release-set/v1"
  and .runtimeLockSha256 == $evidence[0].runtimeLock.sha256
  and .releaseDriversSha256 == $evidence[0].releaseDrivers.sha256
  and .releaseDrivers == $evidence[0].releaseDrivers.drivers
  and .nativeOutputReceipts == $evidence[0].nativeOutputReceipts
  and (.artifacts | length) == 12
' "$evidence_directory/release-set.v1.json" >/dev/null \
  || { printf 'Release-set receipt differs from durable lock, driver, or native-receipt evidence.\n' >&2; exit 1; }
jq -e --slurpfile evidence "$manifest" '
  .schemaVersion == 1
  and .kind == "https://sunderapp.io/acceptance/runtime-registry-publication/v1"
  and .decision == "published"
  and .source == $evidence[0].source
  and .invocation == $evidence[0].invocation
  and .runtimeLockSha256 == $evidence[0].runtimeLock.sha256
  and .releaseDriversSha256 == $evidence[0].releaseDrivers.sha256
  and .releaseSetSha256 == $evidence[0].releaseSet.sha256
  and .releaseDrivers == $evidence[0].releaseDrivers.drivers
  and .nativeOutputReceipts == $evidence[0].nativeOutputReceipts
  and all(.progress[]; .lifecycleState == "supported" and .readyToPromote == true)
  and (.promotions | length) == 2
  and (.resolutions | length) == 12
' "$evidence_directory/registry-publication.v1.json" >/dev/null \
  || { printf 'Registry publication receipt is not final or does not bind the durable release set.\n' >&2; exit 1; }
while IFS=$'\t' read -r rid receipt_file receipt_sha driver_sha; do
  receipt="$evidence_directory/$receipt_file"
  [[ "$(hash_file "$receipt")" == "$receipt_sha" ]] \
    || { printf "Native receipt hash mismatch for '%s'.\n" "$rid" >&2; exit 1; }
  jq -e --arg rid "$rid" --arg lock_sha "$lock_sha" --arg driver_sha "$driver_sha" '
    .schemaVersion == 1
    and .kind == "https://sunderapp.io/acceptance/runtime-native-output/v1"
    and .runtimeLockSha256 == $lock_sha
    and .rid == $rid
    and .releaseDriverSha256 == $driver_sha
    and (.artifacts | length) == 2
  ' "$receipt" >/dev/null \
    || { printf "Native receipt identity mismatch for '%s'.\n" "$rid" >&2; exit 1; }
done < <(jq -r '.nativeOutputReceipts[] | [.rid,.receiptFile,.receiptSha256,.releaseDriverSha256] | @tsv' "$manifest")

release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/$release_id")"
source_sha="$(jq -r '.source.sha' "$manifest")"
jq -e --argjson release_id "$release_id" --arg tag "$tag" --arg source_sha "$source_sha" '
  .id == $release_id
  and .tag_name == $tag
  and .target_commitish == $source_sha
  and .draft == true
  and (.assets | type == "array")
' <<< "$release_json" >/dev/null \
  || { printf 'Durable execution-runtime evidence destination is not the exact source-bound draft.\n' >&2; exit 1; }

staging="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-runtime-evidence-upload.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
prefix="execution-runtime-release-$release_id-$run_id-$run_attempt"
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
    --argjson files "$files" --arg path "$logical_name" --arg asset_name "$asset_name" \
    --arg sha "$(hash_file "$source_path")" \
    --argjson size "$(wc -c < "$source_path" | tr -d '[:space:]')" \
    '$files + [{path:$path,assetName:$asset_name,sha256:$sha,size:$size}]')"
  upload_paths+=("$staged_path")
done < <(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f -exec basename {} \; | sort)

receipt="$staging/$prefix-receipt.json"
jq -S -n --argjson files "$files" --slurpfile evidence "$manifest" '
  {
    schemaVersion:1,
    kind:"https://sunderapp.io/acceptance/runtime-release-assets/v1",
    destination:$evidence[0].destination,
    source:$evidence[0].source,
    invocation:$evidence[0].invocation,
    files:$files
  }
' > "$receipt"

asset_names=()
while IFS= read -r asset_name; do asset_names+=("$asset_name"); done < <(jq -r '.files[].assetName' "$receipt")
asset_names+=("$(basename "$receipt")")
expected_names="$(printf '%s\n' "${asset_names[@]}" | sort | jq -R . | jq -s .)"
release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/$release_id")"
jq -e --arg prefix "$prefix" --arg receipt "$(basename "$receipt")" --argjson expected "$expected_names" '
  ([.assets[].name | select(startswith($prefix + "-"))] | sort) as $existing
  | ($existing - $expected | length) == 0
  and (($existing | index($receipt)) == null or $existing == $expected)
' <<< "$release_json" >/dev/null \
  || { printf 'Existing execution-runtime evidence prefix is unexpected or falsely marked complete.\n' >&2; exit 1; }

# The receipt is the completion marker and is always uploaded after its files.
bash "$uploader" "$release_id" "$tag" "${upload_paths[@]}" "$receipt"

verification="$staging/verified"
mkdir "$verification"
bash "$downloader" "$release_id" "$tag" "$verification" "${asset_names[@]}"
for upload_path in "${upload_paths[@]}" "$receipt"; do
  cmp "$upload_path" "$verification/$(basename "$upload_path")" \
    || { printf 'Durable execution-runtime evidence differs after upload.\n' >&2; exit 1; }
done

release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/$release_id")"
jq -e --arg prefix "$prefix" --argjson expected "$expected_names" '
  ([.assets[].name | select(startswith($prefix + "-"))] | sort) == $expected
' <<< "$release_json" >/dev/null \
  || { printf 'Durable execution-runtime evidence attempt is partial or has unexpected assets.\n' >&2; exit 1; }

printf 'Archived and exact-byte verified execution-runtime release evidence on numeric release %s, run %s attempt %s.\n' \
  "$release_id" "$run_id" "$run_attempt"
