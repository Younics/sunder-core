#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
uploader="$script_dir/upload-production-acceptance.sh"
downloader="$script_dir/download-production-acceptance.sh"
[[ -f "$uploader" && -f "$downloader" ]] \
  || { printf 'Production acceptance upload/download scripts were not found.\n' >&2; exit 1; }
for required_command in cmp cp cut find jq mktemp tr wc; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the production acceptance upload fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the production acceptance upload fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-production-upload-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
evidence="$fixture_dir/evidence"
mkdir -p "$mock_bin" "$evidence"

release_id=4242
tag='app/v1.2.3'
run_id=5001
run_attempt=1
workflow_sha='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
coordinator_sha='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'

printf 'acceptance evidence\n' > "$evidence/results.txt"
printf 'native receipt\n' > "$evidence/native.txt"
jq -n \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" \
  --arg workflow_sha "$workflow_sha" \
  --arg coordinator_sha "$coordinator_sha" '
    {
      schemaVersion:1,
      kind:"https://sunderapp.io/acceptance/production/v1",
      decision:"accepted",
      release:{repository:"Younics/sunder-core",component:"app",releaseId:$release_id,tag:$tag,sourceSha:("c" * 40),publicationRunId:"4001",publicationRunAttempt:"1"},
      invocation:{
        repository:"Younics/sunder-core",
        workflowPath:".github/workflows/sunder-production-acceptance.yml",
        workflowRef:"refs/heads/main",
        workflowSha:$workflow_sha,
        runId:$run_id,
        runAttempt:$run_attempt,
        coordinatorSha256:$coordinator_sha
      },
      checks:[]
    }
  ' > "$evidence/production-acceptance.json"

write_manifest() {
  local files path
  files='[]'
  for path in "$evidence"/*; do
    [[ "$(basename "$path")" != production-acceptance-manifest.json ]] || continue
    files="$(jq -cn \
      --argjson files "$files" \
      --arg path "$(basename "$path")" \
      --arg sha "$(hash_file "$path")" \
      --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" \
      '$files + [{path:$path,sha256:$sha,size:$size}]')"
  done
  jq -n --argjson files "$files" '{schemaVersion:1,files:$files}' \
    > "$evidence/production-acceptance-manifest.json"
}

set_attempt() {
  local attempt="$1"
  jq --arg attempt "$attempt" '.invocation.runAttempt = $attempt' \
    "$evidence/production-acceptance.json" > "$fixture_dir/decision.next"
  mv "$fixture_dir/decision.next" "$evidence/production-acceptance.json"
  write_manifest
}

write_manifest

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
    id=1000
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      id=$((id + 1))
      digest="sha256:$(hash_file "$path")"
      case "${MOCK_DIGEST_MODE:-valid}" in
        null) digest=null ;;
        mismatch) digest="sha256:$(printf '0%.0s' {1..64})" ;;
      esac
      if [[ "$digest" == null ]]; then
        assets="$(jq -cn --argjson assets "$assets" --argjson id "$id" --arg name "$(basename "$path")" --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" '$assets + [{id:$id,name:$name,state:"uploaded",digest:null,size:$size}]')"
      else
        assets="$(jq -cn --argjson assets "$assets" --argjson id "$id" --arg name "$(basename "$path")" --arg digest "$digest" --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" '$assets + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size}]')"
      fi
    done
    jq -n --argjson draft "$draft" --argjson assets "$assets" \
      '{id:4242,tag_name:"app/v1.2.3",draft:$draft,assets:$assets}'
    ;;
  */releases/assets/*)
    requested_id="${endpoint##*/}"
    id=1000
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      id=$((id + 1))
      if [[ "$id" == "$requested_id" ]]; then
        command cat "$path"
        exit 0
      fi
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
    --data-binary)
      shift
      source_path="${1#@}"
      ;;
    https://*) url="$1" ;;
  esac
  shift
done
asset_name="${url##*name=}"
[[ -n "$asset_name" && -f "$source_path" ]] || exit 83
cp "$source_path" "$MOCK_ASSET_STATE/$asset_name"
printf '%s\n' "$asset_name" >> "$MOCK_UPLOAD_LOG"
upload_count="$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')"
if [[ -n "${MOCK_FAIL_AFTER_UPLOAD:-}" && "$upload_count" == "$MOCK_FAIL_AFTER_UPLOAD" ]]; then
  kill -TERM "$PPID"
  sleep 1
  exit 143
fi
jq -n --arg name "$asset_name" '{id:9999,name:$name,state:"uploaded"}'
EOF
chmod +x "$mock_bin/gh" "$mock_bin/curl"

export GH_REPO='Younics/sunder-core'
export GH_TOKEN='fixture-token'
export MOCK_UPLOAD_LOG="$fixture_dir/uploads.log"

asset_state="$fixture_dir/assets"
mkdir "$asset_state"
export MOCK_ASSET_STATE="$asset_state"
touch "$MOCK_UPLOAD_LOG"

PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence"
expected_uploads=$(($(find "$evidence" -mindepth 1 -maxdepth 1 -type f | wc -l) + 1))
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == "$expected_uploads" ]] \
  || { printf 'First production acceptance upload did not publish every file plus its receipt.\n' >&2; exit 1; }
last_upload="$(jq -Rsr 'split("\n") | map(select(length > 0)) | last' "$MOCK_UPLOAD_LOG")"
[[ "$last_upload" == "production-acceptance-$release_id-$run_id-$run_attempt-receipt.json" ]] \
  || { printf 'Production acceptance receipt was not uploaded last.\n' >&2; exit 1; }

before_retry="$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')"
PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence"
[[ "$(wc -l < "$MOCK_UPLOAD_LOG" | tr -d '[:space:]')" == "$before_retry" ]] \
  || { printf 'An equal retry uploaded duplicate production acceptance assets.\n' >&2; exit 1; }

receipt_path="$asset_state/production-acceptance-$release_id-$run_id-$run_attempt-receipt.json"
first_asset="$(jq -r '.files[0].assetName' "$receipt_path")"
first_source="$(jq -r '.files[0].path' "$receipt_path")"
printf 'different bytes\n' > "$asset_state/$first_asset"
if PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence" > "$fixture_dir/immutable-mismatch.log" 2>&1; then
  printf 'Production acceptance uploader accepted a differing immutable retry.\n' >&2
  exit 1
fi
grep -Fq 'never replaced' "$fixture_dir/immutable-mismatch.log"
cp "$evidence/$first_source" "$asset_state/$first_asset"

if MOCK_DIGEST_MODE=null PATH="$mock_bin:$PATH" bash "$downloader" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$fixture_dir/null-download" "$fixture_dir/null-assets.txt" \
  > "$fixture_dir/null-digest.log" 2>&1; then
  printf 'Production acceptance downloader accepted a null GitHub asset digest.\n' >&2
  exit 1
fi
grep -Fq 'contains an unhashed asset' "$fixture_dir/null-digest.log"

if MOCK_DIGEST_MODE=mismatch PATH="$mock_bin:$PATH" bash "$downloader" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$fixture_dir/mismatch-download" "$fixture_dir/mismatch-assets.txt" \
  > "$fixture_dir/digest-mismatch.log" 2>&1; then
  printf 'Production acceptance downloader accepted mismatched GitHub asset metadata.\n' >&2
  exit 1
fi
grep -Fq 'differs from GitHub metadata' "$fixture_dir/digest-mismatch.log"

if MOCK_PUBLIC=true PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$run_attempt" "$evidence" > "$fixture_dir/public.log" 2>&1; then
  printf 'Production acceptance uploader mutated a finalized release.\n' >&2
  exit 1
fi

rm -rf "$asset_state"
mkdir "$asset_state"
: > "$MOCK_UPLOAD_LOG"
failed_attempt=7
set_attempt "$failed_attempt"
if MOCK_FAIL_AFTER_UPLOAD=2 PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$failed_attempt" "$evidence" > "$fixture_dir/runner-loss.log" 2>&1; then
  printf 'Production acceptance upload did not surface simulated runner loss.\n' >&2
  exit 1
fi
[[ ! -e "$asset_state/production-acceptance-$release_id-$run_id-$failed_attempt-receipt.json" ]] \
  || { printf 'A failed attempt published its completion receipt.\n' >&2; exit 1; }
PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$failed_attempt" "$evidence"
[[ -f "$asset_state/production-acceptance-$release_id-$run_id-$failed_attempt-receipt.json" ]] \
  || { printf 'The same attempt did not converge after runner loss.\n' >&2; exit 1; }

new_failed_attempt=8
set_attempt "$new_failed_attempt"
: > "$MOCK_UPLOAD_LOG"
if MOCK_FAIL_AFTER_UPLOAD=2 PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$new_failed_attempt" "$evidence" > "$fixture_dir/new-attempt-runner-loss.log" 2>&1; then
  printf 'New-attempt setup did not surface simulated runner loss.\n' >&2
  exit 1
fi
[[ ! -e "$asset_state/production-acceptance-$release_id-$run_id-$new_failed_attempt-receipt.json" ]] \
  || { printf 'A second failed attempt published its completion receipt.\n' >&2; exit 1; }

recovery_attempt=9
set_attempt "$recovery_attempt"
PATH="$mock_bin:$PATH" bash "$uploader" \
  "$release_id" "$tag" "$run_id" "$recovery_attempt" "$evidence"
[[ -f "$asset_state/production-acceptance-$release_id-$run_id-$recovery_attempt-receipt.json" ]] \
  || { printf 'A new attempt did not recover from stale partial uploads.\n' >&2; exit 1; }
[[ -n "$(find "$asset_state" -type f -name "production-acceptance-$release_id-$run_id-$new_failed_attempt-file-*" -print -quit)" ]] \
  || { printf 'The fixture did not preserve stale failed-attempt assets.\n' >&2; exit 1; }
PATH="$mock_bin:$PATH" bash "$downloader" \
  "$release_id" "$tag" "$run_id" "$recovery_attempt" \
  "$fixture_dir/recovery-download" "$fixture_dir/recovery-assets.txt"

printf 'Production acceptance upload fixture passed: first upload, equal and partial retry, immutable mismatch, null and mismatched metadata, finalized-release refusal, runner loss, and new-attempt recovery are enforced.\n'
