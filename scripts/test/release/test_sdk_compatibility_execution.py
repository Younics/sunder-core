"""Collector execution/input-binding regressions over synthetic bytes, not actual RID qualification."""
import copy
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import sdk_compatibility_fixture as fixture
from sdk_compatibility_fixture import execution as e, c


class SdkCompatibilityExecutionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="sunder-owned-lane-fixture-")
        self.addCleanup(temporary.cleanup)
        self.parent = Path(temporary.name)
        self.root = self.parent / "source"
        self.root.mkdir()
        self.outputs = self.parent / "lanes"
        self.qualification = fixture.fixture(self.root)

    def collect(self, lanes=e.LANES):
        fixture.collect(self.root, self.outputs, self.qualification, lanes)

    def verify(self):
        return e.verify_lanes(self.root, self.root, self.root / "packages", self.outputs, "1.1.0", "a" * 40, "123", "2")

    def lane(self, lane="win-x64"):
        path = self.outputs / (e.PREFIX + lane) / "lane.json"
        return path, c.decode(path.read_bytes())

    def test_real_owner_subprocess_binds_portable_source_and_all_six_simulated_native_contexts(self):
        self.collect()
        checked = self.verify()
        self.assertEqual(13, checked["results"])
        identities = set()
        for lane in e.LANES:
            _, value = self.lane(lane)
            self.assertEqual(lane, value["context"]["lane"])
            for item in value["executions"]:
                self.assertEqual(item["before"], item["after"])
                identities.add(item["executionId"])
                self.assertEqual(0, item["exitCode"])
        self.assertEqual(13, len(identities))
        self.assertEqual(1, len(self.lane("portable")[1]["executions"]))
        self.assertEqual(2, len(self.lane("linux-arm64")[1]["executions"]))

    def test_measured_platform_and_workflow_context_reject_relabelled_or_emulated_runners(self):
        environment = dict(GITHUB_REPOSITORY=c.REPOSITORY, GITHUB_REF_PROTECTED="true", GITHUB_SHA="a" * 40,
                           GITHUB_WORKFLOW_SHA="a" * 40, GITHUB_REF="refs/tags/sdk/v1.1.0", GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="1",
                           GITHUB_WORKFLOW_REF=c.REPOSITORY + "/" + c.SDK_WORKFLOW + "@refs/tags/sdk/v1.1.0", GITHUB_EVENT_NAME="push", GITHUB_JOB=e.JOB)
        for rid in e.RIDS:
            prefix, architecture = rid.split("-")
            system = {"win": "Windows", "linux": "Linux", "osx": "Darwin"}[prefix]
            runner = {"win": "Windows", "linux": "Linux", "osx": "macOS"}[prefix]
            with self.subTest(rid=rid), patch.dict(os.environ, dict(environment, RUNNER_OS=runner, RUNNER_ARCH=architecture.upper())), \
                 patch.object(e.platform, "system", return_value=system), patch.object(e.platform, "machine", return_value="x86_64" if architecture == "x64" else "arm64"):
                self.assertEqual(rid, e.lane_context(rid, "1.1.0")["actualRid"])
                with self.assertRaisesRegex(ValueError, "actual OS/RID"):
                    e.lane_context("linux-arm64" if rid != "linux-arm64" else "win-x64", "1.1.0")
                with patch.dict(os.environ, RUNNER_ARCH="ARM64" if architecture == "x64" else "X64"), self.assertRaisesRegex(ValueError, "emulation"):
                    e.lane_context(rid, "1.1.0")
                with patch.dict(os.environ, GITHUB_JOB="build-test-pack"), self.assertRaisesRegex(ValueError, "owning integrated"):
                    e.lane_context(rid, "1.1.0")

    def test_original_input_and_candidate_digests_checked_before_any_command_runs(self):
        plans = e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)
        selected = [next(item for item in plan["inputs"] if item["kind"] == kind)
                    for kind in ("source", "package", "host") for plan in plans if any(item["kind"] == kind for item in plan["inputs"])]
        paths = [self.root / item["path"] for item in selected] + [self.root / "packages" / e.package_names("1.1.0")[0]]
        for index, path in enumerate(paths):
            old = path.read_bytes()
            path.write_bytes(b"substituted original")
            plan = next(plan for plan in plans if any(self.root / item["path"] == path for item in plan["inputs"])) if index < 3 else plans[0]
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "bytes changed"):
                e.bindings(plan, self.root, self.root, self.root / "packages")
            path.write_bytes(old)

    def test_immutable_history_uses_separately_bound_current_candidate_host(self):
        historical = copy.deepcopy(self.qualification["reference"])
        historical.update(id="synthetic-history", version="1.0.0", source=dict(historical["source"], commit="b" * 40))
        for record in [*historical["artifacts"], *(item["snapshot"] for item in historical["surfaces"]), *(item["recipe"] for item in historical["corpus"])]:
            record.update(fixture.put(self.root, record["path"].replace("synthetic-1.1.0", historical["id"]), (self.root / record["path"]).read_bytes()))
        ledger = c.decode((self.root / "supported-releases.json").read_bytes())
        ledger["releases"] = [historical]
        fixture.put(self.root, "supported-releases.json", c.canonical(ledger))
        candidate = dict(id="candidate-host-linux-x64", kind="host", source=self.qualification["reference"]["source"],
                         retainedIdentity="https://example.invalid/synthetic/candidate-host",
                         **fixture.put(self.root, "eng/compatibility/releases/synthetic-1.1.0/candidate-host.zip", b"synthetic current Host bytes"))
        self.qualification["reference"]["artifacts"].append(candidate)
        fixture.put(self.root, "qualification.json", c.canonical(self.qualification))
        self.qualification = fixture.prepare_recipes(self.root)
        plans = e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)
        old = next(plan for plan in plans if plan["reference"]["id"] == "synthetic-history" and plan["corpus"]["kind"] == "old-package-new-host")
        self.assertEqual([candidate], old["candidateHosts"])
        self.assertNotIn(candidate["id"], old["corpus"]["artifactIds"])
        self.assertTrue(all(not plan["candidateHosts"] for plan in plans if plan["corpus"]["kind"] == "old-source-new-sdk"))
        self.collect(("linux-x64",))  # Real subprocess reads each selected candidate-host copy too.
        self.assertEqual(4, len(self.lane("linux-x64")[1]["executions"]))
        (self.root / candidate["path"]).write_bytes(b"substituted Host")
        with self.assertRaisesRegex(ValueError, "bytes changed"):
            e.bindings(old, self.root, self.root, self.root / "packages")
        fixture.put(self.root, candidate["path"], b"synthetic current Host bytes")
        self.qualification = fixture.prepare_recipes(self.root, "mutate-candidate-host")
        with patch.object(e, "reviewed_source"), self.assertRaisesRegex(ValueError, "bytes changed"):
            e.collect_lane(self.root, self.root, self.root / "packages", self.parent / "mutated-host-lane", fixture.context("linux-x64", self.qualification))

    def test_candidate_host_slot_cannot_relabel_an_older_source_or_nonhost(self):
        for kind, source, identifier in (("package", "a" * 40, "candidate-host-linux-x64"),
                                         ("host", "b" * 40, "candidate-host-linux-x64"),
                                         ("host", "b" * 40, "candidate-app-osx-arm64"),
                                         ("host", "a" * 40, "candidate-host-any")):
            qualification = copy.deepcopy(self.qualification)
            qualification["reference"]["artifacts"].append(dict(id=identifier, kind=kind,
                source=dict(qualification["reference"]["source"], commit=source), retainedIdentity="https://example.invalid/synthetic/host",
                **fixture.put(self.root, "eng/compatibility/releases/synthetic-1.1.0/candidate-host.zip", b"synthetic host")))
            fixture.put(self.root, "qualification.json", c.canonical(qualification))
            with self.subTest(kind=kind, source=source, identifier=identifier), self.assertRaisesRegex(ValueError, "Candidate Host slots"):
                e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)

    def test_owner_recipe_templates_use_collector_fixed_commands_and_exact_declarations(self):
        for path in sorted((e.ROOT / "eng/compatibility/recipe-templates").glob("*.json.in")):
            recipe = c.decode(path.read_bytes().replace(b"<reviewed-reference-id>", b"unit-reference").replace(b"<reviewed-corpus-id>", b"unit-corpus"))
            schema = c.decode((e.SCHEMAS / "sdk-corpus-recipe.schema.json").read_bytes())
            e.validate(recipe, schema, schema)
            command = e.command_for(recipe)
            self.assertEqual(["dotnet", "test"], command["argv"][:2])
            self.assertEqual("FullyQualifiedName=" + recipe["requiredTests"][0], command["argv"][command["argv"].index("--filter") + 1])
            self.assertTrue((e.ROOT / recipe["command"]["entryPoint"]).is_file())

    def test_candidate_app_copy_is_source_bound_and_reverified(self):
        candidate = dict(id="candidate-app-osx-arm64", kind="host", source=self.qualification["reference"]["source"],
                         retainedIdentity="https://example.invalid/synthetic/app",
                         **fixture.put(self.root, "eng/compatibility/releases/synthetic-1.1.0/app.zip", b"synthetic App bytes"))
        self.qualification["reference"]["artifacts"].append(candidate)
        fixture.put(self.root, "qualification.json", c.canonical(self.qualification))
        self.collect(("osx-arm64",))
        plans = e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)
        plan = next(item for item in plans if item["corpus"]["kind"] == "old-package-new-host")
        self.assertEqual([candidate], plan["candidateHosts"])
        (self.root / candidate["path"]).write_bytes(b"substituted App")
        with self.assertRaisesRegex(ValueError, "bytes changed"):
            e.bindings(plan, self.root, self.root, self.root / "packages")

    def test_actual_command_cannot_change_copied_source_binary_or_candidate_bytes_or_original_feed(self):
        for mode, lane in (("mutate-source", "portable"), ("mutate-package", "linux-x64"), ("mutate-host", "linux-arm64"),
                           ("mutate-candidate", "portable"), ("mutate-original", "portable")):
            with self.subTest(mode=mode):
                self.qualification = fixture.fixture(self.root, mode)
                output = self.parent / mode
                with patch.object(e, "reviewed_source"), self.assertRaisesRegex(ValueError, "bytes changed"):
                    e.collect_lane(self.root, self.root, self.root / "packages", output, fixture.context(lane, self.qualification))
                self.assertFalse((output / "lane.json").exists())

    def test_unrelated_passing_test_skips_nonzero_exit_timeout_and_linked_report_cannot_emit_evidence(self):
        for mode, message in (("unrelated", "required executed test"), ("skipped", "failed/skipped"), ("exit", "failed or timed out"),
                              ("timeout", "failed or timed out"), ("link-report", "Linked")):
            with self.subTest(mode=mode):
                self.qualification = fixture.fixture(self.root, mode)
                output = self.parent / mode
                with patch.object(e, "reviewed_source"), self.assertRaisesRegex(ValueError, message):
                    e.collect_lane(self.root, self.root, self.root / "packages", output, fixture.context("linux-x64", self.qualification))
                self.assertFalse((output / "lane.json").exists())

    def test_owned_command_cannot_rewrite_its_reviewed_recipe_code_or_collected_context(self):
        for mode, message in (("mutate-context", "input context"), ("mutate-entry", "Owned command"), ("mutate-recipe", "Reviewed recipe")):
            with self.subTest(mode=mode):
                self.qualification = fixture.fixture(self.root, mode)
                output = self.parent / mode
                with patch.object(e, "reviewed_source"), self.assertRaisesRegex(ValueError, message):
                    e.collect_lane(self.root, self.root, self.root / "packages", output, fixture.context("portable", self.qualification))
                self.assertFalse((output / "lane.json").exists())

    def test_recipe_commands_and_arguments_cannot_escape_to_external_drivers(self):
        corpus = self.qualification["reference"]["corpus"][0]
        original = c.decode((self.root / corpus["recipe"]["path"]).read_bytes())
        cases = [(entry, []) for entry in ("../external.py", "/external.py", "scripts/test/../../../external.py", "scripts/test/linked.py")]
        cases += [(fixture.ENTRY, [argument]) for argument in ("../outside", "--output=/escape", "..", "run;other")]
        for entry, arguments in cases:
            value = copy.deepcopy(original)
            value["command"]["entryPoint"] = entry
            value["command"]["arguments"] = arguments
            raw = c.canonical(value)
            corpus["recipe"] = fixture.put(self.root, corpus["recipe"]["path"], raw)
            fixture.put(self.root, e.recipe_path(self.qualification["reference"], corpus), raw)
            fixture.put(self.root, "qualification.json", c.canonical(self.qualification))
            if entry == "scripts/test/linked.py": (self.root / entry).symlink_to(self.root / fixture.ENTRY)
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)

    def test_recipe_and_owned_command_must_be_the_exact_reviewed_git_blobs(self):
        plans = e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)
        def git(*arguments, **_):
            return b"a" * 40 if arguments[1] == "rev-parse" else (self.root / arguments[2].split(":", 1)[1]).read_bytes()
        with patch.object(c, "command", side_effect=git): e.reviewed_source(self.root, "a" * 40, plans)
        with patch.object(c, "command", side_effect=[b"a" * 40, b"different reviewed source"]), self.assertRaisesRegex(ValueError, "exact reviewed Git source"):
            e.reviewed_source(self.root, "a" * 40, plans)
        corpus = self.qualification["reference"]["corpus"][0]
        retained = self.root / corpus["recipe"]["path"]
        changed = c.decode(retained.read_bytes())
        changed["requiredTests"] = ["unrelated.test"]
        raw = c.canonical(changed)
        corpus["recipe"] = fixture.put(self.root, corpus["recipe"]["path"], raw)
        fixture.put(self.root, "qualification.json", c.canonical(self.qualification))
        with self.assertRaisesRegex(ValueError, "immutable reviewed source"):
            e.load_plans(self.root, self.root, self.root / "packages", "1.1.0", "a" * 40)

    def test_mislabelled_reused_and_changed_binding_receipts_are_rejected(self):
        self.collect()
        path, original = self.lane()
        for field, value in (("operatingSystem", "Linux"), ("architecture", "ARM64"), ("actualRid", "linux-x64"),
                             ("lane", "osx-x64"), ("artifactSourceCommit", "b" * 40), ("runId", "999"), ("runAttempt", "3"), ("job", "another-job")):
            changed = copy.deepcopy(original)
            changed["context"][field] = value
            path.write_bytes(c.canonical(changed))
            with self.subTest(field=field), self.assertRaises(ValueError): self.verify()
        for field in original["executions"][0]["before"]:
            changed = copy.deepcopy(original)
            changed["executions"][0]["before"][field] = changed["executions"][0]["after"][field] = "e" * 64
            path.write_bytes(c.canonical(changed))
            with self.subTest(binding=field), self.assertRaisesRegex(ValueError, "not bound"): self.verify()
        changed = copy.deepcopy(original)
        changed["executions"][0]["executionId"] = self.lane("portable")[1]["executions"][0]["executionId"]
        path.write_bytes(c.canonical(changed))
        with self.assertRaisesRegex(ValueError, "reused"): self.verify()
        path.write_bytes(c.canonical(original))
        self.verify()

    def test_valid_xml_attribute_injection_and_unrelated_report_fail_even_if_report_hashes_are_updated(self):
        self.collect()
        lane_path, lane = self.lane("portable")
        item = lane["executions"][0]
        report = lane_path.parent / item["report"]["path"]
        original = report.read_bytes()
        for data in (original.replace(b'outcome="Passed"', b'''outcome="Failed" note='outcome="Passed"' '''),
                     original.replace(b'name="old_source_new_sdk"', b'''name="Unrelated" note='name="old_source_new_sdk"' ''')):
            report.write_bytes(data)
            item["report"] = e.file_record(report, item["report"]["path"])
            item["resultsSha256"] = c.digest(c.canonical(e.compatibility.validate_report(data, "trx")))
            lane_path.write_bytes(c.canonical(lane))
            with self.assertRaisesRegex(ValueError, "failed/skipped|required executed test"): self.verify()

    def test_missing_native_lane_or_corpus_and_path_redirection_fail_closed(self):
        self.collect()
        path, lane = self.lane("osx-arm64")
        path.unlink()
        with self.assertRaises((ValueError, OSError)): self.verify()
        path.write_bytes(c.canonical(lane))
        lane["executions"].pop()
        path.write_bytes(c.canonical(lane))
        with self.assertRaisesRegex(ValueError, "Missing independently collected"): self.verify()
        for name in ("../outside", "/outside", "a/../outside", "a\\outside"):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Unsafe"): e.safe_file(self.root, name)
        (self.root / "linked").symlink_to(self.root / "qualification.json")
        with self.assertRaisesRegex(ValueError, "Linked"): e.safe_file(self.root, "linked")
        with self.assertRaisesRegex(ValueError, "independent directory"):
            e.collect_lane(self.root, self.root, self.root / "packages", self.root / "unsafe-output", fixture.context("portable", self.qualification))


if __name__ == "__main__": unittest.main()
