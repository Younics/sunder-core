#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 0 ]]; then
  printf 'Usage: verify-nuget-source.sh\n' >&2
  exit 2
fi

for required_command in curl jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to verify the NuGet.org source.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to verify the NuGet.org source.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

fetch_exact_https() {
  local url="$1"
  local output="$2"
  curl --fail --silent --show-error \
    --proto '=https' \
    --tlsv1.2 \
    --max-redirs 0 \
    --connect-timeout 10 \
    --max-time 45 \
    --retry 2 \
    --retry-all-errors \
    --retry-delay 2 \
    --retry-max-time 120 \
    "$url" \
    --output "$output"
}

readonly source_url='https://api.nuget.org/v3/index.json'
readonly flat_container='https://api.nuget.org/v3-flatcontainer/'
readonly publish_endpoint='https://www.nuget.org/api/v2/package'
readonly signatures_url='https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json'
readonly certificate_prefix='https://api.nuget.org/v3-index/repository-signatures/certificates/'
readonly repository_subject='CN=NuGet.org Repository by Microsoft, O=NuGet.org Repository by Microsoft, L=Redmond, S=Washington, C=US'

temp_root="${TMPDIR:-/tmp}"
temp_dir="$(mktemp -d "${temp_root%/}/sunder-nuget-source.XXXXXX")"
trap 'rm -rf "$temp_dir"' EXIT
service_index="$temp_dir/service-index.json"
signatures_index="$temp_dir/repository-signatures.json"

fetch_exact_https "$source_url" "$service_index"
jq -e \
  --arg flat_container "$flat_container" \
  --arg publish_endpoint "$publish_endpoint" \
  --arg signatures_url "$signatures_url" '
    .version == "3.0.0"
    and ([.resources[] | select(."@type" == "PackageBaseAddress/3.0.0" and ."@id" == $flat_container)] | length) == 1
    and ([.resources[] | select(."@type" == "PackagePublish/2.0.0" and ."@id" == $publish_endpoint)] | length) == 1
    and ([.resources[] | select(."@type" == "RepositorySignatures/5.0.0" and ."@id" == $signatures_url)] | length) == 1
  ' "$service_index" >/dev/null \
  || { printf 'NuGet.org service index does not expose the pinned package, publish, and repository-signature endpoints.\n' >&2; exit 1; }

fetch_exact_https "$signatures_url" "$signatures_index"
jq -e \
  --arg certificate_prefix "$certificate_prefix" \
  --arg repository_subject "$repository_subject" '
    .allRepositorySigned == true
    and (.signingCertificates | type == "array" and length > 0)
    and all(.signingCertificates[];
      (.fingerprints["2.16.840.1.101.3.4.2.1"] | type == "string" and test("^[0-9a-f]{64}$"))
      and .subject == $repository_subject
      and .contentUrl == ($certificate_prefix + .fingerprints["2.16.840.1.101.3.4.2.1"] + ".crt"))
  ' "$signatures_index" >/dev/null \
  || { printf 'NuGet.org repository-signature metadata is incomplete or has an unexpected identity.\n' >&2; exit 1; }

while IFS=$'\t' read -r fingerprint certificate_url; do
  certificate_path="$temp_dir/$fingerprint.crt"
  fetch_exact_https "$certificate_url" "$certificate_path"
  actual_fingerprint="$(hash_file "$certificate_path" | tr '[:upper:]' '[:lower:]')"
  [[ "$actual_fingerprint" == "$fingerprint" ]] \
    || { printf 'NuGet.org repository certificate fingerprint mismatch for %s.\n' "$certificate_url" >&2; exit 1; }
done < <(jq -r '.signingCertificates[] | [.fingerprints["2.16.840.1.101.3.4.2.1"], .contentUrl] | @tsv' "$signatures_index")

printf 'Verified NuGet.org service endpoints and repository-signing certificate fingerprints.\n'
