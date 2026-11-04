#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["node"])' "$root/eng/toolchains.json")"
npm_version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["npm"])' "$root/eng/toolchains.json")"
case "$(uname -s)" in Linux) os=linux ;; Darwin) os=darwin ;; *) echo 'Use the setup action on Windows.' >&2; exit 1 ;; esac
case "$(uname -m)" in x86_64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) echo 'Unsupported Node architecture.' >&2; exit 1 ;; esac
name="node-v$version-$os-$arch"
destination="${SUNDER_TOOLCHAIN_ROOT:-$HOME/.local/share/sunder-toolchains}"
mkdir -p "$destination"
temporary="$(mktemp -d)"
trap 'rm -rf -- "$temporary"' EXIT
if [[ ! -x "$destination/$name/bin/node" ]]; then
  curl --fail --location --silent --show-error "https://nodejs.org/dist/v$version/$name.tar.xz" -o "$temporary/$name.tar.xz"
  curl --fail --location --silent --show-error "https://nodejs.org/dist/v$version/SHASUMS256.txt" -o "$temporary/SHASUMS256.txt"
  python3 - "$temporary" "$name.tar.xz" <<'PY'
import hashlib
from pathlib import Path
import sys
root, name = Path(sys.argv[1]), sys.argv[2]
rows = [line.split() for line in (root / 'SHASUMS256.txt').read_text().splitlines()]
expected = [digest for digest, file in rows if file == name]
if len(expected) != 1 or hashlib.sha256((root / name).read_bytes()).hexdigest() != expected[0]:
    raise SystemExit('Node distribution checksum differs from official release metadata')
PY
  tar -xJf "$temporary/$name.tar.xz" -C "$destination"
fi
export PATH="$destination/$name/bin:$PATH"
[[ "$(npm --version)" == "$npm_version" ]] || npm install --global "npm@$npm_version" --registry=https://registry.npmjs.org
test "$(node --version)" = "v$version"
test "$(npm --version)" = "$npm_version"
if [[ -n "${BASH_ENV:-}" ]]; then
  # PATH expands in the consuming shell, after this installer exits.
  # shellcheck disable=SC2016
  printf 'export PATH=%q:"$PATH"\nexport SUNDER_NODE24_EXECUTABLE=%q\n' "$destination/$name/bin" "$destination/$name/bin/node" >> "$BASH_ENV"
fi
printf 'Node toolchain: %s\n' "$destination/$name/bin"
