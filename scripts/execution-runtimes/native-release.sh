#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: native-release.sh --lock <production-lock> --rid <rid> --runner-profile <profile> --output <directory> --receipt <path>

Delegates native build, attestation, and signing to an externally approved driver.
The SHA-256-pinned driver must be one self-contained executable and use workload
identity or non-exportable signer services; no private key is accepted here.
USAGE
}

lock_path=""
rid=""
runner_profile=""
output_path=""
receipt_path=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --lock) lock_path="${2:-}"; shift 2 ;;
    --rid) rid="${2:-}"; shift 2 ;;
    --runner-profile) runner_profile="${2:-}"; shift 2 ;;
    --output) output_path="${2:-}"; shift 2 ;;
    --receipt) receipt_path="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ -z "$lock_path" || -z "$rid" || -z "$runner_profile" || -z "$output_path" || -z "$receipt_path" ]]; then
  usage >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$script_dir/native-release-preflight.sh" \
  --phase "${SUNDER_EXECUTION_RUNTIME_BUILD_PHASE:-acceptance}" \
  --lock "$lock_path" \
  --rid "$rid" \
  --runner-profile "$runner_profile"

driver="${SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER:-}"
driver_sha256="${SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_SHA256:-}"
if [[ ! -f "$driver" || ! -x "$driver" || -L "$driver" ]]; then
  echo "Approved native release driver is not a real executable file on this runner: $driver" >&2
  exit 1
fi
if [[ ! "$driver_sha256" =~ ^[0-9a-f]{64}$ ]]; then
  echo "Approved native release driver SHA-256 is missing or invalid." >&2
  exit 1
fi
if [[ -e "$output_path" ]]; then
  echo "Native release output must not already exist: $output_path" >&2
  exit 1
fi
if [[ -e "$receipt_path" ]]; then
  echo "Native release receipt must not already exist: $receipt_path" >&2
  exit 1
fi
if [[ ! -d "${SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS:-}" || -L "${SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS:-}" ]]; then
  echo "Approved release public-key directory is unavailable on this runner." >&2
  exit 1
fi
if [[ ! -d "${SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS:-}" || -L "${SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS:-}" ]]; then
  echo "Approved attestation public-key directory is unavailable on this runner." >&2
  exit 1
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

if [[ "$(hash_file "$driver")" != "$driver_sha256" ]]; then
  echo "Approved native release driver does not match its configured SHA-256." >&2
  exit 1
fi

driver_snapshot_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-runtime-driver.XXXXXX")"
cleanup_driver_snapshot() {
  chmod 0755 "$driver_snapshot_root" 2>/dev/null || true
  rm -rf "$driver_snapshot_root"
}
trap cleanup_driver_snapshot EXIT
driver_snapshot="$driver_snapshot_root/$(basename "$driver")"
cp -p "$driver" "$driver_snapshot"
chmod 0555 "$driver_snapshot"
chmod 0555 "$driver_snapshot_root"
if [[ -w "$driver_snapshot_root" || -w "$driver_snapshot" \
      || "$(hash_file "$driver_snapshot")" != "$driver_sha256" ]]; then
  echo "Approved native release driver snapshot does not match its configured SHA-256." >&2
  exit 1
fi
mkdir -p "$output_path"

"$driver_snapshot" \
  --lock "$lock_path" \
  --rid "$rid" \
  --runner-profile "$runner_profile" \
  --release-signer-adapter "$SUNDER_EXECUTION_RUNTIME_RELEASE_SIGNER_ADAPTER" \
  --attestation-signer-adapter "$SUNDER_EXECUTION_RUNTIME_ATTESTATION_SIGNER_ADAPTER" \
  --output "$output_path"

if [[ "$(hash_file "$driver_snapshot")" != "$driver_sha256" \
      || "$(hash_file "$driver")" != "$driver_sha256" ]]; then
    echo "Approved native release driver changed during execution." >&2
    exit 1
fi

shopt -s nullglob
artifacts=("$output_path"/*.sunderruntime)
all_outputs=("$output_path"/*)
if [[ ${#artifacts[@]} -ne 2 || ${#all_outputs[@]} -ne 2 ]]; then
  echo "Approved driver must emit exactly two .sunderruntime files and no other outputs for RID '$rid'." >&2
  exit 1
fi
for artifact in "${artifacts[@]}"; do
  if [[ ! -f "$artifact" || -L "$artifact" || ! -s "$artifact" ]]; then
    echo "Native release output is not a non-empty regular archive: $artifact" >&2
    exit 1
  fi
done

project="$script_dir/../../languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj"
dotnet run --project "$project" --configuration Release --no-restore -- \
  validate-native-output \
  --lock "$lock_path" \
  --kind production \
  --rid "$rid" \
  --directory "$output_path" \
  --release-public-keys "$SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS" \
  --attestation-public-keys "$SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS" \
  --release-driver-sha256 "$driver_sha256" \
  --receipt "$receipt_path"
