#!/usr/bin/env python3
"""Assemble the existing post-publication receipt set; native bytes remain retained separately."""
import argparse
from pathlib import Path
import shutil
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import release_coordinates as c
import retained_release_assets as assets


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("publication", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    publication = c.decode(c.read(args.publication))
    c.require(publication["decision"] == "published", "Only a completed publication is final evidence")
    native = c.decode(c.read(args.directory / "retained-assets.receipt.json"))
    c.require(publication["source"]["artifactSourceCommit"] == native["artifactSourceCommit"], "Publication/native source differs")
    args.output.mkdir()
    for name in ("production.lock.json", "release-drivers.v1.json", "release-set.v1.json"):
        shutil.copyfile(args.directory / name, args.output / name)
    for rid in c.RIDS:
        shutil.copyfile(args.directory / "execution-runtime-receipts" / (rid + ".json"), args.output / (rid + ".json"))
    shutil.copyfile(args.publication, args.output / "registry-publication.v1.json")
    files = [assets.file_record(args.output, path.name) for path in sorted(args.output.iterdir())]
    release = c.decode(c.read(args.output / "release-set.v1.json"))
    c.write_new(args.output / "runtime-release-evidence.v1.json", dict(
        schemaVersion=1, kind="https://sunderapp.io/acceptance/runtime-release-evidence/v1",
        destination=native["destination"], source=publication["source"], invocation=publication["invocation"],
        runtimeLock=dict(path="production.lock.json", sha256=native["runtimeLockSha256"]),
        releaseDrivers=dict(path="release-drivers.v1.json", sha256=release["releaseDriversSha256"], drivers=release["releaseDrivers"]),
        nativeOutputReceipts=release["nativeOutputReceipts"],
        releaseSet=dict(path="release-set.v1.json", sha256=c.digest(c.read(args.output / "release-set.v1.json"))),
        registryPublication=dict(path="registry-publication.v1.json", sha256=c.digest(c.read(args.publication))), files=files))


if __name__ == "__main__":
    main()
