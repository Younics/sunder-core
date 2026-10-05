#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
lock_path="$repo_root/eng/execution-runtimes/test-fixtures.lock.json"
work_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-runtime-publication-dry-run.XXXXXX")"
trap 'rm -rf "$work_root"' EXIT

if command -v sha256sum >/dev/null 2>&1; then
  lock_sha256="$(sha256sum "$lock_path" | cut -d ' ' -f 1)"
else
  lock_sha256="$(shasum -a 256 "$lock_path" | cut -d ' ' -f 1)"
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
  else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

hash_text() {
  if command -v sha256sum >/dev/null 2>&1; then printf '%s' "$1" | sha256sum | cut -d ' ' -f 1
  else printf '%s' "$1" | shasum -a 256 | cut -d ' ' -f 1; fi
}

mkdir "$work_root/native-receipts"
jq -n --slurpfile lock "$lock_path" '[
  $lock[0].entries[]
  | {
      runtimeId:.id,
      version,
      packagingRevision,
      rid,
      moduleAbi:.profile.moduleAbi,
      workerProtocol:.profile.workerProtocol,
      minimumHostTrustGeneration,
      releaseSigningKeyId:.trust.releaseSigningKeyId,
      nativeAttestationKeyId:.trust.attestationSigningKeyId,
      archiveFile:(.id + "-" + .rid + ".sunderruntime"),
      archiveSha256:("a" * 64),
      archiveSize:1,
      manifestSha256:("b" * 64),
      contentIdentity:("c" * 64),
      nativeAttestationSha256:("d" * 64),
      releaseSignatureVerified:true,
      nativeAttestationVerified:true
    }
] | sort_by(.runtimeId, .rid)' > "$work_root/artifacts.json"

drivers='[]'
native_receipts='[]'
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
  driver_sha="$(hash_text "reviewed-release-driver/$rid")"
  drivers="$(jq -cn --argjson drivers "$drivers" --arg rid "$rid" --arg sha "$driver_sha" \
    '$drivers + [{rid:$rid,sha256:$sha}]')"
  jq -n --arg lock_sha "$lock_sha256" --arg rid "$rid" --arg driver_sha "$driver_sha" \
    --slurpfile artifacts "$work_root/artifacts.json" '
      {
        schemaVersion:1,
        kind:"https://sunderapp.io/acceptance/runtime-native-output/v1",
        runtimeLockSha256:$lock_sha,
        rid:$rid,
        releaseDriverSha256:$driver_sha,
        artifacts:[$artifacts[0][] | select(.rid == $rid)]
      }
    ' > "$work_root/native-receipts/$rid.json"
  native_receipts="$(jq -cn \
    --argjson receipts "$native_receipts" --arg rid "$rid" --arg driver_sha "$driver_sha" \
    --arg receipt_file "$rid.json" --arg receipt_sha "$(hash_file "$work_root/native-receipts/$rid.json")" \
    '$receipts + [{rid:$rid,releaseDriverSha256:$driver_sha,receiptFile:$receipt_file,receiptSha256:$receipt_sha}]')"
done
jq -n --argjson drivers "$drivers" '{
  schemaVersion:1,
  kind:"https://sunderapp.io/acceptance/runtime-release-drivers/v1",
  drivers:$drivers
}' > "$work_root/release-drivers.v1.json"

jq -n --arg lock_sha "$lock_sha256" \
  --arg drivers_sha "$(hash_file "$work_root/release-drivers.v1.json")" \
  --argjson drivers "$drivers" --argjson native_receipts "$native_receipts" \
  --slurpfile lock "$lock_path" --slurpfile artifacts "$work_root/artifacts.json" '
    {
      schemaVersion:1,
      kind:"https://sunderapp.io/acceptance/runtime-release-set/v1",
      runtimeLockSha256:$lock_sha,
      releaseDriversSha256:$drivers_sha,
      rids:$lock[0].coverage.rids,
      releaseDrivers:$drivers,
      nativeOutputReceipts:$native_receipts,
      artifacts:$artifacts[0]
    }
  ' > "$work_root/release-set.json"

unset SUNDER_REGISTRY_ADMIN_TOKEN
jq -n --arg lock "$lock_sha256" '{
  artifactSourceCommit:("1" * 40),publicationEvidenceCommit:("3" * 40),retainedReceiptSha256:("4" * 64),
  evidenceDiff:{parentCommit:("1" * 40),publicationEvidenceCommit:("3" * 40),baselineChange:"verified-publication-only"},
  invocation:{workflowPath:".github/workflows/sunder-execution-runtime-publication.yml",workflowRef:"refs/heads/main",workflowSha:("2" * 40),runId:"1001",runAttempt:"1"},
  nativeBuild:{artifactSourceCommit:("1" * 40),runtimeLockSha256:$lock,destination:{tag:"runtime/v1.0.0"},
    invocation:{workflowPath:".github/workflows/sunder-execution-runtime-native-release.yml",workflowRef:"refs/tags/runtime/v1.0.0",workflowSha:("1" * 40)}}
}' > "$work_root/source-binding.json"
bash "$script_dir/publish-release-set.sh" \
  --lock "$lock_path" \
  --artifacts "$work_root/not-used" \
  --native-receipts "$work_root/native-receipts" \
  --release-drivers "$work_root/release-drivers.v1.json" \
  --release-set "$work_root/release-set.json" \
  --source-binding "$work_root/source-binding.json" \
  --registry https://registry.invalid \
  --repository Younics/sunder-core \
  --source-sha 1111111111111111111111111111111111111111 \
  --workflow-path .github/workflows/sunder-execution-runtime-publication.yml \
  --workflow-ref refs/heads/main \
  --workflow-sha 2222222222222222222222222222222222222222 \
  --run-id 1001 \
  --run-attempt 1 \
  --output "$work_root/publication-plan.json" \
  --dry-run

jq -e '
  .schemaVersion == 1
  and .kind == "https://sunderapp.io/acceptance/runtime-registry-publication/v1"
  and .decision == "planned"
  and .source.repository == "Younics/sunder-core"
  and .source.sha == ("1" * 40)
  and .source.artifactSourceCommit == ("1" * 40)
  and .source.publicationEvidenceCommit == ("3" * 40)
  and .invocation == {
    workflowPath:".github/workflows/sunder-execution-runtime-publication.yml",
    workflowRef:"refs/heads/main",workflowSha:("2" * 40),runId:"1001",runAttempt:"1"
  }
  and (.releaseSetSha256 | test("^[0-9a-f]{64}$"))
  and (.releaseDriversSha256 | test("^[0-9a-f]{64}$"))
  and (.releaseDrivers | length) == 6
  and (.nativeOutputReceipts | length) == 6
  and (.stages | length) == 2
  and all(.stages[];
    (.expectedRids | sort) == ["linux-arm64","linux-x64","osx-arm64","osx-x64","win-arm64","win-x64"]
    and (.expectedReleaseSigningKeyId | type == "string" and length > 0)
    and ([.expectedNativeAttestations[].rid] | sort) == (.expectedRids | sort)
    and ([.expectedNativeAttestations[] | {rid, nativeAttestationKeyId}] | sort_by(.rid))
      == ([.artifacts[] | {rid, nativeAttestationKeyId}] | sort_by(.rid))
    and ([.artifacts[].releaseSigningKeyId] | unique) == [.expectedReleaseSigningKeyId]
    and (.artifacts | length) == 6
    and ([.artifacts[].rid] | sort) == (.expectedRids | sort)
    and all(.artifacts[];
      (.idempotencyKey | startswith("sunder-runtime-"))
      and (.idempotencyKey | length) <= 256
      and (.archiveSha256 | test("^[0-9a-f]{64}$"))
      and (.manifestSha256 | test("^[0-9a-f]{64}$"))
      and (.contentIdentity | test("^[0-9a-f]{64}$"))
      and (.nativeAttestationSha256 | test("^[0-9a-f]{64}$"))
      and (.releaseDriverSha256 | test("^[0-9a-f]{64}$"))
      and (.releaseSigningKeyId | type == "string" and length > 0)
      and (.nativeAttestationKeyId | type == "string" and length > 0)))
' "$work_root/publication-plan.json" >/dev/null

echo "Credential-free execution-runtime publication dry run passed."
bash "$script_dir/publication-resume.fixture.sh"
