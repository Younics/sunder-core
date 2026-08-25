#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  printf 'Usage: build-core-candidate-packages.sh <empty-output-directory> [repository-root]\n' >&2
  exit 2
fi

output="$1"
repository_root="${2:-.}"
repository_root="$(cd "$repository_root" && pwd -P)"

for command_name in dotnet git jq node python3; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required to build Core candidate packages.\n' "$command_name" >&2; exit 127; }
done

mkdir -p "$output"
output="$(cd "$output" && pwd -P)"
[[ ! -L "$output" ]] || { printf 'Candidate package output must not be a symbolic link: %s\n' "$output" >&2; exit 1; }
shopt -s nullglob
existing=("$output"/*.nupkg)
[[ ${#existing[@]} -eq 0 ]] || { printf 'Candidate package output already contains NuGet packages: %s\n' "$output" >&2; exit 1; }

work_directory="$(mktemp -d "${TMPDIR:-/tmp}/sunder-core-candidate-packages.XXXXXX")"
packages_directory="$work_directory/packages"
cleanup() {
  rm -rf "$work_directory"
}
trap cleanup EXIT

metadata="$(node "$repository_root/scripts/release/check-sdk-version.mjs")"
version="$(jq -er '.version' <<< "$metadata")"
version_range="$(jq -er '.versionRange' <<< "$metadata")"
version_range_msbuild="${version_range/,/%2C}"
commit="$(git -C "$repository_root" rev-parse HEAD)"
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] || { printf 'Core HEAD is not a full commit SHA.\n' >&2; exit 1; }

dotnet restore "$repository_root/Sunder.Core.slnx" \
  --locked-mode \
  --no-http-cache \
  --artifacts-path "$work_directory/artifacts" \
  --packages "$packages_directory"

common_args=(
  -c Release
  --no-restore
  --artifacts-path "$work_directory/artifacts"
  -o "$output"
  -p:SunderDeveloperPackageVersion="$version"
  -p:SunderDeveloperPackageVersionRange="$version_range_msbuild"
  -p:RestorePackagesPath="$packages_directory"
  -p:ContinuousIntegrationBuild=true
  -p:GeneratePackageOnBuild=false
)
projects=(
  Sunder.Sdk
  Sunder.Sdk.Avalonia
  Sunder.Sdk.Stacks
  Sunder.Sdk.Worker
  Sunder.Package.Build
  Sunder.Package.Templates
)
for project in "${projects[@]}"; do
  dotnet pack \
    "$repository_root/languages/csharp/dotnet/packages/$project/$project.csproj" \
    "${common_args[@]}"
done

packages=("$output"/*.nupkg)
[[ ${#packages[@]} -eq 6 ]] || { printf 'Expected exactly six candidate NuGet packages, found %s.\n' "${#packages[@]}" >&2; exit 1; }
python3 "$repository_root/scripts/release/verify-nuget-package-set.py" "$version" "$output" "$commit"
