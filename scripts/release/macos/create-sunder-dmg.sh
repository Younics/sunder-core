#!/usr/bin/env bash
set -euo pipefail

app_path=""
output_path=""
volume_name="Sunder"

usage() {
  cat <<'USAGE'
Usage: create-sunder-dmg.sh --app <Sunder.app> --output <Sunder.dmg> [--volume-name Sunder]
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --app)
      app_path="${2:-}"
      shift 2
      ;;
    --output)
      output_path="${2:-}"
      shift 2
      ;;
    --volume-name)
      volume_name="${2:-}"
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

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Sunder DMG creation requires macOS." >&2
  exit 1
fi
if [[ -z "$app_path" || ! -d "$app_path" || -L "$app_path" ]]; then
  echo "--app must identify a real .app directory." >&2
  exit 2
fi
if [[ "$(basename "$app_path")" != "Sunder.app" || ! -f "$app_path/Contents/Info.plist" ]]; then
  echo "--app must identify a Sunder.app bundle containing Contents/Info.plist." >&2
  exit 2
fi
if [[ -z "$output_path" || "$(basename "$output_path")" != *.dmg ]]; then
  echo "--output must identify a .dmg file." >&2
  exit 2
fi
if [[ -z "$volume_name" || "$volume_name" == *:* || "$volume_name" == */* ]]; then
  echo "--volume-name must be a nonempty Finder-safe name." >&2
  exit 2
fi

for command_name in ditto hdiutil osascript python3 swift tiffutil; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "$command_name is required to create the Sunder DMG." >&2
    exit 127
  fi
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
renderer="$script_dir/render-sunder-dmg-background.swift"
image_tool="$script_dir/dmg_image.py"
[[ -f "$renderer" ]] || { echo "Missing DMG background renderer: $renderer" >&2; exit 1; }
[[ -f "$image_tool" ]] || { echo "Missing DMG attachment helper: $image_tool" >&2; exit 1; }

output_parent="$(dirname "$output_path")"
mkdir -p "$output_parent"
work_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-dmg-layout.XXXXXX")"
stage_root="$work_root/stage"
background_root="$work_root/background"
mount_root="$work_root/mount"
read_write_dmg="$work_root/Sunder-layout.dmg"
compressed_dmg="$work_root/Sunder.dmg"
cleanup() {
  local status=$?
  trap - EXIT HUP INT TERM
  # Query actual attachment state even when attach failed partway through.
  if python3 "$image_tool" detach --image "$read_write_dmg" --mount "$mount_root" --discard; then
    rm -rf "$work_root" || { [[ "$status" != 0 ]] || status=1; }
  else
    echo "Could not confirm DMG detachment; retaining staging files at $work_root" >&2
    [[ "$status" != 0 ]] || status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$stage_root/.background" "$background_root" "$mount_root"
ditto --rsrc --extattr --acl "$app_path" "$stage_root/Sunder.app"
ln -s /Applications "$stage_root/Applications"

swift "$renderer" "$background_root"
tiffutil -cathidpicheck \
  "$background_root/Sunder.png" \
  "$background_root/Sunder@2x.png" \
  -out "$stage_root/.background/Sunder.tiff" >/dev/null

hdiutil create \
  -quiet \
  -ov \
  -srcfolder "$stage_root" \
  -volname "$volume_name" \
  -fs HFS+ \
  -format UDRW \
  "$read_write_dmg"

python3 "$image_tool" attach --image "$read_write_dmg" --mount "$mount_root" --read-write

osascript - "$mount_root" <<'APPLESCRIPT'
on run arguments
  set mountPath to item 1 of arguments
  set mountedFolder to POSIX file mountPath as alias
  tell application "Finder"
    open mountedFolder
    set diskWindow to container window of mountedFolder
    set current view of diskWindow to icon view
    set toolbar visible of diskWindow to false
    set statusbar visible of diskWindow to false
    set pathbar visible of diskWindow to false
    set bounds of diskWindow to {120, 120, 940, 580}
    set viewOptions to icon view options of diskWindow
    set arrangement of viewOptions to not arranged
    set icon size of viewOptions to 112
    set text size of viewOptions to 13
    set label position of viewOptions to bottom
    set shows icon preview of viewOptions to false
    set background picture of viewOptions to file ".background:Sunder.tiff" of mountedFolder
    set position of item "Sunder.app" of mountedFolder to {220, 260}
    set position of item "Applications" of mountedFolder to {600, 260}
    update mountedFolder without registering applications
    delay 2
    close diskWindow
  end tell
end run
APPLESCRIPT

for _ in {1..20}; do
  [[ -s "$mount_root/.DS_Store" ]] && break
  sleep 0.25
done
[[ -s "$mount_root/.DS_Store" ]] || { echo "Finder did not persist the Sunder DMG layout." >&2; exit 1; }

rm -rf "$mount_root/.fseventsd" "$mount_root/.Spotlight-V100" "$mount_root/.Trashes"
sync
python3 "$image_tool" detach --image "$read_write_dmg" --mount "$mount_root"

hdiutil convert \
  -quiet \
  "$read_write_dmg" \
  -format UDZO \
  -imagekey zlib-level=9 \
  -o "$compressed_dmg"
hdiutil verify "$compressed_dmg" >/dev/null

rm -f "$output_path"
mv "$compressed_dmg" "$output_path"
echo "Created themed Sunder DMG: $output_path"
