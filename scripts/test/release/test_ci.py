import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import subprocess
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "ci"))
import metadata
import core_ci


class CiMetadataTests(unittest.TestCase):
    def test_local_inputs_are_available_to_consumers_and_temporary_state_is_cleaned_on_failure(self):
        work = []
        def templates(path):
            work.append(path)
            (path / "fixture").write_text("prepared")
            core_ci.publish_environment({"SUNDER_TEST_LOCAL_INPUT": path / "fixture"})
        def consume(*args, **kwargs):
            self.assertEqual("prepared", Path(os.environ["SUNDER_TEST_LOCAL_INPUT"]).read_text())
            raise RuntimeError("test failure")
        with patch.dict(os.environ, {}, clear=True), patch.object(core_ci, "run"), \
                patch.object(core_ci, "selection", return_value={"components": ["sdk"]}), \
                patch.object(core_ci, "templates", side_effect=templates), patch.object(core_ci, "dotnet_tests", side_effect=consume):
            with self.assertRaisesRegex(RuntimeError, "test failure"): core_ci.validate("candidate")
            self.assertNotIn("SUNDER_TEST_LOCAL_INPUT", os.environ)
        self.assertFalse(work[0].exists())

    def test_batched_python_results_keep_failures_subtests_and_skips_per_method(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, output = root / "fixture.py", root / "results.json"
            source.write_text('import unittest\nclass Cases(unittest.TestCase):\n'
                ' def test_pass(self): pass\n'
                ' def test_fail(self):\n  with self.subTest(value=1): self.fail("seeded failure")\n'
                ' @unittest.skip("seeded skip")\n def test_skip(self): pass\n')
            result = subprocess.run([sys.executable, "-B", str(metadata.ROOT / "scripts/test-oracles/run-python-fixture.py"),
                str(source), str(output), "Cases.test_pass", "Cases.test_fail", "Cases.test_skip"], capture_output=True, text=True)
            self.assertEqual(1, result.returncode, result.stderr)
            rows = json.loads(output.read_text())
            self.assertEqual(["passed", "failed", "skipped"], [row["status"] for row in rows])
            self.assertIn("seeded failure", rows[1]["output"])

    def test_profiles_keep_artifact_preparation_after_node_cleanup_and_require_native_coverage(self):
        calls = []
        def record(name):
            return lambda *args, **kwargs: calls.append((name, args, kwargs))
        for scope, scenarios in (("all", ["interop", "app-interop"]), ("sdk", ["interop"]), ("repository", ["app-interop"])):
            calls.clear()
            with self.subTest(scope=scope), patch.object(core_ci, "run", side_effect=record("run")), \
                    patch.object(core_ci, "oracle", side_effect=record("oracle")), \
                    patch.object(core_ci, "source", side_effect=record("source")), patch.object(core_ci, "module_inputs", side_effect=record("module")), \
                    patch.object(core_ci, "dotnet_tests", side_effect=record("tests")):
                core_ci.validate("portable", scope=scope)
                self.assertLess(next(i for i, row in enumerate(calls) if row[1][0:1] == ("run-node-test-matrix-with-results",)),
                                next(i for i, row in enumerate(calls) if row[0] == "source"))
                self.assertEqual([("primary-lane", "dotnet-linux"), *[("scenario", name) for name in scenarios]],
                                 [row[1] for row in calls if row[0] == "tests"])
                self.assertEqual(scope != "repository", any(row[1][0:2] == ("npm", "ci") for row in calls))
                self.assertEqual(scope != "repository", any(row[0] == "module" for row in calls))

    def test_toolchain_changes_follow_the_owners_without_rewriting_workflows(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("global.json", "eng/toolchains.json", "eng/release/tooling.json", "languages/csharp/dotnet/Directory.Packages.props"):
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(metadata.ROOT / name, target)
            policy = json.loads((root / "eng/toolchains.json").read_text())
            policy.update(dotnetSdk="11.0.100", node="26.1.0", npm="12.0.0")
            (root / "eng/toolchains.json").write_text(json.dumps(policy))
            with self.assertRaisesRegex(ValueError, "global.json"): metadata.toolchains(root)
            (root / "global.json").write_text(json.dumps(dict(sdk=dict(version="11.0.100"))))
            central = root / "languages/csharp/dotnet/Directory.Packages.props"
            central.write_text('<Project><ItemGroup><PackageVersion Include="Velopack" Version="2.3.4" /></ItemGroup></Project>')
            result = metadata.toolchains(root, packaging=True)
            self.assertEqual(("11.0.100", "26.1.0", "12.0.0", "2.3.4"), tuple(result[key] for key in ("dotnet", "node", "npm", "velopack")))
            (root / "eng/release/tooling.json").write_text("broken App packaging metadata")
            central.write_text("broken packaging dependency metadata")
            self.assertEqual(dict(dotnet="11.0.100", node="26.1.0", npm="12.0.0"), metadata.toolchains(root))
            with self.assertRaises(ValueError): metadata.toolchains(root, packaging=True)

    def test_scope_build_roots_and_native_outputs_follow_component_ownership(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            sdk = core_ci.selection("sdk")
            self.assertIn("Sunder.Runtime.Host.Tests", sdk["projects"])
            self.assertFalse(any("/Sunder.App/" in path or "/Sunder.App.Tests/" in path
                                 or "/Sunder.App.Integration.Tests/" in path for path in sdk["buildProjects"]))
            self.assertTrue(any("/Sunder.Sdk.Avalonia/" in path for path in sdk["buildProjects"]))
            solution = core_ci.build_input(sdk, work)
            self.assertEqual([str(core_ci.ROOT / path) for path in sdk["buildProjects"]],
                             [item.get("Path") for item in core_ci.ET.parse(solution).findall("Project")])
            output = work / "github-output"
            with patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}): metadata.emit(metadata.matrices(scope="sdk"))
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
            self.assertEqual(["true", "true", "false"], [values[name] for name in ("platform_tests", "official_tests", "app_packaging")])
            for scope, expected in (("all", (True, True, True)), ("app", (True, False, True)),
                                    ("host", (True, True, False)), ("cli", (False, False, False)), ("repository", (True, False, True))):
                with self.subTest(scope=scope):
                    matrix = metadata.matrices(scope=scope)
                    self.assertEqual(expected, tuple(matrix[name] for name in ("platform_tests", "official_tests", "app_packaging")))

    def test_native_qualification_and_signed_packaging_use_the_same_platform_set(self):
        native = metadata.platforms()["include"]
        app = metadata.platforms(profile="app")["include"]
        self.assertEqual([row["rid"] for row in native], [row["rid"] for row in app])
        self.assertEqual({f"{os}-{arch}" for os in ("linux", "osx", "win") for arch in ("x64", "arm64")},
                         {row["rid"] for row in native})
        runners = {row["rid"]: row["os"] for row in native}
        for os in ("linux", "osx", "win"):
            self.assertNotEqual(runners[f"{os}-x64"], runners[f"{os}-arm64"])
        # App signing uses the x64 client for both Windows packages.
        self.assertEqual({runners["win-x64"]}, {row["os"] for row in app if row["rid"].startswith("win-")})

    def test_fixture_version_changes_select_current_host_leaf_and_preserve_all_rids(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = root / "fixtures/runtime-backed-worker"
            fixture.mkdir(parents=True)
            (fixture / "sunder.package.json").write_text(json.dumps(dict(id="example.worker", version="3.7.2")))
            rids = [row["rid"] for row in metadata.platforms()["include"]]
            for rid in rids:
                (fixture / "dist/targets" / rid).mkdir(parents=True)
            for rid in rids:
                with self.subTest(rid=rid), patch.object(core_ci, "NODE", root), patch.object(core_ci, "run") as run, \
                     patch.object(core_ci, "target", return_value=Path("cli.dll")), \
                     patch.object(core_ci, "property_value", return_value=rid), patch.dict(os.environ, {}, clear=True):
                    values = core_ci.module_inputs()
                    self.assertEqual(fixture / "dist/example.worker.3.7.2.sunderpkg", values["SUNDER_NODE_MODULE_PACKAGE_PATH"])
                    self.assertEqual(rids, values["SUNDER_NODE_MODULE_EXPECTED_RIDS"].split(","))
                    self.assertEqual(fixture / "dist/targets" / rid, values["SUNDER_NODE_MODULE_LEAF_PATH"])
                    self.assertIn(values["SUNDER_NODE_MODULE_PACKAGE_PATH"], run.call_args.args)
                    # Other target leaves cannot substitute for the executing Host.
                    values["SUNDER_NODE_MODULE_LEAF_PATH"].rmdir()
                    with self.assertRaisesRegex(ValueError, "Missing current-RID"):
                        core_ci.module_inputs()


if __name__ == "__main__": unittest.main()
