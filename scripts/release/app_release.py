#!/usr/bin/env python3
"""Select the independently authored App version and launch its exact-tag workflow."""
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
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
import report

ROOT = Path(__file__).resolve().parents[2]
PROJECT = "languages/csharp/dotnet/host/Sunder.App/Sunder.App.csproj"
WORKFLOW = ".github/workflows/sunder-app-release.yml"
PLAN = ROOT / "artifacts/app-release-plan.json"


def authored_version(root=ROOT, *, source=None, evaluate=False):
    data = c.command("git", "show", f"{c.sha(source, 40)}:{PROJECT}", cwd=root) if source else (Path(root) / PROJECT).read_bytes()
    tree = ET.fromstring(data)
    versions = [(group, item) for group in tree.findall("PropertyGroup") for item in group.findall("Version")]
    c.require(len(versions) == 1 and not any(node.get("Condition") for node in versions[0]), "App needs one unconditional authored Version")
    version = versions[0][1].text
    c.require(isinstance(version, str), "App Version is missing")
    metadata.version_key(version)
    c.require("+" not in version, "App publication version cannot contain build metadata")
    if evaluate:
        env = {key: value for key, value in os.environ.items() if key not in ("GH_TOKEN", "GITHUB_TOKEN", "NODE_AUTH_TOKEN", "NUGET_API_KEY")}
        evaluated = subprocess.check_output(["dotnet", "msbuild", PROJECT, "-nologo", "-p:Configuration=Release",
            "-p:ContinuousIntegrationBuild=true", "-getProperty:Version"], cwd=root, env=env, timeout=120).decode().strip()
        c.require(evaluated == version, "Evaluated App version differs from its authored Version")
    return version


def api(endpoint, method="GET", value=None):
    args = ["gh", "api", "--hostname", "github.com", "--method", method, "-H", "Accept: application/vnd.github+json",
            "-H", "X-GitHub-Api-Version: 2026-03-10", f"repos/{c.REPOSITORY}/{endpoint}"]
    if value is not None: args += ["--input", "-"]
    result = subprocess.run(args, input=c.canonical(value) if value is not None else None, capture_output=True, timeout=120)
    c.require(result.returncode == 0, f"GitHub {method} failed for {endpoint}: {result.stderr.decode().strip()}")
    return c.decode(result.stdout) if result.stdout.strip() else None


def pages(endpoint, member=None):
    result = []
    for page in range(1, 101):
        value = api(f"{endpoint}{'&' if '?' in endpoint else '?'}per_page=100&page={page}")
        rows = value[member] if member else value
        c.require(isinstance(rows, list), "Invalid GitHub page")
        result.extend(rows)
        if len(rows) < 100: return result
    raise ValueError("App release pagination limit exceeded")


def history(version, releases):
    current = metadata.version_key(version)
    published = []
    for release in releases:
        if not release["tag_name"].startswith("app/v") or release["draft"]: continue
        text = c.tag(release["tag_name"], "app")[5:]
        key = metadata.version_key(text)
        c.require(release["prerelease"] == (not key[4]), "App release channel differs from its version")
        published.append((key, text))
    stable = [row for row in published if row[0][4]]
    # A maintenance release advances its own established major. A new older
    # major cannot be backfilled below the published streams. Stable ignores RCs.
    comparable = [row for row in published if not current[4] or row[0][4]]
    major = [row for row in comparable if row[0][0] == current[0]]
    latest = max(major or comparable, default=None)
    return dict(latest=latest[1] if latest else None,
                present=any(key == current for key, _ in published),
                newer=latest is None or current > latest[0],
                make_latest=current[4] and (not stable or current >= max(stable)[0]))


def tag_source(tag):
    values = api("git/matching-refs/tags/" + quote(c.tag(tag, "app"), safe=""))
    matches = [row for row in values if row["ref"] == "refs/tags/" + tag]
    c.require(len(matches) <= 1, "Ambiguous App tag")
    if not matches: return None
    value = matches[0]["object"]
    for _ in range(9):
        source = c.sha(value["sha"], 40)
        if value["type"] == "commit": return source
        c.require(value["type"] == "tag", "App tag must resolve to a commit")
        value = api("git/tags/" + source)["object"]
    raise ValueError("Excessive App annotated-tag depth")


def runs(tag, source):
    rows = pages(f"actions/workflows/{Path(WORKFLOW).name}/runs?head_sha={c.sha(source, 40)}", "workflow_runs")
    result = [row for row in rows if row["head_branch"] == tag]
    for row in result:
        c.require(row["head_sha"] == source and row["path"] == WORKFLOW and row["event"] in ("push", "workflow_dispatch")
                  and row["repository"]["full_name"] == c.REPOSITORY and row["head_repository"]["full_name"] == c.REPOSITORY,
                  "Unexpected App workflow run identity")
        c.number(row["id"])
    return sorted(result, key=lambda row: int(row["id"]))


def original_run(store, draft, candidates):
    c.require(candidates, "Unfinished App draft has no original workflow run; restore its retained invocation")
    evidence = [item for item in draft["assets"] if item["name"] == "release-provenance.json"]
    if evidence:
        c.require(len(evidence) == 1, "Ambiguous App release provenance")
        with tempfile.TemporaryDirectory(dir=os.environ.get("RUNNER_TEMP")) as temporary:
            path = Path(temporary) / "provenance.json"
            store.download(evidence[0], path)
            invocation = c.decode(c.read(path))["invocation"]
        c.require(invocation["repository"] == c.REPOSITORY and invocation["sha"] == draft["target_commitish"]
                  and invocation["ref"] == "refs/tags/" + store.tag and int(invocation["releaseId"]) == draft["id"]
                  and invocation["workflowRef"] == f"{c.REPOSITORY}/{WORKFLOW}@refs/tags/{store.tag}", "Foreign App draft provenance")
        candidates = [row for row in candidates if str(row["id"]) == c.number(invocation["runId"])]
    c.require(len(candidates) == 1, "Ambiguous original App run; resume its original retained artifacts")
    return candidates[0]


def select(version, workflow_source):
    tag = "app/v" + version
    store = assets.GitHubRelease(c.REPOSITORY, tag)
    releases = sdk.github_releases(store)
    state = history(version, releases)
    plan = dict(schemaVersion=1, workflowSourceCommit=workflow_source, artifactSourceCommit=workflow_source,
                version=version, tag=tag, latest=state["latest"], action="none", reason="", runId=None, runUrl=None)
    if state["present"] or not state["newer"]:
        plan["reason"] = "Already published" if state["present"] else "No newer version in this App release stream"
        return plan
    drafts = [row for row in releases if row["tag_name"] == tag]
    c.require(len(drafts) <= 1, "Ambiguous App release")
    source = tag_source(tag)
    if source is None:
        c.require(not drafts, "App draft is missing its immutable tag")
        plan.update(action="dispatch", reason="Newer authored App version")
        return plan
    c.command("git", "merge-base", "--is-ancestor", source, workflow_source, cwd=ROOT)
    c.require(authored_version(source=source) == version, "Existing App tag conflicts with the authored version")
    plan["artifactSourceCommit"] = source
    candidates = runs(tag, source)
    if drafts:
        c.require(drafts[0]["target_commitish"] == source, "App draft conflicts with its tag source")
        run = original_run(store, drafts[0], candidates)
    elif candidates:
        # An interrupted dispatch is recovered by exact tag/source, never by
        # starting a second build of an already attempted version.
        c.require(len(candidates) == 1, "Multiple App runs exist; resume the original run explicitly")
        run = candidates[0]
    else:
        plan.update(action="dispatch", reason="Matching tag exists; release workflow has not started")
        return plan
    plan.update(runId=str(run["id"]), runUrl=f"https://github.com/{c.REPOSITORY}/actions/runs/{run['id']}")
    if run["status"] != "completed":
        plan["reason"] = "App release already queued or running"
    else:
        c.require(run["conclusion"] in ("failure", "cancelled", "timed_out"), "Completed App run has no published release; inspect its original run")
        artifacts = pages(f"actions/runs/{run['id']}/artifacts", "artifacts")
        retained = [row for row in artifacts if row["name"].startswith("sunder-app-release-")]
        c.require(not any(row["expired"] for row in retained), "Original signed App artifacts expired; cannot rebuild staged release bytes")
        if drafts:
            c.require({row["name"] for row in retained} >= {"sunder-app-release-" + rid for rid in assets.RIDS},
                      "App draft recovery requires all original signed platform artifacts")
        plan.update(action="rerun", reason="Resume original failed App run with retained artifacts")
    return plan


def emit(plan):
    values = dict(action=plan["action"], version=plan["version"], reason=plan["reason"], run_url=plan["runUrl"] or "")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.writelines(f"{key}={value}\n" for key, value in values.items())
    report.append(["## App release selection", "", "| Authored | Published baseline | Decision |", "|---|---|---|",
                   f"| {plan['version']} | {plan['latest'] or 'none'} | {plan['reason']} |", "",
                   f"App source: `{plan['artifactSourceCommit']}`", f"Workflow source: `{plan['workflowSourceCommit']}`",
                   *([f"[App release run]({plan['runUrl']}) — publication is reported by that workflow."] if plan["runUrl"] else [])])
    print(json.dumps(plan))


def launch(plan):
    sdk.source_guard(dict(artifactSourceCommit=plan["workflowSourceCommit"]))
    c.require(authored_version() == plan["version"], "App plan differs from authored version")
    current = select(plan["version"], plan["workflowSourceCommit"])
    c.require(current["artifactSourceCommit"] == plan["artifactSourceCommit"], "App tag source changed after selection")
    if current["action"] == "none": return current
    if current["action"] == "rerun":
        api(f"actions/runs/{c.number(current['runId'])}/rerun-failed-jobs", "POST")
        current["reason"] = "Original App run resumption requested"
        return current
    if tag_source(current["tag"]) is None:
        try:
            api("git/refs", "POST", dict(ref="refs/tags/" + current["tag"], sha=current["artifactSourceCommit"]))
        except (ValueError, subprocess.TimeoutExpired):
            # Reconcile one lost acknowledgement; never move or replace a tag.
            pass
    c.remote_tag(current["tag"], current["artifactSourceCommit"])
    try:
        response = api(f"actions/workflows/{Path(WORKFLOW).name}/dispatches", "POST", dict(ref=current["tag"]))
    except (ValueError, subprocess.TimeoutExpired):
        response = None
    if response:
        identifier = c.number(response["workflow_run_id"])
    else:
        candidates = runs(current["tag"], current["artifactSourceCommit"])
        c.require(len(candidates) == 1, "App dispatch acknowledgement unavailable; retry selection to reconcile the exact tag before dispatching again")
        identifier = c.number(candidates[0]["id"])
    current.update(runId=identifier, runUrl=f"https://github.com/{c.REPOSITORY}/actions/runs/{identifier}", reason="App release started")
    return current


def tag_guard(tag):
    c.tag(tag, "app")
    source = c.sha(os.environ["GITHUB_SHA"], 40)
    c.require(os.environ.get("GITHUB_REPOSITORY") == c.REPOSITORY
              and os.environ.get("GITHUB_EVENT_NAME") in ("push", "workflow_dispatch")
              and os.environ.get("GITHUB_REF") == "refs/tags/" + tag
              and os.environ.get("GITHUB_WORKFLOW_REF") == f"{c.REPOSITORY}/{WORKFLOW}@refs/tags/{tag}"
              and os.environ.get("GITHUB_WORKFLOW_SHA") == source, "App release requires its exact tag workflow and source")
    c.local_checkout(source)
    c.remote_tag(tag, source)
    c.require(authored_version() == tag[5:], "App tag differs from its authored version")
    if os.environ["GITHUB_EVENT_NAME"] == "workflow_dispatch":
        c.command("git", "merge-base", "--is-ancestor", source, "origin/main", cwd=ROOT)
    return source


def prepare():
    tag = os.environ["GITHUB_REF_NAME"]
    source = tag_guard(tag)
    version = authored_version(evaluate=True)
    store = assets.GitHubRelease(c.REPOSITORY, tag)
    release = store.resolve()
    if release:
        c.require(release["target_commitish"] == source, "App release source conflicts with its tag")
    if release is None or release["draft"]:
        candidates = runs(tag, source)
        c.require(candidates, "App workflow invocation is not discoverable yet; retry this original run")
        original = original_run(store, release, candidates) if release else candidates[0]
        c.require(str(original["id"]) == os.environ["GITHUB_RUN_ID"], "Resume the original App run; do not rebuild an attempted version")
    skip = release is not None and not release["draft"]
    channel = "beta" if "-" in version else "stable"
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        stream.write(f"version={version}\nchannel={channel}\nskip={str(skip).lower()}\n")
    report.append([f"## App {version} — release plan", "", f"Source: `{source}`", f"Channel: **{channel}**",
                   "Already published; packaging and publication skipped." if skip else
                   "App and Host validation → six native packages → verified installers and Full-only update feeds.",
                   "Windows: signed Setup · macOS: signed/notarized DMG · Linux: unsigned AppImage."])


def retained(rid):
    """A packaging job can fail after upload (cleanup/reporting/cancellation).

    Its retry must reuse that run's already verified archive, not sign it again.
    The release job downloads this same immutable artifact by its unique name.
    """
    c.require(rid in assets.RIDS, "Unknown App package RID")
    identifier = c.number(os.environ["GITHUB_RUN_ID"])
    source = c.sha(os.environ["GITHUB_SHA"], 40)
    tag = c.tag(os.environ["GITHUB_REF_NAME"], "app")
    c.require(os.environ.get("GITHUB_REPOSITORY") == c.REPOSITORY
              and os.environ.get("GITHUB_EVENT_NAME") in ("push", "workflow_dispatch")
              and os.environ.get("GITHUB_REF") == "refs/tags/" + tag
              and os.environ.get("GITHUB_WORKFLOW_REF") == f"{c.REPOSITORY}/{WORKFLOW}@refs/tags/{tag}"
              and os.environ.get("GITHUB_WORKFLOW_SHA") == source, "Unexpected App packaging invocation")
    c.local_checkout(source)
    c.remote_tag(tag, source)
    matches = [row for row in pages(f"actions/runs/{identifier}/artifacts", "artifacts") if row["name"] == "sunder-app-release-" + rid]
    c.require(len(matches) <= 1, "Ambiguous retained App platform artifact")
    c.require(not matches or not matches[0]["expired"], "Retained App platform artifact expired; cannot replace original signed bytes")
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        stream.write(f"reuse={str(bool(matches)).lower()}\n")
    if matches:
        report.append([f"### {rid} — reusing original verified package", "",
                       f"Artifact `{matches[0]['name']}` from this exact source/run is retained; signing and packaging are already complete."])


def summary():
    jobs = json.loads(os.environ.get("SUNDER_CI_JOB_RESULTS", "{}"))
    selected = jobs.get("select-app", {}).get("outputs", {})
    launched = jobs.get("release-app", {}).get("outputs", {})
    if "select-app" in jobs:
        report.append(["## App release", "", f"Version: **{report.escape(selected.get('version', 'selection unavailable'))}**",
                       report.escape(launched.get("reason") or selected.get("reason") or "Selection failed; see the job logs."),
                       f"Launch job: **{report.escape(jobs.get('release-app', {}).get('result', 'missing'))}**",
                       *([f"[Follow the App release]({url}) — this main run reports launch, not completed App publication."]
                         if (url := launched.get("run_url") or selected.get("run_url")) else [])])
    else:
        published = jobs.get("release", {}).get("result") == "success"
        skipped = jobs.get("prepare", {}).get("outputs", {}).get("skip") == "true"
        tag = os.environ.get("GITHUB_REF_NAME", "")
        report.append([f"## {report.escape(tag)} — {'Published' if published else 'Already published' if skipped else 'Not published'}", "",
                       *[f"- {report.escape(name)}: **{report.escape(row.get('result', 'missing'))}**" for name, row in jobs.items()],
                       *([f"[Download App installers](https://github.com/{c.REPOSITORY}/releases/tag/{quote(tag, safe='/')})"] if published or skipped else [])])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("select", "launch", "prepare", "retained", "summary"))
    parser.add_argument("--plan", type=Path, default=PLAN)
    parser.add_argument("--rid")
    args = parser.parse_args()
    if args.action == "prepare": return prepare()
    if args.action == "retained": return retained(args.rid)
    if args.action == "summary": return summary()
    if args.action == "select":
        source = c.sha(os.environ["GITHUB_SHA"], 40)
        sdk.source_guard(dict(artifactSourceCommit=source))
        plan = select(authored_version(evaluate=True), source)
        args.plan.parent.mkdir(parents=True, exist_ok=True)
        c.write_new(args.plan, plan)
    else:
        plan = launch(c.decode(c.read(args.plan)))
    emit(plan)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        report.append(["## App release blocked", "", report.escape(error)])
        raise SystemExit(str(error))
