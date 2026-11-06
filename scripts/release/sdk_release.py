#!/usr/bin/env python3
"""Release each public project only when its authored version is newer."""
import argparse
from contextlib import nullcontext
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import HTTPRedirectHandler, Request, build_opener

sys.dont_write_bytecode = True
import github_release as assets
import package_metadata as metadata
import release_coordinates as c

ROOT = Path(__file__).resolve().parents[2]
PLAN = "release-plan.json"
version_key = metadata.version_key


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def registry_json(url):
    """A 404 means absent; service/authentication failures are not absence."""
    c.require(url.startswith(("https://api.nuget.org/", "https://registry.npmjs.org/")), "Unexpected registry")
    try:
        with build_opener(NoRedirect).open(Request(url, headers={"Accept": "application/json"}), timeout=30) as response:
            c.require(response.status == 200, "Registry lookup did not return HTTP 200")
            raw = response.read(8 * 1024 * 1024 + 1)
            c.require(len(raw) <= 8 * 1024 * 1024, "Oversized registry response")
            return c.decode(raw)
    except HTTPError as error:
        code = error.code
        error.close()
        if code == 404:
            return None
        raise ValueError(f"Registry lookup failed (HTTP {code}): {url}") from None


def package_history(kind, name, version, get=registry_json):
    nuget = kind == "nuget"
    document = get(f"https://api.nuget.org/v3-flatcontainer/{name.lower()}/index.json" if nuget
                   else "https://registry.npmjs.org/" + quote(name, safe=""))
    if document is None:
        versions = []
    else:
        c.require(isinstance(document, dict) and isinstance(document.get("versions"), list if nuget else dict)
                  and document["versions"] and (nuget or document.get("name") == name), "Invalid published version history: " + name)
        versions = list(document["versions"])
    keys = {value: version_key(value, nuget) for value in versions}
    current = version_key(version, nuget)
    highest = max(keys, key=keys.get) if keys else None
    return dict(latest=highest, present=current in keys.values(), newer=highest is None or current > keys[highest])


def github_releases(store):
    result = []
    for page in range(1, 101):
        values = store.api(f"releases?per_page=100&page={page}")
        c.require(isinstance(values, list), "Invalid GitHub release page")
        result.extend(values)
        if len(values) < 100:
            c.require(len({item["tag_name"] for item in result}) == len(result), "Ambiguous release tags")
            return result
    raise ValueError("Release pagination limit exceeded")


def select(packages, source, releases, get=registry_json):
    rows = []
    for package in packages:
        history = package_history(package["kind"], package["name"], package["version"], get)
        # Published GitHub archives also cover historical combined sdk/v releases.
        published = []
        prefix, suffix = (package["name"] + ".", ".nupkg") if package["kind"] == "nuget" else (
            package["name"].lstrip("@").replace("/", "-") + "-", ".tgz")
        for release in releases:
            if release["draft"]:
                continue
            for asset in release["assets"]:
                name = asset["name"]
                if name.startswith(prefix) and name.endswith(suffix):
                    text = name[len(prefix):-len(suffix)]
                    try:
                        published.append((version_key(text, package["kind"] == "nuget"), text))
                    except ValueError:
                        continue  # An unrelated asset is not a package version.
        if published:
            previous = max(published)
            history["newer"] = history["newer"] and version_key(package["version"], package["kind"] == "nuget") > previous[0]
            if history["latest"] is None or previous[0] > version_key(history["latest"], package["kind"] == "nuget"):
                history["latest"] = previous[1]
        rows.append(dict(package, selected=history["newer"], latest=history["latest"]))
    return dict(schemaVersion=1, artifactSourceCommit=source, packages=rows)


def load(path):
    value = c.decode(c.read(path))
    c.closed(value, "schemaVersion artifactSourceCommit packages")
    c.require(value["schemaVersion"] == 1, "Unknown release plan")
    c.sha(value["artifactSourceCommit"], 40)
    expected = metadata.packages(ROOT)
    c.require(len(value["packages"]) == len(expected), "Release package inventory differs")
    for row, package in zip(value["packages"], expected):
        c.closed(row, "kind name version project tag archive selected latest")
        c.require(all(row[key] == item for key, item in package.items()) and type(row["selected"]) is bool,
                  "Release plan differs from project metadata")
    return value


def selected(plan, kind=None):
    return [row for row in plan["packages"] if row["selected"] and (kind is None or row["kind"] == kind)]


def source_guard(plan, *, retained=False):
    source = c.sha(os.environ["GITHUB_SHA"], 40)
    c.require(os.environ.get("GITHUB_REPOSITORY") == c.REPOSITORY and os.environ.get("GITHUB_REF") == "refs/heads/main"
              and os.environ.get("GITHUB_EVENT_NAME") in ("push", "workflow_dispatch")
              and os.environ.get("GITHUB_REF_PROTECTED") == "true"
              and os.environ.get("GITHUB_WORKFLOW_SHA") == source
              and os.environ.get("GITHUB_WORKFLOW_REF") == f"{c.REPOSITORY}/{c.SDK_WORKFLOW}@refs/heads/main",
              "Publication requires the protected-main workflow source")
    c.require(c.command("git", "rev-parse", "HEAD", cwd=ROOT).decode().strip() == source, "Release checkout differs")
    c.require(not c.command("git", "status", "--porcelain", "--untracked-files=all", cwd=ROOT).strip(), "Release source must be clean")
    original = c.sha(plan["artifactSourceCommit"], 40)
    if original != source:
        c.require(retained, "New package artifacts must use the current workflow source")
        try:
            c.command("git", "merge-base", "--is-ancestor", original, source, cwd=ROOT)
        except subprocess.CalledProcessError as error:
            raise ValueError("Retained package source must be an ancestor of the protected-main workflow source") from error


def store_for(row):
    return assets.GitHubRelease(c.REPOSITORY, row["tag"])


def prepare(path):
    packages = metadata.packages(ROOT, evaluate=True)
    source = c.sha(os.environ["GITHUB_SHA"], 40)
    releases = github_releases(store_for(packages[0]))
    drafts = [release for release in releases if release["draft"] and release["tag_name"] in {row["tag"] for row in packages}]
    recover = bool(drafts)
    if recover:
        first = drafts[0]
        store = assets.GitHubRelease(c.REPOSITORY, first["tag_name"])
        item = assets.asset(first, PLAN)
        store.download(item, path)
        plan = load(path)
        source_guard(plan, retained=True)
        c.require({release["tag_name"] for release in drafts} <= {row["tag"] for row in selected(plan)},
                  "Conflicting unfinished package release plans")
        by_tag = {release["tag_name"]: release for release in releases}
        for row in selected(plan):
            release = by_tag.get(row["tag"])
            c.require(release and release["target_commitish"] == plan["artifactSourceCommit"]
                      and {row["archive"], PLAN, *assets.EVIDENCE} <= {asset["name"] for asset in release["assets"]},
                      "Staging was interrupted; rerun the original failed job with its retained tested artifacts")
        print(f"Recovering retained packages from {plan['artifactSourceCommit']} using workflow {source}; no rebuild.")
    else:
        plan = select(packages, source, releases)
        source_guard(plan)
        c.write_new(path, plan)
    outputs = dict(selected_count=len(selected(plan)), nuget_count=len(selected(plan, "nuget")), npm_count=len(selected(plan, "npm")), recover=str(recover).lower())
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.writelines(f"{key}={value}\n" for key, value in outputs.items())
    print(json.dumps(outputs))
    summary(plan, {"select": {"result": "success", "outputs": outputs}})


def pack(plan, directory, kind=None):
    import sdk_release_documentation as documentation
    directory.mkdir(parents=True, exist_ok=True)
    release = os.environ.get("GITHUB_WORKFLOW_REF") == f"{c.REPOSITORY}/{c.SDK_WORKFLOW}@refs/heads/main"
    rows = selected(plan, kind)
    if not rows:
        return
    context = documentation.pack_context(ROOT, plan["artifactSourceCommit"], [row["name"] for row in rows]) if release else nullcontext()
    with context:
        for row in rows:
            if row["kind"] == "nuget":
                subprocess.run(["dotnet", "pack", row["project"], "-c", "Release", "--no-restore", "--no-build",
                    "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false", "-o", str(directory)], cwd=ROOT, check=True)
                spec = importlib.util.spec_from_file_location("verify_nuget", ROOT / "scripts/release/verify-nuget-package-set.py")
                module = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(module)
                module.verify_archive(directory / row["archive"], row["name"], row["version"], plan["artifactSourceCommit"])
            else:
                with tempfile.TemporaryDirectory() as temporary:
                    work = Path(temporary)
                    command = ["npm", "pack", "--prefix", "languages/typescript/node", "--workspace", row["name"], "--json", "--pack-destination", str(work)]
                    report = subprocess.check_output(command, cwd=ROOT)
                    report_path = Path(temporary) / "pack.json"
                    report_path.write_bytes(report)
                    manifest = c.decode(c.read(ROOT / row["project"] / "package.json"))
                    tool_manifest = c.decode(c.read(ROOT / "languages/typescript/node/packages/package-tool/package.json"))
                    subprocess.run(["node", str(ROOT / "scripts/release/verify-node-package-artifacts.mjs"), row["version"],
                        manifest.get("dependencies", {}).get("@younics/sunder-sdk", "-"), tool_manifest["dependencies"]["vite"], str(work), str(report_path), row["name"]], check=True)
                    shutil.copyfile(work / row["archive"], directory / row["archive"])
    if release:
        documentation.verify(ROOT, [directory], plan["artifactSourceCommit"], package_names=[row["name"] for row in rows])


def feed(plan, candidate, directory):
    """Template tests use selected archives plus exact already-published dependencies."""
    directory.mkdir(parents=True, exist_ok=True)
    packages = []
    for row in plan["packages"]:
        test_row = dict(row)
        if not row["selected"]:
            history = package_history(row["kind"], row["name"], row["version"], get=registry_json)
            test_row["version"] = row["version"] if history["present"] else history["latest"]
            c.require(test_row["version"], "Missing published dependency: " + row["name"])
            test_row["archive"] = metadata.archive_name(row["kind"], row["name"], test_row["version"])
        packages.append({key: test_row[key] for key in ("kind", "name", "version", "archive")})
        if row["selected"]:
            shutil.copyfile(candidate / row["archive"], directory / row["archive"])
            continue
        download_dependency(test_row, directory)
    # Independently versioned Worker/Avalonia/Stacks archives may refer to an
    # older exact SDK. Preserve that real closure, not a forced same-version set.
    queue = [item for item in packages if item["kind"] == "nuget"]
    known = {item["name"].casefold(): item["name"] for item in queue}
    seen = set()
    while queue:
        row = queue.pop(0)
        if row["archive"] in seen: continue
        seen.add(row["archive"])
        c.require(len(seen) <= 128, "Oversized public NuGet dependency closure")
        with zipfile.ZipFile(directory / row["archive"]) as archive:
            names = [name for name in archive.namelist() if name.endswith(".nuspec")]
            c.require(len(names) == 1 and archive.getinfo(names[0]).file_size <= 1024 * 1024, "Missing/bounded NuGet metadata required")
            raw = archive.read(names[0])
            c.require(not any(marker in raw.upper().replace(b"\0", b"") for marker in (b"<!DOCTYPE", b"<!ENTITY")), "Unsupported NuGet XML markup")
            nuspec = ET.fromstring(raw)
            for dependency in nuspec.iter():
                name, constraint = dependency.get("id", ""), dependency.get("version", "")
                if dependency.tag.rsplit("}", 1)[-1] != "dependency" or name.casefold() not in known: continue
                if not (constraint.startswith("[") and constraint.endswith("]") and "," not in constraint): continue
                version = constraint[1:-1]
                metadata.version_key(version, True)
                name = known[name.casefold()]
                item = dict(kind="nuget", name=name, version=version, archive=metadata.archive_name("nuget", name, version))
                if not (directory / item["archive"]).exists(): download_dependency(item, directory)
                queue.append(item)
    c.write_new(directory / "test-feed.json", dict(packages=packages))


def download_dependency(row, directory):
    integrity = None
    if row["kind"] == "nuget":
        name, version = row["name"].lower(), row["version"].lower()
        url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg"
    else:
        document = registry_json("https://registry.npmjs.org/" + quote(row["name"], safe="") + "/" + quote(row["version"], safe=""))
        c.require(document and document["name"] == row["name"] and document["version"] == row["version"], "Missing published dependency: " + row["name"])
        url, integrity = document["dist"]["tarball"], document["dist"]["integrity"]
        c.require(url.startswith("https://registry.npmjs.org/"), "Unexpected npm download origin")
    digest = hashlib.sha512()
    with build_opener(NoRedirect).open(url, timeout=120) as response, (directory / row["archive"]).open("xb") as output:
        c.require(response.status == 200, "Dependency download did not return HTTP 200")
        size = 0
        while data := response.read(1024 * 1024):
            size += len(data)
            c.require(size <= assets.MAX_ASSET, "Oversized published dependency")
            digest.update(data)
            output.write(data)
        c.require(size > 0, "Empty published dependency")
    if integrity:
        import base64
        c.require(integrity == "sha512-" + base64.b64encode(digest.digest()).decode(), "Published npm dependency integrity differs")


def release_directory(directory, row):
    return directory / row["tag"].split("/")[0]


def verify_retained(store, info, directory, plan, row):
    c.require(info and info["tag_name"] == row["tag"] and info["target_commitish"] == plan["artifactSourceCommit"]
              and info["prerelease"] is store.prerelease, "Retained package release source/version differs")
    files = assets.local_files(directory)
    c.require(c.decode(c.read(directory / PLAN)) == plan and row["archive"] in files, "Retained release plan/archive differs")
    expected = {PLAN, row["archive"], *assets.EVIDENCE}
    if row["name"] == "Sunder.Sdk.Stacks":
        expected.add("sunder-platform-contracts.v1.json")
    c.require(set(files) == expected, "Retained package release inventory differs")
    proof = assets.verify_evidence(files, info["id"], c.REPOSITORY, row["tag"], plan["artifactSourceCommit"])
    c.require(proof["invocation"]["workflowRef"] == f"{c.REPOSITORY}/{c.SDK_WORKFLOW}@refs/heads/main", "Unexpected package publication workflow")
    c.remote_tag(row["tag"], plan["artifactSourceCommit"])
    return store.verify(info["id"], files)


def stage(plan, candidate, directory):
    source_guard(plan)
    for row in selected(plan):
        store = store_for(row)
        folder = release_directory(directory, row)
        folder.mkdir(parents=True, exist_ok=True)
        # Query ref creation through the transport's exact readback, never move it.
        try:
            store.api("git/refs", "POST", dict(ref="refs/tags/" + row["tag"], sha=plan["artifactSourceCommit"]))
        except (ValueError, subprocess.TimeoutExpired):
            pass
        c.remote_tag(row["tag"], plan["artifactSourceCommit"])
        info = store.create(plan["artifactSourceCommit"], row["name"] + " " + row["version"])
        shutil.copyfile(candidate / row["archive"], folder / row["archive"])
        c.write_new(folder / PLAN, plan)
        catalog = candidate / "sunder-platform-contracts.v1.json"
        if row["name"] == "Sunder.Sdk.Stacks" and catalog.exists():
            shutil.copyfile(catalog, folder / catalog.name)
        # Keep original evidence on an interrupted upload; only generate missing files.
        old = {item["name"]: item for item in info["assets"]}
        for name in assets.EVIDENCE & old.keys():
            store.download(old[name], folder / name)
        if not assets.EVIDENCE <= {path.name for path in folder.iterdir()}:
            with tempfile.TemporaryDirectory() as temporary:
                work = Path(temporary)
                for path in folder.iterdir():
                    if path.name not in assets.EVIDENCE:
                        shutil.copyfile(path, work / path.name)
                env = dict(os.environ, GITHUB_RELEASE_ID=str(info["id"]))
                subprocess.run(["bash", str(ROOT / "scripts/release/generate-release-evidence.sh"), str(work),
                    "sunder-core=" + plan["artifactSourceCommit"]], env=env, check=True)
                for name in assets.EVIDENCE - {path.name for path in folder.iterdir()}:
                    shutil.copyfile(work / name, folder / name)
        if info["draft"]:
            store.upload(info["id"], [folder / PLAN, *[path for path in folder.iterdir() if path.name != PLAN]])
        verify_retained(store, info, folder, plan, row)


def download(plan, directory):
    source_guard(plan, retained=True)
    for row in selected(plan):
        store = store_for(row)
        info = store.resolve()
        c.require(info and info["target_commitish"] == plan["artifactSourceCommit"], "Missing original package release")
        info = store.get(info["id"])
        folder = release_directory(directory, row)
        folder.mkdir(parents=True, exist_ok=True)
        for item in info["assets"]:
            store.download(item, folder / item["name"])
        verify_retained(store, info, folder, plan, row)


def publish(plan, directory, kind):
    source_guard(plan, retained=True)
    for row in selected(plan, kind):
        folder = release_directory(directory, row)
        info = store_for(row).resolve()
        verify_retained(store_for(row), info, folder, plan, row)
        if not info["draft"]:
            print(f"Skipping finalized {row['name']}@{row['version']}.", flush=True)
            continue
        subprocess.run(["bash", str(ROOT / "scripts/release/publish-sdk.sh"), kind, row["name"], row["version"],
                        str(folder / row["archive"]), plan["artifactSourceCommit"]], check=True)


def finalize(plan, directory):
    # The workflow requires successful publishing jobs. Registry acceptance is the
    # completion boundary; registry indexing/download availability is not a gate.
    source_guard(plan, retained=True)
    for row in selected(plan):
        store = store_for(row)
        info = store.resolve()
        folder = release_directory(directory, row)
        verify_retained(store, info, folder, plan, row)
        if info["draft"]:
            try:
                store.api("releases/" + str(info["id"]), "PATCH", dict(draft=False, make_latest="false"))
            except (ValueError, subprocess.TimeoutExpired):
                pass
        store.verify(info["id"], assets.local_files(folder), draft=False)


def summary(plan, jobs):
    """Hosted presentation is explicit; library calls and fixtures never emit it."""
    if not os.environ.get("GITHUB_STEP_SUMMARY"):
        return
    lines = ["## SDK release", "", "| Stage | Result |", "|---|---|"]
    labels = {"select": "Select independent package versions", "validation": "Required source validation",
              "stage": "Retain tested packages / recover original archives", "publish-npm": "Publish missing npm versions",
              "publish-nuget": "Publish missing NuGet versions", "finalize": "Finalize accepted package releases"}
    recovered = jobs.get("select", {}).get("outputs", {}).get("recover") == "true"
    for name, row in jobs.items():
        if name not in labels:
            continue
        result = row.get("result", "missing")
        if result == "skipped":
            kind = {"publish-npm": "npm", "publish-nuget": "nuget"}.get(name)
            if name == "validation" and recovered:
                result = "Using retained tested candidate"
            elif plan and not selected(plan, kind):
                result = "No newer version selected"
            else:
                result = "Blocked by prerequisite"
        lines.append(f"| {labels[name]} | {result} |")
    if plan:
        lines += ["", f"Artifact source: `{plan['artifactSourceCommit']}`",
                  f"Workflow source: `{os.environ.get('GITHUB_SHA', plan['artifactSourceCommit'])}`", "",
                  "| Package | Authored | Published baseline | Registry | Selection |", "|---|---|---|---|---|"]
        for row in plan["packages"]:
            selection = ("Retained tested archive" if recovered else "Selected") if row["selected"] else "Already published / no newer version"
            name = row["name"]
            if row["selected"] and jobs.get("finalize", {}).get("result") == "success":
                name = f"[{name}](https://github.com/{c.REPOSITORY}/releases/tag/{row['tag']})"
                selection = "Registry accepted; GitHub release finalized"
            lines.append(f"| {name} | {row['version']} | {row['latest'] or 'none'} | {row['kind']} | {selection} |")
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
        stream.write("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("metadata", "history", "select", "pack", "feed", "stage", "download", "npm", "nuget", "finalize", "summary"))
    parser.add_argument("--name")
    parser.add_argument("--version")
    parser.add_argument("--kind", choices=("nuget", "npm"))
    parser.add_argument("--plan", type=Path, default=ROOT / "artifacts" / PLAN)
    parser.add_argument("--directory", type=Path, default=ROOT / "artifacts/packages")
    parser.add_argument("--candidate", type=Path, default=ROOT / "artifacts/packages")
    args = parser.parse_args()
    if args.action == "summary":
        summary(load(args.plan) if args.plan.exists() else None, json.loads(os.environ["SUNDER_CI_JOB_RESULTS"]))
        return
    if args.action == "history":
        c.require(args.name and args.version and args.kind, "Package history needs kind/name/version")
        print(json.dumps(package_history(args.kind, args.name, args.version)))
        return
    if args.action == "metadata":
        print(json.dumps(metadata.packages(ROOT, evaluate=True)))
        return
    if args.action == "select":
        args.plan.parent.mkdir(parents=True, exist_ok=True)
        prepare(args.plan)
        return
    plan = load(args.plan)
    if args.action == "pack": pack(plan, args.directory.resolve(), args.kind)
    elif args.action == "feed": feed(plan, args.candidate.resolve(), args.directory.resolve())
    elif args.action == "stage": stage(plan, args.candidate.resolve(), args.directory.resolve())
    elif args.action == "download": download(plan, args.directory.resolve())
    elif args.action == "finalize": finalize(plan, args.directory.resolve())
    else: publish(plan, args.directory.resolve(), args.action)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error))
