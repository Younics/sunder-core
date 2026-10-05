#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: publish-release-set.sh --lock <production-lock> --artifacts <six-rid-root> --native-receipts <six-rid-receipt-root> --release-drivers <six-rid-driver-json> --release-set <receipt> --source-binding <verified-retained-binding> --registry <https-origin> --repository <owner/repo> --source-sha <sha> --workflow-path <path> --workflow-ref <ref> --workflow-sha <sha> --run-id <id> --run-attempt <attempt> --output <durable-state-and-receipt> [--dry-run]

Validates both curated runtime families, reconciles an exact existing staged or
Supported release, independently retries all twelve RID uploads, and promotes
each complete six-RID family atomically. The output is updated atomically as a
credential-free recovery journal and becomes the final verification receipt.
The administrator bearer token is read only from SUNDER_REGISTRY_ADMIN_TOKEN.
--dry-run requires no token and performs no network requests.
USAGE
}

lock_path=""
artifacts_root=""
native_receipts_root=""
release_drivers_path=""
release_set_path=""
source_binding_path=""
registry_origin=""
repository=""
source_sha=""
workflow_path=""
workflow_ref=""
workflow_sha=""
run_id=""
run_attempt=""
output_path=""
dry_run=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --lock) lock_path="${2:-}"; shift 2 ;;
    --artifacts) artifacts_root="${2:-}"; shift 2 ;;
    --native-receipts) native_receipts_root="${2:-}"; shift 2 ;;
    --release-drivers) release_drivers_path="${2:-}"; shift 2 ;;
    --release-set) release_set_path="${2:-}"; shift 2 ;;
    --source-binding) source_binding_path="${2:-}"; shift 2 ;;
    --registry) registry_origin="${2:-}"; shift 2 ;;
    --repository) repository="${2:-}"; shift 2 ;;
    --source-sha) source_sha="${2:-}"; shift 2 ;;
    --workflow-path) workflow_path="${2:-}"; shift 2 ;;
    --workflow-ref) workflow_ref="${2:-}"; shift 2 ;;
    --workflow-sha) workflow_sha="${2:-}"; shift 2 ;;
    --run-id) run_id="${2:-}"; shift 2 ;;
    --run-attempt) run_attempt="${2:-}"; shift 2 ;;
    --output) output_path="${2:-}"; shift 2 ;;
    --dry-run) dry_run=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ -z "$lock_path" || -z "$artifacts_root" || -z "$native_receipts_root" \
      || -z "$release_drivers_path" || -z "$release_set_path" || -z "$source_binding_path" || -z "$registry_origin" \
      || -z "$repository" || -z "$source_sha" || -z "$workflow_path" || -z "$workflow_ref" \
      || -z "$workflow_sha" || -z "$run_id" || -z "$run_attempt" || -z "$output_path" ]]; then
  usage >&2
  exit 2
fi
if [[ ! "$registry_origin" =~ ^https://[^/@?#]+$ ]]; then
  echo "Registry must be one credential-free HTTPS origin without a path, query, or fragment." >&2
  exit 1
fi
if [[ "$repository" != "Younics/sunder-core" \
      || ! "$source_sha" =~ ^[0-9a-f]{40}$ \
      || "$workflow_path" != ".github/workflows/sunder-execution-runtime-publication.yml" \
      || "$workflow_ref" != "refs/heads/main" \
      || ! "$workflow_sha" =~ ^[0-9a-f]{40}$ \
      || ! "$run_id" =~ ^[1-9][0-9]*$ \
      || ! "$run_attempt" =~ ^[1-9][0-9]*$ ]]; then
  echo "Publication source and workflow invocation identity is invalid." >&2
  exit 1
fi
if [[ ! -f "$lock_path" || -L "$lock_path" \
      || ! -f "$release_drivers_path" || -L "$release_drivers_path" \
      || ! -f "$release_set_path" || -L "$release_set_path" \
      || ! -f "$source_binding_path" || -L "$source_binding_path" ]]; then
  echo "Production lock, release-driver identity, and release-set receipt must be non-link regular files." >&2
  exit 1
fi
if [[ ! -d "$native_receipts_root" || -L "$native_receipts_root" ]]; then
  echo "Native receipt root must be a non-link directory." >&2
  exit 1
fi
if [[ -e "$output_path" && (! -f "$output_path" || -L "$output_path") ]]; then
  echo "Publication state must be a non-link regular file when it already exists: $output_path" >&2
  exit 1
fi
if [[ "$dry_run" == true && -e "$output_path" ]]; then
  echo "Dry-run publication output must not already exist: $output_path" >&2
  exit 1
fi
for command_name in cmp curl jq dotnet; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { echo "Required publication command is unavailable: $command_name" >&2; exit 1; }
done

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

source_lock_path="$lock_path"
source_native_receipts_root="$native_receipts_root"
source_release_drivers_path="$release_drivers_path"
source_release_set_path="$release_set_path"
source_binding_sha256="$(hash_file "$source_binding_path")"
source_lock_sha256="$(hash_file "$source_lock_path")"
source_release_drivers_sha256="$(hash_file "$source_release_drivers_path")"
source_release_set_sha256="$(hash_file "$source_release_set_path")"
input_snapshot_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-runtime-publication-inputs.XXXXXX")"
work_root=""
cleanup() {
  rm -rf "$input_snapshot_root"
  if [[ -n "$work_root" ]]; then
    rm -rf "$work_root"
  fi
}
trap cleanup EXIT
cp -p "$source_binding_path" "$input_snapshot_root/source-binding.json"
[[ "$(hash_file "$input_snapshot_root/source-binding.json")" == "$source_binding_sha256" \
    && "$(hash_file "$source_binding_path")" == "$source_binding_sha256" ]] \
  || { echo "Retained source binding changed while being snapshotted." >&2; exit 1; }
source_binding_path="$input_snapshot_root/source-binding.json"
jq -e --arg source "$source_sha" --arg workflow "$workflow_path" --arg ref "$workflow_ref" \
  --arg workflow_sha "$workflow_sha" --arg run "$run_id" --arg attempt "$run_attempt" '
    keys == ["artifactSourceCommit","evidenceDiff","invocation","nativeBuild","publicationEvidenceCommit","retainedReceiptSha256"]
    and .artifactSourceCommit == $source
    and (.publicationEvidenceCommit | test("^[0-9a-f]{40}$"))
    and (.retainedReceiptSha256 | test("^[0-9a-f]{64}$"))
    and .evidenceDiff.parentCommit == $source
    and .evidenceDiff.publicationEvidenceCommit == .publicationEvidenceCommit
    and .evidenceDiff.baselineChange == "verified-publication-only"
    and .invocation == {workflowPath:$workflow,workflowRef:$ref,workflowSha:$workflow_sha,runId:$run,runAttempt:$attempt}
    and .nativeBuild.artifactSourceCommit == $source
    and .nativeBuild.invocation.workflowPath == ".github/workflows/sunder-execution-runtime-native-release.yml"
    and .nativeBuild.invocation.workflowSha == $source
    and .nativeBuild.invocation.workflowRef == ("refs/tags/" + .nativeBuild.destination.tag)
  ' "$source_binding_path" >/dev/null \
  || { echo "Publication does not bind the verified artifact source, evidence descendant, and native attempt." >&2; exit 1; }

lock_path="$input_snapshot_root/production.lock.json"
release_drivers_path="$input_snapshot_root/release-drivers.v1.json"
release_set_path="$input_snapshot_root/release-set.json"
native_receipts_root="$input_snapshot_root/native-receipts"
mkdir "$native_receipts_root"
cp -p "$source_lock_path" "$lock_path"
cp -p "$source_release_drivers_path" "$release_drivers_path"
cp -p "$source_release_set_path" "$release_set_path"
shopt -s nullglob dotglob
source_receipt_entries=("$source_native_receipts_root"/*)
if [[ ${#source_receipt_entries[@]} -ne 6 ]]; then
  echo "Native receipt root must contain exactly six canonical receipt files." >&2
  exit 1
fi
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
  source_receipt="$source_native_receipts_root/$rid.json"
  snapshot_receipt="$native_receipts_root/$rid.json"
  if [[ ! -f "$source_receipt" || -L "$source_receipt" ]]; then
    echo "Native receipt root is missing canonical non-link receipt '$rid.json'." >&2
    exit 1
  fi
  cp -p "$source_receipt" "$snapshot_receipt"
  if ! cmp -s "$source_receipt" "$snapshot_receipt"; then
    echo "Native receipt '$rid.json' changed while being snapshotted." >&2
    exit 1
  fi
done
if [[ "$(hash_file "$source_lock_path")" != "$source_lock_sha256" \
      || "$(hash_file "$lock_path")" != "$source_lock_sha256" \
      || "$(hash_file "$source_release_drivers_path")" != "$source_release_drivers_sha256" \
      || "$(hash_file "$release_drivers_path")" != "$source_release_drivers_sha256" \
      || "$(hash_file "$source_release_set_path")" != "$source_release_set_sha256" \
      || "$(hash_file "$release_set_path")" != "$source_release_set_sha256" ]]; then
  echo "Production lock, release-driver identity, or release-set receipt changed while being snapshotted." >&2
  exit 1
fi
lock_sha256="$source_lock_sha256"
release_drivers_sha256="$source_release_drivers_sha256"
release_set_sha256="$source_release_set_sha256"
jq -e --arg lock "$lock_sha256" '.nativeBuild.runtimeLockSha256 == $lock' "$source_binding_path" >/dev/null

jq -e --arg lock_sha "$lock_sha256" --arg drivers_sha "$release_drivers_sha256" \
  --slurpfile lock "$lock_path" --slurpfile drivers "$release_drivers_path" '
  .schemaVersion == 1
  and .kind == "https://sunderapp.io/acceptance/runtime-release-set/v1"
  and .runtimeLockSha256 == $lock_sha
  and .releaseDriversSha256 == $drivers_sha
  and .rids == $lock[0].coverage.rids
  and $drivers[0].schemaVersion == 1
  and $drivers[0].kind == "https://sunderapp.io/acceptance/runtime-release-drivers/v1"
  and ([ $drivers[0].drivers[].rid ]) == $lock[0].coverage.rids
  and all($drivers[0].drivers[]; (.sha256 | test("^[0-9a-f]{64}$")))
  and .releaseDrivers == $drivers[0].drivers
  and (.nativeOutputReceipts | length) == 6
  and ([.nativeOutputReceipts[].rid]) == $lock[0].coverage.rids
  and all(.nativeOutputReceipts[];
    .receiptFile == (.rid + ".json")
    and (.receiptSha256 | test("^[0-9a-f]{64}$"))
    and (.releaseDriverSha256 | test("^[0-9a-f]{64}$")))
  and ([.nativeOutputReceipts[] | {rid,sha256:.releaseDriverSha256}]) == $drivers[0].drivers
  and (.artifacts | length) == 12
  and ([.artifacts[] | .runtimeId + "/" + .version + "/" + (.packagingRevision | tostring) + "/" + .rid] | unique | length) == 12
  and all(.artifacts[];
    (.archiveFile | test("^[A-Za-z0-9][A-Za-z0-9._-]*[.]sunderruntime$"))
    and (.archiveSha256 | test("^[0-9a-f]{64}$"))
    and (.manifestSha256 | test("^[0-9a-f]{64}$"))
    and (.contentIdentity | test("^[0-9a-f]{64}$"))
    and (.nativeAttestationSha256 | test("^[0-9a-f]{64}$"))
    and (.archiveSize | type == "number" and floor == . and . > 0)
    and (.releaseSigningKeyId | type == "string" and length > 0)
    and (.nativeAttestationKeyId | type == "string" and length > 0)
    and .releaseSignatureVerified == true
    and .nativeAttestationVerified == true)
  and ([.artifacts[] | {
    id:.runtimeId, version, packagingRevision, rid, moduleAbi, workerProtocol,
    minimumHostTrustGeneration, releaseSigningKeyId, nativeAttestationKeyId
  }] | sort_by(.id, .rid))
    == ([$lock[0].entries[] | {
      id, version, packagingRevision, rid, moduleAbi:.profile.moduleAbi,
      workerProtocol:.profile.workerProtocol,
      minimumHostTrustGeneration,
      releaseSigningKeyId:.trust.releaseSigningKeyId,
      nativeAttestationKeyId:.trust.attestationSigningKeyId
    }] | sort_by(.id, .rid))
' "$release_set_path" >/dev/null

while IFS=$'\t' read -r rid receipt_file receipt_sha256 release_driver_sha256; do
  receipt_path="$native_receipts_root/$receipt_file"
  [[ "$receipt_file" == "$rid.json" \
      && -f "$receipt_path" && ! -L "$receipt_path" \
      && "$(hash_file "$receipt_path")" == "$receipt_sha256" ]] \
    || { echo "Release set does not bind the exact native receipt bytes for RID '$rid'." >&2; exit 1; }
  jq -e --arg rid "$rid" --arg lock_sha "$lock_sha256" --arg driver_sha "$release_driver_sha256" '
    .schemaVersion == 1
    and .kind == "https://sunderapp.io/acceptance/runtime-native-output/v1"
    and .runtimeLockSha256 == $lock_sha
    and .rid == $rid
    and .releaseDriverSha256 == $driver_sha
    and (.artifacts | length) == 2
  ' "$receipt_path" >/dev/null \
    || { echo "Native receipt identity differs from the release set for RID '$rid'." >&2; exit 1; }
done < <(jq -r '.nativeOutputReceipts[] | [.rid, .receiptFile, .receiptSha256, .releaseDriverSha256] | @tsv' "$release_set_path")

write_plan() {
  local decision="$1"
  local target="$2"
  jq -n \
    --arg decision "$decision" \
    --arg registry "$registry_origin" \
    --arg lock_sha "$lock_sha256" \
    --arg drivers_sha "$release_drivers_sha256" \
    --arg release_set_sha "$release_set_sha256" \
    --arg repository "$repository" \
    --arg source_sha "$source_sha" \
    --arg workflow_path "$workflow_path" \
    --arg workflow_ref "$workflow_ref" \
    --arg workflow_sha "$workflow_sha" \
    --arg run_id "$run_id" \
    --arg run_attempt "$run_attempt" \
    --slurpfile lock "$lock_path" \
    --slurpfile binding "$source_binding_path" \
    --slurpfile release_set "$release_set_path" '
      {
        schemaVersion: 1,
        kind: "https://sunderapp.io/acceptance/runtime-registry-publication/v1",
        decision: $decision,
        registryOrigin: $registry,
        source: {
          repository:$repository,sha:$source_sha,artifactSourceCommit:$source_sha,
          publicationEvidenceCommit:$binding[0].publicationEvidenceCommit,
          evidenceDiff:$binding[0].evidenceDiff,
          nativeBuild:$binding[0].nativeBuild,
          retainedReceiptSha256:$binding[0].retainedReceiptSha256
        },
        invocation: {
          workflowPath:$workflow_path,
          workflowRef:$workflow_ref,
          workflowSha:$workflow_sha,
          runId:$run_id,
          runAttempt:$run_attempt
        },
        runtimeLockSha256: $lock_sha,
        releaseDriversSha256: $drivers_sha,
        releaseSetSha256: $release_set_sha,
        releaseDrivers: $release_set[0].releaseDrivers,
        nativeOutputReceipts: $release_set[0].nativeOutputReceipts,
        stages: [
          $lock[0].coverage.runtimes[] as $runtime
          | ($lock[0].entries | map(select(.id == $runtime.id))) as $entries
          | {
              runtimeId: $runtime.id,
              version: $entries[0].version,
              packagingRevision: $entries[0].packagingRevision,
              moduleAbi: $entries[0].profile.moduleAbi,
              workerProtocol: $entries[0].profile.workerProtocol,
              minimumHostTrustGeneration: $entries[0].minimumHostTrustGeneration,
              expectedReleaseSigningKeyId: (
                [$release_set[0].artifacts[] | select(.runtimeId == $runtime.id) | .releaseSigningKeyId]
                | unique
                | if length == 1 then .[0] else error("one release signing key is required per family") end
              ),
              expectedNativeAttestations: [
                $release_set[0].artifacts[]
                | select(.runtimeId == $runtime.id)
                | {rid, nativeAttestationKeyId}
              ] | sort_by(.rid),
              expectedRids: $lock[0].coverage.rids,
              artifacts: [
                $release_set[0].artifacts[]
                | select(.runtimeId == $runtime.id)
                | . as $artifact
                | {
                    rid, archiveFile, archiveSha256, archiveSize, manifestSha256,
                    contentIdentity, nativeAttestationSha256, releaseSigningKeyId,
                    nativeAttestationKeyId,
                    releaseDriverSha256: (
                      $release_set[0].releaseDrivers[]
                      | select(.rid == $artifact.rid)
                      | .sha256
                    ),
                    idempotencyKey: ("sunder-runtime-" + $release_set_sha + "-" + .runtimeId + "-" + .rid + "-" + .archiveSha256)
                  }
              ] | sort_by(.rid)
            }
        ]
      }
    ' > "$target"
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
output_parent="$(dirname "$output_path")"
mkdir -p "$output_parent"
if [[ ! -d "$output_parent" || -L "$output_parent" ]]; then
  echo "Publication state parent must be a non-link directory." >&2
  exit 1
fi

if [[ "$dry_run" == true ]]; then
  temporary_output="$(mktemp "$output_parent/.$(basename "$output_path").write.XXXXXX")"
  write_plan planned "$temporary_output"
  chmod 600 "$temporary_output"
  mv "$temporary_output" "$output_path"
  echo "Validated credential-free Registry publication plan: $output_path"
  exit 0
fi

if [[ ! -d "$artifacts_root" || -L "$artifacts_root" ]]; then
  echo "Publication artifacts root must be a non-link directory." >&2
  exit 1
fi
verifier="${SUNDER_EXECUTION_RUNTIME_VERIFIER_DLL:-}"
[[ -f "$verifier" && ! -L "$verifier" ]] \
  || { echo "The verified retained Sunder.ExecutionRuntime.Build.dll is required; publication never rebuilds it." >&2; exit 1; }
if [[ -z "${SUNDER_REGISTRY_ADMIN_TOKEN:-}" ]]; then
  echo "SUNDER_REGISTRY_ADMIN_TOKEN is required for non-dry-run Registry publication." >&2
  exit 1
fi
if [[ ! -d "${SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS:-}" || -L "${SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS:-}" \
      || ! -d "${SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS:-}" || -L "${SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS:-}" ]]; then
  echo "Approved release and attestation public-key directories are required for Registry byte reverification." >&2
  exit 1
fi

work_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-runtime-publication.XXXXXX")"
plan_path="$work_root/plan.json"
write_plan publishing "$plan_path"

dotnet "$verifier" \
  validate-release-set \
  --lock "$lock_path" --kind production --directory "$artifacts_root" \
  --native-receipts "$native_receipts_root" \
  --release-drivers "$release_drivers_path" \
  --release-public-keys "$SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS" \
  --attestation-public-keys "$SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS" \
  --receipt "$work_root/local-release-set.json"
cmp "$release_set_path" "$work_root/local-release-set.json"

persist_json() {
  local source="$1"
  local temporary
  temporary="$(mktemp "$output_parent/.$(basename "$output_path").write.XXXXXX")"
  jq '.' "$source" > "$temporary"
  chmod 600 "$temporary"
  mv "$temporary" "$output_path"
}

initialize_state() {
  local target="$1"
  jq -n \
    --slurpfile plan "$plan_path" \
    --slurpfile release_set "$release_set_path" '
      $plan[0] + {
        decision: "publishing",
        artifacts: $release_set[0].artifacts,
        progress: [
          $plan[0].stages[]
          | {
              runtimeId,
              stageId: null,
              lifecycleState: null,
              publishedRids: [],
              missingRids: .expectedRids,
              readyToPromote: null,
              phase: "pending"
            }
        ],
        promotions: [],
        resolutions: []
      }
    ' > "$target"
}

validate_state() {
  jq -e \
    --slurpfile plan "$plan_path" \
    --slurpfile release_set "$release_set_path" '
      (. | keys | sort) == ([
        "artifacts", "decision", "invocation", "kind", "nativeOutputReceipts", "progress",
        "promotions", "registryOrigin", "releaseDrivers", "releaseDriversSha256",
        "releaseSetSha256", "resolutions", "runtimeLockSha256", "schemaVersion", "source", "stages"
      ] | sort)
      and .schemaVersion == 1
      and .kind == "https://sunderapp.io/acceptance/runtime-registry-publication/v1"
      and (.decision == "publishing" or .decision == "published")
      and .registryOrigin == $plan[0].registryOrigin
      and .source == $plan[0].source
      and .invocation == $plan[0].invocation
      and .runtimeLockSha256 == $plan[0].runtimeLockSha256
      and .releaseDriversSha256 == $plan[0].releaseDriversSha256
      and .releaseSetSha256 == $plan[0].releaseSetSha256
      and .releaseDrivers == $release_set[0].releaseDrivers
      and .nativeOutputReceipts == $release_set[0].nativeOutputReceipts
      and .stages == $plan[0].stages
      and .artifacts == $release_set[0].artifacts
      and (.progress | type == "array" and length == ($plan[0].stages | length))
      and ([.progress[].runtimeId] | sort) == ([$plan[0].stages[].runtimeId] | sort)
      and all(.progress[];
        (. | keys | sort) == ([
          "lifecycleState", "missingRids", "phase", "publishedRids", "readyToPromote",
          "runtimeId", "stageId"
        ] | sort)
        and (.stageId == null or (.stageId | type == "string" and test("^[0-9a-f]{32}$")))
        and (.lifecycleState == null or .lifecycleState == "staged" or .lifecycleState == "supported")
        and (.publishedRids | type == "array" and all(.[]; type == "string"))
        and (.missingRids | type == "array" and all(.[]; type == "string"))
        and (.readyToPromote == null or (.readyToPromote | type == "boolean"))
        and (.phase | type == "string" and length <= 80 and test("^[a-z0-9.-]+$")))
      and (.promotions | type == "array")
      and (.resolutions | type == "array")
    ' "$output_path" >/dev/null
}

if [[ -e "$output_path" ]]; then
  validate_state || { echo "Existing publication state does not match the exact Registry, lock, and release set." >&2; exit 1; }
  chmod 600 "$output_path"
else
  initialize_state "$work_root/initial-state.json"
  persist_json "$work_root/initial-state.json"
fi

authorization="Authorization: Bearer $SUNDER_REGISTRY_ADMIN_TOKEN"
curl_common=(--silent --show-error --proto '=https' --connect-timeout 15 --max-time 1800)
HTTP_STATUS="000"
HTTP_EXIT=0

http_request() {
  local response_path="$1"
  shift
  local status
  rm -f "$response_path"
  if status="$(curl "${curl_common[@]}" --output "$response_path" --write-out '%{http_code}' "$@")"; then
    HTTP_EXIT=0
  else
    HTTP_EXIT=$?
  fi
  HTTP_STATUS="${status:-000}"
  if [[ ! "$HTTP_STATUS" =~ ^[0-9]{3}$ ]]; then
    HTTP_STATUS="000"
  fi
}

persist_phase() {
  local runtime_id="$1"
  local phase="$2"
  jq --arg runtime_id "$runtime_id" --arg phase "$phase" '
    .decision = "publishing"
    | .progress |= map(if .runtimeId == $runtime_id then .phase = $phase else . end)
  ' "$output_path" > "$work_root/state-next.json"
  persist_json "$work_root/state-next.json"
}

persist_status() {
  local runtime_id="$1"
  local response_path="$2"
  local phase="$3"
  jq --arg runtime_id "$runtime_id" --arg phase "$phase" --slurpfile status "$response_path" '
    $status[0] as $status
    | .decision = "publishing"
    | .progress |= map(
        if .runtimeId == $runtime_id then {
          runtimeId,
          stageId: $status.stageId,
          lifecycleState: $status.release.lifecycle.state,
          publishedRids: $status.publishedRids,
          missingRids: $status.missingRids,
          readyToPromote: $status.readyToPromote,
          phase: $phase
        } else . end)
  ' "$output_path" > "$work_root/state-next.json"
  persist_json "$work_root/state-next.json"
}

record_promotion() {
  local runtime_id="$1"
  local response_path="$2"
  jq --arg runtime_id "$runtime_id" --slurpfile status "$response_path" '
    .promotions = ((.promotions | map(select(.release.runtimeId != $runtime_id))) + [$status[0]] | sort_by(.release.runtimeId))
  ' "$output_path" > "$work_root/state-next.json"
  persist_json "$work_root/state-next.json"
}

create_family_requests() {
  local runtime_id="$1"
  local safe_runtime_id="${runtime_id//./-}"
  jq -c --arg runtime_id "$runtime_id" '
    .stages[] | select(.runtimeId == $runtime_id)
    | {
        release: {
          runtimeId, version, packagingRevision, moduleAbi, workerProtocol,
          minimumHostTrustGeneration, expectedReleaseSigningKeyId,
          expectedNativeAttestations, expectedRids
        },
        expectedArtifacts: [
          .artifacts[]
          | {
              rid,
              idempotencyKey,
              artifactSha256: .archiveSha256,
              artifactSize: .archiveSize,
              manifestSha256,
              contentIdentity,
              signingKeyId: .releaseSigningKeyId,
              nativeAttestationSha256,
              nativeAttestationKeyId
            }
        ]
      }
  ' "$plan_path" > "$work_root/lookup-request-$safe_runtime_id.json"
  jq -c '.release' "$work_root/lookup-request-$safe_runtime_id.json" \
    > "$work_root/stage-request-$safe_runtime_id.json"
}

validate_publication_response() {
  local response_path="$1"
  local request_path="$2"
  jq -e --slurpfile expected "$request_path" '
    . as $actual
    | $expected[0] as $expected
    | $expected.release as $release
    | (.stageId | type == "string" and test("^[0-9a-f]{32}$"))
    and .release.runtimeId == $release.runtimeId
    and .release.version == $release.version
    and .release.packagingRevision == $release.packagingRevision
    and .release.moduleAbi == $release.moduleAbi
    and .release.workerProtocol == $release.workerProtocol
    and .release.minimumHostTrustGeneration == $release.minimumHostTrustGeneration
    and (.release.lifecycle.state == "staged" or .release.lifecycle.state == "supported")
    and (.expectedRids | sort) == ($release.expectedRids | sort)
    and (.publishedRids | sort | unique) == (.publishedRids | sort)
    and (.missingRids | sort | unique) == (.missingRids | sort)
    and ((.publishedRids + .missingRids) | sort) == ($release.expectedRids | sort)
    and .readyToPromote == ((.missingRids | length) == 0)
    and (.release.supportedRids | sort) == (.publishedRids | sort)
    and (
      [.artifacts[] | {
        rid, artifactSha256, artifactSize, manifestSha256, contentIdentity,
        signingKeyId, nativeAttestationSha256, nativeAttestationKeyId
      }] | sort_by(.rid)
    ) == (
      $expected.expectedArtifacts
      | map(select(.rid as $rid | $actual.publishedRids | index($rid) != null))
      | map({
          rid, artifactSha256, artifactSize, manifestSha256, contentIdentity,
          signingKeyId, nativeAttestationSha256, nativeAttestationKeyId
        })
      | sort_by(.rid)
    )
    and (.release.lifecycle.state != "supported" or (
      (.publishedRids | sort) == ($release.expectedRids | sort)
      and (.missingRids | length) == 0
      and .readyToPromote == true
    ))
  ' "$response_path" >/dev/null
}

# Returns 0 for an exact resumable publication, 3 when the tuple is absent, and
# 1 for any transport, authorization, validation, or mixed-state failure.
lookup_family() {
  local runtime_id="$1"
  local response_path="$2"
  local safe_runtime_id="${runtime_id//./-}"
  local request_path="$work_root/lookup-request-$safe_runtime_id.json"
  local attempt
  for attempt in 1 2 3 4; do
    http_request "$response_path" \
      --request POST \
      --header "$authorization" \
      --header "Content-Type: application/json" \
      --data-binary "@$request_path" \
      "$registry_origin/api/v1/admin/execution-runtimes/stages/lookup"
    if [[ "$HTTP_EXIT" -eq 0 && "$HTTP_STATUS" == "200" ]]; then
      if validate_publication_response "$response_path" "$request_path"; then
        return 0
      fi
      echo "Registry returned publication state that does not match the exact expected release set for $runtime_id." >&2
      return 1
    fi
    if [[ "$HTTP_EXIT" -eq 0 && "$HTTP_STATUS" == "404" ]]; then
      return 3
    fi
    if [[ "$HTTP_EXIT" -eq 0 && "$HTTP_STATUS" =~ ^4[0-9][0-9]$ ]]; then
      echo "Registry rejected exact publication lookup for $runtime_id with HTTP $HTTP_STATUS." >&2
      return 1
    fi
    if [[ "$attempt" -lt 4 ]]; then
      sleep 1
    fi
  done
  echo "Could not reconcile exact publication state for $runtime_id after bounded retries." >&2
  return 1
}

ensure_stage() {
  local runtime_id="$1"
  local safe_runtime_id="${runtime_id//./-}"
  local response_path="$work_root/status-$safe_runtime_id.json"
  local lookup_result
  if lookup_family "$runtime_id" "$response_path"; then
    persist_status "$runtime_id" "$response_path" "reconciled"
    return 0
  else
    lookup_result=$?
  fi
  if [[ "$lookup_result" -ne 3 ]]; then
    return 1
  fi

  local attempt
  for attempt in 1 2 3 4; do
    persist_phase "$runtime_id" "stage-create-pending"
    http_request "$work_root/stage-create-$safe_runtime_id.json" \
      --request POST \
      --header "$authorization" \
      --header "Content-Type: application/json" \
      --data-binary "@$work_root/stage-request-$safe_runtime_id.json" \
      "$registry_origin/api/v1/admin/execution-runtimes/stages"
    local mutation_status="$HTTP_STATUS"
    local mutation_exit="$HTTP_EXIT"

    if lookup_family "$runtime_id" "$response_path"; then
      persist_status "$runtime_id" "$response_path" "staged"
      return 0
    else
      lookup_result=$?
    fi
    if [[ "$lookup_result" -ne 3 ]]; then
      return 1
    fi
    if [[ "$mutation_exit" -eq 0 && "$mutation_status" =~ ^4[0-9][0-9]$ ]]; then
      echo "Registry rejected stage creation for $runtime_id with HTTP $mutation_status and no exact resumable stage exists." >&2
      return 1
    fi
    if [[ "$attempt" -lt 4 ]]; then
      sleep 1
    fi
  done
  echo "Could not create or recover the exact staged publication for $runtime_id." >&2
  return 1
}

publish_family_artifacts() {
  local runtime_id="$1"
  local safe_runtime_id="${runtime_id//./-}"
  local response_path="$work_root/status-$safe_runtime_id.json"
  local lookup_result
  if ! lookup_family "$runtime_id" "$response_path"; then
    lookup_result=$?
    echo "Exact staged publication disappeared before artifact fan-in for $runtime_id (lookup result $lookup_result)." >&2
    return 1
  fi
  persist_status "$runtime_id" "$response_path" "upload-reconciled"
  if [[ "$(jq -r '.release.lifecycle.state' "$response_path")" == "supported" ]]; then
    return 0
  fi

  while IFS=$'\t' read -r rid archive_file archive_sha256 archive_size idempotency_key; do
    if ! lookup_family "$runtime_id" "$response_path"; then
      echo "Exact staged publication could not be reconciled before $runtime_id/$rid upload." >&2
      return 1
    fi
    if [[ "$(jq -r '.release.lifecycle.state' "$response_path")" == "supported" ]]; then
      persist_status "$runtime_id" "$response_path" "supported"
      return 0
    fi
    if jq -e --arg rid "$rid" '(.publishedRids | index($rid)) != null' "$response_path" >/dev/null; then
      persist_status "$runtime_id" "$response_path" "upload-$rid-complete"
      continue
    fi

    local artifact_path="$artifacts_root/$rid/$archive_file"
    if [[ ! -f "$artifact_path" || -L "$artifact_path" \
          || "$(hash_file "$artifact_path")" != "$archive_sha256" \
          || "$(wc -c < "$artifact_path" | tr -d '[:space:]')" != "$archive_size" ]]; then
      echo "Publication artifact changed after fan-in validation: $artifact_path" >&2
      return 1
    fi

    local stage_id
    stage_id="$(jq -r '.stageId' "$response_path")"
    persist_phase "$runtime_id" "upload-$rid-pending"
    local uploaded=false
    local attempt
    for attempt in 1 2 3 4; do
      http_request "$work_root/upload-$safe_runtime_id-$rid.json" \
        --request PUT \
        --header "$authorization" \
        --form "runtime=@$artifact_path;type=application/octet-stream" \
        --form-string "idempotencyKey=$idempotency_key" \
        --form-string "expectedArtifactSha256=$archive_sha256" \
        --form-string "expectedArtifactSize=$archive_size" \
        "$registry_origin/api/v1/admin/execution-runtimes/stages/$stage_id/artifacts/$rid"
      local mutation_status="$HTTP_STATUS"
      local mutation_exit="$HTTP_EXIT"

      if ! lookup_family "$runtime_id" "$response_path"; then
        echo "Artifact upload for $runtime_id/$rid left unknown or mixed Registry state." >&2
        return 1
      fi
      persist_status "$runtime_id" "$response_path" "upload-$rid-reconciled"
      if [[ "$(jq -r '.release.lifecycle.state' "$response_path")" == "supported" ]] \
          || jq -e --arg rid "$rid" '(.publishedRids | index($rid)) != null' "$response_path" >/dev/null; then
        uploaded=true
        break
      fi
      if [[ "$mutation_exit" -eq 0 && "$mutation_status" =~ ^4[0-9][0-9]$ && "$mutation_status" != "408" && "$mutation_status" != "429" ]]; then
        echo "Registry rejected artifact upload for $runtime_id/$rid with HTTP $mutation_status." >&2
        return 1
      fi
      if [[ "$attempt" -lt 4 ]]; then
        sleep 1
      fi
    done
    if [[ "$uploaded" != true ]]; then
      echo "Artifact upload for $runtime_id/$rid did not become durable after bounded retries." >&2
      return 1
    fi
  done < <(jq -r --arg runtime_id "$runtime_id" '
    .stages[] | select(.runtimeId == $runtime_id) | .artifacts[]
    | [.rid, .archiveFile, .archiveSha256, (.archiveSize | tostring), .idempotencyKey] | @tsv
  ' "$plan_path")

  if ! lookup_family "$runtime_id" "$response_path"; then
    echo "Could not reconcile complete six-RID publication for $runtime_id." >&2
    return 1
  fi
  persist_status "$runtime_id" "$response_path" "uploads-complete"
  if [[ "$(jq -r '.release.lifecycle.state' "$response_path")" == "staged" \
        && "$(jq -r '.readyToPromote' "$response_path")" != "true" ]]; then
    echo "Registry did not retain the exact complete six-RID staged publication for $runtime_id." >&2
    return 1
  fi
}

promote_family() {
  local runtime_id="$1"
  local safe_runtime_id="${runtime_id//./-}"
  local response_path="$work_root/status-$safe_runtime_id.json"
  if ! lookup_family "$runtime_id" "$response_path"; then
    echo "Could not reconcile exact publication before promotion for $runtime_id." >&2
    return 1
  fi
  if [[ "$(jq -r '.release.lifecycle.state' "$response_path")" == "supported" ]]; then
    persist_status "$runtime_id" "$response_path" "supported"
    record_promotion "$runtime_id" "$response_path"
    return 0
  fi
  if [[ "$(jq -r '.readyToPromote' "$response_path")" != "true" ]]; then
    echo "Refusing to promote incomplete execution runtime family $runtime_id." >&2
    return 1
  fi

  local stage_id
  stage_id="$(jq -r '.stageId' "$response_path")"
  persist_phase "$runtime_id" "promotion-pending"
  local attempt
  for attempt in 1 2 3 4; do
    http_request "$work_root/promotion-$safe_runtime_id.json" \
      --request POST \
      --header "$authorization" \
      "$registry_origin/api/v1/admin/execution-runtimes/stages/$stage_id/promotion"
    local mutation_status="$HTTP_STATUS"
    local mutation_exit="$HTTP_EXIT"

    if ! lookup_family "$runtime_id" "$response_path"; then
      echo "Promotion for $runtime_id left unknown or mixed Registry state." >&2
      return 1
    fi
    if [[ "$(jq -r '.release.lifecycle.state' "$response_path")" == "supported" ]]; then
      persist_status "$runtime_id" "$response_path" "supported"
      record_promotion "$runtime_id" "$response_path"
      return 0
    fi
    persist_status "$runtime_id" "$response_path" "promotion-reconciled"
    if [[ "$mutation_exit" -eq 0 && "$mutation_status" =~ ^4[0-9][0-9]$ && "$mutation_status" != "408" && "$mutation_status" != "429" ]]; then
      echo "Registry rejected promotion for $runtime_id with HTTP $mutation_status." >&2
      return 1
    fi
    if [[ "$attempt" -lt 4 ]]; then
      sleep 1
    fi
  done
  echo "Promotion for $runtime_id did not become Supported after bounded retries." >&2
  return 1
}

while IFS= read -r runtime_id; do
  create_family_requests "$runtime_id"
done < <(jq -r '.stages[].runtimeId' "$plan_path")

while IFS= read -r runtime_id; do
  ensure_stage "$runtime_id"
done < <(jq -r '.stages[].runtimeId' "$plan_path")

while IFS= read -r runtime_id; do
  publish_family_artifacts "$runtime_id"
done < <(jq -r '.stages[].runtimeId' "$plan_path")

# Promotion is intentionally one transaction per six-RID family. A process
# interruption between the two promotions is recovered by exact lookup.
while IFS= read -r runtime_id; do
  promote_family "$runtime_id"
done < <(jq -r '.stages[].runtimeId' "$plan_path")

download_root="$work_root/downloaded"
mkdir -p "$download_root" "$work_root/resolutions"
curl_read=("${curl_common[@]}" --fail-with-body --retry 3 --retry-all-errors --retry-delay 1)
while IFS=$'\t' read -r runtime_id version revision rid archive_file archive_sha256 archive_size; do
  mkdir -p "$download_root/$rid"
  downloaded_path="$download_root/$rid/$archive_file"
  curl "${curl_read[@]}" --location --proto-redir '=https' \
    --output "$downloaded_path" \
    "$registry_origin/api/v1/execution-runtimes/$runtime_id/releases/$version/revisions/$revision/artifacts/$rid/download"
  if [[ "$(hash_file "$downloaded_path")" != "$archive_sha256" \
        || "$(wc -c < "$downloaded_path" | tr -d '[:space:]')" != "$archive_size" ]]; then
    echo "Registry download does not match the promoted immutable artifact for $runtime_id/$rid." >&2
    exit 1
  fi
done < <(jq -r '
  .artifacts[]
  | [.runtimeId, .version, (.packagingRevision | tostring), .rid, .archiveFile, .archiveSha256, (.archiveSize | tostring)]
  | @tsv
' "$release_set_path")

dotnet "$verifier" \
  validate-release-set \
  --lock "$lock_path" --kind production --directory "$download_root" \
  --native-receipts "$native_receipts_root" \
  --release-drivers "$release_drivers_path" \
  --release-public-keys "$SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS" \
  --attestation-public-keys "$SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS" \
  --receipt "$work_root/downloaded-release-set.json"
cmp "$release_set_path" "$work_root/downloaded-release-set.json"

while IFS=$'\t' read -r runtime_id version revision rid module_abi trust_generation archive_sha256 archive_size manifest_sha256 release_key_id attestation_key_id attestation_sha256; do
  safe_runtime_id="${runtime_id//./-}"
  request_path="$work_root/resolve-request-$safe_runtime_id-$rid.json"
  response_path="$work_root/resolutions/$safe_runtime_id-$rid.json"
  jq -n \
    --arg runtime_id "$runtime_id" --arg version "$version" --arg module_abi "$module_abi" \
    --arg rid "$rid" --argjson trust_generation "$trust_generation" '
      {
        requirement: {runtimeId:$runtime_id,versionRange:$version,moduleAbi:$module_abi},
        rid:$rid,
        runtimeTrust:{profileId:"runtime-publication-verifier",generation:$trust_generation},
        currentRuntime:null,
        expectedResolutionFingerprint:null
      }
    ' > "$request_path"
  curl "${curl_read[@]}" \
    --request POST \
    --header "Content-Type: application/json" \
    --data-binary "@$request_path" \
    --output "$response_path" \
    "$registry_origin/api/v1/execution-runtimes/resolve"
  jq -e \
    --arg runtime_id "$runtime_id" --arg version "$version" --argjson revision "$revision" \
    --arg rid "$rid" --arg module_abi "$module_abi" --arg archive_sha256 "$archive_sha256" \
    --argjson archive_size "$archive_size" --arg manifest_sha256 "$manifest_sha256" \
    --arg release_key_id "$release_key_id" --arg attestation_key_id "$attestation_key_id" \
    --arg attestation_sha256 "$attestation_sha256" --argjson trust_generation "$trust_generation" '
      .success == true
      and .runtimeArtifact.identity.runtimeId == $runtime_id
      and .runtimeArtifact.identity.version == $version
      and .runtimeArtifact.identity.packagingRevision == $revision
      and .runtimeArtifact.identity.rid == $rid
      and .runtimeArtifact.identity.moduleAbi == $module_abi
      and .runtimeArtifact.artifactSha256 == $archive_sha256
      and .runtimeArtifact.artifactSize == $archive_size
      and .runtimeArtifact.manifestSha256 == $manifest_sha256
      and .runtimeArtifact.signingKeyId == $release_key_id
      and .runtimeArtifact.nativeAttestationKeyId == $attestation_key_id
      and .runtimeArtifact.nativeAttestationSha256 == $attestation_sha256
      and .runtimeArtifact.minimumHostTrustGeneration == $trust_generation
      and .runtimeArtifact.lifecycle.state == "supported"
      and (.conflicts | length) == 0
    ' "$response_path" >/dev/null
done < <(jq -r '
  .artifacts[]
  | [.runtimeId, .version, (.packagingRevision | tostring), .rid, .moduleAbi,
     (.minimumHostTrustGeneration | tostring), .archiveSha256, (.archiveSize | tostring),
     .manifestSha256, .releaseSigningKeyId, .nativeAttestationKeyId, .nativeAttestationSha256] | @tsv
' "$release_set_path")

jq -s 'sort_by(.runtimeArtifact.identity.runtimeId, .runtimeArtifact.identity.rid)' \
  "$work_root"/resolutions/*.json > "$work_root/resolutions.json"
jq --slurpfile resolutions "$work_root/resolutions.json" '
  .decision = "published"
  | .resolutions = $resolutions[0]
' "$output_path" > "$work_root/final-state.json"
persist_json "$work_root/final-state.json"
validate_state
jq -e '
  .decision == "published"
  and all(.progress[]; .lifecycleState == "supported" and .readyToPromote == true)
  and (.promotions | length) == 2
  and (.resolutions | length) == 12
' "$output_path" >/dev/null

echo "Published and independently verified both resumable six-RID execution-runtime families: $output_path"
