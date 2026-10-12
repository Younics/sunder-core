#!/usr/bin/env python3
"""Mock Host output only: no vendor download, installation, signature or publication."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import official_runtime_evidence as evidence


def sample(source, rid):
    stamp = "2026-10-07T00:00:00Z"
    version = "10.0.12" if source == "dotnet" else "24.21.0"
    algorithm = "sha512" if source == "dotnet" else "sha256"
    archive_format = "zip" if rid.startswith("win-") else "tar.gz"
    archive_hash = "a" * 64
    root = "https://builds.dotnet.microsoft.com/dotnet/Runtime/" + version if source == "dotnet" else "https://nodejs.org/dist/v" + version
    return {
        "identity": {"sourceId": source, "runtimeId": "sunder.runtime." + source, "vendorVersion": version,
                     "rid": rid, "moduleAbi": "sunder." + source + "-worker-module.v1",
                     "vendorChecksumAlgorithm": algorithm, "vendorChecksumDigest": "b" * 128 if source == "dotnet" else archive_hash},
        "status": "ready", "size": 200, "packageReferenceCount": 1, "retainedUntilUninstall": False,
        "installedArtifact": {"archiveSha256": archive_hash, "archiveSize": 100, "contentIdentity": "c" * 64,
            "provenance": {"distribution": {"downloadUrl": root + "/original." + archive_format,
                "metadataUrl": "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json" if source == "dotnet" else "https://nodejs.org/dist/index.json",
                "metadataSha256": "d" * 64, "checksumUrl": None if source == "dotnet" else root + "/SHASUMS256.txt",
                "checksumDocumentSha256": None if source == "dotnet" else "e" * 64,
                "archiveFormat": archive_format, "materializationProfile": "host-owned-fixture-profile", "downloadSize": None},
                "finalDownloadUrl": root + "/original." + archive_format, "retrievedAtUtc": stamp}},
        "health": {"status": "ready", "checkedAtUtc": stamp, "repairRecommended": False, "message": None},
        "support": {"state": "Supported", "supportLine": "10" if source == "dotnet" else "24 LTS",
                    "observedAtUtc": stamp, "endOfLifeAtUtc": None, "message": None}, "installedAtUtc": stamp,
    }


class OfficialRuntimeEvidenceTests(unittest.TestCase):
    def test_exact_host_records_cover_both_families_and_six_rids(self):
        for rid in evidence.RIDS:
            with self.subTest(rid=rid):
                evidence.verify({"runtimeInstallations": [sample("dotnet", rid), sample("node", rid)]}, rid, "runtimeInstallations", ["dotnet", "node"])
                evidence.verify({"runtimeInstallation": sample("dotnet", rid)}, rid, "runtimeInstallation", ["dotnet"])

    def test_retired_or_unverified_shapes_cannot_be_relabeled_as_host_receipts(self):
        mutations = [lambda v: v["identity"].update(packagingRevision=1), lambda v: v.update(runtimeLockSha256="f" * 64),
                     lambda v: v["identity"].update(vendorChecksumAlgorithm="sha256"), lambda v: v["identity"].update(vendorChecksumDigest="a" * 64),
                     lambda v: v["identity"].update(vendorVersion="10.0.0-preview.1"), lambda v: v["identity"].update(rid="win-x64"),
                     lambda v: v["installedArtifact"].update(archiveSize=0), lambda v: v["health"].update(repairRecommended=True),
                     lambda v: v["support"].update(state="Unknown"), lambda v: v["installedArtifact"].pop("provenance"),
                     lambda v: v["installedArtifact"]["provenance"]["distribution"].update(checksumUrl="https://example.test/checksum"),
                     lambda v: v["installedArtifact"]["provenance"]["distribution"].update(downloadSize=0),
                     lambda v: v["installedArtifact"]["provenance"].update(finalDownloadUrl="http://example.test/runtime")]
        for mutate in mutations:
            value = sample("dotnet", "linux-x64")
            mutate(value)
            with self.assertRaises((ValueError, KeyError)):
                evidence.installed(value, "linux-x64")

    def test_node_original_archive_digest_and_requirement_floor_remain_binding(self):
        value = sample("node", "osx-arm64")
        value["installedArtifact"]["archiveSha256"] = "f" * 64
        with self.assertRaises(ValueError):
            evidence.installed(value, "osx-arm64")
        value = sample("node", "osx-arm64")
        value["identity"]["vendorVersion"] = "24.0.0"
        with self.assertRaises(ValueError):
            evidence.installed(value, "osx-arm64")

    def test_missing_duplicate_and_mutated_family_evidence_is_rejected(self):
        for values in ([], [sample("dotnet", "linux-x64")], [sample("dotnet", "linux-x64")] * 2):
            with self.assertRaises(ValueError):
                evidence.verify({"runtimeInstallations": values}, "linux-x64", "runtimeInstallations", ["dotnet", "node"])

    def test_duplicate_wire_members_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "receipt.json"
            path.write_text('{"runtimeInstallation":{},"runtimeInstallation":{}}')
            with self.assertRaises(ValueError):
                evidence.load(path)


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--sample":
        print(json.dumps([sample("dotnet", sys.argv[2]), sample("node", sys.argv[2])]))
    else:
        unittest.main()
