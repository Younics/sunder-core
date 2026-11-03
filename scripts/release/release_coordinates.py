#!/usr/bin/env python3
"""Shared, read-only GitHub/source checks for Core release handoffs."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote

REPOSITORY = "Younics/sunder-core"
SDK_WORKFLOW = ".github/workflows/sunder-sdk-release.yml"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def closed(value, fields):
    require(isinstance(value, dict) and set(value) == set(fields.split()), "Unexpected closed receipt members")


def sha(value, length=64):
    require(isinstance(value, str) and re.fullmatch(f"[0-9a-f]{{{length}}}", value), "Invalid exact digest/commit")
    return value


def number(value):
    require(type(value) in (str, int) and re.fullmatch(r"[1-9][0-9]*", str(value))
            and int(value) <= 9007199254740991, "Invalid positive numeric coordinate")
    return str(value)


def tag(value, stream):
    require(isinstance(value, str) and re.fullmatch(rf"{re.escape(stream)}/v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?", value),
            f"Expected an exact SemVer {stream}/v<version> release tag")
    version = value[len(stream) + 2:].split("+", 1)[0]
    if "-" in version:
        require(all(not (item.isascii() and item.isdigit() and len(item) > 1 and item.startswith("0"))
                    for item in version.split("-", 1)[1].split(".")), "Numeric prerelease identifiers cannot have leading zeroes")
    return value


def decode(data):
    require(len(data) <= 67_108_864, "JSON input exceeds its bound")
    def pairs(items):
        result = {}
        folded = set()
        for key, value in items:
            lower = key.translate(str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz"))
            require(lower not in folded, "Duplicate/case-colliding JSON member")
            folded.add(lower)
            result[key] = value
        return result
    return json.loads(data.decode("utf-8") if isinstance(data, bytes) else data, object_pairs_hook=pairs,
                      parse_constant=lambda _: require(False, "Non-finite JSON"))


def canonical(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")


def read(path, maximum=67_108_864):
    path = Path(path)
    # Normal platform aliases such as macOS /var are resolved by the caller root.
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= maximum, "Expected bounded regular input")
    return path.read_bytes()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_new(path, value):
    with Path(path).open("xb") as output:
        output.write(canonical(value) + b"\n")


def command(*args, cwd=None):
    return subprocess.check_output(args, cwd=cwd, timeout=300)


def gh(endpoint, binary=False):
    data = command("gh", "api", "-H", "Accept: application/octet-stream" if binary else "Accept: application/vnd.github+json",
                   "-H", "X-GitHub-Api-Version: 2022-11-28", f"repos/{REPOSITORY}/{endpoint}")
    return data if binary else decode(data)


def remote_tag(name, expected):
    sha(expected, 40)
    value = gh("git/ref/tags/" + quote(name, safe=""))["object"]
    for _ in range(9):
        sha(value["sha"], 40)
        if value["type"] == "commit":
            require(value["sha"] == expected, "Remote tag moved or names another artifact source")
            return
        require(value["type"] == "tag", "Tag must resolve to a commit")
        value = gh("git/tags/" + value["sha"])["object"]
    raise ValueError("Excessive annotated-tag depth")


def local_checkout(expected):
    require(command("git", "rev-parse", "HEAD").decode().strip() == sha(expected, 40), "Checkout differs from workflow source")
    require(not command("git", "status", "--porcelain", "--untracked-files=all").strip(), "Release source must be clean")


def invocation(workflow, ref, commit, run, attempt):
    return dict(workflowPath=workflow, workflowRef=ref, workflowSha=sha(commit, 40), runId=number(run), runAttempt=number(attempt))


def current_invocation(workflow):
    require(os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "Unexpected release repository")
    require(os.environ.get("GITHUB_REF_PROTECTED") == "true", "Release workflows require a protected branch/tag ruleset")
    commit = sha(os.environ["GITHUB_SHA"], 40)
    require(os.environ.get("GITHUB_WORKFLOW_SHA") == commit, "Workflow source differs from checked-out source")
    ref = os.environ["GITHUB_REF"]
    require(os.environ.get("GITHUB_WORKFLOW_REF") == f"{REPOSITORY}/{workflow}@{ref}", "Unexpected workflow identity")
    return invocation(workflow, ref, commit, os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])


def sdk_context():
    """Historical retained-corpus context, not current project-version publication."""
    context = current_invocation(SDK_WORKFLOW)
    name = tag(os.environ["GITHUB_REF_NAME"], "sdk")
    require(os.environ.get("GITHUB_EVENT_NAME") in ("push", "workflow_dispatch")
            and context["workflowRef"] == "refs/tags/" + name, "Historical SDK corpus collection requires its exact tag workflow")
    return context
