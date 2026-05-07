#!/usr/bin/env bash
set -euo pipefail

version=""
runtime="linux-x64"
channel="stable"
configuration="Release"
output_root="artifacts"

usage() {
  cat <<'USAGE'
Usage: package-sunder.sh --version <semver> [--runtime <rid>] [--channel stable|beta|nightly]

Examples:
  ./scripts/release/package-sunder.sh --version 0.1.0 --runtime linux-x64
  ./scripts/release/package-sunder.sh --version 0.1.0-beta.1 --runtime osx-arm64 --channel beta
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      version="${2:-}"
      shift 2
      ;;
    --runtime)
      runtime="${2:-}"
      shift 2
      ;;
    --channel)
      channel="${2:-}"
      shift 2
      ;;
    --configuration)
      configuration="${2:-}"
      shift 2
      ;;
    --output-root)
      output_root="${2:-}"
      shift 2
      ;;
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

if [[ -z "$version" ]]; then
  echo "--version is required." >&2
  usage >&2
  exit 2
fi

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.-]+)?$ ]]; then
  echo "Version '$version' is not a valid SemVer value for Velopack." >&2
  exit 2
fi

if [[ "$channel" != "stable" && "$channel" != "beta" && "$channel" != "nightly" ]]; then
  echo "Channel must be stable, beta, or nightly." >&2
  exit 2
fi

if ! command -v vpk >/dev/null 2>&1; then
  echo "The Velopack CLI 'vpk' was not found. Install it with: dotnet tool install --global vpk --version 0.0.1298" >&2
  exit 127
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
project_path="$repo_root/src/Host/Sunder.App/Sunder.App.csproj"
runtime_host_project_path="$repo_root/src/Host/Sunder.Runtime.Host/Sunder.Runtime.Host.csproj"
cli_project_path="$repo_root/src/Host/Sunder.Cli/Sunder.Cli.csproj"
if [[ "$output_root" = /* ]]; then
  artifact_root="$output_root"
else
  artifact_root="$repo_root/$output_root"
fi
publish_dir="$artifact_root/publish/sunder/$runtime"
release_dir="$artifact_root/velopack/$channel/$runtime"
main_exe="Sunder.App"

if [[ "$runtime" == win-* ]]; then
  main_exe="Sunder.App.exe"
fi

for restore_project in "$project_path" "$runtime_host_project_path" "$cli_project_path"; do
  dotnet restore "$restore_project" -r "$runtime" -p:Configuration="$configuration"
done

rm -rf "$publish_dir" "$release_dir"
mkdir -p "$publish_dir" "$release_dir"

dotnet publish "$project_path" \
  -c "$configuration" \
  -r "$runtime" \
  --no-restore \
  --self-contained true \
  -p:Version="$version" \
  -p:InformationalVersion="$version" \
  -p:ContinuousIntegrationBuild=true \
  -p:PublishSingleFile=false \
  -o "$publish_dir"

vpk pack \
  --packId Sunder \
  --packTitle Sunder \
  --packVersion "$version" \
  --packDir "$publish_dir" \
  --mainExe "$main_exe" \
  --runtime "$runtime" \
  --channel "$channel" \
  --outputDir "$release_dir"

echo "Sunder Velopack release created: $release_dir"
