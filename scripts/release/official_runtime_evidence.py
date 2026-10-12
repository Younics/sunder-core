#!/usr/bin/env python3
"""Validate Host output embedded in native acceptance; never select/download a runtime.

The nested records are the current Sunder.Runtime.Contracts records from
docs/OFFICIAL-RUNTIME-HANDOFF.md. Host owns source allowlists, checksum verification,
materialization, installed receipts and launch. This reader adds no runtime archive,
signature, publication, trust policy or configurable vendor URL.
"""

import argparse
from datetime import datetime
import json
from pathlib import Path
import re
from urllib.parse import urlsplit

RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")


def require(value, message):
    if not value:
        raise ValueError(message)


def closed(value, fields, optional=()):
    require(type(value) is dict and set(fields.split()) <= set(value)
            and set(value) <= set(fields.split()) | set(optional), "Unexpected Host runtime evidence fields")


def digest(value, length=64):
    require(type(value) is str and re.fullmatch(f"[0-9a-f]{{{length}}}", value), "Invalid runtime evidence digest")


def text(value):
    require(type(value) is str and 0 < len(value) <= 2048 and not any(ord(c) < 32 for c in value), "Invalid runtime evidence string")


def timestamp(value):
    text(value)
    require(re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)", value), "Invalid runtime evidence timestamp")
    value = re.sub(r"\.(\d+)", lambda m: "." + (m[1] + "000000")[:6], value)
    require(datetime.fromisoformat(value.replace("Z", "+00:00")).tzinfo is not None, "Missing runtime timestamp offset")


def https(value):
    # URL values are Host evidence, never acquisition inputs for this script.
    text(value)
    url = urlsplit(value)
    require(url.scheme == "https" and url.hostname and not url.username and not url.password and not url.fragment,
            "Host source evidence must be credential-free HTTPS")


def positive(value):
    require(type(value) is int and value > 0, "Invalid actual runtime byte count")


def installed(value, rid):
    closed(value, "identity status size packageReferenceCount installedArtifact health support installedAtUtc", ("retainedUntilUninstall",))
    require(value["status"] == "ready" and rid in RIDS, "Runtime installation is not ready on the selected RID")
    positive(value["size"])
    require(type(value["packageReferenceCount"]) is int and value["packageReferenceCount"] >= 0, "Invalid runtime reference count")
    require(type(value.get("retainedUntilUninstall", False)) is bool, "Invalid retention state")
    identity = value["identity"]
    closed(identity, "sourceId runtimeId vendorVersion rid moduleAbi vendorChecksumAlgorithm vendorChecksumDigest")
    source = identity["sourceId"]
    require(source in ("dotnet", "node") and identity["rid"] == rid, "Runtime source/RID differs")
    require(identity["runtimeId"] == "sunder.runtime." + source
            and identity["moduleAbi"] == "sunder." + source + "-worker-module.v1", "Runtime logical identity/ABI differs")
    match = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?", identity["vendorVersion"])
    require(match is not None and int(match[1]) == (10 if source == "dotnet" else 24), "Runtime is not an exact supported stable vendor version")
    require(source != "node" or (int(match[2]), int(match[3])) >= (18, 1), "Node identity is below the Worker requirement floor")
    require(identity["vendorChecksumAlgorithm"] == ("sha512" if source == "dotnet" else "sha256"), "Vendor checksum algorithm/source mismatch")
    digest(identity["vendorChecksumDigest"], 128 if source == "dotnet" else 64)
    artifact = value["installedArtifact"]
    closed(artifact, "archiveSha256 archiveSize contentIdentity provenance")
    digest(artifact["archiveSha256"])
    digest(artifact["contentIdentity"])
    positive(artifact["archiveSize"])
    require(source != "node" or artifact["archiveSha256"] == identity["vendorChecksumDigest"], "Node archive hash differs from its vendor checksum")
    provenance = artifact["provenance"]
    closed(provenance, "distribution finalDownloadUrl retrievedAtUtc")
    https(provenance["finalDownloadUrl"])
    timestamp(provenance["retrievedAtUtc"])
    distribution = provenance["distribution"]
    closed(distribution, "downloadUrl metadataUrl metadataSha256 checksumUrl checksumDocumentSha256 archiveFormat materializationProfile downloadSize")
    https(distribution["downloadUrl"])
    https(distribution["metadataUrl"])
    digest(distribution["metadataSha256"])
    require((distribution["checksumUrl"] is None) == (distribution["checksumDocumentSha256"] is None), "Incomplete checksum-document evidence")
    if distribution["checksumUrl"] is not None:
        https(distribution["checksumUrl"])
        digest(distribution["checksumDocumentSha256"])
    require(distribution["archiveFormat"] == ("zip" if rid.startswith("win-") else "tar.gz"), "Official archive format/RID differs")
    text(distribution["materializationProfile"])
    if distribution["downloadSize"] is not None:
        positive(distribution["downloadSize"])
        require(distribution["downloadSize"] == artifact["archiveSize"], "Known download size differs from original archive bytes")
    health = value["health"]
    closed(health, "status checkedAtUtc repairRecommended message")
    require(health["status"] == "ready" and health["repairRecommended"] is False, "Runtime health has not passed")
    timestamp(health["checkedAtUtc"])
    require(health["message"] is None or isinstance(health["message"], str), "Invalid health message")
    support = value["support"]
    closed(support, "state supportLine observedAtUtc endOfLifeAtUtc message")
    require(support["state"] == "Supported", "Official installation qualification needs observed vendor support")
    text(support["supportLine"])
    timestamp(support["observedAtUtc"])
    if support["endOfLifeAtUtc"] is not None:
        timestamp(support["endOfLifeAtUtc"])
    require(support["message"] is None or isinstance(support["message"], str), "Invalid support message")
    timestamp(value["installedAtUtc"])
    return source


def load(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 4 * 1024 * 1024, "Expected bounded native acceptance evidence")
    def pairs(values):
        result = {}
        for name, value in values:
            require(name not in result, "Duplicate native evidence field")
            result[name] = value
        return result
    return json.loads(path.read_bytes(), object_pairs_hook=pairs, parse_constant=lambda _: require(False, "Invalid JSON number"))


def verify(document, rid, field, sources):
    values = document[field]
    if type(values) is dict:
        values = [values]
    require(type(values) is list and len(values) == len(sources), "Incomplete Host runtime installation evidence")
    actual = [installed(value, rid) for value in values]
    require(sorted(actual) == sorted(sources) and len(set(actual)) == len(actual), "Missing/duplicate official runtime family")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("receipt")
    parser.add_argument("--rid", required=True, choices=RIDS)
    parser.add_argument("--field", default="runtimeInstallations", choices=["runtimeInstallations", "runtimeInstallation"])
    parser.add_argument("--sources", default="dotnet,node", choices=["dotnet,node", "dotnet"])
    args = parser.parse_args()
    verify(load(args.receipt), args.rid, args.field, args.sources.split(","))


if __name__ == "__main__":
    main()
