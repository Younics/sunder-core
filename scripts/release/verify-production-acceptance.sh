#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 10 ]] || {
  printf 'Usage: verify-production-acceptance.sh <component> <release-id> <tag> <publication-source-sha> <publication-run-id> <publication-run-attempt> <production-run-id> <production-run-attempt> <production-workflow-sha> <evidence-directory>\n' >&2
  exit 2
}
component="$1"
release_id="$2"
tag="$3"
source_sha="$4"
publication_run_id="$5"
publication_run_attempt="$6"
production_run_id="$7"
production_run_attempt="$8"
production_workflow_sha="$9"
evidence_directory="${10}"
candidate_only="${SUNDER_PRODUCTION_ACCEPTANCE_CANDIDATE_ONLY:-false}"
workflow_path='.github/workflows/sunder-production-acceptance.yml'
workflow_name='Sunder Production Acceptance'
workflow_branch='main'
workflow_ref="refs/heads/$workflow_branch"
expected_coordinator_sha="${SUNDER_PRODUCTION_ACCEPTANCE_COORDINATOR_SHA256:-}"

[[ "$component" == "app" || "$component" == "cli" || "$component" == "host" || "$component" == "sdk" ]] \
  || { printf 'Unexpected production acceptance component: %s\n' "$component" >&2; exit 2; }
[[ "$release_id" =~ ^[1-9][0-9]*$ \
    && "$publication_run_id" =~ ^[1-9][0-9]*$ \
    && "$publication_run_attempt" =~ ^[1-9][0-9]*$ \
    && "$production_run_id" =~ ^[1-9][0-9]*$ \
    && "$production_run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release, publication, and production run coordinates must be positive decimal integers.\n' >&2; exit 2; }
[[ "$tag" =~ ^$component/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Production acceptance tag does not match component %s.\n' "$component" >&2; exit 2; }
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Production acceptance publication source must be a lowercase 40-character commit.\n' >&2; exit 2; }
[[ "$production_workflow_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { printf 'Production acceptance workflow SHA must be a lowercase 40-character commit.\n' >&2; exit 2; }
[[ "$candidate_only" == "true" || "$candidate_only" == "false" ]] \
  || { printf 'SUNDER_PRODUCTION_ACCEPTANCE_CANDIDATE_ONLY must be true or false.\n' >&2; exit 2; }
[[ -z "$expected_coordinator_sha" || "$expected_coordinator_sha" =~ ^[0-9a-f]{64}$ ]] \
  || { printf 'SUNDER_PRODUCTION_ACCEPTANCE_COORDINATOR_SHA256 must be one lowercase SHA-256 digest.\n' >&2; exit 2; }
[[ -d "$evidence_directory" && ! -L "$evidence_directory" ]] \
  || { printf 'Production acceptance evidence directory must be a real directory.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == "Younics/sunder-core" ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }

for required_command in find gh jq python3 sort; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify production acceptance.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify production acceptance.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

decision="$evidence_directory/production-acceptance.json"
manifest="$evidence_directory/production-acceptance-manifest.json"
[[ -f "$decision" && ! -L "$decision" && -f "$manifest" && ! -L "$manifest" ]] \
  || { printf 'Production acceptance decision or manifest is missing.\n' >&2; exit 1; }

required_component_check="${component}-production-path"
jq -e \
  --arg repository "$GH_REPO" \
  --arg workflow_path "$workflow_path" \
  --arg workflow_ref "$workflow_ref" \
  --arg workflow_sha "$production_workflow_sha" \
  --arg component "$component" \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg source_sha "$source_sha" \
  --arg publication_run_id "$publication_run_id" \
  --arg publication_run_attempt "$publication_run_attempt" \
  --arg production_run_id "$production_run_id" \
  --arg production_run_attempt "$production_run_attempt" \
  --arg expected_coordinator_sha "$expected_coordinator_sha" \
  --arg component_check "$required_component_check" '
    .schemaVersion == 1
    and keys == ["checks","decision","invocation","kind","nativeReceipts","release","schemaVersion"]
    and .kind == "https://sunderapp.io/acceptance/production/v1"
    and .decision == "accepted"
    and .release == {
      repository: $repository,
      component: $component,
      releaseId: $release_id,
      tag: $tag,
      sourceSha: $source_sha,
      publicationRunId: $publication_run_id,
      publicationRunAttempt: $publication_run_attempt
    }
    and .invocation == {
      repository: $repository,
      workflowPath: $workflow_path,
      workflowRef: $workflow_ref,
      workflowSha: $workflow_sha,
      runId: $production_run_id,
      runAttempt: $production_run_attempt,
      coordinatorSha256: .invocation.coordinatorSha256
    }
    and (.invocation.coordinatorSha256 | test("^[0-9a-f]{64}$"))
    and ($expected_coordinator_sha == "" or .invocation.coordinatorSha256 == $expected_coordinator_sha)
    and (.checks | type == "array" and length >= 5)
    and ([.checks[].id] | length == (unique | length))
    and all(.checks[];
      (.id | test("^[a-z][a-z0-9-]+$"))
      and .status == "passed"
      and (.evidence | type == "array" and length > 0)
      and all(.evidence[]; type == "string" and length > 0))
    and ([.checks[] | select(.id == "released-byte-provenance")] | length == 1)
    and ([.checks[] | select(.id == "production-install")] | length == 1)
    and ([.checks[] | select(.id == "production-update")] | length == 1)
    and ([.checks[] | select(.id == "production-recovery")] | length == 1)
    and ([.checks[] | select(.id == $component_check)] | length == 1)
    and ([.nativeReceipts[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
    and all(.nativeReceipts[]; keys == ["driverSha256","rid","sha256"]
      and (.sha256 | test("^[0-9a-f]{64}$")) and (.driverSha256 | test("^[0-9a-f]{64}$")))
  ' "$decision" >/dev/null \
  || { printf 'Production acceptance decision is incomplete or not bound to the selected release.\n' >&2; exit 1; }

jq -e '
  .schemaVersion == 1
  and (.files | type == "array" and length >= 2)
  and ([.files[].path] | length == (unique | length))
  and all(.files[];
    (.path | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
    and .path != "production-acceptance-manifest.json"
    and (.sha256 | test("^[0-9a-f]{64}$"))
    and (.size | type == "number" and . >= 0 and floor == .))
  and ([.files[] | select(.path == "production-acceptance.json")] | length == 1)
' "$manifest" >/dev/null \
  || { printf 'Production acceptance evidence manifest is invalid.\n' >&2; exit 1; }
jq -e -s '
  .[0] as $decision
  | .[1] as $manifest
  | ($decision.checks | map(.evidence[]) | unique | sort) as $referenced
  | ($manifest.files | map(.path) | map(select(. != "production-acceptance.json")) | sort) as $archived
  | $referenced == $archived
' "$decision" "$manifest" >/dev/null \
  || { printf 'Production acceptance checks do not reference the complete archived evidence set.\n' >&2; exit 1; }

actual_names=()
unexpected_entry="$(find "$evidence_directory" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
[[ -z "$unexpected_entry" ]] \
  || { printf 'Production acceptance artifact contains a non-regular entry.\n' >&2; exit 1; }
while IFS= read -r path; do
  actual_names+=("$(basename "$path")")
done < <(find "$evidence_directory" -mindepth 1 -maxdepth 1 -type f -print | sort)
expected_count="$(( $(jq '.files | length' "$manifest") + 1 ))"
[[ ${#actual_names[@]} -eq "$expected_count" ]] \
  || { printf 'Production acceptance artifact contains unmanifested or missing files.\n' >&2; exit 1; }
for asset_name in "${actual_names[@]}"; do
  [[ "$asset_name" == 'production-acceptance-manifest.json' ]] && continue
  manifest_count="$(jq --arg name "$asset_name" '[.files[] | select(.path == $name)] | length' "$manifest")"
  [[ "$manifest_count" == 1 ]] \
    || { printf "Production evidence file '%s' is not uniquely manifested.\n" "$asset_name" >&2; exit 1; }
done
while IFS=$'\t' read -r asset_name expected_sha expected_size; do
  asset_path="$evidence_directory/$asset_name"
  [[ -f "$asset_path" && ! -L "$asset_path" ]] \
    || { printf "Manifested production evidence '%s' is missing.\n" "$asset_name" >&2; exit 1; }
  [[ "$(hash_file "$asset_path")" == "$expected_sha" \
      && "$(wc -c < "$asset_path" | tr -d '[:space:]')" == "$expected_size" ]] \
    || { printf "Production evidence '%s' does not match its manifest.\n" "$asset_name" >&2; exit 1; }
done < <(jq -r '.files[] | [.path, .sha256, (.size | tostring)] | @tsv' "$manifest")

rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
for rid in "${rids[@]}"; do
  [[ -f "$evidence_directory/core-production-$rid.json" ]] \
    || { printf "Production acceptance is missing durable native receipts for '%s'.\n" "$rid" >&2; exit 1; }
done
jq -s -e \
  --arg repository "$GH_REPO" \
  --arg component "$component" \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg source_sha "$source_sha" \
  --arg publication_run_id "$publication_run_id" \
  --arg publication_run_attempt "$publication_run_attempt" \
  --arg workflow_sha "$production_workflow_sha" \
  --arg run_id "$production_run_id" \
  --arg run_attempt "$production_run_attempt" '
    length == 6
    and ([.[].rid] | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
    and all(.[];
      keys == ["checks","decision","evidence","invocation","kind","release","rid","runnerProfile","runtimeInstallations","schemaVersion"]
      and .schemaVersion == 1 and .kind == "https://sunderapp.io/acceptance/core-native/v1"
      and .decision == "accepted"
      and .release == {
        repository:$repository,component:$component,releaseId:$release_id,tag:$tag,sourceSha:$source_sha,
        publicationRunId:$publication_run_id,publicationRunAttempt:$publication_run_attempt
      }
      and .invocation.workflowPath == ".github/workflows/sunder-production-acceptance.yml"
      and .invocation.workflowRef == "refs/heads/main"
      and .invocation.workflowSha == $workflow_sha
      and .invocation.runId == $run_id
      and .invocation.runAttempt == $run_attempt
      and (.invocation.driverSha256 | test("^[0-9a-f]{64}$"))
      and (.checks | type == "array" and length > 0)
      and ([.checks[].id] | length) == ([.checks[].id] | unique | length)
      and ([.checks[].id] | index("official-runtime-install-launch") != null)
      and ([.checks[].id] | index("official-runtime-source-checksum") != null)
      and ([.checks[].id] | index("ambient-runtime-isolation") != null)
      and all(.checks[]; .status == "passed" and (.evidenceIds | type == "array" and length > 0))
      and (.evidence | type == "array" and length > 0)
      and all(.evidence[]; (.sha256 | test("^[0-9a-f]{64}$")) and (.size | type == "number" and floor == . and . > 0))
      and ([.checks[].evidenceIds[]] | unique | sort) == ([.evidence[].id] | sort))
  ' "$evidence_directory"/core-production-*.json >/dev/null \
  || { printf 'Durable Core native receipts do not bind the selected invocation and reviewed driver digests.\n' >&2; exit 1; }
for rid in "${rids[@]}"; do
  core_receipt="$evidence_directory/core-production-$rid.json"
  jq -e --arg rid "$rid" --arg sha "$(hash_file "$core_receipt")" \
    --arg driver "$(jq -er '.invocation.driverSha256' "$core_receipt")" '
      [.nativeReceipts[] | select(.rid == $rid and .sha256 == $sha and .driverSha256 == $driver)] | length == 1
    ' "$decision" >/dev/null \
    || { printf 'Native acceptance bytes/driver differ from the coordinator decision.\n' >&2; exit 1; }
  python3 -B "$(dirname "$0")/official_runtime_evidence.py" "$core_receipt" --rid "$rid" \
    || { printf "Durable Core native evidence for '%s' does not contain verified Host installation records.\n" "$rid" >&2; exit 1; }
done

if [[ "$candidate_only" == "true" ]]; then
  printf 'Verified production acceptance candidate for %s release %s (%s), current run %s attempt %s.\n' \
    "$component" "$release_id" "$tag" "$production_run_id" "$production_run_attempt"
  exit 0
fi

run_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/actions/runs/$production_run_id")"
jq -e \
  --argjson run_id "$production_run_id" \
  --arg repository "$GH_REPO" \
  --arg workflow "$workflow_name" \
  --arg path "$workflow_path" \
  --arg workflow_branch "$workflow_branch" \
  --arg workflow_sha "$production_workflow_sha" '
    .id == $run_id
    and .repository.full_name == $repository
    and .name == $workflow
    and .path == $path
    and .head_branch == $workflow_branch
    and .head_sha == $workflow_sha
    and .event == "workflow_dispatch"
    and .status == "completed"
    and .conclusion == "success"
  ' <<< "$run_json" >/dev/null \
  || { printf 'Production acceptance run is not a successful trusted workflow dispatch.\n' >&2; exit 1; }

attempt_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/actions/runs/$production_run_id/attempts/$production_run_attempt")"
jq -e \
  --argjson run_id "$production_run_id" \
  --argjson run_attempt "$production_run_attempt" \
  --arg repository "$GH_REPO" \
  --arg path "$workflow_path" \
  --arg workflow_branch "$workflow_branch" \
  --arg workflow_sha "$production_workflow_sha" '
    .id == $run_id
    and .run_attempt == $run_attempt
    and .repository.full_name == $repository
    and .path == $path
    and .head_branch == $workflow_branch
    and .head_sha == $workflow_sha
    and .event == "workflow_dispatch"
    and .status == "completed"
    and .conclusion == "success"
  ' <<< "$attempt_json" >/dev/null \
  || { printf 'Production acceptance evidence does not match a successful selected trusted run attempt.\n' >&2; exit 1; }

printf 'Verified durable production acceptance for %s release %s (%s), run %s attempt %s.\n' \
  "$component" "$release_id" "$tag" "$production_run_id" "$production_run_attempt"
