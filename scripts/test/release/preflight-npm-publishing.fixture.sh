#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
preflight="$script_dir/preflight-npm-publishing.sh"
[[ -f "$preflight" ]] || { printf 'Preflight script not found: %s\n' "$preflight" >&2; exit 1; }

for required_command in jq node tar; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the npm preflight fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-npm-preflight-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"
version='1.2.3'
acceptance_tag="accept-v$version"
repository='Younics/sunder-core'
ref="refs/tags/sdk/v$version"
sha='0123456789012345678901234567890123456789'
workflow_ref="$repository/.github/workflows/sunder-sdk-release.yml@$ref"

create_archive() {
  local package_name="$1"
  local archive_name="$2"
  local archive_repository="${3:-git+https://github.com/Younics/sunder-core.git}"
  local staging="$fixture_dir/staging/$archive_name/package"
  mkdir -p "$staging"
  jq -n \
    --arg name "$package_name" \
    --arg version "$version" \
    --arg repository "$archive_repository" '
      {
        name: $name,
        version: $version,
        license: "MIT",
        repository: {url: $repository},
        publishConfig: {access: "public", provenance: true}
      }
    ' > "$staging/package.json"
  tar -czf "$fixture_dir/$archive_name" -C "$(dirname "$staging")" package
}

create_archive '@sunder/sdk' "sunder-sdk-$version.tgz"
create_archive '@sunder/package-tool' "sunder-package-tool-$version.tgz"
create_archive 'create-sunder-package' "create-sunder-package-$version.tgz"

cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
output=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output|-o) output="${2:?}"; shift 2 ;;
    -H|--header)
      [[ "$2" != *"$NODE_AUTH_TOKEN"* ]] || exit 66
      shift 2 ;;
    --request) [[ "$2" == GET ]] || exit 67; shift 2 ;;
    --write-out|--connect-timeout|--max-time) shift 2 ;;
    https://*) url="$1"; shift ;;
    *) shift ;;
  esac
done
[[ -n "$url" ]] || exit 64
printf '%s\n' "$url" >> "$MOCK_CURL_LOG"
case "$url" in
  *'%40sunder%2Fsdk') package_name='@sunder/sdk' ;;
  *'%40sunder%2Fpackage-tool') package_name='@sunder/package-tool' ;;
  *'/create-sunder-package') package_name='create-sunder-package' ;;
  *) exit 65 ;;
esac
if [[ -n "${MOCK_HTTP_STATUS:-}" ]]; then
  jq -n '{error:"Fixture registry failure"}' > "$output"
  printf '%s' "$MOCK_HTTP_STATUS"
  exit 0
fi
if [[ "${MOCK_MALFORMED_JSON:-}" == true ]]; then
  printf '{invalid' > "$output"
  printf '200'
  exit 0
fi
if [[ "${MOCK_MISSING_PACKAGE:-}" == "$package_name" || "${MOCK_MISSING_PACKAGE:-}" == '*' ]]; then
  jq -n '{error:"Not found"}' > "$output"
  printf '404'
  exit 0
fi
repository='git+https://github.com/Younics/sunder-core.git'
if [[ "${MOCK_WRONG_IDENTITY_PACKAGE:-}" == "$package_name" ]]; then
  repository='git+https://github.com/other/project.git'
fi
jq -n --arg name "$package_name" --arg repository "$repository" \
  --arg existing "${MOCK_EXISTING_PACKAGE:-}" --arg version "$MOCK_RELEASE_VERSION" '
  {name:$name,license:"MIT",repository:{url:$repository},versions:{"0.0.1":{}}}
  | if $existing == $name or $existing == "*" then .versions[$version] = {name:$name,version:$version} else . end
' > "$output"
printf '200'
EOF
cat > "$mock_bin/npm" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_NPM_LOG"
[[ "${NODE_AUTH_TOKEN:-}" == 'fixture-npm-token' ]] || exit 65
case "${1:-}" in
  whoami)
    [[ "$*" == 'whoami --registry=https://registry.npmjs.org' ]] || exit 66
    [[ "${MOCK_TOKEN_REJECTED:-}" != true ]] || exit 1
    printf 'fixture-owner\n'
    ;;
  publish)
    # Refuse real writes even if the preflight accidentally drops --dry-run.
    [[ "$*" == *' --dry-run '* && "$*" == *' --ignore-scripts '* \
      && "$*" == *' --access public '* && "$*" == *" --tag $MOCK_ACCEPTANCE_TAG "* \
      && "$*" == *' --registry https://registry.npmjs.org '* && "$*" == *' --provenance' ]] || exit 67
    [[ "${MOCK_DRY_RUN_FAILURE:-}" != true ]] || exit 1
    ;;
  *) exit 64 ;;
esac
EOF
chmod +x "$mock_bin/curl" "$mock_bin/npm"

export ACTIONS_ID_TOKEN_REQUEST_TOKEN='fixture-actions-token'
export ACTIONS_ID_TOKEN_REQUEST_URL='https://actions.example.test/oidc?request=1'
export GITHUB_REF="$ref"
export GITHUB_REPOSITORY="$repository"
export GITHUB_SHA="$sha"
export GITHUB_WORKFLOW_REF="$workflow_ref"
export NODE_AUTH_TOKEN='fixture-npm-token'
export MOCK_CURL_LOG="$fixture_dir/curl.log"
export MOCK_NPM_LOG="$fixture_dir/npm.log"
export MOCK_RELEASE_VERSION="$version"
export MOCK_ACCEPTANCE_TAG="$acceptance_tag"
export RUNNER_TEMP="$fixture_dir"

archives=(
  "$fixture_dir/sunder-sdk-$version.tgz"
  "$fixture_dir/sunder-package-tool-$version.tgz"
  "$fixture_dir/create-sunder-package-$version.tgz"
)

reset_logs() {
  : > "$MOCK_CURL_LOG"
  : > "$MOCK_NPM_LOG"
}

run_preflight() {
  PATH="$mock_bin:$PATH" bash "$preflight" "$version" "$acceptance_tag" "${archives[@]}"
}

expect_failure() {
  local name="$1" expected_status="$2" diagnostic="$3" status=0
  shift 3
  reset_logs
  "$@" > "$fixture_dir/$name.log" 2>&1 || status=$?
  [[ "$status" == "$expected_status" ]] \
    || { cat "$fixture_dir/$name.log" >&2; printf '%s: expected status %s, found %s.\n' "$name" "$expected_status" "$status" >&2; exit 1; }
  grep -Fq "$diagnostic" "$fixture_dir/$name.log" \
    || { cat "$fixture_dir/$name.log" >&2; printf '%s: missing failure diagnostic.\n' "$name" >&2; exit 1; }
  [[ ! -s "$MOCK_NPM_LOG" ]] \
    || { printf '%s: rejected preflight reached npm authentication or dry-run.\n' "$name" >&2; exit 1; }
}

expect_failure invalid-version 2 'leading zero' env PATH="$mock_bin:$PATH" \
  bash "$preflight" '1.2.3-01' 'accept-v1.2.3-01' "${archives[@]}"
expect_failure mutable-tag 2 'version-specific npm acceptance tag' env PATH="$mock_bin:$PATH" \
  bash "$preflight" "$version" latest "${archives[@]}"
NODE_AUTH_TOKEN= expect_failure missing-token 1 'NPM_TOKEN secret' run_preflight
ACTIONS_ID_TOKEN_REQUEST_TOKEN= expect_failure missing-provenance 1 'required for provenance' run_preflight
GITHUB_REF=refs/heads/main expect_failure wrong-ref 1 'exact Sunder SDK tag/workflow' run_preflight

reset_logs
run_preflight
[[ "$(grep -c '^whoami ' "$MOCK_NPM_LOG")" == 1 && "$(grep -c '^publish ' "$MOCK_NPM_LOG")" == 3 ]] \
  || { printf 'New versions on established coordinates must authenticate and dry-run all three packages.\n' >&2; exit 1; }

reset_logs
MOCK_MISSING_PACKAGE='*' run_preflight
[[ "$(grep -c '^whoami ' "$MOCK_NPM_LOG")" == 1 && "$(grep -c '^publish ' "$MOCK_NPM_LOG")" == 3 ]] \
  || { printf 'First publication must support all three missing coordinates without placeholders.\n' >&2; exit 1; }

reset_logs
MOCK_EXISTING_PACKAGE='@sunder/sdk' run_preflight > "$fixture_dir/partial-retry.log"
[[ "$(grep -c '^publish ' "$MOCK_NPM_LOG")" == 2 ]] \
  || { printf 'Partial retry must dry-run only the two missing versions.\n' >&2; exit 1; }
if grep -q '^publish .*sunder-sdk-' "$MOCK_NPM_LOG"; then
  printf 'Existing immutable SDK version reached a conflicting publish dry-run.\n' >&2
  exit 1
fi
grep -Fq 'requires immutable readback verification' "$fixture_dir/partial-retry.log"

reset_logs
MOCK_EXISTING_PACKAGE='*' run_preflight
[[ "$(wc -l < "$MOCK_NPM_LOG" | tr -d '[:space:]')" == 1 ]] \
  || { printf 'Completed retry must only authenticate, leaving immutable verification to the publisher.\n' >&2; exit 1; }

MOCK_WRONG_IDENTITY_PACKAGE='@sunder/package-tool' \
  expect_failure wrong-identity 1 'unexpected public identity' run_preflight
for http_status in 401 403 429 503; do
  MOCK_HTTP_STATUS="$http_status" expect_failure "http-$http_status" 1 "HTTP $http_status" run_preflight
done
MOCK_MALFORMED_JSON=true expect_failure malformed-json 1 'unexpected public identity' run_preflight

create_archive '@sunder/sdk' wrong-repository.tgz 'git+https://github.com/other/project.git'
expect_failure wrong-local-identity 1 'Unexpected local npm publication identity' env PATH="$mock_bin:$PATH" \
  bash "$preflight" "$version" "$acceptance_tag" "$fixture_dir/wrong-repository.tgz" "${archives[1]}" "${archives[2]}"

reset_logs
status=0
MOCK_TOKEN_REJECTED=true run_preflight > "$fixture_dir/rejected-token.log" 2>&1 || status=$?
[[ "$status" == 1 && "$(wc -l < "$MOCK_NPM_LOG" | tr -d '[:space:]')" == 1 ]] \
  || { printf 'Rejected npm token must fail before any publish dry-run.\n' >&2; exit 1; }
grep -Fq 'npm access token authentication failed' "$fixture_dir/rejected-token.log"

reset_logs
status=0
MOCK_DRY_RUN_FAILURE=true run_preflight > "$fixture_dir/rejected-dry-run.log" 2>&1 || status=$?
[[ "$status" == 1 && "$(grep -c '^publish ' "$MOCK_NPM_LOG")" == 1 ]] \
  || { printf 'Failed dry-run must stop before advancing to the next package.\n' >&2; exit 1; }
if grep -RFq 'fixture-npm-token' "$fixture_dir"/*.log; then
  printf 'npm preflight exposed its token in logs or command arguments.\n' >&2
  exit 1
fi

printf 'npm preflight fixture passed: token authentication, first publication, immutable retries, identity/HTTP boundaries, provenance context, and no-write dry-runs.\n'
