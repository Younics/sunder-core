#!/usr/bin/env bash
set -euo pipefail

tag="${1:?Usage: resolve-github-release.sh <tag>}"
[[ $# -eq 1 ]] || { printf 'Usage: resolve-github-release.sh <tag>\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected release tag: %s\n' "$tag" >&2; exit 2; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }

for required_command in gh jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to resolve a GitHub release.\n' "$required_command" >&2; exit 127; }
done

matches='[]'
page=1
while [[ "$page" -le 100 ]]; do
  page_json="$(gh api -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/$GH_REPO/releases?per_page=100&page=$page")"
  jq -e '
    type == "array"
    and all(.[];
      (.id | type == "number" and . > 0 and floor == .)
      and (.tag_name | type == "string")
      and (.draft | type == "boolean"))
  ' <<< "$page_json" >/dev/null \
    || { printf 'GitHub returned an invalid release listing.\n' >&2; exit 1; }
  page_matches="$(jq -c --arg tag "$tag" '[.[] | select(.tag_name == $tag)]' <<< "$page_json")"
  matches="$(jq -cn --argjson current "$matches" --argjson page "$page_matches" '$current + $page')"
  page_count="$(jq 'length' <<< "$page_json")"
  if [[ "$page_count" -lt 100 ]]; then
    break
  fi
  if [[ "$page" == 100 ]]; then
    printf 'GitHub release listing exceeded the fail-closed pagination bound.\n' >&2
    exit 1
  fi
  page=$((page + 1))
done

match_count="$(jq 'length' <<< "$matches")"
case "$match_count" in
  0) exit 4 ;;
  1) jq -c '.[0]' <<< "$matches" ;;
  *)
    printf "GitHub contains %s releases for tag '%s'; refusing ambiguous draft mutation.\n" "$match_count" "$tag" >&2
    exit 1
    ;;
esac
