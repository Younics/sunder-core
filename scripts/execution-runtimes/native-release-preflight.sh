#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: native-release-preflight.sh --lock <production-lock> [--rid <rid> --runner-profile <profile>] [--phase native|acceptance]

Validates external release approvals and the production lock. It accepts signer
adapter identities only; private keys and private-key paths are not supported.
USAGE
}

lock_path=""
rid=""
runner_profile=""
phase="acceptance"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --lock)
      lock_path="${2:-}"
      shift 2
      ;;
    --rid)
      rid="${2:-}"
      shift 2
      ;;
    --runner-profile)
      runner_profile="${2:-}"
      shift 2
      ;;
    --phase) phase="${2:-}"; shift 2 ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$lock_path" || ( -n "$rid" && -z "$runner_profile" ) || ( -z "$rid" && -n "$runner_profile" ) \
      || ( "$phase" != native && "$phase" != acceptance ) ]]; then
  usage >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
if [[ "$lock_path" != /* ]]; then
  lock_path="$repo_root/$lock_path"
fi
if [[ ! -f "$lock_path" || -L "$lock_path" ]]; then
  echo "Approved production execution-runtime lock is missing or is not a regular file: $lock_path" >&2
  exit 1
fi
if [[ "$(basename "$lock_path")" != "production.lock.json" ]]; then
  echo "Native release requires the explicitly approved production.lock.json, never a fixture lock." >&2
  exit 1
fi

require_approval() {
  local status_name="$1"
  local reference_name="$2"
  local status="${!status_name:-}"
  local reference="${!reference_name:-}"
  if [[ "$status" != "approved" || ! "$reference" =~ ^https://[^/?#]+/[^[:space:]#]+$ ]]; then
    echo "External gate '$status_name' is not approved with an HTTPS evidence reference in '$reference_name'." >&2
    exit 1
  fi
}

require_identifier() {
  local name="$1"
  local value="${!name:-}"
  if [[ ! "$value" =~ ^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$ ]]; then
    echo "External configuration '$name' must contain an approved lower-case adapter/profile identity." >&2
    exit 1
  fi
}

require_nonempty() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "External configuration '$name' is required." >&2
    exit 1
  fi
}

require_sha256() {
  local name="$1"
  local value="${!name:-}"
  if [[ ! "$value" =~ ^[0-9a-f]{64}$ ]]; then
    echo "External configuration '$name' must contain one exact lower-case SHA-256 digest." >&2
    exit 1
  fi
}

require_approval SUNDER_EXECUTION_RUNTIME_SOURCES_APPROVED SUNDER_EXECUTION_RUNTIME_SOURCES_APPROVAL_REFERENCE
require_approval SUNDER_EXECUTION_RUNTIME_SIGNERS_APPROVED SUNDER_EXECUTION_RUNTIME_SIGNERS_APPROVAL_REFERENCE
require_approval SUNDER_EXECUTION_RUNTIME_RUNNERS_APPROVED SUNDER_EXECUTION_RUNTIME_RUNNERS_APPROVAL_REFERENCE
require_identifier SUNDER_EXECUTION_RUNTIME_RELEASE_SIGNER_ADAPTER
require_identifier SUNDER_EXECUTION_RUNTIME_ATTESTATION_SIGNER_ADAPTER
require_nonempty SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS
require_nonempty SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS
if [[ "$phase" == native ]]; then
  require_sha256 SUNDER_EXECUTION_RUNTIME_LOCK_SHA256
  python3 "$repo_root/scripts/release/release_coordinates.py" runtime --lock "$lock_path"
else
  # Existing production-acceptance drivers retain their publication approval gate.
  require_approval SUNDER_EXECUTION_RUNTIME_PUBLICATION_APPROVED SUNDER_EXECUTION_RUNTIME_PUBLICATION_APPROVAL_REFERENCE
fi

for suffix in WIN_X64 WIN_ARM64 LINUX_X64 LINUX_ARM64 OSX_X64 OSX_ARM64; do
  require_nonempty "SUNDER_EXECUTION_RUNTIME_RUNNER_${suffix}"
  require_identifier "SUNDER_EXECUTION_RUNTIME_RUNNER_PROFILE_${suffix}"
  require_nonempty "SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_${suffix}"
  require_sha256 "SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_SHA256_${suffix}"
done
if [[ -n "$rid" ]]; then
  require_nonempty SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER
  require_sha256 SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_SHA256
fi

project="$repo_root/languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj"
dotnet run --project "$project" --configuration Release --no-restore -- \
  validate-lock --lock "$lock_path" --kind production
if [[ -n "$rid" ]]; then
  dotnet run --project "$project" --configuration Release --no-restore -- \
    validate-native-lane \
    --lock "$lock_path" \
    --kind production \
    --rid "$rid" \
    --runner-profile "$runner_profile"
fi
