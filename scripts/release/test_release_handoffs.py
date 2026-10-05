#!/usr/bin/env python3
"""Mocked GitHub handoffs: no credentials, runtime build, signing, or publication."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import release_coordinates as c
import retained_release_assets as assets
import core_catalog_release as catalog

spec = importlib.util.spec_from_file_location("retained_native", Path(__file__).resolve().parents[1] / "execution-runtimes/retained_native.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)

SOURCE, EVIDENCE, MAIN = "a" * 40, "b" * 40, "c" * 40


class GitHub:
    def __init__(self, name="runtime/v1.1.0"):
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

    def helper(self, command, **kwargs):
        self.calls.append(Path(command[1]).name)
        if Path(command[1]).name == "upload-github-release-assets.sh":
            for path in command[4:]:
                self.put(Path(path).name, Path(path).read_bytes())
        elif Path(command[1]).name == "download-github-release-assets.sh":
            for name in command[5:]:
                (Path(command[4]) / name).write_bytes(self.files[name][1])
        else:
            raise AssertionError("Unexpected helper or native execution: " + str(command))


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

    def fixture(self):
        root = self.root / "native"
        root.mkdir()
        (root / "execution-runtime-receipts").mkdir()
        c.write_new(root / "production.lock.json", dict(lockKind="production"))
        lock = c.digest(c.read(root / "production.lock.json"))
        drivers = [dict(rid=rid, sha256=c.digest(rid.encode())) for rid in c.RIDS]
        c.write_new(root / "release-drivers.v1.json", dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/runtime-release-drivers/v1", drivers=drivers))
        records, receipts = [], []
        for driver in drivers:
            rid = driver["rid"]
            directory = root / "execution-runtimes" / rid
            directory.mkdir(parents=True)
            lane = []
            for family in ("sunder.runtime.dotnet", "sunder.runtime.node"):
                name = family + ".sunderruntime"
                (directory / name).write_bytes((family + rid).encode())
                record = assets.file_record(directory, name)
                lane.append(dict(rid=rid, runtimeId=family, archiveFile=name, archiveSha256=record["sha256"],
                                 archiveSize=record["size"], releaseSignatureVerified=True, nativeAttestationVerified=True))
            c.write_new(root / "execution-runtime-receipts" / (rid + ".json"), dict(schemaVersion=1,
                        kind="https://sunderapp.io/acceptance/runtime-native-output/v1", runtimeLockSha256=lock, rid=rid,
                        releaseDriverSha256=driver["sha256"], artifacts=lane))
            receipts.append(dict(rid=rid, receiptFile=rid + ".json", receiptSha256=c.digest(c.read(root / "execution-runtime-receipts" / (rid + ".json"))),
                                 releaseDriverSha256=driver["sha256"]))
            records += lane
        c.write_new(root / "release-set.v1.json", dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/runtime-release-set/v1",
                    runtimeLockSha256=lock, releaseDriversSha256=c.digest(c.read(root / "release-drivers.v1.json")),
                    rids=c.RIDS, releaseDrivers=drivers, nativeOutputReceipts=receipts, artifacts=records))
        (root / "runtime-verifier.zip").write_bytes(b"mock verifier; never executed")
        expected = native.metadata("42", "runtime/v1.1.0", SOURCE, "sdk/v1.1.0", "1001", "2", lock)
        return root, expected

    def test_numbered_native_roundtrip_and_equal_partial_retry(self):
        root, expected = self.fixture()
        paths = native.inventory(root, expected)
        self.assertEqual(22, len(paths))
        github = GitHub()
        prefix = "runtime-native-42-1001-2"
        with patch.object(c, "gh", github.gh), patch.object(assets.subprocess, "run", github.helper):
            receipt = assets.upload(root, paths, prefix, expected)
            self.assertEqual(23, len(github.files))
            self.assertEqual(receipt, assets.upload(root, paths, prefix, expected))
            del github.files[prefix + "-receipt.json"]
            self.assertEqual(receipt, assets.upload(root, paths, prefix, expected))
            output = self.root / "download"
            downloaded = assets.download(output, prefix, expected, lambda tree, value: self.assertEqual(paths, native.inventory(tree, value)))
            self.assertEqual(receipt, downloaded)
            for name in paths:
                self.assertEqual(assets.file_record(root, name), assets.file_record(output, name))

    def test_numbered_native_rejects_replaced_ids_partial_extra_and_wrong_attempt(self):
        root, expected = self.fixture()
        github = GitHub()
        prefix = "runtime-native-42-1001-2"
        with patch.object(c, "gh", github.gh), patch.object(assets.subprocess, "run", github.helper):
            assets.upload(root, native.inventory(root, expected), prefix, expected)
            saved = copy.deepcopy(github.files)
            for mutation in ("id", "bytes", "partial", "extra", "attempt"):
                with self.subTest(mutation=mutation):
                    github.files = copy.deepcopy(saved)
                    first = prefix + "-file-001"
                    if mutation == "id": github.files[first] = (99999, github.files[first][1])
                    if mutation == "bytes": github.files[first] = (github.files[first][0], b"modified")
                    if mutation == "partial": del github.files[first]
                    if mutation == "extra": github.put(prefix + "-file-999", b"foreign")
                    changed = copy.deepcopy(expected)
                    if mutation == "attempt": changed["invocation"]["runAttempt"] = "3"
                    with self.assertRaises(ValueError):
                        assets.download(self.root / "rejected", prefix, changed, lambda *_: None)
                    self.assertFalse((self.root / "rejected").exists())

    def test_native_inventory_rechecks_archives_receipts_and_complete_rids(self):
        root, expected = self.fixture()
        archive = next((root / "execution-runtimes").rglob("*.sunderruntime"))
        archive.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "archive differs"):
            native.inventory(root, expected)
        expected["runtimeLockSha256"] = "f" * 64
        with self.assertRaisesRegex(ValueError, "lock differs"):
            native.inventory(root, expected)

    def test_successful_exact_native_attempt_survives_newer_failed_attempt(self):
        run = dict(id=1001, repository=dict(full_name=c.REPOSITORY), path=c.NATIVE_WORKFLOW, event="push",
                   head_sha=SOURCE, head_branch="runtime/v1.1.0", status="completed", conclusion="failure", run_attempt=3)
        attempt = dict(run, conclusion="success", run_attempt=2)
        with patch.object(c, "gh", side_effect=[run, attempt]):
            c.successful_native_run("1001", "2", "runtime/v1.1.0", SOURCE)
        for field, value in (("head_sha", MAIN), ("head_branch", "main"), ("event", "workflow_dispatch"),
                             ("path", c.PUBLICATION_WORKFLOW), ("conclusion", "failure"), ("run_attempt", 3)):
            with self.subTest(field=field), patch.object(c, "gh", side_effect=[run, dict(attempt, **{field: value})]):
                with self.assertRaises(ValueError):
                    c.successful_native_run("1001", "2", "runtime/v1.1.0", SOURCE)

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

    def test_publication_main_may_advance_without_rebuilding_tagged_assets(self):
        output = self.root / "retained"
        output.mkdir()
        (output / "retained-assets.receipt.json").write_bytes(b"mock retained receipt")
        arguments = ["retained_native.py", "download", "--release-id", "42", "--tag", "runtime/v1.1.0",
                     "--source", SOURCE, "--sdk-tag", "sdk/v1.1.0", "--run-id", "1001", "--run-attempt", "2",
                     "--lock-sha256", c.digest(b"approved lock"), "--publication-evidence-commit", EVIDENCE, "--directory", str(output)]
        environment = dict(GITHUB_SHA=MAIN, GITHUB_WORKFLOW_SHA=MAIN, GITHUB_REF="refs/heads/main", GITHUB_EVENT_NAME="workflow_dispatch",
                           GITHUB_WORKFLOW_REF=c.REPOSITORY + "/" + c.PUBLICATION_WORKFLOW + "@refs/heads/main",
                           SUNDER_CORE_SDK_TAG="sdk/v1.1.0", SUNDER_EXECUTION_RUNTIME_LOCK_SHA256=c.digest(b"approved lock"),
                           SUNDER_EXECUTION_RUNTIME_PUBLICATION_APPROVED="approved",
                           SUNDER_EXECUTION_RUNTIME_PUBLICATION_APPROVAL_REFERENCE="https://review.invalid/publication/1")
        with patch.dict(os.environ, environment), patch.object(sys, "argv", arguments), patch.object(c, "remote_tag") as tags, \
                patch.object(c, "local_checkout") as checkout, patch.object(c, "evidence_descendant", return_value=dict(
                    parentCommit=SOURCE, publicationEvidenceCommit=EVIDENCE, changedPaths=[c.BASELINE], baselineChange="verified-publication-only")) as evidence, \
                patch.object(c, "source_file", return_value=b"approved lock"), patch.object(c, "successful_native_run") as run, \
                patch.object(assets, "download") as download, patch.object(native, "unpack_verifier"):
            native.main()
        checkout.assert_called_once_with(MAIN)
        evidence.assert_called_once_with(SOURCE, EVIDENCE)
        self.assertTrue(all(call.args[-1] == SOURCE for call in tags.call_args_list))
        self.assertTrue(all(call.args == ("1001", "2", "runtime/v1.1.0", SOURCE) for call in run.call_args_list))
        self.assertEqual(SOURCE, download.call_args.args[2]["artifactSourceCommit"])
        binding = c.decode((output / "publication-source.v1.json").read_bytes())
        self.assertEqual(SOURCE, binding["artifactSourceCommit"])
        self.assertEqual(EVIDENCE, binding["publicationEvidenceCommit"])
        self.assertEqual(MAIN, binding["invocation"]["workflowSha"])

    def test_retained_verifier_unpack_rejects_traversal_links_and_duplicates(self):
        for mutation in ("traversal", "link", "case-collision"):
            directory = self.root / mutation
            directory.mkdir()
            with zipfile.ZipFile(directory / "runtime-verifier.zip", "w") as archive:
                if mutation == "traversal": archive.writestr("../escape", b"foreign")
                if mutation == "link":
                    item = zipfile.ZipInfo("linked")
                    item.external_attr = (0o120777 << 16)
                    archive.writestr(item, b"/elsewhere")
                if mutation == "case-collision":
                    archive.writestr("First.dll", b"a")
                    archive.writestr("first.dll", b"b")
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): native.unpack_verifier(directory)
            self.assertFalse((directory / "escape").exists())

    def test_annotated_tag_requires_exact_artifact_source(self):
        with patch.object(c, "gh", side_effect=[dict(object=dict(type="tag", sha=EVIDENCE)), dict(object=dict(type="commit", sha=SOURCE))]):
            c.remote_tag("sdk/v1.1.0", SOURCE)
        with patch.object(c, "gh", return_value=dict(object=dict(type="commit", sha=MAIN))):
            with self.assertRaises(ValueError): c.remote_tag("sdk/v1.1.0", SOURCE)

    def baseline(self):
        root = Path(__file__).resolve().parents[2]
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
        with patch.object(c, "gh", github.gh), patch.object(c, "remote_tag"), patch.object(c, "source_file", return_value=baseline), patch.object(catalog.subprocess, "run", github.helper):
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

    def test_closed_json_paths_and_coordinates_reject_ambiguous_inputs(self):
        for raw in (b'{"id":1,"id":2}', b'{"id":1,"ID":2}', b'{"id":NaN}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError): c.decode(raw)
        for value in (True, 0, "01", "-1", "1e2", "9007199254740992"):
            with self.subTest(value=value), self.assertRaises(ValueError): c.number(value)
        for name in ("../out", "/absolute", "x\\y", "x//y", "x/./y"):
            with self.subTest(name=name), self.assertRaises(ValueError): assets.path(self.root, name)


if __name__ == "__main__":
    unittest.main()
