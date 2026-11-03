#!/usr/bin/env bash
# Selected-package preflight only. History/retry reconciliation is tested by its owner.
set -euo pipefail
helpers="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
fixture="$(mktemp -d "${TMPDIR:-/tmp}/sunder-npm-preflight.XXXXXX")"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/package"
export MOCK_NPM_LOG="$fixture/npm.log"
cat > "$fixture/bin/npm" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_NPM_LOG"
[[ "${NODE_AUTH_TOKEN:-}" == fixture-npm-token ]] || exit 1
case "$1" in
  whoami) [[ "$*" == 'whoami --registry=https://registry.npmjs.org' && "${MOCK_TOKEN_REJECTED:-}" != true ]] ;;
  publish)
    # A malformed preflight must never turn this fixture into a real write.
    [[ "$*" == *' --dry-run '* && "$*" == *' --ignore-scripts '* && "$*" == *' --access public '* \
      && "$*" == *' --tag accept-v1.2.3 '* && "$*" == *' --registry=https://registry.npmjs.org '* \
      && "$*" == *' --provenance' && "${MOCK_DRY_RUN_FAILURE:-}" != true ]] ;;
  *) exit 64 ;;
esac
SH
chmod +x "$fixture/bin/npm"
export PATH="$fixture/bin:$PATH"
export NODE_AUTH_TOKEN=fixture-npm-token
export ACTIONS_ID_TOKEN_REQUEST_URL=https://actions.example.test/oidc
export ACTIONS_ID_TOKEN_REQUEST_TOKEN=fixture-oidc-token
export GITHUB_REPOSITORY=Younics/sunder-core
export GITHUB_REF=refs/heads/main
export GITHUB_WORKFLOW_REF=Younics/sunder-core/.github/workflows/sunder-sdk-release.yml@refs/heads/main
archive="$fixture/sunder-sdk-1.2.3.tgz"
make_archive() {
  jq -n --arg repository "$1" '{name:"@sunder/sdk",version:"1.2.3",license:"MIT",repository:{url:$repository},publishConfig:{access:"public",provenance:true}}' > "$fixture/package/package.json"
  tar -czf "$archive" -C "$fixture" package
}
make_archive git+https://github.com/Younics/sunder-core.git
preflight() { bash "$helpers/preflight-npm-publishing.sh" '@sunder/sdk' "${TEST_VERSION:-1.2.3}" "$archive"; }
reject_before_npm() {
  : > "$MOCK_NPM_LOG"
  if preflight > "$fixture/failure.log" 2>&1; then printf 'Invalid preflight succeeded\n' >&2; exit 1; fi
  [[ ! -s "$MOCK_NPM_LOG" ]]
}
TEST_VERSION=1.2.3-01 reject_before_npm
NODE_AUTH_TOKEN= reject_before_npm
ACTIONS_ID_TOKEN_REQUEST_TOKEN= reject_before_npm
GITHUB_REF=refs/tags/sdk/v1.2.3 reject_before_npm
GITHUB_WORKFLOW_REF=other/workflow@main reject_before_npm
make_archive git+https://github.com/other/project.git
reject_before_npm
make_archive git+https://github.com/Younics/sunder-core.git
: > "$MOCK_NPM_LOG"
preflight
[[ "$(wc -l < "$MOCK_NPM_LOG" | tr -d '[:space:]')" == 2 ]]
: > "$MOCK_NPM_LOG"
if MOCK_TOKEN_REJECTED=true preflight > "$fixture/failure.log" 2>&1; then exit 1; fi
[[ "$(wc -l < "$MOCK_NPM_LOG" | tr -d '[:space:]')" == 1 ]]
if MOCK_DRY_RUN_FAILURE=true preflight > "$fixture/failure.log" 2>&1; then exit 1; fi
if grep -Fq fixture-npm-token "$fixture/failure.log" "$MOCK_NPM_LOG"; then exit 1; fi
printf 'Selected npm preflight passed: identity, SemVer, protected workflow, credentials and no-write dry-run.\n'
