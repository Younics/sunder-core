#!/usr/bin/env python3
"""Select releases before installing toolchains, then select their existing test owners."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
import native_release as native
import package_metadata as packages
import release_coordinates as c
import sdk_release as sdk
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
import metadata
import report

ROOT = Path(__file__).resolve().parents[2]
VALIDATION = ROOT / "artifacts/validation-selection.json"


def requirements(package_rows, native_components, *, recover=False, scopes=None, tooling=False, root=ROOT):
    policy = json.loads((Path(root) / "eng/test-oracles/core-test-policy.json").read_text())
    owners, scenarios = set(), set()
    candidates = [] if recover else [row for row in package_rows if row["selected"]]
    if scopes is not None:
        for scope in scopes:
            owners.update(policy["scopes"][scope])
        if {"dotnet", "host", "npm"} <= owners:
            scenarios.add("interop")
    for kind in {row["kind"] for row in candidates} | set(native_components):
        rule = policy["releaseValidation"][kind]
        owners.update(rule["components"])
        scenarios.update(rule["scenarios"])
    for row in candidates:
        owners.update(policy["packageConsumers"].get(row["name"], []))
    if candidates:
        scenarios.add("generated-templates")
    # The Host portable/native jobs already own both client test projects.
    if "host" in owners:
        scenarios.discard("cli-clients")
    if owners or scenarios or tooling:
        owners.add("ci")
    return dict(schemaVersion=1, components=[metadata.COMPONENTS[owner][0] for owner in policy["scopes"]["all"] if owner in owners],
                scenarios=sorted(scenarios), candidatePackages=bool(candidates))


def changed_versions(current, previous):
    old = {(row["kind"], row["name"]): row["version"] for row in previous}
    return [dict(row, selected=old.get((row["kind"], row["name"])) != row["version"], latest=old.get((row["kind"], row["name"])))
            for row in current]


def tooling_changed(paths):
    return any(path.startswith((".github/", "scripts/", "eng/")) or path in
               ("global.json", "languages/csharp/dotnet/Directory.Build.props", "languages/csharp/dotnet/Directory.Packages.props")
               for path in paths)


def outputs(validation, sdk_plan, recover, native_rows, publish):
    owners = ["ci" if scope == "repository" else scope for scope in validation["components"]]
    matrix = metadata.component_matrices(owners=owners)
    selected_native = [row for row in native_rows if row["action"] != "skip"] if publish else []
    builds = [row for row in selected_native if row["action"] == "build"]
    app_rows, binaries = [], []
    for row in builds:
        for platform in metadata.platforms(profile="app" if row["component"] == "app" else "native")["include"]:
            label = "CLI" if row["component"] == "cli" else row["component"].title()
            item = {**row, **platform, "job_name": f"Package {label} · {metadata.platform_name(platform['rid'])}"}
            (app_rows if row["component"] == "app" else binaries).append(item)
    consumers = [{"scenario": name, "job_name": {"interop": "Test .NET ↔ Node", "generated-templates": "Test package consumers",
                   "cli-clients": "Test CLI clients"}[name]} for name in validation["scenarios"]]
    return dict(publish=publish, work=bool(owners), components=matrix["components"], platform=matrix["platform"],
                consumers=matrix["consumers"], platform_tests=matrix["platform_tests"], consumer_tests=matrix["consumer_tests"],
                scenarios={"include": consumers}, scenario_tests=bool(consumers), candidate_packages=validation["candidatePackages"],
                selected_count=len(sdk.selected(sdk_plan)) if publish else 0,
                nuget_count=len(sdk.selected(sdk_plan, "nuget")) if publish else 0,
                npm_count=len(sdk.selected(sdk_plan, "npm")) if publish else 0, recover=recover,
                native_count=len(selected_native), native_build_count=len(builds),
                app_packages=bool(app_rows), app_matrix={"include": app_rows},
                binary_packages=bool(binaries), binary_matrix={"include": binaries},
                native_matrix={"include": [{**row, "name": "CLI" if row["component"] == "cli" else row["component"].title()}
                                           for row in selected_native]})


def select(mode, scope):
    event = os.environ["GITHUB_EVENT_NAME"]
    source = c.sha(os.environ["GITHUB_SHA"], 40)
    payload = c.decode(Path(os.environ["GITHUB_EVENT_PATH"]).read_bytes())
    publish = event == "push" or event == "workflow_dispatch" and mode == "release"
    c.require(event in ("push", "workflow_dispatch", "pull_request", "merge_group", "schedule"), "Unsupported selection event")
    current = packages.packages(ROOT)
    native_rows = []
    recover = False
    base = None
    if publish:
        sdk.source_guard(dict(artifactSourceCommit=source))
        releases = sdk.github_releases(sdk.store_for(current[0]))
        sdk_plan, recover = sdk.prepare(ROOT / "artifacts" / sdk.PLAN, releases=releases)
        native_rows = [native.select(component, native.authored_version(component), source, releases) for component in native.PROJECTS]
        before = payload.get("before")
        base = c.sha(before, 40) if before and before != "0" * 40 else None
        changed = c.command("git", "diff", "--name-only", base, source, cwd=ROOT).decode().splitlines() if base else []
        validation = requirements(sdk_plan["packages"], [row["component"] for row in native_rows if row["action"] == "build"],
                                  recover=recover, tooling=tooling_changed(changed))
    else:
        if event in ("pull_request", "merge_group"):
            base = c.sha(payload["pull_request"]["base"]["sha"] if event == "pull_request" else payload["merge_group"]["base_sha"], 40)
            rows = changed_versions(current, packages.packages(ROOT, source=base))
            components = [component for component in native.PROJECTS if native.authored_version(component) !=
                          native.authored_version(component, source=base, allow_missing=True)]
            changed = c.command("git", "diff", "--name-only", base, source, cwd=ROOT).decode().splitlines()
            validation = requirements(rows, components, tooling=tooling_changed(changed))
        else:
            c.require(event == "schedule" or mode == "validate", "Manual validation requires validate mode")
            rows = [dict(row, selected=False, latest=None) for row in current]
            validation = requirements(rows, [], scopes=["all" if event == "schedule" else scope])
        sdk_plan = dict(schemaVersion=1, artifactSourceCommit=source, packages=rows)
        c.write_new(ROOT / "artifacts" / sdk.PLAN, sdk_plan)
    c.write_new(native.SELECTION, dict(schemaVersion=1, components=native_rows))
    c.write_new(VALIDATION, validation)
    result = outputs(validation, sdk_plan, recover, native_rows, publish)
    result["compatibility_base"] = base or ""
    metadata.emit(result)
    lines = ["## Release selection" if publish else "## Validation selection", "", "| Output | Authored | Decision |", "|---|---|---|"]
    for row in sdk_plan["packages"]:
        decision = ("Recover retained artifacts" if recover else "Release" if publish else "Validate") if row["selected"] else "Not selected"
        lines.append(f"| {report.escape(row['name'])} | {row['version']} | {decision} |")
    for row in native_rows:
        lines.append(f"| {row['component'].title()} | {row['version']} | {report.escape(row['reason'])} |")
    lines += ["", "Required components: " + (", ".join(validation["components"]) or "none"),
              "Required consumer scenarios: " + (", ".join(validation["scenarios"]) or "none")]
    if not result["work"] and not result["selected_count"] and not result["native_count"]:
        lines += ["", "No new versions or selected checks. Finished after selection."]
    report.append(lines)


def summary():
    jobs = json.loads(os.environ["SUNDER_CI_JOB_RESULTS"])
    required = set()
    for selector in ("select", "select-checks"):
        if jobs.get(selector, {}).get("result") != "success":
            continue
        outputs = jobs[selector]["outputs"]
        if outputs.get("work") == "true":
            required.update(("preflight", "validation"))
        if int(outputs.get("selected_count", "0")) > 0:
            required.update(("stage", "finalize"))
            required.update("publish-" + kind for kind in ("npm", "nuget") if int(outputs.get(kind + "_count", "0")) > 0)
        if int(outputs.get("native_count", "0")) > 0:
            required.add("publish-native")
        if int(outputs.get("native_build_count", "0")) > 0:
            required.add("prepare-native")
        for flag, job in (("app_packages", "package-app"), ("binary_packages", "package-native")):
            if outputs.get(flag) == "true":
                required.add(job)
    failed = sorted({name for name, row in jobs.items() if row["result"] in ("failure", "cancelled")}
                    | {name for name in required if jobs.get(name, {}).get("result") != "success"})
    lines = ["## " + ("Failed" if failed else "Completed"), "", "| Job | Result |", "|---|---|"]
    lines += [f"| {report.escape(name)} | {report.escape(row['result'])} |" for name, row in jobs.items()]
    report.append(lines)
    if failed:
        raise ValueError("Required execution failed or is missing: " + ", ".join(failed))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("select", "summary"))
    parser.add_argument("--mode", choices=("release", "validate"), default="release")
    parser.add_argument("--scope", choices=("all", "sdk", "npm", "dotnet", "app", "host", "cli", "repository"), default="all")
    args = parser.parse_args()
    if args.action == "summary":
        summary()
    else:
        (ROOT / "artifacts").mkdir(exist_ok=True)
        select(args.mode, args.scope)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        report.append(["## Selection or publication failed", "", report.escape(error)])
        raise SystemExit(str(error))
