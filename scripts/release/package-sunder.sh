#!/usr/bin/env bash
set -euo pipefail

version=""
runtime="linux-x64"
channel="stable"
configuration="Release"
output_root="artifacts"
github_repository_url=""
github_token=""
include_prerelease_updates="false"

usage() {
  cat <<'USAGE'
Usage: package-sunder.sh --version <semver> [--runtime <rid>] [--channel stable|beta|nightly]
                         [--github-repository-url <url>] [--include-prerelease-updates]

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
    --github-repository-url)
      github_repository_url="${2:-}"
      shift 2
      ;;
    --github-token)
      github_token="${2:-}"
      shift 2
      ;;
    --include-prerelease-updates)
      include_prerelease_updates="true"
      shift
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

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
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

create_macos_icon() {
  local source_png="$repo_root/src/Host/Sunder.App/Assets/Images/logo.png"
  local icon_root="$artifact_root/icons"
  local iconset="$icon_root/Sunder.iconset"
  local icns="$icon_root/Sunder.icns"

  if [[ -f "$icns" ]]; then
    printf '%s\n' "$icns"
    return 0
  fi

  if ! command -v sips >/dev/null 2>&1 || ! command -v iconutil >/dev/null 2>&1; then
    echo "macOS packaging requires sips and iconutil to create an .icns icon." >&2
    exit 1
  fi

  rm -rf "$iconset"
  mkdir -p "$iconset"
  sips -z 16 16 "$source_png" --out "$iconset/icon_16x16.png" >/dev/null
  sips -z 32 32 "$source_png" --out "$iconset/icon_16x16@2x.png" >/dev/null
  sips -z 32 32 "$source_png" --out "$iconset/icon_32x32.png" >/dev/null
  sips -z 64 64 "$source_png" --out "$iconset/icon_32x32@2x.png" >/dev/null
  sips -z 128 128 "$source_png" --out "$iconset/icon_128x128.png" >/dev/null
  sips -z 256 256 "$source_png" --out "$iconset/icon_128x128@2x.png" >/dev/null
  sips -z 256 256 "$source_png" --out "$iconset/icon_256x256.png" >/dev/null
  sips -z 512 512 "$source_png" --out "$iconset/icon_256x256@2x.png" >/dev/null
  sips -z 512 512 "$source_png" --out "$iconset/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$source_png" --out "$iconset/icon_512x512@2x.png" >/dev/null
  iconutil -c icns "$iconset" -o "$icns"
  printf '%s\n' "$icns"
}

publish_dir="$artifact_root/publish/sunder/$runtime"
release_dir="$artifact_root/velopack/$channel/$runtime"
velopack_channel="app-$runtime-$channel"
main_exe="Sunder.App"

if [[ "$runtime" == win-* ]]; then
  main_exe="Sunder.App.exe"
fi

for restore_project in "$project_path" "$runtime_host_project_path" "$cli_project_path"; do
  dotnet restore "$restore_project" -r "$runtime" -p:Configuration="$configuration"
done

rm -rf "$publish_dir" "$release_dir"
mkdir -p "$publish_dir" "$release_dir"

if [[ -n "$github_repository_url" ]]; then
  effective_github_token="${github_token:-${GITHUB_TOKEN:-}}"
  download_args=(download github --repoUrl "$github_repository_url" --channel "$velopack_channel" --outputDir "$release_dir")

  if [[ -n "$effective_github_token" ]]; then
    download_args+=(--token "$effective_github_token")
  fi

  if [[ "$include_prerelease_updates" == "true" ]]; then
    download_args+=(--pre)
  fi

  if ! vpk "${download_args[@]}"; then
    echo "Existing Velopack assets for channel '$velopack_channel' could not be downloaded. Continuing without delta history." >&2
  fi
fi

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

pack_args=(pack \
  --packId Sunder \
  --packTitle Sunder \
  --packVersion "$version" \
  --packDir "$publish_dir" \
  --mainExe "$main_exe" \
  --runtime "$runtime" \
  --channel "$velopack_channel" \
  --outputDir "$release_dir")

case "$runtime" in
  win-*)
    pack_args+=(--icon "$repo_root/src/Host/Sunder.App/Assets/Images/app.ico")
    ;;
  linux-*)
    pack_args+=(--icon "$repo_root/src/Host/Sunder.App/Assets/Images/logo.png" --categories Utility)
    ;;
  osx-*)
    macos_icon="$(create_macos_icon)"
    pack_args+=(--icon "$macos_icon" --bundleId dev.sunder.app)
    ;;
esac

vpk "${pack_args[@]}"

echo "Sunder Velopack release created: $release_dir ($velopack_channel)"
