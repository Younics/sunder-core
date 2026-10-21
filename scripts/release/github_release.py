#!/usr/bin/env python3
"""Immutable GitHub release transport and the integrated Core publication stage."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import quote

sys.dont_write_bytecode = True
import release_coordinates as c

ROOT = Path(__file__).resolve().parents[2]
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
EVIDENCE = {"SHA256SUMS", "release-provenance.json", "release-sbom.spdx.json", "toolchain.txt"}
MAX_ASSET = 2 * 1024 ** 3


def asset_name(value):
    c.require(isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]*", value), "Unsafe asset name")
    return value


def asset(value, name):
    matches = [item for item in value["assets"] if item["name"] == name]
    c.require(len(matches) == 1, "Missing/ambiguous retained asset: " + name)
    return matches[0]


def file_identity(path):
    path = Path(path)
    c.require(path.is_file() and not path.is_symlink() and path.stat().st_size <= MAX_ASSET,
              "Expected a bounded regular release file: " + str(path))
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(data)
            c.require(size <= MAX_ASSET, "Release file exceeds its bound")
            digest.update(data)
    return size, "sha256:" + digest.hexdigest()


def local_files(directory):
    c.require(not Path(directory).is_symlink(), "Linked release input directory")
    root = Path(directory).resolve(strict=True)
    c.require(root.is_dir(), "Release input must be a directory")
    files = {}
    for path in root.rglob("*"):
        c.require(not path.is_symlink(), "Linked release input")
        if path.is_dir():
            continue
        name = asset_name(path.name)
        # Velopack's local working indexes are not public release assets.
        if name.startswith(("assets.", "RELEASES-")):
            continue
        c.require(name not in files and name.casefold() not in {key.casefold() for key in files}, "Duplicate flat asset name")
        file_identity(path)
        files[name] = path
        c.require(len(files) <= 256, "Oversized release inventory")
    c.require(files, "Release has no files")
    return files


class GitHubRelease:
    def __init__(self, repository, tag):
        c.require(repository in ("Younics/sunder-core", "Younics/sunder-agent-package"), "Unexpected release repository")
        stream = tag.split("/", 1)[0]
        c.require(stream in (("app", "cli", "host", "sdk") if repository.endswith("sunder-core") else ("agent",)), "Unexpected release stream")
        c.tag(tag, stream)
        self.repository, self.tag = repository, tag

    def api(self, suffix, method="GET", value=None):
        arguments = ["gh", "api", "--hostname", "github.com", "--method", method,
                     "-H", "Accept: application/vnd.github+json", "-H", "X-GitHub-Api-Version: 2022-11-28",
                     f"repos/{self.repository}/{suffix}"]
        if value is not None:
            arguments += ["--input", "-"]
        result = subprocess.run(arguments, input=c.canonical(value) if value is not None else None,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
        c.require(result.returncode == 0, f"GitHub {method} failed for {suffix}")
        return c.decode(result.stdout)

    def resolve(self):
        matches = []
        for page in range(1, 101):
            values = self.api(f"releases?per_page=100&page={page}")
            c.require(isinstance(values, list) and len(values) <= 100, "Invalid release page")
            for value in values:
                c.number(value["id"])
                c.require(type(value["draft"]) is bool and isinstance(value["tag_name"], str), "Invalid release listing")
                if value["tag_name"] == self.tag:
                    matches.append(value)
            if len(values) < 100:
                c.require(len(matches) <= 1, "Ambiguous release tag")
                return matches[0] if matches else None
        raise ValueError("Release pagination limit exceeded")

    def get(self, release_id, draft=None):
        value = self.api("releases/" + c.number(release_id))
        c.require(type(value["id"]) is int and value["id"] == int(release_id) and value["tag_name"] == self.tag
                  and type(value["draft"]) is bool and (draft is None or value["draft"] is draft), "Release identity/state changed")
        assets = value["assets"]
        c.require(isinstance(assets, list) and len(assets) <= 256, "Invalid asset inventory")
        names, ids = set(), set()
        for item in assets:
            name = asset_name(item["name"])
            identifier = c.number(item["id"])
            c.require(name.casefold() not in names and identifier not in ids, "Ambiguous release assets")
            names.add(name.casefold()); ids.add(identifier)
            c.require(item["state"] == "uploaded" and type(item["size"]) is int and 0 <= item["size"] <= MAX_ASSET
                      and re.fullmatch(r"sha256:[0-9a-f]{64}", item.get("digest") or ""), "Incomplete asset metadata")
        return value

    def create(self, source, title):
        c.sha(source, 40)
        value = self.resolve()
        if value is None:
            # A lost creation response is reconciled by tag; never create twice blindly.
            try:
                self.api("releases", "POST", dict(tag_name=self.tag, target_commitish=source, name=title,
                         body=f"Source: {source}", draft=True, prerelease="-" in self.tag, make_latest="false"))
            except (ValueError, subprocess.TimeoutExpired):
                pass
            for _ in range(4):
                value = self.resolve()
                if value is not None:
                    break
                time.sleep(1)
        c.require(value is not None and value["target_commitish"] == source
                  and value["prerelease"] is ("-" in self.tag), "Release does not bind the exact source/version")
        return self.get(value["id"])

    def download(self, item, destination):
        destination = Path(destination)
        c.require(destination.parent.is_dir() and not destination.is_symlink(), "Unsafe download destination")
        with tempfile.NamedTemporaryFile(dir=destination.parent, prefix=".release-", delete=False) as stream:
            temporary = Path(stream.name)
            try:
                with tempfile.TemporaryFile() as errors:
                    process = subprocess.Popen(["gh", "api", "--hostname", "github.com", "-H", "Accept: application/octet-stream",
                        "-H", "X-GitHub-Api-Version: 2022-11-28",
                        f"repos/{self.repository}/releases/assets/{c.number(item['id'])}"], stdout=subprocess.PIPE, stderr=errors)
                    timer = threading.Timer(300, process.kill)
                    timer.start()
                    try:
                        size = 0
                        while data := process.stdout.read(1024 * 1024):
                            size += len(data)
                            c.require(size <= item["size"], "Downloaded asset exceeds its declared size")
                            stream.write(data)
                        c.require(process.wait(timeout=10) == 0, "Asset download failed")
                    finally:
                        timer.cancel()
                        if process.poll() is None:
                            process.kill()
                        process.wait()
                        process.stdout.close()
                stream.flush()
                c.require(file_identity(temporary) == (item["size"], item["digest"]), "Downloaded bytes do not match asset metadata")
                if destination.exists():
                    c.require(file_identity(destination) == file_identity(temporary), "Download would replace different local bytes")
                else:
                    # Link gives no-replace publication on the same filesystem.
                    os.link(temporary, destination)
            finally:
                temporary.unlink(missing_ok=True)

    def upload(self, release_id, paths):
        files = {asset_name(Path(path).name): Path(path) for path in paths}
        c.require(len(files) == len(paths) == len({name.casefold() for name in files}), "Duplicate upload names")
        for name, path in files.items():
            identity = file_identity(path)
            for attempt in range(4):
                release = self.get(release_id, draft=True)
                c.require(not any(item["name"].casefold() == name.casefold() and item["name"] != name
                                  for item in release["assets"]), "Case-colliding remote asset")
                existing = next((item for item in release["assets"] if item["name"] == name), None)
                if existing is not None:
                    c.require((existing["size"], existing["digest"]) == identity, "Immutable asset conflict: " + name)
                    with tempfile.TemporaryDirectory() as temporary:
                        self.download(existing, Path(temporary) / name)
                    c.require(file_identity(path) == identity, "Upload input changed")
                    break
                c.require(attempt < 3, "Upload did not become observable: " + name)
                try:
                    subprocess.run(["gh", "api", "--hostname", "github.com", "--method", "POST",
                        "-H", "Content-Type: application/octet-stream", "-H", "X-GitHub-Api-Version: 2022-11-28",
                        "--input", str(path), f"https://uploads.github.com/repos/{self.repository}/releases/{c.number(release_id)}/assets?name={quote(name, safe='')}"],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
                except subprocess.TimeoutExpired:
                    pass
                # Success, conflict and lost responses all follow the same exact readback.
                c.require(file_identity(path) == identity, "Upload input changed")
                time.sleep(1)

    def verify(self, release_id, files, draft=None):
        value = self.get(release_id, draft)
        c.require({item["name"] for item in value["assets"]} == set(files), "Release asset inventory differs")
        with tempfile.TemporaryDirectory() as directory:
            for item in value["assets"]:
                c.require(file_identity(files[item["name"]]) == (item["size"], item["digest"]), "Remote asset differs: " + item["name"])
                self.download(item, Path(directory) / item["name"])
        return value


def core_names(component, version, channel="stable"):
    if component in ("cli", "host"):
        prefix = "sunder-cli" if component == "cli" else "sunder-runtime-host"
        return {f"{prefix}-{version}-{rid}.{'zip' if rid.startswith('win-') else 'tar.gz'}" for rid in RIDS}
    if component == "app":
        c.require(re.fullmatch(r"[a-z0-9][a-z0-9-]*", channel), "Invalid App release channel")
        names = set()
        for rid in RIDS:
            names.update((f"Sunder-{version}-app-{rid}-{channel}-full.nupkg", f"releases.app-{rid}-{channel}.json"))
            suffix = "-Setup.exe" if rid.startswith("win-") else ".AppImage" if rid.startswith("linux-") else ".dmg"
            names.add(f"Sunder-app-{rid}-{channel}{suffix}")
        return names
    c.require(component == "sdk", "Unknown Core component")
    inventory = c.decode(c.read(ROOT / "eng/test-oracles/core-public-packages.json"))
    names = {f"{package['packageId']}.{version}.nupkg" for package in inventory["nuget"]}
    names.update(f"{package}-{version}.tgz" for package in ("sunder-sdk", "sunder-package-tool", "create-sunder-package"))
    names.update(("npm-MIT-LICENSE.txt", "npm-NOTICES.txt", "node-dependency-notices.json"))
    names.add("sunder-sdk-compatibility.tar")
    if "-" not in version:
        names.update(("sunder-platform-contracts.v1.json", "sunder-platform-contracts.receipt.v1.json"))
    return names


def verify_evidence(files, release_id, repository, tag, source, run_id=None):
    c.require(EVIDENCE <= files.keys(), "Incomplete release evidence")
    value = c.decode(c.read(files["release-provenance.json"]))
    invocation = value["invocation"]
    c.require(value["schemaVersion"] == 1 and value["buildType"] == "https://sunderapp.io/provenance/github-actions/v1"
              and invocation["repository"] == repository and invocation["releaseId"] == int(release_id)
              and invocation["sha"] == source and invocation["ref"] == "refs/tags/" + tag
              and invocation["refName"] == tag and (run_id is None or invocation["runId"] == run_id), "Provenance source/run/release mismatch")
    if repository == c.REPOSITORY:
        component = tag.split("/", 1)[0]
        c.require(value["sources"] == [{"name": "sunder-core", "sha": source}]
                  and invocation["workflowRef"] == f"{repository}/.github/workflows/sunder-{component}-release.yml@refs/tags/{tag}",
                  "Provenance workflow/material mismatch")
    c.number(invocation["runId"]); c.number(invocation["runAttempt"])
    artifacts = value["artifacts"]
    c.require(len({item["path"] for item in artifacts}) == len(artifacts)
              and {item["path"] for item in artifacts} == set(files) - EVIDENCE, "Provenance inventory differs")
    sums = []
    for item in artifacts:
        c.require(file_identity(files[item["path"]]) == (item["size"], "sha256:" + item["sha256"]), "Provenance bytes differ")
        sums.append(f"{item['sha256']}  {item['path']}")
    c.require(sorted(c.read(files["SHA256SUMS"]).decode().splitlines()) == sorted(sums), "Checksum inventory differs")
    sbom = c.decode(c.read(files["release-sbom.spdx.json"]))
    c.require(sbom["spdxVersion"] == "SPDX-2.3" and sbom["dataLicense"] == "CC0-1.0"
              and len(sbom["files"]) == len(artifacts), "Invalid SBOM inventory")
    for item in artifacts:
        matches = [entry for entry in sbom["files"] if entry["fileName"] == "./" + item["path"]]
        c.require(len(matches) == 1 and matches[0]["checksums"] == [{"algorithm": "SHA256", "checksumValue": item["sha256"]}], "SBOM bytes differ")
    return value


def core_stage(store, directory, component, channel, finalize=False):
    c.require(store.repository == c.REPOSITORY, "Core publication requires the Core repository")
    source = c.sha(os.environ["GITHUB_SHA"], 40)
    c.require(os.environ["GITHUB_EVENT_NAME"] == "push" and os.environ["GITHUB_REF"] == "refs/tags/" + store.tag,
              "Component publication requires its tag workflow")
    c.require(os.environ["GITHUB_WORKFLOW_REF"] == f"{store.repository}/.github/workflows/sunder-{component}-release.yml@refs/tags/{store.tag}", "Unexpected component workflow")
    c.require(os.environ["GITHUB_WORKFLOW_SHA"] == source, "Workflow/source mismatch")
    subprocess.run(["bash", str(ROOT / "scripts/release/verify-release-tag.sh"), store.tag], check=True, cwd=ROOT)
    version = store.tag.split("/v", 1)[1]
    expected = core_names(component, version, channel)
    if component == "sdk":
        verify_sdk_compatibility(directory)
    release = store.resolve() if finalize else store.create(source, f"Sunder {component.title()} {version}")
    c.require(release is not None and release["target_commitish"] == source
              and release["prerelease"] is ("-" in version), "Missing source-bound release")
    release_id = release["id"]
    files = local_files(directory)
    if not finalize:
        c.require(set(files) - EVIDENCE == expected, "Incomplete component artifact inventory")
        old = {item["name"]: item for item in store.get(release_id)["assets"]}
        c.require(old.keys() <= expected | EVIDENCE, "Unexpected retained release assets")
        for name in sorted(old.keys() & EVIDENCE):
            store.download(old[name], Path(directory) / name)
        files = local_files(directory)
        if not EVIDENCE <= files.keys():
            # Evidence files independently describe the same payload. Preserve every
            # retained byte and generate only missing files after an interrupted upload.
            # In particular, never rewrite the original invocation on a later attempt.
            environment = dict(os.environ, GITHUB_RELEASE_ID=str(release_id))
            if "release-provenance.json" in files:
                environment["GITHUB_RUN_ATTEMPT"] = c.decode(c.read(files["release-provenance.json"]))["invocation"]["runAttempt"]
            with tempfile.TemporaryDirectory(dir=directory) as temporary:
                for name in sorted(expected):
                    os.link(files[name], Path(temporary) / name)
                subprocess.run(["bash", str(ROOT / "scripts/release/generate-release-evidence.sh"), temporary, "sunder-core=" + source],
                               env=environment, check=True)
                for name in EVIDENCE - files.keys():
                    with (Path(directory) / name).open("xb") as output:
                        output.write(c.read(Path(temporary) / name))
        files = local_files(directory)
    c.require(set(files) == expected | EVIDENCE, "Incomplete component/evidence inventory")
    evidence = verify_evidence(files, release_id, store.repository, store.tag, source, os.environ["GITHUB_RUN_ID"])
    c.require(int(evidence["invocation"]["runAttempt"]) <= int(os.environ["GITHUB_RUN_ATTEMPT"]), "Future publication attempt")
    if component == "sdk" and "-" not in version:
        verify_catalog(directory, release_id, store.tag, source, release["draft"])
    if component == "app":
        for rid in RIDS:
            manifest = c.decode(c.read(files[f"releases.app-{rid}-{channel}.json"]))
            full = f"Sunder-{version}-app-{rid}-{channel}-full.nupkg"
            c.require(len(manifest["Assets"]) == 1 and manifest["Assets"][0]["FileName"] == full
                      and manifest["Assets"][0]["SHA256"].lower() == file_identity(files[full])[1][7:], "Invalid Velopack Full manifest")
    if not finalize and release["draft"]:
        store.upload(release_id, list(files.values()))
    verified = store.verify(release_id, files)
    if finalize and release["draft"]:
        subprocess.run(["bash", str(ROOT / "scripts/release/verify-release-tag.sh"), store.tag], check=True, cwd=ROOT)
        before = store.get(release_id, draft=True)
        c.require(before["assets"] == verified["assets"] and before["target_commitish"] == source
                  and before["prerelease"] is ("-" in version), "Release changed after byte verification")
        latest = component == "app" and "-" not in version
        try:
            store.api("releases/" + str(release_id), "PATCH", {"draft": False, "make_latest": str(latest).lower()})
        except (ValueError, subprocess.TimeoutExpired):
            # The mutation can succeed even when the acknowledgement is lost.
            pass
        store.verify(release_id, files, draft=False)
    result = dict(release_id=release_id, evidence_run_attempt=evidence["invocation"]["runAttempt"])
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.writelines(f"{key}={value}\n" for key, value in result.items())
    print(json.dumps(result))


def verify_sdk_compatibility(directory):
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/compatibility/sdk_release_compatibility.py"),
                    "verify", "--directory", str(directory)], check=True, cwd=ROOT)


def verify_catalog(directory, release_id, tag, source, draft):
    # Import lazily: catalog retention uses this module's transport. The closed
    # catalog receipt also binds numeric asset identity, not just equal file bytes.
    import core_catalog_release as catalog
    receipt = c.decode(c.read(Path(directory) / catalog.RECEIPT))
    catalog.verify(Path(directory), release_id, tag, source, receipt["registryOrigin"], c.source_file(source, c.BASELINE), draft=draft)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("metadata", "resolve", "create", "upload", "download", "verify", "stage", "finalize"))
    parser.add_argument("tag")
    parser.add_argument("paths", nargs="*")
    parser.add_argument("--repository", default=os.environ.get("GH_REPO", c.REPOSITORY))
    parser.add_argument("--release-id")
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--channel", default="stable")
    args = parser.parse_args()
    store = GitHubRelease(args.repository, args.tag)
    if args.action == "metadata":
        values = {"version": args.tag.split("/v", 1)[1], "is_prerelease": str("-" in args.tag).lower()}
        c.require(not (args.tag.startswith("sdk/") and "-" in values["version"]
                       and values["version"].split("-", 1)[0].split(".")[2] == "0"),
                  "A coordinated SDK minor line starts at stable X.Y.0")
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.writelines(f"{key}={value}\n" for key, value in values.items())
        print(json.dumps(values))
    elif args.action == "resolve":
        value = store.resolve()
        if value is None: return 4
        print(json.dumps(value))
    elif args.action == "create":
        print(json.dumps(store.create(os.environ["GITHUB_SHA"], "Sunder " + args.tag)))
    elif args.action == "upload":
        c.require(args.paths, "Upload requires files")
        store.upload(args.release_id, args.paths)
    elif args.action == "download":
        c.require(args.directory and args.directory.is_dir(), "Download directory must exist")
        value = store.get(args.release_id)
        selected = args.paths or [item["name"] for item in value["assets"]]
        c.require(selected and len(set(selected)) == len(selected), "Empty/duplicate asset selection")
        for name in selected:
            matches = [item for item in value["assets"] if item["name"] == asset_name(name)]
            c.require(len(matches) == 1, "Missing selected asset")
            store.download(matches[0], args.directory / name)
    elif args.action == "verify":
        files = local_files(args.directory)
        if store.repository == c.REPOSITORY:
            source = c.sha(os.environ["GITHUB_SHA"], 40)
            component, version = store.tag.split("/v", 1)
            c.require(set(files) == core_names(component, version, args.channel) | EVIDENCE, "Incomplete component inventory")
            if component == "sdk":
                verify_sdk_compatibility(args.directory)
            verify_evidence(files, args.release_id, store.repository, store.tag, source, os.environ["GITHUB_RUN_ID"])
            value = store.get(args.release_id)
            c.require(value["target_commitish"] == source and value["prerelease"] is ("-" in version), "Release source/version changed")
            if component == "sdk" and "-" not in version:
                verify_catalog(args.directory, args.release_id, store.tag, source, value["draft"])
        store.verify(args.release_id, files)
    else:
        c.require(args.directory is not None, "Publication directory is required")
        core_stage(store, args.directory, args.tag.split("/", 1)[0], args.channel, args.action == "finalize")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error))
