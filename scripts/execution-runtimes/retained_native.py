#!/usr/bin/env python3
"""Tag-native retention and source-bound, no-rebuild publication handoff."""
import argparse
import os
from pathlib import Path
import sys
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import release_coordinates as c
import retained_release_assets as assets


def metadata(release_id, name, source, sdk_tag, run, attempt, lock_sha):
    return dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/runtime-native-assets/v1",
                destination=dict(repository=c.REPOSITORY, releaseId=int(c.number(release_id)), tag=c.tag(name, "runtime")),
                artifactSourceCommit=c.sha(source, 40), sdkTag=c.tag(sdk_tag, "sdk"), runtimeLockSha256=c.sha(lock_sha),
                invocation=c.invocation(c.NATIVE_WORKFLOW, "refs/tags/" + name, source, run, attempt))


def inventory(root, expected):
    lock = c.read(root / "production.lock.json")
    c.require(c.digest(lock) == expected["runtimeLockSha256"] and c.decode(lock)["lockKind"] == "production", "Retained production lock differs")
    drivers = c.decode(c.read(root / "release-drivers.v1.json"))
    c.closed(drivers, "schemaVersion kind drivers")
    c.require(type(drivers["schemaVersion"]) is int and drivers["schemaVersion"] == 1
              and drivers["kind"] == "https://sunderapp.io/acceptance/runtime-release-drivers/v1", "Unexpected release-driver contract")
    for driver in drivers["drivers"]:
        c.closed(driver, "rid sha256")
        c.sha(driver["sha256"])
    release = c.decode(c.read(root / "release-set.v1.json"))
    c.closed(release, "schemaVersion kind runtimeLockSha256 releaseDriversSha256 rids releaseDrivers nativeOutputReceipts artifacts")
    c.require(type(release["schemaVersion"]) is int and release["schemaVersion"] == 1 and release["kind"] == "https://sunderapp.io/acceptance/runtime-release-set/v1"
              and release["runtimeLockSha256"] == c.digest(lock) and release["rids"] == c.RIDS
              and release["releaseDriversSha256"] == c.digest(c.read(root / "release-drivers.v1.json"))
              and release["releaseDrivers"] == drivers["drivers"], "Retained release set differs from lock/driver evidence")
    c.require([item["rid"] for item in release["nativeOutputReceipts"]] == c.RIDS
              and [item["rid"] for item in drivers["drivers"]] == c.RIDS, "Expected all six native receipts/drivers")
    files = ["production.lock.json", "release-drivers.v1.json", "release-set.v1.json", "runtime-verifier.zip"]
    records = release["artifacts"]
    c.require(len(records) == 12 and {(item["rid"], item["runtimeId"]) for item in records}
              == {(rid, family) for rid in c.RIDS for family in ("sunder.runtime.dotnet", "sunder.runtime.node")}, "Expected both exact six-RID families")
    for receipt in release["nativeOutputReceipts"]:
        rid = receipt["rid"]
        name = "execution-runtime-receipts/" + rid + ".json"
        data = c.read(root / name)
        lane = c.decode(data)
        c.closed(lane, "schemaVersion kind runtimeLockSha256 rid releaseDriverSha256 artifacts")
        c.require(type(lane["schemaVersion"]) is int and lane["schemaVersion"] == 1
                  and lane["kind"] == "https://sunderapp.io/acceptance/runtime-native-output/v1", "Unexpected native receipt contract")
        c.require(receipt["receiptFile"] == rid + ".json" and c.digest(data) == receipt["receiptSha256"]
                  and lane["runtimeLockSha256"] == c.digest(lock) and lane["rid"] == rid
                  and lane["releaseDriverSha256"] == receipt["releaseDriverSha256"]
                  and lane["artifacts"] == [item for item in records if item["rid"] == rid], "Native receipt binding differs")
        files.append(name)
    for item in records:
        c.require(item["releaseSignatureVerified"] is True and item["nativeAttestationVerified"] is True, "Unverified native release evidence")
        name = "execution-runtimes/" + item["rid"] + "/" + item["archiveFile"]
        record = assets.file_record(root, name)
        c.require(record["sha256"] == item["archiveSha256"] and record["size"] == item["archiveSize"], "Native archive differs from release set")
        files.append(name)
    return sorted(files)


def unpack_verifier(root):
    destination = root / "verifier"
    destination.mkdir()
    with zipfile.ZipFile(root / "runtime-verifier.zip") as archive:
        entries = archive.infolist()
        c.require(1 <= len(entries) <= 4096 and sum(item.file_size for item in entries) <= 536_870_912, "Verifier archive exceeds its bound")
        folded = set()
        for item in entries:
            target = assets.path(destination, item.filename)
            c.require(item.filename.lower() not in folded and not item.is_dir() and ((item.external_attr >> 16) & 0o170000) in (0, 0o100000),
                      "Verifier archive contains a duplicate, link or non-file")
            folded.add(item.filename.lower())
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as output:
                output.write(archive.read(item))
    c.require((destination / "Sunder.ExecutionRuntime.Build.dll").is_file()
              and (destination / "Sunder.ExecutionRuntime.Build.runtimeconfig.json").is_file(), "Retained verifier is incomplete")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["upload", "download"])
    for name in ("release-id", "tag", "source", "sdk-tag", "run-id", "run-attempt", "lock-sha256", "directory"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--publication-evidence-commit")
    args = parser.parse_args()
    expected = metadata(args.release_id, args.tag, args.source, args.sdk_tag, args.run_id, args.run_attempt, args.lock_sha256)
    c.approved_source(args.source)
    c.remote_tag(args.tag, args.source)
    c.remote_tag(args.sdk_tag, args.source)
    c.require(args.lock_sha256 == os.environ.get("SUNDER_EXECUTION_RUNTIME_LOCK_SHA256"), "Unapproved runtime lock digest")
    c.require(args.sdk_tag == os.environ.get("SUNDER_CORE_SDK_TAG"), "Unapproved SDK source tag")
    prefix = f"runtime-native-{args.release_id}-{args.run_id}-{args.run_attempt}"
    root = Path(args.directory).absolute()
    if args.action == "upload":
        c.require(c.current_invocation(c.NATIVE_WORKFLOW) == expected["invocation"] and os.environ["GITHUB_EVENT_NAME"] == "push", "Only the matching tag-native attempt may retain assets")
        files = inventory(root, expected)
        c.require(sorted(item.relative_to(root).as_posix() for item in root.rglob("*") if item.is_file()) == files, "Unmanifested native output")
        assets.upload(root, files, prefix, expected)
    else:
        control = c.current_invocation(c.PUBLICATION_WORKFLOW)
        c.require(control["workflowRef"] == "refs/heads/main" and os.environ["GITHUB_EVENT_NAME"] == "workflow_dispatch", "Publication must dispatch from protected main")
        c.approval("SUNDER_EXECUTION_RUNTIME_PUBLICATION")
        c.local_checkout(control["workflowSha"])
        evidence_diff = c.evidence_descendant(args.source, args.publication_evidence_commit)
        c.require(c.digest(c.source_file(args.source, c.LOCK)) == args.lock_sha256, "Approved source lock differs")
        c.successful_native_run(args.run_id, args.run_attempt, args.tag, args.source)
        def validate(tree, receipt):
            c.require(inventory(tree, expected) == [item["path"] for item in receipt["files"]], "Native receipt inventory differs")
        assets.download(root, prefix, expected, validate)
        c.successful_native_run(args.run_id, args.run_attempt, args.tag, args.source)
        c.remote_tag(args.tag, args.source)
        c.remote_tag(args.sdk_tag, args.source)
        unpack_verifier(root)
        c.write_new(root / "publication-source.v1.json", dict(artifactSourceCommit=args.source,
                    publicationEvidenceCommit=args.publication_evidence_commit, evidenceDiff=evidence_diff, nativeBuild=expected,
                    retainedReceiptSha256=c.digest(c.read(root / "retained-assets.receipt.json")), invocation=control))
    c.remote_tag(args.tag, args.source)
    c.remote_tag(args.sdk_tag, args.source)
    print("Verified exact retained native attempt:", args.release_id, args.run_id, args.run_attempt)


if __name__ == "__main__":
    main()
