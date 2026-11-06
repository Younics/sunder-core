#!/usr/bin/env bash
# One retained package. Registry acceptance completes publication; indexing is asynchronous.
set -euo pipefail
kind="${1:?Package kind is required}"
name="${2:?Package name is required}"
version="${3:?Package version is required}"
archive="${4:?Tested archive is required}"
source="${5:?Artifact source commit is required}"
helpers="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
case "$kind" in nuget|npm) ;; *) exit 2 ;; esac
history="$(python3 -B "$helpers/sdk_release.py" history --kind "$kind" --name "$name" --version "$version")"
if [[ "$(jq -r .present <<< "$history")" == true ]]; then
  printf 'Skipping already published %s@%s.\n' "$name" "$version"
  exit 0
fi
[[ "$(jq -r .newer <<< "$history")" == true ]] || { printf 'Refusing to backfill %s@%s below a newer publication.\n' "$name" "$version" >&2; exit 1; }
if [[ "$kind" == nuget ]]; then
  : "${NUGET_API_KEY:?NuGet trusted publishing credential is required}"
  dotnet nuget push "$archive" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate
else
  # npm's automatic provenance names this workflow's commit. A later workflow may
  # skip existing versions, but must not attest an older tarball as its own source.
  [[ "$source" == "$GITHUB_SHA" ]] || { printf 'Unpublished npm recovery requires the original source workflow: %s@%s (%s).\n' "$name" "$version" "$source" >&2; exit 1; }
  channel=latest
  [[ "$version" != *-* ]] || channel=next
  bash "$helpers/preflight-npm-publishing.sh" "$name" "$version" "$archive"
  npm publish "$archive" --ignore-scripts --access public --tag "$channel" --registry=https://registry.npmjs.org --provenance
fi
printf 'Registry accepted %s@%s; registry processing continues asynchronously.\n' "$name" "$version"
