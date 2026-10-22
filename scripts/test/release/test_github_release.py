import copy
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[2] / "release"
sys.path.insert(0, str(TOOLS))
import github_release as release


class MemoryRelease(release.GitHubRelease):
    def __init__(self):
        super().__init__("Younics/sunder-core", "cli/v1.0.0")
        self.value = dict(id=12, tag_name=self.tag, draft=True, prerelease=False, target_commitish="a" * 40, assets=[])
        self.bytes = {}
        self.exists = True
        self.requests = []

    def api(self, suffix, method="GET", value=None):
        self.requests.append((suffix, method, value))
        if suffix.startswith("releases?"):
            return [copy.deepcopy(self.value)] if self.exists else []
        if method == "POST" and suffix == "releases":
            self.exists = True
            raise subprocess.TimeoutExpired("lost creation acknowledgement", 1)
        if method == "PATCH":
            self.value.update(value)
        return copy.deepcopy(self.value)

    def add(self, path, data):
        name = Path(path).name
        item = dict(id=100 + len(self.bytes), name=name, size=len(data), digest="sha256:" + hashlib.sha256(data).hexdigest(), state="uploaded")
        self.value["assets"].append(item)
        self.bytes[item["id"]] = data
        return item

    def download(self, item, destination):
        data = self.bytes[item["id"]]
        if (len(data), "sha256:" + hashlib.sha256(data).hexdigest()) != (item["size"], item["digest"]):
            raise ValueError("Downloaded bytes differ")
        destination = Path(destination)
        if destination.exists() and destination.read_bytes() != data:
            raise ValueError("Download would replace different local bytes")
        destination.write_bytes(data)


class GitHubReleaseTests(unittest.TestCase):
    def candidate(self, root, store):
        artifacts = []
        for name in sorted(release.core_names("cli", "1.0.0")):
            (root / name).write_bytes(name.encode())
            size, digest = release.file_identity(root / name)
            artifacts.append(dict(path=name, size=size, sha256=digest[7:]))
        invocation = dict(repository=store.repository, releaseId=12, sha="a" * 40, ref="refs/tags/cli/v1.0.0",
                          refName=store.tag, runId="123", runAttempt="1",
                          workflowRef=store.repository + "/.github/workflows/sunder-cli-release.yml@refs/tags/" + store.tag)
        provenance = dict(schemaVersion=1, buildType="https://sunderapp.io/provenance/github-actions/v1", invocation=invocation,
                          sources=[dict(name="sunder-core", sha="a" * 40)], artifacts=artifacts)
        sbom = dict(spdxVersion="SPDX-2.3", dataLicense="CC0-1.0", files=[dict(fileName="./" + item["path"],
                    checksums=[dict(algorithm="SHA256", checksumValue=item["sha256"])]) for item in artifacts])
        evidence = {"release-provenance.json": json.dumps(provenance).encode(),
                    "release-sbom.spdx.json": json.dumps(sbom).encode(), "toolchain.txt": b"original toolchain\n",
                    "SHA256SUMS": "".join(f"{item['sha256']}  {item['path']}\n" for item in artifacts).encode()}
        for name, data in evidence.items(): (root / name).write_bytes(data)
        return evidence

    def invocation(self, **changes):
        return patch.dict(os.environ, dict(GITHUB_SHA="a" * 40, GITHUB_WORKFLOW_SHA="a" * 40,
            GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/cli/v1.0.0", GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="2",
            GITHUB_WORKFLOW_REF="Younics/sunder-core/.github/workflows/sunder-cli-release.yml@refs/tags/cli/v1.0.0",
            GITHUB_OUTPUT="", **changes))

    def test_creation_lost_response_reuses_the_same_numeric_draft(self):
        store = MemoryRelease(); store.exists = False
        self.assertEqual(12, store.create("a" * 40, "CLI")["id"])
        self.assertEqual(1, sum(method == "POST" for _, method, _ in store.requests))

    def test_source_and_prerelease_mismatch_refuse_adoption(self):
        for field, changed in (("target_commitish", "b" * 40), ("prerelease", True)):
            store = MemoryRelease(); store.value[field] = changed
            with self.assertRaises(ValueError): store.create("a" * 40, "CLI")

    def test_duplicate_ids_names_and_incomplete_assets_are_rejected(self):
        for mutation in (lambda assets: assets.append(copy.deepcopy(assets[0])),
                         lambda assets: assets[0].update(state="starter"),
                         lambda assets: assets[0].update(digest=None),
                         lambda assets: assets[0].update(name="../outside")):
            store = MemoryRelease(); store.add("asset.zip", b"artifact")
            mutation(store.value["assets"])
            with self.assertRaises(ValueError): store.get(12)

    def test_equal_retry_lost_upload_ack_and_conflicting_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "asset.zip"; path.write_bytes(b"artifact")
            store = MemoryRelease()
            def upload(arguments, **_):
                store.add(path, path.read_bytes())
                raise subprocess.TimeoutExpired(arguments, 1)
            with patch.object(release.subprocess, "run", side_effect=upload) as command, patch.object(release.time, "sleep"):
                store.upload(12, [path]); store.upload(12, [path])
                self.assertEqual(1, command.call_count)
                path.write_bytes(b"changed")
                with self.assertRaises(ValueError): store.upload(12, [path])
                self.assertEqual(1, command.call_count)

    def test_published_release_cannot_receive_uploads(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "asset.zip"; path.write_bytes(b"artifact")
            store = MemoryRelease(); store.value["draft"] = False
            with patch.object(release.subprocess, "run") as command:
                with self.assertRaises(ValueError): store.upload(12, [path])
                command.assert_not_called()

    def test_verify_requires_complete_inventory_and_actual_remote_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "asset.zip"; path.write_bytes(b"artifact")
            store = MemoryRelease(); item = store.add(path, path.read_bytes())
            store.verify(12, {path.name: path}, draft=True)
            store.bytes[item["id"]] = b"tampered"
            with self.assertRaises(ValueError): store.verify(12, {path.name: path})
            store.bytes[item["id"]] = path.read_bytes()
            with self.assertRaises(ValueError): store.verify(12, {})

    def test_local_inventory_rejects_links_and_duplicate_flat_names(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); (root / "one").mkdir(); (root / "two").mkdir()
            (root / "one/a.zip").write_bytes(b"a"); (root / "two/a.zip").write_bytes(b"a")
            with self.assertRaises(ValueError): release.local_files(root)
            (root / "two/a.zip").unlink(); (root / "two/link.zip").symlink_to(root / "one/a.zip")
            with self.assertRaises(ValueError): release.local_files(root)

    def test_component_inventory_has_all_six_rids_and_sdk_package_set(self):
        self.assertEqual(6, len(release.core_names("cli", "1.0.0")))
        self.assertEqual(6, len(release.core_names("host", "1.0.0")))
        self.assertEqual(18, len(release.core_names("app", "1.0.0")))
        self.assertEqual(15, len(release.core_names("sdk", "1.1.0")))
        self.assertEqual(13, len(release.core_names("sdk", "1.1.0-beta.1")))

    def test_sdk_missing_qualification_blocks_staging_and_finalization_before_remote_mutation(self):
        for finalize in (False, True):
            with self.subTest(finalize=finalize), tempfile.TemporaryDirectory() as directory:
                store = MemoryRelease(); store.tag = "sdk/v1.1.0"
                with patch.dict(os.environ, dict(GITHUB_SHA="a" * 40, GITHUB_WORKFLOW_SHA="a" * 40,
                     GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/sdk/v1.1.0",
                     GITHUB_WORKFLOW_REF="Younics/sunder-core/.github/workflows/sunder-sdk-release.yml@refs/tags/sdk/v1.1.0")), \
                     patch.object(release.subprocess, "run"), \
                     patch.object(release, "verify_sdk_compatibility", side_effect=ValueError("missing qualification")):
                    with self.assertRaisesRegex(ValueError, "missing qualification"):
                        release.core_stage(store, Path(directory), "sdk", "stable", finalize=finalize)
                self.assertEqual([], store.requests)

    def test_sdk_unbound_documentation_blocks_stage_finalize_and_publication_verification(self):
        for action in ("stage", "finalize", "verify"):
            with self.subTest(action=action), tempfile.TemporaryDirectory() as directory:
                store = MemoryRelease(); store.tag = "sdk/v1.1.0"
                with patch.dict(os.environ, dict(GITHUB_SHA="a" * 40, GITHUB_WORKFLOW_SHA="a" * 40,
                     GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/sdk/v1.1.0",
                     GITHUB_WORKFLOW_REF="Younics/sunder-core/.github/workflows/sunder-sdk-release.yml@refs/tags/sdk/v1.1.0")), \
                     patch.object(release.subprocess, "run"), patch.object(release, "verify_sdk_compatibility"), \
                     patch.object(release, "verify_sdk_documentation", side_effect=ValueError("mutable /main/ documentation")) as docs:
                    with self.assertRaisesRegex(ValueError, "mutable /main/"):
                        if action == "verify":
                            with patch.object(sys, "argv", ["github_release.py", "verify", store.tag, "--directory", directory, "--release-id", "12"]), \
                                 patch.object(release, "local_files", return_value={name: Path(directory) / name for name in
                                     release.core_names("sdk", "1.1.0") | release.EVIDENCE}):
                                release.main()
                        else:
                            release.core_stage(store, Path(directory), "sdk", "stable", finalize=action == "finalize")
                    docs.assert_called_once_with(Path(directory), "a" * 40)
                self.assertEqual([], store.requests)

    def test_stream_download_is_bounded_and_does_not_replace_local_bytes(self):
        class Process:
            def __init__(self, *_, **__): self.stdout = io.BytesIO(b"artifact"); self.returncode = 0
            def poll(self): return self.returncode
            def wait(self, **_): return self.returncode
            def kill(self): self.returncode = -9
        with tempfile.TemporaryDirectory() as directory, patch.object(release.subprocess, "Popen", Process):
            path = Path(directory) / "asset.zip"
            store = release.GitHubRelease("Younics/sunder-core", "cli/v1.0.0")
            item = dict(id=1, size=8, digest="sha256:" + hashlib.sha256(b"artifact").hexdigest())
            store.download(item, path)
            self.assertEqual(b"artifact", path.read_bytes())
            path.write_bytes(b"keep")
            with self.assertRaises(ValueError): store.download(item, path)
            self.assertEqual(b"keep", path.read_bytes())
            item["size"] = 1
            with self.assertRaises(ValueError): store.download(item, path)
            self.assertEqual([path], list(Path(directory).iterdir()))

    def test_partial_evidence_retry_preserves_original_bytes_and_invocation(self):
        with tempfile.TemporaryDirectory() as directory, self.invocation():
            root = Path(directory); store = MemoryRelease(); evidence = self.candidate(root, store)
            for name in ("release-provenance.json", "toolchain.txt"):
                store.add(name, evidence[name])
            for name in evidence: (root / name).unlink()
            def command(arguments, **kwargs):
                if Path(arguments[1]).name == "generate-release-evidence.sh":
                    self.assertEqual("1", kwargs["env"]["GITHUB_RUN_ATTEMPT"])
                    for name, data in evidence.items(): (Path(arguments[2]) / name).write_bytes(data)
            def upload(identifier, paths):
                self.assertEqual(12, identifier)
                existing = {item["name"] for item in store.value["assets"]}
                for path in paths:
                    if path.name not in existing: store.add(path, path.read_bytes())
            with patch.object(release.subprocess, "run", side_effect=command), patch.object(store, "upload", side_effect=upload):
                release.core_stage(store, root, "cli", "stable")
            self.assertTrue(store.value["draft"])
            self.assertEqual(evidence, {name: (root / name).read_bytes() for name in evidence})
            self.assertEqual(10, len(store.value["assets"]))

    def test_foreign_run_changed_payload_and_incomplete_inventory_fail_before_upload(self):
        for mutation in ("run", "payload", "inventory", "sbom"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory, self.invocation():
                root = Path(directory); store = MemoryRelease(); self.candidate(root, store)
                name = sorted(release.core_names("cli", "1.0.0"))[0]
                if mutation == "payload": (root / name).write_bytes(b"changed")
                if mutation == "inventory": (root / name).unlink()
                if mutation in ("run", "sbom"):
                    path = root / ("release-provenance.json" if mutation == "run" else "release-sbom.spdx.json")
                    value = json.loads(path.read_bytes())
                    if mutation == "run": value["invocation"]["runId"] = "456"
                    else: value["files"][0]["checksums"][0]["checksumValue"] = "f" * 64
                    path.write_text(json.dumps(value))
                with patch.object(release.subprocess, "run"), patch.object(store, "upload") as upload:
                    with self.assertRaises(ValueError): release.core_stage(store, root, "cli", "stable")
                    upload.assert_not_called()
                self.assertFalse(any(method == "PATCH" for _, method, _ in store.requests))

    def test_finalization_reconciles_lost_ack_and_published_retry_without_upload(self):
        with tempfile.TemporaryDirectory() as directory, self.invocation():
            root = Path(directory); store = MemoryRelease(); self.candidate(root, store)
            for path in root.iterdir(): store.add(path, path.read_bytes())
            api = store.api
            def lost_ack(suffix, method="GET", value=None):
                result = api(suffix, method, value)
                if method == "PATCH": raise subprocess.TimeoutExpired("lost finalize acknowledgement", 1)
                return result
            with patch.object(release.subprocess, "run"), patch.object(store, "api", side_effect=lost_ack), patch.object(store, "upload") as upload:
                release.core_stage(store, root, "cli", "stable", finalize=True)
                release.core_stage(store, root, "cli", "stable", finalize=True)
                upload.assert_not_called()
            self.assertFalse(store.value["draft"])
            self.assertEqual("false", store.value["make_latest"])
            self.assertEqual(1, sum(method == "PATCH" for _, method, _ in store.requests))

    def test_remote_mutation_after_readback_prevents_finalization(self):
        with tempfile.TemporaryDirectory() as directory, self.invocation():
            root = Path(directory); store = MemoryRelease(); self.candidate(root, store)
            for path in root.iterdir(): store.add(path, path.read_bytes())
            count = 0
            def tag_check(*_, **__):
                nonlocal count
                count += 1
                if count == 2: store.value["assets"][0]["id"] += 1000
            with patch.object(release.subprocess, "run", side_effect=tag_check):
                with self.assertRaises(ValueError): release.core_stage(store, root, "cli", "stable", finalize=True)
            self.assertTrue(store.value["draft"])
            self.assertFalse(any(method == "PATCH" for _, method, _ in store.requests))


if __name__ == "__main__": unittest.main()
