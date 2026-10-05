#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-release-promotion.sh"
evidence_generator="$script_dir/generate-release-evidence.sh"
[[ -f "$verifier" && -f "$evidence_generator" ]] \
  || { printf 'Release promotion fixture scripts are missing.\n' >&2; exit 1; }

for required_command in dotnet git jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the release promotion fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the release promotion fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-release-promotion-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
local_dir="$fixture_dir/local"
remote_dir="$fixture_dir/remote"
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$local_dir" "$remote_dir" "$mock_bin"

component='cli'
version='1.2.3'
tag="$component/v$version"
source_sha="$(git rev-parse HEAD)"
release_id='4242'
run_id='12345'
run_attempt='2'
repository='Younics/sunder-core'
workflow='.github/workflows/sunder-cli-release.yml'
workflow_name='Sunder CLI Release'
for runtime in win-x64 win-arm64; do
  printf 'fixture %s\n' "$runtime" > "$local_dir/sunder-cli-$version-$runtime.zip"
done
for runtime in linux-x64 linux-arm64 osx-x64 osx-arm64; do
  printf 'fixture %s\n' "$runtime" > "$local_dir/sunder-cli-$version-$runtime.tar.gz"
done
GITHUB_REPOSITORY="$repository" \
GITHUB_WORKFLOW="$workflow_name" \
GITHUB_WORKFLOW_REF="$repository/$workflow@refs/tags/$tag" \
GITHUB_RUN_ID="$run_id" \
GITHUB_RUN_ATTEMPT="$run_attempt" \
GITHUB_RELEASE_ID="$release_id" \
GITHUB_REF="refs/tags/$tag" \
GITHUB_REF_NAME="$tag" \
GITHUB_SHA="$source_sha" \
  bash "$evidence_generator" "$local_dir" "sunder-core=$source_sha"
cp "$local_dir"/* "$remote_dir"/

build_release_json() {
  local assets='[]'
  local path
  local asset_id=100
  for path in "$remote_dir"/*; do
    local name
    local digest
    local size
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
  jq -n --argjson id "$release_id" --arg tag "$tag" --argjson assets "$assets" \
    '{id:$id,tag_name:$tag,draft:true,prerelease:false,assets:$assets}' > "$fixture_dir/release-api.json"
}
build_release_json

jq -n \
  --argjson id "$run_id" \
  --arg repository "$repository" \
  --arg name "$workflow_name" \
  --arg path "$workflow" \
  --arg sha "$source_sha" '
    {
      id: $id,
      repository: {full_name: $repository},
      name: $name,
      path: $path,
      event: "push",
      head_sha: $sha,
      status: "completed",
      conclusion: "success"
    }
  ' > "$fixture_dir/run.json"
jq -n \
  --argjson id "$run_id" \
  --argjson attempt "$run_attempt" \
  --arg repository "$repository" \
  --arg path "$workflow" \
  --arg sha "$source_sha" '
    {
      id: $id,
      run_attempt: $attempt,
      repository: {full_name: $repository},
      path: $path,
      event: "push",
      head_sha: $sha
    }
  ' > "$fixture_dir/attempt.json"
cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "api" ]]; then
  endpoint="${*: -1}"
  case "$endpoint" in
    */git/ref/tags/*)
      jq -n --arg sha "$MOCK_SOURCE_SHA" '{object:{type:"commit",sha:$sha}}'
      ;;
    */actions/runs/*/attempts/*)
      command cat "$MOCK_ATTEMPT_JSON"
      ;;
    */actions/runs/*)
      command cat "$MOCK_RUN_JSON"
      ;;
    */releases/tags/*)
      exit 44
      ;;
    */releases/4242)
      command cat "$MOCK_RELEASE_API_JSON"
      ;;
    */releases/assets/*)
      asset_id="${endpoint##*/}"
      asset_name="$(jq -r --argjson id "$asset_id" '.assets[] | select(.id == $id) | .name' "$MOCK_RELEASE_API_JSON")"
      [[ -n "$asset_name" && "$asset_name" != null ]] || exit 45
      command cat "$MOCK_REMOTE_DIR/$asset_name"
      ;;
    *) exit 64 ;;
  esac
  exit 0
fi
exit 66
EOF
chmod +x "$mock_bin/gh"

export GH_REPO="$repository"
export MOCK_ATTEMPT_JSON="$fixture_dir/attempt.json"
export MOCK_RELEASE_API_JSON="$fixture_dir/release-api.json"
export MOCK_REMOTE_DIR="$remote_dir"
export MOCK_RUN_JSON="$fixture_dir/run.json"
export MOCK_SOURCE_SHA="$source_sha"

PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" "$run_id" "$run_attempt" "$local_dir"

printf 'stale partial acceptance bytes\n' > "$remote_dir/production-acceptance-$release_id-999-1-file-001"
printf '{"schemaVersion":1}\n' > "$remote_dir/production-acceptance-$release_id-998-1-receipt.json"
build_release_json
SUNDER_ALLOW_PRODUCTION_ACCEPTANCE_ASSETS=true PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" "$run_id" "$run_attempt" "$local_dir"
rm "$remote_dir/production-acceptance-$release_id-999-1-file-001" \
  "$remote_dir/production-acceptance-$release_id-998-1-receipt.json"
build_release_json

jq '.invocation.runId = "54321"' "$local_dir/release-provenance.json" > "$fixture_dir/tampered-provenance.json"
cp "$fixture_dir/tampered-provenance.json" "$local_dir/release-provenance.json"
cp "$fixture_dir/tampered-provenance.json" "$remote_dir/release-provenance.json"
build_release_json
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" "$run_id" "$run_attempt" "$local_dir" \
  > "$fixture_dir/wrong-run.log" 2>&1; then
  printf 'Release promotion verifier accepted evidence from another run.\n' >&2
  exit 1
fi
grep -Fq 'not bound to the exact repository, workflow, release ID, tag, source, run, attempt' "$fixture_dir/wrong-run.log" \
  || { printf 'Release promotion verifier did not report the run binding failure.\n' >&2; exit 1; }

printf 'Release promotion fixture passed: numeric draft, exact tag, source, successful publication run attempt, bytes, provenance, and retained acceptance-attempt assets are bound.\n'
