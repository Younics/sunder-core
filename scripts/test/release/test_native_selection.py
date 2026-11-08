"""Native source, reservation and retry boundaries; no remote mutations."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import native_release as native
from test_github_release import MemoryRelease
import test_github_release

SOURCE, OLD = "a" * 40, "b" * 40


def plan(component="app", source=SOURCE):
    return dict(schemaVersion=1, component=component, version="1.0.0", tag=component + "/v1.0.0",
                artifactSourceCommit=source, runId="123", workflowRef=native.WORKFLOW_REF)


def published(version, component="app", draft=False):
    return dict(id=12, tag_name=component + "/v" + version, draft=draft, prerelease="-" in version,
                target_commitish=SOURCE, assets=[])


def store_for(component="app"):
    store = MemoryRelease()
    store.tag = store.value["tag_name"] = component + "/v1.0.0"
    return store


class NativeSelectionTests(unittest.TestCase):
    def test_versions_are_explicit_and_evaluation_does_not_receive_publication_credentials(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for component, project in native.PROJECTS.items():
                path = root / project
                path.parent.mkdir(parents=True)
                for contents in ("<OutputType>Exe</OutputType>", '<Version Condition="true">1.0.0</Version>',
                                 "<Version>1.0.0</Version><Version>1.1.0</Version>", "<Version>1.0.0+build.1</Version>"):
                    path.write_text(f"<Project><PropertyGroup>{contents}</PropertyGroup></Project>")
                    with self.assertRaises(ValueError): native.authored_version(component, root)
                path.write_text("<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")
                with patch.dict(os.environ, GH_TOKEN="secret", GITHUB_TOKEN="secret", NUGET_API_KEY="secret", NODE_AUTH_TOKEN="secret"), \
                     patch.object(native.subprocess, "check_output", return_value=b"1.0.0\n") as evaluate:
                    self.assertEqual("1.0.0", native.authored_version(component, root))
                    evaluate.assert_not_called()
                    self.assertEqual("1.0.0", native.authored_version(component, root, evaluate=True))
                    self.assertFalse({"GH_TOKEN", "GITHUB_TOKEN", "NUGET_API_KEY", "NODE_AUTH_TOKEN"} & evaluate.call_args.kwargs["env"].keys())
                    evaluate.return_value = b"2.0.0\n"
                    with self.assertRaisesRegex(ValueError, "Evaluated"): native.authored_version(component, root, evaluate=True)

    def test_semver_channels_and_maintenance_releases_preserve_latest(self):
        for version, versions, newer, latest in (
            ("1.0.0", ["0.9.1"], True, True), ("1.0.0", ["1.0.0"], False, True),
            ("1.0.0", ["1.0.1"], False, False), ("1.0.10", ["1.0.9"], True, True),
            ("1.0.0", ["1.1.0-rc.1"], True, True), ("1.0.0-rc.2", ["1.0.0-rc.1"], True, False),
            ("1.0.0-rc.2", ["1.0.0"], False, False), ("1.0.1", ["1.0.0", "2.0.0"], True, False),
            ("1.0.1", ["2.0.0"], False, False),
        ):
            for component in native.PROJECTS:
                with self.subTest(component=component, version=version, versions=versions):
                    result = native.history(component, version, [published(value, component) for value in versions]
                                            + [published("9.0.0", component, True)])
                    self.assertEqual((newer, component == "app" and latest), (result["newer"], result["make_latest"]))

    def test_each_component_selects_independently_and_published_versions_do_not_inspect_builds(self):
        for component in native.PROJECTS:
            with self.subTest(component=component), patch.object(native, "tag_source", return_value=None) as tag:
                row = native.select(component, "1.0.0", SOURCE, [])
                self.assertEqual("build", row["action"])
                tag.reset_mock()
                row = native.select(component, "1.0.0", SOURCE, [published("1.0.0", component)])
                self.assertEqual("skip", row["action"])
                tag.assert_not_called()

    def test_foreign_tags_and_legacy_drafts_require_an_explicit_version_cutover(self):
        with patch.object(native, "tag_source", return_value=OLD):
            with self.assertRaisesRegex(ValueError, "another source"):
                native.select("app", "1.0.0", SOURCE, [])
        store = store_for()
        with patch.object(native, "tag_source", return_value=SOURCE), patch.object(native.assets, "GitHubRelease", return_value=store):
            with self.assertRaisesRegex(ValueError, "predates the integrated workflow"):
                native.select("app", "1.0.0", SOURCE, [store.value])

    def test_incomplete_reservation_resumes_only_its_original_run_and_complete_bytes_can_recover(self):
        store = store_for()
        store.add(native.PLAN, native.c.canonical(plan()))
        with patch.object(native, "tag_source", return_value=SOURCE), patch.object(native.assets, "GitHubRelease", return_value=store), \
             patch.object(native.c, "command"), patch.object(native, "authored_version", return_value="1.0.0"), \
             patch.dict(os.environ, GITHUB_RUN_ID="123"):
            self.assertEqual("build", native.select("app", "1.0.0", SOURCE, [store.value])["action"])
            with patch.dict(os.environ, GITHUB_RUN_ID="456"):
                with self.assertRaisesRegex(ValueError, "original workflow.*123"):
                    native.select("app", "1.0.0", SOURCE, [store.value])
                for name in native.assets.core_names("app", "1.0.0") | native.assets.EVIDENCE:
                    store.add(name, name.encode())
                row = native.select("app", "1.0.0", OLD, [store.value])
                self.assertEqual(("recover", SOURCE), (row["action"], row["artifactSourceCommit"]))
                native.c.command.assert_called_with("git", "merge-base", "--is-ancestor", SOURCE, OLD, cwd=native.ROOT)
                native.c.command.side_effect = subprocess.CalledProcessError(1, "git")
                with self.assertRaises(subprocess.CalledProcessError): native.select("app", "1.0.0", OLD, [store.value])

    def test_reservation_records_run_before_packaging_and_refuses_another_run(self):
        store = store_for()
        row = dict(plan(), action="build")
        def upload(identifier, paths):
            for path in paths: store.add(path, path.read_bytes())
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, GITHUB_RUN_ID="123"), \
             patch.object(native, "selection", return_value=row), patch.object(native.sdk, "source_guard"), \
             patch.object(native, "tag_source", return_value=SOURCE), patch.object(native.c, "remote_tag"), \
             patch.object(native.assets, "GitHubRelease", return_value=store), patch.object(store, "upload", side_effect=upload) as push:
            root = Path(temporary)
            native.reserve("app", root / "first")
            self.assertEqual(plan(), native.read_plan(root / "first" / native.PLAN))
            native.reserve("app", root / "retry")
            self.assertEqual(1, push.call_count)
            with patch.dict(os.environ, GITHUB_RUN_ID="456"), self.assertRaisesRegex(ValueError, "original native run.*123"):
                native.reserve("app", root / "foreign")
            self.assertEqual(1, push.call_count)

    def test_tag_creation_reconciles_only_a_matching_lost_acknowledgement(self):
        for observed in (None, OLD, SOURCE):
            store = store_for()
            api = store.api
            def rejected(endpoint, method="GET", value=None):
                if endpoint == "git/refs": raise ValueError("Tag creation rejected")
                return api(endpoint, method, value)
            with self.subTest(observed=observed), tempfile.TemporaryDirectory() as temporary, \
                 patch.dict(os.environ, GITHUB_RUN_ID="123"), patch.object(native, "selection", return_value=dict(plan(), action="build")), \
                 patch.object(native.sdk, "source_guard"), patch.object(native.assets, "GitHubRelease", return_value=store), \
                 patch.object(native, "tag_source", side_effect=[None, observed]), patch.object(native.c, "remote_tag") as remote, \
                 patch.object(store, "api", side_effect=rejected), patch.object(store, "upload", side_effect=lambda _, paths: store.add(paths[0], paths[0].read_bytes())):
                if observed == SOURCE:
                    native.reserve("app", Path(temporary))
                    remote.assert_called_once_with("app/v1.0.0", SOURCE)
                else:
                    with self.assertRaisesRegex(ValueError, "Tag creation rejected"):
                        native.reserve("app", Path(temporary))
                    remote.assert_not_called()

    def test_packaging_guard_requires_protected_main_exact_source_version_tag_and_original_run(self):
        env = dict(GITHUB_REPOSITORY=native.c.REPOSITORY, GITHUB_SHA=SOURCE, GITHUB_WORKFLOW_SHA=SOURCE,
                   GITHUB_REF="refs/heads/main", GITHUB_REF_PROTECTED="true", GITHUB_EVENT_NAME="push",
                   GITHUB_WORKFLOW_REF=native.WORKFLOW_REF, GITHUB_RUN_ID="123")
        with patch.dict(os.environ, env, clear=True), patch.object(native, "authored_version", return_value="1.0.0") as version, \
             patch.object(native.c, "command", side_effect=lambda *args, **_: SOURCE.encode() if args[1] == "rev-parse" else b""), \
             patch.object(native.c, "remote_tag") as tag:
            native.guard(plan())
            tag.assert_called_with("app/v1.0.0", SOURCE)
            for field, value in (("GITHUB_REF", "refs/tags/app/v1.0.0"), ("GITHUB_WORKFLOW_SHA", OLD),
                                 ("GITHUB_REPOSITORY", "other/repo"), ("GITHUB_EVENT_NAME", "pull_request"),
                                 ("GITHUB_REF_PROTECTED", "false"), ("GITHUB_WORKFLOW_REF", "other/workflow"), ("GITHUB_RUN_ID", "456")):
                with self.subTest(field=field), patch.dict(os.environ, {field: value}), self.assertRaises(ValueError): native.guard(plan())
            version.return_value = "1.0.1"
            with self.assertRaisesRegex(ValueError, "authored version"): native.guard(plan())

    def test_packaging_retry_reuses_only_unique_unexpired_artifacts_from_its_own_run(self):
        for component in native.PROJECTS:
            with self.subTest(component=component), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / native.PLAN).write_text(json.dumps(plan(component)))
                store = store_for(component)
                with patch.object(native, "guard"), patch.object(native.assets, "GitHubRelease", return_value=store), \
                     patch.object(store, "api", return_value=dict(artifacts=[])) as api:
                    self.assertFalse(native.retained(component, "win-x64", root / native.PLAN))
                    api.assert_called_with("actions/runs/123/artifacts?per_page=100&page=1")
                    api.return_value = dict(artifacts=[dict(name=native.artifact_name(component, "win-x64"), expired=False)])
                    self.assertTrue(native.retained(component, "win-x64", root / native.PLAN))
                    api.return_value["artifacts"][0]["expired"] = True
                    with self.assertRaisesRegex(ValueError, "expired"): native.retained(component, "win-x64", root / native.PLAN)
                    api.return_value["artifacts"] *= 2
                    with self.assertRaisesRegex(ValueError, "Ambiguous"): native.retained(component, "win-x64", root / native.PLAN)

    def test_native_recovery_downloads_complete_original_bytes_and_rejects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); original = root / "original"; original.mkdir()
            store = store_for("cli")
            test_github_release.GitHubReleaseTests().candidate(original, store)
            for path in original.iterdir(): store.add(path, path.read_bytes())
            row = dict(plan("cli"), action="recover", channel="stable")
            before = copy.deepcopy(store.bytes)
            with patch.object(native, "selection", return_value=row), patch.object(native.sdk, "source_guard"), \
                 patch.object(native, "guard"), patch.object(native.assets, "GitHubRelease", return_value=store):
                native.download("cli", root / "download")
                self.assertEqual({path.name: path.read_bytes() for path in original.iterdir()},
                                 {path.name: path.read_bytes() for path in (root / "download").iterdir()})
                self.assertEqual(before, store.bytes)
                store.bytes[next(iter(store.bytes))] = b"changed"
                with self.assertRaisesRegex(ValueError, "bytes differ"): native.download("cli", root / "tampered")


if __name__ == "__main__": unittest.main()
