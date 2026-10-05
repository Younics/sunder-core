#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
publisher="$script_dir/publish-release-set.sh"
lock_path="$repo_root/eng/execution-runtimes/test-fixtures.lock.json"
for required_command in bash jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the publication-resume fixture.\n' "$required_command" >&2; exit 127; }
done

fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-runtime-publication-resume.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
mock_bin="$fixture_root/mock-bin"
artifact_root="$fixture_root/artifacts"
native_receipts_root="$fixture_root/native-receipts"
release_keys="$fixture_root/release-public-keys"
attestation_keys="$fixture_root/attestation-public-keys"
mkdir -p "$mock_bin" "$artifact_root" "$native_receipts_root" "$release_keys" "$attestation_keys"

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

hash_text() {
  if command -v sha256sum >/dev/null 2>&1; then
    printf '%s' "$1" | sha256sum | cut -d ' ' -f 1
  else
    printf '%s' "$1" | shasum -a 256 | cut -d ' ' -f 1
  fi
}

lock_sha256="$(hash_file "$lock_path")"
artifacts_json='[]'
while IFS= read -r entry; do
  runtime_id="$(jq -r '.id' <<<"$entry")"
  version="$(jq -r '.version' <<<"$entry")"
  revision="$(jq -r '.packagingRevision' <<<"$entry")"
  rid="$(jq -r '.rid' <<<"$entry")"
  archive_file="$runtime_id-$rid.sunderruntime"
  mkdir -p "$artifact_root/$rid"
  printf '%s\n' "$runtime_id/$version/$revision/$rid" > "$artifact_root/$rid/$archive_file"
  archive_sha256="$(hash_file "$artifact_root/$rid/$archive_file")"
  archive_size="$(wc -c < "$artifact_root/$rid/$archive_file" | tr -d '[:space:]')"
  manifest_sha256="$(hash_text "manifest/$runtime_id/$rid")"
  content_identity="$(hash_text "content/$runtime_id/$rid")"
  attestation_sha256="$(hash_text "attestation/$runtime_id/$rid")"
  artifact="$(jq -n \
    --argjson entry "$entry" \
    --arg archive_file "$archive_file" \
    --arg archive_sha256 "$archive_sha256" \
    --argjson archive_size "$archive_size" \
    --arg manifest_sha256 "$manifest_sha256" \
    --arg content_identity "$content_identity" \
    --arg attestation_sha256 "$attestation_sha256" '
      {
        runtimeId: $entry.id,
        version: $entry.version,
        packagingRevision: $entry.packagingRevision,
        rid: $entry.rid,
        moduleAbi: $entry.profile.moduleAbi,
        workerProtocol: $entry.profile.workerProtocol,
        minimumHostTrustGeneration: $entry.minimumHostTrustGeneration,
        releaseSigningKeyId: $entry.trust.releaseSigningKeyId,
        nativeAttestationKeyId: $entry.trust.attestationSigningKeyId,
        archiveFile: $archive_file,
        archiveSha256: $archive_sha256,
        archiveSize: $archive_size,
        manifestSha256: $manifest_sha256,
        contentIdentity: $content_identity,
        nativeAttestationSha256: $attestation_sha256,
        releaseSignatureVerified: true,
        nativeAttestationVerified: true
      }
    ')"
  artifacts_json="$(jq -c --argjson artifact "$artifact" '. + [$artifact]' <<<"$artifacts_json")"
done < <(jq -c '.entries[]' "$lock_path")

drivers_json='[]'
native_receipts_json='[]'
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
  driver_sha256="$(hash_text "reviewed-release-driver/$rid")"
  drivers_json="$(jq -cn --argjson drivers "$drivers_json" --arg rid "$rid" --arg sha "$driver_sha256" \
    '$drivers + [{rid:$rid,sha256:$sha}]')"
  jq -n \
    --arg lock_sha "$lock_sha256" \
    --arg rid "$rid" \
    --arg driver_sha "$driver_sha256" \
    --argjson artifacts "$artifacts_json" '
      {
        schemaVersion:1,
        kind:"https://sunderapp.io/acceptance/runtime-native-output/v1",
        runtimeLockSha256:$lock_sha,
        rid:$rid,
        releaseDriverSha256:$driver_sha,
        artifacts:[$artifacts[] | select(.rid == $rid)]
      }
    ' > "$native_receipts_root/$rid.json"
  native_receipts_json="$(jq -cn \
    --argjson receipts "$native_receipts_json" --arg rid "$rid" --arg driver_sha "$driver_sha256" \
    --arg receipt_file "$rid.json" --arg receipt_sha "$(hash_file "$native_receipts_root/$rid.json")" \
    '$receipts + [{rid:$rid,releaseDriverSha256:$driver_sha,receiptFile:$receipt_file,receiptSha256:$receipt_sha}]')"
done
release_drivers_path="$fixture_root/release-drivers.v1.json"
jq -n --argjson drivers "$drivers_json" '{
  schemaVersion:1,
  kind:"https://sunderapp.io/acceptance/runtime-release-drivers/v1",
  drivers:$drivers
}' > "$release_drivers_path"

jq -n \
  --arg lock_sha "$lock_sha256" \
  --arg drivers_sha "$(hash_file "$release_drivers_path")" \
  --argjson drivers "$drivers_json" \
  --argjson native_receipts "$native_receipts_json" \
  --argjson artifacts "$artifacts_json" \
  --slurpfile lock "$lock_path" '
    {
      schemaVersion: 1,
      kind: "https://sunderapp.io/acceptance/runtime-release-set/v1",
      runtimeLockSha256: $lock_sha,
      releaseDriversSha256: $drivers_sha,
      rids: $lock[0].coverage.rids,
      releaseDrivers: $drivers,
      nativeOutputReceipts: $native_receipts,
      artifacts: ($artifacts | sort_by(.runtimeId, .rid))
    }
  ' > "$fixture_root/release-set.json"
jq -n --arg lock "$(hash_file "$lock_path")" '{
  artifactSourceCommit:("1" * 40),publicationEvidenceCommit:("3" * 40),retainedReceiptSha256:("4" * 64),
  evidenceDiff:{parentCommit:("1" * 40),publicationEvidenceCommit:("3" * 40),baselineChange:"verified-publication-only"},
  invocation:{workflowPath:".github/workflows/sunder-execution-runtime-publication.yml",workflowRef:"refs/heads/main",workflowSha:("2" * 40),runId:"1001",runAttempt:"1"},
  nativeBuild:{artifactSourceCommit:("1" * 40),runtimeLockSha256:$lock,destination:{tag:"runtime/v1.0.0"},
    invocation:{workflowPath:".github/workflows/sunder-execution-runtime-native-release.yml",workflowRef:"refs/tags/runtime/v1.0.0",workflowSha:("1" * 40)}}
}' > "$fixture_root/source-binding.json"

cat > "$mock_bin/dotnet" <<'MOCK_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
receipt=""
while [[ $# -gt 0 ]]; do
  if [[ "$1" == "--receipt" ]]; then
    receipt="${2:-}"
    shift 2
  else
    shift
  fi
done
[[ -n "$receipt" ]]
cp "$MOCK_RELEASE_SET" "$receipt"
MOCK_DOTNET

cat > "$mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail

method="GET"
output_path=""
write_out=""
data_path=""
runtime_path=""
authorization=""
url=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --request) method="${2:-}"; shift 2 ;;
    --output) output_path="${2:-}"; shift 2 ;;
    --write-out) write_out="${2:-}"; shift 2 ;;
    --data-binary) data_path="${2#@}"; shift 2 ;;
    --form)
      value="${2:-}"
      if [[ "$value" == runtime=@* ]]; then
        runtime_path="${value#runtime=@}"
        runtime_path="${runtime_path%%;*}"
      fi
      shift 2
      ;;
    --form-string) shift 2 ;;
    --header)
      if [[ "${2:-}" == Authorization:* ]]; then
        authorization="${2:-}"
      fi
      shift 2
      ;;
    --connect-timeout|--max-time|--retry|--retry-delay|--proto|--proto-redir) shift 2 ;;
    --silent|--show-error|--fail-with-body|--retry-all-errors|--location) shift ;;
    https://*) url="$1"; shift ;;
    *) shift ;;
  esac
done

printf '%s\t%s\n' "$method" "$url" >> "$MOCK_CURL_LOG"

respond() {
  local status="$1"
  local body_path="${2:-}"
  if [[ -n "$output_path" ]]; then
    if [[ -n "$body_path" ]]; then
      cp "$body_path" "$output_path"
    else
      printf '{}\n' > "$output_path"
    fi
  elif [[ -n "$body_path" ]]; then
    command cat "$body_path"
  else
    printf '{}\n'
  fi
  if [[ -n "$write_out" ]]; then
    printf '%s' "$status"
  fi
}

unknown_outcome() {
  rm -f "$output_path"
  if [[ -n "$write_out" ]]; then
    printf '000'
  fi
  exit 56
}

state_update() {
  local temporary
  temporary="$(mktemp "$MOCK_FLAGS/state.XXXXXX")"
  jq "$@" "$MOCK_SERVER_STATE" > "$temporary"
  mv "$temporary" "$MOCK_SERVER_STATE"
}

stage_id_for() {
  case "$1" in
    sunder.runtime.dotnet) printf '11111111111111111111111111111111' ;;
    sunder.runtime.node) printf '22222222222222222222222222222222' ;;
    *) exit 90 ;;
  esac
}

runtime_id_for_stage() {
  local stage_id="$1"
  jq -r --arg stage_id "$stage_id" '.families | to_entries[] | select(.value.stageId == $stage_id) | .key' "$MOCK_SERVER_STATE"
}

if [[ "$url" == *"/api/v1/admin/"* && "$authorization" != "Authorization: Bearer $MOCK_EXPECTED_TOKEN" ]]; then
  respond 401
  exit 0
fi

if [[ "$method" == "POST" && "$url" == */api/v1/admin/execution-runtimes/stages/lookup ]]; then
  runtime_id="$(jq -r '.release.runtimeId' "$data_path")"
  if ! jq -e --arg runtime_id "$runtime_id" '.families[$runtime_id] != null' "$MOCK_SERVER_STATE" >/dev/null; then
    respond 404
    exit 0
  fi
  if ! jq -e --arg runtime_id "$runtime_id" --slurpfile request "$data_path" \
      '.families[$runtime_id].release == $request[0].release' "$MOCK_SERVER_STATE" >/dev/null; then
    respond 409
    exit 0
  fi
  lifecycle="$(jq -r --arg runtime_id "$runtime_id" '.families[$runtime_id].lifecycle' "$MOCK_SERVER_STATE")"
  if [[ "$lifecycle" != "staged" && "$lifecycle" != "supported" ]]; then
    respond 409
    exit 0
  fi
  response="$MOCK_FLAGS/lookup-response.json"
  jq -n \
    --arg runtime_id "$runtime_id" \
    --arg lifecycle "$lifecycle" \
    --slurpfile state "$MOCK_SERVER_STATE" \
    --slurpfile request "$data_path" '
      $state[0].families[$runtime_id] as $family
      | $request[0] as $request
      | ($family.publishedRids | sort) as $published
      | ($request.release.expectedRids - $published | sort) as $missing
      | ($request.expectedArtifacts
          | map(select(.rid as $rid | $published | index($rid) != null))
          | map({
              rid,
              artifactSha256,
              artifactSize,
              manifestSha256,
              contentIdentity,
              signingKeyId,
              nativeAttestationSha256,
              nativeAttestationKeyId
            })
        ) as $actualArtifacts
      | (if $state[0].mixedRuntimeId == $runtime_id and ($actualArtifacts | length) > 0 then
          ($actualArtifacts | .[0].artifactSha256 = ("f" * 64))
        else $actualArtifacts end) as $artifacts
      | {
          stageId: $family.stageId,
          release: (($family.release | del(.expectedRids)) + {
            lifecycle: {
              state: $lifecycle,
              stagedAtUtc: "2026-09-02T00:00:00+00:00",
              supportedFromUtc: (if $lifecycle == "supported" then "2026-09-02T00:01:00+00:00" else null end),
              deprecatedAtUtc: null,
              installBlockedAtUtc: null,
              activationBlockedAtUtc: null,
              revokedAtUtc: null,
              message: null
            },
            supportedRids: $published,
            publishedAtUtc: "2026-09-02T00:00:00+00:00"
          }),
          expectedRids: $request.release.expectedRids,
          publishedRids: $published,
          missingRids: $missing,
          readyToPromote: (($missing | length) == 0),
          artifacts: $artifacts
        }
    ' > "$response"
  respond 200 "$response"
  exit 0
fi

if [[ "$method" == "POST" && "$url" == */api/v1/admin/execution-runtimes/stages ]]; then
  runtime_id="$(jq -r '.runtimeId' "$data_path")"
  if jq -e --arg runtime_id "$runtime_id" '.families[$runtime_id] != null' "$MOCK_SERVER_STATE" >/dev/null; then
    respond 409
    exit 0
  fi
  stage_id="$(stage_id_for "$runtime_id")"
  state_update --arg runtime_id "$runtime_id" --arg stage_id "$stage_id" --slurpfile release "$data_path" '
    .families[$runtime_id] = {
      stageId: $stage_id,
      lifecycle: "staged",
      release: $release[0],
      publishedRids: []
    }
  '
  marker="$MOCK_FLAGS/unknown-create-${runtime_id//./-}"
  if [[ "${MOCK_UNKNOWN_CREATE_RUNTIME:-}" == "$runtime_id" && ! -e "$marker" ]]; then
    : > "$marker"
    unknown_outcome
  fi
  respond 201
  exit 0
fi

if [[ "$method" == "PUT" && "$url" =~ /stages/([0-9a-f]{32})/artifacts/([^/]+)$ ]]; then
  stage_id="${BASH_REMATCH[1]}"
  rid="${BASH_REMATCH[2]}"
  runtime_id="$(runtime_id_for_stage "$stage_id")"
  [[ -n "$runtime_id" && -f "$runtime_path" ]] || { respond 404; exit 0; }
  retry_marker="$MOCK_FLAGS/retry-upload-$rid"
  if [[ "${MOCK_UPLOAD_FAIL_ONCE_RID:-}" == "$rid" && ! -e "$retry_marker" ]]; then
    : > "$retry_marker"
    respond 503
    exit 0
  fi
  state_update --arg runtime_id "$runtime_id" --arg rid "$rid" '
    .families[$runtime_id].publishedRids = ((.families[$runtime_id].publishedRids + [$rid]) | unique)
  '
  unknown_marker="$MOCK_FLAGS/unknown-upload-$rid"
  if [[ "${MOCK_UNKNOWN_UPLOAD_RID:-}" == "$rid" && ! -e "$unknown_marker" ]]; then
    : > "$unknown_marker"
    unknown_outcome
  fi
  respond 200
  exit 0
fi

if [[ "$method" == "POST" && "$url" =~ /stages/([0-9a-f]{32})/promotion$ ]]; then
  stage_id="${BASH_REMATCH[1]}"
  runtime_id="$(runtime_id_for_stage "$stage_id")"
  point="${MOCK_INTERRUPT_POINT:-}"
  marker="$MOCK_FLAGS/interrupt-${point//[:.]/-}"
  if [[ "$point" == "before:$runtime_id" && ! -e "$marker" ]]; then
    : > "$marker"
    kill -TERM "$PPID"
    sleep 1
    exit 143
  fi
  if [[ "$(jq -r --arg runtime_id "$runtime_id" '.families[$runtime_id].publishedRids | length' "$MOCK_SERVER_STATE")" != "6" ]]; then
    respond 409
    exit 0
  fi
  state_update --arg runtime_id "$runtime_id" '.families[$runtime_id].lifecycle = "supported"'
  if [[ "$point" == "after:$runtime_id" && ! -e "$marker" ]]; then
    : > "$marker"
    kill -TERM "$PPID"
    sleep 1
    exit 143
  fi
  respond 200
  exit 0
fi

if [[ "$method" == "GET" && "$url" =~ /execution-runtimes/([^/]+)/releases/([^/]+)/revisions/([0-9]+)/artifacts/([^/]+)/download$ ]]; then
  runtime_id="${BASH_REMATCH[1]}"
  rid="${BASH_REMATCH[4]}"
  archive_file="$(jq -r --arg runtime_id "$runtime_id" --arg rid "$rid" \
    '.artifacts[] | select(.runtimeId == $runtime_id and .rid == $rid) | .archiveFile' "$MOCK_RELEASE_SET")"
  cp "$MOCK_ARTIFACT_ROOT/$rid/$archive_file" "$output_path"
  exit 0
fi

if [[ "$method" == "POST" && "$url" == */api/v1/execution-runtimes/resolve ]]; then
  runtime_id="$(jq -r '.requirement.runtimeId' "$data_path")"
  rid="$(jq -r '.rid' "$data_path")"
  response="$MOCK_FLAGS/resolve-response.json"
  jq -n \
    --arg runtime_id "$runtime_id" \
    --arg rid "$rid" \
    --slurpfile release_set "$MOCK_RELEASE_SET" '
      ($release_set[0].artifacts[] | select(.runtimeId == $runtime_id and .rid == $rid)) as $artifact
      | {
          success: true,
          runtimeArtifact: {
            identity: {
              runtimeId: $artifact.runtimeId,
              version: $artifact.version,
              packagingRevision: $artifact.packagingRevision,
              rid: $artifact.rid,
              moduleAbi: $artifact.moduleAbi
            },
            manifestSha256: $artifact.manifestSha256,
            artifactSha256: $artifact.archiveSha256,
            artifactSize: $artifact.archiveSize,
            signingKeyId: $artifact.releaseSigningKeyId,
            minimumHostTrustGeneration: $artifact.minimumHostTrustGeneration,
            nativeAttestationSha256: $artifact.nativeAttestationSha256,
            nativeAttestationKeyId: $artifact.nativeAttestationKeyId,
            lifecycle: {
              state: "supported",
              stagedAtUtc: "2026-09-02T00:00:00+00:00",
              supportedFromUtc: "2026-09-02T00:01:00+00:00",
              deprecatedAtUtc: null,
              installBlockedAtUtc: null,
              activationBlockedAtUtc: null,
              revokedAtUtc: null,
              message: null
            },
            observedRuntimePolicyRevision: 2,
            downloadUrl: "https://registry.fixture/runtime"
          },
          resolutionFingerprint: ("0" * 64),
          runtimePolicyRevision: 2,
          expiresAtUtc: "2026-09-02T00:05:00+00:00",
          warnings: [],
          conflicts: []
        }
    ' > "$response"
  respond 200 "$response"
  exit 0
fi

respond 404
MOCK_CURL

chmod +x "$mock_bin/dotnet" "$mock_bin/curl"
export MOCK_ARTIFACT_ROOT="$artifact_root"
export MOCK_CURL_LOG="$fixture_root/curl.log"
export MOCK_EXPECTED_TOKEN="fixture-admin-token"
export MOCK_FLAGS="$fixture_root/flags"
export MOCK_RELEASE_SET="$fixture_root/release-set.json"
export MOCK_SERVER_STATE="$fixture_root/server-state.json"
export MOCK_UNKNOWN_CREATE_RUNTIME="sunder.runtime.dotnet"
export MOCK_UPLOAD_FAIL_ONCE_RID="linux-arm64"
export MOCK_UNKNOWN_UPLOAD_RID="linux-x64"

reset_registry() {
  rm -rf "$MOCK_FLAGS"
  mkdir -p "$MOCK_FLAGS"
  printf '{"families":{},"mixedRuntimeId":null}\n' > "$MOCK_SERVER_STATE"
  : > "$MOCK_CURL_LOG"
}

run_publication() {
  local output_path="$1"
  local interrupt_point="${2:-}"
  PATH="$mock_bin:$PATH" \
  SUNDER_REGISTRY_ADMIN_TOKEN="$MOCK_EXPECTED_TOKEN" \
  SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS="$release_keys" \
  SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS="$attestation_keys" \
  SUNDER_EXECUTION_RUNTIME_VERIFIER_DLL="$mock_bin/dotnet" \
  MOCK_INTERRUPT_POINT="$interrupt_point" \
    bash "$publisher" \
      --lock "$lock_path" \
      --artifacts "$artifact_root" \
      --native-receipts "$native_receipts_root" \
      --release-drivers "$release_drivers_path" \
      --release-set "$MOCK_RELEASE_SET" \
      --source-binding "$fixture_root/source-binding.json" \
      --registry https://registry.fixture \
      --repository Younics/sunder-core \
      --source-sha 1111111111111111111111111111111111111111 \
      --workflow-path .github/workflows/sunder-execution-runtime-publication.yml \
      --workflow-ref refs/heads/main \
      --workflow-sha 2222222222222222222222222222222222222222 \
      --run-id 1001 \
      --run-attempt 1 \
      --output "$output_path"
}

assert_private_state() {
  local state_path="$1"
  [[ -f "$state_path" && ! -L "$state_path" ]]
  if [[ "$(uname -s)" == "Darwin" ]]; then
    permissions="$(stat -f '%Lp' "$state_path")"
  else
    permissions="$(stat -c '%a' "$state_path")"
  fi
  [[ "$permissions" == "600" ]] || { printf 'Publication state permissions were %s, not 600.\n' "$permissions" >&2; exit 1; }
  if grep -Fq "$MOCK_EXPECTED_TOKEN" "$state_path"; then
    printf 'Publication state persisted the administrator credential.\n' >&2
    exit 1
  fi
}

mutation_count() {
  jq -R -s '
    split("\n")
    | map(select(
        test("/admin/execution-runtimes/stages$")
        or test("/artifacts/[^/]+$")
        or test("/promotion$")))
    | length
  ' "$MOCK_CURL_LOG"
}

for point in \
  "before:sunder.runtime.dotnet" \
  "after:sunder.runtime.dotnet" \
  "before:sunder.runtime.node" \
  "after:sunder.runtime.node"; do
  reset_registry
  safe_point="${point//[:.]/-}"
  state_path="$fixture_root/publication-$safe_point.json"
  if run_publication "$state_path" "$point" > "$fixture_root/first-$safe_point.log" 2>&1; then
    printf 'Publication unexpectedly completed at interruption point %s.\n' "$point" >&2
    exit 1
  fi
  if [[ ! -e "$MOCK_FLAGS/interrupt-$safe_point" ]]; then
    command cat "$fixture_root/first-$safe_point.log" >&2
    printf 'Fixture did not reach deterministic interruption point %s.\n' "$point" >&2
    exit 1
  fi
  assert_private_state "$state_path"
  jq -e '.decision == "publishing"' "$state_path" >/dev/null

  run_publication "$state_path" > "$fixture_root/resume-$safe_point.log" 2>&1
  assert_private_state "$state_path"
  jq -e '
    .decision == "published"
    and (.promotions | length) == 2
    and (.resolutions | length) == 12
    and all(.progress[]; .lifecycleState == "supported")
  ' "$state_path" >/dev/null
  jq -e 'all(.families[]; .lifecycle == "supported" and (.publishedRids | length) == 6)' \
    "$MOCK_SERVER_STATE" >/dev/null

  mutation_count_before="$(mutation_count)"
  recovered_without_state="$fixture_root/recovered-without-state-$safe_point.json"
  run_publication "$recovered_without_state" > "$fixture_root/recovered-$safe_point.log" 2>&1
  mutation_count_after="$(mutation_count)"
  [[ "$mutation_count_after" == "$mutation_count_before" ]] \
    || { printf 'Already-Supported exact families were mutated again after state loss at %s.\n' "$point" >&2; exit 1; }
  jq -e '.decision == "published" and (.promotions | length) == 2' "$recovered_without_state" >/dev/null
done

jq '.mixedRuntimeId = "sunder.runtime.dotnet"' "$MOCK_SERVER_STATE" > "$fixture_root/mixed-state.json"
mv "$fixture_root/mixed-state.json" "$MOCK_SERVER_STATE"
if run_publication "$fixture_root/mixed-publication.json" > "$fixture_root/mixed.log" 2>&1; then
  printf 'Publication accepted mismatched exact-family Registry state.\n' >&2
  exit 1
fi

printf 'Resumable execution-runtime publication fixture passed all unknown-outcome and family-promotion interruption points.\n'
