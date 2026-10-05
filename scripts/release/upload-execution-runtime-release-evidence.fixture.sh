#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
archiver="$script_dir/upload-execution-runtime-release-evidence.sh"
[[ -f "$archiver" ]] || { printf 'Execution-runtime release evidence archiver not found.\n' >&2; exit 1; }
for required_command in cmp jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the runtime evidence upload fixture.\n' "$required_command" >&2; exit 127; }
done

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

hash_text() {
  if command -v sha256sum >/dev/null 2>&1; then printf '%s' "$1" | sha256sum | cut -d ' ' -f 1
  else printf '%s' "$1" | shasum -a 256 | cut -d ' ' -f 1; fi
}

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-runtime-evidence-upload-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
evidence="$fixture_dir/evidence"
mock_bin="$fixture_dir/mock-bin"
asset_state="$fixture_dir/assets"
mkdir -p "$evidence" "$mock_bin" "$asset_state"

release_id=4242
tag='runtime/v1.0.0'
run_id=5001
run_attempt=2
source_sha='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
workflow_sha='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
printf '{"schemaVersion":1,"lockKind":"production"}\n' > "$evidence/production.lock.json"
lock_sha="$(hash_file "$evidence/production.lock.json")"
drivers='[]'
artifacts='[]'
native_receipts='[]'
rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
for rid in "${rids[@]}"; do
  driver_sha="$(hash_text "reviewed-driver/$rid")"
  drivers="$(jq -cn --argjson drivers "$drivers" --arg rid "$rid" --arg sha "$driver_sha" \
    '$drivers + [{rid:$rid,sha256:$sha}]')"
  lane_artifacts='[]'
  for runtime_id in sunder.runtime.dotnet sunder.runtime.node; do
    archive_sha="$(hash_text "$runtime_id/archive/$rid")"
    artifact="$(jq -cn \
      --arg runtime_id "$runtime_id" --arg rid "$rid" --arg archive_sha "$archive_sha" '
        {
          runtimeId:$runtime_id,version:"1.0.0",packagingRevision:1,rid:$rid,
          moduleAbi:($runtime_id + ".abi.v1"),workerProtocol:"sunder.worker.v1",
          minimumHostTrustGeneration:1,releaseSigningKeyId:"release-key",
          nativeAttestationKeyId:"attestation-key",
          archiveFile:($runtime_id + "-" + $rid + ".sunderruntime"),
          archiveSha256:$archive_sha,archiveSize:100,manifestSha256:("c" * 64),
          contentIdentity:("d" * 64),nativeAttestationSha256:("e" * 64),
          releaseSignatureVerified:true,nativeAttestationVerified:true
        }
      ')"
    lane_artifacts="$(jq -cn --argjson values "$lane_artifacts" --argjson artifact "$artifact" '$values + [$artifact]')"
    artifacts="$(jq -cn --argjson values "$artifacts" --argjson artifact "$artifact" '$values + [$artifact]')"
  done
  jq -n --arg lock_sha "$lock_sha" --arg rid "$rid" --arg driver_sha "$driver_sha" \
    --argjson artifacts "$lane_artifacts" '
      {
        schemaVersion:1,
        kind:"https://sunderapp.io/acceptance/runtime-native-output/v1",
        runtimeLockSha256:$lock_sha,
        rid:$rid,
        releaseDriverSha256:$driver_sha,
        artifacts:$artifacts
      }
    ' > "$evidence/$rid.json"
  native_receipts="$(jq -cn \
    --argjson receipts "$native_receipts" --arg rid "$rid" --arg driver_sha "$driver_sha" \
    --arg receipt_file "$rid.json" --arg receipt_sha "$(hash_file "$evidence/$rid.json")" \
    '$receipts + [{rid:$rid,releaseDriverSha256:$driver_sha,receiptFile:$receipt_file,receiptSha256:$receipt_sha}]')"
done
jq -n --argjson drivers "$drivers" '{
  schemaVersion:1,
  kind:"https://sunderapp.io/acceptance/runtime-release-drivers/v1",
  drivers:$drivers
}' > "$evidence/release-drivers.v1.json"
drivers_sha="$(hash_file "$evidence/release-drivers.v1.json")"
jq -n \
  --arg lock_sha "$lock_sha" --arg drivers_sha "$drivers_sha" \
  --argjson drivers "$drivers" --argjson native_receipts "$native_receipts" --argjson artifacts "$artifacts" '
    {
      schemaVersion:1,
      kind:"https://sunderapp.io/acceptance/runtime-release-set/v1",
      runtimeLockSha256:$lock_sha,
      releaseDriversSha256:$drivers_sha,
      rids:["win-x64","win-arm64","linux-x64","linux-arm64","osx-x64","osx-arm64"],
      releaseDrivers:$drivers,
      nativeOutputReceipts:$native_receipts,
      artifacts:$artifacts
    }
  ' > "$evidence/release-set.v1.json"
release_set_sha="$(hash_file "$evidence/release-set.v1.json")"
jq -n \
  --arg source_sha "$source_sha" --arg workflow_sha "$workflow_sha" \
  --arg run_id "$run_id" --arg run_attempt "$run_attempt" \
  --arg lock_sha "$lock_sha" --arg drivers_sha "$drivers_sha" --arg release_set_sha "$release_set_sha" \
  --argjson drivers "$drivers" --argjson native_receipts "$native_receipts" --argjson artifacts "$artifacts" '
    {
      schemaVersion:1,
      kind:"https://sunderapp.io/acceptance/runtime-registry-publication/v1",
      decision:"published",
      registryOrigin:"https://registry.fixture",
      source:{repository:"Younics/sunder-core",sha:$source_sha,artifactSourceCommit:$source_sha,publicationEvidenceCommit:("f" * 40)},
      invocation:{
        workflowPath:".github/workflows/sunder-execution-runtime-publication.yml",
        workflowRef:"refs/heads/main",workflowSha:$workflow_sha,runId:$run_id,runAttempt:$run_attempt
      },
      runtimeLockSha256:$lock_sha,
      releaseDriversSha256:$drivers_sha,
      releaseSetSha256:$release_set_sha,
      releaseDrivers:$drivers,
      nativeOutputReceipts:$native_receipts,
      stages:[],artifacts:$artifacts,
      progress:[
        {runtimeId:"sunder.runtime.dotnet",stageId:("1" * 32),lifecycleState:"supported",publishedRids:[],missingRids:[],readyToPromote:true,phase:"supported"},
        {runtimeId:"sunder.runtime.node",stageId:("2" * 32),lifecycleState:"supported",publishedRids:[],missingRids:[],readyToPromote:true,phase:"supported"}
      ],
      promotions:[{},{}],
      resolutions:[range(0;12) | {success:true}]
    }
  ' > "$evidence/registry-publication.v1.json"
publication_sha="$(hash_file "$evidence/registry-publication.v1.json")"

files='[]'
for path in "$evidence"/*; do
  name="$(basename "$path")"
  files="$(jq -cn \
    --argjson files "$files" --arg path "$name" --arg sha "$(hash_file "$path")" \
    --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" \
    '$files + [{path:$path,sha256:$sha,size:$size}]')"
done
jq -S -n \
  --argjson release_id "$release_id" --arg tag "$tag" --arg source_sha "$source_sha" \
  --arg workflow_sha "$workflow_sha" --arg run_id "$run_id" --arg run_attempt "$run_attempt" \
  --arg lock_sha "$lock_sha" --arg drivers_sha "$drivers_sha" \
  --arg release_set_sha "$release_set_sha" --arg publication_sha "$publication_sha" \
  --argjson drivers "$drivers" --argjson native_receipts "$native_receipts" --argjson files "$files" '
    {
      schemaVersion:1,
      kind:"https://sunderapp.io/acceptance/runtime-release-evidence/v1",
      destination:{repository:"Younics/sunder-core",releaseId:$release_id,tag:$tag},
      source:{repository:"Younics/sunder-core",sha:$source_sha,artifactSourceCommit:$source_sha,publicationEvidenceCommit:("f" * 40)},
      invocation:{
        workflowPath:".github/workflows/sunder-execution-runtime-publication.yml",
        workflowRef:"refs/heads/main",workflowSha:$workflow_sha,runId:$run_id,runAttempt:$run_attempt
      },
      runtimeLock:{path:"production.lock.json",sha256:$lock_sha},
      releaseDrivers:{path:"release-drivers.v1.json",sha256:$drivers_sha,drivers:$drivers},
      nativeOutputReceipts:$native_receipts,
      releaseSet:{path:"release-set.v1.json",sha256:$release_set_sha},
      registryPublication:{path:"registry-publication.v1.json",sha256:$publication_sha},
      files:$files
    }
  ' > "$evidence/runtime-release-evidence.v1.json"

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || exit 80
hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}
endpoint="${*: -1}"
case "$endpoint" in
  */releases/4242)
    draft=true
    [[ "${MOCK_PUBLIC:-false}" != true ]] || draft=false
    assets='[]'
    id=7000
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      assets="$(jq -cn \
        --argjson assets "$assets" --argjson id "$id" --arg name "$(basename "$path")" \
        --arg digest "sha256:$(hash_file "$path")" \
        --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" \
        '$assets + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size}]')"
      id=$((id + 1))
    done
    jq -n --argjson draft "$draft" --argjson assets "$assets" '
      {id:4242,tag_name:"runtime/v1.0.0",target_commitish:("a" * 40),draft:$draft,assets:$assets}
    '
    ;;
  */releases/assets/*)
    requested_id="${endpoint##*/}"
    id=7000
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      if [[ "$id" == "$requested_id" ]]; then command cat "$path"; exit 0; fi
      id=$((id + 1))
    done
    exit 82
    ;;
  *) exit 81 ;;
esac
EOF

cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
url=''
source_path=''
while (($#)); do
  case "$1" in
    --data-binary) shift; source_path="${1#@}" ;;
    https://*) url="$1" ;;
  esac
  shift
done
asset_name="${url##*name=}"
[[ -n "$asset_name" && -f "$source_path" ]] || exit 83
cp "$source_path" "$MOCK_ASSET_STATE/$asset_name"
printf '%s\n' "$asset_name" >> "$MOCK_UPLOAD_LOG"
upload_count="$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')"
if [[ -n "${MOCK_KILL_AFTER_UPLOAD:-}" && "$upload_count" == "$MOCK_KILL_AFTER_UPLOAD" ]]; then
  kill -TERM "$PPID"
  sleep 1
  exit 143
fi
jq -n --arg name "$asset_name" '{id:9999,name:$name,state:"uploaded"}'
EOF
chmod +x "$mock_bin/gh" "$mock_bin/curl"

export GH_REPO='Younics/sunder-core'
export GH_TOKEN='fixture-token'
export MOCK_ASSET_STATE="$asset_state"
export MOCK_UPLOAD_LOG="$fixture_dir/uploads.log"
: > "$MOCK_UPLOAD_LOG"

PATH="$mock_bin:$PATH" bash "$archiver" "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence"
receipt_name="execution-runtime-release-$release_id-$run_id-$run_attempt-receipt.json"
[[ -f "$asset_state/$receipt_name" ]] || { printf 'Durable runtime completion receipt was not uploaded.\n' >&2; exit 1; }
last_upload="$(jq -Rsr 'split("\n") | map(select(length > 0)) | last' "$MOCK_UPLOAD_LOG")"
[[ "$last_upload" == "$receipt_name" ]] || { printf 'Durable runtime completion receipt was not uploaded last.\n' >&2; exit 1; }

before_retry="$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')"
PATH="$mock_bin:$PATH" bash "$archiver" "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence"
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == "$before_retry" ]] \
  || { printf 'Equal runtime evidence retry uploaded duplicate assets.\n' >&2; exit 1; }

first_asset="$(jq -r '.files[0].assetName' "$asset_state/$receipt_name")"
first_source="$(jq -r '.files[0].path' "$asset_state/$receipt_name")"
cp "$asset_state/$first_asset" "$fixture_dir/original-first-asset"
printf 'mismatched immutable evidence\n' > "$asset_state/$first_asset"
if PATH="$mock_bin:$PATH" bash "$archiver" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence" > "$fixture_dir/mismatch.log" 2>&1; then
  printf 'Runtime evidence archiver accepted a mismatched retry.\n' >&2
  exit 1
fi
grep -Fq 'never replaced' "$fixture_dir/mismatch.log"
cp "$evidence/$first_source" "$asset_state/$first_asset"

before_false_completion="$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')"
rm "$asset_state/$first_asset"
if PATH="$mock_bin:$PATH" bash "$archiver" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence" > "$fixture_dir/false-completion.log" 2>&1; then
  printf 'Runtime evidence archiver repaired an asset set whose completion receipt already existed.\n' >&2
  exit 1
fi
grep -Fq 'falsely marked complete' "$fixture_dir/false-completion.log"
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == "$before_false_completion" ]] \
  || { printf 'False completion recovery wrote an asset before failing.\n' >&2; exit 1; }
cp "$evidence/$first_source" "$asset_state/$first_asset"

if MOCK_PUBLIC=true PATH="$mock_bin:$PATH" bash "$archiver" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence" > "$fixture_dir/finalized.log" 2>&1; then
  printf 'Runtime evidence archiver accepted a finalized destination.\n' >&2
  exit 1
fi
grep -Fq 'source-bound draft' "$fixture_dir/finalized.log"

rm -rf "$asset_state"
mkdir "$asset_state"
: > "$MOCK_UPLOAD_LOG"
if MOCK_KILL_AFTER_UPLOAD=2 PATH="$mock_bin:$PATH" bash "$archiver" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence" > "$fixture_dir/runner-loss.log" 2>&1; then
  printf 'Runtime evidence upload runner loss unexpectedly succeeded.\n' >&2
  exit 1
fi
[[ ! -e "$asset_state/$receipt_name" ]] \
  || { printf 'Interrupted runtime upload published its completion receipt.\n' >&2; exit 1; }
PATH="$mock_bin:$PATH" bash "$archiver" "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence"
[[ -f "$asset_state/$receipt_name" ]] \
  || { printf 'Runtime evidence did not converge after upload runner loss.\n' >&2; exit 1; }

printf 'Execution-runtime durable upload fixture passed: upload runner loss, equal retry, mismatch, receipt-last completion, false-completion refusal, exact-byte verification, and finalized destination policy are enforced.\n'
