#!/usr/bin/env python3
"""Retain/recheck compatibility inputs through the SDK's existing numeric release transport.

This never manufactures qualification receipts or executes an input-supplied recipe.
The owning integrated test lanes must provide the real qualification bundle first.
"""
import argparse
import hashlib
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/release"))
import release_coordinates as c

ASSET = "sunder-sdk-compatibility.tar"
MANIFEST = "eng/compatibility/supported-releases.json"
PUBLICATION = "eng/release/publication-evidence/sdk-compatibility.json"
MAXIMUM = 2 * 1024 ** 3
MAX_FILES = 8192
MISSING = ("SDK release qualification missing: integrated sunder-tested-sdk must include compatibility/qualification.json, "
           "independent base/current manifests, a NEW retained candidate reference, exact tested artifacts and passing "
           "retained-corpus reports. UNFROZEN/UNRELEASED dev success does not authorize publication.")


def context():
    invocation = c.current_invocation(c.SDK_WORKFLOW)
    name = c.tag(os.environ["GITHUB_REF_NAME"], "sdk")
    c.require(os.environ.get("GITHUB_EVENT_NAME") == "push" and invocation["workflowRef"] == "refs/tags/" + name,
              "Compatibility qualification requires the integrated SDK tag run")
    c.require(c.command("git", "rev-parse", "HEAD", cwd=ROOT).decode().strip() == invocation["workflowSha"],
              "Compatibility checkout differs from artifactSourceCommit")
    base = os.environ.get("SUNDER_SDK_COMPATIBILITY_BASE_COMMIT", "")
    c.require(re.fullmatch(r"[a-f0-9]{40}", base) and base != "0" * 40 and base != invocation["workflowSha"],
              "Missing independent SUNDER_SDK_COMPATIBILITY_BASE_COMMIT: protected pre-release history commit required")
    return invocation, name[5:], base


def gate(bundle, packages, base_file, invocation, version, base, executions):
    arguments = ["node", str(ROOT / "scripts/compatibility/check-supported-releases.mjs"),
                 "--base-manifest", str(base_file), "--history-base-commit", base,
                 "--release-version", version, "--artifact-source-commit", invocation["workflowSha"],
                 "--qualification-root", str(bundle), "--candidate-root", str(packages),
                 "--execution-root", str(executions),
                 "--run-id", invocation["runId"], "--run-attempt", invocation["runAttempt"]]
    result = subprocess.run(arguments, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
    c.require(result.returncode == 0, result.stderr.decode("utf-8", errors="replace").strip() or "SDK compatibility gate failed")
    return c.decode(result.stdout)


def identity(path):
    c.require(path.is_file() and not path.is_symlink() and path.stat().st_size <= MAXIMUM, "Missing/bounded compatibility asset required")
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(data)
            c.require(size <= MAXIMUM, "Compatibility asset exceeds 2 GiB bound")
            digest.update(data)
    return digest.hexdigest()


def validate_report(data, format):
    c.require(0 < len(data) <= 8 * 1024 * 1024, "Missing/oversized qualification report")
    text = data.decode("utf-8")
    c.require(not any(marker in text for marker in ("<!DOCTYPE", "<!ENTITY", "<!--", "<![CDATA[")),
              "Unsupported XML markup in qualification report")
    root = ET.fromstring(text)
    local = lambda item: item.tag.rsplit("}", 1)[-1]
    expected_root = ("TestRun",) if format == "trx" else ("testsuite", "testsuites")
    c.require(local(root) in expected_root, "Unexpected qualification report root")
    result_name = "UnitTestResult" if format == "trx" else "testcase"
    results = [item for item in root.iter() if local(item) == result_name]
    c.require(results, "Qualification report has zero executed results")
    for item in root.iter():
        name = local(item)
        if name in ("Counters", "testsuite", "testsuites"):
            for key in ("failed", "failures", "errors", "error", "skipped", "disabled", "notExecuted", "aborted", "timeout", "inconclusive"):
                if key in item.attrib:
                    c.require(re.fullmatch(r"[0-9]+", item.attrib[key]) and int(item.attrib[key]) == 0,
                              "Qualification report has failed/skipped/unfinished counters")
            for key, count in (("total", len(results)), ("tests", sum(local(child) == result_name for child in item.iter()))):
                if key in item.attrib:
                    c.require(re.fullmatch(r"[0-9]+", item.attrib[key]) and int(item.attrib[key]) == count,
                              "Qualification report result count differs")
        if name == "ResultSummary":
            c.require(item.get("outcome") in ("Passed", "Completed"), "Qualification report summary did not pass")
        if name in ("Results", "testsuite"):
            continue
        c.require(not any(local(child) == result_name for child in item)
                  or (format == "trx" and name == "TestRun"), "Qualification result is hidden outside its results container")
    # The XML parser is the authority for attributes, selectors and outcomes.
    # Never rescan raw attribute text: note='outcome="Passed"' is just a note.
    normalized = []
    executions = set()
    if format == "trx":
        definitions = {}
        for item in root.iter():
            if local(item) != "UnitTest":
                continue
            methods = [child for child in item.iter() if local(child) == "TestMethod"]
            identifier = item.get("id")
            c.require(identifier and identifier not in definitions and len(methods) == 1, "Missing/duplicate TRX definition")
            method = methods[0]
            c.require(method.get("className") and method.get("name"), "Incomplete TRX selector")
            definitions[identifier] = method.get("className") + "." + method.get("name")
        for item in results:
            selector = definitions.get(item.get("testId"))
            execution = item.get("executionId")
            c.require(selector and execution and execution not in executions, "Missing/duplicate TRX execution/selector")
            executions.add(execution)
            actual = item.get("outcome")
            outcome = "Passed" if actual == "Passed" else "Skipped" if actual in ("NotExecuted", "Skipped", "NotRunnable") else "Failed"
            normalized.append(dict(selector=selector, outcome=outcome))
    else:
        for item in results:
            source, name = item.get("file"), item.get("name")
            c.require(source and name, "JUnit result requires its actual file/name selector")
            selector = source.replace("\\", "/") + "::" + name
            c.require(selector not in executions, "Duplicate JUnit selector")
            executions.add(selector)
            children = {local(child) for child in item.iter() if child is not item}
            outcome = "Failed" if children & {"failure", "error"} else "Skipped" if children & {"skipped", "disabled"} else "Passed"
            if item.get("status", "run") not in ("run", "passed") or item.get("outcome", "Passed") != "Passed":
                outcome = "Failed"
            normalized.append(dict(selector=selector, outcome=outcome))
    c.require(len(normalized) <= 32768 and all(len(item["selector"]) <= 1024 for item in normalized), "Oversized normalized qualification results")
    return normalized


def prepare(candidate, directory, executions=None):
    # Diagnose missing real evidence before creating or uploading any release asset.
    bundle = candidate / "compatibility"
    c.require((bundle / "qualification.json").is_file(), MISSING)
    c.require(executions is not None and executions.is_dir() and not executions.is_symlink()
              and not executions.resolve().is_relative_to(candidate.resolve()) and not candidate.resolve().is_relative_to(executions.resolve()),
              "Missing independent same-run owning-lane artifacts; candidate-bundled execution claims are not trusted")
    invocation, version, base = context()
    c.approved_source(invocation["workflowSha"])
    c.require(c.command("git", "cat-file", "-t", base, cwd=ROOT).strip() == b"commit", "Trusted history must be a Git commit")
    subprocess.run(["git", "merge-base", "--is-ancestor", base, invocation["workflowSha"]], cwd=ROOT, check=True)
    trusted = c.command("git", "show", f"{base}:{MANIFEST}", cwd=ROOT)
    c.require(c.read(bundle / "base-supported-releases.json", 1_048_576) == trusted, "Compatibility history differs from protected base")
    source_manifest = c.command("git", "show", f"{invocation['workflowSha']}:{MANIFEST}", cwd=ROOT)
    c.require(c.read(ROOT / MANIFEST, 1_048_576) == source_manifest, "Working ledger differs from tagged source")
    with tempfile.TemporaryDirectory() as temporary:
        base_file = Path(temporary) / "trusted-base.json"
        base_file.write_bytes(trusted)
        checked = gate(bundle, directory, base_file, invocation, version, base, executions)
    # The JS gate returns only authenticated retained files. No recursive tar of
    # unchecked workspaces, output executables or arbitrary receipt-selected scripts.
    retained = [(name, bundle / name) for name in checked["files"]]
    retained += [("executions/" + name, executions / name) for name in checked["executionFiles"]]
    c.require(len(retained) <= MAX_FILES, "Compatibility inventory exceeds bound")
    output = directory / ASSET
    with tarfile.open(output, "x", format=tarfile.USTAR_FORMAT) as archive:
        total = 0
        for name, path in sorted(retained):
            info = archive.gettarinfo(str(path), arcname=name)
            total += info.size + 1024
            c.require(info.isfile() and total <= MAXIMUM - 10240, "Compatibility inventory exceeds bound or is not regular")
            info.uid = info.gid = info.mtime = 0
            info.uname = info.gname = ""
            info.mode = 0o644
            with path.open("rb") as stream:
                archive.addfile(info, stream)
    checksum = identity(output)
    # Re-read the completed container; publication cannot rely on pre-copy checks.
    verify(directory, checksum, c.digest(trusted))
    values = dict(compatibility_sha256=checksum, compatibility_base_commit=base, compatibility_base_sha256=c.digest(trusted))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.writelines(f"{key}={value}\n" for key, value in values.items())
    print(c.canonical(values).decode())


def unpack(path, destination):
    names = set()
    folded = set()
    total = 0
    # Uncompressed, bounded, regular-file-only container. Never extractall().
    with path.open("rb") as archive:
        while True:
            header = archive.read(512)
            c.require(len(header) == 512, "Truncated compatibility tar header")
            if header == b"\0" * 512:
                c.require(archive.read(512) == b"\0" * 512, "Missing compatibility tar end marker")
                while padding := archive.read(1024 * 1024):
                    c.require(not padding.strip(b"\0"), "Trailing compatibility tar data")
                break
            # Parse one USTAR header without tarfile's implicit GNU/PAX expansion.
            item = tarfile.TarInfo.frombuf(header, "utf-8", "strict")
            name = item.name
            c.require(re.fullmatch(r"[A-Za-z0-9_@.-]+(?:/[A-Za-z0-9_@.-]+)*", name)
                      and all(part not in (".", "..") for part in name.split("/")), "Unsafe compatibility member path")
            c.require(item.type == tarfile.REGTYPE and not item.linkname
                      and name.lower() not in folded, "Linked/special/duplicate compatibility member")
            names.add(name); folded.add(name.lower())
            total += item.size
            c.require(len(names) <= MAX_FILES and 0 < item.size <= MAXIMUM and total <= MAXIMUM, "Oversized compatibility container")
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as output:
                remaining = item.size
                while remaining:
                    data = archive.read(min(remaining, 1024 * 1024))
                    c.require(data, "Truncated compatibility member")
                    output.write(data)
                    remaining -= len(data)
            padding = (-item.size) % 512
            c.require(archive.read(padding) == b"\0" * padding, "Invalid compatibility tar padding")
    return names


def verify(directory, expected_sha256=None, expected_base_sha256=None):
    expected_sha256 = expected_sha256 or os.environ.get("SUNDER_SDK_COMPATIBILITY_SHA256", "")
    expected_base_sha256 = expected_base_sha256 or os.environ.get("SUNDER_SDK_COMPATIBILITY_BASE_SHA256", "")
    c.require(expected_sha256 and expected_base_sha256, "Missing stage-pinned SDK compatibility qualification/base digests; publication is blocked")
    c.sha(expected_sha256); c.sha(expected_base_sha256)
    path = directory / ASSET
    c.require(identity(path) == expected_sha256, "Compatibility asset differs from the qualification stage")
    invocation, version, base = context()
    with tempfile.TemporaryDirectory() as temporary:
        bundle = Path(temporary)
        names = unpack(path, bundle)
        base_file = bundle / "base-supported-releases.json"
        c.require(c.digest(c.read(base_file, 1_048_576)) == expected_base_sha256, "Trusted compatibility history digest differs")
        checked = gate(bundle, directory, base_file, invocation, version, base, bundle / "executions")
        expected = set(checked["files"]) | {"executions/" + name for name in checked["executionFiles"]}
        c.require(names == expected, "Unexpected/missing compatibility container files")
    return checked


def publication_transition(before, reference):
    c.require(not any(item["id"] == reference["id"] or item["version"] == reference["version"] for item in before["releases"]),
              "Publication confirmation must append a new immutable reference")
    return dict(before, status="FROZEN", releaseStatus="RELEASED", releases=[*before["releases"], reference])


def verify_publication_evidence(proof, before, after, source):
    """Called by the existing direct-descendant publication-evidence validator."""
    import github_release as assets
    c.closed(proof, "schemaVersion kind artifactSourceCommit tag releaseId compatibilityAsset referenceSha256 sourceManifestSha256")
    c.require(type(proof["schemaVersion"]) is int and proof["schemaVersion"] == 1
              and proof["kind"] == "https://sunderapp.io/acceptance/sdk-compatibility-publication/v1"
              and proof["artifactSourceCommit"] == source and proof["sourceManifestSha256"] == c.digest(c.canonical(before)),
              "Compatibility publication source/history differs")
    c.tag(proof["tag"], "sdk"); c.remote_tag(proof["tag"], source)
    c.closed(proof["compatibilityAsset"], "assetName assetId sha256 size")
    c.require(type(proof["releaseId"]) is int and type(proof["compatibilityAsset"]["assetId"]) is int
              and type(proof["compatibilityAsset"]["size"]) is int, "Publication release/asset coordinates must be numeric")
    c.number(proof["releaseId"]); c.number(proof["compatibilityAsset"]["assetId"])
    c.sha(proof["compatibilityAsset"]["sha256"]); c.sha(proof["referenceSha256"])
    store = assets.GitHubRelease(c.REPOSITORY, proof["tag"])
    remote = store.get(proof["releaseId"], draft=False)
    c.require(remote["target_commitish"] == source and remote["prerelease"] is ("-" in proof["tag"]), "Publication release source/version differs")
    item = assets.asset(remote, ASSET)
    expected = dict(assetName=ASSET, assetId=item["id"], sha256=item["digest"][7:], size=item["size"])
    c.require(proof["compatibilityAsset"] == expected, "Publication compatibility asset identity differs")
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        store.download(item, root / ASSET)
        unpack(root / ASSET, root / "bundle")
        receipt = c.decode(c.read(root / "bundle/qualification.json", 1_048_576))
        c.require(receipt["status"] == "freeze-qualified" and receipt["artifactSourceCommit"] == source
                  and receipt["sdkVersion"] == proof["tag"][5:]
                  and c.decode(c.read(root / "bundle/supported-releases.json", 1_048_576)) == before,
                  "Retained qualification is from another candidate/history")
        reference = receipt["reference"]
        c.require(c.digest(c.canonical(reference)) == proof["referenceSha256"]
                  and after == publication_transition(before, reference),
                  "Publication confirmation rewrites the qualified reference or supported history")


def confirm(directory, release_id, output):
    """Produce evidence-only files after actual publication, never during freeze."""
    import github_release as assets
    verify(directory)
    invocation, version, _ = context()
    source = invocation["workflowSha"]
    store = assets.GitHubRelease(c.REPOSITORY, "sdk/v" + version)
    files = assets.local_files(directory)
    c.require(set(files) == assets.core_names("sdk", version) | assets.EVIDENCE, "Incomplete published SDK inventory")
    assets.verify_evidence(files, release_id, c.REPOSITORY, store.tag, source, invocation["runId"])
    remote = store.verify(release_id, files, draft=False)
    item = assets.asset(remote, ASSET)
    with tempfile.TemporaryDirectory() as temporary:
        unpack(directory / ASSET, Path(temporary))
        receipt = c.decode(c.read(Path(temporary) / "qualification.json", 1_048_576))
    before = c.decode(c.read(ROOT / MANIFEST, 1_048_576))
    after = publication_transition(before, receipt["reference"])
    proof = dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/sdk-compatibility-publication/v1",
                 artifactSourceCommit=source, tag=store.tag, releaseId=int(c.number(release_id)),
                 compatibilityAsset=dict(assetName=ASSET, assetId=item["id"], sha256=item["digest"][7:], size=item["size"]),
                 referenceSha256=c.digest(c.canonical(receipt["reference"])), sourceManifestSha256=c.digest(c.canonical(before)))
    verify_publication_evidence(proof, before, after, source)
    c.require(not output.exists() and not output.is_relative_to(ROOT), "Confirmation output must be a new external directory; never rewrite artifact source")
    output.mkdir(parents=True)
    for name, value in ((MANIFEST, after), (PUBLICATION, proof)):
        path = output / name
        path.parent.mkdir(parents=True, exist_ok=True)
        c.write_new(path, value)
    print("Verified published reference; evidence-only files prepared at " + str(output))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify", "confirm", "report"))
    parser.add_argument("--candidate", type=Path)
    parser.add_argument("--executions", type=Path)
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--format", choices=("trx", "junit"))
    parser.add_argument("--release-id")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.action == "report":
        c.require(args.format, "Qualification report format required")
        print(c.canonical(validate_report(sys.stdin.buffer.read(8 * 1024 * 1024 + 1), args.format)).decode())
        return
    c.require(args.directory is not None, "Release asset --directory required")
    if args.action == "prepare":
        c.require(args.candidate is not None, "Tested candidate artifact directory required")
        prepare(args.candidate.resolve(), args.directory.resolve(), args.executions)
    elif args.action == "verify":
        verify(args.directory.resolve())
    else:
        c.require(args.release_id and args.output, "Confirmation requires exact --release-id and new external --output")
        confirm(args.directory.resolve(), args.release_id, args.output.resolve())


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, subprocess.SubprocessError, tarfile.TarError, ET.ParseError) as error:
        raise SystemExit(str(error))
