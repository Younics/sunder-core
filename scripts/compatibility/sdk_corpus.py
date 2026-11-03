#!/usr/bin/env python3
"""Bounded files, XML reports and retained inputs for compatibility tests.

These are test utilities, not a package-publication approval protocol.
"""
import hashlib
from pathlib import Path
import re
import sys
import tarfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/release"))
import release_coordinates as c

MAXIMUM = 2 * 1024 ** 3
MAX_FILES = 8192
MISSING = "Compatibility corpus inputs are missing: supply genuine artifacts and owning test results."


def identity(path):
    c.require(path.is_file() and not path.is_symlink() and path.stat().st_size <= MAXIMUM, "Missing/bounded compatibility input required")
    digest, size = hashlib.sha256(), 0
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(data)
            c.require(size <= MAXIMUM, "Compatibility input exceeds bound")
            digest.update(data)
    return digest.hexdigest()


def validate_report(data, format):
    c.require(0 < len(data) <= 8 * 1024 * 1024, "Missing/oversized qualification report")
    text = data.decode("utf-8")
    c.require(not any(marker in text for marker in ("<!DOCTYPE", "<!ENTITY", "<!--", "<![CDATA[")), "Unsupported XML markup in qualification report")
    root = ET.fromstring(text)
    local = lambda item: item.tag.rsplit("}", 1)[-1]
    c.require(local(root) in (("TestRun",) if format == "trx" else ("testsuite", "testsuites")), "Unexpected qualification report root")
    result_name = "UnitTestResult" if format == "trx" else "testcase"
    results = [item for item in root.iter() if local(item) == result_name]
    c.require(results, "Qualification report has zero executed results")
    for item in root.iter():
        name = local(item)
        if name in ("Counters", "testsuite", "testsuites"):
            for key in ("failed", "failures", "errors", "error", "skipped", "disabled", "notExecuted", "aborted", "timeout", "inconclusive"):
                if key in item.attrib:
                    c.require(re.fullmatch(r"[0-9]+", item.attrib[key]) and int(item.attrib[key]) == 0, "Qualification report has failed/skipped/unfinished counters")
            for key, count in (("total", len(results)), ("tests", sum(local(child) == result_name for child in item.iter()))):
                if key in item.attrib:
                    c.require(re.fullmatch(r"[0-9]+", item.attrib[key]) and int(item.attrib[key]) == count, "Qualification report result count differs")
        if name == "ResultSummary":
            c.require(item.get("outcome") in ("Passed", "Completed"), "Qualification report summary did not pass")
        if name not in ("Results", "testsuite"):
            c.require(not any(local(child) == result_name for child in item) or format == "trx" and name == "TestRun", "Qualification result is hidden outside its results container")
    normalized, executions = [], set()
    if format == "trx":
        definitions = {}
        for item in root.iter():
            if local(item) != "UnitTest": continue
            methods = [child for child in item.iter() if local(child) == "TestMethod"]
            identifier = item.get("id")
            c.require(identifier and identifier not in definitions and len(methods) == 1, "Missing/duplicate TRX definition")
            method = methods[0]
            c.require(method.get("className") and method.get("name"), "Incomplete TRX selector")
            definitions[identifier] = method.get("className") + "." + method.get("name")
        for item in results:
            selector, execution = definitions.get(item.get("testId")), item.get("executionId")
            c.require(selector and execution and execution not in executions, "Missing/duplicate TRX execution/selector")
            executions.add(execution)
            actual = item.get("outcome")
            normalized.append(dict(selector=selector, outcome="Passed" if actual == "Passed" else "Skipped" if actual in ("NotExecuted", "Skipped", "NotRunnable") else "Failed"))
    else:
        for item in results:
            source, name = item.get("file"), item.get("name")
            c.require(source and name, "JUnit result requires its actual file/name selector")
            selector = source.replace("\\", "/") + "::" + name
            c.require(selector not in executions, "Duplicate JUnit selector")
            executions.add(selector)
            children = {local(child) for child in item.iter() if child is not item}
            outcome = "Failed" if children & {"failure", "error"} else "Skipped" if children & {"skipped", "disabled"} else "Passed"
            if item.get("status", "run") not in ("run", "passed") or item.get("outcome", "Passed") != "Passed": outcome = "Failed"
            normalized.append(dict(selector=selector, outcome=outcome))
    c.require(len(normalized) <= 32768 and all(len(item["selector"]) <= 1024 for item in normalized), "Oversized normalized qualification results")
    return normalized


def unpack(path, destination):
    names, folded, total = set(), set(), 0
    with path.open("rb") as archive:
        while True:
            header = archive.read(512)
            c.require(len(header) == 512, "Truncated compatibility tar header")
            if header == b"\0" * 512:
                c.require(archive.read(512) == b"\0" * 512, "Missing compatibility tar end marker")
                while padding := archive.read(1024 * 1024): c.require(not padding.strip(b"\0"), "Trailing compatibility tar data")
                break
            item = tarfile.TarInfo.frombuf(header, "utf-8", "strict")
            name = item.name
            c.require(re.fullmatch(r"[A-Za-z0-9_@.-]+(?:/[A-Za-z0-9_@.-]+)*", name) and all(part not in (".", "..") for part in name.split("/")), "Unsafe compatibility member path")
            c.require(item.type == tarfile.REGTYPE and not item.linkname and name.lower() not in folded, "Linked/special/duplicate compatibility member")
            names.add(name); folded.add(name.lower()); total += item.size
            c.require(len(names) <= MAX_FILES and 0 < item.size <= MAXIMUM and total <= MAXIMUM, "Oversized compatibility container")
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as output:
                remaining = item.size
                while remaining:
                    data = archive.read(min(remaining, 1024 * 1024))
                    c.require(data, "Truncated compatibility member")
                    output.write(data); remaining -= len(data)
            padding = (-item.size) % 512
            c.require(archive.read(padding) == b"\0" * padding, "Invalid compatibility tar padding")
    return names
