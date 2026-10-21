#!/usr/bin/env python3
"""Mocked source and platform-catalog handoffs; no credentials or publication."""
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import release_coordinates as c
import github_release as assets
import core_catalog_release as catalog

SOURCE, EVIDENCE, MAIN = "a" * 40, "b" * 40, "c" * 40


class GitHub:
    def __init__(self, name="sdk/v1.1.0"):
        self.name = name
        self.files = {}
        self.next_id = 100
        self.calls = []

    def put(self, name, data):
        if name in self.files:
            c.require(self.files[name][1] == data, "immutable mismatch")
        else:
            self.files[name] = (self.next_id, data)
            self.next_id += 1

    def gh(self, endpoint, binary=False):
        self.calls.append(endpoint)
        if endpoint == "releases/42":
            return dict(id=42, tag_name=self.name, target_commitish=SOURCE, draft=True,
                        assets=[dict(id=identity, name=name, size=len(data), digest="sha256:" + c.digest(data), state="uploaded")
                                for name, (identity, data) in self.files.items()])
        if endpoint.startswith("releases/assets/"):
            return next(data for identity, data in self.files.values() if str(identity) == endpoint.split("/")[-1])
        raise AssertionError("Unexpected GitHub API: " + endpoint)

    def upload(self, release_id, paths):
        self.calls.append((release_id, paths))
        for path in paths:
            self.put(Path(path).name, Path(path).read_bytes())


class ReleaseHandoffTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="core-release-handoff-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.env = patch.dict(os.environ, {
            "GH_REPO": c.REPOSITORY, "GITHUB_REPOSITORY": c.REPOSITORY,
            "GITHUB_SHA": SOURCE, "GITHUB_WORKFLOW_SHA": SOURCE, "GITHUB_REF": "refs/tags/sdk/v1.1.0",
            "GITHUB_REF_NAME": "sdk/v1.1.0", "GITHUB_EVENT_NAME": "push", "GITHUB_RUN_ID": "1001", "GITHUB_RUN_ATTEMPT": "2",
            "GITHUB_REF_PROTECTED": "true",
            "GITHUB_WORKFLOW_REF": c.REPOSITORY + "/" + c.SDK_WORKFLOW + "@refs/tags/sdk/v1.1.0",
            "SUNDER_CORE_RELEASE_APPROVED": "approved", "SUNDER_CORE_RELEASE_APPROVAL_REFERENCE": "https://review.invalid/approval/1",
            "SUNDER_CORE_ARTIFACT_SOURCE_COMMIT": SOURCE, "SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT": EVIDENCE,
            "SUNDER_CORE_REGISTRY_ORIGIN": "https://registry.invalid/", "RUNNER_TEMP": str(self.root),
        })
        self.env.start()
        self.addCleanup(self.env.stop)

    def test_source_approval_and_workflow_identity_cannot_be_implied(self):
        c.approved_source(SOURCE)
        c.current_invocation(c.SDK_WORKFLOW)
        for key, value in (("SUNDER_CORE_ARTIFACT_SOURCE_COMMIT", MAIN), ("SUNDER_CORE_RELEASE_APPROVED", "pending")):
            with self.subTest(key=key), patch.dict(os.environ, {key: value}):
                with self.assertRaises(ValueError): c.approved_source(SOURCE)
        with patch.dict(os.environ, {"GITHUB_WORKFLOW_SHA": MAIN}):
            with self.assertRaises(ValueError): c.current_invocation(c.SDK_WORKFLOW)
        with patch.dict(os.environ, {"GITHUB_REF_PROTECTED": "false"}):
            with self.assertRaises(ValueError): c.current_invocation(c.SDK_WORKFLOW)

    def test_dirty_or_wrong_checkout_is_not_clean_release_evidence(self):
        for head, status in ((MAIN, b""), (SOURCE, b" M input.json\n"), (SOURCE, b"?? input.json\n")):
            with self.subTest(head=head, status=status), patch.object(c, "command", side_effect=[head.encode(), status]):
                with self.assertRaises(ValueError): c.local_checkout(SOURCE)

    def test_annotated_tag_requires_exact_artifact_source(self):
        with patch.object(c, "gh", side_effect=[dict(object=dict(type="tag", sha=EVIDENCE)), dict(object=dict(type="commit", sha=SOURCE))]):
            c.remote_tag("sdk/v1.1.0", SOURCE)
        with patch.object(c, "gh", return_value=dict(object=dict(type="commit", sha=MAIN))):
            with self.assertRaises(ValueError): c.remote_tag("sdk/v1.1.0", SOURCE)

    def baseline(self):
        root = Path(__file__).resolve().parents[3]
        baseline = (root / c.BASELINE).read_bytes()
        owned = c.decode(baseline)["contracts"]["stack-contributor-rpc-service-adapter"]
        current = owned["current"]
        value = dict(schemaVersion=1, authorityKind="platform", authorityId="sunder.core", releaseVersion="1.1.0", contractCount=1,
                     contracts=[dict(contractId=owned["contractId"], **{key: current[key] for key in
                         ("contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "canonicalDescriptor")})])
        return baseline, json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode()

    def test_evidence_only_direct_descendant_preserves_tagged_baseline(self):
        baseline, data = self.baseline()
        before = c.decode(baseline)
        after = copy.deepcopy(before)
        current = after["contracts"]["stack-contributor-rpc-service-adapter"]["current"]
        current.update(state="published", publication=dict(registryOrigin="https://registry.invalid/", authorityId="sunder.core",
                       catalogRelease="1.1.0", catalogContentIdentity=c.digest(data), publishedAtUtc="2026-10-05T00:00:00Z", etag='"' + current["descriptorSha256"] + '"'))
        comparison = dict(ahead_by=1, behind_by=0, total_commits=1, files=[dict(filename=c.BASELINE, status="modified")])
        with patch.object(c, "gh", side_effect=[dict(parents=[dict(sha=SOURCE)]), comparison]), patch.object(c, "source_file", side_effect=[baseline, c.canonical(after)]):
            c.evidence_descendant(SOURCE, EVIDENCE)
        for mutation in ("parent", "source", "baseline", "catalog"):
            changed, files = copy.deepcopy(after), copy.deepcopy(comparison)
            if mutation == "source": files["files"].append(dict(filename="scripts/release/changed.py", status="added"))
            if mutation == "baseline": changed["schemaVersion"] = 2
            if mutation == "catalog": changed["contracts"]["stack-contributor-rpc-service-adapter"]["current"]["publication"]["catalogContentIdentity"] = "f" * 64
            with self.subTest(mutation=mutation), patch.object(c, "gh", side_effect=[dict(parents=[dict(sha=MAIN if mutation == "parent" else SOURCE)]), files]), patch.object(c, "source_file", side_effect=[baseline, c.canonical(changed)]):
                with self.assertRaises(ValueError): c.evidence_descendant(SOURCE, EVIDENCE)

    def test_catalog_retention_binds_numeric_asset_source_and_inventory(self):
        baseline, data = self.baseline()
        directory = self.root / "catalog"
        directory.mkdir()
        (directory / catalog.CATALOG).write_bytes(data)
        c.write_new(directory / catalog.EXPORT, dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/platform-catalog-export/v1",
                    artifactSourceCommit=SOURCE, sdkTag="sdk/v1.1.0", registryOrigin="https://registry.invalid/", baselineSha256=c.digest(baseline),
                    inventorySha256=catalog.catalog(data, c.decode(baseline), "1.1.0"), catalogSha256=c.digest(data), catalogBytes=len(data),
                    invocation=c.current_invocation(c.SDK_WORKFLOW)))
        github = GitHub("sdk/v1.1.0")
        with patch.object(c, "gh", github.gh), patch.object(c, "remote_tag"), patch.object(c, "source_file", return_value=baseline), patch.object(assets.GitHubRelease, "upload", github.upload):
            catalog.retain(directory, "42")
            original = (directory / catalog.RECEIPT).read_bytes()
            catalog.retain(directory, "42")
            self.assertEqual(original, (directory / catalog.RECEIPT).read_bytes())
            self.assertEqual({catalog.CATALOG, catalog.RECEIPT}, set(github.files))
            for mutation in ("source", "id", "inventory", "origin", "extra"):
                changed = c.decode(original)
                if mutation == "source": changed["artifactSourceCommit"] = MAIN
                if mutation == "id": changed["catalog"]["assetId"] += 100
                if mutation == "inventory": changed["inventorySha256"] = "f" * 64
                if mutation == "origin": changed["registryOrigin"] = "https://other.invalid/"
                if mutation == "extra": changed["signature"] = "not part of this protocol"
                (directory / catalog.RECEIPT).write_bytes(c.canonical(changed))
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    catalog.verify(directory, "42", "sdk/v1.1.0", SOURCE, "https://registry.invalid/", baseline)

    def test_catalog_export_uses_only_existing_check_export_and_keeps_frozen_baseline(self):
        baseline, data = self.baseline()
        (self.root / c.BASELINE).parent.mkdir(parents=True)
        (self.root / c.BASELINE).write_bytes(baseline)
        calls = []
        def exporter(arguments, **kwargs):
            calls.append(arguments)
            if arguments[3] == "export": Path(arguments[-1]).write_bytes(data)
        with patch.object(c, "local_checkout") as clean, patch.object(c, "remote_tag"), patch.object(catalog.subprocess, "run", exporter):
            catalog.export_catalog(self.root, self.root / "export", self.root / "source-built-verifier.dll")
        self.assertEqual(["check", "export"], [arguments[3] for arguments in calls])
        self.assertEqual(2, clean.call_count)
        self.assertEqual(baseline, (self.root / c.BASELINE).read_bytes())
        exported = c.decode(c.read(self.root / "export" / catalog.EXPORT))
        self.assertEqual(SOURCE, exported["artifactSourceCommit"])
        self.assertEqual(c.digest(data), exported["catalogSha256"])

    def test_catalog_rejects_noncanonical_unfrozen_and_changed_inventory(self):
        baseline, data = self.baseline()
        for mutation in ("whitespace", "draft", "tuple"):
            frozen, raw = c.decode(baseline), data
            if mutation == "whitespace": raw += b"\n"
            if mutation == "draft": frozen["contracts"]["stack-contributor-rpc-service-adapter"]["current"]["state"] = "draft"
            if mutation == "tuple": raw = raw.replace(b'"contractVersion":"2.0.0"', b'"contractVersion":"3.0.0"')
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): catalog.catalog(raw, frozen, "1.1.0")

    def test_closed_json_assets_and_coordinates_reject_ambiguous_inputs(self):
        for raw in (b'{"id":1,"id":2}', b'{"id":1,"ID":2}', b'{"id":NaN}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError): c.decode(raw)
        for value in (True, 0, "01", "-1", "1e2", "9007199254740992"):
            with self.subTest(value=value), self.assertRaises(ValueError): c.number(value)
        for entries in ([], [dict(name="catalog"), dict(name="catalog")]):
            with self.subTest(entries=entries), self.assertRaises(ValueError): assets.asset(dict(assets=entries), "catalog")


if __name__ == "__main__":
    unittest.main()
