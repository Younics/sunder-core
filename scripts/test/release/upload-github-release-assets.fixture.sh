#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
uploader="$script_dir/upload-github-release-assets.sh"
downloader="$script_dir/download-github-release-assets.sh"
[[ -f "$uploader" && -f "$downloader" ]] || { printf 'GitHub release asset transport helpers not found.\n' >&2; exit 1; }
for required_command in cmp jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the release uploader fixture.\n' "$required_command" >&2; exit 127; }
done

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-release-uploader-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
asset_state="$fixture_dir/assets"
mkdir -p "$mock_bin" "$asset_state"
printf 'immutable bytes\n' > "$fixture_dir/artifact.zip"
cp "$fixture_dir/artifact.zip" "$asset_state/artifact.zip"

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh %s\n' "$*" >> "$MOCK_TRANSPORT_LOG"
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
    id=100
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      assets="$(jq -cn \
        --argjson assets "$assets" --argjson id "$id" --arg name "$(basename "$path")" \
        --arg digest "sha256:$(hash_file "$path")" \
        --argjson size "$(wc -c < "$path" | tr -d '[:space:]')" \
        '$assets + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size}]')"
      id=$((id + 1))
    done
    jq -n --arg tag "${MOCK_RELEASE_TAG:-cli/v1.2.3}" --argjson draft "$draft" --argjson assets "$assets" \
      '{id:4242,tag_name:$tag,draft:$draft,assets:$assets}'
    ;;
  */releases/assets/*)
    requested_id="${endpoint##*/}"
    id=100
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      if [[ "$id" == "$requested_id" ]]; then
        case "${MOCK_DOWNLOAD_MODE:-original}" in
          original) command cat "$path" ;;
          tampered) printf 'different bytes\n' ;;
          truncated) printf 'short\n' ;;
          *) exit 82 ;;
        esac
        exit 0
      fi
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
printf 'curl %s\n' "$*" >> "$MOCK_TRANSPORT_LOG"
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
printf '%s\n' "$asset_name" >> "$MOCK_CURL_LOG"
if [[ "${MOCK_KILL_UPLOAD_RUNNER:-false}" == true && ! -e "$MOCK_KILL_MARKER" ]]; then
  : > "$MOCK_KILL_MARKER"
  kill -TERM "$PPID"
  sleep 1
  exit 143
fi
if [[ "${MOCK_UNKNOWN_UPLOAD_RESULT:-false}" == true && ! -e "$MOCK_UNKNOWN_MARKER" ]]; then
  : > "$MOCK_UNKNOWN_MARKER"
  exit 75
fi
jq -n --arg name "$asset_name" '{id:101,name:$name,state:"uploaded"}'
EOF
chmod +x "$mock_bin/gh" "$mock_bin/curl"

export GH_REPO='Younics/sunder-core'
export GH_TOKEN='fixture-token'
export RUNNER_TEMP="$fixture_dir"
export MOCK_ASSET_STATE="$asset_state"
export MOCK_CURL_LOG="$fixture_dir/curl.log"
export MOCK_TRANSPORT_LOG="$fixture_dir/transport.log"
export MOCK_KILL_MARKER="$fixture_dir/killed"
export MOCK_UNKNOWN_MARKER="$fixture_dir/unknown"
: > "$MOCK_CURL_LOG"
: > "$MOCK_TRANSPORT_LOG"

# An equal existing asset is an idempotent no-op and is byte-compared.
PATH="$mock_bin:$PATH" bash "$uploader" 4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
[[ ! -s "$MOCK_CURL_LOG" ]] || { printf 'Exact existing asset was uploaded again.\n' >&2; exit 1; }

printf 'different bytes\n' > "$asset_state/artifact.zip"
if PATH="$mock_bin:$PATH" bash "$uploader" \
  4242 'cli/v1.2.3' "$fixture_dir/artifact.zip" > "$fixture_dir/different.log" 2>&1; then
  printf 'Uploader replaced a differing existing asset.\n' >&2
  exit 1
fi
grep -Fq 'never replaced' "$fixture_dir/different.log"
cp "$fixture_dir/artifact.zip" "$asset_state/artifact.zip"

# A missing asset is uploaded once; an equal retry performs no mutation.
rm "$asset_state/artifact.zip"
PATH="$mock_bin:$PATH" bash "$uploader" 4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
[[ "$(wc -l < "$MOCK_CURL_LOG" | tr -d '[:space:]')" == 1 ]]
PATH="$mock_bin:$PATH" bash "$uploader" 4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
[[ "$(wc -l < "$MOCK_CURL_LOG" | tr -d '[:space:]')" == 1 ]] \
  || { printf 'Equal retry uploaded the asset again.\n' >&2; exit 1; }

# A transport failure after persistence is reconciled by exact remote bytes.
rm "$asset_state/artifact.zip"
: > "$MOCK_CURL_LOG"
rm -f "$MOCK_UNKNOWN_MARKER"
MOCK_UNKNOWN_UPLOAD_RESULT=true PATH="$mock_bin:$PATH" \
  bash "$uploader" 4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
[[ -f "$MOCK_UNKNOWN_MARKER" && -f "$asset_state/artifact.zip" ]]

# Actual runner loss leaves no process to reconcile, but the equal rerun converges.
rm "$asset_state/artifact.zip"
: > "$MOCK_CURL_LOG"
rm -f "$MOCK_KILL_MARKER"
if MOCK_KILL_UPLOAD_RUNNER=true PATH="$mock_bin:$PATH" bash "$uploader" \
  4242 'cli/v1.2.3' "$fixture_dir/artifact.zip" > "$fixture_dir/runner-loss.log" 2>&1; then
  printf 'Simulated upload runner loss unexpectedly succeeded.\n' >&2
  exit 1
fi
[[ -f "$MOCK_KILL_MARKER" && -f "$asset_state/artifact.zip" ]]
uploads_before_retry="$(wc -l < "$MOCK_CURL_LOG" | tr -d '[:space:]')"
PATH="$mock_bin:$PATH" bash "$uploader" 4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
[[ "$(wc -l < "$MOCK_CURL_LOG" | tr -d '[:space:]')" == "$uploads_before_retry" ]] \
  || { printf 'Runner-loss recovery duplicated an already durable upload.\n' >&2; exit 1; }

# Finalized destinations are read-only even for equal bytes.
if MOCK_PUBLIC=true PATH="$mock_bin:$PATH" bash "$uploader" \
  4242 'cli/v1.2.3' "$fixture_dir/artifact.zip" > "$fixture_dir/public.log" 2>&1; then
  printf 'Uploader mutated or accepted a finalized release destination.\n' >&2
  exit 1
fi
grep -Fq 'left draft state' "$fixture_dir/public.log"

# Every supported stream can upload missing bytes and download them by numeric asset ID.
for component in app cli host sdk; do
  tag="$component/v1.2.3"
  rm "$asset_state/artifact.zip"
  : > "$MOCK_CURL_LOG"
  : > "$MOCK_TRANSPORT_LOG"
  MOCK_RELEASE_TAG="$tag" PATH="$mock_bin:$PATH" \
    bash "$uploader" 4242 "$tag" "$fixture_dir/artifact.zip"
  [[ "$(wc -l < "$MOCK_CURL_LOG" | tr -d '[:space:]')" == 1 ]]
  download_directory="$fixture_dir/download-$component"
  mkdir "$download_directory"
  MOCK_RELEASE_TAG="$tag" PATH="$mock_bin:$PATH" \
    bash "$downloader" 4242 "$tag" "$download_directory" artifact.zip
  cmp "$fixture_dir/artifact.zip" "$download_directory/artifact.zip"
  grep -Fq 'repos/Younics/sunder-core/releases/4242' "$MOCK_TRANSPORT_LOG"
  grep -Fq 'repos/Younics/sunder-core/releases/assets/100' "$MOCK_TRANSPORT_LOG"
done

# Omitting names downloads the observed inventory through the same verified transport.
mkdir "$fixture_dir/download-all"
PATH="$mock_bin:$PATH" bash "$downloader" 4242 'cli/v1.2.3' "$fixture_dir/download-all"
cmp "$fixture_dir/artifact.zip" "$fixture_dir/download-all/artifact.zip"

# Both same-size corruption and truncation must fail before publishing a destination file.
for mode in tampered truncated; do
  download_directory="$fixture_dir/download-$mode"
  mkdir "$download_directory"
  if MOCK_DOWNLOAD_MODE="$mode" PATH="$mock_bin:$PATH" bash "$downloader" \
      4242 'cli/v1.2.3' "$download_directory" artifact.zip > "$fixture_dir/$mode.log" 2>&1; then
    printf 'Downloader accepted %s release asset bytes.\n' "$mode" >&2
    exit 1
  fi
  grep -Fq 'Downloaded bytes do not match GitHub metadata' "$fixture_dir/$mode.log"
  [[ ! -e "$download_directory/artifact.zip" ]] \
    || { printf 'Downloader published %s release asset bytes.\n' "$mode" >&2; exit 1; }
done

# Matching mock metadata would permit these tags if a helper's admission check regressed.
# Require the tag diagnostic and usage exit before even one gh/curl invocation.
mkdir "$fixture_dir/download-invalid"
for tag in runtime/v1.2.3 runtime/v1.2.3-preview.1 other/v1.2.3 sdk/1.2.3; do
  for helper in "$uploader" "$downloader"; do
    arguments=("$fixture_dir/artifact.zip")
    if [[ "$helper" == "$downloader" ]]; then
      arguments=("$fixture_dir/download-invalid" artifact.zip)
    fi
    : > "$MOCK_TRANSPORT_LOG"
    failure_log="$fixture_dir/invalid-$(basename "$helper").log"
    if MOCK_RELEASE_TAG="$tag" PATH="$mock_bin:$PATH" bash "$helper" \
        4242 "$tag" "${arguments[@]}" > "$failure_log" 2>&1; then
      printf '%s accepted invalid release tag %s.\n' "$(basename "$helper")" "$tag" >&2
      exit 1
    else
      status=$?
    fi
    [[ "$status" == 2 ]] \
      || { printf '%s did not reject tag %s at admission (exit %s).\n' "$(basename "$helper")" "$tag" "$status" >&2; exit 1; }
    grep -Fq "Unexpected release tag: $tag" "$failure_log"
    [[ ! -s "$MOCK_TRANSPORT_LOG" ]] \
      || { printf '%s invoked gh/curl before rejecting tag %s.\n' "$(basename "$helper")" "$tag" >&2; exit 1; }
    [[ ! -e "$fixture_dir/download-invalid/artifact.zip" ]]
  done
done

printf 'GitHub release asset fixture passed: supported streams, invalid/retired tags before transport, verified downloads, exact-byte upload, unknown outcome, upload runner loss, equal retry, mismatch refusal, and finalized destination policy are enforced.\n'
