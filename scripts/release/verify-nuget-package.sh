#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: verify-nuget-package.sh <expected-unsigned.nupkg> <published-signed.nupkg>\n' >&2
  exit 2
fi

expected_package="$1"
published_package="$2"
[[ -f "$expected_package" ]] || { printf 'Expected NuGet package not found: %s\n' "$expected_package" >&2; exit 1; }
[[ -f "$published_package" ]] || { printf 'Published NuGet package not found: %s\n' "$published_package" >&2; exit 1; }

for required_command in diff dotnet sed unzip; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify NuGet packages.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify NuGet packages.\n' >&2
  exit 127
fi

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  else
    shasum -a 256 | cut -d ' ' -f 1
  fi
}

canonical_nupkg_manifest() {
  local package="$1"
  local output="$2"
  local entry
  local entry_lower
  local entry_pattern

  : > "$output"
  while IFS= read -r entry; do
    entry_lower="$(printf '%s' "$entry" | tr '[:upper:]' '[:lower:]')"
    [[ "$entry_lower" == ".signature.p7s" ]] && continue
    entry_pattern="$(printf '%s' "$entry" | sed -e 's/\\/\\\\/g' -e 's/\[/[[]/g' -e 's/\*/\\*/g' -e 's/?/\\?/g')"
    printf '%s  %s\n' "$(unzip -p "$package" "$entry_pattern" | hash_stream)" "$entry" >> "$output"
  done < <(unzip -Z1 "$package" | LC_ALL=C sort)
}

temp_root="${TMPDIR:-/tmp}"
temp_dir="$(mktemp -d "${temp_root%/}/sunder-nuget-verify.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT
expected_manifest="$temp_dir/expected.manifest"
published_manifest="$temp_dir/published.manifest"
signature_config="$temp_dir/nuget-signature.config"

canonical_nupkg_manifest "$expected_package" "$expected_manifest"
canonical_nupkg_manifest "$published_package" "$published_manifest"
if ! diff -u "$expected_manifest" "$published_manifest"; then
  printf 'Published NuGet package canonical content differs from the expected release artifact.\n' >&2
  exit 1
fi

dotnet nuget verify "$published_package" --all
cat > "$signature_config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <trustedSigners />
</configuration>
EOF
dotnet nuget trust repository nuget.org "$published_package" --configfile "$signature_config"
