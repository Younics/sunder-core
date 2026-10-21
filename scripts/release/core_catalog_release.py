#!/usr/bin/env python3
"""Source-derived canonical catalog and closed numeric-release handoff receipt."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
import release_coordinates as c
import github_release as assets

CATALOG = "sunder-platform-contracts.v1.json"
RECEIPT = "sunder-platform-contracts.receipt.v1.json"
EXPORT = "catalog-export.v1.json"


def catalog(data, baseline, version):
    value = c.decode(data)
    c.closed(value, "schemaVersion authorityKind authorityId releaseVersion contractCount contracts")
    c.require(type(value["schemaVersion"]) is int and type(value["contractCount"]) is int
              and value["schemaVersion"] == 1 and value["authorityKind"] == "platform" and value["authorityId"] == "sunder.core"
              and value["releaseVersion"] == version and value["contractCount"] == len(value["contracts"]) == 1, "Unexpected Core catalog identity/inventory")
    fields = "contractId contractVersion descriptorBytes descriptorSha256 abiSha256 canonicalDescriptor".split()
    ordered = {key: value[key] for key in "schemaVersion authorityKind authorityId releaseVersion contractCount contracts".split()}
    ordered["contracts"] = []
    expected = []
    for owned in baseline["contracts"].values():
        current = owned["current"]
        c.require(current["state"] == "release-frozen" and "publication" not in current
                  and current["catalogRelease"] == dict(authorityId="sunder.core", releaseVersion=version), "Catalog must come from the tagged frozen source baseline")
        expected.append(dict(contractId=owned["contractId"], **{key: current[key] for key in fields if key != "contractId"}))
    c.require(value["contracts"] == sorted(expected, key=lambda item: item["contractId"]), "Catalog differs from the exact source baseline")
    for item in value["contracts"]:
        c.closed(item, " ".join(fields))
        raw = item["canonicalDescriptor"].encode("utf-8")
        c.require(len(raw) == item["descriptorBytes"] and c.digest(raw) == item["descriptorSha256"], "Catalog descriptor bytes differ")
        c.sha(item["abiSha256"])
        ordered["contracts"].append({key: item[key] for key in fields})
    c.require(json.dumps(ordered, ensure_ascii=False, separators=(",", ":")).encode("utf-8") == data, "Catalog is not the existing exporter's canonical byte representation")
    return c.digest(c.canonical(value["contracts"]))


def export_catalog(root, output, tool):
    context = c.current_invocation(c.SDK_WORKFLOW)
    source = context["workflowSha"]
    c.approved_source(source)
    c.local_checkout(source)
    name = c.tag(os.environ["GITHUB_REF_NAME"], "sdk")
    c.require(os.environ["GITHUB_EVENT_NAME"] == "push" and context["workflowRef"] == "refs/tags/" + name, "Catalog export requires the SDK tag source")
    c.remote_tag(name, source)
    registry = c.origin(os.environ["SUNDER_CORE_REGISTRY_ORIGIN"])
    baseline = c.read(root / c.BASELINE)
    c.require(not output.exists(), "Catalog export destination must be new")
    output.mkdir(parents=True)
    for action, extra in (("check", []), ("export", ["--catalog-release", name[5:], "--output", str(output / CATALOG)])):
        subprocess.run(["dotnet", str(tool), "platform-contracts", action, "--core-root", str(root), *extra], check=True)
    data = c.read(output / CATALOG)
    inventory = catalog(data, c.decode(baseline), name[5:])
    c.require(c.read(root / c.BASELINE) == baseline, "Frozen baseline changed during catalog export")
    c.local_checkout(source)
    c.write_new(output / EXPORT, dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/platform-catalog-export/v1",
                artifactSourceCommit=source, sdkTag=name, registryOrigin=registry, baselineSha256=c.digest(baseline),
                inventorySha256=inventory, catalogSha256=c.digest(data), catalogBytes=len(data), invocation=context))


def verify(directory, release_id, name, source, registry, baseline, draft=True):
    data = c.read(directory / CATALOG)
    inventory = catalog(data, c.decode(baseline), name[5:])
    receipt = c.decode(c.read(directory / RECEIPT))
    c.closed(receipt, "schemaVersion kind repository releaseId tag artifactSourceCommit registryOrigin baselineSha256 inventorySha256 invocation catalog")
    c.require(type(receipt["schemaVersion"]) is int and type(receipt["releaseId"]) is int
              and receipt["schemaVersion"] == 1 and receipt["kind"] == "https://sunderapp.io/acceptance/platform-catalog-release/v1"
              and receipt["repository"] == c.REPOSITORY and receipt["releaseId"] == int(c.number(release_id))
              and receipt["tag"] == c.tag(name, "sdk") and receipt["artifactSourceCommit"] == c.sha(source, 40)
              and receipt["registryOrigin"] == c.origin(registry) and receipt["baselineSha256"] == c.digest(baseline)
              and receipt["inventorySha256"] == inventory, "Catalog receipt source/release/origin/inventory differs")
    invocation = receipt["invocation"]
    c.require(invocation == c.invocation(c.SDK_WORKFLOW, "refs/tags/" + name, source, invocation["runId"], invocation["runAttempt"]), "Catalog workflow binding differs")
    c.closed(receipt["catalog"], "assetName assetId sha256 size")
    c.require(type(receipt["catalog"]["assetId"]) is int and type(receipt["catalog"]["size"]) is int, "Catalog asset coordinates must be numeric")
    remote = c.release(release_id, name, source, draft=draft)
    actual = assets.asset(remote, CATALOG)
    c.require(receipt["catalog"] == dict(assetName=CATALOG, assetId=actual["id"], sha256=c.digest(data), size=len(data))
              and actual["digest"] == "sha256:" + c.digest(data) and actual["size"] == len(data), "Catalog numeric asset binding differs")
    return receipt


def retain(directory, release_id):
    exported = c.decode(c.read(directory / EXPORT))
    c.closed(exported, "schemaVersion kind artifactSourceCommit sdkTag registryOrigin baselineSha256 inventorySha256 catalogSha256 catalogBytes invocation")
    context = c.current_invocation(c.SDK_WORKFLOW)
    source, name = context["workflowSha"], c.tag(os.environ["GITHUB_REF_NAME"], "sdk")
    c.approved_source(source)
    c.remote_tag(name, source)
    c.require(exported["schemaVersion"] == 1 and exported["kind"] == "https://sunderapp.io/acceptance/platform-catalog-export/v1"
              and exported["artifactSourceCommit"] == source and exported["sdkTag"] == name
              and exported["invocation"] == context, "Catalog export is from another source/run attempt")
    registry = c.origin(os.environ["SUNDER_CORE_REGISTRY_ORIGIN"])
    c.require(exported["registryOrigin"] == registry, "Registry origin changed after export")
    baseline = c.source_file(source, c.BASELINE)
    data = c.read(directory / CATALOG)
    c.require(exported["baselineSha256"] == c.digest(baseline) and exported["inventorySha256"] == catalog(data, c.decode(baseline), name[5:])
              and exported["catalogSha256"] == c.digest(data) and exported["catalogBytes"] == len(data), "Exported bytes changed before retention")
    remote = c.release(release_id, name, source)
    store = assets.GitHubRelease(c.REPOSITORY, name)
    store.upload(release_id, [directory / CATALOG])
    remote = c.release(release_id, name, source)
    old = [item for item in remote["assets"] if item["name"] == RECEIPT]
    if old:
        c.require(len(old) == 1, "Ambiguous catalog completion receipt")
        c.require(old[0]["size"] <= 1_048_576, "Catalog completion receipt exceeds its bound")
        raw = c.gh("releases/assets/" + c.number(old[0]["id"]), binary=True)
        c.require(c.digest(raw) == old[0]["digest"][7:] and len(raw) == old[0]["size"], "Catalog receipt bytes differ")
        (directory / RECEIPT).write_bytes(raw)
        retained = verify(directory, release_id, name, source, registry, baseline)
        # Preserve original creation evidence on an equal retry of the same workflow run.
        c.require(retained["invocation"]["runId"] == context["runId"]
                  and int(retained["invocation"]["runAttempt"]) <= int(context["runAttempt"]), "Catalog receipt belongs to another release run")
    else:
        item = assets.asset(remote, CATALOG)
        c.write_new(directory / RECEIPT, dict(schemaVersion=1, kind="https://sunderapp.io/acceptance/platform-catalog-release/v1",
                    repository=c.REPOSITORY, releaseId=int(c.number(release_id)), tag=name, artifactSourceCommit=source,
                    registryOrigin=registry, baselineSha256=exported["baselineSha256"], inventorySha256=exported["inventorySha256"],
                    invocation=context, catalog=dict(assetName=CATALOG, assetId=item["id"], sha256=c.digest(data), size=len(data))))
        verify(directory, release_id, name, source, registry, baseline)
    store.upload(release_id, [directory / RECEIPT])
    # A completion receipt is authoritative only at its exact numbered GitHub asset identity.
    receipt_asset = assets.asset(c.release(release_id, name, source), RECEIPT)
    c.require(c.gh("releases/assets/" + c.number(receipt_asset["id"]), binary=True) == c.read(directory / RECEIPT), "Catalog receipt differs after upload")
    c.remote_tag(name, source)
    print(json.dumps(dict(releaseId=int(release_id), catalogAssetId=assets.asset(remote, CATALOG)["id"],
                          receiptAssetId=receipt_asset["id"], receiptSha256=c.digest(c.read(directory / RECEIPT)))))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["export", "retain", "verify"])
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--tool", type=Path)
    parser.add_argument("--release-id")
    parser.add_argument("--tag")
    parser.add_argument("--source")
    args = parser.parse_args()
    if args.action == "export":
        c.require(args.tool is not None, "Exporter DLL is required")
        export_catalog(Path.cwd(), args.directory.absolute(), args.tool.absolute())
    elif args.action == "retain":
        retain(args.directory, args.release_id)
    else:
        receipt = c.decode(c.read(args.directory / RECEIPT))
        expected_draft = os.environ.get("EXPECTED_RELEASE_DRAFT", "true")
        c.require(expected_draft in ("true", "false"), "Invalid expected release state")
        verify(args.directory, args.release_id, args.tag, args.source, receipt["registryOrigin"], c.source_file(args.source, c.BASELINE),
               draft=expected_draft == "true")
    print("Verified canonical source-bound Core catalog handoff")


if __name__ == "__main__":
    main()
