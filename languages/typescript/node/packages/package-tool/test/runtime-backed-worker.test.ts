import assert from "node:assert/strict";
import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalizeDescriptor, type JsonObject } from "@sunder/sdk";
import { prepareProject } from "../src/package";
import { parseBaseline, planAcceptance, planFreeze, validateBaselineSource } from "../src/rpc-baseline";
import { sameReviewedProjection } from "../src/rpc-owned-projection-review";

test("current runtime-backed fixture retains its reviewed 2.0.0 tuple and exact wire oracle", async () => {
  const nodeRoot = resolve(__dirname, "../../../..");
  const root = resolve(nodeRoot, "fixtures/runtime-backed-worker");
  const config = JSON.parse(await readFile(resolve(root, "sunder.package.json"), "utf8"));
  assert.equal(config.schemaVersion, 3);
  for (const key of ["contracts", "rpcBindings", "providers", "usesContracts"]) assert.equal(Object.hasOwn(config, key), false);

  const original = JSON.parse(await readFile(resolve(__dirname, "../../test/runtime-backed-worker.before-source.json"), "utf8")) as JsonObject;
  assert.equal(original.contractId, "example.messages");
  const baselinePath = resolve(root, "sunder-contracts.owned.json"), before = await readFile(baselinePath), mtime = (await stat(baselinePath)).mtimeMs;
  const baseline = parseBaseline(before), snapshot = baseline.contracts.get("messages")!.current!;
  const project = await prepareProject(root);
  assert.equal(project.contracts.length, 1);
  assert.equal(project.contracts[0]!.canonical, canonicalizeDescriptor(original));
  assert.equal(project.contracts[0]!.descriptor.version, "2.0.0");
  assert.equal(snapshot.state, "release-frozen"); assert.equal(snapshot.projectionReview!.reviewKind, "initial-source");
  assert.equal(sameReviewedProjection(snapshot.projectionReview, project.contracts[0]!), true);
  validateBaselineSource(baseline, project.ownedRpc!.contracts, true);
  assert.deepEqual(planAcceptance(baseline, project.ownedRpc!.contracts), before);
  assert.deepEqual(planFreeze(baseline, project.ownedRpc!.contracts, project.config.version), before);
  assert.deepEqual(await readFile(baselinePath), before); assert.equal((await stat(baselinePath)).mtimeMs, mtime);
  assert.deepEqual(project.config.usesContracts, []);
  assert.equal(project.config.providers[0]!.providerId, "sunder.node.runtimebacked.provider");
  assert.equal(project.config.providers.length, 1);

  // The separately permissioned .NET protocol probe must speak exactly the
  // source-derived tuple. Its fixture descriptor is not a Node build input.
  const probe = JSON.parse(await readFile(resolve(nodeRoot,
    "../../csharp/dotnet/tests/Sunder.Runtime.Host.InteropWorker/Contracts/example.rpc.json"), "utf8"));
  assert.equal(canonicalizeDescriptor(probe), project.contracts[0]!.canonical);
});
