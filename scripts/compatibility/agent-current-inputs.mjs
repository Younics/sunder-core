// Shared test/orchestration reader. Never discovers expectations from artifacts or history.
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { posix, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";

export const currentInventoryPath = "compatibility/agent-current-inventory.json";
export const currentContractsPath = "compatibility/agent-current-contracts.json";
export const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");
export function requireCurrent(condition, message) { if (!condition) throw new Error(message); }
export function exactSet(actual, expected, label) {
  requireCurrent(Array.isArray(actual) && new Set(actual).size === actual.length
    && isDeepStrictEqual([...actual].sort(), [...expected].sort()), `${label} differs from current expectations`);
}
const safePath = (path) => typeof path === "string" && !path.includes("\\") && !path.includes(":")
  && path.split("/").every((part) => part && part !== "." && part !== "..");

export function validateCurrentInventory(expected) {
  requireCurrent(expected?.schemaVersion === 1 && expected.packageVersion === "2.0.0" && expected.publicVersion === "1.0.0", "Unsupported current Agent inventory/version");
  const { packages, contracts } = expected;
  requireCurrent(Array.isArray(packages) && packages.length === 15 && packages.filter((p) => p.hasApp === true).length === 12
    && packages.every((p) => typeof p.hasApp === "boolean" && typeof p.packageApi === "boolean"), "Expected 15 current Worker packages / 12 App roles");
  for (const field of ["key", "packageId", "projectPath"]) {
    requireCurrent(packages.every((p) => typeof p[field] === "string" && p[field]) && new Set(packages.map((p) => p[field])).size === 15, `Duplicate/invalid current package ${field}`);
  }
  requireCurrent(packages.find((p) => p.key === "agent-builder").packageId === "younics.sunder.package.maker", "Package Maker must be an explicit current member");
  requireCurrent(packages.find((p) => p.key === "agent").packageId === "younics.ai.agent", "Current public owner differs");
  requireCurrent(Array.isArray(contracts) && contracts.length === 31 && new Set(contracts.map((c) => c.contractId)).size === 31
    && contracts.filter((c) => c.exposure === "public").length === 18 && contracts.filter((c) => c.exposure === "package").length === 13, "Expected 18 public / 13 private current contracts");
  requireCurrent(isDeepStrictEqual(expected.platform, { contractId: "sunder.stack.contributor", authorityKind: "platform", authorityId: "sunder.core", version: "2.0.0" }), "Current platform authority differs");
  for (const contract of contracts) {
    const owner = packages.find((p) => p.packageId === contract.packageId);
    requireCurrent(owner && safePath(contract.source) && contract.source.startsWith(posix.dirname(owner.projectPath) + "/")
      && (contract.exposure !== "public" || contract.packageId === "younics.ai.agent"), "Current contract source/authority differs");
    requireCurrent(typeof contract.service === "string" && contract.service && contract.methods?.length > 0
      && typeof contract.runtime === "boolean" && typeof contract.app === "boolean", "Invalid current service/use expectation");
    exactSet(contract.methods, [...new Set(contract.methods)], "current methods");
    exactSet(contract.streams, contract.methods.filter((m) => contract.streams.includes(m)), "current streams");
  }
  requireCurrent(packages.reduce((count, p) => count + Object.keys(p.providers).length, 0) === 62, "Expected 62 current providers");
  for (const p of packages) {
    const parts = p.projectPath.split("/");
    requireCurrent(safePath(p.projectPath) && parts.length === 3 && parts[0] === "src" && parts[2] === parts[1] + ".csproj", "Expected canonical root project topology");
    requireCurrent(Object.values(p.providers).every((id) => id === expected.platform.contractId || contracts.some((c) => c.contractId === id)), "Unknown current provider contract");
    exactSet(p.requiredRuntime, contracts.filter((c) => c.exposure === "public" && p.requiredRuntime.includes(c.contractId)).map((c) => c.contractId), "current required Runtime contracts");
  }
}

export function validateCurrentRecords(records, expected, inventorySha256) {
  validateCurrentInventory(expected);
  requireCurrent(records?.schemaVersion === 1 && records.inventorySha256 === inventorySha256, "Current records do not authenticate the expected inventory bytes");
  exactSet(records.contracts.map((c) => c.contractId), expected.contracts.map((c) => c.contractId), "current contract records");
  for (const record of records.contracts) {
    const contract = expected.contracts.find((c) => c.contractId === record.contractId);
    const descriptor = JSON.parse(record.canonicalDescriptor);
    requireCurrent(record.packageId === contract.packageId && record.exposure === contract.exposure
      && descriptor.contractId === contract.contractId && descriptor.version === (contract.exposure === "public" ? expected.publicVersion : expected.packageVersion)
      && descriptor.descriptorVersion === 1 && descriptor.schemaProfile === "sunder.rpc.values.v1"
      && sha256(Buffer.from(record.canonicalDescriptor, "utf8")) === record.descriptorSha256
      && /^[0-9a-f]{64}$/u.test(record.abiSha256), `Current descriptor tuple differs: ${record.contractId}`);
    exactSet(Object.keys(descriptor.services), [contract.service], "current service");
    const methods = descriptor.services[contract.service].methods;
    exactSet(Object.keys(methods), contract.methods, "current methods");
    requireCurrent(Object.values(methods).every((m) => ["unary", "server-stream"].includes(m.kind)), "Unknown current method kind");
    exactSet(Object.keys(methods).filter((id) => methods[id].kind === "server-stream"), contract.streams, "current streams");
  }
  // ABI digests are reviewed expected records here. SDK parsing recomputes them in
  // the C# consumers; output observations must match these records, not each other.
}

export function validateObservedInventory(actual, expected) {
  validateCurrentInventory(expected);
  requireCurrent(actual.schemaVersion === 1 && actual.releaseVersion === expected.packageVersion, "Current package inventory version differs");
  exactSet(actual.packages.map((p) => p.packageId), expected.packages.map((p) => p.packageId), "current package IDs");
  exactSet(Object.keys(actual.capabilities), expected.packages.map((p) => p.packageId), "current capability owners");
  for (const p of expected.packages) {
    const observed = actual.packages.find((value) => value.packageId === p.packageId);
    requireCurrent(observed.key === p.key && observed.projectPath === p.projectPath && observed.artifactType === "sunderpkg", "Current package/project pair differs");
    exactSet(observed.providerIds, Object.keys(p.providers), "current provider IDs");
  }
}

export async function readCurrentAgentInputs(agentRoot) {
  const [inventoryBytes, contractBytes, packageBytes] = await Promise.all([currentInventoryPath, currentContractsPath, "packages.json"].map((path) =>
    readFile(resolve(agentRoot, path)).catch((error) => { throw new Error(`Missing/unreadable CURRENT input ${path}: ${error.message}`); })));
  const inventory = JSON.parse(inventoryBytes);
  const records = JSON.parse(contractBytes);
  const packageInventory = JSON.parse(packageBytes);
  const inventorySha256 = sha256(inventoryBytes);
  validateCurrentRecords(records, inventory, inventorySha256);
  validateObservedInventory(packageInventory, inventory);
  return { inventory, records, packageInventory, inventorySha256, contractsSha256: sha256(contractBytes), packagesSha256: sha256(packageBytes) };
}
