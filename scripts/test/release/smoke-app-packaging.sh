#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$root"
work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-package-smoke.XXXXXX")"
case "$(uname -s)" in
  Linux)
    version="$(dotnet msbuild languages/csharp/dotnet/host/Sunder.App/Sunder.App.csproj -getProperty:Version)"
    bash scripts/release/package-sunder.sh --version "$version" --runtime "$1" --output-root "$work" --strict-release
    git diff --exit-code -- ':(glob)languages/csharp/dotnet/**/packages.lock.json'
    test -f "$work/velopack/stable/$1/Sunder-app-$1-stable.AppImage"
    test -f "$work/velopack/stable/$1/Sunder-$version-app-$1-stable-full.nupkg"
    ;;
  Darwin)
    swiftc -typecheck scripts/release/macos/render-sunder-dmg-background.swift
    app="$work/Sunder.app"
    mkdir -p "$app/Contents/MacOS"
    python3 - "$app" <<'PY'
import pathlib, plistlib, sys
root = pathlib.Path(sys.argv[1]) / 'Contents'
(root / 'Info.plist').write_bytes(plistlib.dumps(dict(CFBundleExecutable='Sunder', CFBundleIdentifier='com.younics.sunder.fixture', CFBundleName='Sunder', CFBundlePackageType='APPL')))
(root / 'MacOS/Sunder').write_text('#!/usr/bin/env bash\nexit 0\n')
(root / 'MacOS/Sunder').chmod(0o755)
PY
    bash scripts/release/macos/create-sunder-dmg.sh --app "$app" --output "$work/Sunder.dmg"
    bash scripts/release/macos/verify-sunder-dmg.sh "$work/Sunder.dmg"
    ;;
  *) echo 'Unsupported packaging-smoke platform' >&2; exit 1 ;;
esac
