#!/usr/bin/env bash
set -euo pipefail

repository="Younics/sunder-core"
version="latest"

usage() {
  cat <<'USAGE'
Usage: install.sh [--repo owner/name] [--version tag]

Examples:
  curl -fsSL https://raw.githubusercontent.com/Younics/sunder-core/main/scripts/install/install.sh | sh
  ./scripts/install/install.sh --version v0.1.0
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      repository="${2:-}"
      shift 2
      ;;
    --version)
      version="${2:-}"
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

os="$(uname -s)"
arch="$(uname -m)"
runtime=""
asset_pattern=""

case "$os:$arch" in
  Linux:x86_64|Linux:amd64)
    runtime="linux-x64"
    asset_pattern='\.AppImage$'
    ;;
  Linux:aarch64|Linux:arm64)
    runtime="linux-arm64"
    asset_pattern='\.AppImage$'
    ;;
  Darwin:x86_64)
    runtime="osx-x64"
    asset_pattern='\.(pkg|dmg|zip)$'
    ;;
  Darwin:arm64)
    runtime="osx-arm64"
    asset_pattern='\.(pkg|dmg|zip)$'
    ;;
  *)
    echo "Unsupported platform: $os $arch" >&2
    exit 1
    ;;
esac

if [[ "$version" == "latest" ]]; then
  release_api_url="https://api.github.com/repos/$repository/releases/latest"
else
  release_api_url="https://api.github.com/repos/$repository/releases/tags/$version"
fi

release_json="$(curl -fsSL -H "User-Agent: sunder-install-script" "$release_api_url")"
asset_url="$(printf '%s\n' "$release_json" \
  | grep -E '"browser_download_url": ' \
  | grep "$runtime" \
  | grep -E "$asset_pattern" \
  | head -n 1 \
  | sed -E 's/.*"browser_download_url": "([^"]+)".*/\1/')"

if [[ -z "$asset_url" ]]; then
  echo "No Sunder asset matching runtime '$runtime' was found in $repository release '$version'." >&2
  exit 1
fi

asset_name="$(basename "$asset_url")"
tmp_file="$(mktemp -t sunder-install.XXXXXX)"
trap 'rm -f "$tmp_file"' EXIT

echo "Downloading $asset_name from $repository..."
curl -fL "$asset_url" -o "$tmp_file"

if [[ "$os" == "Linux" ]]; then
  install_dir="$HOME/.local/share/sunder"
  bin_dir="$HOME/.local/bin"
  appimage_path="$install_dir/Sunder.AppImage"
  mkdir -p "$install_dir" "$bin_dir"
  mv "$tmp_file" "$appimage_path"
  chmod +x "$appimage_path"
  ln -sf "$appimage_path" "$bin_dir/sunder-app"
  trap - EXIT
  echo "Sunder installed to $appimage_path"
  echo "A launcher symlink was created at $bin_dir/sunder-app"
  echo "Ensure $bin_dir is on PATH, then run: sunder-app"
  exit 0
fi

if [[ "$asset_name" == *.pkg ]]; then
  echo "Installing Sunder package..."
  sudo installer -pkg "$tmp_file" -target /
  exit 0
fi

download_dir="$HOME/Downloads"
mkdir -p "$download_dir"
target_path="$download_dir/$asset_name"
mv "$tmp_file" "$target_path"
trap - EXIT
echo "Downloaded Sunder to $target_path"
echo "Open the downloaded file to finish installation."
