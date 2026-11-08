#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../release" && pwd)"
verifier="$script_dir/verify-release-tag.sh"
[[ -f "$verifier" ]] || { printf 'Release tag verifier not found: %s\n' "$verifier" >&2; exit 1; }

for required_command in git jq mktemp; do
  command -v "$required_command" >/dev/null 2>&1 \
    || { printf '%s is required to run the release tag fixture.\n' "$required_command" >&2; exit 127; }
done

temp_root="${TMPDIR:-/tmp}"
fixture_dir="$(mktemp -d "${temp_root%/}/sunder-release-tag-fixture.XXXXXX")"
trap 'cd "$script_dir"; rm -rf "$fixture_dir"' EXIT
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

# The caller may be a depth-one Actions checkout. Own the complete graph used
# by the verifier, including an unrelated source for the ancestry rejection.
unset GIT_DIR GIT_WORK_TREE GIT_COMMON_DIR GIT_INDEX_FILE GIT_OBJECT_DIRECTORY \
  GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_CONFIG_PARAMETERS GIT_CONFIG_COUNT
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
git init --quiet --initial-branch=main --template= "$fixture_dir/repository"
cd "$fixture_dir/repository"
git config user.name 'Sunder fixture'
git config user.email 'fixture@example.invalid'
git config commit.gpgsign false
git commit --quiet --allow-empty -m 'Retained ancestor'
ancestor="$(git rev-parse HEAD)"
git commit --quiet --allow-empty -m 'Workflow source'
git checkout --quiet --orphan unrelated
git commit --quiet --allow-empty -m 'Unrelated source'
unrelated="$(git rev-parse HEAD)"
git checkout --quiet main

export GH_REPO='Younics/sunder-core'
export GITHUB_REF='refs/tags/sdk/v1.2.3'
export GITHUB_SHA="$(git rev-parse HEAD)"
export MOCK_ANNOTATION_SHA='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
export MOCK_COMMIT_SHA="$GITHUB_SHA"

PATH="$mock_bin:$PATH" bash "$verifier" 'sdk/v1.2.3'
if GITHUB_REF=refs/tags/runtime/v1.2.3 PATH="$mock_bin:$PATH" bash "$verifier" 'runtime/v1.2.3' \
    > "$fixture_dir/retired-stream.log" 2>&1; then
  printf 'Release tag verifier accepted the retired language-runtime stream.\n' >&2
  exit 1
fi
grep -Fq 'Unexpected release tag' "$fixture_dir/retired-stream.log"

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

export GITHUB_REF=refs/heads/main GITHUB_REF_PROTECTED=true GITHUB_EVENT_NAME=push
export GITHUB_WORKFLOW_SHA="$GITHUB_SHA"
export GITHUB_WORKFLOW_REF="$GH_REPO/.github/workflows/sunder-sdk-release.yml@refs/heads/main"
PATH="$mock_bin:$PATH" bash "$verifier" 'app/v1.2.3' "$GITHUB_SHA"
MOCK_COMMIT_SHA="$ancestor" PATH="$mock_bin:$PATH" bash "$verifier" 'app/v1.2.3' "$ancestor"
if MOCK_COMMIT_SHA="$unrelated" PATH="$mock_bin:$PATH" bash "$verifier" 'app/v1.2.3' "$unrelated" \
    > "$fixture_dir/unrelated-source.log" 2>&1; then
  printf 'Release tag verifier accepted an unrelated artifact source.\n' >&2
  exit 1
fi
grep -Fq 'must be an ancestor' "$fixture_dir/unrelated-source.log"
for invalid in GITHUB_REF_PROTECTED=false GITHUB_EVENT_NAME=pull_request GITHUB_WORKFLOW_REF=foreign/workflow GITHUB_WORKFLOW_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb; do
  if env "$invalid" PATH="$mock_bin:$PATH" bash "$verifier" 'app/v1.2.3' "$GITHUB_SHA" > "$fixture_dir/main-guard.log" 2>&1; then
    printf 'Release tag verifier accepted invalid main publication context: %s\n' "$invalid" >&2
    exit 1
  fi
  grep -Fq 'protected-main workflow' "$fixture_dir/main-guard.log"
done
if PATH="$mock_bin:$PATH" bash "$verifier" 'app/v1.2.3' "$ancestor" > "$fixture_dir/retained-tag.log" 2>&1; then
  printf 'Release tag verifier accepted a retained source differing from its remote tag.\n' >&2
  exit 1
fi
grep -Fq 'not workflow commit/artifact source' "$fixture_dir/retained-tag.log"

printf 'Release tag fixture passed: historical tags and protected-main retained sources are enforced.\n'
