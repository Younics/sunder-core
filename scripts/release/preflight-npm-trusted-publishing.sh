#!/usr/bin/env bash
set -euo pipefail

expected_version="${1:?Usage: preflight-npm-trusted-publishing.sh <version> <npm-tag> <package.tgz>...}"
npm_tag="${2:?Usage: preflight-npm-trusted-publishing.sh <version> <npm-tag> <package.tgz>...}"
shift 2
[[ $# -eq 3 ]] || { printf 'Expected exactly three npm package archives.\n' >&2; exit 2; }
[[ "$npm_tag" == "latest" || "$npm_tag" == "next" ]] \
  || { printf 'Unexpected npm dist-tag: %s\n' "$npm_tag" >&2; exit 2; }

for command_name in curl jq node npm tar; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required for npm trusted-publishing preflight.\n' "$command_name" >&2; exit 127; }
done

: "${ACTIONS_ID_TOKEN_REQUEST_URL:?GitHub Actions id-token: write permission is required.}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?GitHub Actions id-token: write permission is required.}"
: "${GITHUB_REF:?GITHUB_REF is required.}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
: "${GITHUB_SHA:?GITHUB_SHA is required.}"
: "${GITHUB_WORKFLOW_REF:?GITHUB_WORKFLOW_REF is required.}"

expected_workflow_ref="$GITHUB_REPOSITORY/.github/workflows/sunder-sdk-release.yml@$GITHUB_REF"
[[ "$GITHUB_WORKFLOW_REF" == "$expected_workflow_ref" ]] || {
  printf 'Trusted publishing must run from %s, not %s.\n' "$expected_workflow_ref" "$GITHUB_WORKFLOW_REF" >&2
  exit 1
}

request_id_token() {
  local request_separator='&'
  local oidc_response
  local id_token
  local jwt_payload

  [[ "$ACTIONS_ID_TOKEN_REQUEST_URL" == *'?'* ]] || request_separator='?'
  if ! oidc_response="$(curl --fail --silent --show-error \
      -H 'Accept: application/json' \
      -H "Authorization: Bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
      "${ACTIONS_ID_TOKEN_REQUEST_URL}${request_separator}audience=npm%3Aregistry.npmjs.org")"; then
    return 1
  fi
  if ! id_token="$(jq -er '.value | select(type == "string" and length > 0)' <<< "$oidc_response")"; then
    return 1
  fi
  if ! jwt_payload="$(printf '%s' "$id_token" | node -e '
      let input = "";
      process.stdin.setEncoding("utf8");
      process.stdin.on("data", (chunk) => input += chunk);
      process.stdin.on("end", () => {
        const segment = input.trim().split(".")[1];
        if (segment === undefined) throw new Error("GitHub returned an invalid OIDC token.");
        process.stdout.write(Buffer.from(segment, "base64url").toString("utf8"));
      });
    ')"; then
    return 1
  fi
  if ! jq -e \
    --arg repository "$GITHUB_REPOSITORY" \
    --arg ref "$GITHUB_REF" \
    --arg sha "$GITHUB_SHA" \
    --arg workflow_ref "$expected_workflow_ref" '
      .aud == "npm:registry.npmjs.org"
      and .repository == $repository
      and .repository_visibility == "public"
      and .ref == $ref
      and .sha == $sha
      and .workflow_ref == $workflow_ref
      and .runner_environment == "github-hosted"
    ' <<< "$jwt_payload" >/dev/null; then
    return 1
  fi
  printf '%s' "$id_token"
}

temp_dir="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-npm-preflight.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT
seen_sdk=false
seen_tool=false
seen_create=false
unset NODE_AUTH_TOKEN NPM_TOKEN

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
    '@sunder/sdk') [[ "$seen_sdk" == false ]] || exit 1; seen_sdk=true ;;
    '@sunder/package-tool') [[ "$seen_tool" == false ]] || exit 1; seen_tool=true ;;
    'create-sunder-package') [[ "$seen_create" == false ]] || exit 1; seen_create=true ;;
    *) printf 'Unexpected npm release package: %s\n' "$package_name" >&2; exit 1 ;;
  esac

  escaped_name="$(jq -rn --arg value "$package_name" '$value | @uri')"
  id_token="$(request_id_token)"
  response_path="$temp_dir/$(basename "$archive").oidc.json"
  status="$(curl --silent --show-error \
    --output "$response_path" \
    --write-out '%{http_code}' \
    --request POST \
    -H "Authorization: Bearer $id_token" \
    "https://registry.npmjs.org/-/npm/v1/oidc/token/exchange/package/$escaped_name")"
  if [[ "$status" != 201 ]]; then
    message="$(jq -r '.message // "unknown npm OIDC exchange error"' "$response_path" 2>/dev/null || true)"
    printf "npm trusted-publisher preflight failed for '%s' (HTTP %s): %s\n" "$package_name" "$status" "$message" >&2
    printf 'Each real package must already exist and trust workflow filename sunder-sdk-release.yml with allowed action npm publish.\n' >&2
    exit 1
  fi
  jq -e '
    .token_type == "oidc"
    and (.token | type == "string" and length > 0)
    and (.created | type == "string" and length > 0)
    and (.expires | type == "string" and length > 0)
  ' "$response_path" >/dev/null

  npm publish "$archive" \
    --dry-run \
    --ignore-scripts \
    --access public \
    --tag "$npm_tag" \
    --provenance >/dev/null
  printf "Preflighted npm OIDC exchange and local publish dry-run for '%s@%s'; no registry write was attempted.\n" "$package_name" "$package_version"
done

[[ "$seen_sdk" == true && "$seen_tool" == true && "$seen_create" == true ]] \
  || { printf 'The coordinated npm package set is incomplete.\n' >&2; exit 1; }
