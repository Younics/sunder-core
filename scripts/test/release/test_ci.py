import json
from contextlib import nullcontext
import os
from pathlib import Path
import shutil
import sys
import tempfile
from types import SimpleNamespace
import unittest
import subprocess
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "ci"))
import metadata
import core_ci
import report
import bash_runtime
import linux_secret_service


class CiMetadataTests(unittest.TestCase):
    def test_tool_launchers_preserve_argv_without_cmd_or_unqualified_bash(self):
        argument = 'path with spaces & | %PATH% $HOME "quotes"'
        with patch.object(core_ci.subprocess, "run") as run:
            core_ci.run("npm", "pack", argument)
            self.assertEqual(["node", str(core_ci.ROOT / "scripts/npm-command.mjs"), "pack", argument], run.call_args.args[0])
            core_ci.run("bash", "fixture.sh", argument)
            self.assertEqual(bash_runtime.command("fixture.sh", argument), run.call_args.args[0])
            self.assertNotIn("shell", run.call_args.kwargs)

    def test_native_bash_rejects_invalid_explicit_overrides_without_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "bash.exe"
            path.write_bytes(b"fixture")
            environment = {bash_runtime.VARIABLE: str(path)}
            with patch.object(bash_runtime.shutil, "which", side_effect=AssertionError("No fallback")), \
                    patch.object(bash_runtime.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="5.2\nlinux-gnu\n", stderr="")):
                with self.assertRaisesRegex(ValueError, "native Windows Bash"):
                    bash_runtime.executable(environment, windows=True)
                self.assertEqual(str(path.resolve()), bash_runtime.executable(environment, windows=False))
            with self.assertRaisesRegex(ValueError, bash_runtime.VARIABLE):
                bash_runtime.executable({bash_runtime.VARIABLE: "relative-bash"}, windows=True)

    def test_native_bash_uses_git_installation_and_binds_nested_process_environment(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            git = root / "cmd/git.exe"
            bash = root / "bin/bash.exe"
            git.parent.mkdir(); bash.parent.mkdir()
            git.write_bytes(b"fixture"); bash.write_bytes(b"fixture")
            with patch.object(bash_runtime.shutil, "which", return_value=str(git)) as which, \
                    patch.object(bash_runtime.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="5.2\nmsys\n", stderr="")):
                self.assertEqual(str(bash.resolve()), bash_runtime.executable({"PATH": "fixture"}, windows=True))
                which.assert_called_once_with("git", path="fixture")
            arguments = ["fixture.sh", "a & | %PATH% $HOME", ""]
            with patch.object(bash_runtime, "executable", return_value=str(bash)), \
                    patch.object(bash_runtime.subprocess, "run", return_value=SimpleNamespace(returncode=37)) as run:
                self.assertEqual(37, bash_runtime.main(["--run", *arguments]))
                self.assertEqual([str(bash), "--noprofile", "--norc", *arguments], run.call_args.args[0])
                self.assertEqual(str(bash), run.call_args.kwargs["env"][bash_runtime.VARIABLE])

    def test_native_bash_bridge_preserves_environment_case_unicode_and_argument_bytes(self):
        # The Windows Python bridge must not uppercase Bash variables or advance
        # through its UTF-16 environment block by Unicode code-point counts.
        value = 'Unicode-😀 & | %PATH% $HOME "quoted"'
        environment = {**os.environ, "sunder_case_probe": value, "zz_sunder_after_probe": "after supplementary character"}
        arguments = [value, ""]
        result = subprocess.run(bash_runtime.command("-c",
            r'printf "%s\0" "$sunder_case_probe" "$zz_sunder_after_probe" "$1" "$2"', "fixture", *arguments),
            env=environment, capture_output=True, timeout=20)
        self.assertEqual(0, result.returncode, result.stderr.decode("utf-8", errors="replace"))
        self.assertEqual([value, environment["zz_sunder_after_probe"], *arguments, ""], result.stdout.decode("utf-8").split("\0"))

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
                patch.object(core_ci, "execution_evidence", side_effect=lambda *args: nullcontext()), \
                 patch.object(core_ci, "selection", return_value={"components": ["dotnet"]}), \
                patch.object(core_ci, "templates", side_effect=templates), patch.object(core_ci, "dotnet_tests", side_effect=consume):
            with self.assertRaisesRegex(RuntimeError, "test failure"): core_ci.validate("candidate")
            self.assertNotIn("SUNDER_TEST_LOCAL_INPUT", os.environ)
        self.assertFalse(work[0].exists())

    def test_template_sdk_resolution_and_restore_share_discoverable_candidate_feed_and_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            work, feed, output = root / "consumers", root / "feed", root / "packages"
            for path in (work, feed, output, root / "eng/test-oracles", root / "eng/platform-contracts"):
                path.mkdir(parents=True, exist_ok=True)
            variants = {"dotnet": [{"name": "headless", "options": [], "platformContract": "sample.contract"}], "node": []}
            (root / "eng/test-oracles/template-smoke.json").write_text(json.dumps(variants))
            (root / "eng/platform-contracts/sunder-contracts.owned.json").write_text(json.dumps({
                "contracts": {"sample.contract": {"current": {"catalogRelease": {"releaseVersion": "1.1.0"}}}}}))
            names = ["Sunder.Package.Templates", "@younics/sunder-sdk", "@younics/create-sunder-package"]
            (feed / "test-feed.json").write_text(json.dumps({"packages": [{"name": name, "archive": str(index)} for index, name in enumerate(names)]}))
            for index in range(len(names)):
                (feed / str(index)).write_bytes(b"retained candidate")
            evaluations = []

            def run(*args, **kwargs):
                if "export" in args:
                    Path(args[args.index("--output") + 1]).write_text("catalog")
                if args[:3] == ("dotnet", "new", "sunder-package"):
                    sample = Path(args[args.index("--output") + 1])
                    sample.mkdir()
                    (sample / "Smoke.headless.csproj").write_text('<Project Sdk="Sunder.Package.Build/1.1.0"/>')
                if args[:2] not in (("dotnet", "restore"), ("dotnet", "build"), ("dotnet", "msbuild")):
                    return
                path = Path(args[2])
                self.assertEqual(path.parent, kwargs.get("cwd"))
                environment = kwargs["env"]
                self.assertEqual(str(work / "template-cli-home"), environment["DOTNET_CLI_HOME"])
                self.assertEqual(str(work / "template-packages"), environment["NUGET_PACKAGES"])
                config = work / "NuGet.Config"
                document = core_ci.ET.parse(config)
                self.assertEqual(str(feed), document.find("./packageSources/add[@key='candidate']").get("value"))
                self.assertEqual("Sunder.*", document.find("./packageSourceMapping/packageSource[@key='candidate']/package").get("pattern"))
                if args[1] == "restore":
                    self.assertEqual(str(config), str(args[args.index("--configfile") + 1]))
                evaluations.append(args)
                if "-t:PackSunderPackage" in args:
                    (path.parent / "smoke.headless.1.0.0.sunderpkg").write_bytes(b"validated output")
                if kwargs.get("capture"):
                    return str(path.parent)

            with patch.object(core_ci, "ROOT", root), patch.object(core_ci, "target", side_effect=lambda name: root / (name + ".dll")), \
                    patch.object(core_ci, "run", side_effect=run), patch.dict(os.environ, {}, clear=True):
                result = core_ci.templates(work, feed, output)
            self.assertEqual(5, len(evaluations))  # restore, platform import, build, pack, evaluated output
            self.assertEqual(str(work / "dotnet-template-headless/sunder-dev"), result["SUNDER_DOTNET_TEMPLATE_DEV_PATH"])

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
        for scope, scenarios in (("all", ["interop", "app-interop"]), ("sdk", ["interop"]), ("repository", [])):
            calls.clear()
            with self.subTest(scope=scope), patch.object(core_ci, "run", side_effect=record("run")), \
                    patch.object(core_ci, "execution_evidence", side_effect=lambda *args: nullcontext()), \
                    patch.object(core_ci, "oracle", side_effect=record("oracle")), \
                    patch.object(core_ci, "source", side_effect=record("source")), patch.object(core_ci, "module_inputs", side_effect=record("module")), \
                    patch.object(core_ci, "dotnet_tests", side_effect=record("tests")):
                core_ci.validate("portable", scope=scope)
                fixtures = [i for i, row in enumerate(calls) if row[1][0:1] == ("run-release-fixtures-with-results",)]
                self.assertEqual(1, len(fixtures))
                self.assertLess(fixtures[0], next(i for i, row in enumerate(calls) if row[1][0:1] == ("run-node-test-matrix-with-results",)))
                self.assertLess(next(i for i, row in enumerate(calls) if row[1][0:1] == ("run-node-test-matrix-with-results",)),
                                next(i for i, row in enumerate(calls) if row[0] == "source"))
                self.assertEqual([*([("primary-lane", "dotnet-linux")] if scope != "repository" else []), *[("scenario", name) for name in scenarios]],
                                 [row[1] for row in calls if row[0] == "tests"])
                self.assertTrue(any(row[1][0:2] == ("npm", "ci") for row in calls))
                self.assertEqual(scope != "repository", any(row[0] == "module" for row in calls))

    def test_source_build_failure_retains_diagnostics_before_dotnet_tests_start(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            calls = []
            def run(*args, **kwargs):
                calls.append(args)
                if args[:2] == ("dotnet", "build"): raise subprocess.CalledProcessError(1, args)
            with patch.object(core_ci, "ROOT", root), patch.object(core_ci, "selection", return_value={"scope": "cli", "components": ["cli"], "buildProjects": ["cli.csproj"]}), \
                    patch.object(core_ci, "run", side_effect=run):
                with self.assertRaises(subprocess.CalledProcessError): core_ci.source("cli", root)
            diagnostics = root / "artifacts/test-results/cli/build"
            self.assertEqual(0, json.loads((diagnostics / "restore.process.json").read_text())["status"])
            self.assertEqual(1, json.loads((diagnostics / "build.process.json").read_text())["status"])
            self.assertTrue(all(any(str(arg).endswith(".binlog") for arg in call) for call in calls))

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
                                     ("host", (True, True, False)), ("cli", (False, False, False)), ("repository", (False, False, False))):
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
        for scope, expected in (("sdk", {"main", "package-build-1", "package-build-2", "package-build-3"}), ("repository", {"main"})):
            rows = metadata.matrices(scope=scope)["platform"]["include"]
            self.assertEqual(len(native) * len(expected), len(rows))
            self.assertEqual(len(rows), len({(row["rid"], row["artifact_suffix"]) for row in rows}))
            for row in native:
                self.assertEqual(expected, {item["partition"] for item in rows if item["rid"] == row["rid"]})
                self.assertTrue(all(item["os"] == row["os"] for item in rows if item["rid"] == row["rid"]))
        matrix = metadata.component_matrices()
        self.assertEqual({"npm", "dotnet", "host", "app", "cli", "repository"}, set(matrix["component_scopes"]))
        platform = matrix["platform"]["include"]
        self.assertEqual(36, len(platform))
        self.assertEqual(len(platform), len({(row["scope"], row["rid"], row["partition"]) for row in platform}))
        consumers = matrix["consumers"]["include"]
        self.assertEqual(6, sum(row["task"] == "official" for row in consumers))
        self.assertEqual(2, sum(row["task"] == "packaging" for row in consumers))
        for scope in ("app", "host", "cli", "repository"):
            narrowed = metadata.component_matrices(scope=scope)
            self.assertEqual([scope], narrowed["component_scopes"])
            self.assertTrue(all(row["scope"] == scope for row in narrowed["platform"]["include"] + narrowed["consumers"]["include"]))
            self.assertFalse(narrowed["interop"])

    def test_native_partition_preparation_routes_side_scenarios_to_main_and_cleans_environment(self):
        selected = {"components": ["dotnet", "host"], "nativePartitions": ["main", "package-build-1"]}
        for partition in selected["nativePartitions"]:
            calls = []
            with self.subTest(partition=partition), patch.object(core_ci, "sys", SimpleNamespace(platform="win32")), \
                    patch.object(core_ci, "selection", return_value=selected), \
                    patch.object(core_ci, "os", SimpleNamespace(name="nt", environ=os.environ)), \
                    patch.object(core_ci, "execution_evidence", side_effect=lambda *args: nullcontext()), \
                    patch.object(core_ci, "node_inputs"), patch.object(core_ci, "build_input", return_value="fixture.slnx"), \
                    patch.object(core_ci, "run") as run, patch.dict(os.environ, {}, clear=True), \
                    patch.object(core_ci, "dotnet_tests", side_effect=lambda *args, **kwargs: calls.append((args, kwargs, dict(os.environ)))):
                core_ci.validate("platform", scope="sdk", partition=partition)
                self.assertEqual(("native-partition", partition), calls[0][0])
                self.assertEqual("platform/" + partition, calls[0][1]["directory"])
                self.assertTrue(calls[0][1]["build"])
                self.assertEqual("1", calls[0][2].get("SUNDER_REQUIRE_TEST_PREREQUISITES"))
                self.assertEqual(partition != "main", calls[0][2].get("SUNDER_TEST_DIAGNOSTICS") == "1")
                self.assertNotIn("SUNDER_TEST_DIAGNOSTICS", os.environ)
                self.assertNotIn("SUNDER_REQUIRE_TEST_PREREQUISITES", os.environ)
                if partition == "main":
                    self.assertEqual(("scenario", "output-lock"), calls[1][0])
                    self.assertTrue(calls[1][1]["build"])
                else: self.assertEqual(1, len(calls))
                with self.assertRaisesRegex(ValueError, "Unknown native partition"):
                    core_ci.validate("platform", scope="sdk", partition="../unowned")
                self.assertEqual(1, run.call_count)

    def test_linux_host_main_requires_private_keystore_without_affecting_other_owners(self):
        for components, partition in ((["host"], None), (["host"], "main"), (["dotnet", "host"], "package-build-1"), (["app"], "main")):
            with self.subTest(components=components, partition=partition), \
                    patch.object(core_ci, "sys", SimpleNamespace(platform="linux")), \
                    patch.object(core_ci, "selection", return_value={"components": components, "nativePartitions": ["main", "package-build-1"]}), \
                    patch.object(core_ci, "execution_evidence", side_effect=lambda *args: nullcontext()), \
                    patch.object(core_ci, "node_inputs"), patch.object(core_ci, "build_input", return_value="fixture.slnx"), \
                    patch.object(core_ci, "run"), patch.dict(os.environ, {}, clear=True), \
                    patch.object(core_ci, "dotnet_tests") as tests:
                core_ci.validate("platform", scope="host", partition=partition)
                self.assertEqual("host" in components and partition in (None, "main"), tests.call_args_list[0].kwargs.get("linux_keystore", False))
                self.assertEqual("app" in components and partition in (None, "main"), tests.call_args_list[0].kwargs.get("linux_display", False))
                self.assertNotIn("SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS", os.environ)

    def test_private_linux_secret_service_is_isolated_and_reaps_daemons_on_test_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            processes, environments, calls = [], [], []
            def start(command, **kwargs):
                process = Mock()
                process.poll.return_value = None
                processes.append(process)
                environments.append(dict(kwargs["env"]))
                calls.append(command)
                if "--session" in command:
                    Path(next(arg.removeprefix("--address=unix:path=") for arg in command if arg.startswith("--address="))).touch()
                return process
            original = dict(HOME=str(root / "original-home"), DBUS_SESSION_BUS_ADDRESS="do-not-use-desktop",
                            DBUS_STARTER_ADDRESS="do-not-use-starter", GNOME_KEYRING_CONTROL="do-not-use-control", RUNNER_TEMP=temporary)
            with patch.dict(os.environ, original, clear=True), \
                    patch.object(linux_secret_service, "sys", SimpleNamespace(platform="linux")), \
                    patch.object(linux_secret_service.shutil, "which", side_effect=lambda name: "native/" + name), \
                    patch.object(linux_secret_service.subprocess, "Popen", side_effect=start), \
                    patch.object(linux_secret_service.subprocess, "run") as run:
                with self.assertRaisesRegex(RuntimeError, "test failed"):
                    with linux_secret_service.session(root / "keystore.log"):
                        self.assertNotEqual(original["DBUS_SESSION_BUS_ADDRESS"], os.environ["DBUS_SESSION_BUS_ADDRESS"])
                        self.assertEqual(original["HOME"], os.environ["HOME"])
                        self.assertEqual("1", os.environ["SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS"])
                        self.assertNotIn("GNOME_KEYRING_CONTROL", os.environ)
                        self.assertNotIn("DBUS_STARTER_ADDRESS", os.environ)
                        data = Path(os.environ["XDG_DATA_HOME"])
                        self.assertTrue(data.is_dir())
                        raise RuntimeError("test failed")
                self.assertEqual(original, dict(os.environ))
                self.assertFalse(data.exists())
                self.assertEqual(2, len(processes))
                self.assertTrue(all(env["XDG_DATA_HOME"] == str(data) for env in environments))
                for process in processes:
                    process.terminate.assert_called_once()
                    process.wait.assert_called_once_with(timeout=5)
                self.assertIn("--nofork", calls[0])
                self.assertIn("--foreground", calls[1])
                self.assertIn("--components=secrets", calls[1])
                self.assertNotIn("--replace", calls[1])
                processes[1].stdin.write.assert_called_once_with(b"sunder-ci-only")
                processes[1].stdin.close.assert_called_once()
                self.assertEqual(["call", "wait"], [call.args[0][1] for call in run.call_args_list])
                self.assertNotIn("--activate", run.call_args.args[0])
                self.assertTrue(all(call.kwargs["check"] and call.kwargs["timeout"] == 15 for call in run.call_args_list))

    def test_private_linux_secret_service_cannot_fall_back_on_missing_tools_or_failed_startup(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(linux_secret_service, "sys", SimpleNamespace(platform="linux")):
            log = Path(temporary) / "keystore.log"
            with patch.object(linux_secret_service.shutil, "which", return_value=None), \
                    patch.object(linux_secret_service.subprocess, "Popen") as start:
                with self.assertRaisesRegex(ValueError, "Missing required Linux Secret Service"):
                    with linux_secret_service.session(log): self.fail("Must not run tests")
                start.assert_not_called()
            process = Mock()
            process.poll.return_value = 1
            before = dict(os.environ)
            with patch.object(linux_secret_service.shutil, "which", side_effect=lambda name: "native/" + name), \
                    patch.object(linux_secret_service.subprocess, "Popen", return_value=process) as start:
                with self.assertRaisesRegex(RuntimeError, "Private D-Bus did not start"):
                    with linux_secret_service.session(log): self.fail("Must not run tests")
                self.assertEqual(1, start.call_count)
                process.wait.assert_called_once_with(timeout=5)
                self.assertEqual(before, dict(os.environ))
            # A wedged startup must be killed/reaped, not leak a daemon or fall
            # back to the desktop service. The deadline is a fixture, not a sleep.
            process.reset_mock()
            process.poll.return_value = None
            process.wait.side_effect = [subprocess.TimeoutExpired("private-dbus", 5), 0]
            with patch.object(linux_secret_service.shutil, "which", side_effect=lambda name: "native/" + name), \
                    patch.object(linux_secret_service.subprocess, "Popen", return_value=process), \
                    patch.object(linux_secret_service.time, "monotonic", side_effect=[0, 11]):
                with self.assertRaisesRegex(RuntimeError, "Private D-Bus did not start"):
                    with linux_secret_service.session(log): self.fail("Must not run tests")
                process.terminate.assert_called_once()
                process.kill.assert_called_once()
                self.assertEqual(2, process.wait.call_count)
                self.assertEqual(before, dict(os.environ))

    def test_linux_keystore_session_surrounds_test_execution_and_does_not_hide_failure(self):
        calls = []
        from contextlib import contextmanager
        @contextmanager
        def session(path):
            calls.append(("start", path))
            try: yield
            finally: calls.append(("stop", path))
        def oracle(name, *args):
            calls.append((name, args))
            if name == "run-dotnet-tests-with-results": raise RuntimeError("test failed")
        with patch.object(core_ci.linux_secret_service, "session", side_effect=session), patch.object(core_ci, "oracle", side_effect=oracle):
            with self.assertRaisesRegex(RuntimeError, "test failed"):
                core_ci.dotnet_tests("native-partition", "main", scope="host", directory="platform/main", linux_keystore=True)
        self.assertEqual(["start", "run-dotnet-tests-with-results", "stop", "summarize-core-test-results"], [call[0] for call in calls])
        self.assertEqual("main-secret-service.log", calls[0][1].name)

    def test_linux_app_runs_its_existing_test_owner_under_a_private_display_and_bus(self):
        with patch.object(core_ci, "run") as run:
            core_ci.dotnet_tests("native-partition", "main", scope="app", linux_display=True)
        arguments = run.call_args_list[0].args
        self.assertEqual(("dbus-run-session", "--", "xvfb-run", "--auto-servernum", "--server-args=-screen 0 1280x1024x24", "node"), arguments[:6])
        self.assertEqual("--native-partition", arguments[arguments.index("--scope") + 2])
        self.assertEqual("node", run.call_args_list[1].args[0])

    def test_job_labels_follow_actual_tasks_and_policy_partition_counts(self):
        ordinary = metadata.component_matrices()
        self.assertEqual({"Test Node packages", "Test .NET packages", "Test Host", "Test CLI", "Test App", "Check release tooling"},
                         {row["job_name"] for row in ordinary["components"]["include"]})
        release = metadata.component_matrices(release_packages=True)
        self.assertEqual(ordinary, release)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("eng/ci/platforms.json", "eng/test-oracles/core-test-policy.json"):
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(metadata.ROOT / name, path)
            path = root / "eng/test-oracles/core-test-policy.json"
            policy = json.loads(path.read_text())
            policy["nativePartitions"][0]["shards"] = 5
            path.write_text(json.dumps(policy))
            rows = metadata.component_matrices(root)["platform"]["include"]
            self.assertEqual(len(rows), len({row["job_name"] for row in rows}))
            self.assertEqual(6, sum("Test package builds" in row["job_name"] and row["job_name"].endswith("shard 5/5") for row in rows))
            self.assertFalse(any(row["job_name"].endswith("/3") for row in rows))

    def test_paginated_job_links_keep_attempt_and_duration_without_slurp_jq_conflict(self):
        env = dict(GITHUB_REPOSITORY="Younics/sunder-core", GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="2")
        job = dict(id=1, name="App — Process tests — linux-x64", html_url="https://github.com/Younics/sunder-core/actions/runs/123/job/1",
                   status="completed", conclusion="success", started_at="2026-11-07T10:00:00Z", completed_at="2026-11-07T10:02:13Z")
        pages = [dict(jobs=[job]), dict(jobs=[dict(job, id=2, name="Host — Process tests — linux-x64")])]
        with patch.dict(os.environ, env), patch.object(report.subprocess, "check_output", return_value=json.dumps(pages).encode()) as gh:
            rows = report.collect_jobs()
            self.assertEqual(2, len(rows))
            self.assertEqual("2m 13s", rows[0]["duration"])
            args = gh.call_args.args[0]
            self.assertIn("--slurp", args)
            self.assertNotIn("--jq", args)
            self.assertIn("/attempts/2/jobs", args[-1])
            self.assertIn(job["html_url"], "\n".join(report.job_table(rows)))
            gh.side_effect = subprocess.CalledProcessError(1, "gh")
            self.assertEqual([], report.collect_jobs())

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
                with self.subTest(rid=rid), patch.object(core_ci, "run", return_value=json.dumps({"nodeRoot": str(root)})) as run, \
                     patch.object(core_ci, "target", return_value=Path("cli.dll")), \
                     patch.object(core_ci, "property_value", return_value=rid), patch.dict(os.environ, {}, clear=True):
                    values = core_ci.module_inputs()
                    self.assertEqual(fixture / "dist/example.worker.3.7.2.sunderpkg", values["SUNDER_NODE_MODULE_PACKAGE_PATH"])
                    self.assertEqual(rids, values["SUNDER_NODE_MODULE_EXPECTED_RIDS"].split(","))
                    self.assertEqual(fixture / "dist/targets" / rid, values["SUNDER_NODE_MODULE_LEAF_PATH"])
                    self.assertEqual(root, values["SUNDER_NODE_MODULE_WORKSPACE_PATH"])
                    self.assertEqual(str(root), os.environ["SUNDER_NODE_MODULE_WORKSPACE_PATH"])
                    self.assertIn(values["SUNDER_NODE_MODULE_PACKAGE_PATH"], run.call_args.args)
                    self.assertEqual(("node", core_ci.ROOT / "scripts/test-oracles/node-test-workspace.mjs"), run.call_args_list[0].args[:2])
                    self.assertEqual(("npm", "--prefix", root, "run", "build"), run.call_args_list[1].args)
                    # Other target leaves cannot substitute for the executing Host.
                    values["SUNDER_NODE_MODULE_LEAF_PATH"].rmdir()
                    with self.assertRaisesRegex(ValueError, "Missing current-RID"):
                        core_ci.module_inputs()


if __name__ == "__main__": unittest.main()
