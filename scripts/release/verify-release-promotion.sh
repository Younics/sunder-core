#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 7 ]] || {
  printf 'Usage: verify-release-promotion.sh <app|cli|host|sdk> <release-id> <tag> <source-sha> <publication-run-id> <publication-run-attempt> <evidence-directory>\n' >&2
  exit 2
}
component="$1"
release_id="$2"
tag="$3"
source_sha="$4"
run_id="$5"
run_attempt="$6"
evidence_directory="$7"
expected_draft="${EXPECTED_RELEASE_DRAFT:-true}"

[[ "$component" == "app" || "$component" == "cli" || "$component" == "host" || "$component" == "sdk" ]] \
  || { printf 'Unexpected release component: %s\n' "$component" >&2; exit 2; }
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Promotion source SHA must be a lowercase 40-character commit.\n' >&2; exit 2; }
[[ "$release_id" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Promotion release ID must be a positive decimal integer.\n' >&2; exit 2; }
[[ "$run_id" =~ ^[1-9][0-9]*$ && "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Publication run ID and attempt must be positive decimal integers.\n' >&2; exit 2; }
[[ "$expected_draft" == "true" || "$expected_draft" == "false" ]] \
  || { printf 'EXPECTED_RELEASE_DRAFT must be true or false.\n' >&2; exit 2; }
[[ -d "$evidence_directory" && ! -L "$evidence_directory" ]] \
  || { printf 'Publication evidence directory must be a real directory: %s\n' "$evidence_directory" >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }

for required_command in find gh git jq; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify release promotion.\n' "$required_command" >&2; exit 127; }
done

semver_pattern='(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?'
if [[ ! "$tag" =~ ^$component/v($semver_pattern)$ ]]; then
  printf 'Promotion tag %s does not match component %s with strict SemVer.\n' "$tag" "$component" >&2
  exit 2
fi
version="${BASH_REMATCH[1]}"
is_prerelease=false
if [[ "$version" == *-* ]]; then
  is_prerelease=true
  IFS='.' read -ra identifiers <<< "${version#*-}"
  for identifier in "${identifiers[@]}"; do
    if [[ "$identifier" =~ ^[0-9]+$ && "$identifier" == 0* && "$identifier" != "0" ]]; then
      printf 'Numeric SemVer prerelease identifiers must not contain leading zeroes.\n' >&2
      exit 2
    fi
  done
fi

case "$component" in
  app)
    expected_workflow='.github/workflows/sunder-app-release.yml'
    expected_workflow_name='Sunder App Release'
    ;;
  cli)
    expected_workflow='.github/workflows/sunder-cli-release.yml'
    expected_workflow_name='Sunder CLI Release'
    ;;
  host)
    expected_workflow='.github/workflows/sunder-host-release.yml'
    expected_workflow_name='Sunder Runtime Host Release'
    ;;
  sdk)
    expected_workflow='.github/workflows/sunder-sdk-release.yml'
    expected_workflow_name='Sunder SDK Release'
    ;;
esac

expected_artifacts=()
case "$component" in
  sdk)
    expected_artifacts=(
      "Sunder.Sdk.$version.nupkg"
      "Sunder.Sdk.Avalonia.$version.nupkg"
      "Sunder.Sdk.Stacks.$version.nupkg"
      "Sunder.Sdk.Worker.$version.nupkg"
      "Sunder.Package.Build.$version.nupkg"
      "Sunder.Package.Templates.$version.nupkg"
      "sunder-sdk-$version.tgz"
      "sunder-package-tool-$version.tgz"
      "create-sunder-package-$version.tgz"
      npm-MIT-LICENSE.txt
      npm-NOTICES.txt
      node-dependency-notices.json
    )
    ;;
  host)
    expected_artifacts=(
      "sunder-runtime-host-$version-win-x64.zip"
      "sunder-runtime-host-$version-win-arm64.zip"
      "sunder-runtime-host-$version-linux-x64.tar.gz"
      "sunder-runtime-host-$version-linux-arm64.tar.gz"
      "sunder-runtime-host-$version-osx-x64.tar.gz"
      "sunder-runtime-host-$version-osx-arm64.tar.gz"
    )
    ;;
  cli)
    expected_artifacts=(
      "sunder-cli-$version-win-x64.zip"
      "sunder-cli-$version-win-arm64.zip"
      "sunder-cli-$version-linux-x64.tar.gz"
      "sunder-cli-$version-linux-arm64.tar.gz"
      "sunder-cli-$version-osx-x64.tar.gz"
      "sunder-cli-$version-osx-arm64.tar.gz"
    )
    ;;
  app)
    runtimes=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
    for runtime in "${runtimes[@]}"; do
      channel="app-$runtime-stable"
      expected_artifacts+=(
        "Sunder-$version-$channel-full.nupkg"
        "releases.$channel.json"
      )
      case "$runtime" in
        win-*) expected_artifacts+=("Sunder-$channel-Setup.exe") ;;
        linux-*) expected_artifacts+=("Sunder-$channel.AppImage") ;;
        osx-*) expected_artifacts+=("Sunder-$channel.dmg") ;;
      esac
    done
    ;;
esac
evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)
expected_files=("${expected_artifacts[@]}" "${evidence_names[@]}")

actual_files=()
unexpected_entry="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
[[ -z "$unexpected_entry" ]] \
  || { printf 'Publication evidence contains a non-regular entry.\n' >&2; exit 1; }
while IFS= read -r file; do
  actual_files+=("$file")
done < <(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f -print | sort)
if [[ ${#actual_files[@]} -ne ${#expected_files[@]} ]]; then
  printf 'Expected exactly %s publication evidence files for %s, found %s.\n' \
    "${#expected_files[@]}" "$component" "${#actual_files[@]}" >&2
  exit 1
fi
for file_name in "${expected_files[@]}"; do
  [[ -f "$evidence_directory/$file_name" && ! -L "$evidence_directory/$file_name" ]] \
    || { printf 'Publication evidence is missing exact regular file %s.\n' "$file_name" >&2; exit 1; }
done

expected_artifacts_json="$(printf '%s\n' "${expected_artifacts[@]}" | jq -R . | jq -s 'sort')"
expected_ref="refs/tags/$tag"
expected_workflow_ref="$GH_REPO/$expected_workflow@$expected_ref"
jq -e \
  --arg repository "$GH_REPO" \
  --arg workflow "$expected_workflow_name" \
  --arg workflow_ref "$expected_workflow_ref" \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" \
  --argjson release_id "$release_id" \
  --arg ref "$expected_ref" \
  --arg ref_name "$tag" \
  --arg source_sha "$source_sha" \
  --argjson expected_artifacts "$expected_artifacts_json" '
    .schemaVersion == 1
    and .buildType == "https://sunderapp.io/provenance/github-actions/v1"
    and .invocation == {
      repository: $repository,
      workflow: $workflow,
      workflowRef: $workflow_ref,
      runId: $run_id,
      runAttempt: $run_attempt,
      releaseId: $release_id,
      ref: $ref,
      refName: $ref_name,
      sha: $source_sha
    }
    and .sources == [{name: "sunder-core", sha: $source_sha}]
    and ((.artifacts | map(.path) | sort) == $expected_artifacts)
  ' "$evidence_directory/release-provenance.json" >/dev/null \
  || { printf 'Publication evidence is not bound to the exact repository, workflow, release ID, tag, source, run, attempt, and artifact set.\n' >&2; exit 1; }

head_sha="$(git rev-parse HEAD)"
[[ "$head_sha" == "$source_sha" ]] \
  || { printf 'Promotion checkout %s does not match published source %s.\n' "$head_sha" "$source_sha" >&2; exit 1; }
GITHUB_SHA="$source_sha" GITHUB_REF="$expected_ref" \
  bash "$(dirname "$0")/verify-release-tag.sh" "$tag"

run_json="$(gh api -H 'Accept: application/vnd.github+json' "repos/$GH_REPO/actions/runs/$run_id")"
jq -e \
  --argjson run_id "$run_id" \
  --arg repository "$GH_REPO" \
  --arg workflow "$expected_workflow_name" \
  --arg path "$expected_workflow" \
  --arg source_sha "$source_sha" '
    .id == $run_id
    and .repository.full_name == $repository
    and .name == $workflow
    and .path == $path
    and .event == "push"
    and .head_sha == $source_sha
    and .status == "completed"
    and .conclusion == "success"
  ' <<< "$run_json" >/dev/null \
  || { printf 'Publication workflow run is not the successful tag-push run for the exact source.\n' >&2; exit 1; }

attempt_json="$(gh api -H 'Accept: application/vnd.github+json' \
  "repos/$GH_REPO/actions/runs/$run_id/attempts/$run_attempt")"
jq -e \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  --arg repository "$GH_REPO" \
  --arg path "$expected_workflow" \
  --arg source_sha "$source_sha" '
    .id == $run_id
    and .run_attempt == $run_attempt
    and .repository.full_name == $repository
    and .path == $path
    and .event == "push"
    and .head_sha == $source_sha
  ' <<< "$attempt_json" >/dev/null \
  || { printf 'Publication evidence does not match the selected workflow run attempt.\n' >&2; exit 1; }

release_state="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/$release_id")"
jq -e \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --argjson expected_draft "$expected_draft" \
  --argjson is_prerelease "$is_prerelease" '
    .id == $release_id
    and .tag_name == $tag
    and .draft == $expected_draft
    and .prerelease == $is_prerelease
  ' <<< "$release_state" >/dev/null \
  || { printf 'GitHub release state does not match the selected tag, draft state, or SemVer kind.\n' >&2; exit 1; }

EXPECTED_RELEASE_DRAFT="$expected_draft" \
  bash "$(dirname "$0")/verify-github-release-assets.sh" \
    "$release_id" "$tag" "$evidence_directory" "${expected_files[@]}"

printf 'Verified %s publication evidence for release %s (%s) at %s from run %s attempt %s.\n' \
  "$component" "$release_id" "$tag" "$source_sha" "$run_id" "$run_attempt"
