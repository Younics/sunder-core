#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
uploader="$script_dir/upload-github-release-assets.sh"
[[ -f "$uploader" ]] || { printf 'GitHub release asset uploader not found.\n' >&2; exit 1; }
for required_command in jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the release uploader fixture.\n' "$required_command" >&2; exit 127; }
done

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-release-uploader-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"
printf 'immutable bytes\n' > "$fixture_dir/artifact.zip"
cp "$fixture_dir/artifact.zip" "$fixture_dir/remote.zip"

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || exit 80
endpoint="${*: -1}"
case "$endpoint" in
  */releases/4242)
    draft=true
    [[ "${MOCK_PUBLIC:-false}" != true ]] || draft=false
    if [[ "${MOCK_MISSING:-false}" == true ]]; then
      jq -n --argjson draft "$draft" '{id:4242,tag_name:"cli/v1.2.3",draft:$draft,assets:[]}'
    else
      jq -n --argjson draft "$draft" '{id:4242,tag_name:"cli/v1.2.3",draft:$draft,assets:[{id:100,name:"artifact.zip"}]}'
    fi
    ;;
  */releases/assets/100)
    if [[ "${MOCK_DIFFERENT:-false}" == true ]]; then
      printf 'different bytes\n'
    else
      command cat "$MOCK_REMOTE_FILE"
    fi
    ;;
  *) exit 81 ;;
esac
EOF
cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_CURL_LOG"
jq -n '{id:101,name:"artifact.zip",state:"uploaded"}'
EOF
chmod +x "$mock_bin/gh" "$mock_bin/curl"
export GH_REPO='Younics/sunder-core'
export GH_TOKEN='fixture-token'
export MOCK_CURL_LOG="$fixture_dir/curl.log"
export MOCK_REMOTE_FILE="$fixture_dir/remote.zip"

PATH="$mock_bin:$PATH" bash "$uploader" 4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
[[ ! -s "$MOCK_CURL_LOG" ]] || { printf 'Exact existing asset was uploaded again.\n' >&2; exit 1; }

if MOCK_DIFFERENT=true PATH="$mock_bin:$PATH" bash "$uploader" \
  4242 'cli/v1.2.3' "$fixture_dir/artifact.zip" > "$fixture_dir/different.log" 2>&1; then
  printf 'Uploader replaced a differing existing asset.\n' >&2
  exit 1
fi
grep -Fq 'never replaced' "$fixture_dir/different.log" \
  || { printf 'Differing immutable asset did not fail closed.\n' >&2; exit 1; }
[[ ! -s "$MOCK_CURL_LOG" ]] || { printf 'Differing asset reached upload mutation.\n' >&2; exit 1; }

MOCK_MISSING=true PATH="$mock_bin:$PATH" bash "$uploader" \
  4242 'cli/v1.2.3' "$fixture_dir/artifact.zip"
grep -Fq 'uploads.github.com/repos/Younics/sunder-core/releases/4242/assets' "$MOCK_CURL_LOG" \
  || { printf 'Missing asset was not uploaded through its numeric release ID.\n' >&2; exit 1; }

if MOCK_PUBLIC=true PATH="$mock_bin:$PATH" bash "$uploader" \
  4242 'cli/v1.2.3' "$fixture_dir/artifact.zip" > "$fixture_dir/public.log" 2>&1; then
  printf 'Uploader mutated a finalized release.\n' >&2
  exit 1
fi

printf 'GitHub release uploader fixture passed: numeric upload, exact retry reuse, immutable mismatch rejection, and public-release refusal are enforced.\n'
