#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 1 ]]; then
  printf 'Usage: verify-nuget-package.fixture.sh [repository-signed.nupkg]\n' >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-nuget-package.sh"
[[ -f "$verifier" ]] || { printf 'Verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in cmp curl grep unzip zip; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the NuGet verification fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-nuget-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
signed_package="$fixture_dir/published-signed.nupkg"
unsigned_package="$fixture_dir/local-unsigned.nupkg"
tampered_package="$fixture_dir/published-tampered.nupkg"

if [[ $# -eq 1 ]]; then
  cp "$1" "$signed_package"
else
  curl -fsSL --connect-timeout 10 --max-time 45 \
    --retry 2 --retry-all-errors --retry-delay 2 --retry-max-time 120 \
    'https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/1.0.128/microsoft.artifactsigning.client.1.0.128.nupkg' \
    -o "$signed_package"
fi

unzip -Z1 "$signed_package" | grep -Fqx '.signature.p7s' \
  || { printf 'Fixture package is not repository signed.\n' >&2; exit 1; }
cp "$signed_package" "$unsigned_package"
zip -q -d "$unsigned_package" '.signature.p7s'
if cmp -s "$unsigned_package" "$signed_package"; then
  printf 'Fixture setup failed: signed and unsigned package bytes are identical.\n' >&2
  exit 1
fi

bash "$verifier" "$unsigned_package" "$signed_package"

unsigned_failure_log="$fixture_dir/unsigned-verification.log"
if bash "$verifier" "$unsigned_package" "$unsigned_package" > "$unsigned_failure_log" 2>&1; then
  printf 'Signature verification unexpectedly accepted an unsigned published package.\n' >&2
  exit 1
fi

payload_entry=''
while IFS= read -r entry; do
  entry_lower="$(printf '%s' "$entry" | tr '[:upper:]' '[:lower:]')"
  if [[ "$entry_lower" == *.nuspec ]]; then
    payload_entry="$entry"
    break
  fi
done < <(unzip -Z1 "$signed_package" | LC_ALL=C sort)
[[ -n "$payload_entry" ]] || { printf 'Fixture package does not contain a .nuspec payload.\n' >&2; exit 1; }
[[ "$payload_entry" != */* ]] || { printf 'Fixture .nuspec must be at the package root.\n' >&2; exit 1; }

mkdir -p "$fixture_dir/tampered"
unzip -p "$signed_package" "$payload_entry" > "$fixture_dir/tampered/$payload_entry"
printf '\n<!-- canonical-content-fixture-change -->\n' >> "$fixture_dir/tampered/$payload_entry"
cp "$signed_package" "$tampered_package"
(
  cd "$fixture_dir/tampered"
  zip -q -u "$tampered_package" "$payload_entry"
)

failure_log="$fixture_dir/tampered-verification.log"
if bash "$verifier" "$unsigned_package" "$tampered_package" > "$failure_log" 2>&1; then
  printf 'Canonical comparison unexpectedly accepted a changed package payload.\n' >&2
  exit 1
fi
grep -Fq 'canonical content differs' "$failure_log" \
  || { printf 'Changed payload did not fail canonical comparison as expected.\n' >&2; exit 1; }

printf 'NuGet verification fixture passed: signature-only changes accepted; unsigned and changed packages rejected.\n'
