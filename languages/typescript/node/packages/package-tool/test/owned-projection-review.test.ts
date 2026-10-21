import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { atVersion, emptyBaseline, parseBaseline, planAcceptance, planFreeze, sha256, validateBaselineSource, validateImmutableProjectionReviews, writeBaseline, type AcceptOptions, type OwnedBaseline } from "../src/rpc-baseline";
import { object, outerBytes, parseOuter } from "../src/rpc-baseline-json";
import { classify } from "../src/rpc-evolution";
import { modelProjectionOuter, readModelProjection, type RpcModelProjection } from "../src/rpc-model-projection";
import { createProjectionReview, projectionChange, validateReviewedArtifactProjections } from "../src/rpc-owned-projection-review";
import type { LoadedContract } from "../src/model";

const corpus = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring");
const bytes = (value: unknown): Buffer => Buffer.from(JSON.stringify(value, null, 2) + "\n");
async function source(version = "2.0.0"): Promise<LoadedContract> {
  const document = parseOuter(readFileSync(resolve(corpus, "conformance/valid/package-project-export-optional-reference.json")));
  const contract = object((document.contracts as import("../src/rpc-baseline-json").Outer[])[0]);
  const descriptor = atVersion(parseRpcContractDescriptor(String(contract.canonicalDescriptor)), version);
  const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
  const service = Object.keys(descriptor.services)[0]!, method = Object.keys(descriptor.services[service]!.methods)[0]!;
  return { contractKey: "reference", descriptor, canonicalBytes, canonical: canonicalBytes.toString(), descriptorSha256: sha256(canonicalBytes), abiSha256: await computeRpcAbiSha256(descriptor),
    configuredPath: "rpc.ts", sourcePath: "rpc.ts", descriptorPath: "contracts/reference.rpc.json", exposure: "public",
    modelProjection: readModelProjection(contract.modelProjection, descriptor), methodPolicies: { [service]: { [method]: { timeoutSeconds: 60 } } } };
}
function frozen(candidate: LoadedContract): OwnedBaseline {
  const draft = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [candidate], { key: "reference", nextVersion: candidate.descriptor.version }));
  return parseBaseline(planFreeze(draft, [candidate], "7.6.5"));
}
for (const change of ["model", "service-reference", "optional-reference", "method-policy"] as const)
  test(`reviewed ${change} drift is wire-identical but needs an explicit exported-source Major`, async () => {
    const before = await source(); const baseline = frozen(before);
    let projection: RpcModelProjection = before.modelProjection;
    if (change === "model") projection = { ...projection, models: { Context: "RenamedContext" } };
    if (change === "service-reference") projection = { ...projection, serviceReferences: { Locator: { contractId: "example.other", serviceId: "example" } } };
    if (change === "optional-reference") projection = { models: projection.models, serviceReferences: projection.serviceReferences };
    const service = Object.keys(before.methodPolicies!)[0]!, method = Object.keys(before.methodPolicies![service]!)[0]!;
    const candidate: LoadedContract = { ...before, modelProjection: readModelProjection(modelProjectionOuter(projection), before.descriptor),
      ...(change === "method-policy" ? { methodPolicies: { [service]: { [method]: { timeoutSeconds: 90 } } } } : {}) };
    assert.equal(classify(before.descriptor, candidate.descriptor).kind, "Unchanged");
    for (const release of [false, true]) assert.throws(() => validateBaselineSource(baseline, [candidate], release), /wire classification Unchanged; exported source classification Major/);
    assert.throws(() => planFreeze(baseline, [candidate], "7.6.6"), /source-projection-drift/);
    assert.throws(() => planAcceptance(baseline, [candidate]), /acknowledgement/);
    assert.throws(() => planAcceptance(baseline, [candidate], { key: "reference", currentVersion: "2.0.0", nextVersion: "2.0.1", acknowledgeBreakingChange: true }), /source-projection-bump-required/);
    const accepted = parseBaseline(planAcceptance(baseline, [candidate], { key: "reference", currentVersion: "2.0.0", nextVersion: "3.0.0", acknowledgeBreakingChange: true }));
    const original = baseline.contracts.get("reference")!.current!, retained = accepted.contracts.get("reference")!.history[0]!;
    assert.deepEqual(outerBytes(retained.json), outerBytes(original.json));
    assert.deepEqual(outerBytes(retained.projectionReview!.json), outerBytes(original.projectionReview!.json));
    const next = { ...await source("3.0.0"), modelProjection: candidate.modelProjection, methodPolicies: candidate.methodPolicies };
    const release = parseBaseline(planFreeze(accepted, [next], "7.6.6"));
    assert.deepEqual(planFreeze(release, [next], "8.0.0"), release.bytes);
  });

test("unreviewed source stays inert and cannot backfill frozen or published projection evidence", async () => {
  const candidate = await source();
  for (const state of ["draft", "release-frozen", "published"]) {
    const json = JSON.parse(frozen(candidate).bytes.toString()); delete json.contracts.reference.projectionReviews;
    const current = json.contracts.reference.current; current.state = state;
    if (state === "draft") delete current.packageRelease;
    if (state === "published") current.publication = { registryOrigin: "https://registry.example/", packageId: "example.owner", packageVersion: "7.6.5", packageContentIdentity: "a".repeat(64), publishedAtUtc: "2026-09-11T12:34:56Z", etag: `"${current.descriptorSha256}"` };
    const unreviewed = parseBaseline(bytes(json));
    assert.deepEqual(writeBaseline(unreviewed.packageId, unreviewed.contracts), unreviewed.bytes);
    for (const release of [false, true]) assert.throws(() => validateBaselineSource(unreviewed, [candidate], release), /source-projection-unreviewed/);
    assert.throws(() => planFreeze(unreviewed, [candidate], "8.0.0"), /source-projection-unreviewed/);
    assert.throws(() => planAcceptance(unreviewed, [candidate]), /source-projection-unreviewed/);
    assert.throws(() => planAcceptance(unreviewed, [candidate], { adoptInitialSourceProjection: true } as unknown as AcceptOptions), /unknown contracts review option/);
    if (state === "draft") continue;
    for (const reviewKind of ["accepted-source", "initial-source"]) {
      json.contracts.reference.projectionReviews = [{ ...createProjectionReview(candidate.descriptor, candidate).json, reviewKind }];
      assert.throws(() => validateImmutableProjectionReviews(unreviewed, parseBaseline(bytes(json))), /source-projection-history-unavailable/);
    }
  }
});

test("missing historical source is unknown, requiring a new Major and never inferred from the candidate", async () => {
  const candidate = await source(); const json = JSON.parse(frozen(candidate).bytes.toString()); delete json.contracts.reference.projectionReviews;
  const legacy = parseBaseline(bytes(json));
  assert.throws(() => planAcceptance(legacy, [candidate], { key: "reference", currentVersion: "2.0.0", nextVersion: "2.1.0", acknowledgeBreakingChange: true }), /historical source projection unavailable/);
  const next = parseBaseline(planAcceptance(legacy, [candidate], { key: "reference", currentVersion: "2.0.0", nextVersion: "3.0.0", acknowledgeBreakingChange: true }));
  assert.equal(next.contracts.get("reference")!.history[0]!.projectionReview, undefined);
  assert.deepEqual(outerBytes(next.contracts.get("reference")!.history[0]!.json), outerBytes(legacy.contracts.get("reference")!.current!.json));
  const insufficient = JSON.parse(next.bytes.toString()), minor = await source("2.1.0");
  const draft = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [minor], { key: "reference", nextVersion: "2.1.0" }));
  insufficient.contracts.reference.current = draft.contracts.get("reference")!.current!.json;
  insufficient.contracts.reference.projectionReviews = [{ ...createProjectionReview(minor.descriptor, minor).json, reviewKind: "initial-source" }];
  const labeled = parseBaseline(bytes(insufficient));
  assert.throws(() => validateBaselineSource(labeled, [minor]), /historical source projection unavailable/);
  assert.throws(() => planFreeze(labeled, [minor], "7.6.6"), /historical source projection unavailable/);
});

test("companion reviews validate exact linkage, canonical bytes and hash; frozen reviews cannot be replaced", async () => {
  const candidate = await source(); const baseline = frozen(candidate);
  for (const field of ["contractVersion", "descriptorSha256", "abiSha256", "canonicalProjection", "projectionSha256"]) {
    const json = JSON.parse(baseline.bytes.toString());
    json.contracts.reference.projectionReviews[0][field] = field === "contractVersion" ? "9.0.0" : field === "canonicalProjection" ? "{}\n" : "f".repeat(64);
    assert.throws(() => parseBaseline(bytes(json)), field);
  }
  const entry = baseline.contracts.get("reference")!;
  const changed = { ...candidate, modelProjection: { ...candidate.modelProjection, models: { Context: "RenamedContext" } } };
  const review = createProjectionReview(candidate.descriptor, changed);
  const tampered = parseBaseline(writeBaseline(baseline.packageId, new Map([["reference", { ...entry, current: { ...entry.current!, projectionReview: review } }]])));
  assert.throws(() => validateImmutableProjectionReviews(baseline, tampered), /immutable-projection-review-mutated/);
  assert.throws(() => writeBaseline(baseline.packageId, new Map([["reference", { ...entry, current: { ...entry.current!, projectionReview: { ...review, modelProjection: candidate.modelProjection } } }]])), /model disagrees/);
  const owned = [{ contractId: candidate.descriptor.contractId, version: candidate.descriptor.version, exposure: "public" }];
  const projection = { contractId: candidate.descriptor.contractId, contractVersion: candidate.descriptor.version, descriptorSha256: candidate.descriptorSha256,
    abiSha256: candidate.abiSha256, modelProjection: candidate.modelProjection, methodPolicies: candidate.methodPolicies };
  validateReviewedArtifactProjections(baseline, owned, [projection]);
  assert.throws(() => validateReviewedArtifactProjections(baseline, owned, [{ ...projection, modelProjection: changed.modelProjection }]), /publication-evidence-mismatch/);
});

const shapeCases = JSON.parse(readFileSync(resolve(corpus, "evolution/source-shape-cases.json"), "utf8"));
for (const entry of shapeCases.cases) test(`shared source shape: ${entry.name}`, async () => {
  const next = structuredClone(shapeCases.descriptor);
  for (const change of entry.changes) {
    const path = change.path as string[];
    const parent = path.slice(0, -1).reduce((node, key) => node[key], next);
    if (change.remove) delete parent[path.at(-1)!];
    else parent[path.at(-1)!] = structuredClone(change.copyFrom
      ? (change.copyFrom as string[]).reduce((node, key) => node[key], shapeCases.descriptor) : change.value);
  }
  const before = await shapeSource(shapeCases.descriptor), candidate = await shapeSource(next);
  const baseline = frozen(before), previous = baseline.contracts.get("reference")!.current!;
  assert.equal(classify(before.descriptor, candidate.descriptor).kind, entry.wire);
  assert.equal(projectionChange(previous, candidate), entry.source);
  if (entry.source === "Major") {
    assert.throws(() => planAcceptance(baseline, [candidate]), /acknowledgement/);
    assert.throws(() => planAcceptance(baseline, [candidate], { key: "reference", currentVersion: "1.0.0", nextVersion: "1.1.0", acknowledgeBreakingChange: true }), /source-projection-bump-required/);
    const accepted = parseBaseline(planAcceptance(baseline, [candidate], { key: "reference", currentVersion: "1.0.0", nextVersion: "2.0.0", acknowledgeBreakingChange: true }));
    const historical = accepted.contracts.get("reference")!.history[0]!;
    assert.deepEqual(outerBytes(historical.json), outerBytes(previous.json));
    assert.deepEqual(outerBytes(historical.projectionReview!.json), outerBytes(previous.projectionReview!.json));
  } else if (entry.name === "exact-reuse") assert.deepEqual(planFreeze(baseline, [before], "99.0.0"), baseline.bytes);
  else {
    const accepted = parseBaseline(planAcceptance(baseline, [candidate]));
    assert.equal(accepted.contracts.get("reference")!.current!.descriptor.version, entry.wire === "Minor" ? "1.1.0" : "1.0.1");
  }
});

test("a distinct new contract does not add obligations to the existing provider interface", async () => {
  const before = await shapeSource(shapeCases.descriptor), baseline = frozen(before);
  const addition = { ...await shapeSource({ ...shapeCases.descriptor, contractId: "example.source.other" }), contractKey: "other" };
  const accepted = parseBaseline(planAcceptance(baseline, [before, addition]));
  assert.deepEqual(outerBytes(accepted.contracts.get("reference")!.current!.json), outerBytes(baseline.contracts.get("reference")!.current!.json));
  assert.equal(accepted.contracts.get("other")!.current!.descriptor.version, "1.0.0");
});

async function shapeSource(value: unknown): Promise<LoadedContract> {
  const descriptor = parseRpcContractDescriptor(JSON.stringify(value)), canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
  return { contractKey: "reference", descriptor, canonicalBytes, canonical: canonicalBytes.toString(), descriptorSha256: sha256(canonicalBytes), abiSha256: await computeRpcAbiSha256(descriptor),
    configuredPath: "rpc.ts", sourcePath: "rpc.ts", descriptorPath: "contracts/reference.rpc.json", exposure: "public",
    modelProjection: readModelProjection(modelProjectionOuter(shapeCases.modelProjection), descriptor) };
}
