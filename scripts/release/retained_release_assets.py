#!/usr/bin/env python3
"""Numbered immutable handoffs using the existing upload/download byte verifiers."""
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile

import release_coordinates as c

HELPERS = Path(__file__).resolve().parent


def path(root, name):
    value = PurePosixPath(name)
    c.require(isinstance(name, str) and not value.is_absolute() and str(value) == name
              and all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]*", part) for part in value.parts), "Unsafe retained asset path")
    candidate = Path(root).joinpath(*value.parts)
    for ancestor in (candidate, *candidate.parents):
        if ancestor == Path(root):
            break
        c.require(not ancestor.is_symlink(), "Linked retained asset path")
    return candidate


def file_record(root, name):
    candidate = path(root, name)
    c.require(candidate.is_file(), "Missing retained asset: " + name)
    # Runtime archives are streamed; receipt metadata never allocates archive-sized JSON buffers.
    import hashlib
    before = candidate.stat()
    with candidate.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    after = candidate.stat()
    c.require((before.st_ino, before.st_size, before.st_mtime_ns) == (after.st_ino, after.st_size, after.st_mtime_ns)
              and 0 < before.st_size <= 2_147_483_648, "Retained asset changed or exceeds its bound")
    return dict(path=name, sha256=digest, size=before.st_size)


def asset(value, name):
    matches = [item for item in value["assets"] if item["name"] == name]
    c.require(len(matches) == 1, "Missing/ambiguous numbered asset: " + name)
    return matches[0]


def upload(root, paths, prefix, metadata):
    c.require(Path(root).is_dir() and not Path(root).is_symlink(), "Retained input root must be a regular directory")
    destination = metadata["destination"]
    release_id, tag, source = destination["releaseId"], destination["tag"], metadata["artifactSourceCommit"]
    receipt_name = prefix + "-receipt.json"
    expected = [f"{prefix}-file-{index:03}" for index in range(1, len(paths) + 1)] + [receipt_name]
    remote = c.release(release_id, tag, source)
    existing = {entry["name"] for entry in remote["assets"] if entry["name"].startswith(prefix + "-")}
    c.require(existing <= set(expected) and (receipt_name not in existing or existing == set(expected)), "Unexpected assets or falsely marked complete handoff")
    with tempfile.TemporaryDirectory(prefix="sunder-retained-assets-", dir=os.environ.get("RUNNER_TEMP")) as staging:
        staged = Path(staging)
        files = []
        for index, name in enumerate(sorted(paths), 1):
            record = file_record(root, name)
            record["assetName"] = f"{prefix}-file-{index:03}"
            shutil.copyfile(path(root, name), staged / record["assetName"])
            c.require(file_record(staged, record["assetName"])["sha256"] == record["sha256"], "Retained input changed during staging")
            files.append(record)
        env = dict(os.environ, GH_REPO=c.REPOSITORY)
        subprocess.run(["bash", str(HELPERS / "upload-github-release-assets.sh"), str(release_id), tag,
                        *(str(staged / item["assetName"]) for item in files)], env=env, check=True)
        remote = c.release(release_id, tag, source)
        for record in files:
            uploaded = asset(remote, record["assetName"])
            c.require(uploaded["size"] == record["size"] and uploaded["digest"] == "sha256:" + record["sha256"], "Uploaded metadata differs")
            record["assetId"] = uploaded["id"]
        receipt = dict(metadata, files=files)
        c.write_new(staged / receipt_name, receipt)
        # Completion follows verified durable files; partial uploads resume through the existing helper.
        subprocess.run(["bash", str(HELPERS / "upload-github-release-assets.sh"), str(release_id), tag,
                        str(staged / receipt_name)], env=env, check=True)
        verified = staged / "verified"
        verified.mkdir()
        subprocess.run(["bash", str(HELPERS / "download-github-release-assets.sh"), str(release_id), tag,
                        str(verified), *expected], env=env, check=True)
        for name in expected:
            c.require(file_record(staged, name)["sha256"] == file_record(verified, name)["sha256"], "Retained bytes differ after upload")
        c.require({entry["name"] for entry in c.release(release_id, tag, source)["assets"] if entry["name"].startswith(prefix + "-")} == set(expected),
                  "Retained attempt has partial or extra assets")
    return receipt


def download(destination, prefix, expected_metadata, validate):
    coordinates = expected_metadata["destination"]
    release_id, tag, source = coordinates["releaseId"], coordinates["tag"], expected_metadata["artifactSourceCommit"]
    receipt_name = prefix + "-receipt.json"
    remote = c.release(release_id, tag, source)
    receipt_asset = asset(remote, receipt_name)
    c.require(receipt_asset["size"] <= 1_048_576, "Retained receipt exceeds its metadata bound")
    raw = c.gh("releases/assets/" + c.number(receipt_asset["id"]), binary=True)
    c.require(len(raw) == receipt_asset["size"] and "sha256:" + c.digest(raw) == receipt_asset["digest"], "Receipt digest/size mismatch")
    receipt = c.decode(raw)
    c.closed(receipt, " ".join(expected_metadata) + " files")
    c.require(type(receipt["schemaVersion"]) is int and type(receipt["destination"]["releaseId"]) is int, "Invalid receipt numeric metadata")
    c.require({key: receipt[key] for key in expected_metadata} == expected_metadata, "Retained attempt/source/approval coordinates differ")
    records = receipt["files"]
    c.require(isinstance(records, list) and 1 <= len(records) <= 64, "Invalid retained inventory")
    c.require(len({item["path"].lower() for item in records}) == len(records), "Duplicate/case-colliding retained paths")
    names = []
    for index, item in enumerate(records, 1):
        c.closed(item, "path sha256 size assetName assetId")
        path(destination, item["path"])
        c.sha(item["sha256"])
        c.number(item["assetId"])
        c.require(type(item["assetId"]) is int and type(item["size"]) is int and 0 < item["size"] <= 2_147_483_648, "Invalid retained asset numeric metadata")
        c.require(item["assetName"] == f"{prefix}-file-{index:03}", "Noncanonical numbered asset inventory")
        actual = asset(remote, item["assetName"])
        c.require(actual["id"] == item["assetId"] and actual["size"] == item["size"]
                  and actual["digest"] == "sha256:" + item["sha256"], "Retained asset ID, size or digest changed")
        names.append(item["assetName"])
    c.require({item["name"] for item in remote["assets"] if item["name"].startswith(prefix + "-")} == set(names + [receipt_name]),
              "Unexpected assets or falsely marked complete handoff")
    destination = Path(destination)
    c.require(not destination.exists(), "Retained output must be new")
    with tempfile.TemporaryDirectory(prefix="sunder-retained-download-", dir=destination.parent) as staging:
        staged = Path(staging)
        env = dict(os.environ, GH_REPO=c.REPOSITORY)
        subprocess.run(["bash", str(HELPERS / "download-github-release-assets.sh"), str(release_id), tag,
                        str(staged), *names], env=env, check=True)
        tree = staged / "tree"
        tree.mkdir()
        for item in records:
            observed = file_record(staged, item["assetName"])
            c.require(observed["sha256"] == item["sha256"] and observed["size"] == item["size"], "Retained bytes differ from receipt")
            target = path(tree, item["path"])
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(staged / item["assetName"], target)
        validate(tree, receipt)
        after = c.release(release_id, tag, source)
        for name in names + [receipt_name]:
            c.require(asset(after, name) == asset(remote, name), "Retained GitHub asset changed during download")
        (tree / "retained-assets.receipt.json").write_bytes(raw)
        tree.rename(destination)
    return receipt
