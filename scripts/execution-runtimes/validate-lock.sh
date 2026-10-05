#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' "Usage: validate-lock.sh <lock-path> <production|test-fixture>"
}

if [[ $# -ne 2 ]]; then
  usage >&2
  exit 2
fi

lock_path="$1"
lock_kind="$2"
case "$lock_kind" in
  production|test-fixture) ;;
  *)
    echo "Lock kind must be production or test-fixture." >&2
    exit 2
    ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
if [[ "$lock_path" != /* ]]; then
  lock_path="$repo_root/$lock_path"
fi

dotnet run \
  --project "$repo_root/languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj" \
  --configuration Release \
  --no-restore \
  -- \
  validate-lock \
  --lock "$lock_path" \
  --kind "$lock_kind"
