#!/usr/bin/env python3
"""Hosted navigation and execution summaries; test acceptance stays with its owner."""
import argparse
from datetime import datetime
import html
import json
import os
from pathlib import Path
import subprocess

from metadata import component_matrices


def escape(value):
    return html.escape(str(value)).replace("|", "&#124;").replace("\n", " ")


def append(lines):
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write("\n".join(lines) + "\n\n")


def run_url():
    return f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"


def collect_jobs():
    # --slurp and --jq cannot be combined. Parse the paginated response here.
    endpoint = (f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
                f"/attempts/{os.environ['GITHUB_RUN_ATTEMPT']}/jobs?per_page=100")
    try:
        pages = json.loads(subprocess.check_output(["gh", "api", "--paginate", "--slurp", endpoint], timeout=60))
        rows = []
        for page in pages:
            for job in page["jobs"]:
                row = {key: job.get(key) for key in ("id", "name", "html_url", "status", "conclusion", "started_at", "completed_at")}
                if not isinstance(row["name"], str) or not row["html_url"].startswith(run_url() + "/job/"):
                    raise ValueError("Invalid job navigation response")
                row["duration"] = "running" if row["status"] == "in_progress" else "—"
                if row["started_at"] and row["completed_at"]:
                    seconds = max(0, int((datetime.fromisoformat(row["completed_at"]) - datetime.fromisoformat(row["started_at"])).total_seconds()))
                    row["duration"] = f"{seconds // 60}m {seconds % 60}s"
                rows.append(row)
        return rows
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError, AttributeError) as error:
        print(f"::warning::Job navigation unavailable; required job/test results still determine acceptance: {error}")
        return []


def job_table(rows):
    lines = ["### Job navigation", "", "| Job | Result | Duration |", "|---|---|---|"]
    for row in rows:
        lines.append(f"| [{escape(row['name'])}]({row['html_url']}) | {escape(row['conclusion'] or row['status'])} | {row['duration']} |")
    if not rows:
        lines.append("| Job links unavailable; see the Actions sidebar | — | — |")
    return lines


def plan(scope):
    matrix = component_matrices(scope=scope, release_packages=os.environ.get("RELEASE_PACKAGES") == "true")
    lines = [f"## Validation plan — {escape(scope)}", "", f"Source: `{escape(os.environ.get('GITHUB_SHA', 'local'))}`", "",
             "| Component job |", "|---|"]
    lines += [f"| {row['job_name']} |" for row in matrix["components"]["include"]]
    native = matrix["platform"]["include"]
    consumers = matrix["consumers"]["include"]
    lines += ["", f"**{len(native)} native process/build jobs + {len(consumers)} native consumer jobs.**",
              "Each native job is one component × platform × partition. Native phases share a six-runner capacity limit.", ""]
    for component in dict.fromkeys(row["component"] for row in native):
        rows = [row for row in native if row["component"] == component]
        lines.append(f"- {component}: {len({row['rid'] for row in rows})} platforms × {len({row['partition'] for row in rows})} partitions = {len(rows)} jobs.")
    lines += [f"- Cross-language compatibility — .NET ↔ Node: {'selected' if matrix['interop'] else 'not selected for this component scope'}.",
              "- Package templates — Generate, build, package, run: " +
              ("selected for release candidates." if os.environ.get("RELEASE_PACKAGES") == "true" else "not selected; no release candidates requested.")]
    append(lines)


def job(directory):
    lines = [f"## {escape(os.environ.get('CI_JOB_NAME', os.environ.get('GITHUB_JOB', 'Execution')))}", "",
             f"Source: `{escape(os.environ.get('GITHUB_SHA', 'local'))}`", ""]
    steps = json.loads(os.environ.get("SUNDER_CI_STEPS", "{}"))
    lines += ["| Step | Result |", "|---|---|"]
    for name, row in steps.items():
        if "outcome" in row:
            lines.append(f"| {escape(name)} | {escape(row['outcome'])} |")
    # Existing result owners already calculated counts and diagnostics. Display
    # each report's scope separately rather than double-counting nested summaries.
    if directory and directory.exists():
        lines += ["", "| Result scope | Passed | Failed | Skipped |", "|---|---:|---:|---:|"]
        diagnostics = []
        for path in sorted(directory.rglob("core-test-summary.json")):
            value = json.loads(path.read_text())
            counts = value["counts"]
            lines.append(f"| {escape(path.parent.relative_to(directory))} | {counts['passed']} | {counts['failed']} | {counts['skipped']} |")
            diagnostics += [f"- {escape(issue)}" for issue in value.get("issues", [])]
        lines += ["", *diagnostics]
    artifact = os.environ.get("CI_REPORT_ARTIFACT")
    if artifact:
        lines += ["", f"Reports and diagnostics: `{escape(artifact)}` in [run artifacts]({run_url()}#artifacts)."]
    append(lines)


def release():
    jobs = json.loads(os.environ["SUNDER_CI_JOB_RESULTS"])
    published = jobs.get("release", {}).get("result") == "success"
    tag = os.environ["GITHUB_REF_NAME"]
    lines = [f"## {escape(tag)} — {'Published' if published else 'Not published'}", "", "| Stage | Result |", "|---|---|"]
    lines += [f"| {escape(name)} | {escape(row.get('result', 'missing'))} |" for name, row in jobs.items()]
    if published:
        lines += ["", f"[Download verified archives](https://github.com/{os.environ['GITHUB_REPOSITORY']}/releases/tag/{tag})"]
    append(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("plan", "job", "jobs", "release"))
    parser.add_argument("--scope", default="all")
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.action == "plan": plan(args.scope)
    elif args.action == "job": job(args.directory)
    elif args.action == "release": release()
    else:
        rows = collect_jobs()
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(rows, indent=2) + "\n")
        append(job_table(rows))


if __name__ == "__main__":
    main()
