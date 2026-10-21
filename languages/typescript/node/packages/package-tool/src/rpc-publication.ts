import { isSemanticVersion } from "@sunder/sdk";
import { resolve } from "node:path";
import { inspectContractCandidate, type InspectedOwnedContract } from "./rpc-contract-candidate";
import { parseContractWireJson, registryOrigin, type RegistryOriginOptions } from "./rpc-contract-lock";
import { readResponse } from "./rpc-contract-restore";
import { assertBaselineFreeEvidence, captureBaseline, commitBaseline, exists, verifyFreshOwnedSource, withBaselineLease } from "./rpc-baseline-store";
import { hasPublicEvidence, writeBaseline, type OwnedBaseline, type OwnedSnapshot } from "./rpc-baseline";
import { ordered, outerKeys, type Outer, type OuterObject } from "./rpc-baseline-json";
import { readProjectDescriptorFile } from "./descriptor-files";
import { parseModelProjectionDocument } from "./rpc-model-projection";
import { validateReviewedArtifactProjections } from "./rpc-owned-projection-review";

export interface PublicationImportOptions extends RegistryOriginOptions {
  readonly packagePath: string;
  readonly registryOrigin: string;
  readonly fetchImplementation?: typeof fetch;
  readonly signal?: AbortSignal;
}
type PublishedContract = InspectedOwnedContract;
interface Publication { readonly registryOrigin: string; readonly packageId: string; readonly packageVersion: string; readonly contentIdentity: string; readonly publishedAtUtc: string; readonly ownedContracts: readonly PublishedContract[] }
function invalid(message: string): never { throw new Error("rpc-authoring.publication-evidence-mismatch: " + message); }
function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid("Publication metadata must be an object.");
  return value as Record<string, unknown>;
}
function text(value: unknown): string { if (typeof value !== "string") return invalid("Publication text is missing."); return value; }

/** Explicit online confirmation. Baseline and retained archive are fenced at commit. */
export async function importContractPublication(project: string, options: PublicationImportOptions): Promise<boolean> {
  const origin = registryOrigin(options.registryOrigin, options.allowDevelopmentRegistryOrigin);
  if (!options.packagePath?.trim()) invalid("A retained package archive is required.");
  const input = await captureBaseline(project); const baseline = input.baseline;
  const verifyAbsence = async (): Promise<void> => {
    if (baseline) return;
    await verifyFreshOwnedSource(project, input);
    // Check every retained role/export independently; one private receipt cannot
    // conceal a public export or a deleted-baseline marker from another role.
    for (const name of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json", "package-export.v1.json"]) {
      const path = resolve(project, ".sunder/generated/rpc", name);
      if (await exists(path)) assertBaselineFreeEvidence(await readProjectDescriptorFile(project, path, "Publication generation evidence", 67_108_864), path, undefined, name === "package-export.v1.json" ? "export" : "receipt");
    }
  };
  await verifyAbsence();
  const candidate = await inspectContractCandidate(options.packagePath, options.signal, true);
  const expected = candidate.inventory; const id = text(expected.packageId); const version = text(expected.packageVersion);
  if (baseline && id !== baseline.packageId) invalid("The retained archive belongs to another baseline owner.");
  const request = options.fetchImplementation ?? fetch;
  const read = async (route: string): Promise<Record<string, unknown>> => record(parseContractWireJson(await readResponse(new URL(route, origin),
    "application/json", 1_048_576, undefined, request, options.signal, undefined, undefined, options.allowDevelopmentRegistryOrigin), 1_048_576));
  const route = `api/v1/packages/${encodeURIComponent(id)}/versions/${encodeURIComponent(version)}`;
  const details = await read(route); const artifact = record(details.canonicalArtifact); const evidence = record(details.publication); const identity = record(evidence.package);
  if (details.packageId !== id || details.version !== version || artifact.sha256 !== candidate.archiveSha256 || artifact.size !== candidate.archiveBytes
    || identity.packageId !== id || identity.version !== version || identity.packageContentIdentity !== expected.packageContentIdentity
    || evidence.publicInventorySha256 !== candidate.publicInventorySha256 || !/^[0-9a-f]{32}$/u.test(text(evidence.ownerPrincipalId))
    || evidence.initialAvailability !== "Listed" && evidence.initialAvailability !== "Yanked") invalid("Registry did not confirm the exact retained artifact and immutable publication identity.");
  const inventory = await read(route + "/public-api");
  if (inventory.publicInventorySha256 !== candidate.publicInventorySha256 || JSON.stringify(inventory.inventory) !== JSON.stringify(expected))
    invalid("Registry public inventory differs from the complete validated artifact.");
  if (!Array.isArray(details.contractBundles) || details.contractBundles.length > 128) invalid("Registry bundle evidence is missing or oversized.");
  const publicContracts = new Map((expected.contracts as unknown[]).map(item => { const value = record(item); return [value.contractId + "\0" + value.contractVersion, value]; }));
  const ownedContracts: PublishedContract[] = []; const identities = new Set<string>();
  for (const item of details.contractBundles) {
    const bundle = record(item); const contractId = text(bundle.contractId); const contractVersion = text(bundle.version); const key = contractId + "\0" + contractVersion;
    if (identities.has(key)) invalid("Registry bundle identities are duplicated."); identities.add(key);
    const exposure = text(bundle.exposure);
    if (exposure === "public") {
      const contract = publicContracts.get(key);
      if (!contract || contract.descriptorSha256 !== bundle.descriptorSha256 || contract.abiSha256 !== bundle.abiSha256) invalid("Registry bundle/public inventory identities differ.");
      if (contract.artifactSource !== "owned") continue;
      if (contract.authorityKind !== "package" || contract.authorityId !== id) invalid("Published source is not the package's owned authority.");
    } else if (exposure !== "package") invalid("Registry bundle exposure is invalid.");
    ownedContracts.push({ contractId, version: contractVersion, descriptorSha256: text(bundle.descriptorSha256), abiSha256: text(bundle.abiSha256), exposure });
  }
  if ([...publicContracts.keys()].some(key => !identities.has(key))) invalid("Registry omitted a public bundle.");
  const tuples = (values: readonly PublishedContract[]): string => JSON.stringify(values.map(value => [value.contractId, value.version, value.descriptorSha256, value.abiSha256, value.exposure])
    .sort((a, b) => JSON.stringify(a) < JSON.stringify(b) ? -1 : JSON.stringify(a) > JSON.stringify(b) ? 1 : 0));
  if (!candidate.ownedContracts || tuples(ownedContracts) !== tuples(candidate.ownedContracts)) invalid("Registry owned bundle evidence differs from the canonical inspected archive.");
  if (baseline) validateReviewedArtifactProjections(baseline, ownedContracts, parseModelProjectionDocument(candidate.modelProjectionBytes).contracts);
  const proposal = planPublication(baseline, { registryOrigin: origin, packageId: id, packageVersion: version,
    contentIdentity: text(expected.packageContentIdentity), publishedAtUtc: text(details.publishedAtUtc), ownedContracts });
  const verify = async (): Promise<void> => { options.signal?.throwIfAborted(); await candidate.revalidate(); await verifyAbsence(); };
  if (proposal === undefined) return withBaselineLease(input, async () => { await verify(); await input.revalidate(); return false; });
  return commitBaseline(input, proposal, verify);
}

export function planPublication(baseline: OwnedBaseline, publication: Publication): Buffer;
export function planPublication(baseline: undefined, publication: Publication): undefined;
export function planPublication(baseline: OwnedBaseline | undefined, publication: Publication): Buffer | undefined;
export function planPublication(baseline: OwnedBaseline | undefined, publication: Publication): Buffer | undefined {
  if (baseline && publication.packageId !== baseline.packageId) invalid("Publication names a different owner.");
  if (!isSemanticVersion(publication.packageVersion)) invalid("Publication requires the full owning package SemVer.");
  const key = (id: string, version: string): string => id + "\0" + version;
  const bundles = new Map(publication.ownedContracts.map(value => [key(value.contractId, value.version), value]));
  if (bundles.size !== publication.ownedContracts.length) invalid("Owned publication identities are duplicated.");
  const entries = [...baseline?.contracts.values() ?? []];
  const governed = new Set(entries.filter(hasPublicEvidence).map(entry => entry.contractId));
  const snapshots = entries.flatMap(value => [...value.history, ...(value.current ? [value.current] : [])]);
  const matches = (snapshot: OwnedSnapshot, bundle: PublishedContract): boolean => snapshot.exposure === bundle.exposure
    && snapshot.json.descriptorSha256 === bundle.descriptorSha256 && snapshot.json.abiSha256 === bundle.abiSha256;
  for (const bundle of bundles.values()) {
    if (bundle.exposure === "package" && !governed.has(bundle.contractId)) {
      if (bundle.version !== publication.packageVersion) invalid("Current private bundles must use the exact full owning package version.");
      continue; // The canonical inspector and Registry bind these bytes, not retired evolution snapshots.
    }
    const snapshot = snapshots.find(value => value.descriptor.contractId === bundle.contractId && value.descriptor.version === bundle.version);
    if (!snapshot || snapshot.state === "draft" || !matches(snapshot, bundle)) invalid("Published owned contracts must match retained immutable baseline snapshots.");
    if (snapshot.state !== "published" && record(snapshot.json.packageRelease).packageVersion !== publication.packageVersion)
      invalid("The artifact differs from a contract's frozen package release.");
  }
  const utc = publication.publishedAtUtc.replace(/\+00:00$/u, "Z");
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.[0-9]{1,9})?Z$/u.test(utc)) invalid("Registry publication time must be UTC.");
  const date = Date.parse(utc.slice(0, 19) + "Z");
  if (utc.startsWith("0000-") || !Number.isFinite(date) || new Date(date).toISOString().slice(0, 19) !== utc.slice(0, 19)) invalid("Registry publication time has an invalid calendar value.");
  const timestamp = utc.replace(/\.([0-9]*?)0+Z$/u, (_, fraction: string) => (fraction ? "." + fraction : "") + "Z");
  if (!baseline) return undefined;
  const confirm = (snapshot: OwnedSnapshot): OwnedSnapshot => {
    if (!governed.has(snapshot.descriptor.contractId) || snapshot.state === "draft" || record(snapshot.json.packageRelease).packageVersion !== publication.packageVersion) return snapshot;
    const bundle = bundles.get(key(snapshot.descriptor.contractId, snapshot.descriptor.version));
    if (!bundle || !matches(snapshot, bundle)) invalid("A frozen snapshot is absent or differs from the published archive.");
    const evidence: OuterObject = ordered([["registryOrigin", publication.registryOrigin], ["packageId", publication.packageId], ["packageVersion", publication.packageVersion],
      ["packageContentIdentity", publication.contentIdentity], ["publishedAtUtc", timestamp], ["etag", `"${bundle.descriptorSha256}"`]]);
    if (snapshot.state === "published") {
      if (JSON.stringify(snapshot.json.publication) !== JSON.stringify(evidence)) invalid("Previously retained publication evidence conflicts.");
      return snapshot;
    }
    return { ...snapshot, state: "published", json: ordered([...outerKeys(snapshot.json).map((field): [string, Outer] => [field, field === "state" ? "published" : snapshot.json[field]!]), ["publication", evidence]]) };
  };
  return writeBaseline(baseline.packageId, new Map([...baseline.contracts].map(([name, value]) => [name,
    { ...value, ...(value.current ? { current: confirm(value.current) } : {}), history: value.history.map(confirm) }])), baseline.retiredPrivateContracts);
}
