#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 5 ]] || { printf 'Usage: preflight-npm-publishing.sh <version> <npm-tag> <package.tgz>...\n' >&2; exit 2; }
expected_version="$1"
npm_tag="$2"
shift 2

for command_name in curl jq mktemp node npm tar; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required for npm publication preflight.\n' "$command_name" >&2; exit 127; }
done

[[ "$expected_version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?$ ]] \
  || { printf 'Expected a strict SemVer package version, received %s.\n' "$expected_version" >&2; exit 2; }
if [[ "$expected_version" == *-* ]]; then
  IFS='.' read -r -a prerelease_identifiers <<< "${expected_version#*-}"
  for identifier in "${prerelease_identifiers[@]}"; do
    if [[ "$identifier" =~ ^[0-9]+$ && ${#identifier} -gt 1 && "$identifier" == 0* ]]; then
      printf 'Numeric SemVer prerelease identifier %s contains a leading zero.\n' "$identifier" >&2
      exit 2
    fi
  done
fi
expected_npm_tag="accept-v$expected_version"
[[ "$npm_tag" == "$expected_npm_tag" ]] \
  || { printf 'Expected version-specific npm acceptance tag %s, received %s.\n' "$expected_npm_tag" "$npm_tag" >&2; exit 2; }

readonly npm_registry='https://registry.npmjs.org'
readonly expected_repository='git+https://github.com/Younics/sunder-core.git'
: "${NODE_AUTH_TOKEN:?Set the NPM_TOKEN secret in sunder-core-release or repository Actions secrets for npm publication}"
archives=()
package_names=()
existing_versions=()
seen_sdk=false
seen_tool=false
seen_create=false

for archive in "$@"; do
  [[ -f "$archive" ]] || { printf 'npm package archive not found: %s\n' "$archive" >&2; exit 1; }
  manifest="$(tar -xOf "$archive" package/package.json)"
  package_name="$(jq -er '.name | select(type == "string" and length > 0)' <<< "$manifest")"
  package_version="$(jq -er '.version | select(type == "string" and length > 0)' <<< "$manifest")"
  [[ "$package_version" == "$expected_version" ]] || {
    printf '%s contains version %s, expected %s.\n' "$archive" "$package_version" "$expected_version" >&2
    exit 1
  }

  case "$package_name" in
    '@sunder/sdk') [[ "$seen_sdk" == false ]] || { printf 'Duplicate npm package identity: %s\n' "$package_name" >&2; exit 1; }; seen_sdk=true ;;
    '@sunder/package-tool') [[ "$seen_tool" == false ]] || { printf 'Duplicate npm package identity: %s\n' "$package_name" >&2; exit 1; }; seen_tool=true ;;
    'create-sunder-package') [[ "$seen_create" == false ]] || { printf 'Duplicate npm package identity: %s\n' "$package_name" >&2; exit 1; }; seen_create=true ;;
    *) printf 'Unexpected npm release package: %s\n' "$package_name" >&2; exit 1 ;;
  esac

  jq -e --arg repository "$expected_repository" '
    .license == "MIT"
    and .repository.url == $repository
    and .private != true
    and .publishConfig.access == "public"
    and .publishConfig.provenance == true
  ' <<< "$manifest" >/dev/null \
    || { printf 'Unexpected local npm publication identity: %s\n' "$package_name" >&2; exit 1; }

  archives+=("$archive")
  package_names+=("$package_name")
done

[[ "$seen_sdk" == true && "$seen_tool" == true && "$seen_create" == true ]] \
  || { printf 'The coordinated npm package set is incomplete.\n' >&2; exit 1; }

# The npm token authenticates publication. GitHub OIDC is still required for
# provenance, not for an npm trusted-publisher configuration or token exchange.
: "${ACTIONS_ID_TOKEN_REQUEST_URL:?GitHub Actions id-token: write permission is required for provenance.}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?GitHub Actions id-token: write permission is required for provenance.}"
: "${GITHUB_REF:?GITHUB_REF is required.}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
: "${GITHUB_SHA:?GITHUB_SHA is required.}"
: "${GITHUB_WORKFLOW_REF:?GITHUB_WORKFLOW_REF is required.}"
expected_workflow_ref="Younics/sunder-core/.github/workflows/sunder-sdk-release.yml@$GITHUB_REF"
[[ "$GITHUB_REPOSITORY" == 'Younics/sunder-core' \
    && "$GITHUB_REF" == "refs/tags/sdk/v$expected_version" \
    && "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ \
    && "$GITHUB_WORKFLOW_REF" == "$expected_workflow_ref" \
    && "$ACTIONS_ID_TOKEN_REQUEST_URL" == https://* ]] || {
  printf 'npm publication requires the exact Sunder SDK tag/workflow and GitHub provenance context.\n' >&2
  exit 1
}

temp_dir="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-npm-preflight.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT

# Missing public coordinates are legitimate first publications. Existing
# coordinates must match the public identity; other HTTP failures are not absence.
for index in "${!package_names[@]}"; do
  package_name="${package_names[$index]}"
  escaped_name="$(jq -rn --arg value "$package_name" '$value | @uri')"
  coordinate_path="$temp_dir/coordinate-$index.json"
  status="$(curl --silent --show-error \
    --proto '=https' \
    --tlsv1.2 \
    --max-redirs 0 \
    --connect-timeout 10 \
    --max-time 45 \
    --output "$coordinate_path" \
    --write-out '%{http_code}' \
    --request GET \
    -H 'Accept: application/json' \
    "$npm_registry/$escaped_name")"
  case "$status" in
    200)
      jq -e \
        --arg name "$package_name" \
        --arg repository "$expected_repository" '
          .name == $name
          and .license == "MIT"
          and ((.repository.url // .repository) == $repository)
          and (.versions | type == "object" and length > 0)
        ' "$coordinate_path" >/dev/null \
        || { printf "npm coordinate '%s' exists with an unexpected public identity.\n" "$package_name" >&2; exit 1; }
      existing_versions+=("$(jq -r --arg version "$expected_version" '.versions | has($version)' "$coordinate_path")")
      ;;
    404)
      existing_versions+=(false)
      ;;
    *)
      message="$(jq -r '.error // .message // "unknown npm registry error"' "$coordinate_path" 2>/dev/null || true)"
      printf "npm coordinate lookup failed for '%s' (HTTP %s): %s\n" "$package_name" "$status" "$message" >&2
      exit 1
      ;;
  esac
done

# This proves token authentication, not package write rights or 2FA bypass. Only
# actual publication can establish those; no placeholder or other write is used.
npm whoami --registry="$npm_registry" >/dev/null || {
  printf 'npm access token authentication failed; check the NPM_TOKEN secret.\n' >&2
  exit 1
}

for index in "${!package_names[@]}"; do
  archive="${archives[$index]}"
  package_name="${package_names[$index]}"

  # npm rejects a publish dry-run of an existing version. The publisher must still
  # perform exact byte/signature/provenance verification before skipping it.
  if [[ "${existing_versions[$index]}" == true ]]; then
    printf 'Existing npm version %s@%s requires immutable readback verification; publish dry-run skipped.\n' "$package_name" "$expected_version"
    continue
  fi

  npm publish "$archive" \
    --dry-run \
    --ignore-scripts \
    --access public \
    --tag "$npm_tag" \
    --registry "$npm_registry" \
    --provenance >/dev/null
  printf "Preflighted npm token authentication and local publish dry-run for '%s@%s'; no registry write was attempted.\n" "$package_name" "$expected_version"
done
