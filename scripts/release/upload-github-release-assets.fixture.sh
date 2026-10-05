#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
uploader="$script_dir/upload-github-release-assets.sh"
[[ -f "$uploader" ]] || { printf 'GitHub release asset uploader not found.\n' >&2; exit 1; }
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
    jq -n --argjson draft "$draft" --argjson assets "$assets" \
      '{id:4242,tag_name:"cli/v1.2.3",draft:$draft,assets:$assets}'
    ;;
  */releases/assets/*)
    requested_id="${endpoint##*/}"
    id=100
    for path in "$MOCK_ASSET_STATE"/*; do
      [[ -f "$path" ]] || continue
      if [[ "$id" == "$requested_id" ]]; then
        command cat "$path"
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
export MOCK_ASSET_STATE="$asset_state"
export MOCK_CURL_LOG="$fixture_dir/curl.log"
export MOCK_KILL_MARKER="$fixture_dir/killed"
export MOCK_UNKNOWN_MARKER="$fixture_dir/unknown"
: > "$MOCK_CURL_LOG"

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

printf 'GitHub release uploader fixture passed: exact-byte upload, unknown outcome, upload runner loss, equal retry, mismatch refusal, and finalized destination policy are enforced.\n'
