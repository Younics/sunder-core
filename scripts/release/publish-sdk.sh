#!/usr/bin/env bash
# One selected package. Existing uploads are checked, never rebuilt or overwritten.
set -euo pipefail
action="${1:?Action is required}"
name="${2:?Package name is required}"
version="${3:?Package version is required}"
archive="${4:?Tested archive is required}"
helpers="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
kind="${action#verify-}"
case "$kind" in nuget|npm) ;; *) exit 2 ;; esac
retry() {
  local attempt
  for attempt in {1..30}; do
    if "$@"; then return 0; fi
    [[ "$attempt" == 30 ]] || sleep 10
  done
  return 1
}
history="$(python3 -B "$helpers/sdk_release.py" history --kind "$kind" --name "$name" --version "$version")"
if [[ "$action" != verify-* ]] && [[ "$(jq -r .present <<< "$history")" != true ]]; then
  [[ "$(jq -r .newer <<< "$history")" == true ]] || { printf 'Refusing to backfill %s@%s below a newer publication.\n' "$name" "$version" >&2; exit 1; }
  if [[ "$kind" == nuget ]]; then
    : "${NUGET_API_KEY:?NuGet trusted publishing credential is required}"
    dotnet nuget push "$archive" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate || true
  else
    bash "$helpers/preflight-npm-publishing.sh" "$name" "$version" "$archive"
    npm publish "$archive" --ignore-scripts --access public --tag "accept-v$version" --registry=https://registry.npmjs.org --provenance || true
  fi
fi
if [[ "$kind" == nuget ]]; then
  remote="$(mktemp -d)"
  trap 'rm -rf "$remote"' EXIT
  download() {
    local lower_name lower_version
    lower_name="$(tr '[:upper:]' '[:lower:]' <<< "$name")"
    lower_version="$(tr '[:upper:]' '[:lower:]' <<< "$version")"
    curl --fail --silent --show-error --proto '=https' --max-redirs 0 --max-time 120 \
      "https://api.nuget.org/v3-flatcontainer/$lower_name/$lower_version/$lower_name.$lower_version.nupkg" -o "$remote/package.nupkg"
  }
  retry download
  bash "$helpers/verify-nuget-package.sh" "$archive" "$remote/package.nupkg"
else
  channel=latest
  [[ "$version" != *-* ]] || channel=next
  verify() { node "$helpers/verify-npm-release.mjs" "$name" "$version" "$archive" "$GITHUB_SHA" "$1"; }
  if [[ "$action" != verify-* ]]; then
    : "${NODE_AUTH_TOKEN:?npm token is required}"
    retry verify publication
    # Never move a newer consumer channel backwards on a stale retry.
    current="$(npm view "$name" "dist-tags.$channel" --json --registry=https://registry.npmjs.org)"
    python3 -B - "$helpers" "$version" "$current" <<'PY'
import json, sys
sys.path.insert(0, sys.argv[1])
from package_metadata import version_key
current = json.loads(sys.argv[3]) if sys.argv[3].strip() else None
if current and version_key(current) > version_key(sys.argv[2]):
    raise SystemExit('Refusing to move an npm consumer channel backwards')
PY
    npm dist-tag add "$name@$version" "$channel" --registry=https://registry.npmjs.org
  fi
  retry verify promotion
fi
