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

for required_command in cut diff dotnet sed sort tr unzip; do
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
  local entries="${output}.entries"
  local entry
  local entry_hash
  local entry_lower
  local entry_pattern

  : > "$output"
  if ! unzip -Z1 "$package" | LC_ALL=C sort > "$entries"; then
    printf 'Failed to list NuGet package entries: %s\n' "$package" >&2
    rm -f "$entries"
    return 1
  fi

  while IFS= read -r entry; do
    entry_lower="$(printf '%s' "$entry" | tr '[:upper:]' '[:lower:]')"
    [[ "$entry_lower" == ".signature.p7s" ]] && continue
    entry_pattern="$(printf '%s' "$entry" | sed -e 's/\\/\\\\/g' -e 's/\[/[[]/g' -e 's/\*/\\*/g' -e 's/?/\\?/g')"
    if ! entry_hash="$(unzip -p "$package" "$entry_pattern" | hash_stream)"; then
      printf "Failed to extract NuGet package entry '%s' from %s.\n" "$entry" "$package" >&2
      rm -f "$entries"
      return 1
    fi
    printf '%s  %s\n' "$entry_hash" "$entry" >> "$output"
  done < "$entries"
  rm -f "$entries"
}

temp_root="${TMPDIR:-/tmp}"
temp_dir="$(mktemp -d "${temp_root%/}/sunder-nuget-verify.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT
expected_manifest="$temp_dir/expected.manifest"
published_manifest="$temp_dir/published.manifest"
signature_config="$temp_dir/nuget-signature.config"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_verifier="$script_dir/verify-nuget-source.sh"
[[ -f "$source_verifier" ]] || { printf 'NuGet source verifier not found: %s\n' "$source_verifier" >&2; exit 1; }

canonical_nupkg_manifest "$expected_package" "$expected_manifest"
canonical_nupkg_manifest "$published_package" "$published_manifest"
if ! diff -u "$expected_manifest" "$published_manifest"; then
  printf 'Published NuGet package canonical content differs from the expected release artifact.\n' >&2
  exit 1
fi

cat > "$signature_config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="signatureValidationMode" value="require" />
  </config>
  <trustedSigners />
</configuration>
EOF
bash "$source_verifier"
dotnet nuget trust source nuget.org \
  --source-url https://api.nuget.org/v3/index.json \
  --configfile "$signature_config"
dotnet nuget verify "$published_package" --all --configfile "$signature_config"
