#!/usr/bin/env python3
"""Independent App/Host/CLI versions and source-bound native release recovery."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from urllib.parse import quote
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
import github_release as assets
import package_metadata as metadata
import release_coordinates as c
import sdk_release as sdk

ROOT = Path(__file__).resolve().parents[2]
PROJECTS = {component: f"languages/csharp/dotnet/host/{project}/{project}.csproj" for component, project in
            (("app", "Sunder.App"), ("host", "Sunder.Host.Supervisor"), ("cli", "Sunder.Cli"))}
PLAN = "native-release-plan.json"
SELECTION = ROOT / "artifacts/native-release-selection.json"
WORKFLOW_REF = f"{c.REPOSITORY}/{c.SDK_WORKFLOW}@refs/heads/main"


def authored_version(component, root=ROOT, *, source=None, evaluate=False, allow_missing=False):
    project = PROJECTS[component]
    data = c.command("git", "show", f"{c.sha(source, 40)}:{project}", cwd=root) if source else (Path(root) / project).read_bytes()
    tree = ET.fromstring(data)
    versions = [(group, item) for group in tree.findall("PropertyGroup") for item in group.findall("Version")]
    if not versions and allow_missing:
        return None
    c.require(len(versions) == 1 and not any(node.get("Condition") for node in versions[0]),
              f"{component} needs one unconditional authored Version")
    version = versions[0][1].text
    c.require(isinstance(version, str), f"{component} Version is missing")
    metadata.version_key(version)
    c.require("+" not in version, "Native publication versions cannot contain build metadata")
    if evaluate:
        env = {key: value for key, value in os.environ.items() if key not in
               ("GH_TOKEN", "GITHUB_TOKEN", "NODE_AUTH_TOKEN", "NUGET_API_KEY")}
        evaluated = subprocess.check_output(["dotnet", "msbuild", project, "-nologo", "-p:Configuration=Release",
            "-p:ContinuousIntegrationBuild=true", "-getProperty:Version"], cwd=root, env=env, timeout=120).decode().strip()
        c.require(evaluated == version, f"Evaluated {component} version differs from its authored Version")
    return version


def history(component, version, releases):
    current = metadata.version_key(version)
    published = []
    prefix = component + "/v"
    for release in releases:
        if not release["tag_name"].startswith(prefix) or release["draft"]:
            continue
        text = c.tag(release["tag_name"], component)[len(prefix):]
        key = metadata.version_key(text)
        c.require(release["prerelease"] == (not key[4]), "Native release channel differs from its version")
        published.append((key, text))
    stable = [row for row in published if row[0][4]]
    comparable = [row for row in published if not current[4] or row[0][4]]
    major = [row for row in comparable if row[0][0] == current[0]]
    latest = max(major or comparable, default=None)
    return dict(latest=latest[1] if latest else None, present=any(key == current for key, _ in published),
                newer=latest is None or current > latest[0],
                make_latest=component == "app" and current[4] and (not stable or current >= max(stable)[0]))


def tag_source(store):
    values = store.api("git/matching-refs/tags/" + quote(store.tag, safe=""))
    matches = [row for row in values if row["ref"] == "refs/tags/" + store.tag]
    c.require(len(matches) <= 1, "Ambiguous native tag")
    if not matches:
        return None
    value = matches[0]["object"]
    for _ in range(9):
        source = c.sha(value["sha"], 40)
        if value["type"] == "commit":
            return source
        c.require(value["type"] == "tag", "Native tag must resolve to a commit")
        value = store.api("git/tags/" + source)["object"]
    raise ValueError("Excessive annotated-tag depth")


def read_plan(path):
    value = c.decode(c.read(path))
    c.closed(value, "schemaVersion component version tag artifactSourceCommit runId workflowRef")
    c.require(value["schemaVersion"] == 1 and value["component"] in PROJECTS, "Unknown native release plan")
    c.require(value["tag"] == value["component"] + "/v" + value["version"]
              and value["workflowRef"] == WORKFLOW_REF, "Native release identity differs")
    c.tag(value["tag"], value["component"])
    c.sha(value["artifactSourceCommit"], 40)
    c.number(value["runId"])
    return value


def original_run(plan):
    return f"https://github.com/{c.REPOSITORY}/actions/runs/{plan['runId']}"


def select(component, version, source, releases):
    state = history(component, version, releases)
    tag = component + "/v" + version
    row = dict(component=component, version=version, tag=tag, channel="beta" if "-" in version else "stable",
               artifactSourceCommit=source, action="skip", latest=state["latest"], reason="Already published")
    if state["present"] or not state["newer"]:
        if not state["present"]:
            row["reason"] = "No newer version in this release stream"
        return row
    store = assets.GitHubRelease(c.REPOSITORY, tag)
    tagged = tag_source(store)
    drafts = [release for release in releases if release["tag_name"] == tag]
    c.require(len(drafts) <= 1, "Ambiguous native release")
    if drafts:
        draft = store.get(drafts[0]["id"], draft=True)
        c.require(tagged == draft["target_commitish"], "Native draft conflicts with its immutable tag")
        c.require(any(item["name"] == PLAN for item in draft["assets"]),
                  f"{tag} predates the integrated workflow; finish its original release or author a new version")
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / PLAN
            store.download(assets.asset(draft, PLAN), path)
            plan = read_plan(path)
        c.require(plan["tag"] == tag and plan["artifactSourceCommit"] == tagged, "Foreign native release plan")
        c.command("git", "merge-base", "--is-ancestor", tagged, source, cwd=ROOT)
        c.require(authored_version(component, source=tagged) == version, "Original native version differs")
        complete = assets.core_names(component, version, row["channel"]) | assets.EVIDENCE | {PLAN}
        if {item["name"] for item in draft["assets"]} == complete:
            row.update(action="recover", artifactSourceCommit=tagged, reason="Resume retained tested artifacts")
            return row
        c.require(plan["runId"] == os.environ["GITHUB_RUN_ID"] and tagged == source,
                  "Native staging is incomplete; rerun the original workflow: " + original_run(plan))
    else:
        c.require(tagged is None or tagged == source,
                  f"{tag} belongs to another source; finish its original release or author a new version")
    row.update(action="build", reason="Newer authored version")
    return row


def selection(component):
    document = c.decode(c.read(SELECTION))
    c.closed(document, "schemaVersion components")
    c.require(document["schemaVersion"] == 1, "Unknown native selection")
    rows = [row for row in document["components"] if row["component"] == component]
    c.require(len(rows) == 1, "Missing/ambiguous native selection")
    row = rows[0]
    c.closed(row, "component version tag channel artifactSourceCommit action latest reason")
    c.require(row["version"] == authored_version(component) and row["tag"] == component + "/v" + row["version"]
              and row["channel"] == ("beta" if "-" in row["version"] else "stable")
              and row["action"] in ("build", "recover", "skip"), "Native selection differs from authored metadata")
    c.sha(row["artifactSourceCommit"], 40)
    return row


def guard(plan, *, retained=False, evaluate=False):
    sdk.source_guard(plan, retained=retained)
    c.require(authored_version(plan["component"], evaluate=evaluate) == plan["version"], "Native authored version differs")
    if retained and plan["artifactSourceCommit"] != os.environ["GITHUB_SHA"]:
        c.require(authored_version(plan["component"], source=plan["artifactSourceCommit"]) == plan["version"], "Original native version differs")
    if not retained:
        c.require(plan["runId"] == os.environ["GITHUB_RUN_ID"], "Resume the original native run: " + original_run(plan))
    c.remote_tag(plan["tag"], plan["artifactSourceCommit"])


def reserve(component, directory):
    row = selection(component)
    c.require(row["action"] == "build", "Only new native candidates may reserve publication")
    sdk.source_guard(row)
    store = assets.GitHubRelease(c.REPOSITORY, row["tag"])
    if tag_source(store) is None:
        try:
            store.api("git/refs", "POST", dict(ref="refs/tags/" + store.tag, sha=row["artifactSourceCommit"]))
        except (ValueError, subprocess.TimeoutExpired):
            if tag_source(store) != row["artifactSourceCommit"]:
                raise
    c.remote_tag(store.tag, row["artifactSourceCommit"])
    info = store.create(row["artifactSourceCommit"], f"Sunder {component.title()} {row['version']}")
    c.require(info["draft"], "Native version is already published; rerun selection")
    plan = dict(schemaVersion=1, component=component, version=row["version"], tag=row["tag"],
                artifactSourceCommit=row["artifactSourceCommit"], runId=c.number(os.environ["GITHUB_RUN_ID"]), workflowRef=WORKFLOW_REF)
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / PLAN
    if any(item["name"] == PLAN for item in info["assets"]):
        store.download(assets.asset(info, PLAN), path)
        original = read_plan(path)
        c.require(original == plan, "Resume the original native run: " + original_run(original))
    else:
        c.require(not info["assets"], "Unowned native draft contains artifacts")
        c.write_new(path, plan)
        store.upload(info["id"], [path])
    store.download(assets.asset(store.get(info["id"]), PLAN), path)


def artifact_name(component, rid):
    c.require(rid in assets.RIDS, "Unknown native package RID")
    return {"app": "sunder-app-release-", "cli": "sunder-cli-", "host": "sunder-runtime-host-"}[component] + rid


def retained(component, rid, plan_path):
    plan = read_plan(plan_path)
    c.require(plan["component"] == component, "Foreign platform reservation")
    guard(plan)
    store = assets.GitHubRelease(c.REPOSITORY, plan["tag"])
    name = artifact_name(component, rid)
    matches = []
    for page in range(1, 101):
        rows = store.api(f"actions/runs/{plan['runId']}/artifacts?per_page=100&page={page}")["artifacts"]
        matches.extend(row for row in rows if row["name"] == name)
        if len(rows) < 100:
            break
    else:
        raise ValueError("Native artifact pagination limit exceeded")
    c.require(len(matches) <= 1, "Ambiguous retained native platform artifact")
    c.require(not matches or not matches[0]["expired"], "Retained native platform artifact expired; cannot replace original bytes")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(f"reuse={str(bool(matches)).lower()}\n")
    return bool(matches)


def download(component, directory):
    row = selection(component)
    c.require(row["action"] == "recover", "Native download requires a retained selection")
    sdk.source_guard(row, retained=True)
    store = assets.GitHubRelease(c.REPOSITORY, row["tag"])
    info = store.resolve()
    c.require(info is not None and info["target_commitish"] == row["artifactSourceCommit"], "Missing native recovery source")
    info = store.get(info["id"])
    expected = assets.core_names(component, row["version"], row["channel"]) | assets.EVIDENCE | {PLAN}
    c.require({item["name"] for item in info["assets"]} == expected, "Incomplete retained native release")
    directory.mkdir(parents=True, exist_ok=True)
    for item in info["assets"]:
        store.download(item, directory / item["name"])
    plan = read_plan(directory / PLAN)
    c.require(plan["tag"] == row["tag"] and plan["artifactSourceCommit"] == row["artifactSourceCommit"], "Retained native plan differs")
    guard(plan, retained=True)
    assets.verify_evidence(assets.local_files(directory), info["id"], c.REPOSITORY, row["tag"], row["artifactSourceCommit"], plan["runId"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("reserve", "retained", "check", "download"))
    parser.add_argument("component", choices=tuple(PROJECTS), nargs="?")
    parser.add_argument("--rid", choices=assets.RIDS)
    parser.add_argument("--directory", type=Path)
    args = parser.parse_args()
    if args.component is None:
        if args.action != "reserve": parser.error("This action requires a component")
        for component in PROJECTS:
            if selection(component)["action"] == "build":
                reserve(component, ROOT / "artifacts/native-reservations" / component)
        return
    directory = args.directory or ROOT / "artifacts/native-reservations" / args.component
    if args.action == "reserve": reserve(args.component, directory)
    elif args.action == "download": download(args.component, directory)
    elif args.action == "retained": retained(args.component, args.rid, directory / PLAN)
    else:
        plan = read_plan(directory / PLAN)
        c.require(plan["component"] == args.component, "Foreign native reservation")
        guard(plan, evaluate=True)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error))
