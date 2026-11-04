#!/usr/bin/env python3
"""Run one unittest module once and retain a result for every selected method."""
import importlib.util
import json
from pathlib import Path
import sys
import time
import unittest


class Results(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.rows = []

    def startTest(self, test):
        super().startTest(test)
        self.started = time.monotonic()
        self.row = dict(selector=".".join(test.id().split(".")[-2:]), status="passed", output="")

    def addError(self, test, err):
        super().addError(test, err)
        self.row.update(status="failed", output=self._exc_info_to_string(err, test))

    def addFailure(self, test, err):
        super().addFailure(test, err)
        self.row.update(status="failed", output=self._exc_info_to_string(err, test))

    def addSubTest(self, test, subtest, err):
        super().addSubTest(test, subtest, err)
        if err is not None:
            self.row.update(status="failed", output=self.row["output"] + self._exc_info_to_string(err, test))

    def addSkip(self, test, reason):
        super().addSkip(test, reason)
        if self.row["status"] != "failed": self.row.update(status="skipped", output=reason)

    def addUnexpectedSuccess(self, test):
        super().addUnexpectedSuccess(test)
        self.row.update(status="failed", output="Unexpected success")

    def addExpectedFailure(self, test, err):
        super().addExpectedFailure(test, err)
        self.row.update(status="skipped", output="Expected failure: " + self._exc_info_to_string(err, test))

    def stopTest(self, test):
        self.rows.append(dict(self.row, durationSeconds=time.monotonic() - self.started))
        super().stopTest(test)


def main():
    source, output, *selectors = sys.argv[1:]
    sys.path.insert(0, str(Path(source).resolve().parent))
    spec = importlib.util.spec_from_file_location("sunder_fixture", source)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    suite = unittest.defaultTestLoader.loadTestsFromNames(selectors, module)
    result = unittest.TextTestRunner(verbosity=2, resultclass=Results).run(suite)
    Path(output).write_text(json.dumps(result.rows), encoding="utf-8")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
