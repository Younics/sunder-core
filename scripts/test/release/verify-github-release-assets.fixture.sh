#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
verifier="$script_dir/verify-github-release-assets.sh"
evidence_generator="$script_dir/generate-release-evidence.sh"
[[ -f "$verifier" && -f "$evidence_generator" ]] \
  || { printf 'GitHub release verifier fixture scripts are missing.\n' >&2; exit 1; }

for required_command in jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the GitHub release verifier fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the GitHub release verifier fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-github-release-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
local_dir="$fixture_dir/local"
remote_dir="$fixture_dir/remote"
mock_bin="$fixture_dir/mock-bin"
release_id='4242'
mkdir -p "$local_dir" "$remote_dir" "$mock_bin"
printf 'first release artifact\n' > "$local_dir/sunder-first-1.2.3.tar.gz"
printf 'second release artifact\n' > "$local_dir/sunder-second-1.2.3.zip"
GITHUB_RELEASE_ID="$release_id" bash "$evidence_generator" \
  "$local_dir" 'sunder-core=0123456789012345678901234567890123456789'
cp "$local_dir"/* "$remote_dir"/

build_release_json() {
  local output="$1"
  local assets='[]'
  local path
  local asset_id=100
  for path in "$remote_dir"/*; do
    name="$(basename "$path")"
    digest="$(hash_file "$path")"
    size="$(wc -c < "$path" | tr -d '[:space:]')"
    assets="$(jq -cn \
      --argjson assets "$assets" \
      --argjson id "$asset_id" \
      --arg name "$name" \
      --arg digest "sha256:$digest" \
      --argjson size "$size" \
      '$assets + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size}]')"
    asset_id=$((asset_id + 1))
  done
  jq -n --argjson id "$release_id" --arg tag 'fixture/v1.2.3' --argjson assets "$assets" \
    '{id:$id,tag_name:$tag,draft:true,assets:$assets}' > "$output"
}

build_release_json "$fixture_dir/release.json"
cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "api" ]]; then
  endpoint="${*: -1}"
  case "$endpoint" in
    */releases/tags/*)
      # GitHub's tag endpoint only resolves published releases.
      exit 44
      ;;
    */releases/4242)
      if [[ "${MOCK_API_FAIL_ONCE:-false}" == "true" && ! -f "$MOCK_API_FAILURE_MARKER" ]]; then
        : > "$MOCK_API_FAILURE_MARKER"
        exit 1
      fi
      command cat "$MOCK_RELEASE_JSON"
      ;;
    */releases/assets/*)
      asset_id="${endpoint##*/}"
      asset_name="$(jq -r --argjson id "$asset_id" '.assets[] | select(.id == $id) | .name' "$MOCK_RELEASE_JSON")"
      [[ -n "$asset_name" && "$asset_name" != null ]] || exit 45
      command cat "$MOCK_REMOTE_DIR/$asset_name"
      ;;
    *) exit 46 ;;
  esac
  exit 0
fi
exit 65
EOF
chmod +x "$mock_bin/gh"
export GH_REPO='Younics/sunder-core'
export MOCK_RELEASE_JSON="$fixture_dir/release.json"
export MOCK_REMOTE_DIR="$remote_dir"
export MOCK_API_FAILURE_MARKER="$fixture_dir/api-failed-once"
expected_names=(
  sunder-first-1.2.3.tar.gz
  sunder-second-1.2.3.zip
  SHA256SUMS
  release-provenance.json
  release-sbom.spdx.json
  toolchain.txt
)

MOCK_API_FAIL_ONCE=true PATH="$mock_bin:$PATH" \
  bash "$verifier" "$release_id" 'fixture/v1.2.3' "$local_dir" "${expected_names[@]}"
[[ -f "$MOCK_API_FAILURE_MARKER" ]] \
  || { printf 'GitHub release verifier fixture did not exercise transient API failure recovery.\n' >&2; exit 1; }

printf 'durable production acceptance\n' > "$remote_dir/production-acceptance.json"
build_release_json "$fixture_dir/release.json"
SUNDER_ADDITIONAL_RELEASE_ASSETS=production-acceptance.json PATH="$mock_bin:$PATH" \
  bash "$verifier" "$release_id" 'fixture/v1.2.3' "$local_dir" "${expected_names[@]}"
rm "$remote_dir/production-acceptance.json"
build_release_json "$fixture_dir/release.json"

cp "$local_dir/release-provenance.json" "$fixture_dir/original-provenance.json"
jq '.invocation.releaseId = 9999' "$local_dir/release-provenance.json" > "$fixture_dir/wrong-release-provenance.json"
cp "$fixture_dir/wrong-release-provenance.json" "$local_dir/release-provenance.json"
cp "$fixture_dir/wrong-release-provenance.json" "$remote_dir/release-provenance.json"
build_release_json "$fixture_dir/release.json"
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$release_id" 'fixture/v1.2.3' "$local_dir" "${expected_names[@]}" > "$fixture_dir/wrong-release.log" 2>&1; then
  printf 'GitHub release verifier accepted provenance for another numeric release.\n' >&2
  exit 1
fi
grep -Fq 'provenance artifact inventory is invalid' "$fixture_dir/wrong-release.log" \
  || { printf 'Wrong provenance release ID did not fail at the provenance gate.\n' >&2; exit 1; }
cp "$fixture_dir/original-provenance.json" "$local_dir/release-provenance.json"
cp "$fixture_dir/original-provenance.json" "$remote_dir/release-provenance.json"

grep -Fv 'sunder-second-1.2.3.zip' "$local_dir/SHA256SUMS" > "$fixture_dir/incomplete-sums"
cp "$fixture_dir/incomplete-sums" "$local_dir/SHA256SUMS"
cp "$fixture_dir/incomplete-sums" "$remote_dir/SHA256SUMS"
build_release_json "$fixture_dir/release.json"
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$release_id" 'fixture/v1.2.3' "$local_dir" "${expected_names[@]}" > "$fixture_dir/incomplete.log" 2>&1; then
  printf 'GitHub release verifier accepted incomplete SHA256SUMS evidence.\n' >&2
  exit 1
fi
grep -Fq 'inventory counts differ' "$fixture_dir/incomplete.log" \
  || { printf 'Incomplete GitHub release evidence did not fail the inventory gate.\n' >&2; exit 1; }

printf 'GitHub release verifier fixture passed: remote bytes, explicit durable extras, API digests, provenance, checksums, and SBOM inventory are enforced.\n'
