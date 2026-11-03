#!/usr/bin/env python3
"""Public package identities come from projects, never a second version ledger."""
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = "eng/test-oracles/core-public-packages.json"


def version_key(value, nuget=False):
    match = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?"
                         r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?", value)
    if not match or match[4] is not None and not nuget:
        raise ValueError("Invalid package SemVer: " + value)
    pre = match[5]
    if pre and any(item.isdigit() and len(item) > 1 and item.startswith("0") for item in pre.split(".")):
        raise ValueError("Numeric prerelease identifiers cannot have leading zeroes")
    if nuget and pre:
        pre = pre.lower()
    return (*map(int, match.group(1, 2, 3)), int(match[4] or 0), pre is None,
            tuple((0, int(item)) if item.isdigit() else (1, item) for item in pre.split(".")) if pre else ())


def archive_name(kind, name, version):
    return f"{name}.{version}.nupkg" if kind == "nuget" else f"{name.lstrip('@').replace('/', '-')}-{version}.tgz"


def packages(root=ROOT, *, evaluate=False):
    root = Path(root)
    inventory = json.loads((root / INVENTORY).read_text())
    result = []
    for kind in ("nuget", "npm"):
        for item in inventory[kind]:
            project = item["project"]
            if kind == "nuget":
                tree = ET.parse(root / project)
                name = tree.findtext("./PropertyGroup/PackageId") or Path(project).stem
                version = tree.findtext("./PropertyGroup/Version")
                if evaluate:
                    env = {key: value for key, value in os.environ.items() if key not in
                           ("GH_TOKEN", "GITHUB_TOKEN", "NODE_AUTH_TOKEN", "NUGET_API_KEY")}
                    output = subprocess.check_output(["dotnet", "msbuild", project, "-p:Configuration=Release",
                        "-p:ContinuousIntegrationBuild=true", "-getProperty:PackageId,PackageVersion"], cwd=root, env=env)
                    properties = json.loads(output)["Properties"]
                    if (properties["PackageId"], properties["PackageVersion"]) != (name, version):
                        raise ValueError("Evaluated package metadata differs from authored metadata: " + project)
            else:
                manifest = json.loads((root / project / "package.json").read_text())
                name, version = manifest["name"], manifest["version"]
                if manifest.get("private"):
                    raise ValueError("Public package inventory contains a private workspace")
            if name != item.get("packageId", item.get("name")) or not isinstance(version, str):
                raise ValueError("Public package identity/version differs: " + project)
            version_key(version)
            if "+" in version:
                raise ValueError("Publication versions cannot contain build metadata: " + project)
            key = kind + "-" + name.lower().lstrip("@").replace("/", "-").replace(".", "-")
            result.append(dict(kind=kind, name=name, version=version, project=project,
                               tag=key + "/v" + version, archive=archive_name(kind, name, version)))
    if len({item["name"] for item in result}) != len(result):
        raise ValueError("Duplicate public package identity")
    return result
