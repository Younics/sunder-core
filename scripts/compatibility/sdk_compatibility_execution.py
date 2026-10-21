#!/usr/bin/env python3
"""Owned, bounded SDK corpus collector and verifier. Recipes come from reviewed source.

Only this collector emits lane evidence. The tag workflow transports its outputs
separately from the candidate; candidate manifests cannot supply results/selectors.
"""
import argparse
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import uuid

sys.dont_write_bytecode = True
import sdk_release_compatibility as compatibility
from sdk_release_compatibility import c, ROOT

SCHEMAS = ROOT / "eng/compatibility"
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
LANES = ("portable", *RIDS)
PREFIX = "sunder-sdk-compatibility-lane-"
JOB = "compatibility-corpus"
MAX_JSON = 1024 * 1024


def validate(value, rule, document, depth=0):
    c.require(depth <= 32, "Compatibility JSON nesting exceeds bound")
    known = {"$schema", "title", "$defs", "$ref", "type", "additionalProperties", "required", "properties", "items", "enum", "const",
             "pattern", "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "minimum", "maximum"}
    c.require(rule.keys() <= known, "Unsupported compatibility schema keyword")
    if "$ref" in rule:
        file, pointer = rule["$ref"].split("#/$defs/")
        if file:
            c.require(file == "supported-releases.schema.json", "Unsupported compatibility schema reference")
            document = c.decode(c.read(SCHEMAS / file, MAX_JSON))
        return validate(value, document["$defs"][pointer], document, depth)
    if "const" in rule:
        c.require(type(value) is type(rule["const"]) and value == rule["const"], "Invalid compatibility constant")
    if "enum" in rule:
        c.require(value in rule["enum"], "Invalid compatibility enum")
    kind = rule.get("type")
    if kind == "object":
        c.require(isinstance(value, dict) and set(rule.get("required", ())) <= value.keys()
                  and value.keys() <= rule.get("properties", {}).keys(), "Unexpected/missing closed compatibility members")
        for key, child in value.items(): validate(child, rule["properties"][key], document, depth + 1)
    elif kind == "array":
        c.require(isinstance(value, list) and rule.get("minItems", 0) <= len(value) <= rule["maxItems"], "Invalid compatibility array bound")
        if rule.get("uniqueItems"): c.require(len({c.canonical(item) for item in value}) == len(value), "Duplicate compatibility entries")
        for child in value: validate(child, rule["items"], document, depth + 1)
    elif kind == "string":
        c.require(isinstance(value, str) and rule.get("minLength", 0) <= len(value) <= rule["maxLength"]
                  and ("pattern" not in rule or re.search(rule["pattern"], value)), "Invalid compatibility string/path")
    elif kind == "integer":
        c.require(type(value) is int and rule["minimum"] <= value <= rule["maximum"], "Invalid compatibility integer")


def document(path, schema):
    raw = c.read(path, MAX_JSON)
    value = c.decode(raw)
    definition = c.decode(c.read(SCHEMAS / schema, MAX_JSON))
    validate(value, definition, definition)
    return value, raw


def safe_file(root, name):
    c.require(isinstance(name, str) and re.fullmatch(r"[A-Za-z0-9_@.-]+(?:/[A-Za-z0-9_@.-]+)*", name)
              and all(part not in (".", "..") for part in name.split("/")), "Unsafe compatibility path")
    root = root.resolve(strict=True)
    path = root
    for part in name.split("/"):
        path = path / part
        c.require(not path.is_symlink(), "Linked compatibility input/output")
    c.require(path.is_file() and path.resolve().is_relative_to(root), "Missing/escaping compatibility file: " + name)
    return path


def file_record(path, name):
    return dict(path=name, size=path.stat().st_size, sha256=compatibility.identity(path))


def verified(root, record):
    path = safe_file(root, record["path"])
    c.require(file_record(path, record["path"]) == {key: record[key] for key in ("path", "size", "sha256")},
              "Compatibility input/report bytes changed: " + record["path"])
    return path


def package_names(version):
    return [f"{name}.{version}.nupkg" for name in ("Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build", "Sunder.Package.Templates")] + [
        f"{name}-{version}.tgz" for name in ("sunder-sdk", "sunder-package-tool", "create-sunder-package")]


def recipe_path(reference, corpus):
    return f"eng/compatibility/recipes/{reference['id']}/{corpus['id']}.json"


def command_for(recipe):
    command = recipe["command"]
    entry = command["entryPoint"]
    runner = command["runner"]
    prefixes = ("languages/csharp/dotnet/tests/",) if runner == "dotnet-test" else ("scripts/test/", "languages/typescript/node/")
    extension = ".csproj" if runner == "dotnet-test" else ".py" if runner == "python3" else ".mjs"
    c.require(entry.startswith(prefixes) and entry.endswith(extension), "Recipe command must be an owned source test entry point")
    c.require(all(value.split("=")[-1] not in (".", "..") for value in command["arguments"]), "Unsafe recipe argument path")
    if runner == "dotnet-test":
        c.require(not command["arguments"] and recipe["reportFormat"] == "trx"
                  and all(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.+]*", item) for item in recipe["requiredTests"]),
                  "dotnet-test uses only the collector's fixed command and exact reviewed selectors")
        argv = ["dotnet", "test", "{source}/" + entry, "-c", "Release", "-p:RestoreLockedMode=true", "-p:GeneratePackageOnBuild=false",
                "--filter", "|".join("FullyQualifiedName=" + item for item in recipe["requiredTests"]),
                "--logger", "trx;LogFileName=report.xml", "--results-directory", "{work}"]
    else:
        argv = [runner, *(["-B"] if runner == "python3" else []), "{source}/" + entry, *command["arguments"]]
    return dict(argv=argv, cwd="{work}", environment=dict(SUNDER_COMPATIBILITY_CONTEXT="{work}/context.json",
                SUNDER_COMPATIBILITY_REPORT="{work}/report.xml"))


def load_plans(source_root, bundle, packages_root, version, source, layout="flat"):
    qualification, raw = document(safe_file(bundle, "qualification.json"), "sdk-release-qualification.schema.json")
    ledger, ledger_raw = document(safe_file(bundle, "supported-releases.json"), "supported-releases.schema.json")
    c.require(qualification["sdkVersion"] == version and qualification["artifactSourceCommit"] == source
              and qualification["reference"]["version"] == version and qualification["reference"]["source"]["commit"] == source
              and qualification["supportedManifestSha256"] == c.digest(ledger_raw), "Collector candidate source/version/history differs")
    c.require(sorted(item["assetName"] for item in qualification["packages"]) == sorted(package_names(version)), "Collector needs the exact nine candidate packages")
    artifacts = {item["id"]: item for item in qualification["reference"]["artifacts"]}
    # Historical corpus artifactIds are immutable: they cannot contain a future
    # candidate Host. Reserve a stable candidate-only Host slot per native RID.
    # Other owning profiles may omit these; the dotnet-worker-v1 owner requires
    # its exact slot. These bytes come only from the current candidate reference.
    candidate_hosts = sorted([item for item in artifacts.values() if item["id"].startswith(("candidate-host-", "candidate-app-"))], key=lambda item: item["id"])
    c.require(len(candidate_hosts) <= 2 * len(RIDS) and all(item["id"] in {prefix + rid for rid in RIDS for prefix in ("candidate-host-", "candidate-app-")}
              and item["kind"] == "host" and item["source"] == qualification["reference"]["source"] for item in candidate_hosts),
              "Candidate Host slots must name exact native RIDs and the candidate source")
    packages = []
    for item in sorted(qualification["packages"], key=lambda value: value["assetName"]):
        artifact = artifacts[item["artifactId"]]
        name = item["assetName"]
        relative = name if layout == "flat" else ("sdk/" if name.endswith(".nupkg") else "npm/") + name
        packages.append(dict(assetName=name, artifactId=item["artifactId"], sha256=artifact["sha256"], size=artifact["size"], inputPath=relative))
    plans = []
    for reference in [*ledger["releases"], qualification["reference"]]:
        for corpus in reference["corpus"]:
            reviewed = safe_file(source_root, recipe_path(reference, corpus))
            retained = verified(bundle, corpus["recipe"])
            c.require(c.read(reviewed, MAX_JSON) == c.read(retained, MAX_JSON), "Corpus recipe differs from immutable reviewed source")
            recipe, _ = document(reviewed, "sdk-corpus-recipe.schema.json")
            c.require((recipe["referenceId"], recipe["corpusId"], recipe["kind"], recipe["owner"]) ==
                      (reference["id"], corpus["id"], corpus["kind"], corpus["owner"]), "Reviewed recipe ownership differs")
            command = command_for(recipe)
            entry = safe_file(source_root, recipe["command"]["entryPoint"])
            inputs = sorted([item for item in reference["artifacts"] if item["id"] in corpus["artifactIds"]], key=lambda item: item["id"])
            c.require(len(inputs) == len(corpus["artifactIds"]), "Missing exact retained corpus input")
            hosts = [] if corpus["kind"] == "old-source-new-sdk" else candidate_hosts
            c.require(sum(item["size"] for item in inputs + packages + hosts) <= compatibility.MAXIMUM, "Collector input set exceeds 2 GiB bound")
            plans.append(dict(reference=reference, corpus=corpus, recipe=recipe, command=command, inputs=inputs, packages=packages, candidateHosts=hosts,
                              qualificationSha256=c.digest(raw), entryPointSha256=compatibility.identity(entry),
                              lanes=("portable",) if corpus["kind"] == "old-source-new-sdk" else RIDS))
            c.require(len(plans) <= 4096, "Collector recipe inventory exceeds bound")
    return plans


def bindings(plan, source_root, bundle, packages_root):
    c.require(c.digest(c.read(safe_file(bundle, "qualification.json"), MAX_JSON)) == plan["qualificationSha256"], "Candidate qualification changed during execution")
    verified(bundle, plan["corpus"]["recipe"])
    reviewed = safe_file(source_root, recipe_path(plan["reference"], plan["corpus"]))
    c.require(compatibility.identity(reviewed) == plan["corpus"]["recipe"]["sha256"], "Reviewed recipe changed during execution")
    entry = safe_file(source_root, plan["recipe"]["command"]["entryPoint"])
    c.require(compatibility.identity(entry) == plan["entryPointSha256"], "Owned command changed during execution")
    for item in plan["inputs"]: verified(bundle, item)
    for item in plan["candidateHosts"]: verified(bundle, item)
    candidates = []
    for item in plan["packages"]:
        verified(packages_root, dict(path=item["inputPath"], sha256=item["sha256"], size=item["size"]))
        candidates.append({key: item[key] for key in ("assetName", "artifactId", "sha256", "size")})
    return dict(qualificationSha256=plan["qualificationSha256"], referenceSha256=c.digest(c.canonical(plan["reference"])),
                recipeSha256=plan["corpus"]["recipe"]["sha256"], corpusSha256=c.digest(c.canonical(plan["inputs"])),
                candidateSha256=c.digest(c.canonical(dict(packages=candidates, hosts=plan["candidateHosts"]))),
                entryPointSha256=plan["entryPointSha256"], commandSha256=c.digest(c.canonical(plan["command"])))


def platform_identity():
    operating_system = {"Linux": "Linux", "Windows": "Windows", "Darwin": "macOS"}.get(platform.system())
    architecture = {"amd64": "X64", "x86_64": "X64", "aarch64": "ARM64", "arm64": "ARM64"}.get(platform.machine().lower())
    c.require(operating_system and architecture, "Unsupported actual collector OS/architecture")
    c.require(os.environ.get("RUNNER_OS") == operating_system and os.environ.get("RUNNER_ARCH") == architecture,
              "Actual collector OS/architecture differs from the native runner (including emulation)")
    prefix = {"Linux": "linux", "Windows": "win", "macOS": "osx"}[operating_system]
    return dict(operatingSystem=operating_system, architecture=architecture, actualRid=prefix + "-" + architecture.lower())


def lane_context(lane, version):
    invocation = c.current_invocation(c.SDK_WORKFLOW)
    c.require(os.environ.get("GITHUB_EVENT_NAME") == "push" and invocation["workflowRef"] == "refs/tags/sdk/v" + version
              and os.environ.get("GITHUB_JOB") == JOB, "Collector must run in the owning integrated SDK job")
    actual = platform_identity()
    c.require(lane in LANES and actual["actualRid"] == ("linux-x64" if lane == "portable" else lane), "Selected lane differs from actual OS/RID")
    return dict(repository=c.REPOSITORY, sdkVersion=version, artifactSourceCommit=invocation["workflowSha"], **invocation, job=JOB, lane=lane, **actual)


def reviewed_source(source_root, source, plans):
    c.require(c.command("git", "rev-parse", "HEAD", cwd=source_root).decode().strip() == source, "Collector checkout differs from artifact source")
    paths = {recipe_path(plan["reference"], plan["corpus"]) for plan in plans} | {plan["recipe"]["command"]["entryPoint"] for plan in plans}
    for name in paths:
        c.require(c.command("git", "show", source + ":" + name, cwd=source_root) == c.read(safe_file(source_root, name), 8 * MAX_JSON),
                  "Recipe/owned command is not the exact reviewed Git source: " + name)


def run_owned(argv, cwd, environment, timeout):
    # No shell, external executable selection, inherited scenario driver, or
    # unbounded process output. A timeout/oversized log cannot emit a receipt.
    process = subprocess.Popen(argv, cwd=cwd, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               start_new_session=os.name != "nt", bufsize=0)
    def stop():
        if os.name == "nt":
            if process.poll() is None:
                try: subprocess.run(["taskkill", "/F", "/T", "/PID", str(process.pid)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
                finally:
                    if process.poll() is None: process.kill()
        else:
            # Kill the group even if the parent exited but a child holds stdout.
            try: os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError: pass
    drained = threading.Event()
    errors = []
    def drain():
        try:
            size = 0
            while data := process.stdout.read(65536):
                size += len(data)
                c.require(size <= 8 * MAX_JSON, "Owned corpus command output exceeds bound")
        except (ValueError, OSError) as error:
            errors.append(error)
        finally:
            drained.set()
    # Waiting for the pipe must also be bounded if a child inherited it and its
    # parent already exited (including on Windows). No receipt on that path.
    reader = threading.Thread(target=drain, daemon=True)
    deadline = time.monotonic() + timeout
    reader.start()
    try:
        c.require(drained.wait(timeout), "Owned corpus command failed or timed out")
        if errors: raise errors[0]
        try: code = process.wait(timeout=max(0.001, deadline - time.monotonic()))
        except subprocess.TimeoutExpired: raise ValueError("Owned corpus command failed or timed out") from None
        c.require(code == 0, "Owned corpus command failed or timed out")
    finally:
        stop()
        process.wait()
        process.stdout.close()


def require_results(data, recipe):
    results = compatibility.validate_report(data, recipe["reportFormat"])
    c.require(all(item["outcome"] == "Passed" for item in results), "Corpus execution has failed/skipped outcomes")
    c.require(set(recipe["requiredTests"]) <= {item["selector"] for item in results}, "Missing required executed test from immutable reviewed recipe")
    return results


def collect_lane(source_root, bundle, packages_root, output, context, layout="flat"):
    plans = load_plans(source_root, bundle, packages_root, context["sdkVersion"], context["artifactSourceCommit"], layout)
    plans = [plan for plan in plans if context["lane"] in plan["lanes"]]
    c.require(plans and not output.exists() and not output.resolve().is_relative_to(source_root.resolve())
              and not output.resolve().is_relative_to(bundle.resolve()) and not output.resolve().is_relative_to(packages_root.resolve()),
              "Collector output must be a new independent directory")
    reviewed_source(source_root, context["artifactSourceCommit"], plans)
    output.mkdir(parents=True)
    executions = []
    for plan in plans:
        before = bindings(plan, source_root, bundle, packages_root)
        identifier = str(uuid.uuid4())
        with tempfile.TemporaryDirectory(prefix="sunder-owned-corpus-") as temporary:
            work = Path(temporary).resolve()
            copies = []
            inputs = []
            packages = []
            hosts = []
            for group, records, root in (("corpus", plan["inputs"], bundle), ("candidate", plan["packages"], packages_root),
                                         ("candidate-host", plan["candidateHosts"], bundle)):
                for item in records:
                    name = item["inputPath"] if group == "candidate" else item["path"]
                    relative = f"inputs/{group}/{item['artifactId'] if group == 'candidate' else item['id']}/{Path(name).name}"
                    target = work / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(safe_file(root, name), target)
                    record = dict(path=relative, sha256=item["sha256"], size=item["size"])
                    verified(work, record)
                    copies.append(record)
                    dict(corpus=inputs, candidate=packages, **{"candidate-host": hosts})[group].append(dict(item, path=str(target)))
            request = dict(schemaVersion=1, executionId=identifier, context=context, referenceId=plan["reference"]["id"],
                           corpusId=plan["corpus"]["id"], corpusKind=plan["corpus"]["kind"], bindings=before, inputs=inputs,
                           candidatePackages=packages, candidateHostInputs=hosts)
            c.write_new(work / "context.json", request)
            context_bytes = c.read(work / "context.json", MAX_JSON)
            expand = lambda value: value.replace("{source}", str(source_root.resolve())).replace("{work}", str(work))
            command = plan["command"]
            environment = dict(os.environ, **{key: expand(value) for key, value in command["environment"].items()})
            run_owned([expand(value) for value in command["argv"]], work, environment, plan["recipe"]["timeoutSeconds"])
            report = safe_file(work, "report.xml")
            data = c.read(report, 8 * MAX_JSON)
            results = require_results(data, plan["recipe"])
            for record in copies: verified(work, record)
            c.require(c.read(safe_file(work, "context.json"), MAX_JSON) == context_bytes, "Collector input context was changed")
            after = bindings(plan, source_root, bundle, packages_root)
            c.require(after == before, "Corpus/candidate bytes changed during execution")
            report_name = f"reports/{plan['reference']['id']}/{plan['corpus']['id']}.xml"
            target = output / report_name
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as stream: stream.write(data)
            executions.append(dict(referenceId=plan["reference"]["id"], corpusId=plan["corpus"]["id"], corpusKind=plan["corpus"]["kind"],
                                   executionId=identifier, before=before, after=after, report=file_record(target, report_name),
                                   resultsSha256=c.digest(c.canonical(results)), exitCode=0))
    reviewed_source(source_root, context["artifactSourceCommit"], plans)
    value = dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/sdk-corpus-lane/v1", context=context, executions=executions)
    c.require(len(c.canonical(value)) < MAX_JSON, "Lane execution receipt exceeds bound")
    c.write_new(output / "lane.json", value)
    document(output / "lane.json", "sdk-corpus-lane.schema.json")


def verify_lanes(source_root, bundle, packages_root, execution_root, version, source, run, attempt):
    c.require(execution_root.is_dir() and not execution_root.is_symlink(), "Missing independent owning-lane execution artifacts")
    plans = load_plans(source_root, bundle, packages_root, version, source)
    c.number(run); c.number(attempt)
    files = []
    observed_ids = set()
    count = 0
    for lane in LANES:
        directory = PREFIX + lane
        path = safe_file(execution_root, directory + "/lane.json")
        value, _ = document(path, "sdk-corpus-lane.schema.json")
        context = value["context"]
        actual = "linux-x64" if lane == "portable" else lane
        expected_os = {"win": "Windows", "linux": "Linux", "osx": "macOS"}[actual.split("-")[0]]
        c.require(context["repository"] == c.REPOSITORY and context["sdkVersion"] == version
                  and context["artifactSourceCommit"] == context["workflowSha"] == source
                  and context["workflowRef"] == "refs/tags/sdk/v" + version and context["runId"] == run
                  and int(c.number(context["runAttempt"])) <= int(attempt)
                  and context["lane"] == lane and context["actualRid"] == actual
                  and context["operatingSystem"] == expected_os and context["architecture"] == actual.split("-")[1].upper(),
                  "Owning-lane execution OS/RID/source/run/attempt differs")
        expected = {(plan["reference"]["id"], plan["corpus"]["id"]): plan for plan in plans if lane in plan["lanes"]}
        observed = set()
        files.append(directory + "/lane.json")
        for execution in value["executions"]:
            key = execution["referenceId"], execution["corpusId"]
            c.require(key in expected and key not in observed and execution["executionId"] not in observed_ids, "Mislabelled/reused/unexpected corpus execution")
            observed.add(key); observed_ids.add(execution["executionId"])
            plan = expected[key]
            identity = bindings(plan, source_root, bundle, packages_root)
            c.require(execution["corpusKind"] == plan["corpus"]["kind"] and execution["before"] == execution["after"] == identity,
                      "Execution is not bound to the reviewed recipe/command and exact corpus/candidate bytes before and after execution")
            report_name = f"reports/{key[0]}/{key[1]}.xml"
            c.require(execution["report"]["path"] == report_name, "Execution report path differs from its owning slot")
            report = verified(execution_root / directory, execution["report"])
            results = require_results(c.read(report, 8 * MAX_JSON), plan["recipe"])
            c.require(c.digest(c.canonical(results)) == execution["resultsSha256"], "Actual XML results differ from collected outcomes")
            files.append(directory + "/" + report_name)
            count += 1
        c.require(observed == expected.keys(), "Missing independently collected corpus execution")
    actual_files = set()
    for path in execution_root.rglob("*"):
        c.require(not path.is_symlink(), "Linked owning-lane evidence")
        if path.is_file(): actual_files.add(path.relative_to(execution_root).as_posix())
        c.require(len(actual_files) <= compatibility.MAX_FILES, "Oversized lane evidence inventory")
    c.require(actual_files == set(files), "Unexpected/missing owning-lane evidence files")
    return dict(results=count, files=sorted(files))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("collect", "verify"))
    parser.add_argument("--source-root", type=Path, default=ROOT)
    parser.add_argument("--candidate", type=Path)
    parser.add_argument("--bundle-root", type=Path)
    parser.add_argument("--candidate-root", type=Path)
    parser.add_argument("--execution-root", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--lane", choices=LANES)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source")
    parser.add_argument("--run-id")
    parser.add_argument("--run-attempt")
    args = parser.parse_args()
    if args.action == "collect":
        c.require(args.candidate and args.output and args.lane, "Collector requires same-run candidate, new output and native lane")
        c.require((args.candidate / "compatibility/qualification.json").is_file(), compatibility.MISSING)
        context = lane_context(args.lane, args.version)
        collect_lane(args.source_root, args.candidate / "compatibility", args.candidate, args.output, context, "feed")
    else:
        c.require(args.bundle_root and args.candidate_root and args.execution_root and args.source and args.run_id and args.run_attempt,
                  "Independent owning-lane execution evidence and exact context are required")
        result = verify_lanes(args.source_root, args.bundle_root, args.candidate_root, args.execution_root,
                              args.version, args.source, args.run_id, args.run_attempt)
        print(c.canonical(result).decode())


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, subprocess.SubprocessError, compatibility.ET.ParseError) as error:
        raise SystemExit(str(error))
