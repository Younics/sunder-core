#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
preflight="$script_dir/preflight-npm-trusted-publishing.sh"
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
  local staging="$fixture_dir/staging/$archive_name/package"
  mkdir -p "$staging"
  jq -n \
    --arg name "$package_name" \
    --arg version "$version" '
      {
        name: $name,
        version: $version,
        license: "MIT",
        repository: {url: "git+https://github.com/Younics/sunder-core.git"}
      }
    ' > "$staging/package.json"
  tar -czf "$fixture_dir/$archive_name" -C "$(dirname "$staging")" package
}

create_archive '@sunder/sdk' "sunder-sdk-$version.tgz"
create_archive '@sunder/package-tool' "sunder-package-tool-$version.tgz"
create_archive 'create-sunder-package' "create-sunder-package-$version.tgz"

jq -n \
  --arg ref "$ref" \
  --arg repository "$repository" \
  --arg sha "$sha" \
  --arg workflow_ref "$workflow_ref" '
    {
      aud: "npm:registry.npmjs.org",
      repository: $repository,
      repository_visibility: "public",
      ref: $ref,
      sha: $sha,
      workflow_ref: $workflow_ref,
      runner_environment: "github-hosted"
    }
  ' > "$fixture_dir/oidc-payload.json"
oidc_payload="$(node -e '
  const fs = require("node:fs");
  process.stdout.write(Buffer.from(fs.readFileSync(process.argv[1])).toString("base64url"));
' "$fixture_dir/oidc-payload.json")"
jq -n --arg value "header.$oidc_payload.signature" '{value: $value}' > "$fixture_dir/oidc-response.json"

cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
output=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output|-o) output="${2:?}"; shift 2 ;;
    -H|--header|--write-out|--request|--connect-timeout|--max-time) shift 2 ;;
    https://*) url="$1"; shift ;;
    *) shift ;;
  esac
done
[[ -n "$url" ]] || exit 64
printf '%s\n' "$url" >> "$MOCK_CURL_LOG"
if [[ "$url" == "$MOCK_ACTIONS_URL"* ]]; then
  command cat "$MOCK_OIDC_RESPONSE"
  exit 0
fi
if [[ "$url" == *'/-/npm/v1/oidc/token/exchange/package/'* ]]; then
  jq -n '{token_type:"oidc",token:"fixture-token",created:"2026-01-01T00:00:00Z",expires:"2026-01-01T00:05:00Z"}' > "$output"
  printf '201'
  exit 0
fi

case "$url" in
  *'%40sunder%2Fsdk') package_name='@sunder/sdk' ;;
  *'%40sunder%2Fpackage-tool') package_name='@sunder/package-tool' ;;
  *'/create-sunder-package') package_name='create-sunder-package' ;;
  *) exit 65 ;;
esac
if [[ "${MOCK_MISSING_PACKAGE:-}" == "$package_name" ]]; then
  jq -n '{error:"Not found"}' > "$output"
  printf '404'
  exit 0
fi
repository='git+https://github.com/Younics/sunder-core.git'
if [[ "${MOCK_WRONG_IDENTITY_PACKAGE:-}" == "$package_name" ]]; then
  repository='git+https://github.com/other/project.git'
fi
jq -n --arg name "$package_name" --arg repository "$repository" '
  {name:$name,license:"MIT",repository:{url:$repository},versions:{"0.0.1":{}}}
' > "$output"
printf '200'
EOF
cat > "$mock_bin/npm" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_NPM_LOG"
[[ "${1:-}" == "publish" ]] || exit 64
EOF
chmod +x "$mock_bin/curl" "$mock_bin/npm"

export ACTIONS_ID_TOKEN_REQUEST_TOKEN='fixture-actions-token'
export ACTIONS_ID_TOKEN_REQUEST_URL='https://actions.example.test/oidc?request=1'
export GITHUB_REF="$ref"
export GITHUB_REPOSITORY="$repository"
export GITHUB_SHA="$sha"
export GITHUB_WORKFLOW_REF="$workflow_ref"
export MOCK_ACTIONS_URL="$ACTIONS_ID_TOKEN_REQUEST_URL"
export MOCK_CURL_LOG="$fixture_dir/curl.log"
export MOCK_NPM_LOG="$fixture_dir/npm.log"
export MOCK_OIDC_RESPONSE="$fixture_dir/oidc-response.json"

archives=(
  "$fixture_dir/sunder-sdk-$version.tgz"
  "$fixture_dir/sunder-package-tool-$version.tgz"
  "$fixture_dir/create-sunder-package-$version.tgz"
)

set +e
PATH="$mock_bin:$PATH" \
  bash "$preflight" '1.2.3-01' 'accept-v1.2.3-01' "${archives[@]}" > "$fixture_dir/invalid-version.log" 2>&1
invalid_version_status=$?
set -e
[[ "$invalid_version_status" == 2 ]] \
  || { printf 'Malformed SemVer preflight input must return status 2, found %s.\n' "$invalid_version_status" >&2; exit 1; }
grep -Fq 'leading zero' "$fixture_dir/invalid-version.log" \
  || { printf 'Malformed SemVer preflight input did not report its leading zero.\n' >&2; exit 1; }

set +e
PATH="$mock_bin:$PATH" \
  bash "$preflight" "$version" latest "${archives[@]}" > "$fixture_dir/mutable-tag.log" 2>&1
mutable_tag_status=$?
set -e
[[ "$mutable_tag_status" == 2 ]] \
  || { printf 'Consumer-facing npm tag must be rejected during acceptance, found status %s.\n' "$mutable_tag_status" >&2; exit 1; }
grep -Fq 'version-specific npm acceptance tag' "$fixture_dir/mutable-tag.log" \
  || { printf 'Mutable npm tag rejection did not report the acceptance-tag requirement.\n' >&2; exit 1; }

PATH="$mock_bin:$PATH" bash "$preflight" "$version" "$acceptance_tag" "${archives[@]}"
[[ "$(wc -l < "$MOCK_NPM_LOG" | tr -d '[:space:]')" == 3 ]] \
  || { printf 'Established-coordinate fixture did not dry-run all three packages.\n' >&2; exit 1; }
[[ "$(grep -c '/-/npm/v1/oidc/token/exchange/package/' "$MOCK_CURL_LOG")" == 3 ]] \
  || { printf 'Established-coordinate fixture did not exchange all three package-scoped tokens.\n' >&2; exit 1; }

: > "$MOCK_CURL_LOG"
: > "$MOCK_NPM_LOG"
set +e
MOCK_MISSING_PACKAGE='@sunder/sdk' PATH="$mock_bin:$PATH" \
  bash "$preflight" "$version" "$acceptance_tag" "${archives[@]}" > "$fixture_dir/missing.log" 2>&1
missing_status=$?
set -e
[[ "$missing_status" == 3 ]] \
  || { printf 'Missing npm coordinate must return the bootstrap-required status 3, found %s.\n' "$missing_status" >&2; exit 1; }
grep -Fq 'coordinate bootstrap is required' "$fixture_dir/missing.log" \
  || { printf 'Missing npm coordinate did not report the bootstrap prerequisite.\n' >&2; exit 1; }
if grep -q '/-/npm/v1/oidc/token/exchange/package/' "$MOCK_CURL_LOG" || [[ -s "$MOCK_NPM_LOG" ]]; then
  printf 'Missing npm coordinate reached trusted-publisher exchange or publish dry-run.\n' >&2
  exit 1
fi

: > "$MOCK_CURL_LOG"
set +e
MOCK_WRONG_IDENTITY_PACKAGE='@sunder/package-tool' PATH="$mock_bin:$PATH" \
  bash "$preflight" "$version" "$acceptance_tag" "${archives[@]}" > "$fixture_dir/wrong-identity.log" 2>&1
identity_status=$?
set -e
[[ "$identity_status" == 1 ]] \
  || { printf 'Unexpected npm coordinate identity must fail, found status %s.\n' "$identity_status" >&2; exit 1; }
grep -Fq 'unexpected public identity' "$fixture_dir/wrong-identity.log" \
  || { printf 'Unexpected npm coordinate identity did not report its failure.\n' >&2; exit 1; }

printf 'npm preflight fixture passed: immutable publication staging tags, coordinate bootstrap, public identity, OIDC exchange, and dry-run phases remain separate.\n'
