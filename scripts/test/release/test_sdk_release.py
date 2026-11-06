"""Project-version selection and retained-byte publication; no remote writes."""
import copy
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import unquote
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import package_metadata as metadata
import sdk_release as sdk
from test_github_release import MemoryRelease

SOURCE = "a" * 40


class PackageReleaseTests(unittest.TestCase):
    def setUp(self):
        self.packages = metadata.packages()
        self.addCleanup(patch.stopall)

    def history(self, versions):
        def get(url):
            return dict(versions=versions) if "nuget.org" in url else dict(name=unquote(url.rsplit("/", 1)[1]), versions=dict.fromkeys(versions, {}))
        return get

    def test_never_published_newer_equal_and_lower_versions(self):
        for published, expected in ((None, True), (["0.9.0"], True), (["1.0.0"], False), (["1.10.0"], False)):
            with self.subTest(published=published):
                get = (lambda _: None) if published is None else self.history(published)
                plan = sdk.select(self.packages, SOURCE, [], get)
                self.assertTrue(all(row["selected"] is expected for row in plan["packages"]))

    def test_independent_versions_and_mixed_registry_selection(self):
        self.packages[0].update(version="2.0.0", tag="nuget-sunder-sdk/v2.0.0", archive="Sunder.Sdk.2.0.0.nupkg")
        def get(url):
            if "sunder.sdk.worker" in url: return dict(versions=["1.2.0"])
            return self.history(["1.1.0"])(url)
        plan = sdk.select(self.packages, SOURCE, [], get)
        self.assertEqual(["Sunder.Sdk"], [row["name"] for row in sdk.selected(plan)])
        # No complete previous cohort/release is required for an ordinary mixed selection.
        self.assertEqual(1, len(sdk.selected(plan, "nuget")))
        self.assertEqual([], sdk.selected(plan, "npm"))

    def test_historical_combined_releases_are_recognized_by_package_assets(self):
        old = dict(tag_name="sdk/v1.1.0", draft=False, assets=[dict(name="Sunder.Sdk.1.1.0.nupkg")])
        plan = sdk.select(self.packages, SOURCE, [old], lambda _: None)
        self.assertFalse(plan["packages"][0]["selected"])
        self.assertEqual(8, len(sdk.selected(plan)))
        old["draft"] = True
        self.assertEqual(9, len(sdk.selected(sdk.select(self.packages, SOURCE, [old], lambda _: None))))

    def test_semver_is_not_lexical_or_a_mutable_channel(self):
        versions = ["1.1.0-2", "1.1.0-10", "1.1.0-alpha", "1.1.0-alpha.1", "1.1.0", "1.2.0", "1.10.0"]
        self.assertEqual(versions, sorted(reversed(versions), key=metadata.version_key))
        self.assertEqual(metadata.version_key("1.1.0"), metadata.version_key("1.1.0+other"))
        self.assertGreater(metadata.version_key("1.1.0.1", True), metadata.version_key("1.1.0", True))
        for bad in ("01.1.0", "1.1.0-01", "1.1.0-", "missing"):
            with self.subTest(bad=bad), self.assertRaises(ValueError): metadata.version_key(bad)

    def test_registry_failures_are_not_missing_publications(self):
        for code in (401, 403, 404, 429, 503):
            with self.subTest(code=code), patch.object(sdk, "build_opener") as opener:
                error = HTTPError("url", code, "fixture", {}, io.BytesIO())
                self.addCleanup(error.close)
                opener.return_value.open.side_effect = error
                if code == 404: self.assertIsNone(sdk.registry_json("https://registry.npmjs.org/test"))
                else:
                    with self.assertRaisesRegex(ValueError, str(code)): sdk.registry_json("https://registry.npmjs.org/test")
        for malformed in ({}, {"versions": []}):
            with self.assertRaises(ValueError): sdk.package_history("nuget", "Sunder.Sdk", "1.1.0", lambda _: malformed)

    def test_metadata_evaluation_does_not_receive_publication_credentials(self):
        def evaluate(command, **kwargs):
            self.assertFalse({"GH_TOKEN", "GITHUB_TOKEN", "NODE_AUTH_TOKEN", "NUGET_API_KEY"} & kwargs["env"].keys())
            project = next(row for row in self.packages if row["project"] == command[2])
            return json.dumps(dict(Properties=dict(PackageId=project["name"], PackageVersion=project["version"]))).encode()
        with patch.dict(os.environ, GH_TOKEN="secret", NODE_AUTH_TOKEN="secret"), patch.object(metadata.subprocess, "check_output", side_effect=evaluate):
            self.assertEqual(self.packages, metadata.packages(evaluate=True))

    def test_plan_cannot_substitute_a_project_version(self):
        plan = sdk.select(self.packages, SOURCE, [], lambda _: None)
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / sdk.PLAN
            path.write_bytes(sdk.c.canonical(plan))
            self.assertEqual(plan, sdk.load(path))
            plan["packages"][0]["version"] = "99.0.0"
            path.write_bytes(sdk.c.canonical(plan))
            with self.assertRaisesRegex(ValueError, "project metadata"): sdk.load(path)

    def test_mixed_template_feed_keeps_independent_versions_and_exact_published_sdk_closure(self):
        def archive(name, version, dependency=""):
            output = io.BytesIO()
            with zipfile.ZipFile(output, "w") as package:
                package.writestr(name + ".nuspec", f'<package><metadata><id>{name}</id><version>{version}</version>{dependency}</metadata></package>')
            return output.getvalue()
        sdk_row = dict(kind="nuget", name="Sunder.Sdk", version="3.0.0", archive="Sunder.Sdk.3.0.0.nupkg", selected=True)
        worker_row = dict(kind="nuget", name="Sunder.Sdk.Worker", version="0.9.0", archive="Sunder.Sdk.Worker.0.9.0.nupkg", selected=False)
        plan = dict(packages=[sdk_row, worker_row])
        original = copy.deepcopy(plan)
        worker = archive("Sunder.Sdk.Worker", "2.0.0", '<dependencies><dependency id="Sunder.Sdk" version="[1.5.0]" /></dependencies>')
        def response(url, **kwargs):
            data = worker if "sunder.sdk.worker" in url else archive("Sunder.Sdk", "1.5.0")
            value = io.BytesIO(data); value.status = 200
            return value
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); candidate = root / "candidate"; candidate.mkdir()
            (candidate / sdk_row["archive"]).write_bytes(archive("Sunder.Sdk", "3.0.0"))
            with patch.object(sdk, "registry_json", return_value=dict(versions=["2.0.0"])), patch.object(sdk, "build_opener") as opener:
                opener.return_value.open.side_effect = response
                sdk.feed(plan, candidate, root / "feed")
            self.assertEqual(original, plan)
            self.assertEqual({"Sunder.Sdk.3.0.0.nupkg", "Sunder.Sdk.Worker.2.0.0.nupkg", "Sunder.Sdk.1.5.0.nupkg", "test-feed.json"},
                             {path.name for path in (root / "feed").iterdir()})
            self.assertEqual(["3.0.0", "2.0.0"], [row["version"] for row in sdk.c.decode((root / "feed/test-feed.json").read_bytes())["packages"]])

    def test_protected_main_source_is_automatic_not_an_approval_variable(self):
        plan = sdk.select(self.packages, SOURCE, [], lambda _: None)
        env = dict(GITHUB_REPOSITORY=sdk.c.REPOSITORY, GITHUB_REF="refs/heads/main", GITHUB_EVENT_NAME="push",
            GITHUB_REF_PROTECTED="true", GITHUB_SHA=SOURCE, GITHUB_WORKFLOW_SHA=SOURCE,
            GITHUB_WORKFLOW_REF=f"{sdk.c.REPOSITORY}/{sdk.c.SDK_WORKFLOW}@refs/heads/main")
        with patch.dict(os.environ, env, clear=True), patch.object(sdk.c, "command", side_effect=lambda *args, **kwargs: SOURCE.encode() if args[1] == "rev-parse" else b""):
            sdk.source_guard(plan)
            for name, value in (("GITHUB_REF", "refs/pull/1/merge"), ("GITHUB_WORKFLOW_SHA", "b" * 40), ("GITHUB_REF_PROTECTED", "false")):
                with patch.dict(os.environ, {name: value}), self.assertRaises(ValueError): sdk.source_guard(plan)

    def test_subset_stage_download_publish_and_finalize_preserve_exact_tested_bytes(self):
        plan = sdk.select(self.packages, SOURCE, [], self.history(["1.1.0"]))
        for row in plan["packages"]:
            row["selected"] = row["name"] in ("Sunder.Sdk.Worker", "@younics/sunder-package-tool")
        stores = {}
        for row in sdk.selected(plan):
            store = MemoryRelease()
            store.tag = store.value["tag_name"] = row["tag"]
            stores[row["name"]] = store
        def evidence(command, **kwargs):
            folder = Path(command[2])
            store = next(store for row in sdk.selected(plan) for store in [stores[row["name"]]] if (folder / row["archive"]).exists())
            records = [dict(path=path.name, size=path.stat().st_size, sha256=sdk.c.digest(path.read_bytes())) for path in sorted(folder.iterdir())]
            invocation = dict(repository=sdk.c.REPOSITORY, releaseId=12, sha=SOURCE, ref="refs/heads/main", refName="main", runId="123", runAttempt="1",
                workflowRef=f"{sdk.c.REPOSITORY}/{sdk.c.SDK_WORKFLOW}@refs/heads/main")
            (folder / "release-provenance.json").write_bytes(sdk.c.canonical(dict(schemaVersion=1, buildType="https://sunderapp.io/provenance/github-actions/v1",
                invocation=invocation, sources=[dict(name="sunder-core", sha=SOURCE)], artifacts=records)))
            (folder / "SHA256SUMS").write_text("".join(f"{item['sha256']}  {item['path']}\n" for item in records))
            (folder / "toolchain.txt").write_text("tested toolchain")
            (folder / "release-sbom.spdx.json").write_bytes(sdk.c.canonical(dict(spdxVersion="SPDX-2.3", dataLicense="CC0-1.0",
                files=[dict(fileName="./" + item["path"], checksums=[dict(algorithm="SHA256", checksumValue=item["sha256"])]) for item in records])))
        def upload(store, release_id, paths):
            for path in paths:
                existing = [item for item in store.value["assets"] if item["name"] == path.name]
                if not existing: store.add(path, path.read_bytes())
                elif store.bytes[existing[0]["id"]] != path.read_bytes(): raise ValueError("Immutable conflict")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            candidate = root / "candidate"; candidate.mkdir()
            for row in sdk.selected(plan): (candidate / row["archive"]).write_bytes((row["name"] + " tested bytes").encode())
            with patch.object(sdk, "source_guard"), patch.object(sdk.c, "remote_tag"), \
                 patch.object(sdk, "store_for", side_effect=lambda row: stores[row["name"]]), \
                 patch.object(MemoryRelease, "upload", upload), patch.object(sdk.subprocess, "run", side_effect=evidence):
                sdk.stage(plan, candidate, root / "staged")
            original = {name: copy.deepcopy(store.bytes) for name, store in stores.items()}
            with patch.object(sdk, "source_guard"), patch.object(sdk.c, "remote_tag"), \
                 patch.object(sdk, "store_for", side_effect=lambda row: stores[row["name"]]), patch.object(sdk.subprocess, "run") as publish:
                sdk.download(plan, root / "recovered")
                sdk.publish(plan, root / "recovered", "npm")
                self.assertEqual("@younics/sunder-package-tool", publish.call_args.args[0][3])
                sdk.finalize(plan, root / "recovered")
                self.assertTrue(all(not store.value["draft"] for store in stores.values()))
                self.assertEqual(original, {name: store.bytes for name, store in stores.items()})


if __name__ == "__main__":
    unittest.main()
