#!/usr/bin/env python3
"""Local/CI preparation for the Core test lanes. Product metadata owns output names."""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

from metadata import ROOT, platforms

NODE = ROOT / "languages/typescript/node"
DOTNET = ROOT / "languages/csharp/dotnet"


def run(*args, capture=False, env=None, cwd=ROOT):
    command = [str(arg) for arg in args]
    if os.name == "nt" and command[0] == "npm": command[0] = "npm.cmd"
    print("+ " + " ".join(command), flush=True)
    result = subprocess.run(command, cwd=cwd, env=env, check=True, text=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout.strip() if capture else None


def project(name):
    matches = list(DOTNET.glob(f"*/{name}/{name}.csproj"))
    if len(matches) != 1: raise ValueError("Missing/ambiguous project: " + name)
    return matches[0]


def property_value(path, name):
    return run("dotnet", "msbuild", path, "-p:Configuration=Release", "-getProperty:" + name, capture=True)


def target(name):
    return Path(property_value(project(name), "TargetPath"))


def one(paths):
    paths = list(paths)
    if len(paths) != 1: raise ValueError("Expected exactly one generated output: " + str(paths))
    return paths[0].resolve()


def publish_environment(values):
    os.environ.update({name: str(value) for name, value in values.items()})
    if os.environ.get("GITHUB_ENV"):
        with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as stream:
            for name, value in values.items():
                if any(char in str(value) for char in "\r\n"): raise ValueError("Multiline environment value")
                stream.write(f"{name}={value}\n")
    return values


def node_inputs(restore=True):
    if restore: run("npm", "ci", "--prefix", NODE)
    run("npm", "run", "build", "--prefix", NODE)
    run("npm", "exec", "--prefix", NODE, "--", "tsc", "-p", NODE / "packages/package-tool/tsconfig.test.json")


def selection(scope):
    value = json.loads(subprocess.check_output(["node", str(ROOT / "scripts/test-oracles/core-test-policy.mjs"),
                                               "--scope", scope], cwd=ROOT))
    roots = {str(project(name).relative_to(ROOT)) for name in value["projects"]}
    if "dotnet" in value["components"]:
        packages = json.loads((ROOT / "eng/test-oracles/core-public-packages.json").read_text())
        roots.update(row["project"] for row in packages["nuget"])
        roots.update(str(project(name).relative_to(ROOT)) for name in ("Sunder.Cli", "Sunder.ExecutionRuntime.Build"))
    value["buildProjects"] = sorted(roots)
    return value


def build_input(selected, work):
    if selected["scope"] == "all": return ROOT / "Sunder.Core.slnx"
    solution = ET.Element("Solution")
    for path in selected["buildProjects"]: ET.SubElement(solution, "Project", Path=str(ROOT / path))
    path = work / (selected["scope"] + ".slnx")
    ET.ElementTree(solution).write(path, encoding="utf-8", xml_declaration=True)
    return path


def source(scope="all", work=None, stage=None, scenario=None, prepare_node=True):
    if work is None:
        with tempfile.TemporaryDirectory(prefix="sunder-source-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
            return source(scope, Path(temporary), stage, scenario, prepare_node)
    selected = selection(scope)
    if scenario:
        preview = json.loads(run("node", ROOT / "scripts/test-oracles/run-dotnet-tests-with-results.mjs",
                                 "--scope", scope, "--scenario", scenario, "--list", capture=True))
        names = [row["project"] for row in preview["projects"]] + ["Sunder.Cli", "Sunder.ExecutionRuntime.Build"]
        selected["buildProjects"] = sorted({str(project(name).relative_to(ROOT)) for name in names})
    if not selected["buildProjects"]:
        return
    if prepare_node and "dotnet" in selected["components"] and stage != "restore": node_inputs(restore=False)
    solution = build_input(selected, work)
    diagnostics = ROOT / "artifacts/test-results" / scope / "build"
    if stage != "build":
        with execution_evidence(diagnostics, "restore"):
            run("dotnet", "restore", solution, "--locked-mode", "-bl:" + str(diagnostics / "restore.binlog"))
    # A scoped solution lists roots; transitive project references must inherit
    # Release even when they have no entry in the solution's configuration map.
    if stage != "restore":
        with execution_evidence(diagnostics, "build"):
            run("dotnet", "build", solution, "-c", "Release", "--no-restore",
                 "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false",
                 "-p:ShouldUnsetParentConfigurationAndPlatform=false", "-bl:" + str(diagnostics / "build.binlog"))


@contextmanager
def execution_evidence(directory, name):
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (name + ".process.json")
    status = dict(status=None, signal=None, error="Execution did not complete", cleanupComplete=False)
    started = time.monotonic()
    status.update(stage=name, component=os.environ.get("CI_COMPONENT", directory.name),
                  job=os.environ.get("GITHUB_JOB"), sourceCommit=os.environ.get("GITHUB_SHA"))
    path.write_text(json.dumps(status) + "\n", encoding="utf-8")
    try:
        yield
    except BaseException as error:
        status.update(status=getattr(error, "returncode", 1), error=type(error).__name__ + ": " + str(error)[:4096],
                      cleanupComplete=isinstance(error, (subprocess.CalledProcessError, OSError)))
        raise
    else:
        status.update(status=0, error=None, cleanupComplete=True)
    finally:
        status["elapsedSeconds"] = round(time.monotonic() - started, 3)
        path.write_text(json.dumps(status) + "\n", encoding="utf-8")


def templates(work, feed=None, package_output=None):
    feed = feed or ROOT / "artifacts/test-feed"
    records = json.loads((feed / "test-feed.json").read_text())["packages"]
    def archive(name):
        row = one_record(records, name)
        # The feed inventory records exact independently selected package versions.
        filename = row["archive"]
        path = feed / filename
        if not path.is_file(): raise ValueError("Missing test-feed archive: " + filename)
        return path
    variants = json.loads((ROOT / "eng/test-oracles/template-smoke.json").read_text())
    catalogs = json.loads((ROOT / "eng/platform-contracts/sunder-contracts.owned.json").read_text())
    versions = {item["current"]["catalogRelease"]["releaseVersion"] for item in catalogs["contracts"].values()}
    if len(versions) != 1: raise ValueError("Ambiguous source catalog release")
    catalog = work / "platform-catalog.json"
    tool = target("Sunder.ExecutionRuntime.Build")
    run("dotnet", tool, "platform-contracts", "check", "--core-root", ROOT)
    run("dotnet", tool, "platform-contracts", "export", "--core-root", ROOT, "--catalog-release", versions.pop(), "--output", catalog)
    shutil.copyfile(catalog, (package_output or ROOT / "artifacts/packages") / "sunder-platform-contracts.v1.json")
    env = {**os.environ, "DOTNET_CLI_HOME": str(work / "template-cli-home"), "NUGET_PACKAGES": str(work / "template-packages")}
    run("dotnet", "new", "install", archive("Sunder.Package.Templates"), env=env)
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources"); ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mappings = ET.SubElement(config, "packageSourceMapping"); ET.SubElement(mappings, "clear")
    ET.SubElement(ET.SubElement(mappings, "packageSource", key="candidate"), "package", pattern="Sunder.*")
    ET.SubElement(ET.SubElement(mappings, "packageSource", key="nuget.org"), "package", pattern="*")
    config_path = work / "template.nuget.config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    cli = target("Sunder.Cli")
    host_inputs = {}
    for variant in variants["dotnet"]:
        name = variant["name"]
        sample = work / ("dotnet-template-" + name)
        run("dotnet", "new", "sunder-package", "--name", "Smoke." + name, "--packageId", "smoke." + name.replace("-", ""),
            "--packageName", "Smoke " + name, "--output", sample, *variant["options"], env=env)
        path = one(sample.glob("*.csproj"))
        run("dotnet", "restore", path, "--configfile", config_path, env=env)
        if variant.get("platformContract"):
            run("dotnet", "msbuild", path, "-t:ImplementSunderPlatformApi", "-p:SunderContractId=" + variant["platformContract"],
                "-p:SunderPlatformCatalogPath=" + str(catalog), env=env)
        run("dotnet", "build", path, "-c", "Release", "--no-restore", env=env)
        run("dotnet", "msbuild", path, "-t:PackSunderPackage", "-p:Configuration=Release", env=env)
        output = Path(property_value(path, "TargetDir"))
        run("dotnet", cli, "dev", "package", "validate", one(output.glob("*.sunderpkg")))
        if name == "headless": host_inputs["SUNDER_DOTNET_TEMPLATE_DEV_PATH"] = str(output / "sunder-dev")
    bootstrap = work / "node-template-bootstrap"
    run("npm", "install", "--prefix", bootstrap, "--no-audit", "--no-fund", archive("@sunder/sdk"), archive("create-sunder-package"))
    creator = bootstrap / "node_modules/create-sunder-package/dist/cli.js"
    for preset in variants["node"]:
        sample = work / ("node-template-" + preset)
        run("node", creator, "--preset", preset, "--package-id", "smoke." + preset.replace("-", ""), "--package-name", "Smoke " + preset, "--output", sample)
        run("npm", "install", "--prefix", sample, "--no-audit", "--no-fund", archive("@sunder/sdk"), archive("@sunder/package-tool"))
        for command in ("contracts:check", "typecheck", "test", "sunder:dev:once", "build", "package"):
            run("npm", "--prefix", sample, "run", command)
        if preset == "node": host_inputs["SUNDER_NODE_TEMPLATE_DEV_PATH"] = str(sample / "dist/sunder-dev")
    return publish_environment(host_inputs)


def one_record(records, name):
    values = [row for row in records if row["name"] == name]
    if len(values) != 1: raise ValueError("Missing/ambiguous test-feed package: " + name)
    return values[0]


def module_inputs(work=None):
    rids = [row["rid"] for row in platforms()["include"]]
    rid = property_value(project("Sunder.Cli"), "NETCoreSdkRuntimeIdentifier")
    if rid not in rids: raise ValueError("Unsupported interop host RID: " + rid)
    workspace = json.loads(run("node", ROOT / "scripts/test-oracles/node-test-workspace.mjs",
                               work or ROOT / "artifacts/node-producers", capture=True))
    node = Path(workspace["nodeRoot"])
    run("npm", "--prefix", node, "run", "build")
    run("npm", "--prefix", node, "run", "module:package")
    fixture = node / "fixtures/runtime-backed-worker"
    manifest = json.loads((fixture / "sunder.package.json").read_text())
    archive = fixture / "dist" / f"{manifest['id']}.{manifest['version']}.sunderpkg"
    leaf = fixture / "dist/targets" / rid
    if not leaf.is_dir(): raise ValueError("Missing current-RID Node module leaf: " + str(leaf))
    run("dotnet", target("Sunder.Cli"), "dev", "package", "validate", archive)
    return publish_environment({"SUNDER_NODE_MODULE_PACKAGE_PATH": archive,
        "SUNDER_NODE_MODULE_WORKSPACE_PATH": node,
        "SUNDER_NODE_MODULE_EXPECTED_RIDS": ",".join(rids),
        "SUNDER_NODE_MODULE_LEAF_PATH": leaf})


def official_inputs(work, rid, scope="all"):
    fixture = DOTNET / "tests/Sunder.Package.Build.TestFixtures/WorkerV1Executable/WorkerV1Executable.csproj"
    archive = work / "native-worker.sunderpkg"
    run("dotnet", "msbuild", fixture, "-t:PackSunderPackage", "-p:Configuration=Release", "-p:RuntimeIdentifier=" + rid,
        "-p:SunderPackageOutputPath=" + str(archive))
    publish_environment({"SUNDER_DOTNET_WORKER_ARCHIVE_PATH": archive,
        "SUNDER_WORKER_RELEASE_METRICS_PATH": ROOT / "artifacts/test-results" / scope / "official" / f"dotnet-worker-{rid}.json"})


def oracle(script, *args):
    run("node", ROOT / "scripts/test-oracles" / (script + ".mjs"), *args)


def dotnet_tests(selection, name, *, scope="all", build=False, directory=None):
    output = ROOT / "artifacts/test-results" / scope / (directory or name)
    try:
        oracle("run-dotnet-tests-with-results", "--scope", scope, "--" + selection, name, "--configuration", "Release",
               "--results-directory", output, *(["--build", "--disable-build-servers"] if build else []))
    finally:
        oracle("summarize-core-test-results", output, "--scope", scope, "--allow-partial", "--require-" + selection, name)


@contextmanager
def local_environment():
    previous = dict(os.environ)
    # Prepared paths belong to this invocation, including local runs with no CI env file.
    os.environ.pop("GITHUB_ENV", None)
    try:
        yield
    finally:
        os.environ.clear()
        os.environ.update(previous)


def validate(profile, *, rid=None, scope="all", partition=None):
    if partition is not None and profile != "platform": raise ValueError("Native partitions require the platform profile")
    selected = selection(scope)
    if partition is not None and partition not in selected["nativePartitions"]:
        raise ValueError("Unknown native partition: " + partition)
    components = selected["components"]
    output = ROOT / "artifacts/test-results" / scope
    with execution_evidence(output, profile), local_environment(), tempfile.TemporaryDirectory(prefix="sunder-ci-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
        work = Path(temporary)
        if profile == "portable":
            if "dotnet" in components or "npm" in components:
                run("node", ROOT / "scripts/toolchains.mjs")
                run("node", ROOT / "scripts/release/check-sdk-version.mjs")
            if "ci" in components:
                oracle("check-core-test-policy")
                run(sys.executable, "-B", ROOT / "scripts/test/check-markdown-links.py", "--tracked", ROOT)
            if "release-fixtures.junit.xml" in selected["reports"]:
                oracle("run-release-fixtures-with-results", "--scope", scope, "--results-directory", output / "scripts")
            if "node" in selected["lanes"]:
                if any(owner in components for owner in ("dotnet", "npm", "ci")): run("npm", "ci", "--prefix", NODE)
                oracle("run-core-test-lane", "--scope", scope, "--primary-lane", "node")
                oracle("run-node-test-matrix-with-results", "--scope", scope, "--results-directory", output / "node")
                oracle("summarize-core-test-results", output / "node", "--scope", scope, "--allow-partial", "--require-primary-lane", "node")
            # Artifact tests remove Node outputs; interop preparation must follow them.
            source(scope, work)
            if "dotnet-linux" in selected["lanes"]:
                oracle("run-core-test-lane", "--scope", scope, "--primary-lane", "dotnet-linux")
                dotnet_tests("primary-lane", "dotnet-linux", scope=scope, directory="dotnet")
            if "interop" in selected["scenarios"]["portable"]: module_inputs(work)
            for scenario in selected["scenarios"]["portable"]: dotnet_tests("scenario", scenario, scope=scope)
        elif profile == "candidate":
            if "dotnet" not in components: raise ValueError("Candidate packages require SDK validation")
            run(sys.executable, "-B", ROOT / "scripts/release/sdk_release.py", "pack")
            run(sys.executable, "-B", ROOT / "scripts/release/sdk_release.py", "feed", "--directory", "artifacts/test-feed")
            templates(work)
            dotnet_tests("scenario", "generated-templates", scope=scope)
        else:
            if "dotnet" in components or "host" in components: node_inputs()
            run("dotnet", "restore", build_input(selected, work), "--locked-mode")
            if profile == "platform":
                os.environ["SUNDER_REQUIRE_NODE24_HOST_INTEGRATION"] = "1"
                os.environ["SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS"] = "1" if sys.platform == "darwin" else "0"
                if partition is None:
                    dotnet_tests("primary-lane", "host-platform", scope=scope, build=True, directory="platform")
                else:
                    if partition != "main": os.environ["SUNDER_TEST_DIAGNOSTICS"] = "1"
                    dotnet_tests("native-partition", partition, scope=scope, build=True, directory="platform/" + partition)
                if partition not in (None, "main"): return
                if os.name == "nt" and "dotnet" in components: dotnet_tests("scenario", "output-lock", scope=scope, build=True, directory="lock-interop")
                if "app" in components:
                    dotnet_tests("primary-lane", "app-packaging", scope=scope, build=True)
                    if os.name == "nt": run("pwsh", "-File", ROOT / "scripts/ci/check-powershell.ps1")
            elif profile == "official":
                if "host" not in components: raise ValueError("Official runtimes require a Host consumer scope")
                rid = rid or property_value(project("Sunder.Cli"), "NETCoreSdkRuntimeIdentifier")
                if rid not in [row["rid"] for row in platforms()["include"]]: raise ValueError("Unsupported native RID: " + rid)
                official_inputs(work, rid, scope)
                dotnet_tests("scenario", "official", scope=scope, build=True)
            else:
                raise ValueError("Unknown validation profile: " + profile)


def rehearse(output):
    """Exercise the production pack/feed/template owners with a local all-package selection."""
    sys.path.insert(0, str(ROOT / "scripts/release"))
    import sdk_release
    import package_metadata
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    with local_environment(), tempfile.TemporaryDirectory(prefix="sunder-rehearsal-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
        os.environ.pop("GITHUB_WORKFLOW_REF", None)
        run("npm", "ci", "--prefix", NODE)
        source("sdk", Path(temporary))
        plan = dict(artifactSourceCommit=run("git", "rev-parse", "HEAD", capture=True),
                    packages=[dict(row, selected=True) for row in package_metadata.packages(ROOT, evaluate=True)])
        sdk_release.pack(plan, output / "packages")
        sdk_release.feed(plan, output / "packages", output / "test-feed")
        templates(Path(temporary), output / "test-feed", output / "packages")
        dotnet_tests("scenario", "generated-templates", scope="sdk", directory="rehearsal-templates")
        (output / "rehearsal.json").write_text(json.dumps(dict(scope="local-artifact-rehearsal", **plan), indent=2) + "\n")


def component_stage(action, scope, *, scenario=None, rid=None):
    """Hosted steps use the same owners as local profiles, with visible boundaries."""
    selected = selection(scope) if action not in ("step-results", "packaging") else None
    output = ROOT / "artifacts/test-results" / scope
    if action == "step-results":
        steps = json.loads(os.environ["SUNDER_CI_STEPS"])
        rows = [{"stage": name, "outcome": row["outcome"]} for name, row in steps.items()
                if "outcome" in row]
        output.mkdir(parents=True, exist_ok=True)
        (output / "steps.json").write_text(json.dumps(dict(component=scope, steps=rows), indent=2) + "\n")
        return
    with execution_evidence(output, action), local_environment():
        if action in ("restore", "build"):
            source(scope, stage=action, scenario=scenario, prepare_node=False)
        elif action == "fixtures":
            if "release-fixtures.junit.xml" in selected["reports"]:
                oracle("run-release-fixtures-with-results", "--scope", scope, "--results-directory", output / "scripts")
        elif action == "script-tests":
            if "node-core-oracles.junit.xml" in selected["reports"]:
                try:
                    oracle("run-node-test-matrix-with-results", "--scope", scope, "--results-directory", output / "node", "--action", "scripts")
                finally:
                    oracle("summarize-core-test-results", output / "node", "--scope", scope, "--allow-partial", "--require-primary-lane", "node")
        elif action == "test":
            if "dotnet-linux" in selected["lanes"]:
                oracle("run-core-test-lane", "--scope", scope, "--primary-lane", "dotnet-linux")
                dotnet_tests("primary-lane", "dotnet-linux", scope=scope, directory="dotnet")
            if "app" in selected["components"]:
                dotnet_tests("scenario", "app-interop", scope=scope)
        elif action == "pack":
            sys.path.insert(0, str(ROOT / "scripts/release"))
            import sdk_release
            kind = {"npm": "npm", "dotnet": "nuget"}[scope]
            plan = sdk_release.load(ROOT / "artifacts/release-plan.json")
            rows = sdk_release.selected(plan, kind)
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                    stream.write(f"package_count={len(rows)}\n")
            sdk_release.pack(plan, ROOT / "artifacts/packages", kind)
        elif action in ("interop", "template-consumers"):
            with tempfile.TemporaryDirectory(prefix="sunder-consumers-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
                work = Path(temporary)
                if action == "interop":
                    module_inputs(work)
                    dotnet_tests("scenario", "interop", scope=scope)
                else:
                    run(sys.executable, "-B", ROOT / "scripts/release/sdk_release.py", "feed", "--directory", "artifacts/test-feed")
                    templates(work)
                    dotnet_tests("scenario", "generated-templates", scope=scope)
        elif action == "packaging":
            if rid not in [row["rid"] for row in platforms(profile="packaging")["include"]]:
                raise ValueError("Unsupported packaging smoke RID")
            run("bash", ROOT / "scripts/test/release/smoke-app-packaging.sh", rid)
        else:
            raise ValueError("Unknown component stage: " + action)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("source", "node-inputs", "templates", "module", "official-inputs", "portable", "platform", "official", "candidate", "rehearse",
                                          "restore", "build", "fixtures", "script-tests", "test", "pack", "interop", "template-consumers", "step-results", "packaging"))
    parser.add_argument("--work", type=Path)
    parser.add_argument("--rid", choices=[row["rid"] for row in platforms()["include"]])
    parser.add_argument("--scope", choices=tuple(json.loads((ROOT / "eng/test-oracles/core-test-policy.json").read_text())["scopes"]), default="all")
    parser.add_argument("--partition", help="Native method partition (main or a policy-owned project shard)")
    parser.add_argument("--scenario", choices=("interop", "generated-templates"), help="Build only the selected consumer roots")
    parser.add_argument("--no-restore", action="store_true", help="Node inputs only: use the already restored workspace")
    parser.add_argument("--list", action="store_true", help="Print scope ownership and build roots without changing outputs")
    args = parser.parse_args()
    if args.list:
        print(json.dumps(selection(args.scope), indent=2))
    elif args.action in ("restore", "build", "fixtures", "script-tests", "test", "pack", "interop", "template-consumers", "step-results", "packaging"):
        component_stage(args.action, args.scope, scenario=args.scenario, rid=args.rid)
    elif args.action in ("portable", "platform", "official", "candidate"):
        validate(args.action, rid=args.rid, scope=args.scope, partition=args.partition)
    elif args.action == "rehearse":
        if args.work is None: parser.error("rehearse requires a new --work output directory")
        rehearse(args.work)
    elif args.action == "source": source(args.scope)
    elif args.action == "node-inputs": node_inputs(restore=not args.no_restore)
    elif args.action == "module": module_inputs()
    else:
        if args.work is None: parser.error("Prepared inputs require --work; use a validation profile for automatic cleanup")
        work = args.work
        work.mkdir(parents=True, exist_ok=True)
        if args.action == "templates": templates(work.resolve())
        else:
            if not args.rid: parser.error("official-inputs requires --rid")
            official_inputs(work.resolve(), args.rid, args.scope)


if __name__ == "__main__":
    main()
