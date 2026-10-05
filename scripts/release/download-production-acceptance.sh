#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 6 ]] || {
  printf 'Usage: download-production-acceptance.sh <release-id> <tag> <run-id> <run-attempt> <destination> <asset-inventory-output>\n' >&2
  exit 2
}
release_id="$1"
tag="$2"
run_id="$3"
run_attempt="$4"
destination="$5"
asset_inventory_output="$6"
prefix="production-acceptance-$release_id-$run_id-$run_attempt"
receipt_name="$prefix-receipt.json"

[[ "$release_id" =~ ^[1-9][0-9]*$ && "$run_id" =~ ^[1-9][0-9]*$ && "$run_attempt" =~ ^[1-9][0-9]*$ ]] \
  || { printf 'Release, run, and attempt coordinates must be positive decimal integers.\n' >&2; exit 2; }
[[ "$tag" =~ ^(app|cli|host|sdk)/v[A-Za-z0-9.-]+$ ]] \
  || { printf 'Unexpected production acceptance tag: %s\n' "$tag" >&2; exit 2; }
[[ ! -e "$destination" && ! -L "$destination" ]] \
  || { printf 'Production acceptance destination already exists: %s\n' "$destination" >&2; exit 1; }
destination_parent="$(dirname "$destination")"
[[ -d "$destination_parent" && ! -L "$destination_parent" ]] \
  || { printf 'Production acceptance destination parent must be a real directory: %s\n' "$destination_parent" >&2; exit 1; }
[[ ! -e "$asset_inventory_output" && ! -L "$asset_inventory_output" ]] \
  || { printf 'Production acceptance asset inventory output already exists.\n' >&2; exit 1; }
inventory_parent="$(dirname "$asset_inventory_output")"
[[ -d "$inventory_parent" && ! -L "$inventory_parent" ]] \
  || { printf 'Production acceptance asset inventory parent must be a real directory.\n' >&2; exit 1; }
: "${GH_REPO:?GH_REPO must identify the GitHub repository as owner/repo.}"
[[ "$GH_REPO" == 'Younics/sunder-core' ]] \
  || { printf 'Expected GitHub repository Younics/sunder-core, received %s.\n' "$GH_REPO" >&2; exit 2; }
for required_command in cmp gh jq mktemp sort; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to download production acceptance.\n' "$required_command" >&2; exit 127; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  printf 'sha256sum or shasum is required to download production acceptance.\n' >&2
  exit 127
fi

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

release_json="$(gh api -H 'Accept: application/vnd.github+json' \
  -H 'X-GitHub-Api-Version: 2022-11-28' \
  "repos/$GH_REPO/releases/$release_id")"
jq -e --argjson release_id "$release_id" --arg tag "$tag" '
  .id == $release_id
  and .tag_name == $tag
  and (.draft | type == "boolean")
  and (.assets | type == "array")
  and ([.assets[].name] | length == (unique | length))
  and ([.assets[].id] | length == (unique | length))
  and all(.assets[];
    (.id | type == "number" and floor == . and . > 0)
    and (.name | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
    and .state == "uploaded"
    and (.size | type == "number" and floor == . and . >= 0)
    and (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$")))
' <<< "$release_json" >/dev/null \
  || { printf 'Numeric GitHub release metadata is invalid or contains an unhashed asset.\n' >&2; exit 1; }

acceptance_pattern="^production-acceptance-${release_id}-[1-9][0-9]*-[1-9][0-9]*-(receipt[.]json|file-[0-9]{3})$"
invalid_acceptance_name="$(jq -r --arg pattern "$acceptance_pattern" '
  [.assets[].name | select(startswith("production-acceptance-") and (test($pattern) | not))][0] // empty
' <<< "$release_json")"
[[ -z "$invalid_acceptance_name" ]] \
  || { printf 'Numeric release contains a malformed production acceptance asset name: %s\n' "$invalid_acceptance_name" >&2; exit 1; }

work_root="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/sunder-production-acceptance-download.XXXXXX")"
trap 'rm -rf "$work_root"' EXIT
download_asset() {
  local asset_name="$1"
  local output="$2"
  local asset_count
  local asset_id
  local expected_digest
  local expected_size
  asset_count="$(jq --arg name "$asset_name" '[.assets[] | select(.name == $name)] | length' <<< "$release_json")"
  [[ "$asset_count" == 1 ]] \
    || { printf "Expected exactly one release asset '%s', found %s.\n" "$asset_name" "$asset_count" >&2; return 1; }
  asset_id="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .id' <<< "$release_json")"
  expected_size="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .size' <<< "$release_json")"
  expected_digest="$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .digest' <<< "$release_json")"
  gh api -H 'Accept: application/octet-stream' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/$GH_REPO/releases/assets/$asset_id" > "$output"
  [[ "$(wc -c < "$output" | tr -d '[:space:]')" == "$expected_size" \
      && "sha256:$(hash_file "$output")" == "$expected_digest" ]] \
    || { printf "Downloaded release asset '%s' differs from GitHub metadata.\n" "$asset_name" >&2; return 1; }
}

receipt="$work_root/$receipt_name"
download_asset "$receipt_name" "$receipt"
component="${tag%%/*}"
jq -e \
  --arg component "$component" \
  --arg prefix "$prefix" \
  --arg repository "$GH_REPO" \
  --argjson release_id "$release_id" \
  --arg tag "$tag" \
  --arg run_id "$run_id" \
  --arg run_attempt "$run_attempt" '
    type == "object"
    and keys == ["files","invocation","kind","release","schemaVersion"]
    and .schemaVersion == 1
    and .kind == "https://sunderapp.io/acceptance/production-release-assets/v1"
    and (.release | keys) == ["component","publicationRunAttempt","publicationRunId","releaseId","repository","sourceSha","tag"]
    and .release.repository == $repository
    and .release.component == $component
    and .release.releaseId == $release_id
    and .release.tag == $tag
    and (.release.sourceSha | test("^[0-9a-f]{40}$"))
    and (.release.publicationRunId | test("^[1-9][0-9]*$"))
    and (.release.publicationRunAttempt | test("^[1-9][0-9]*$"))
    and (.invocation | keys) == ["coordinatorSha256","repository","runAttempt","runId","workflowPath","workflowRef","workflowSha"]
    and .invocation.repository == $repository
    and .invocation.workflowPath == ".github/workflows/sunder-production-acceptance.yml"
    and .invocation.workflowRef == "refs/heads/main"
    and (.invocation.workflowSha | test("^[0-9a-f]{40}$"))
    and (.invocation.coordinatorSha256 | test("^[0-9a-f]{64}$"))
    and .invocation.runId == $run_id
    and .invocation.runAttempt == $run_attempt
    and (.files | type == "array" and length >= 3 and length <= 999)
    and ([.files[].path] | length == (unique | length))
    and ([.files[].assetName] | length == (unique | length))
    and all(.files[];
      type == "object" and keys == ["assetName","path","sha256","size"]
      and (.path | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
      and (.assetName | startswith($prefix + "-file-"))
      and (.sha256 | test("^[0-9a-f]{64}$"))
      and (.size | type == "number" and floor == . and . >= 0))
    and ([.files[] | select(.path == "production-acceptance.json")] | length == 1)
    and ([.files[] | select(.path == "production-acceptance-manifest.json")] | length == 1)
  ' "$receipt" >/dev/null \
  || { printf 'Durable production acceptance receipt is invalid or belongs to another attempt.\n' >&2; exit 1; }

# Asset names use fixed-width ordinals. Validate them separately because jq's
# numeric formatting should not define the release naming contract.
index=0
while IFS= read -r asset_name; do
  index=$((index + 1))
  expected_name="$(printf '%s-file-%03d' "$prefix" "$index")"
  [[ "$asset_name" == "$expected_name" ]] \
    || { printf 'Durable production acceptance receipt has a non-canonical asset name.\n' >&2; exit 1; }
done < <(jq -r '.files[].assetName' "$receipt")

selected_names_json="$(jq -c --arg receipt "$receipt_name" '[.files[].assetName, $receipt] | sort' "$receipt")"
jq -e --arg prefix "$prefix" --argjson expected "$selected_names_json" '
  ([.assets[].name | select(startswith($prefix + "-"))] | sort) == $expected
' <<< "$release_json" >/dev/null \
  || { printf 'Selected production acceptance attempt is partial or contains unexpected assets.\n' >&2; exit 1; }

mkdir "$destination"
while IFS=$'\t' read -r logical_name asset_name expected_sha expected_size; do
  target="$destination/$logical_name"
  download_asset "$asset_name" "$target"
  [[ "$(hash_file "$target")" == "$expected_sha" \
      && "$(wc -c < "$target" | tr -d '[:space:]')" == "$expected_size" ]] \
    || { printf "Production evidence '%s' differs from its durable receipt.\n" "$logical_name" >&2; exit 1; }
done < <(jq -r '.files[] | [.path, .assetName, .sha256, (.size | tostring)] | @tsv' "$receipt")

receipt_recheck="$work_root/receipt-recheck.json"
download_asset "$receipt_name" "$receipt_recheck"
cmp "$receipt" "$receipt_recheck" \
  || { printf 'Production acceptance receipt changed while its evidence was downloaded.\n' >&2; exit 1; }

jq -r --arg pattern "$acceptance_pattern" '.assets[].name | select(test($pattern))' <<< "$release_json" \
  | sort > "$asset_inventory_output"
[[ -s "$asset_inventory_output" ]] \
  || { printf 'Production acceptance release-asset inventory is empty.\n' >&2; exit 1; }

printf 'Downloaded receipt-bound production acceptance for release %s (%s), run %s attempt %s.\n' \
  "$release_id" "$tag" "$run_id" "$run_attempt"
