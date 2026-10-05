import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { emptyBaseline, parseBaseline, sha256, writeBaseline, type OwnedSnapshot } from "../src/rpc-baseline";
import { ordered } from "../src/rpc-baseline-json";
import { planPublication } from "../src/rpc-publication";

const fixturePath = resolve(__dirname, "../../../../../../../specs/rpc/v1/conformance/valid/minimal.rpc.json");
async function fixture(state: OwnedSnapshot["state"] = "release-frozen", exposure: OwnedSnapshot["exposure"] = "public") {
  const descriptor = parseRpcContractDescriptor(await readFile(fixturePath));
  const bytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor)); const abi = Buffer.from(canonicalRpcAbiProjectionBytes(descriptor));
  const snapshot: OwnedSnapshot = { state, exposure, descriptor, json: ordered([
    ["state", state], ["exposure", exposure], ["contractVersion", descriptor.version], ["descriptorBytes", bytes.length], ["canonicalDescriptor", bytes.toString()],
    ["descriptorSha256", sha256(bytes)], ["abiProjectionBytes", abi.length], ["canonicalAbiProjection", abi.toString()], ["abiSha256", await computeRpcAbiSha256(descriptor)],
    ...(state === "draft" ? [] : [["packageRelease", { packageId: "example.owner", packageVersion: "7.6.5" }] as const]),
  ]) };
  const baseline = parseBaseline(writeBaseline("example.owner", new Map([["sample", { contractId: descriptor.contractId, current: snapshot, history: [] }]])));
  const publication = { registryOrigin: "https://registry.example/", packageId: "example.owner", packageVersion: "7.6.5", contentIdentity: "a".repeat(64),
    publishedAtUtc: "2026-09-17T12:34:56.1234000+00:00", ownedContracts: [{ contractId: descriptor.contractId, version: descriptor.version,
      descriptorSha256: sha256(bytes), abiSha256: await computeRpcAbiSha256(descriptor), exposure }] };
  return { baseline, snapshot, publication };
}

for (const exposure of ["public", "package"] as const) test(`publication confirms immutable ${exposure} bytes and preserves no-op evidence`, async () => {
  const { baseline, publication } = await fixture("release-frozen", exposure);
  const bytes = planPublication(baseline, publication); const confirmed = parseBaseline(bytes);
  const current = confirmed.contracts.get("sample")!.current!;
  assert.equal(current.state, "published");
  assert.deepEqual(canonicalRpcDescriptorBytes(current.descriptor), canonicalRpcDescriptorBytes(baseline.contracts.get("sample")!.current!.descriptor));
  assert.equal((current.json.publication as Record<string, unknown>).publishedAtUtc, "2026-09-17T12:34:56.1234Z");
  assert.deepEqual(planPublication(confirmed, publication), bytes);
  assert.throws(() => planPublication(confirmed, { ...publication, contentIdentity: "b".repeat(64) }), /conflicts/u);
});

test("publication recovery confirms retained history without resurrecting a current contract", async () => {
  const { baseline, snapshot, publication } = await fixture();
  const historyOnly = parseBaseline(writeBaseline(baseline.packageId, new Map([["sample", { contractId: snapshot.descriptor.contractId, history: [snapshot] }]])));
  const result = parseBaseline(planPublication(historyOnly, publication));
  assert.equal(result.contracts.get("sample")!.current, undefined); assert.equal(result.contracts.get("sample")!.history[0]!.state, "published");
});

for (const mismatch of ["draft", "owner", "release", "descriptor", "abi", "exposure", "missing", "time", "empty-baseline"]) test(`publication rejects ${mismatch} rather than inventing authority`, async () => {
  const { baseline, publication } = await fixture(mismatch === "draft" ? "draft" : "release-frozen");
  const invalid = { ...publication, ownedContracts: publication.ownedContracts.map(value => ({ ...value })) };
  if (mismatch === "owner") invalid.packageId = "other.owner";
  if (mismatch === "release") invalid.packageVersion = "99.0.0";
  if (mismatch === "descriptor") invalid.ownedContracts[0]!.descriptorSha256 = "f".repeat(64);
  if (mismatch === "abi") invalid.ownedContracts[0]!.abiSha256 = "f".repeat(64);
  if (mismatch === "exposure") invalid.ownedContracts[0]!.exposure = "package";
  if (mismatch === "missing") invalid.ownedContracts = [];
  if (mismatch === "time") invalid.publishedAtUtc = "2026-09-17T12:34:56+01:00";
  assert.throws(() => planPublication(mismatch === "empty-baseline" ? emptyBaseline("example.owner") : baseline, invalid));
});
