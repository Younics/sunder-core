#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
verifier="$script_dir/verify-nuget-source.sh"
[[ -f "$verifier" ]] || { printf 'Verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the NuGet source fixture.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to run the NuGet source fixture.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-nuget-source-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"
printf 'fixture repository certificate\n' > "$fixture_dir/repository.crt"
fingerprint="$(hash_file "$fixture_dir/repository.crt")"

jq -n \
  --arg signatures 'https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json' '
  {
    version: "3.0.0",
    resources: [
      {"@id": "https://api.nuget.org/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0"},
      {"@id": "https://www.nuget.org/api/v2/package", "@type": "PackagePublish/2.0.0"},
      {"@id": $signatures, "@type": "RepositorySignatures/5.0.0"}
    ]
  }' > "$fixture_dir/service-index.json"
jq -n \
  --arg fingerprint "$fingerprint" \
  --arg subject 'CN=NuGet.org Repository by Microsoft, O=NuGet.org Repository by Microsoft, L=Redmond, S=Washington, C=US' \
  --arg url "https://api.nuget.org/v3-index/repository-signatures/certificates/$fingerprint.crt" '
  {
    allRepositorySigned: true,
    signingCertificates: [{
      fingerprints: {"2.16.840.1.101.3.4.2.1": $fingerprint},
      subject: $subject,
      contentUrl: $url
    }]
  }' > "$fixture_dir/repository-signatures.json"

cat > "$mock_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_CURL_LOG"
output=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output|-o) output="${2:?}"; shift 2 ;;
    https://*) url="$1"; shift ;;
    *) shift ;;
  esac
done
[[ -n "$output" && -n "$url" ]] || exit 64
case "$url" in
  https://api.nuget.org/v3/index.json)
    cp "$MOCK_SERVICE_INDEX" "$output"
    ;;
  https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json)
    cp "$MOCK_SIGNATURES_INDEX" "$output"
    ;;
  https://api.nuget.org/v3-index/repository-signatures/certificates/*.crt)
    cp "$MOCK_CERTIFICATE" "$output"
    ;;
  *) exit 65 ;;
esac
EOF
chmod +x "$mock_bin/curl"
export MOCK_CERTIFICATE="$fixture_dir/repository.crt"
export MOCK_CURL_LOG="$fixture_dir/curl.log"
export MOCK_SERVICE_INDEX="$fixture_dir/service-index.json"
export MOCK_SIGNATURES_INDEX="$fixture_dir/repository-signatures.json"

PATH="$mock_bin:$PATH" bash "$verifier"
grep -Fq -- "--proto =https --tlsv1.2 --max-redirs 0" "$MOCK_CURL_LOG" \
  || { printf 'NuGet source verifier did not require exact HTTPS without redirects.\n' >&2; exit 1; }

jq '.resources |= map(select(."@type" != "RepositorySignatures/5.0.0"))' \
  "$fixture_dir/service-index.json" > "$fixture_dir/invalid-service-index.json"
if MOCK_SERVICE_INDEX="$fixture_dir/invalid-service-index.json" PATH="$mock_bin:$PATH" \
  bash "$verifier" > "$fixture_dir/invalid-service.log" 2>&1; then
  printf 'NuGet source verifier accepted a status-only service-index response without signature metadata.\n' >&2
  exit 1
fi

printf 'different certificate bytes\n' > "$fixture_dir/wrong.crt"
if MOCK_CERTIFICATE="$fixture_dir/wrong.crt" PATH="$mock_bin:$PATH" \
  bash "$verifier" > "$fixture_dir/wrong-certificate.log" 2>&1; then
  printf 'NuGet source verifier accepted repository certificate bytes with the wrong fingerprint.\n' >&2
  exit 1
fi

printf 'NuGet source fixture passed: endpoint content, HTTPS policy, and certificate fingerprints are enforced.\n'
