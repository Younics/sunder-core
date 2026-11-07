"""App selection/dispatch boundaries; fixtures never mutate remote state."""
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
import app_release as app

SOURCE, OLD = "a" * 40, "b" * 40
TAG = "app/v1.0.0"


def release(version, draft=False):
    return dict(id=12, tag_name="app/v" + version, draft=draft, prerelease="-" in version,
                target_commitish=SOURCE, assets=[])


def run(status="completed", conclusion="failure"):
    return dict(id=123, status=status, conclusion=conclusion, head_sha=SOURCE, head_branch=TAG,
                path=app.WORKFLOW, event="workflow_dispatch", repository=dict(full_name=app.c.REPOSITORY),
                head_repository=dict(full_name=app.c.REPOSITORY))


class AppSelectionTests(unittest.TestCase):
    def setUp(self):
        self.addCleanup(patch.stopall)

    def selection(self, releases=None, source=None, candidates=None):
        patch.object(app.sdk, "github_releases", return_value=releases if releases is not None else [release("0.9.1")]).start()
        patch.object(app, "tag_source", return_value=source).start()
        patch.object(app, "runs", return_value=candidates or []).start()
        patch.object(app, "authored_version", return_value="1.0.0").start()
        patch.object(app.c, "command", return_value=b"").start()

    def test_app_version_must_be_explicit_and_evaluation_never_receives_publication_credentials(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / app.PROJECT
            project.parent.mkdir(parents=True)
            for contents in ("<OutputType>WinExe</OutputType>", '<Version Condition="true">1.0.0</Version>',
                             "<Version>1.0.0</Version><Version>1.1.0</Version>", "<Version>1.0.0+build.1</Version>"):
                project.write_text(f"<Project><PropertyGroup>{contents}</PropertyGroup></Project>")
                with self.assertRaises(ValueError): app.authored_version(root)
            project.write_text("<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")
            with patch.dict(os.environ, GH_TOKEN="secret", GITHUB_TOKEN="secret", NUGET_API_KEY="secret", NODE_AUTH_TOKEN="secret"), \
                 patch.object(app.subprocess, "check_output", return_value=b"1.0.0\n") as evaluate:
                self.assertEqual("1.0.0", app.authored_version(root, evaluate=True))
                self.assertFalse({"GH_TOKEN", "GITHUB_TOKEN", "NUGET_API_KEY", "NODE_AUTH_TOKEN"} & evaluate.call_args.kwargs["env"].keys())
                evaluate.return_value = b"2.0.0\n"
                with self.assertRaisesRegex(ValueError, "Evaluated App"): app.authored_version(root, evaluate=True)

    def test_semver_channels_and_established_major_maintenance_do_not_move_latest_backwards(self):
        for version, published, newer, latest in (
            ("1.0.0", ["0.9.1"], True, True), ("1.0.0", ["1.0.0"], False, True),
            ("1.0.0", ["1.0.1"], False, False), ("1.0.10", ["1.0.9"], True, True),
            ("1.0.0", ["1.1.0-rc.1"], True, True), ("1.0.0-rc.2", ["1.0.0-rc.1"], True, False),
            ("1.0.0-rc.2", ["1.0.0"], False, False), ("1.0.1", ["1.0.0", "2.0.0"], True, False),
            ("1.0.1", ["2.0.0"], False, False),
        ):
            with self.subTest(version=version, published=published):
                state = app.history(version, [release(value) for value in published] + [release("9.0.0", True)])
                self.assertEqual((newer, latest), (state["newer"], state["make_latest"]))

    def test_new_app_selects_without_sdk_candidates_and_published_versions_do_not_start_runs(self):
        self.selection()
        result = app.select("1.0.0", SOURCE)
        self.assertEqual(("dispatch", "0.9.1", SOURCE), (result["action"], result["latest"], result["artifactSourceCommit"]))
        app.sdk.github_releases.return_value = [release("1.0.0")]
        app.tag_source.side_effect = AssertionError("Published versions must not launch or inspect builds")
        self.assertEqual("Already published", app.select("1.0.0", SOURCE)["reason"])
        app.sdk.github_releases.side_effect = ValueError("GitHub unavailable")
        with self.assertRaisesRegex(ValueError, "GitHub unavailable"): app.select("1.0.0", SOURCE)

    def test_existing_ancestor_tag_reuses_run_and_conflicting_source_or_version_fails(self):
        self.selection(source=OLD, candidates=[run("in_progress", None)])
        self.assertEqual("none", app.select("1.0.0", SOURCE)["action"])
        app.c.command.assert_called_with("git", "merge-base", "--is-ancestor", OLD, SOURCE, cwd=app.ROOT)
        app.authored_version.assert_called_with(source=OLD)
        app.authored_version.return_value = "1.1.0"
        with self.assertRaisesRegex(ValueError, "conflicts"): app.select("1.0.0", SOURCE)
        app.authored_version.return_value = "1.0.0"
        app.c.command.side_effect = subprocess.CalledProcessError(1, "git merge-base")
        with self.assertRaises(subprocess.CalledProcessError): app.select("1.0.0", SOURCE)

    def test_failed_app_run_resumes_without_rebuilding_and_drafts_require_retained_signed_artifacts(self):
        self.selection([release("0.9.1"), release("1.0.0", True)], SOURCE, [run()])
        artifacts = [dict(name="sunder-app-release-" + rid, expired=False) for rid in app.assets.RIDS]
        with patch.object(app, "pages", return_value=artifacts):
            result = app.select("1.0.0", SOURCE)
            self.assertEqual(("rerun", "123"), (result["action"], result["runId"]))
            artifacts[0]["expired"] = True
            with self.assertRaisesRegex(ValueError, "expired"): app.select("1.0.0", SOURCE)
            artifacts.clear()
            with self.assertRaisesRegex(ValueError, "all original"): app.select("1.0.0", SOURCE)
        app.runs.return_value = [run(), dict(run(), id=124)]
        with self.assertRaisesRegex(ValueError, "Ambiguous original"): app.select("1.0.0", SOURCE)

    def test_retained_provenance_selects_only_its_original_run(self):
        draft = release("1.0.0", True)
        draft["assets"] = [dict(name="release-provenance.json")]
        invocation = dict(repository=app.c.REPOSITORY, sha=SOURCE, ref="refs/tags/" + TAG, releaseId=12,
                          workflowRef=f"{app.c.REPOSITORY}/{app.WORKFLOW}@refs/tags/{TAG}", runId="123")
        store = app.assets.GitHubRelease(app.c.REPOSITORY, TAG)
        def download(_, path): path.write_text(json.dumps(dict(invocation=invocation)))
        with patch.object(store, "download", side_effect=download):
            self.assertEqual(123, app.original_run(store, draft, [run(), dict(run(), id=124)])["id"])
            invocation["sha"] = OLD
            with self.assertRaisesRegex(ValueError, "Foreign"): app.original_run(store, draft, [run()])

    def test_dispatch_uses_exact_tag_and_reconciles_lost_ack_without_duplicate_dispatch(self):
        self.selection()
        plan = app.select("1.0.0", SOURCE)
        for lost_ack in (False, True):
            with self.subTest(lost_ack=lost_ack), patch.object(app.sdk, "source_guard"), patch.object(app.c, "remote_tag") as remote, \
                 patch.object(app, "select", return_value=copy.deepcopy(plan)), patch.object(app, "api") as api:
                def respond(endpoint, *args):
                    if endpoint == "git/refs": return {}
                    self.assertTrue(endpoint.endswith("/dispatches"))
                    self.assertEqual(("POST", dict(ref=TAG)), args)
                    if lost_ack: raise subprocess.TimeoutExpired("dispatch acknowledgement", 1)
                    return dict(workflow_run_id=123)
                api.side_effect = respond
                app.runs.return_value = [run("queued", None)]
                launched = app.launch(plan)
                self.assertEqual("App release started", launched["reason"])
                self.assertTrue(launched["runUrl"].endswith("/123"))
                self.assertEqual(1, sum(call.args[0].endswith("/dispatches") for call in api.call_args_list))
                remote.assert_called_once_with(TAG, SOURCE)

    def test_tag_creation_preserves_rejection_and_recovers_only_a_matching_lost_ack(self):
        self.selection()
        plan = app.select("1.0.0", SOURCE)
        for observed in (None, OLD, SOURCE):
            with self.subTest(observed=observed), patch.object(app.sdk, "source_guard"), \
                 patch.object(app, "select", return_value=copy.deepcopy(plan)), \
                 patch.object(app, "tag_source", side_effect=[None, observed]), \
                 patch.object(app.c, "remote_tag") as remote, patch.object(app, "api") as api:
                api.side_effect = [ValueError("Tag creation rejected by repository rules"), dict(workflow_run_id=123)]
                if observed == SOURCE:
                    self.assertEqual("App release started", app.launch(plan)["reason"])
                    remote.assert_called_once_with(TAG, SOURCE)
                    self.assertEqual(2, api.call_count)
                else:
                    with self.assertRaisesRegex(ValueError, "Tag creation rejected"):
                        app.launch(plan)
                    remote.assert_not_called()
                    self.assertEqual(1, api.call_count)

    def test_launch_rechecks_state_before_mutations_and_reruns_only_failed_jobs(self):
        self.selection(source=SOURCE, candidates=[run()])
        with patch.object(app, "pages", return_value=[]): plan = app.select("1.0.0", SOURCE)
        with patch.object(app.sdk, "source_guard"), patch.object(app, "select", return_value=copy.deepcopy(plan)) as select, \
             patch.object(app, "pages", return_value=[dict(id=456)]) as jobs, patch.object(app, "api") as api:
            self.assertEqual("Original App run resumption requested", app.launch(plan)["reason"])
            jobs.assert_called_once_with("actions/runs/123/jobs", "jobs")
            api.assert_called_once_with("actions/runs/123/rerun-failed-jobs", "POST")
            api.reset_mock()
            select.return_value.update(action="none", reason="App release already queued or running")
            app.launch(plan)
            api.assert_not_called()
            select.return_value["artifactSourceCommit"] = OLD
            with self.assertRaisesRegex(ValueError, "source changed"): app.launch(plan)

    def test_startup_failure_with_no_jobs_reruns_all_jobs_on_the_original_run(self):
        self.selection(source=SOURCE, candidates=[run()])
        with patch.object(app, "pages", return_value=[]) as jobs, patch.object(app.sdk, "source_guard"), patch.object(app, "api") as api:
            plan = app.select("1.0.0", SOURCE)
            self.assertEqual("123", app.launch(plan)["runId"])
            jobs.assert_called_with("actions/runs/123/jobs", "jobs")
            api.assert_called_once_with("actions/runs/123/rerun", "POST")

    def test_tag_dispatch_requires_exact_repository_workflow_ref_version_and_main_ancestry(self):
        env = dict(GITHUB_REPOSITORY=app.c.REPOSITORY, GITHUB_SHA=SOURCE, GITHUB_WORKFLOW_SHA=SOURCE,
                   GITHUB_REF="refs/tags/" + TAG, GITHUB_EVENT_NAME="workflow_dispatch",
                   GITHUB_WORKFLOW_REF=f"{app.c.REPOSITORY}/{app.WORKFLOW}@refs/tags/{TAG}")
        with patch.dict(os.environ, env, clear=True), patch.object(app.c, "local_checkout"), patch.object(app.c, "remote_tag"), \
             patch.object(app, "authored_version", return_value="1.0.0"), patch.object(app.c, "command") as git:
            self.assertEqual(SOURCE, app.tag_guard(TAG))
            git.assert_called_once_with("git", "merge-base", "--is-ancestor", SOURCE, "origin/main", cwd=app.ROOT)
            for field, value in (("GITHUB_REF", "refs/heads/main"), ("GITHUB_WORKFLOW_SHA", OLD), ("GITHUB_REPOSITORY", "other/repo"),
                                 ("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_WORKFLOW_REF", "other/workflow")):
                with self.subTest(field=field), patch.dict(os.environ, {field: value}), self.assertRaises(ValueError): app.tag_guard(TAG)
            app.authored_version.return_value = "1.0.1"
            with self.assertRaisesRegex(ValueError, "authored version"): app.tag_guard(TAG)

    def test_prepare_skips_published_app_and_refuses_foreign_draft_invocation(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, GITHUB_OUTPUT=str(Path(temporary) / "outputs"),
                GITHUB_REF_NAME=TAG, GITHUB_RUN_ID="123"), patch.object(app, "tag_guard", return_value=SOURCE), \
             patch.object(app, "authored_version", return_value="1.0.0"), patch.object(app.assets.GitHubRelease, "resolve", return_value=release("1.0.0")) as resolve:
            app.prepare()
            self.assertIn("skip=true", (Path(temporary) / "outputs").read_text())
            resolve.return_value = release("1.0.0", True)
            with patch.object(app, "runs", return_value=[dict(run(), id=456)]):
                with self.assertRaisesRegex(ValueError, "original App run"): app.prepare()
            # A duplicate dispatch must not replace signed artifacts even if the
            # first run failed before creating a GitHub draft.
            resolve.return_value = None
            with patch.object(app, "runs", return_value=[dict(run(), id=122), run()]):
                with self.assertRaisesRegex(ValueError, "original App run"): app.prepare()
            with patch.object(app, "runs", return_value=[run()]):
                app.prepare()
                self.assertIn("skip=false", (Path(temporary) / "outputs").read_text())

    def test_packaging_retry_reuses_only_unique_unexpired_artifact_from_its_own_run(self):
        env = dict(GITHUB_REPOSITORY=app.c.REPOSITORY, GITHUB_RUN_ID="123", GITHUB_SHA=SOURCE, GITHUB_WORKFLOW_SHA=SOURCE,
                   GITHUB_REF="refs/tags/" + TAG, GITHUB_EVENT_NAME="workflow_dispatch",
                   GITHUB_REF_NAME=TAG, GITHUB_WORKFLOW_REF=f"{app.c.REPOSITORY}/{app.WORKFLOW}@refs/tags/{TAG}")
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, dict(env, GITHUB_OUTPUT=str(Path(temporary) / "outputs"))), \
             patch.object(app.c, "local_checkout"), patch.object(app.c, "remote_tag"), patch.object(app, "pages", return_value=[]) as artifacts:
            app.retained("win-x64")
            self.assertIn("reuse=false", (Path(temporary) / "outputs").read_text())
            artifacts.return_value = [dict(name="sunder-app-release-win-x64", expired=False)]
            app.retained("win-x64")
            self.assertIn("reuse=true", (Path(temporary) / "outputs").read_text())
            artifacts.assert_called_with("actions/runs/123/artifacts", "artifacts")
            artifacts.return_value[0]["expired"] = True
            with self.assertRaisesRegex(ValueError, "expired"): app.retained("win-x64")
            artifacts.return_value *= 2
            with self.assertRaisesRegex(ValueError, "Ambiguous"): app.retained("win-x64")


if __name__ == "__main__": unittest.main()
