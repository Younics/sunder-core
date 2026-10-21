"""Synthetic owning-lane fixture shared by JS/Python tests. Never production evidence.

Executes a tiny Python owner against real temporary byte inputs. Git/runner
identities are mocked explicitly; no native RID or published asset is claimed.
"""
import json
from pathlib import Path
import sys
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/compatibility"))
import sdk_compatibility_execution as execution
from sdk_compatibility_execution import c

ENTRY = "scripts/test/release/synthetic_owner.py"
OWNER = '''import hashlib, json, os, pathlib, sys, xml.etree.ElementTree as ET
request = json.loads(pathlib.Path(os.environ["SUNDER_COMPATIBILITY_CONTEXT"]).read_text())
for item in request["inputs"] + request["candidatePackages"] + request["candidateHostInputs"]:
    assert hashlib.sha256(pathlib.Path(item["path"]).read_bytes()).hexdigest() == item["sha256"]
mode = sys.argv[1] if len(sys.argv) > 1 else "pass"
if mode == "mutate-candidate": pathlib.Path(request["candidatePackages"][0]["path"]).write_bytes(b"changed candidate")
if mode == "mutate-candidate-host": pathlib.Path(request["candidateHostInputs"][0]["path"]).write_bytes(b"changed candidate host")
if mode == "mutate-corpus": pathlib.Path(request["inputs"][0]["path"]).write_bytes(b"changed corpus")
for kind in ("source", "host", "package"):
    if mode == "mutate-" + kind:
        for item in request["inputs"]:
            if item["kind"] == kind: pathlib.Path(item["path"]).write_bytes(b"changed " + kind.encode())
if mode == "mutate-original":
    root = pathlib.Path(__file__).resolve().parents[3]
    (root / "packages" / request["candidatePackages"][0]["assetName"]).write_bytes(b"changed original")
if mode == "mutate-context": pathlib.Path(os.environ["SUNDER_COMPATIBILITY_CONTEXT"]).write_text("{}")
if mode == "mutate-entry": pathlib.Path(__file__).write_text("# changed owned command")
if mode == "mutate-recipe":
    root = pathlib.Path(__file__).resolve().parents[3]
    (root / "eng/compatibility/recipes" / request["referenceId"] / (request["corpusId"] + ".json")).write_text("{}")
if mode == "exit": sys.exit(7)
if mode == "timeout":
    import time
    time.sleep(10)
kind = request["corpusKind"]
if mode == "unrelated": kind = "unrelated"
if request["corpusKind"] == "old-source-new-sdk":
    root = ET.Element("TestRun")
    definition = ET.SubElement(ET.SubElement(root, "TestDefinitions"), "UnitTest", id="definition")
    ET.SubElement(definition, "TestMethod", className="Synthetic", name=kind.replace("-", "_"))
    ET.SubElement(ET.SubElement(root, "Results"), "UnitTestResult", testId="definition", executionId=request["executionId"], outcome="Passed")
else:
    root = ET.Element("testsuite", tests="1")
    case = ET.SubElement(root, "testcase", file="synthetic_owner.py", name=kind)
    if mode == "skipped": ET.SubElement(case, "skipped")
report = pathlib.Path(os.environ["SUNDER_COMPATIBILITY_REPORT"])
if mode == "link-report": report.symlink_to(__file__)
else: report.write_bytes(ET.tostring(root))
'''


def put(root, name, raw):
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(raw)
    return dict(path=name, sha256=c.digest(raw), size=len(raw))


def prepare_recipes(root, mode="pass"):
    qualification = c.decode((root / "qualification.json").read_bytes())
    ledger = c.decode((root / "supported-releases.json").read_bytes())
    put(root, ENTRY, OWNER.encode())
    for reference in [*ledger["releases"], qualification["reference"]]:
        for corpus in reference["corpus"]:
            portable = corpus["kind"] == "old-source-new-sdk"
            recipe = dict(schemaVersion=1, referenceId=reference["id"], corpusId=corpus["id"], kind=corpus["kind"], owner=corpus["owner"],
                          command=dict(runner="python3", entryPoint=ENTRY, arguments=[mode]), reportFormat="trx" if portable else "junit",
                          requiredTests=["Synthetic." + corpus["kind"].replace("-", "_") if portable else "synthetic_owner.py::" + corpus["kind"]], timeoutSeconds=1 if mode == "timeout" else 10)
            raw = c.canonical(recipe) + b"\n"
            corpus["recipe"] = put(root, corpus["recipe"]["path"], raw)
            put(root, execution.recipe_path(reference, corpus), raw)
    ledger_raw = c.canonical(ledger) + b"\n"
    for name in ("supported-releases.json", "base-supported-releases.json", "eng/compatibility/supported-releases.json"):
        put(root, name, ledger_raw)
    qualification["supportedManifestSha256"] = qualification["baseManifestSha256"] = c.digest(ledger_raw)
    put(root, "qualification.json", c.canonical(qualification) + b"\n")
    return qualification


def context(lane, qualification):
    rid = "linux-x64" if lane == "portable" else lane
    return dict(repository=c.REPOSITORY, sdkVersion=qualification["sdkVersion"], artifactSourceCommit=qualification["artifactSourceCommit"],
                **qualification["invocation"], job=execution.JOB, lane=lane, actualRid=rid,
                operatingSystem={"win": "Windows", "linux": "Linux", "osx": "macOS"}[rid.split("-")[0]], architecture=rid.split("-")[1].upper())


def collect(root, destination, qualification, lanes=execution.LANES):
    with patch.object(execution, "reviewed_source"):
        for lane in lanes:
            execution.collect_lane(root, root, root / "packages", destination / (execution.PREFIX + lane), context(lane, qualification))


def fixture(root, mode="pass"):
    source = dict(repository="https://github.com/Younics/sunder-core", commit="a" * 40)
    ledger = dict(schemaVersion=1, baselineId="sunder-v1", status="UNFROZEN", releaseStatus="UNRELEASED", releases=[])
    reference = dict(id="synthetic-1.1.0", version="1.1.0", source=source, artifacts=[], surfaces=[], corpus=[])
    prefix = "eng/compatibility/releases/" + reference["id"] + "/"
    packages = []
    for index, name in enumerate(execution.package_names("1.1.0")):
        data = ("synthetic " + name).encode()
        identifier = "sdk-" + str(index)
        reference["artifacts"].append(dict(id=identifier, kind="nuget" if name.endswith(".nupkg") else "npm", source=source,
                                         retainedIdentity="https://example.invalid/synthetic/" + identifier, **put(root, prefix + name, data)))
        packages.append(dict(assetName=name, artifactId=identifier))
        put(root, "packages/" + name, data)
    for kind in ("source", "package", "host"):
        reference["artifacts"].append(dict(id=kind, kind=kind, source=source, retainedIdentity="https://example.invalid/synthetic/" + kind,
                                         **put(root, prefix + kind + ".bin", ("synthetic " + kind).encode())))
    reference["surfaces"] = [dict(id="surface", format="exact-bytes-v1", artifactId="sdk-0", snapshot=put(root, prefix + "api.txt", b"synthetic api"), currentPath="current/api.txt")]
    put(root, "current/api.txt", b"synthetic api")
    for kind, inputs in (("old-source-new-sdk", ["source", "sdk-0"]), ("old-package-new-host", ["package"]), ("new-feature-old-host", ["host"])):
        reference["corpus"].append(dict(id=kind, kind=kind, artifactIds=inputs, owner="synthetic-owner",
                                       recipe=put(root, prefix + kind + ".json", b"{}")))
    qualification = dict(schemaVersion=2, status="freeze-qualified", repository=c.REPOSITORY, sdkVersion="1.1.0", artifactSourceCommit=source["commit"], baseCommit="b" * 40,
                         baseManifestSha256="0" * 64, supportedManifestSha256="0" * 64, reference=reference, packages=packages,
                         invocation=c.invocation(c.SDK_WORKFLOW, "refs/tags/sdk/v1.1.0", source["commit"], "123", "1"))
    put(root, "qualification.json", c.canonical(qualification))
    put(root, "supported-releases.json", c.canonical(ledger))
    return prepare_recipes(root, mode)


if __name__ == "__main__":
    root, destination = (Path(value) for value in sys.argv[1:])
    qualification = prepare_recipes(root)
    collect(root, destination, qualification)
