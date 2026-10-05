#!/usr/bin/env bash
set -euo pipefail

tag="${1:?Usage: verify-release-tag.sh <tag>}"
[[ $# -eq 1 ]] || { printf 'Usage: verify-release-tag.sh <tag>\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk|runtime)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected release tag: %s\n' "$tag" >&2; exit 2; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }
: "${GITHUB_SHA:?GITHUB_SHA is required.}"
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'GITHUB_SHA must be a lowercase 40-character commit.\n' >&2; exit 2; }

for required_command in gh git jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify the release tag.\n' "$required_command" >&2; exit 127; }
done

head_commit="$(git rev-parse HEAD)"
[[ "$head_commit" == "$GITHUB_SHA" ]] \
  || { printf 'Checked-out commit %s does not match workflow commit %s.\n' "$head_commit" "$GITHUB_SHA" >&2; exit 1; }
[[ "${GITHUB_REF:-}" == "refs/tags/$tag" ]] \
  || { printf 'Workflow ref %s does not match release tag refs/tags/%s.\n' "${GITHUB_REF:-<unset>}" "$tag" >&2; exit 1; }

encoded_tag="$(jq -rn --arg value "$tag" '$value | @uri')"
reference="$(gh api -H 'Accept: application/vnd.github+json' "repos/$GH_REPO/git/ref/tags/$encoded_tag")"
object_type="$(jq -er '.object.type | select(. == "commit" or . == "tag")' <<< "$reference")"
object_sha="$(jq -er '.object.sha | select(test("^[0-9a-f]{40}$"))' <<< "$reference")"
depth=0
while [[ "$object_type" == "tag" ]]; do
  depth=$((depth + 1))
  [[ $depth -le 8 ]] || { printf "Release tag '%s' has an excessive annotation chain.\n" "$tag" >&2; exit 1; }
  annotation="$(gh api -H 'Accept: application/vnd.github+json' "repos/$GH_REPO/git/tags/$object_sha")"
  object_type="$(jq -er '.object.type | select(. == "commit" or . == "tag")' <<< "$annotation")"
  object_sha="$(jq -er '.object.sha | select(test("^[0-9a-f]{40}$"))' <<< "$annotation")"
done

[[ "$object_sha" == "$GITHUB_SHA" ]] \
  || { printf "Remote release tag '%s' resolves to %s, not workflow commit %s.\n" "$tag" "$object_sha" "$GITHUB_SHA" >&2; exit 1; }
printf "Verified immutable release input '%s' at commit %s.\n" "$tag" "$GITHUB_SHA"
