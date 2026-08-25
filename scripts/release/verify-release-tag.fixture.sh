#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verifier="$script_dir/verify-release-tag.sh"
[[ -f "$verifier" ]] || { printf 'Release tag verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in git jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the release tag fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-release-tag-fixture.XXXXXX")"
trap 'rm -rf "$fixture_dir"' EXIT
mock_bin="$fixture_dir/mock-bin"
mkdir -p "$mock_bin"

cat > "$mock_bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == "api" ]] || exit 64
endpoint="${*: -1}"
case "$endpoint" in
  */git/ref/tags/*)
    jq -n --arg sha "$MOCK_ANNOTATION_SHA" '{object:{type:"tag",sha:$sha}}'
    ;;
  */git/tags/*)
    jq -n --arg sha "$MOCK_COMMIT_SHA" '{object:{type:"commit",sha:$sha}}'
    ;;
  *) exit 65 ;;
esac
EOF
chmod +x "$mock_bin/gh"

export GH_REPO='Younics/sunder-core'
export GITHUB_REF='refs/tags/sdk/v1.2.3'
export GITHUB_SHA="$(git rev-parse HEAD)"
export MOCK_ANNOTATION_SHA='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
export MOCK_COMMIT_SHA="$GITHUB_SHA"

PATH="$mock_bin:$PATH" bash "$verifier" 'sdk/v1.2.3'

if MOCK_COMMIT_SHA='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' PATH="$mock_bin:$PATH" \
  bash "$verifier" 'sdk/v1.2.3' > "$fixture_dir/wrong-commit.log" 2>&1; then
  printf 'Release tag verifier accepted a remote tag resolving to another commit.\n' >&2
  exit 1
fi
grep -Fq 'not workflow commit' "$fixture_dir/wrong-commit.log" \
  || { printf 'Release tag verifier did not report the remote commit mismatch.\n' >&2; exit 1; }

if GH_REPO='Other/sunder-core' PATH="$mock_bin:$PATH" \
  bash "$verifier" 'sdk/v1.2.3' > "$fixture_dir/wrong-repository.log" 2>&1; then
  printf 'Release tag verifier accepted another GitHub repository.\n' >&2
  exit 1
fi

printf 'Release tag fixture passed: repository identity and annotated remote tag resolution are enforced.\n'
