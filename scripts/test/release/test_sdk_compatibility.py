"""Compatibility corpus parsing remains independent of publication orchestration."""
import io
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "compatibility"))
import sdk_corpus as compatibility


class SdkCompatibilityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def archive(self, entries):
        path = self.root / "corpus.tar"
        with tarfile.open(path, "w", format=tarfile.USTAR_FORMAT) as archive:
            for name, data, kind in entries:
                info = tarfile.TarInfo(name); info.size = len(data); info.type = kind
                if kind == tarfile.SYMTYPE: info.linkname = "outside"
                archive.addfile(info, io.BytesIO(data))
        return path

    def test_report_structure_and_summary_cannot_hide_failed_or_missing_results(self):
        self.assertEqual([dict(selector="synthetic.py::case", outcome="Passed")], compatibility.validate_report(
            b'<testsuite tests="1"><testcase file="synthetic.py" name="case"/></testsuite>', "junit"))
        for raw in (b'<testsuite><testcase/></unclosed>', b'<testsuite tests="0"><testcase/></testsuite>',
            b'<testsuite failures="1"><testcase/></testsuite>', b'<testsuite><system-out><testcase/></system-out></testsuite>',
            b'<testsuite/>', b'<!DOCTYPE testsuite><testsuite><testcase/></testsuite>'):
            with self.subTest(raw=raw), self.assertRaises((ValueError, compatibility.ET.ParseError)): compatibility.validate_report(raw, "junit")
        with self.assertRaisesRegex(ValueError, "summary"):
            compatibility.validate_report(b'<TestRun><UnitTestResult/><ResultSummary outcome="Failed"/></TestRun>', "trx")

    def test_xml_attributes_are_authoritative_without_summary_or_counters(self):
        raw = b'''<TestRun><TestDefinitions><UnitTest id="1"><TestMethod className="Actual" name="Case" note='className="Spoofed" name="Success"'/></UnitTest></TestDefinitions>
          <Results><UnitTestResult testId="1" executionId="run" outcome="Failed" note='outcome="Passed"'/></Results></TestRun>'''
        self.assertEqual([dict(selector="Actual.Case", outcome="Failed")], compatibility.validate_report(raw, "trx"))
        self.assertEqual("Skipped", compatibility.validate_report(raw.replace(b'outcome="Failed" note=', b'outcome="NotExecuted" note='), "trx")[0]["outcome"])

    def test_container_rejects_traversal_links_duplicates_extensions_and_truncation(self):
        for entries in [
            [("../escape", b"x", tarfile.REGTYPE)], [("/absolute", b"x", tarfile.REGTYPE)], [("a/./b", b"x", tarfile.REGTYPE)],
            [("linked", b"", tarfile.SYMTYPE)], [("linked", b"", tarfile.LNKTYPE)], [("pax", b"x", tarfile.XHDTYPE)],
            [("a", b"x", tarfile.REGTYPE), ("A", b"x", tarfile.REGTYPE)],
        ]:
            with self.subTest(entries=entries), tempfile.TemporaryDirectory() as output:
                with self.assertRaises(ValueError): compatibility.unpack(self.archive(entries), Path(output))
        path = self.archive([("reports/result.xml", b"synthetic", tarfile.REGTYPE)])
        with tempfile.TemporaryDirectory() as output:
            self.assertEqual({"reports/result.xml"}, compatibility.unpack(path, Path(output)))
        path.write_bytes(path.read_bytes()[:515])
        with tempfile.TemporaryDirectory() as output, self.assertRaises(ValueError): compatibility.unpack(path, Path(output))

    def test_container_size_inventory_and_trailing_data_are_bounded(self):
        path = self.archive([("one", b"x", tarfile.REGTYPE), ("two", b"x", tarfile.REGTYPE)])
        for bound in ("MAX_FILES", "MAXIMUM"):
            with tempfile.TemporaryDirectory() as output, patch.object(compatibility, bound, 1), self.assertRaises(ValueError):
                compatibility.unpack(path, Path(output))
        path.write_bytes(path.read_bytes() + b"trailing")
        with tempfile.TemporaryDirectory() as output, self.assertRaisesRegex(ValueError, "Trailing"): compatibility.unpack(path, Path(output))


if __name__ == "__main__": unittest.main()
