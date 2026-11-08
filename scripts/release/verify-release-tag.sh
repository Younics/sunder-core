#!/usr/bin/env bash
set -euo pipefail

tag="${1:?Usage: verify-release-tag.sh <tag> [artifact-source]}"
[[ $# -ge 1 && $# -le 2 ]] || { printf 'Usage: verify-release-tag.sh <tag> [artifact-source]\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected release tag: %s\n' "$tag" >&2; exit 2; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }
: "${GITHUB_SHA:?GITHUB_SHA is required.}"
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'GITHUB_SHA must be a lowercase 40-character commit.\n' >&2; exit 2; }
source="${2:-$GITHUB_SHA}"
[[ "$source" =~ ^[0-9a-f]{40}$ ]] || { printf 'Invalid artifact source.\n' >&2; exit 2; }

for required_command in gh git jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify the release tag.\n' "$required_command" >&2; exit 127; }
done

head_commit="$(git rev-parse HEAD)"
[[ "$head_commit" == "$GITHUB_SHA" ]] \
  || { printf 'Checked-out commit %s does not match workflow commit %s.\n' "$head_commit" "$GITHUB_SHA" >&2; exit 1; }
if [[ "${GITHUB_REF:-}" == refs/heads/main ]]; then
  [[ "${GITHUB_REF_PROTECTED:-}" == true && "${GITHUB_WORKFLOW_SHA:-}" == "$GITHUB_SHA" \
     && "${GITHUB_WORKFLOW_REF:-}" == "$GH_REPO/.github/workflows/sunder-sdk-release.yml@refs/heads/main" \
     && ( "${GITHUB_EVENT_NAME:-}" == push || "${GITHUB_EVENT_NAME:-}" == workflow_dispatch ) ]] \
    || { printf 'Native publication requires the protected-main workflow.\n' >&2; exit 1; }
  git merge-base --is-ancestor "$source" "$GITHUB_SHA" \
    || { printf 'Retained artifact source must be an ancestor of the workflow.\n' >&2; exit 1; }
else
  # Historical exact-tag evidence remains verifiable from its own checkout.
  [[ "${GITHUB_REF:-}" == "refs/tags/$tag" && "$source" == "$GITHUB_SHA" ]] \
    || { printf 'Workflow ref %s does not match release tag refs/tags/%s.\n' "${GITHUB_REF:-<unset>}" "$tag" >&2; exit 1; }
fi

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

[[ "$object_sha" == "$source" ]] \
  || { printf "Remote release tag '%s' resolves to %s, not workflow commit/artifact source %s.\n" "$tag" "$object_sha" "$source" >&2; exit 1; }
printf "Verified immutable release input '%s' at commit %s.\n" "$tag" "$source"
