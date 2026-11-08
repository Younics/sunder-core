"""Version-first scheduling and event boundaries without builds or remote writes."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import select_releases as selection

SOURCE, BASE = "a" * 40, "b" * 40


class ReleaseSelectionTests(unittest.TestCase):
    def setUp(self):
        self.rows = [dict(row, selected=False, latest=row["version"]) for row in selection.packages.packages()]

    def candidates(self, *names):
        return [dict(row, selected=row["name"] in names) for row in self.rows]

    def test_no_new_versions_select_no_tests_platforms_or_packages(self):
        required = selection.requirements(self.rows, [])
        outputs = selection.outputs(required, dict(packages=self.rows), False, [], True)
        for key in ("work", "platform_tests", "consumer_tests", "scenario_tests", "candidate_packages", "app_packages", "binary_packages"):
            self.assertIs(False, outputs[key], key)
        for key in ("components", "platform", "consumers", "scenarios", "app_matrix", "binary_matrix", "native_matrix"):
            self.assertEqual({"include": []}, outputs[key], key)
        self.assertEqual(0, outputs["selected_count"])

    def test_each_native_component_selects_its_dependencies_and_not_sdk_build_shards(self):
        for component, owners, scenarios in (("app", {"app", "host", "cli", "repository"}, []),
                                             ("host", {"host", "repository"}, []),
                                             ("cli", {"cli", "repository"}, ["cli-clients"])):
            with self.subTest(component=component):
                required = selection.requirements(self.rows, [component])
                self.assertEqual(owners, set(required["components"]))
                self.assertEqual(scenarios, required["scenarios"])
                native = [dict(component=component, version="1.0.0", tag=component + "/v1.0.0", action="build", channel="stable")]
                output = selection.outputs(required, dict(packages=self.rows), False, native, True)
                self.assertFalse(any(row["partition"].startswith("package-build") for row in output["platform"]["include"]))
                self.assertEqual(6, len(output["app_matrix" if component == "app" else "binary_matrix"]["include"]))

    def test_package_selection_requires_its_consumers_without_selecting_another_registry(self):
        cases = (("Sunder.Sdk", {"dotnet", "host", "cli", "repository"}),
                 ("Sunder.Sdk.Avalonia", {"dotnet", "host", "cli", "app", "repository"}),
                 ("@younics/sunder-sdk", {"npm", "host", "repository"}))
        for name, owners in cases:
            with self.subTest(name=name):
                rows = self.candidates(name)
                required = selection.requirements(rows, [])
                self.assertEqual(owners, set(required["components"]))
                self.assertEqual(["generated-templates", "interop"], required["scenarios"])
                output = selection.outputs(required, dict(packages=rows), False, [], True)
                self.assertEqual(1, output["selected_count"])
                self.assertEqual((0, 1) if name.startswith("@") else (1, 0), (output["nuget_count"], output["npm_count"]))
                if name.startswith("@"):
                    self.assertFalse(any(row["partition"].startswith("package-build") for row in output["platform"]["include"]))

    def test_mixed_selections_deduplicate_shared_tests_and_recovery_does_not_rebuild_sdk(self):
        rows = self.candidates("Sunder.Sdk")
        required = selection.requirements(rows, ["app", "host", "cli"])
        output = selection.outputs(required, dict(packages=rows), False, [], True)
        self.assertEqual(len(required["components"]), len(set(required["components"])))
        platform = output["platform"]["include"]
        self.assertEqual(len(platform), len({(row["scope"], row["rid"], row["partition"]) for row in platform}))
        self.assertNotIn("cli-clients", required["scenarios"])
        recovered = selection.requirements(rows, ["app"], recover=True)
        self.assertEqual({"app", "host", "cli", "repository"}, set(recovered["components"]))
        self.assertFalse(recovered["candidatePackages"])
        self.assertEqual([], recovered["scenarios"])

    def test_validation_version_comparison_is_per_package_and_tooling_changes_are_explicit(self):
        previous = [{key: value for key, value in row.items() if key not in ("selected", "latest")} for row in self.rows]
        current = [dict(row) for row in previous]
        current[2]["version"] = "2.0.0"
        rows = selection.changed_versions(current, previous)
        self.assertEqual([current[2]["name"]], [row["name"] for row in rows if row["selected"]])
        self.assertFalse(selection.tooling_changed(["README.md", "languages/csharp/dotnet/host/Sunder.App/MainWindow.axaml"]))
        self.assertTrue(selection.tooling_changed([".github/workflows/sunder-sdk-release.yml"]))
        self.assertEqual(["repository"], selection.requirements(self.rows, [], tooling=True)["components"])
        for scope in ("all", "sdk", "host", "cli", "repository"):
            required = selection.requirements(self.rows, [], scopes=[scope])
            self.assertFalse(required["candidatePackages"])
            self.assertNotIn("generated-templates", required["scenarios"])

    def test_push_noop_performs_no_toolchain_evaluation_and_writes_auditable_selection(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); (root / "artifacts").mkdir()
            event = root / "event.json"; event.write_text(json.dumps(dict(before=BASE)))
            outputs = root / "outputs"
            env = dict(GITHUB_EVENT_NAME="push", GITHUB_SHA=SOURCE, GITHUB_EVENT_PATH=str(event), GITHUB_OUTPUT=str(outputs))
            plan = dict(schemaVersion=1, artifactSourceCommit=SOURCE, packages=self.rows)
            def native(component, version, *_):
                return dict(component=component, version=version, action="skip", reason="Already published")
            with patch.dict(os.environ, env), patch.object(selection, "ROOT", root), \
                 patch.object(selection, "VALIDATION", root / "artifacts/validation-selection.json"), \
                 patch.object(selection.native, "SELECTION", root / "artifacts/native-release-selection.json"), \
                 patch.object(selection.packages, "packages", return_value=self.rows), patch.object(selection.sdk, "source_guard"), \
                 patch.object(selection.sdk, "github_releases", return_value=[]), patch.object(selection.sdk, "prepare", return_value=(plan, False)) as sdk, \
                 patch.object(selection.native, "authored_version", return_value="1.0.0") as version, \
                 patch.object(selection.native, "select", side_effect=native), patch.object(selection.c, "command", return_value=b""), \
                 patch.object(selection.subprocess, "check_output", side_effect=AssertionError("No toolchain execution during selection")):
                selection.select("release", "all")
            self.assertIn("work=false", outputs.read_text())
            self.assertEqual([], json.loads((root / "artifacts/validation-selection.json").read_text())["components"])
            self.assertTrue(all(call.kwargs == {} for call in version.call_args_list))
            sdk.assert_called_once_with(root / "artifacts/release-plan.json", releases=[])

    def test_pr_merge_queue_and_explicit_validation_use_read_only_event_selection(self):
        for event, mode, scope in (("pull_request", "validate", "all"), ("merge_group", "validate", "all"),
                                    ("schedule", "validate", "cli"), ("workflow_dispatch", "validate", "cli")):
            with self.subTest(event=event), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); (root / "artifacts").mkdir()
                payload = root / "event.json"
                payload.write_text(json.dumps(dict(pull_request=dict(base=dict(sha=BASE)), merge_group=dict(base_sha=BASE))))
                current = [{key: value for key, value in row.items() if key not in ("selected", "latest")} for row in self.rows]
                previous = [dict(row) for row in current]
                previous[0]["version"] = "0.9.0"
                env = dict(GITHUB_EVENT_NAME=event, GITHUB_SHA=SOURCE, GITHUB_EVENT_PATH=str(payload))
                def version(component, **options):
                    return None if options.get("source") and component == "cli" else "1.0.0"
                with patch.dict(os.environ, env, clear=True), patch.object(selection, "ROOT", root), \
                     patch.object(selection, "VALIDATION", root / "artifacts/validation-selection.json"), \
                     patch.object(selection.native, "SELECTION", root / "artifacts/native-release-selection.json"), \
                     patch.object(selection.packages, "packages", side_effect=lambda _, **options: previous if options.get("source") else current), \
                     patch.object(selection.native, "authored_version", side_effect=version), \
                     patch.object(selection.sdk, "prepare", side_effect=AssertionError("Validation cannot prepare publication")), \
                     patch.object(selection.sdk, "github_releases", side_effect=AssertionError("Validation cannot query private drafts")), \
                     patch.object(selection.c, "command", return_value=b"README.md\n"), patch.object(selection.metadata, "emit") as emit:
                    selection.select(mode, scope)
                result = emit.call_args.args[0]
                self.assertFalse(result["publish"])
                self.assertEqual(0, result["selected_count"] + result["native_count"])
                selected = json.loads((root / "artifacts/validation-selection.json").read_text())
                if event in ("pull_request", "merge_group"):
                    self.assertTrue(selected["candidatePackages"])
                    self.assertEqual({"dotnet", "host", "cli", "repository"}, set(selected["components"]))
                    self.assertEqual(BASE, result["compatibility_base"])
                else:
                    self.assertFalse(selected["candidatePackages"])
                    self.assertEqual({"npm", "dotnet", "host", "cli", "app", "repository"} if event == "schedule" else {"cli", "repository"},
                                     set(selected["components"]))

    def test_final_summary_rejects_skipped_or_missing_selected_publication(self):
        outputs = dict(work="true", selected_count="1", nuget_count="1", npm_count="0", native_count="1",
                       native_build_count="1", app_packages="true", binary_packages="false")
        required = ("preflight", "validation", "stage", "publish-nuget", "finalize", "prepare-native", "package-app", "publish-native")
        jobs = {name: dict(result="success") for name in required}
        jobs.update(select=dict(result="success", outputs=outputs), **{"select-checks": dict(result="skipped"), "publish-npm": dict(result="skipped")})
        with patch.object(selection.report, "append"):
            with patch.dict(os.environ, SUNDER_CI_JOB_RESULTS=json.dumps(jobs)):
                selection.summary()
            for name in required:
                for outcome in ("failure", "cancelled", "skipped", "missing"):
                    with self.subTest(job=name, outcome=outcome):
                        changed = dict(jobs, **{name: dict(result=outcome)})
                        if outcome == "missing": del changed[name]
                        with patch.dict(os.environ, SUNDER_CI_JOB_RESULTS=json.dumps(changed)), self.assertRaisesRegex(ValueError, name):
                            selection.summary()


if __name__ == "__main__": unittest.main()
