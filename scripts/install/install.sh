#!/usr/bin/env bash
set -euo pipefail

repository="Younics/sunder-core"
version="latest"
apple_team_id="${SUNDER_APPLE_TEAM_ID:-}"

usage() {
  cat <<'USAGE'
Usage: install.sh [--repo owner/name] [--version tag] [--apple-team-id TEAMID]

Examples:
  curl -fsSL https://raw.githubusercontent.com/Younics/sunder-core/main/scripts/install/install.sh | bash
  ./scripts/install/install.sh --version app/v0.1.0
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
    --apple-team-id)
      apple_team_id="${2:-}"
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
asset_name=""

case "$os:$arch" in
  Linux:x86_64|Linux:amd64)
    asset_name="Sunder-app-linux-x64-stable.AppImage"
    ;;
  Linux:aarch64|Linux:arm64)
    asset_name="Sunder-app-linux-arm64-stable.AppImage"
    ;;
  Darwin:x86_64)
    asset_name="Sunder-app-osx-x64-stable.dmg"
    ;;
  Darwin:arm64)
    asset_name="Sunder-app-osx-arm64-stable.dmg"
    ;;
  *)
    echo "Unsupported platform: $os $arch" >&2
    exit 1
    ;;
esac

if [[ "$os" == "Darwin" && -n "$apple_team_id" && ! "$apple_team_id" =~ ^[A-Z0-9]{10}$ ]]; then
  echo "--apple-team-id must be a 10-character Apple Team ID." >&2
  exit 1
fi

if [[ "$version" == "latest" ]]; then
  release_page_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' \
    -H "User-Agent: sunder-install-script" \
    "https://github.com/$repository/releases/latest")"
  encoded_tag="${release_page_url#*/releases/tag/}"
  encoded_tag="${encoded_tag%%\?*}"
  release_tag="$(printf '%s' "$encoded_tag" | sed -E 's/%2[Ff]/\//g')"
else
  if [[ "$version" =~ ^v?[0-9] ]]; then
    version="app/${version#app/}"
    [[ "$version" == app/v* ]] || version="app/v${version#app/}"
  fi
  release_tag="$version"
fi

if [[ "$release_tag" != app/v* ]]; then
  echo "Release '$release_tag' is not a Sunder App release. Specify an app/v* tag." >&2
  exit 1
fi

verify_sha256() {
  local path="$1"
  local expected="$2"
  local actual
  if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$path" | cut -d ' ' -f 1)"
  else
    actual="$(shasum -a 256 "$path" | cut -d ' ' -f 1)"
  fi
  actual="$(printf '%s' "$actual" | tr '[:upper:]' '[:lower:]')"
  expected="$(printf '%s' "$expected" | tr '[:upper:]' '[:lower:]')"
  if [[ "$actual" != "$expected" ]]; then
    echo "Release asset '$(basename "$path")' does not match its GitHub SHA-256 digest." >&2
    exit 1
  fi
}

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-install.XXXXXX")"
tmp_file="$tmp_dir/$asset_name"
checksums_file="$tmp_dir/SHA256SUMS"
mount_dir="$tmp_dir/mount"
mounted="false"
cleanup() {
  if [[ "$mounted" == "true" ]]; then
    hdiutil detach "$mount_dir" -quiet >/dev/null 2>&1 || true
  fi
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

encoded_release_tag="${release_tag//\//%2F}"
release_download_url="https://github.com/$repository/releases/download/$encoded_release_tag"
curl -fsSL -H "User-Agent: sunder-install-script" \
  "$release_download_url/SHA256SUMS" -o "$checksums_file"
asset_match_count=0
asset_sha256=""
while read -r checksum file_name extra; do
  if [[ -z "${extra:-}" && ( "$file_name" == "$asset_name" || "$file_name" == */"$asset_name" ) ]]; then
    asset_match_count=$((asset_match_count + 1))
    asset_sha256="$(printf '%s' "$checksum" | tr '[:upper:]' '[:lower:]')"
  fi
done < "$checksums_file"
if [[ "$asset_match_count" != "1" || ! "$asset_sha256" =~ ^[0-9a-f]{64}$ ]]; then
  echo "SHA256SUMS must contain exactly one valid digest for '$asset_name'." >&2
  exit 1
fi

asset_url="$release_download_url/$asset_name"
echo "Downloading $asset_name from $repository release $release_tag..."
curl -fL -H "User-Agent: sunder-install-script" "$asset_url" -o "$tmp_file"
verify_sha256 "$tmp_file" "$asset_sha256"

if [[ "$os" == "Linux" ]]; then
  install_dir="$HOME/.local/share/sunder"
  bin_dir="$HOME/.local/bin"
  appimage_path="$install_dir/Sunder.AppImage"
  mkdir -p "$install_dir" "$bin_dir"
  mv "$tmp_file" "$appimage_path"
  chmod +x "$appimage_path"
  ln -sf "$appimage_path" "$bin_dir/sunder-app"
  echo "Sunder installed to $appimage_path"
  echo "A launcher symlink was created at $bin_dir/sunder-app"
  echo "Ensure $bin_dir is on PATH, then run: sunder-app"
  exit 0
fi

dmg_signature="$(codesign --display --verbose=4 "$tmp_file" 2>&1)"
dmg_team_id="$(printf '%s\n' "$dmg_signature" | sed -n 's/^TeamIdentifier=//p' | sed -n '1p')"
if [[ ! "$dmg_team_id" =~ ^[A-Z0-9]{10}$ ]]; then
  echo "The Sunder DMG does not have a valid Developer ID team identifier." >&2
  exit 1
fi
if [[ -n "$apple_team_id" && "$dmg_team_id" != "$apple_team_id" ]]; then
  echo "The Sunder DMG is not signed by the expected Apple team." >&2
  exit 1
fi
hdiutil verify "$tmp_file" >/dev/null
codesign --verify --strict --verbose=2 "$tmp_file"
xcrun stapler validate "$tmp_file"
spctl --assess --type open --context context:primary-signature --verbose=4 "$tmp_file"

mkdir -p "$mount_dir"
hdiutil attach -readonly -nobrowse -mountpoint "$mount_dir" "$tmp_file" >/dev/null
mounted="true"
app_path="$mount_dir/Sunder.app"
applications_link="$mount_dir/Applications"
background_path="$mount_dir/.background/Sunder.tiff"
finder_layout="$mount_dir/.DS_Store"
if [[ ! -d "$app_path" || -L "$app_path" \
    || ! -L "$applications_link" || "$(readlink "$applications_link")" != "/Applications" \
    || ! -s "$background_path" || ! -s "$finder_layout" ]]; then
  echo "The Sunder DMG does not contain the expected app, Applications symlink, and branded Finder layout." >&2
  exit 1
fi

app_signature="$(codesign --display --verbose=4 "$app_path" 2>&1)"
app_team_id="$(printf '%s\n' "$app_signature" | sed -n 's/^TeamIdentifier=//p' | sed -n '1p')"
if [[ "$app_team_id" != "$dmg_team_id" ]]; then
  echo "Sunder.app and its DMG are not signed by the same Apple team." >&2
  exit 1
fi
codesign --verify --deep --strict --verbose=2 "$app_path"
xcrun stapler validate "$app_path"
spctl --assess --type execute --verbose=4 "$app_path"
echo "Verified Apple Developer Team ID: $app_team_id"
hdiutil detach "$mount_dir" -quiet
mounted="false"

download_dir="$HOME/Downloads"
mkdir -p "$download_dir"
target_path="$download_dir/$asset_name"
mv "$tmp_file" "$target_path"
echo "Downloaded Sunder to $target_path"
echo "Open the downloaded file to finish installation."
