#!/usr/bin/env bash
set -euo pipefail
[[ $# == 3 ]] || { printf 'Usage: preflight-npm-publishing.sh <name> <version> <archive>\n' >&2; exit 2; }
name="$1" version="$2" archive="$3"
helpers="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 -B - "$helpers" "$version" <<'PY'
import sys
sys.path.insert(0, sys.argv[1])
from package_metadata import version_key
version_key(sys.argv[2])
if '+' in sys.argv[2]: raise SystemExit('Publication versions cannot contain build metadata')
PY
case "$name" in @sunder/sdk|@sunder/package-tool|create-sunder-package) ;; *) printf 'Unknown public npm package\n' >&2; exit 2 ;; esac
: "${NODE_AUTH_TOKEN:?Set NPM_TOKEN in sunder-core-release}"
: "${ACTIONS_ID_TOKEN_REQUEST_URL:?id-token write is required for npm provenance}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?id-token write is required for npm provenance}"
[[ "$GITHUB_REPOSITORY" == Younics/sunder-core && "$GITHUB_REF" == refs/heads/main \
  && "$GITHUB_WORKFLOW_REF" == Younics/sunder-core/.github/workflows/sunder-sdk-release.yml@refs/heads/main ]] \
  || { printf 'npm publication requires the protected-main release workflow\n' >&2; exit 1; }
tar -xOf "$archive" package/package.json | jq -e --arg name "$name" --arg version "$version" \
  '.name == $name and .version == $version and .private != true and .license == "MIT" and
   .repository.url == "git+https://github.com/Younics/sunder-core.git" and
   .publishConfig.access == "public" and .publishConfig.provenance == true' >/dev/null \
  || { printf 'Unexpected local npm publication identity\n' >&2; exit 1; }
npm whoami --registry=https://registry.npmjs.org >/dev/null
npm publish "$archive" --dry-run --ignore-scripts --access public --tag "accept-v$version" --registry=https://registry.npmjs.org --provenance >/dev/null
