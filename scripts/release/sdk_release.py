#!/usr/bin/env python3
"""Select an authored SDK version and start/resume its existing tag workflow."""
import argparse
from contextlib import nullcontext
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import HTTPRedirectHandler, Request, build_opener

sys.dont_write_bytecode = True
import github_release as assets
import release_coordinates as c

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = c.SDK_WORKFLOW.rsplit("/", 1)[-1]
ACTIVE = {"queued", "in_progress", "waiting", "pending", "requested"}


def version_key(value):
    c.tag("sdk/v" + value, "sdk")
    c.require("+" not in value, "SDK versions cannot contain build metadata")
    return published_version_key(value)


def published_version_key(value, nuget=False):
    c.require(isinstance(value, str), "Published package version must be a string")
    core, _, suffix = value.split("+", 1)[0].partition("-")
    c.require("-" not in value.split("+", 1)[0] or suffix, "Invalid published prerelease version: " + value)
    numbers = core.split(".")
    c.require(len(numbers) == 3 or nuget and len(numbers) == 4, "Invalid published package version: " + value)
    # NuGet also supports normalized four-part legacy versions. Do not silently
    # ignore one when deciding whether an authored version is actually newer.
    revision = numbers[3] if len(numbers) == 4 else "0"
    c.require(re.fullmatch(r"0|[1-9][0-9]*", revision), "Invalid NuGet revision")
    normalized = ".".join(numbers[:3]) + ("-" + suffix if suffix else "")
    if "+" in value:
        normalized += "+" + value.split("+", 1)[1]
    c.tag("sdk/v" + normalized, "sdk")
    if nuget:
        suffix = suffix.lower()
    identifiers = tuple((0, int(item)) if item.isdigit() else (1, item) for item in suffix.split(".")) if suffix else ()
    return (*map(int, numbers[:3]), int(revision), 0 if suffix else 1, identifiers)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def registry_json(url):
    """Only a real 404 means absent; auth, rate limits and network errors fail."""
    c.require(url.startswith(("https://api.nuget.org/", "https://registry.npmjs.org/")), "Unexpected package registry")
    try:
        with build_opener(NoRedirect).open(Request(url, headers={"Accept": "application/json"}), timeout=30) as response:
            c.require(response.status == 200, "Registry lookup did not return HTTP 200")
            raw = response.read(8 * 1024 * 1024 + 1)
            c.require(len(raw) <= 8 * 1024 * 1024, "Registry response exceeds its bound")
            value = c.decode(raw)
            c.require(isinstance(value, dict), "Registry metadata must be an object")
            return value
    except HTTPError as error:
        code = error.code
        error.close()
        if code == 404:
            return None
        raise ValueError(f"Registry lookup failed (HTTP {code}): {url}") from None


def package_presence(version, inventory=None, get=registry_json):
    return {name: item["present"] for name, item in package_statuses(version, inventory, get).items()}


def package_history(kind, name, version, get=registry_json):
    nuget = kind == "nuget"
    current = published_version_key(version, nuget=nuget)
    if nuget:
        metadata = get(f"https://api.nuget.org/v3-flatcontainer/{name.lower()}/index.json")
        c.require(metadata is None or isinstance(metadata.get("versions"), list), "Invalid NuGet version history: " + name)
    else:
        metadata = get(f"https://registry.npmjs.org/{quote(name, safe='')}")
        c.require(metadata is None or metadata.get("name") == name and isinstance(metadata.get("versions"), dict),
                  "Invalid npm version history: " + name)
    versions = list(metadata["versions"]) if metadata is not None else []
    c.require(metadata is None or versions, "Empty published version history is not a not-found response: " + name)
    c.require(all(isinstance(item, str) for item in versions), "Invalid published version history: " + name)
    keys = {item: published_version_key(item, nuget=nuget) for item in versions}
    highest = max(keys, key=keys.get) if keys else None
    return dict(latest=highest, present=current in keys.values(), newer=highest is None or current > keys[highest])


def package_statuses(version, inventory=None, get=registry_json):
    version_key(version)
    inventory = inventory or c.decode(c.read(ROOT / "eng/test-oracles/core-public-packages.json"))
    c.require(inventory["coordinatedVersion"] == version, "SDK selection must use the authored source version")
    result = {}
    for kind in ("nuget", "npm"):
        for package in inventory[kind]:
            name = package["packageId"] if kind == "nuget" else package["name"]
            c.require(name not in result, "Duplicate public package identity")
            result[name] = dict(kind=kind, name=name, version=version, project=package["project"],
                                **package_history(kind, name, version, get))
    c.require(len(result) == 9, "Expected six public NuGets and three npm packages")
    return result


def selection(version, statuses=None):
    statuses = package_statuses(version) if statuses is None else statuses
    packages = list(statuses.values())
    return dict(version=version, publish=any(item["newer"] for item in packages),
                all_new=all(item["newer"] for item in packages), packages=packages,
                **{kind: [item["name"] for item in packages if item["kind"] == kind and item["newer"]] for kind in ("nuget", "npm")})


def package_present(version, name):
    inventory = c.decode(c.read(ROOT / "eng/test-oracles/core-public-packages.json"))
    selected = {"nuget": [item for item in inventory["nuget"] if item["packageId"] == name],
                "npm": [item for item in inventory["npm"] if item["name"] == name]}
    c.require(sum(map(len, selected.values())) == 1, "Not a public SDK package: " + name)
    history = package_history("nuget" if selected["nuget"] else "npm", name, version)
    c.require(history["present"] or history["newer"],
              f"Refusing to publish {name}@{version}: highest published version is {history['latest']}")
    return history["present"]


class Workflow:
    def api(self, suffix, method="GET", value=None, optional=False):
        arguments = ["gh", "api", "--hostname", "github.com", "--include", "--method", method,
                     "-H", "Accept: application/vnd.github+json", "-H", "X-GitHub-Api-Version: 2022-11-28",
                     f"repos/{c.REPOSITORY}/{suffix}"]
        if value is not None:
            arguments += ["--input", "-"]
        result = subprocess.run(arguments, input=c.canonical(value) if value is not None else None,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
        raw = result.stdout.replace(b"\r\n", b"\n")
        header, separator, body = raw.partition(b"\n\n")
        status = re.match(rb"HTTP/\S+ (\d{3})\b", header)
        c.require(separator and status, "GitHub response has no HTTP status: " + suffix)
        code = int(status[1])
        if optional and code == 404:
            return None
        c.require(result.returncode == 0 and 200 <= code < 300, f"GitHub {method} failed (HTTP {code}): {suffix}")
        return c.decode(body) if body.strip() else None

    def source(self, tag):
        value = self.api("git/ref/tags/" + quote(tag, safe=""), optional=True)
        if value is None:
            return None
        item = value["object"]
        for _ in range(9):
            c.sha(item["sha"], 40)
            if item["type"] == "commit":
                return item["sha"]
            c.require(item["type"] == "tag", "Release tag does not resolve to a commit")
            item = self.api("git/tags/" + item["sha"])["object"]
        raise ValueError("Excessive annotated-tag depth")

    def runs(self, tag, source):
        found = []
        for page in range(1, 101):
            values = self.api(f"actions/workflows/{WORKFLOW}/runs?head_sha={source}&per_page=100&page={page}")["workflow_runs"]
            for run in values:
                if run["head_branch"] == tag and run["head_sha"] == source and run["event"] in ("push", "workflow_dispatch"):
                    c.require(run["path"] == c.SDK_WORKFLOW, "Unexpected SDK workflow path")
                    c.number(run["id"])
                    found.append(run)
            if len(values) < 100:
                return sorted(found, key=lambda run: int(run["id"]))
        raise ValueError("SDK workflow pagination limit exceeded")


def require_retained_candidate(store, release):
    c.require(release is not None, "Published package versions have no retained SDK release; refusing to rebuild them")
    value = store.get(release["id"])
    c.require({item["name"] for item in value["assets"]} == assets.core_names("sdk", store.tag[5:]) | assets.EVIDENCE,
              "Published package versions have no complete retained candidate; resume the original SDK run")


def require_recipes():
    c.require(any((ROOT / "eng/compatibility/recipes").glob("*/*.json")),
              "SDK release inputs are not ready: no reviewed compatibility recipes exist under eng/compatibility/recipes. "
              "Complete the compatibility-input producer before creating a release tag; no tag or package was created.")


def select(version, workflow=None, store=None, statuses=None):
    version_key(version)
    c.require(os.environ.get("GITHUB_REPOSITORY") == c.REPOSITORY and os.environ.get("GITHUB_REF") == "refs/heads/main"
              and os.environ.get("GITHUB_EVENT_NAME") in ("push", "workflow_dispatch"), "SDK selection runs only on main")
    commit = c.sha(os.environ["GITHUB_SHA"], 40)
    c.require(os.environ.get("GITHUB_WORKFLOW_SHA") == commit
              and os.environ.get("GITHUB_WORKFLOW_REF") == f"{c.REPOSITORY}/{c.SDK_WORKFLOW}@refs/heads/main",
              "SDK selector workflow/source differs")
    workflow = workflow or Workflow()
    tag = "sdk/v" + version
    store = store or assets.GitHubRelease(c.REPOSITORY, tag)
    statuses = package_statuses(version) if statuses is None else statuses
    presence = {name: item["present"] for name, item in statuses.items()}
    newer = any(item["newer"] for item in statuses.values())
    obsolete = any(item["latest"] is not None and
        published_version_key(item["latest"], nuget=item["kind"] == "nuget") >
        published_version_key(version, nuget=item["kind"] == "nuget") for item in statuses.values())
    if not newer and (obsolete or not all(presence.values())):
        return dict(action="skip", tag=tag, reason="Authored version is not newer than the highest published versions")
    release = store.resolve()
    if not newer and (release is None or not release["draft"]):
        return dict(action="skip", tag=tag, reason="Authored version is not newer than the highest published versions")
    source = workflow.source(tag)
    if release:
        c.require(source and release["target_commitish"] == source, "SDK release/tag source conflict")
    if any(presence.values()):
        require_retained_candidate(store, release)
    if source is None:
        c.require(release is None and not any(presence.values()), "Existing publication has no source tag")
        require_recipes()
        try:
            workflow.api("git/refs", "POST", dict(ref="refs/tags/" + tag, sha=commit))
        except (ValueError, subprocess.TimeoutExpired):
            # Read after even a failed/lost acknowledgement. Never move a tag.
            if workflow.source(tag) != commit:
                raise
        source = workflow.source(tag)
        c.require(source == commit, "Created SDK tag names another source")
    comparison = workflow.api(f"compare/{source}...main")
    c.require(comparison["status"] in ("ahead", "identical"), "SDK source is not part of main")
    runs = workflow.runs(tag, source)
    active = [run for run in runs if run["status"] in ACTIVE]
    if active:
        return dict(action="active", tag=tag, run_id=active[0]["id"])
    if runs:
        # All attempts retain one run ID. Re-running only failed jobs preserves
        # successful build/stage outputs and cannot rebuild a published candidate.
        original = runs[0]
        c.require(original["status"] == "completed" and original["conclusion"] in ("failure", "cancelled", "timed_out", "action_required", "startup_failure"),
                  "SDK run succeeded but publication is incomplete; inspect the original run instead of rebuilding")
        workflow.api(f"actions/runs/{c.number(original['id'])}/rerun-failed-jobs", "POST")
        return dict(action="resume", tag=tag, run_id=original["id"])
    c.require(release is None and not any(presence.values()), "Original SDK workflow is unavailable; retained publication cannot be rebuilt")
    workflow.api(f"actions/workflows/{WORKFLOW}/dispatches", "POST", dict(ref=tag))
    return dict(action="dispatch", tag=tag, source=source)


def prepare(version, store=None, statuses=None):
    store = store or assets.GitHubRelease(c.REPOSITORY, "sdk/v" + version)
    statuses = package_statuses(version) if statuses is None else statuses
    if not any(item["newer"] for item in statuses.values()):
        return False
    presence = {name: item["present"] for name, item in statuses.items()}
    release = store.resolve()
    if release:
        c.require(release["target_commitish"] == os.environ["GITHUB_SHA"], "SDK release/tag source conflict")
    if any(presence.values()):
        require_retained_candidate(store, release)
    if release:
        provenance = next((item for item in release["assets"] if item["name"] == "release-provenance.json"), None)
        if provenance:
            value = c.gh("releases/assets/" + c.number(provenance["id"]), binary=True)
            c.require(c.digest(value) == provenance["digest"][7:], "Retained SDK provenance digest differs")
            invocation = c.decode(value)["invocation"]
            c.require(invocation["runId"] == os.environ["GITHUB_RUN_ID"], "Resume the original SDK run; a new run must not rebuild its candidate")
    require_recipes()
    return True


def output_selection(value):
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for key in ("publish", "all_new", "nuget", "npm"):
                output.write(key + "=" + json.dumps(value[key], separators=(",", ":")) + "\n")
    print(json.dumps(value))


def pack(version):
    """Only newly authored versions get release archives; normal source tests are separate."""
    import sdk_release_documentation as documentation
    value = selection(version)
    if not value["publish"]:
        return value
    commit = c.sha(os.environ["GITHUB_SHA"], 40)
    root = ROOT / "artifacts"
    selected = [item for item in value["packages"] if item["newer"]]
    is_tag = os.environ.get("GITHUB_REF", "").startswith("refs/tags/sdk/")
    context = documentation.pack_context(ROOT, commit) if is_tag else nullcontext()
    with context:
        for item in selected:
            if item["kind"] == "nuget":
                (root / "sdk").mkdir(parents=True, exist_ok=True)
                subprocess.run(["dotnet", "pack", item["project"], "-c", "Release", "--no-restore", "--no-build",
                    "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false", "-o", str(root / "sdk")], cwd=ROOT, check=True)
        if value["nuget"]:
            subprocess.run([sys.executable, "-B", str(ROOT / "scripts/release/verify-nuget-package-set.py"),
                version, str(root / "sdk"), commit, *value["nuget"]], cwd=ROOT, check=True)
        if value["npm"]:
            (root / "npm").mkdir(parents=True, exist_ok=True)
            command = ["npm", "pack", "--prefix", "languages/typescript/node", "--json", "--pack-destination", str(root / "npm")]
            for name in value["npm"]:
                command += ["--workspace", name]
            report = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, check=True)
            (root / "npm-pack.json").write_bytes(report.stdout)
            source = c.decode(c.read(ROOT / "languages/typescript/node/packages/package-tool/package.json"))
            subprocess.run(["node", str(ROOT / "scripts/release/verify-node-package-artifacts.mjs"), version,
                source["dependencies"]["@sunder/sdk"], source["dependencies"]["vite"], str(root / "npm"),
                str(root / "npm-pack.json"), *value["npm"]], cwd=ROOT, check=True)
    if is_tag:
        documentation.verify(ROOT, [root / kind for kind in ("sdk", "npm") if value[kind]], commit,
                             package_names=value["nuget"] + value["npm"])
    return value


def channels(version, get=registry_json):
    """Check the entire channel before the first dist-tag mutation."""
    channel = "next" if "-" in version else "latest"
    for name in ("@sunder/sdk", "@sunder/package-tool", "create-sunder-package"):
        metadata = get("https://registry.npmjs.org/" + quote(name, safe=""))
        c.require(metadata and metadata.get("name") == name, "Missing npm package during channel promotion")
        current = metadata.get("dist-tags", {}).get(channel)
        c.require(current is None or version_key(current) <= version_key(version), f"Refusing to move {name}:{channel} backwards from {current} to {version}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("check", "pack", "select", "prepare", "channels", "package"))
    parser.add_argument("version")
    parser.add_argument("name", nargs="?")
    args = parser.parse_args()
    version_key(args.version)
    if args.action in ("check", "pack"):
        output_selection(selection(args.version) if args.action == "check" else pack(args.version))
    elif args.action == "select":
        print(json.dumps(select(args.version)))
    elif args.action == "prepare":
        result = str(prepare(args.version)).lower()
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.write("publish=" + result + "\n")
        print("SDK publication needed: " + result)
    elif args.action == "channels":
        channels(args.version)
    else:
        c.require(args.name, "Package lookup needs an exact public package name")
        return 0 if package_present(args.version, args.name) else 4
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error))
