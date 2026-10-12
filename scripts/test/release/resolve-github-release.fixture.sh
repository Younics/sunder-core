#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
resolver="$script_dir/resolve-github-release.sh"
[[ -f "$resolver" ]] || { printf 'GitHub release resolver not found.\n' >&2; exit 1; }
for required_command in jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the release resolver fixture.\n' "$required_command" >&2; exit 127; }
done

fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/sunder-release-resolver-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"

jq -n '[range(0; 100) | {id:(1000 + .),tag_name:("old/v" + (. | tostring)),draft:false}]' \
  > "$fixture_dir/page-1.json"
jq -n '[{id:4242,tag_name:"sdk/v1.2.3",draft:true,prerelease:false,assets:[]}]' \
  > "$fixture_dir/page-2.json"
jq -n '[
  {id:4242,tag_name:"sdk/v1.2.3",draft:true,prerelease:false,assets:[]},
  {id:4243,tag_name:"sdk/v1.2.3",draft:true,prerelease:false,assets:[]}
]' > "$fixture_dir/page-2-duplicate.json"

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == api ]] || exit 60
endpoint="${*: -1}"
printf '%s\n' "$endpoint" >> "$MOCK_GH_LOG"
case "$endpoint" in
  */releases/tags/*) exit 44 ;;
  *'releases?per_page=100&page=1') command cat "$MOCK_PAGE_1" ;;
  *'releases?per_page=100&page=2')
    if [[ "${MOCK_DUPLICATE:-false}" == true ]]; then
      command cat "$MOCK_PAGE_2_DUPLICATE"
    elif [[ "${MOCK_EMPTY:-false}" == true ]]; then
      printf '[]\n'
    else
      command cat "$MOCK_PAGE_2"
    fi
    ;;
  *) exit 61 ;;
esac
EOF
chmod +x "$mock_bin/gh"

export GH_REPO='Younics/sunder-core'
export MOCK_GH_LOG="$fixture_dir/gh.log"
export MOCK_PAGE_1="$fixture_dir/page-1.json"
export MOCK_PAGE_2="$fixture_dir/page-2.json"
export MOCK_PAGE_2_DUPLICATE="$fixture_dir/page-2-duplicate.json"

resolved="$(PATH="$mock_bin:$PATH" bash "$resolver" 'sdk/v1.2.3')"
jq -e '.id == 4242 and .tag_name == "sdk/v1.2.3" and .draft == true' <<< "$resolved" >/dev/null
if grep -q '/releases/tags/' "$MOCK_GH_LOG"; then
  printf 'Resolver used the published-only release-by-tag endpoint.\n' >&2
  exit 1
fi

if MOCK_DUPLICATE=true PATH="$mock_bin:$PATH" bash "$resolver" 'sdk/v1.2.3' \
  > "$fixture_dir/duplicate.log" 2>&1; then
  printf 'Resolver accepted ambiguous releases for one tag.\n' >&2
  exit 1
fi
grep -Fq 'refusing ambiguous draft mutation' "$fixture_dir/duplicate.log" \
  || { printf 'Resolver did not report ambiguous draft state.\n' >&2; exit 1; }

set +e
MOCK_EMPTY=true PATH="$mock_bin:$PATH" bash "$resolver" 'sdk/v1.2.3' \
  > "$fixture_dir/missing.log" 2>&1
missing_status=$?
set -e
[[ "$missing_status" == 4 ]] \
  || { printf 'Missing release must return status 4, found %s.\n' "$missing_status" >&2; exit 1; }

printf 'GitHub release resolver fixture passed: authenticated pagination sees drafts, tag lookup is unused, and ambiguity fails closed.\n'
