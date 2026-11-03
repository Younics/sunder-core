#!/usr/bin/env bash
# The tag workflow owns ordering and credentials; each registry step checks exact retained bytes.
set -euo pipefail
action="${1:?Usage: publish-sdk.sh <npm|nuget|promote|verify> <version> <directory>}"
version="${2:?Version is required}"
directory="${3:?Artifact directory is required}"
case "$action" in npm|nuget|promote|verify) ;; *) exit 2 ;; esac
helpers="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bash "$helpers/verify-release-tag.sh" "sdk/v$version"
python3 -B "$helpers/github_release.py" verify "sdk/v$version" --release-id "${RELEASE_ID:?}" --directory "$directory"
node_range="$(python3 -c 'import sys; v=sys.argv[1].split("."); print(f">={v[0]}.{v[1]}.0 <{v[0]}.{int(v[1])+1}.0")' "$version")"
registry=https://registry.npmjs.org
channel=latest
[[ "$version" != *-* ]] || channel=next
retry() {
  local attempt
  for attempt in {1..30}; do
    if "$@"; then return 0; fi
    [[ "$attempt" == 30 ]] || sleep 10
  done
  return 1
}
verify_npm() {
  local tag="$1" policy="$2"
  shift 2
  node "$helpers/verify-npm-release.mjs" "$version" "$node_range" "$tag" "$directory" \
    "$GITHUB_REPOSITORY" .github/workflows/sunder-sdk-release.yml "$GITHUB_SHA" "$policy" "$@"
}
if [[ "$action" == nuget ]]; then
  : "${NUGET_API_KEY:?NuGet trusted-publishing credential is required}"
  bash "$helpers/verify-nuget-source.sh"
  python3 "$helpers/verify-nuget-package-set.py" "$version" "$directory" "$GITHUB_SHA"
  remote="$(mktemp -d)"
  trap 'rm -rf "$remote"' EXIT
  download_nuget() {
    curl --fail --silent --show-error --proto '=https' --tlsv1.2 --max-redirs 0 --max-time 120 \
      "https://api.nuget.org/v3-flatcontainer/${package_id,,}/${version,,}/${package_id,,}.${version,,}.nupkg" \
      -o "$remote/package.nupkg"
  }
  for package_id in Sunder.Sdk Sunder.Sdk.Avalonia Sunder.Sdk.Stacks Sunder.Sdk.Worker Sunder.Package.Build Sunder.Package.Templates; do
    package="$directory/$package_id.$version.nupkg"
    if python3 -B "$helpers/sdk_release.py" package "$version" "$package_id"; then
      retry download_nuget
    else
      lookup_status=$?
      [[ "$lookup_status" == 4 ]] || exit "$lookup_status"
      # Reconcile duplicate/lost push acknowledgements by signed canonical readback.
      dotnet nuget push "$package" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate || true
      retry download_nuget
    fi
    bash "$helpers/verify-nuget-package.sh" "$package" "$remote/package.nupkg"
  done
elif [[ "$action" == npm ]]; then
  : "${NODE_AUTH_TOKEN:?Set the NPM_TOKEN secret in sunder-core-release or repository Actions secrets for npm publication}"
  package_ids=("@sunder/sdk" "@sunder/package-tool" "create-sunder-package")
  archives=("sunder-sdk" "sunder-package-tool" "create-sunder-package")
  for index in "${!package_ids[@]}"; do
    package_id="${package_ids[$index]}"
    if python3 -B "$helpers/sdk_release.py" package "$version" "$package_id"; then
      : # Verify the existing immutable version below, without publishing it again.
    else
      lookup_status=$?
      [[ "$lookup_status" == 4 ]] || exit "$lookup_status"
      npm publish "$directory/${archives[$index]}-$version.tgz" --ignore-scripts --access public \
        --tag "accept-v$version" --registry="$registry" --provenance || true
    fi
    retry verify_npm "accept-v$version" publication "$package_id"
  done
  verify_npm "accept-v$version" publication
elif [[ "$action" == promote ]]; then
  : "${NODE_AUTH_TOKEN:?Set the NPM_TOKEN secret in sunder-core-release or repository Actions secrets for npm channel promotion}"
  verify_npm "accept-v$version" publication
  python3 -B "$helpers/sdk_release.py" channels "$version"
  for package_id in "@sunder/sdk" "@sunder/package-tool" "create-sunder-package"; do
    npm dist-tag add "$package_id@$version" "$channel" --registry="$registry"
    retry verify_npm "$channel" promotion "$package_id"
  done
  verify_npm "$channel" promotion
else
  verify_npm "$channel" promotion
fi
