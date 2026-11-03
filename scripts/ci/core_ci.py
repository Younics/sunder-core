#!/usr/bin/env python3
"""Local/CI preparation for the Core test lanes. Product metadata owns output names."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
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


def source():
    node_inputs(restore=False)
    run("dotnet", "restore", ROOT / "Sunder.Core.slnx", "--locked-mode")
    run("dotnet", "build", ROOT / "Sunder.Core.slnx", "-c", "Release", "--no-restore",
        "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false")


def templates(work):
    feed = ROOT / "artifacts/test-feed"
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
    shutil.copyfile(catalog, ROOT / "artifacts/packages/sunder-platform-contracts.v1.json")
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


def module_inputs():
    rids = [row["rid"] for row in platforms()["include"]]
    rid = property_value(project("Sunder.Cli"), "NETCoreSdkRuntimeIdentifier")
    if rid not in rids: raise ValueError("Unsupported interop host RID: " + rid)
    run("npm", "--prefix", NODE, "run", "module:package")
    fixture = NODE / "fixtures/runtime-backed-worker"
    manifest = json.loads((fixture / "sunder.package.json").read_text())
    archive = fixture / "dist" / f"{manifest['id']}.{manifest['version']}.sunderpkg"
    leaf = fixture / "dist/targets" / rid
    if not leaf.is_dir(): raise ValueError("Missing current-RID Node module leaf: " + str(leaf))
    run("dotnet", target("Sunder.Cli"), "dev", "package", "validate", archive)
    return publish_environment({"SUNDER_NODE_MODULE_PACKAGE_PATH": archive,
        "SUNDER_NODE_MODULE_EXPECTED_RIDS": ",".join(rids),
        "SUNDER_NODE_MODULE_LEAF_PATH": leaf})


def official_inputs(work, rid):
    fixture = DOTNET / "tests/Sunder.Package.Build.TestFixtures/WorkerV1Executable/WorkerV1Executable.csproj"
    archive = work / "native-worker.sunderpkg"
    run("dotnet", "msbuild", fixture, "-t:PackSunderPackage", "-p:Configuration=Release", "-p:RuntimeIdentifier=" + rid,
        "-p:SunderPackageOutputPath=" + str(archive))
    publish_environment({"SUNDER_DOTNET_WORKER_ARCHIVE_PATH": archive,
        "SUNDER_WORKER_RELEASE_METRICS_PATH": ROOT / "artifacts/test-results/official" / f"dotnet-worker-{rid}.json"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("source", "node-inputs", "templates", "module", "official-inputs"))
    parser.add_argument("--work", type=Path)
    parser.add_argument("--rid", choices=[row["rid"] for row in platforms()["include"]])
    args = parser.parse_args()
    if args.action == "source": source()
    elif args.action == "node-inputs": node_inputs()
    elif args.action == "module": module_inputs()
    else:
        work = args.work or Path(tempfile.mkdtemp(prefix="sunder-ci-", dir=os.environ.get("RUNNER_TEMP")))
        work.mkdir(parents=True, exist_ok=True)
        if args.action == "templates": templates(work.resolve())
        else:
            if not args.rid: parser.error("official-inputs requires --rid")
            official_inputs(work.resolve(), args.rid)


if __name__ == "__main__":
    main()
