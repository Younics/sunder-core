#!/usr/bin/env bash
set -euo pipefail
dmg="$1"
runtime="${2:-}"
scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
hdiutil verify "$dmg" >/dev/null
if [[ -n "$runtime" ]]; then
  codesign --verify --strict "$dmg"
  xcrun stapler validate "$dmg"
  spctl --assess --type open --context context:primary-signature "$dmg"
fi
mount="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-dmg.XXXXXX")"
cleanup() {
  status=$?
  trap - EXIT
  if python3 "$scripts/dmg_image.py" detach --image "$dmg" --mount "$mount" --discard; then
    rmdir "$mount" || { [[ "$status" != 0 ]] || status=1; }
  else
    [[ "$status" != 0 ]] || status=1
  fi
  exit "$status"
}
trap cleanup EXIT
python3 "$scripts/dmg_image.py" attach --image "$dmg" --mount "$mount"
test -d "$mount/Sunder.app"
test -L "$mount/Applications" && test "$(readlink "$mount/Applications")" = /Applications
test -s "$mount/.background/Sunder.tiff" && test -s "$mount/.DS_Store"
shopt -s nullglob
visible=("$mount"/*); test "${#visible[@]}" = 2
for hidden in "$mount"/.[!.]*; do
  case "$(basename "$hidden")" in .background|.DS_Store) ;; *) exit 1 ;; esac
done
info="$(tiffutil -info "$mount/.background/Sunder.tiff")"
grep -q 'Image Width: 820 Image Length: 460' <<< "$info"
grep -q 'Image Width: 1640 Image Length: 920' <<< "$info"
if [[ -n "$runtime" ]]; then
  app="$mount/Sunder.app"
  codesign --verify --deep --strict "$app"
  codesign --display --verbose=4 "$app" 2>&1 | grep -Fx "TeamIdentifier=$APPLE_TEAM_ID"
  test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")" = com.younics.sunder
  executable="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")"
  architecture=x86_64; [[ "$runtime" != osx-arm64 ]] || architecture=arm64
  lipo -archs "$app/Contents/MacOS/$executable" | grep -w "$architecture"
  xcrun stapler validate "$app"
  spctl --assess --type execute "$app"
fi
python3 "$scripts/dmg_image.py" detach --image "$dmg" --mount "$mount"
