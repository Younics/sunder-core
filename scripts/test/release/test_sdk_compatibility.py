"""Synthetic compatibility retention/confirmation fixtures; no real release qualification."""
import copy
import io
import os
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/compatibility"))
sys.path.insert(0, str(ROOT / "scripts/release"))
import sdk_release_compatibility as compatibility
import release_coordinates as c
import github_release as assets

SOURCE, BASE = "a" * 40, "b" * 40


class SdkCompatibilityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="sunder-sdk-qualification-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def archive(self, entries):
        path = self.root / compatibility.ASSET
        with tarfile.open(path, "w", format=tarfile.USTAR_FORMAT) as archive:
            for name, data, kind in entries:
                info = tarfile.TarInfo(name)
                info.size = len(data)
                info.type = kind
                if kind == tarfile.SYMTYPE: info.linkname = "outside"
                archive.addfile(info, io.BytesIO(data))
        return path

    def test_missing_real_candidate_fails_before_context_or_remote_operations(self):
        with patch.object(compatibility, "context") as context:
            with self.assertRaisesRegex(ValueError, "qualification missing"):
                compatibility.prepare(self.root, self.root)
            context.assert_not_called()

    def test_candidate_bundled_execution_claims_cannot_replace_independent_lane_transport(self):
        candidate = self.root / "candidate"
        bundle = candidate / "compatibility"
        bundle.mkdir(parents=True)
        (bundle / "qualification.json").write_bytes(b"{}")
        for supplied in (None, candidate, bundle, self.root):
            with self.subTest(supplied=supplied), patch.object(compatibility, "context") as context:
                with self.assertRaisesRegex(ValueError, "independent same-run owning-lane"):
                    compatibility.prepare(candidate, self.root, supplied)
                context.assert_not_called()

    def test_report_structure_and_summary_cannot_hide_failed_or_missing_results(self):
        self.assertEqual([dict(selector="synthetic.py::case", outcome="Passed")], compatibility.validate_report(
            b'<testsuite tests="1"><testcase file="synthetic.py" name="case"/></testsuite>', "junit"))
        for raw in (b'<testsuite><testcase/></unclosed>', b'<testsuite tests="0"><testcase/></testsuite>',
                    b'<testsuite failures="1"><testcase/></testsuite>', b'<testsuite><system-out><testcase/></system-out></testsuite>',
                    b'<testsuite/>', b'<!DOCTYPE testsuite><testsuite><testcase/></testsuite>'):
            with self.subTest(raw=raw), self.assertRaises((ValueError, compatibility.ET.ParseError)):
                compatibility.validate_report(raw, "junit")
        with self.assertRaisesRegex(ValueError, "summary"):
            compatibility.validate_report(b'<TestRun><UnitTestResult/><ResultSummary outcome="Failed"/></TestRun>', "trx")

    def test_real_xml_attributes_are_authoritative_without_summary_or_counters(self):
        raw = b'''<TestRun><TestDefinitions><UnitTest id="1"><TestMethod className="Actual" name="Case" note='className="Spoofed" name="Success"'/></UnitTest></TestDefinitions>
          <Results><UnitTestResult testId="1" executionId="run" outcome="Failed" note='outcome="Passed"'/></Results></TestRun>'''
        self.assertEqual([dict(selector="Actual.Case", outcome="Failed")], compatibility.validate_report(raw, "trx"))
        raw = raw.replace(b'outcome="Failed" note=', b'outcome="NotExecuted" note=')
        self.assertEqual("Skipped", compatibility.validate_report(raw, "trx")[0]["outcome"])

    def test_regular_container_readback_rejects_traversal_links_duplicates_extensions_and_truncation(self):
        for entries in [
            [("../escape", b"x", tarfile.REGTYPE)],
            [("/absolute", b"x", tarfile.REGTYPE)],
            [("a/./b", b"x", tarfile.REGTYPE)],
            [("linked", b"", tarfile.SYMTYPE)],
            [("linked", b"", tarfile.LNKTYPE)],
            [("pax", b"x", tarfile.XHDTYPE)],
            [("a", b"x", tarfile.REGTYPE), ("A", b"x", tarfile.REGTYPE)],
        ]:
            with self.subTest(entries=entries), tempfile.TemporaryDirectory() as output:
                with self.assertRaises(ValueError): compatibility.unpack(self.archive(entries), Path(output))
        path = self.archive([("reports/result.xml", b"synthetic", tarfile.REGTYPE)])
        with tempfile.TemporaryDirectory() as output:
            self.assertEqual({"reports/result.xml"}, compatibility.unpack(path, Path(output)))
            self.assertEqual(b"synthetic", (Path(output) / "reports/result.xml").read_bytes())
        path.write_bytes(path.read_bytes()[:515])
        with tempfile.TemporaryDirectory() as output, self.assertRaises(ValueError): compatibility.unpack(path, Path(output))

    def test_container_size_inventory_and_trailing_data_are_bounded(self):
        path = self.archive([("one", b"x", tarfile.REGTYPE), ("two", b"x", tarfile.REGTYPE)])
        for bound in ("MAX_FILES", "MAXIMUM"):
            with tempfile.TemporaryDirectory() as output, patch.object(compatibility, bound, 1):
                with self.assertRaises(ValueError): compatibility.unpack(path, Path(output))
        path.write_bytes(path.read_bytes() + b"trailing")
        with tempfile.TemporaryDirectory() as output, self.assertRaisesRegex(ValueError, "Trailing"):
            compatibility.unpack(path, Path(output))

    def test_verify_requires_stage_hash_and_trusted_history_and_rechecks_gate(self):
        base = b'{"synthetic":"base"}'
        path = self.archive([("base-supported-releases.json", base, tarfile.REGTYPE)])
        invocation = dict(workflowSha=SOURCE, runId="1", runAttempt="1")
        checked = dict(files=["base-supported-releases.json"], executionFiles=[])
        with patch.object(compatibility, "context", return_value=(invocation, "1.1.0", BASE)), \
             patch.object(compatibility, "gate", return_value=checked) as gate:
            compatibility.verify(self.root, compatibility.identity(path), c.digest(base))
            self.assertEqual(1, gate.call_count)
            for payload_hash, history_hash in (("0" * 64, c.digest(base)), (compatibility.identity(path), "0" * 64)):
                with self.assertRaises(ValueError): compatibility.verify(self.root, payload_hash, history_hash)
            gate.return_value = dict(files=["base-supported-releases.json", "missing"], executionFiles=[])
            with self.assertRaisesRegex(ValueError, "container files"):
                compatibility.verify(self.root, compatibility.identity(path), c.digest(base))

    def test_prepare_rechecks_deterministic_retained_bytes_before_emitting_stage_coordinates(self):
        candidate = self.root / "candidate"
        bundle = candidate / "compatibility"
        bundle.mkdir(parents=True)
        ledger = (ROOT / compatibility.MANIFEST).read_bytes()
        values = {"base-supported-releases.json": ledger, "supported-releases.json": ledger,
                  "qualification.json": b'{"synthetic":"receipt"}', "retained.bin": b"tested-original"}
        for name, data in values.items(): (bundle / name).write_bytes(data)
        (bundle / "unselected.txt").write_bytes(b"must not be retained")
        executions = self.root / "independent-lanes"
        executions.mkdir()
        (executions / "synthetic.json").write_bytes(b"synthetic owning-lane evidence")
        invocation = dict(workflowSha=SOURCE, runId="123", runAttempt="1")
        digests = []
        for attempt in ("first", "retry"):
            directory = self.root / attempt
            directory.mkdir()
            with patch.object(compatibility, "context", return_value=(invocation, "1.1.0", BASE)), \
                 patch.object(c, "approved_source"), patch.object(c, "command", side_effect=[b"commit", ledger, ledger]), \
                 patch.object(compatibility.subprocess, "run"), \
                 patch.object(compatibility, "gate", return_value=dict(files=sorted(values), executionFiles=["synthetic.json"])) as gate, \
                 patch.dict(os.environ, GITHUB_OUTPUT=str(self.root / "outputs")):
                compatibility.prepare(candidate, directory, executions)
                self.assertEqual(2, gate.call_count)
            digests.append(compatibility.identity(directory / compatibility.ASSET))
        self.assertEqual(digests[0], digests[1])
        self.assertIn("compatibility_base_commit=" + BASE, (self.root / "outputs").read_text())

    def test_publication_confirmation_only_appends_the_exact_retained_qualified_reference(self):
        before = dict(schemaVersion=1, baselineId="sunder-v1", status="UNFROZEN", releaseStatus="UNRELEASED", releases=[])
        reference = dict(id="synthetic-1.1.0", version="1.1.0", source=dict(commit=SOURCE))
        receipt = dict(status="freeze-qualified", artifactSourceCommit=SOURCE, sdkVersion="1.1.0", reference=reference)
        path = self.archive([("qualification.json", c.canonical(receipt), tarfile.REGTYPE),
                             ("supported-releases.json", c.canonical(before), tarfile.REGTYPE)])
        data = path.read_bytes()
        item = dict(id=5, name=compatibility.ASSET, digest="sha256:" + c.digest(data), size=len(data))
        remote = dict(id=4, draft=False, target_commitish=SOURCE, prerelease=False, assets=[item])
        proof = dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/sdk-compatibility-publication/v1",
                     artifactSourceCommit=SOURCE, tag="sdk/v1.1.0", releaseId=4,
                     compatibilityAsset=dict(assetName=compatibility.ASSET, assetId=5, sha256=c.digest(data), size=len(data)),
                     referenceSha256=c.digest(c.canonical(reference)), sourceManifestSha256=c.digest(c.canonical(before)))
        after = compatibility.publication_transition(before, reference)
        with patch.object(c, "remote_tag"), patch.object(assets.GitHubRelease, "get", return_value=remote) as get, \
             patch.object(assets.GitHubRelease, "download", side_effect=lambda _, target: target.write_bytes(data)):
            compatibility.verify_publication_evidence(proof, before, after, SOURCE)
            get.assert_called_with(4, draft=False)
            for mutation in ("source", "asset", "extra", "reference", "history"):
                changed, ledger = copy.deepcopy(proof), copy.deepcopy(after)
                if mutation == "source": changed["artifactSourceCommit"] = BASE
                if mutation == "asset": changed["compatibilityAsset"]["assetId"] = 6
                if mutation == "extra": changed["approved"] = True
                if mutation == "reference": ledger["releases"][0]["version"] = "1.1.1"
                if mutation == "history": ledger["releases"].append(reference)
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    compatibility.verify_publication_evidence(changed, before, ledger, SOURCE)
        with self.assertRaisesRegex(ValueError, "new immutable reference"):
            compatibility.publication_transition(after, reference)

    def test_direct_untagged_evidence_descendant_requires_numeric_publication_proof(self):
        evidence = "c" * 40
        changed = [dict(filename=compatibility.MANIFEST, status="modified"), dict(filename=compatibility.PUBLICATION, status="added")]
        comparison = dict(ahead_by=1, behind_by=0, total_commits=1, files=changed)
        with patch.dict(os.environ, SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT=evidence), \
             patch.object(c, "gh", side_effect=[dict(parents=[dict(sha=SOURCE)]), comparison]), \
             patch.object(c, "source_file", return_value=b"{}"), \
             patch.object(compatibility, "verify_publication_evidence", side_effect=ValueError("unconfirmed publication")) as verify:
            with self.assertRaisesRegex(ValueError, "unconfirmed publication"): c.evidence_descendant(SOURCE, evidence)
            verify.assert_called_once()


if __name__ == "__main__": unittest.main()
