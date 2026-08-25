#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-production-acceptance.sh"
[[ -f "$verifier" ]] || { printf 'Production acceptance verifier not found.\n' >&2; exit 1; }
for required_command in jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the production acceptance fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the production acceptance fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-production-acceptance-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
evidence="$fixture_dir/evidence"
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$evidence" "$mock_bin"
component='app'
release_id='4242'
tag='app/v1.2.3'
source_sha='0123456789012345678901234567890123456789'
publication_run_id='12345'
publication_run_attempt='1'
production_run_id='54321'
production_run_attempt='2'
workflow_path='.github/workflows/sunder-production-acceptance.yml'
workflow_branch='main'
workflow_ref="refs/heads/$workflow_branch"
workflow_sha='abcdefabcdefabcdefabcdefabcdefabcdefabcd'

printf 'native lifecycle, rollback, browser, and migration evidence\n' > "$evidence/production-results.txt"
jq -n \
  --arg component "$component" \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg source_sha "$source_sha" \
  --arg publication_run_id "$publication_run_id" \
  --arg publication_run_attempt "$publication_run_attempt" \
  --arg production_run_id "$production_run_id" \
  --arg production_run_attempt "$production_run_attempt" \
  --arg workflow_path "$workflow_path" \
  --arg workflow_ref "$workflow_ref" \
  --arg workflow_sha "$workflow_sha" '
  {
    schemaVersion: 1,
    kind: "https://sunderapp.io/acceptance/production/v1",
    decision: "accepted",
    release: {
      repository: "Younics/sunder-core",
      component: $component,
      releaseId: $release_id,
      tag: $tag,
      sourceSha: $source_sha,
      publicationRunId: $publication_run_id,
      publicationRunAttempt: $publication_run_attempt
    },
    invocation: {
      repository: "Younics/sunder-core",
      workflowPath: $workflow_path,
      workflowRef: $workflow_ref,
      workflowSha: $workflow_sha,
      runId: $production_run_id,
      runAttempt: $production_run_attempt
    },
    checks: [
      {id:"released-byte-provenance",status:"passed",evidence:["production-results.txt"]},
      {id:"production-install",status:"passed",evidence:["production-results.txt"]},
      {id:"production-update",status:"passed",evidence:["production-results.txt"]},
      {id:"production-recovery",status:"passed",evidence:["production-results.txt"]},
      {id:"production-migration",status:"passed",evidence:["production-results.txt"]},
      {id:"app-production-path",status:"passed",evidence:["production-results.txt"]}
    ]
  }
' > "$evidence/production-acceptance.json"

files='[]'
for path in "$evidence/production-acceptance.json" "$evidence/production-results.txt"; do
  name="$(basename "$path")"
  digest="$(hash_file "$path")"
  size="$(wc -c < "$path" | tr -d '[:space:]')"
  files="$(jq -cn --argjson files "$files" --arg path "$name" --arg sha "$digest" --argjson size "$size" \
    '$files + [{path:$path,sha256:$sha,size:$size}]')"
done
jq -n --argjson files "$files" '{schemaVersion:1,files:$files}' \
  > "$evidence/production-acceptance-manifest.json"
cp "$evidence/production-acceptance.json" "$fixture_dir/original-decision.json"
cp "$evidence/production-results.txt" "$fixture_dir/original-results.txt"

jq -n \
  --argjson id "$production_run_id" \
  --arg path "$workflow_path" \
  --arg branch "$workflow_branch" \
  --arg sha "$workflow_sha" '
  {
    id:$id,
    repository:{full_name:"Younics/sunder-core"},
    name:"Sunder Production Acceptance",
    path:$path,
    head_branch:$branch,
    head_sha:$sha,
    event:"workflow_dispatch",
    status:"completed",
    conclusion:"success"
  }
' > "$fixture_dir/run.json"
jq -n \
  --argjson id "$production_run_id" \
  --argjson attempt "$production_run_attempt" \
  --arg path "$workflow_path" \
  --arg branch "$workflow_branch" \
  --arg sha "$workflow_sha" '
  {
    id:$id,
    run_attempt:$attempt,
    repository:{full_name:"Younics/sunder-core"},
    path:$path,
    head_branch:$branch,
    head_sha:$sha,
    event:"workflow_dispatch",
    status:"completed",
    conclusion:"success"
  }
' > "$fixture_dir/attempt.json"

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || exit 70
endpoint="${*: -1}"
case "$endpoint" in
  */attempts/*) command cat "$MOCK_ATTEMPT_JSON" ;;
  */actions/runs/*) command cat "$MOCK_RUN_JSON" ;;
  *) exit 71 ;;
esac
EOF
chmod +x "$mock_bin/gh"
export GH_REPO='Younics/sunder-core'
export MOCK_ATTEMPT_JSON="$fixture_dir/attempt.json"
export MOCK_RUN_JSON="$fixture_dir/run.json"

PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence"

jq '.release.releaseId = 9999' "$evidence/production-acceptance.json" > "$fixture_dir/wrong-release.json"
cp "$fixture_dir/wrong-release.json" "$evidence/production-acceptance.json"
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/wrong-release.log" 2>&1; then
  printf 'Production acceptance verifier accepted another numeric release.\n' >&2
  exit 1
fi
grep -Fq 'not bound to the selected release' "$fixture_dir/wrong-release.log" \
  || { printf 'Wrong release binding did not fail at the decision gate.\n' >&2; exit 1; }

cp "$fixture_dir/original-decision.json" "$evidence/production-acceptance.json"
jq '.invocation.workflowSha = "1111111111111111111111111111111111111111"' \
  "$evidence/production-acceptance.json" > "$fixture_dir/wrong-evidence-workflow.json"
cp "$fixture_dir/wrong-evidence-workflow.json" "$evidence/production-acceptance.json"
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/wrong-evidence-workflow.log" 2>&1; then
  printf 'Production acceptance verifier accepted evidence from another workflow revision.\n' >&2
  exit 1
fi
grep -Fq 'not bound to the selected release' "$fixture_dir/wrong-evidence-workflow.log" \
  || { printf 'Wrong evidence workflow SHA did not fail at the decision gate.\n' >&2; exit 1; }

cp "$fixture_dir/original-decision.json" "$evidence/production-acceptance.json"
rm "$evidence/production-results.txt"
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/incomplete.log" 2>&1; then
  printf 'Production acceptance verifier accepted an incomplete archive.\n' >&2
  exit 1
fi
grep -Fq 'unmanifested or missing files' "$fixture_dir/incomplete.log" \
  || { printf 'Incomplete production archive did not fail at the completeness gate.\n' >&2; exit 1; }

cp "$fixture_dir/original-results.txt" "$evidence/production-results.txt"
mkdir "$evidence/unmanifested-directory"
if PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/non-regular.log" 2>&1; then
  printf 'Production acceptance verifier accepted an unmanifested directory.\n' >&2
  exit 1
fi
grep -Fq 'non-regular entry' "$fixture_dir/non-regular.log" \
  || { printf 'Unmanifested production directory did not fail at the completeness gate.\n' >&2; exit 1; }
rmdir "$evidence/unmanifested-directory"

jq '.conclusion = "failure"' "$fixture_dir/run.json" > "$fixture_dir/failed-run.json"
MOCK_RUN_JSON="$fixture_dir/failed-run.json" PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/failed-run.log" 2>&1 && {
    printf 'Production acceptance verifier accepted a failed trusted run.\n' >&2
    exit 1
  }
grep -Fq 'not a successful trusted workflow dispatch' "$fixture_dir/failed-run.log" \
  || { printf 'Failed production run did not fail at the trusted-run gate.\n' >&2; exit 1; }

jq '.head_sha = "1111111111111111111111111111111111111111"' \
  "$fixture_dir/run.json" > "$fixture_dir/wrong-run-sha.json"
if MOCK_RUN_JSON="$fixture_dir/wrong-run-sha.json" PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/wrong-run-sha.log" 2>&1; then
  printf 'Production acceptance verifier accepted a run from another workflow SHA.\n' >&2
  exit 1
fi
grep -Fq 'not a successful trusted workflow dispatch' "$fixture_dir/wrong-run-sha.log" \
  || { printf 'Wrong production run SHA did not fail at the trusted-run gate.\n' >&2; exit 1; }

jq '.head_branch = "review/untrusted"' \
  "$fixture_dir/attempt.json" > "$fixture_dir/wrong-attempt-branch.json"
if MOCK_ATTEMPT_JSON="$fixture_dir/wrong-attempt-branch.json" PATH="$mock_bin:$PATH" bash "$verifier" \
  "$component" "$release_id" "$tag" "$source_sha" \
  "$publication_run_id" "$publication_run_attempt" \
  "$production_run_id" "$production_run_attempt" "$workflow_sha" "$evidence" \
  > "$fixture_dir/wrong-attempt-branch.log" 2>&1; then
  printf 'Production acceptance verifier accepted an attempt from another workflow branch.\n' >&2
  exit 1
fi
grep -Fq 'successful selected trusted run attempt' "$fixture_dir/wrong-attempt-branch.log" \
  || { printf 'Wrong production attempt branch did not fail at the selected-attempt gate.\n' >&2; exit 1; }

printf 'Production acceptance fixture passed: promotion requires a complete archive from a successful trusted run and attempt bound to the exact workflow ref, workflow SHA, publication source, and numeric release.\n'
