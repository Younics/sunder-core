"""Automatic SDK selection uses mocked GitHub/registry responses; never publishes."""
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import sdk_release as sdk
from test_github_release import MemoryRelease

SOURCE, LATER = "a" * 40, "b" * 40
VERSION, TAG = "1.1.0", "sdk/v1.1.0"


class MemoryWorkflow:
    def __init__(self, source=None, runs=()):
        self.commit, self.values, self.writes = source, list(runs), []
        self.ancestry = "ahead"
        self.lost_creation = False

    def source(self, tag):
        assert tag == TAG
        return self.commit

    def runs(self, tag, source):
        assert (tag, source) == (TAG, self.commit)
        return self.values

    def api(self, suffix, method="GET", value=None):
        if suffix.startswith("compare/"):
            return dict(status=self.ancestry)
        self.writes.append((suffix, method, value))
        if suffix == "git/refs":
            self.commit = value["sha"]
            if self.lost_creation:
                raise subprocess.TimeoutExpired("lost tag acknowledgement", 1)


class SdkReleaseTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, dict(GITHUB_REPOSITORY=sdk.c.REPOSITORY,
            GITHUB_REF="refs/heads/main", GITHUB_EVENT_NAME="push", GITHUB_SHA=LATER, GITHUB_WORKFLOW_SHA=LATER,
            GITHUB_WORKFLOW_REF=sdk.c.REPOSITORY + "/" + sdk.c.SDK_WORKFLOW + "@refs/heads/main"))
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.store = MemoryRelease()
        self.store.tag = TAG
        self.store.value.update(tag_name=TAG)
        self.statuses = sdk.package_statuses(VERSION, get=lambda _: None)

    def candidate(self, draft=True):
        for name in sdk.assets.core_names("sdk", VERSION) | sdk.assets.EVIDENCE:
            self.store.add(name, name.encode())
        self.store.value["draft"] = draft

    def run_info(self, status="completed", conclusion="failure", identifier=123):
        return dict(id=identifier, status=status, conclusion=conclusion)

    def select(self, workflow):
        return sdk.select(VERSION, workflow, self.store, self.statuses)

    def test_first_publication_creates_tag_then_explicitly_dispatches(self):
        self.store.exists = False
        workflow = MemoryWorkflow()
        with patch.object(sdk, "require_recipes"):
            self.assertEqual("dispatch", self.select(workflow)["action"])
        self.assertEqual([("git/refs", "POST", dict(ref="refs/tags/" + TAG, sha=LATER)),
            ("actions/workflows/sunder-sdk-release.yml/dispatches", "POST", dict(ref=TAG))], workflow.writes)

    def test_lost_tag_acknowledgement_reconciles_without_creating_twice(self):
        self.store.exists = False
        workflow = MemoryWorkflow(); workflow.lost_creation = True
        with patch.object(sdk, "require_recipes"):
            self.assertEqual("dispatch", self.select(workflow)["action"])
        self.assertEqual(1, sum(suffix == "git/refs" for suffix, _, _ in workflow.writes))

    def test_missing_qualification_inputs_stop_before_creating_a_useless_tag(self):
        self.store.exists = False
        workflow = MemoryWorkflow()
        with tempfile.TemporaryDirectory() as directory, patch.object(sdk, "ROOT", Path(directory)):
            with self.assertRaisesRegex(ValueError, "inputs are not ready"): self.select(workflow)
        self.assertEqual([], workflow.writes)

    def test_completed_version_on_later_main_is_a_read_only_noop(self):
        self.candidate(draft=False)
        for item in self.statuses.values(): item.update(latest=VERSION, present=True, newer=False)
        workflow = MemoryWorkflow(SOURCE)
        self.assertEqual("skip", self.select(workflow)["action"])
        self.assertEqual([], workflow.writes)

    def test_partial_publication_resumes_original_run_without_retagging(self):
        self.candidate()
        self.statuses["Sunder.Sdk"].update(latest=VERSION, present=True, newer=False)
        workflow = MemoryWorkflow(SOURCE, [self.run_info()])
        self.assertEqual(dict(action="resume", tag=TAG, run_id=123), self.select(workflow))
        self.assertEqual([("actions/runs/123/rerun-failed-jobs", "POST", None)], workflow.writes)
        self.assertEqual(SOURCE, workflow.commit)

    def test_all_uploaded_but_unfinalized_versions_resume_only_the_original_run(self):
        self.candidate()
        for item in self.statuses.values(): item.update(latest=VERSION, present=True, newer=False)
        workflow = MemoryWorkflow(SOURCE, [self.run_info()])
        self.assertEqual(dict(action="resume", tag=TAG, run_id=123), self.select(workflow))
        self.assertEqual([("actions/runs/123/rerun-failed-jobs", "POST", None)], workflow.writes)
        for item in self.statuses.values(): item.update(latest="1.10.0")
        workflow.writes.clear()
        self.assertEqual("skip", self.select(workflow)["action"])
        self.assertEqual([], workflow.writes)

    def test_duplicate_selection_reuses_active_run(self):
        for status in sdk.ACTIVE:
            with self.subTest(status=status):
                workflow = MemoryWorkflow(SOURCE, [self.run_info(status=status, conclusion=None)])
                self.assertEqual("active", self.select(workflow)["action"])
                self.assertEqual([], workflow.writes)

    def test_missing_retained_bytes_or_original_run_never_rebuilds_published_version(self):
        self.statuses["Sunder.Sdk"].update(latest=VERSION, present=True, newer=False)
        workflow = MemoryWorkflow(SOURCE)
        for missing in ("assets", "release", "run"):
            with self.subTest(missing=missing):
                if missing == "release": self.store.exists = False
                if missing == "run": self.store.exists = True; self.candidate()
                with self.assertRaises(ValueError): self.select(workflow)
                self.assertEqual([], workflow.writes)

    def test_tag_release_source_conflict_and_non_main_source_stop_before_mutations(self):
        workflow = MemoryWorkflow(LATER)
        with self.assertRaisesRegex(ValueError, "source conflict"): self.select(workflow)
        workflow = MemoryWorkflow(SOURCE); workflow.ancestry = "diverged"
        with self.assertRaisesRegex(ValueError, "not part of main"): self.select(workflow)
        self.assertEqual([], workflow.writes)

    def test_successful_but_incomplete_run_is_not_rebuilt(self):
        workflow = MemoryWorkflow(SOURCE, [self.run_info(conclusion="success")])
        with self.assertRaisesRegex(ValueError, "succeeded but publication is incomplete"): self.select(workflow)
        self.assertEqual([], workflow.writes)

    def test_selection_from_pull_request_or_different_workflow_is_rejected(self):
        for change in (dict(GITHUB_REF="refs/pull/1/merge"), dict(GITHUB_EVENT_NAME="pull_request"),
                       dict(GITHUB_WORKFLOW_SHA=SOURCE), dict(GITHUB_REPOSITORY="other/repository")):
            with self.subTest(change=change), patch.dict(os.environ, change), self.assertRaises(ValueError):
                self.select(MemoryWorkflow())

    def test_prepare_skips_complete_version_and_refuses_a_new_run_for_retained_candidate(self):
        self.candidate(draft=False)
        with patch.dict(os.environ, GITHUB_SHA=SOURCE):
            published = {name: dict(item, latest=VERSION, present=True, newer=False) for name, item in self.statuses.items()}
            self.assertFalse(sdk.prepare(VERSION, self.store, published))
        provenance = sdk.c.canonical(dict(invocation=dict(runId="123")))
        self.store.value["draft"] = True
        item = next(item for item in self.store.value["assets"] if item["name"] == "release-provenance.json")
        item["digest"] = "sha256:" + sdk.c.digest(provenance)
        with patch.dict(os.environ, dict(GITHUB_SHA=SOURCE, GITHUB_RUN_ID="456")), patch.object(sdk.c, "gh", return_value=provenance):
            with self.assertRaisesRegex(ValueError, "original SDK run"): sdk.prepare(VERSION, self.store, self.statuses)
        with patch.dict(os.environ, dict(GITHUB_SHA=SOURCE, GITHUB_RUN_ID="123")), patch.object(sdk.c, "gh", return_value=provenance), \
             patch.object(sdk, "require_recipes"):
            self.assertTrue(sdk.prepare(VERSION, self.store, self.statuses))

    def test_semver_and_channel_guard_never_promote_an_older_version(self):
        ordered = ["1.1.1-0", "1.1.1-2", "1.1.1-10", "1.1.1-alpha", "1.1.1-alpha.1", "1.1.1", "1.2.0", "2.0.0"]
        self.assertEqual(ordered, sorted(reversed(ordered), key=sdk.version_key))
        for invalid in ("01.1.0", "1.1.0-01", "1.1.0+build"):
            with self.assertRaises(ValueError): sdk.version_key(invalid)
        def get(url):
            from urllib.parse import unquote
            return dict(name=unquote(url.rsplit("/", 1)[1]), **{"dist-tags": {"latest": "1.2.0"}})
        with self.assertRaisesRegex(ValueError, "backwards"): sdk.channels(VERSION, get)
        sdk.channels("1.2.0", get)

    def test_registry_checks_all_nine_version_histories_including_missing_names(self):
        calls = []
        def get(url):
            calls.append(url)
            return None if "npmjs" in url else dict(versions=[VERSION])
        result = sdk.package_presence(VERSION, get=get)
        self.assertEqual(9, len(calls))
        self.assertEqual(6, sum(result.values()))
        self.assertTrue(any(url.endswith("/%40sunder%2Fsdk") for url in calls))
        with self.assertRaises(ValueError): sdk.package_presence(VERSION, get=lambda _: {})
        with self.assertRaisesRegex(ValueError, "not a not-found response"):
            sdk.package_statuses(VERSION, get=lambda _: dict(versions=[]))

    def test_equal_and_older_authored_versions_skip_even_without_a_github_release(self):
        self.store.exists = False
        for published, present in ((VERSION, True), ("1.10.0", False)):
            with self.subTest(published=published):
                for item in self.statuses.values(): item.update(latest=published, present=present, newer=False)
                workflow = MemoryWorkflow()
                with patch.object(sdk, "require_recipes") as recipes:
                    self.assertEqual("skip", self.select(workflow)["action"])
                    self.assertFalse(sdk.prepare(VERSION, self.store, self.statuses))
                    recipes.assert_not_called()
                self.assertEqual([], workflow.writes)

    def test_highest_semver_not_exact_absence_or_mutable_dist_tag_decides_selection(self):
        from urllib.parse import unquote
        def history(versions):
            def get(url):
                if "nuget.org" in url: return dict(versions=versions)
                return dict(name=unquote(url.rsplit("/", 1)[1]), versions=dict.fromkeys(versions, {}), **{"dist-tags": {"latest": "0.1.0"}})
            return get
        for versions, newer, present in ((["1.10.0", "1.0.0"], False, False),
                (["1.1.0", "1.0.0"], False, True), (["1.0.10", "1.0.2"], True, False),
                (["1.1.0-rc.10", "1.1.0-rc.2"], True, False), (["1.2.0-alpha.1"], False, False)):
            with self.subTest(versions=versions):
                statuses = sdk.package_statuses(VERSION, get=history(versions))
                self.assertTrue(all(item["newer"] is newer and item["present"] is present for item in statuses.values()))
        self.assertEqual(sdk.published_version_key("1.1.0"), sdk.published_version_key("1.1.0+other.build"))
        self.assertLess(sdk.version_key(VERSION), sdk.published_version_key("1.1.0.1", nuget=True))
        for bad in ("1.1.0-", "1.1.0-01", "1.1.0-+build", "not-semver"):
            with self.subTest(bad=bad), self.assertRaises(ValueError): sdk.package_statuses(VERSION, get=history([bad]))

    def test_selection_is_per_package_not_a_cohort_wide_boolean(self):
        for item in self.statuses.values(): item.update(latest=VERSION, present=True, newer=False)
        for name in ("Sunder.Sdk.Worker", "@sunder/package-tool"):
            self.statuses[name].update(latest="1.0.9", present=False, newer=True)
        self.statuses["Sunder.Sdk"].update(latest="2.0.0", present=False)
        value = sdk.selection(VERSION, self.statuses)
        self.assertTrue(value["publish"])
        self.assertFalse(value["all_new"])
        self.assertEqual(["Sunder.Sdk.Worker"], value["nuget"])
        self.assertEqual(["@sunder/package-tool"], value["npm"])

    def test_pack_calls_only_selected_projects_and_workspaces_and_rechecks_noop(self):
        for item in self.statuses.values(): item.update(latest=VERSION, present=True, newer=False)
        for name in ("Sunder.Sdk.Worker", "@sunder/package-tool"):
            self.statuses[name].update(latest="1.0.9", present=False, newer=True)
        value = sdk.selection(VERSION, self.statuses)
        with tempfile.TemporaryDirectory() as directory, patch.object(sdk, "ROOT", Path(directory)), \
             patch.dict(os.environ, GITHUB_SHA=SOURCE), patch.object(sdk, "selection", return_value=value) as check, \
             patch.object(sdk.c, "read", return_value=b'{"dependencies":{"@sunder/sdk":">=1.1.0 <1.2.0","vite":"8.2.0"}}'), \
             patch.object(sdk.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, b"[]")) as run:
            self.assertEqual(value, sdk.pack(VERSION))
            check.assert_called_once_with(VERSION)
            commands = [call.args[0] for call in run.call_args_list]
            dotnet = [command for command in commands if command[:2] == ["dotnet", "pack"]]
            self.assertEqual([self.statuses["Sunder.Sdk.Worker"]["project"]], [command[2] for command in dotnet])
            npm = next(command for command in commands if command[:2] == ["npm", "pack"])
            self.assertEqual(["@sunder/package-tool"], [npm[index + 1] for index, arg in enumerate(npm) if arg == "--workspace"])
            self.assertEqual(["Sunder.Sdk.Worker"], next(command for command in commands if "verify-nuget-package-set.py" in str(command))[-1:])
            self.assertEqual(["@sunder/package-tool"], next(command for command in commands if "verify-node-package-artifacts.mjs" in str(command))[-1:])
            run.reset_mock()
            for item in self.statuses.values(): item.update(latest="2.0.0", present=False, newer=False)
            check.return_value = dict(value, publish=False, all_new=False, nuget=[], npm=[])
            self.assertFalse(sdk.pack(VERSION)["publish"])
            run.assert_not_called()

    def test_publisher_never_backfills_an_absent_version_below_an_existing_newer_one(self):
        with patch.object(sdk, "package_history", return_value=dict(latest="1.10.0", present=False, newer=False)):
            with self.assertRaisesRegex(ValueError, "highest published version is 1.10.0"): sdk.package_present(VERSION, "@sunder/sdk")

    def test_only_http_404_is_absence_for_registry_and_github_lookups(self):
        for code in (401, 403, 404, 429, 503):
            with self.subTest(code=code), patch.object(sdk, "build_opener") as opener:
                error = HTTPError("url", code, "fixture", {}, io.BytesIO())
                self.addCleanup(error.close)
                opener.return_value.open.side_effect = error
                if code == 404: self.assertIsNone(sdk.registry_json("https://registry.npmjs.org/test/1.1.0"))
                else:
                    with self.assertRaisesRegex(ValueError, f"HTTP {code}"): sdk.registry_json("https://registry.npmjs.org/test/1.1.0")
            response = subprocess.CompletedProcess([], 1, f"HTTP/2.0 {code} Fixture\r\nContent-Type: application/json\r\n\r\n{{}}".encode(), b"")
            with patch.object(sdk.subprocess, "run", return_value=response):
                if code == 404: self.assertIsNone(sdk.Workflow().api("git/ref/tags/test", optional=True))
                else:
                    with self.assertRaisesRegex(ValueError, f"HTTP {code}"): sdk.Workflow().api("git/ref/tags/test", optional=True)


if __name__ == "__main__":
    unittest.main()
