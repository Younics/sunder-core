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
mac_bundle_id="com.younics.sunder"
mac_sign_app_identity=""
mac_notary_profile=""
mac_keychain=""
mac_team_id=""

download_velopack_history() {
  local repository_url="$1"
  local velopack_channel="$2"
  local destination="$3"
  local access_token="$4"
  local include_prerelease="$5"

  if ! command -v jq >/dev/null 2>&1; then
    echo "jq is required to download paginated Velopack release history." >&2
    return 1
  fi
  if [[ ! "$repository_url" =~ ^https://github\.com/([^/]+)/([^/?#]+)(/)?$ ]]; then
    echo "GitHub repository URL must use https://github.com/owner/repository." >&2
    return 1
  fi

  local owner="${BASH_REMATCH[1]}"
  local repository="${BASH_REMATCH[2]%.git}"
  local manifest_name="releases.$velopack_channel.json"
  local page=1
  local page_json
  local page_count
  local release_assets=""
  local best_release=""
  local page_best
  local api_headers=(
    -H "Accept: application/vnd.github+json"
    -H "User-Agent: sunder-release-packager"
    -H "X-GitHub-Api-Version: 2022-11-28"
  )
  if [[ -n "$access_token" ]]; then
    api_headers+=(-H "Authorization: Bearer $access_token")
  fi

  while true; do
    if ! page_json="$(curl -fsSL "${api_headers[@]}" \
      "https://api.github.com/repos/$owner/$repository/releases?per_page=100&page=$page")"; then
      return 1
    fi
    if ! page_best="$(jq -c \
      --arg manifest "$manifest_name" \
      --arg include_prerelease "$include_prerelease" \
      'def semver_key:
         (.tag_name
          | capture("^app/v(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$")) as $version
         | [($version.major | tonumber),
            ($version.minor | tonumber),
            ($version.patch | tonumber),
            (if ($version.pre // "") == "" then 1 else 0 end),
            (($version.pre // "") | split(".")
              | map(if test("^[0-9]+$") then [0, tonumber] else [1, .] end))];
       [.[]
        | select(.draft == false)
        | select(($include_prerelease == "true") or (.prerelease == false))
        | select([.assets[]? | select(.name == $manifest)] | length == 1)
        | select(.tag_name | test("^app/v[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$"))
        | . + {_sunderSemverKey: semver_key}
      ] | sort_by(._sunderSemverKey) | last // empty' <<< "$page_json")"; then
      return 1
    fi
    if [[ -n "$page_best" ]]; then
      if [[ -z "$best_release" ]]; then
        best_release="$page_best"
      elif ! best_release="$(jq -nc \
        --argjson current "$best_release" \
        --argjson candidate "$page_best" \
        '[$current, $candidate] | max_by(._sunderSemverKey)')"; then
        return 1
      fi
    fi
    if ! page_count="$(jq 'length' <<< "$page_json")" || [[ ! "$page_count" =~ ^[0-9]+$ ]]; then
      return 1
    fi
    if [[ "$page_count" -lt 100 ]]; then
      break
    fi
    page=$((page + 1))
  done
  if [[ -z "$best_release" ]]; then
    return 0
  fi
  if ! release_assets="$(jq -c '.assets' <<< "$best_release")"; then
    return 1
  fi

  local history_directory="$destination/.history-$$-$RANDOM"
  mkdir -p "$history_directory"
  if ! (
    set -euo pipefail

    manifest_count="$(jq --arg name "$manifest_name" \
      '[.[] | select(.name == $name)] | length' <<< "$release_assets")" || exit 1
    [[ "$manifest_count" == "1" ]] || exit 1
    manifest_url="$(jq -r --arg name "$manifest_name" \
      '.[] | select(.name == $name) | .browser_download_url' <<< "$release_assets")" || exit 1
    manifest_path="$history_directory/$manifest_name"
    curl -fsSL "${api_headers[@]}" "$manifest_url" -o "$manifest_path" || exit 1
    jq -e '.Assets | type == "array" and length > 0' "$manifest_path" >/dev/null || exit 1

    while IFS=$'\t' read -r package_name expected_sha256; do
      [[ "$package_name" =~ ^[A-Za-z0-9._+-]+\.nupkg$ ]] || exit 1
      [[ "$expected_sha256" =~ ^[0-9a-fA-F]{64}$ ]] || exit 1
      package_count="$(jq --arg name "$package_name" \
        '[.[] | select(.name == $name)] | length' <<< "$release_assets")" || exit 1
      [[ "$package_count" == "1" ]] || exit 1
      package_url="$(jq -r --arg name "$package_name" \
        '.[] | select(.name == $name) | .browser_download_url' <<< "$release_assets")" || exit 1
      package_path="$history_directory/$package_name"
      curl -fsSL "${api_headers[@]}" "$package_url" -o "$package_path" || exit 1
      if command -v sha256sum >/dev/null 2>&1; then
        actual_sha256="$(sha256sum "$package_path" | cut -d ' ' -f 1)" || exit 1
      else
        actual_sha256="$(shasum -a 256 "$package_path" | cut -d ' ' -f 1)" || exit 1
      fi
      actual_sha256="$(printf '%s' "$actual_sha256" | tr '[:lower:]' '[:upper:]')"
      expected_sha256="$(printf '%s' "$expected_sha256" | tr '[:lower:]' '[:upper:]')"
      [[ "$actual_sha256" == "$expected_sha256" ]] || exit 1
    done < <(jq -r '.Assets[] | [.FileName, .SHA256] | @tsv' "$manifest_path")

    mv "$history_directory"/* "$destination"/ || exit 1
  ); then
    rm -rf "$history_directory"
    return 1
  fi
  rm -rf "$history_directory"
}

usage() {
  cat <<'USAGE'
Usage: package-sunder.sh --version <semver> [--runtime <linux-x64|linux-arm64|osx-x64|osx-arm64>]
                         [--channel stable|beta|nightly] [--configuration <configuration>]
                         [--output-root <directory>] [--github-repository-url <url>]
                         [--github-token <token>] [--include-prerelease-updates]
                         [--mac-bundle-id <id>]
                         [--mac-sign-app-identity <identity>] [--mac-notary-profile <profile>]
                         [--mac-keychain <path>] [--mac-team-id <team-id>]

Examples:
  ./scripts/release/package-sunder.sh --version 0.1.0 --runtime linux-x64
  ./scripts/release/package-sunder.sh --version 0.1.0 --runtime osx-arm64 \
    --mac-sign-app-identity "Developer ID Application: Example" \
    --mac-notary-profile sunder-notary --mac-keychain signing.keychain-db \
    --mac-team-id ABCDE12345
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
    --mac-bundle-id)
      mac_bundle_id="${2:-}"
      shift 2
      ;;
    --mac-sign-app-identity)
      mac_sign_app_identity="${2:-}"
      shift 2
      ;;
    --mac-notary-profile)
      mac_notary_profile="${2:-}"
      shift 2
      ;;
    --mac-keychain)
      mac_keychain="${2:-}"
      shift 2
      ;;
    --mac-team-id)
      mac_team_id="${2:-}"
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

if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
  echo "Version '$version' must be strict SemVer without build metadata." >&2
  exit 2
fi

if [[ "$version" == *-* ]]; then
  prerelease="${version#*-}"
  IFS='.' read -ra identifiers <<< "$prerelease"
  for identifier in "${identifiers[@]}"; do
    if [[ "$identifier" =~ ^[0-9]+$ && "$identifier" == 0* && "$identifier" != "0" ]]; then
      echo "Numeric SemVer prerelease identifiers must not contain leading zeroes." >&2
      exit 2
    fi
  done
fi

if [[ "$channel" != "stable" && "$channel" != "beta" && "$channel" != "nightly" ]]; then
  echo "Channel must be stable, beta, or nightly." >&2
  exit 2
fi

case "$runtime" in
  linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *)
    echo "Runtime must be linux-x64, linux-arm64, osx-x64, or osx-arm64." >&2
    exit 2
    ;;
esac

if [[ -z "$mac_bundle_id" ]]; then
  echo "macOS bundle id must not be empty." >&2
  exit 2
fi

if [[ "$runtime" == osx-* ]]; then
  mac_signing_arg_count=0
  [[ -n "$mac_sign_app_identity" ]] && mac_signing_arg_count=$((mac_signing_arg_count + 1))
  [[ -n "$mac_notary_profile" ]] && mac_signing_arg_count=$((mac_signing_arg_count + 1))
  [[ -n "$mac_keychain" ]] && mac_signing_arg_count=$((mac_signing_arg_count + 1))
  [[ -n "$mac_team_id" ]] && mac_signing_arg_count=$((mac_signing_arg_count + 1))

  if [[ "$mac_signing_arg_count" -ne 4 || ! "$mac_team_id" =~ ^[A-Z0-9]{10}$ ]]; then
    echo "macOS packaging requires an app identity, notary profile, keychain, and 10-character Team ID." >&2
    exit 2
  fi
fi

if ! command -v vpk >/dev/null 2>&1; then
  echo "The Velopack CLI 'vpk' was not found. Install it with: dotnet tool install --global vpk --version 0.0.1298" >&2
  exit 127
fi
if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required for Sunder release packaging." >&2
  exit 127
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
project_path="$repo_root/languages/csharp/dotnet/host/Sunder.App/Sunder.App.csproj"
if [[ "$output_root" = /* ]]; then
  artifact_root="$output_root"
else
  artifact_root="$repo_root/$output_root"
fi

create_macos_icon() {
  local source_png="$repo_root/languages/csharp/dotnet/host/Sunder.App/Assets/Images/logo.png"
  local icon_root="$artifact_root/icons"
  local iconset="$icon_root/Sunder.iconset"
  local icns="$icon_root/Sunder.icns"

  if ! command -v sips >/dev/null 2>&1 || ! command -v iconutil >/dev/null 2>&1; then
    echo "macOS packaging requires sips and iconutil to create an .icns icon." >&2
    exit 1
  fi
  if [[ ! -f "$source_png" ]]; then
    echo "macOS packaging icon source is missing: $source_png" >&2
    exit 1
  fi

  rm -rf "$iconset"
  rm -f "$icns"
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

create_macos_plist() {
  local plist_dir="$artifact_root/macos"
  local plist="$plist_dir/Sunder.Info.plist"
  local bundle_version="${version%%-*}"
  bundle_version="${bundle_version%%+*}"

  mkdir -p "$plist_dir"
  cat > "$plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>Sunder</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeExtensions</key>
      <array>
        <string>sunderstack</string>
      </array>
      <key>CFBundleTypeMIMETypes</key>
      <array>
        <string>application/vnd.sunder.stack</string>
      </array>
      <key>CFBundleTypeName</key>
      <string>Sunder Stack</string>
      <key>CFBundleTypeRole</key>
      <string>Viewer</string>
      <key>LSHandlerRank</key>
      <string>Owner</string>
      <key>LSItemContentTypes</key>
      <array>
        <string>${mac_bundle_id}.stack</string>
      </array>
    </dict>
  </array>
  <key>CFBundleExecutable</key>
  <string>Sunder.App</string>
  <key>CFBundleIconFile</key>
  <string>Sunder.icns</string>
  <key>CFBundleIdentifier</key>
  <string>${mac_bundle_id}</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Sunder</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${bundle_version}</string>
  <key>CFBundleSupportedPlatforms</key>
  <array>
    <string>MacOSX</string>
  </array>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key>
      <string>${mac_bundle_id}.url</string>
      <key>CFBundleURLSchemes</key>
      <array>
        <string>sunder</string>
      </array>
    </dict>
  </array>
  <key>CFBundleVersion</key>
  <string>${bundle_version}</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>UTExportedTypeDeclarations</key>
  <array>
    <dict>
      <key>UTTypeConformsTo</key>
      <array>
        <string>public.data</string>
      </array>
      <key>UTTypeDescription</key>
      <string>Sunder Stack</string>
      <key>UTTypeIdentifier</key>
      <string>${mac_bundle_id}.stack</string>
      <key>UTTypeIconFile</key>
      <string>Sunder.icns</string>
      <key>UTTypeTagSpecification</key>
      <dict>
        <key>public.filename-extension</key>
        <array>
          <string>sunderstack</string>
        </array>
        <key>public.mime-type</key>
        <string>application/vnd.sunder.stack</string>
      </dict>
    </dict>
  </array>
</dict>
</plist>
PLIST

  printf '%s\n' "$plist"
}

publish_dir="$artifact_root/publish/sunder/$runtime"
release_dir="$artifact_root/velopack/$channel/$runtime"
velopack_channel="app-$runtime-$channel"
main_exe="Sunder.App"

dotnet restore "$project_path" -r "$runtime" -p:Configuration="$configuration"

rm -rf "$publish_dir" "$release_dir"
mkdir -p "$publish_dir" "$release_dir"

if [[ -n "$github_repository_url" ]]; then
  effective_github_token="${github_token:-${GITHUB_TOKEN:-}}"
  if ! download_velopack_history \
    "$github_repository_url" \
    "$velopack_channel" \
    "$release_dir" \
    "$effective_github_token" \
    "$include_prerelease_updates"; then
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
  -p:IncludeSourceRevisionInInformationalVersion=false \
  -p:ContinuousIntegrationBuild=true \
  -p:PublishSingleFile=false \
  -o "$publish_dir"

published_settings="$publish_dir/appsettings.json"
published_settings_temp="$published_settings.$$.tmp"
jq \
  --arg repository_url "$github_repository_url" \
  --argjson include_prerelease "$include_prerelease_updates" \
  '.Updates.IncludePrerelease = $include_prerelease
   | if $repository_url == "" then . else .Updates.GitHubRepositoryUrl = $repository_url end' \
  "$published_settings" > "$published_settings_temp"
mv "$published_settings_temp" "$published_settings"

required_bundled_files=(
  "$publish_dir/RuntimeHost/Sunder.Host.Supervisor"
  "$publish_dir/RuntimeHost/RuntimeHost/Sunder.Runtime.Host"
  "$publish_dir/Cli/sunder"
)
for required_bundled_file in "${required_bundled_files[@]}"; do
  [[ -f "$required_bundled_file" ]] || {
    echo "The App publish is missing bundled payload '$required_bundled_file'." >&2
    exit 1
  }
done

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
  linux-*)
    pack_args+=(--icon "$repo_root/languages/csharp/dotnet/host/Sunder.App/Assets/Images/logo.png" --categories Utility)
    ;;
  osx-*)
    macos_icon="$(create_macos_icon)"
    macos_plist="$(create_macos_plist)"
    pack_args+=(
      --icon "$macos_icon"
      --plist "$macos_plist"
      --noInst
      --signAppIdentity "$mac_sign_app_identity"
      --notaryProfile "$mac_notary_profile"
      --keychain "$mac_keychain"
    )
    ;;
esac

vpk "${pack_args[@]}"

rm -f "$release_dir/releases.$velopack_channel.json" "$release_dir"/RELEASES-*
shopt -s nullglob
for historical_package in "$release_dir"/*.nupkg; do
  if [[ "$(basename "$historical_package")" != Sunder-"$version"-"$velopack_channel"-*.nupkg ]]; then
    rm -f "$historical_package"
  fi
done

case "$runtime" in
  linux-*)
    expected_appimage="$release_dir/Sunder-$velopack_channel.AppImage"
    shopt -s nullglob
    appimages=("$release_dir"/*.AppImage)
    if [[ ${#appimages[@]} -ne 1 || "${appimages[0]}" != "$expected_appimage" ]]; then
      echo "Expected exactly one Linux AppImage named '$(basename "$expected_appimage")'." >&2
      exit 1
    fi
    ;;
  osx-*)
    portable_name="Sunder-$velopack_channel-Portable.zip"
    portable_zip="$release_dir/$portable_name"
    assets_manifest="$release_dir/assets.$velopack_channel.json"
    dmg_name="Sunder-$velopack_channel.dmg"
    dmg_path="$release_dir/$dmg_name"
    dmg_builder="$script_dir/macos/create-sunder-dmg.sh"
    [[ -f "$portable_zip" ]] || { echo "Missing exact Velopack portable app output: $portable_zip" >&2; exit 1; }
    [[ -f "$assets_manifest" ]] || { echo "Missing Velopack build asset manifest: $assets_manifest" >&2; exit 1; }
    [[ -x "$dmg_builder" ]] || { echo "Missing executable Sunder DMG builder: $dmg_builder" >&2; exit 1; }

    dmg_work_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-dmg.XXXXXX")"
    cleanup_dmg_work() {
      rm -rf "$dmg_work_root"
    }
    trap cleanup_dmg_work EXIT

    ditto -x -k "$portable_zip" "$dmg_work_root"
    app_path="$dmg_work_root/Sunder.app"
    [[ -d "$app_path" && ! -L "$app_path" ]] \
      || { echo "The Velopack portable output does not contain a real Sunder.app directory." >&2; exit 1; }
    app_bundle_id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app_path/Contents/Info.plist")"
    if [[ "$app_bundle_id" != "$mac_bundle_id" ]]; then
      echo "The Velopack-generated Sunder.app has unexpected bundle id '$app_bundle_id'." >&2
      exit 1
    fi
    codesign --verify --deep --strict --verbose=2 "$app_path"
    app_signature="$(codesign --display --verbose=4 "$app_path" 2>&1)"
    app_team_id="$(printf '%s\n' "$app_signature" | sed -n 's/^TeamIdentifier=//p' | sed -n '1p')"
    if [[ "$app_team_id" != "$mac_team_id" ]]; then
      echo "The Velopack-generated Sunder.app is not signed by the expected Apple team." >&2
      exit 1
    fi
    xcrun stapler validate "$app_path"
    spctl --assess --type execute --verbose=4 "$app_path"

    rm -f "$dmg_path"
    "$dmg_builder" --app "$app_path" --output "$dmg_path"

    codesign --force --timestamp --sign "$mac_sign_app_identity" --keychain "$mac_keychain" "$dmg_path"
    codesign --verify --strict --verbose=2 "$dmg_path"
    dmg_signature="$(codesign --display --verbose=4 "$dmg_path" 2>&1)"
    dmg_team_id="$(printf '%s\n' "$dmg_signature" | sed -n 's/^TeamIdentifier=//p' | sed -n '1p')"
    if [[ "$dmg_team_id" != "$mac_team_id" ]]; then
      echo "The Sunder DMG is not signed by the expected Apple team." >&2
      exit 1
    fi
    xcrun notarytool submit "$dmg_path" \
      --keychain-profile "$mac_notary_profile" \
      --keychain "$mac_keychain" \
      --wait
    xcrun stapler staple "$dmg_path"
    xcrun stapler validate "$dmg_path"
    spctl --assess --type open --context context:primary-signature --verbose=4 "$dmg_path"
    hdiutil verify "$dmg_path" >/dev/null

    portable_asset_count="$(jq \
      --arg portable "$portable_name" \
      '[.[] | select(.RelativeFileName == $portable and .Type == "Portable")] | length' \
      "$assets_manifest")"
    if [[ "$portable_asset_count" != "1" ]]; then
      echo "Velopack upload manifest must contain exactly one '$portable_name' Portable entry." >&2
      exit 1
    fi
    sanitized_assets="$assets_manifest.$$.tmp"
    jq \
      --arg portable "$portable_name" \
      'map(select(.RelativeFileName != $portable or .Type != "Portable"))
       | if all(.[]; (.Type == "Full" or .Type == "Delta")
                       and (.RelativeFileName | endswith(".nupkg")))
         then .
         else error("unexpected non-update asset remains after removing the Portable entry")
         end' \
      "$assets_manifest" > "$sanitized_assets"
    full_asset_name="Sunder-$version-$velopack_channel-full.nupkg"
    full_asset_count="$(jq \
      --arg full "$full_asset_name" \
      '[.[] | select(.RelativeFileName == $full and .Type == "Full")] | length' \
      "$sanitized_assets")"
    if [[ "$full_asset_count" != "1" ]]; then
      rm -f "$sanitized_assets"
      echo "Velopack upload manifest must retain exactly one current Full package '$full_asset_name'." >&2
      exit 1
    fi
    while IFS= read -r update_asset; do
      if [[ "$(basename "$update_asset")" != "$update_asset" || ! -f "$release_dir/$update_asset" ]]; then
        rm -f "$sanitized_assets"
        echo "Velopack upload manifest references missing or unsafe update asset '$update_asset'." >&2
        exit 1
      fi
    done < <(jq -r '.[].RelativeFileName' "$sanitized_assets")
    mv "$sanitized_assets" "$assets_manifest"
    rm -f "$portable_zip"

    shopt -s nullglob
    portable_files=("$release_dir"/*-Portable.zip)
    pkg_files=("$release_dir"/*.pkg)
    dmg_files=("$release_dir"/*.dmg)
    if [[ ${#portable_files[@]} -ne 0 || ${#pkg_files[@]} -ne 0 \
        || ${#dmg_files[@]} -ne 1 || "${dmg_files[0]}" != "$dmg_path" ]]; then
      echo "The macOS release must contain exactly one DMG and no portable ZIP or PKG." >&2
      exit 1
    fi
    ;;
esac

echo "Sunder Velopack release created: $release_dir ($velopack_channel)"
