#!/usr/bin/env python3
"""Read CI inputs from their owners without installing tools or evaluating source."""
import argparse
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def platforms(root=ROOT, profile="native"):
    document = json.loads((Path(root) / "eng/ci/platforms.json").read_text())
    if document["schemaVersion"] != 1 or profile not in ("native", "app", "packaging"):
        raise ValueError("Unknown CI platform policy/profile")
    rows = document["platforms"]
    if not rows or len({row["rid"] for row in rows}) != len(rows):
        raise ValueError("Empty/duplicate CI platforms")
    for row in rows:
        if not re.fullmatch(r"(?:win|linux|osx)-(?:x64|arm64)", row["rid"]):
            raise ValueError("Unsupported platform: " + row["rid"])
        for key in ("os", "appRunner"):
            if key in row and not re.fullmatch(r"[A-Za-z0-9._-]+", row[key]):
                raise ValueError("Invalid runner label")
    return {"include": [{"rid": row["rid"], "os": row.get("appRunner", row["os"]) if profile == "app" else row["os"]}
                        for row in rows if profile != "packaging" or row.get("packagingSmoke")]}


def toolchains(root=ROOT, packaging=False):
    root = Path(root)
    policy = json.loads((root / "eng/toolchains.json").read_text())
    sdk = json.loads((root / "global.json").read_text())["sdk"]["version"]
    if sdk != policy["dotnetSdk"]:
        raise ValueError("global.json differs from eng/toolchains.json")
    result = dict(dotnet=sdk, node=policy["node"], npm=policy["npm"])
    if packaging:
        tooling = json.loads((root / "eng/release/tooling.json").read_text())
        central = ET.parse(root / "languages/csharp/dotnet/Directory.Packages.props")
        versions = [item.attrib["Version"] for item in central.findall("./ItemGroup/PackageVersion") if item.get("Include") == "Velopack"]
        if len(versions) != 1 or tooling["schemaVersion"] != 1:
            raise ValueError("Ambiguous release tooling metadata")
        result.update(velopack=versions[0], velopack_sdk=tooling["velopackSdk"], signing_sdk=tooling["signingSdk"])
    if any(not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", value) for value in result.values()):
        raise ValueError("Toolchain versions must be exact")
    return result


def emit(values):
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            for key, value in values.items():
                text = json.dumps(value, separators=(",", ":")) if isinstance(value, (dict, list, bool)) else str(value)
                if "\n" in text or "\r" in text:
                    raise ValueError("Multiline workflow output")
                stream.write(f"{key}={text}\n")
    print(json.dumps(values))


def matrices(root=ROOT, scope="all"):
    policy = json.loads((Path(root) / "eng/test-oracles/core-test-policy.json").read_text())
    if scope not in policy["scopes"]: raise ValueError("Unknown validation scope: " + scope)
    owners = policy["scopes"][scope]
    partitions = ["main", *[f"{rule['name']}-{index + 1}" for rule in policy.get("nativePartitions", [])
                            if rule["component"] in owners for index in range(rule["shards"])]]
    return {**{profile: platforms(root, profile) for profile in ("native", "app", "packaging")},
            "platform": {"include": [{**row, "partition": partition,
                                      "artifact_suffix": "" if partition == "main" else "-" + partition}
                                     for partition in partitions for row in platforms(root)["include"]]},
            "platform_tests": any(owner in owners for owner in ("sdk", "host", "app")),
            "official_tests": "host" in owners, "app_packaging": "app" in owners}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("toolchains", "matrices"))
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--packaging", action="store_true")
    parser.add_argument("--scope", default="all")
    args = parser.parse_args()
    emit(toolchains(args.root, args.packaging) if args.action == "toolchains" else
         matrices(args.root, args.scope))


if __name__ == "__main__":
    main()
