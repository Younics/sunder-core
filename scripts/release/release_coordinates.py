#!/usr/bin/env python3
"""Shared, read-only GitHub/source checks for Core release handoffs."""
import argparse
import base64
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote, urlsplit

REPOSITORY = "Younics/sunder-core"
SDK_WORKFLOW = ".github/workflows/sunder-sdk-release.yml"
BASELINE = "eng/platform-contracts/sunder-contracts.owned.json"


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
    require(isinstance(value, str) and re.fullmatch(rf"{stream}/v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?", value),
            f"Expected an exact SemVer {stream}/v<version> release tag")
    if "-" in value:
        require(all(not (item.isascii() and item.isdigit() and len(item) > 1 and item.startswith("0"))
                    for item in value.split("-", 1)[1].split(".")), "Numeric prerelease identifiers cannot have leading zeroes")
    return value


def origin(value):
    parsed = urlsplit(value)
    require(parsed.scheme == "https" and parsed.hostname and not parsed.username and not parsed.password
            and parsed.path in ("", "/") and not parsed.query and not parsed.fragment
            and parsed.netloc == parsed.netloc.lower() and parsed.port != 443
            and value == f"https://{parsed.netloc}/", "Registry origin must be canonical credential-free HTTPS with a terminal slash")
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


def source_file(commit, path):
    value = gh("contents/" + path + "?ref=" + sha(commit, 40))
    require(value["type"] == "file" and value["encoding"] == "base64", "Expected exact source blob")
    return base64.b64decode(value["content"], validate=False)


def approval(prefix):
    require(os.environ.get(prefix + "_APPROVED") == "approved", "Missing explicit " + prefix + " approval")
    reference = os.environ.get(prefix + "_APPROVAL_REFERENCE", "")
    parsed = urlsplit(reference)
    require(parsed.scheme == "https" and parsed.hostname and parsed.path not in ("", "/")
            and not parsed.username and not parsed.password and not parsed.fragment
            and not any(character.isspace() for character in reference), "Missing HTTPS approval evidence reference")


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


def approved_source(source):
    approval("SUNDER_CORE_RELEASE")
    require(sha(source, 40) == os.environ.get("SUNDER_CORE_ARTIFACT_SOURCE_COMMIT"), "Artifact source is not the approved Core SDK source")


def release(release_id, name, source, draft=True):
    value = gh("releases/" + number(release_id))
    require(type(value["id"]) is int and value["id"] == int(release_id) and value["tag_name"] == name and value["target_commitish"] == source
            and value["draft"] is draft, "Expected exact numeric source-bound release state")
    assets = value["assets"]
    require(len({asset["name"] for asset in assets}) == len(assets)
            and len({asset["id"] for asset in assets}) == len(assets), "Ambiguous release assets")
    for asset in assets:
        require(type(asset["id"]) is int, "GitHub asset ID must be numeric")
        number(asset["id"])
        require(asset["state"] == "uploaded" and type(asset["size"]) is int and asset["size"] > 0
                and re.fullmatch(r"sha256:[0-9a-f]{64}", asset["digest"] or ""), "Incomplete release asset metadata")
    return value


def evidence_descendant(source, evidence):
    require(sha(evidence, 40) == os.environ.get("SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT"), "Unapproved publication evidence commit")
    commit = gh("git/commits/" + evidence)
    require([parent["sha"] for parent in commit["parents"]] == [source], "Publication evidence must be a direct untagged descendant")
    comparison = gh(f"compare/{source}...{evidence}")
    require(comparison["ahead_by"] == 1 and comparison["behind_by"] == 0 and comparison["total_commits"] == 1,
            "Publication evidence ancestry differs")
    files = comparison["files"]
    require(files and len(files) < 300 and all(item["filename"] == BASELINE or re.fullmatch(
        r"eng/release/publication-evidence/[a-z0-9][a-z0-9.-]*\.json", item["filename"]) for item in files),
        "Publication evidence commit changes artifact inputs outside the evidence allowlist")
    require(all(item["status"] in ("added", "modified") for item in files), "Evidence files cannot be deleted or renamed")
    require(any(item["filename"] == BASELINE for item in files), "Evidence commit must record the catalog publication")
    before, after = (decode(source_file(commit, BASELINE)) for commit in (source, evidence))
    require(set(before["contracts"]) == set(after["contracts"]), "Publication changed the owned inventory")
    for key, contract in before["contracts"].items():
        published = after["contracts"][key]["current"]
        require(contract["current"]["state"] == "release-frozen" and published["state"] == "published", "Expected verified publication transition")
        publication = published.get("publication")
        closed(publication, "registryOrigin authorityId catalogRelease catalogContentIdentity publishedAtUtc etag")
        require(publication["registryOrigin"] == origin(os.environ["SUNDER_CORE_REGISTRY_ORIGIN"])
                and publication["authorityId"] == "sunder.core"
                and publication["catalogRelease"] == contract["current"]["catalogRelease"]["releaseVersion"]
                and publication["etag"] == '"' + contract["current"]["descriptorSha256"] + '"', "Publication evidence identity differs")
        sha(publication["catalogContentIdentity"])
        require(datetime.fromisoformat(publication["publishedAtUtc"].replace("Z", "+00:00")).tzinfo is not None,
                "Publication timestamp must include its UTC offset")
        contracts = []
        for owned in before["contracts"].values():
            snapshot = owned["current"]
            contracts.append(dict(contractId=owned["contractId"], **{name: snapshot[name] for name in
                ("contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "canonicalDescriptor")}))
        catalog = dict(schemaVersion=1, authorityKind="platform", authorityId="sunder.core",
                       releaseVersion=publication["catalogRelease"], contractCount=len(contracts),
                       contracts=sorted(contracts, key=lambda item: item["contractId"]))
        require(publication["catalogContentIdentity"] == digest(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")).encode()),
                "Publication evidence does not confirm the exact source-derived catalog")
        published.pop("publication")
        published["state"] = "release-frozen"
    require(before == after, "Publication evidence rewrites frozen source/baseline bytes")
    return dict(parentCommit=source, publicationEvidenceCommit=evidence,
                changedPaths=sorted(item["filename"] for item in files), baselineChange="verified-publication-only")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("stream", choices=["sdk"])
    args = parser.parse_args()
    name = tag(os.environ["GITHUB_REF_NAME"], args.stream)
    context = current_invocation(SDK_WORKFLOW)
    require(os.environ["GITHUB_EVENT_NAME"] == "push" and context["workflowRef"] == "refs/tags/" + name, "Only tag pushes produce artifacts")
    source = context["workflowSha"]
    approved_source(source)
    local_checkout(source)
    remote_tag(name, source)
    origin(os.environ["SUNDER_CORE_REGISTRY_ORIGIN"])
    print("Verified approved artifactSourceCommit:", source)


if __name__ == "__main__":
    main()
