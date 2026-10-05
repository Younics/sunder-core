import { inspectContractCandidate } from "./rpc-contract-candidate";
import { parseContractWireJson, registryOrigin, type RegistryOriginOptions } from "./rpc-contract-lock";
import { readResponse } from "./rpc-contract-restore";
import { captureBaseline, commitBaseline } from "./rpc-baseline-store";
import { writeBaseline, type OwnedBaseline, type OwnedSnapshot } from "./rpc-baseline";
import { ordered, outerKeys, type Outer, type OuterObject } from "./rpc-baseline-json";

export interface PublicationImportOptions extends RegistryOriginOptions {
  readonly packagePath: string;
  readonly registryOrigin: string;
  readonly fetchImplementation?: typeof fetch;
  readonly signal?: AbortSignal;
}
interface PublishedContract { readonly contractId: string; readonly version: string; readonly descriptorSha256: string; readonly abiSha256: string; readonly exposure: string }
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
  if (!baseline) invalid("Publication import requires an existing frozen owned baseline.");
  const candidate = await inspectContractCandidate(options.packagePath, options.signal);
  const expected = candidate.inventory; const id = text(expected.packageId); const version = text(expected.packageVersion);
  if (id !== baseline.packageId) invalid("The retained archive belongs to another baseline owner.");
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
  const proposal = planPublication(baseline, { registryOrigin: origin, packageId: id, packageVersion: version,
    contentIdentity: text(expected.packageContentIdentity), publishedAtUtc: text(details.publishedAtUtc), ownedContracts });
  return commitBaseline(input, proposal, async () => { options.signal?.throwIfAborted(); await candidate.revalidate(); });
}

export function planPublication(baseline: OwnedBaseline, publication: Publication): Buffer {
  if (publication.packageId !== baseline.packageId) invalid("Publication names a different owner.");
  const key = (id: string, version: string): string => id + "\0" + version;
  const bundles = new Map(publication.ownedContracts.map(value => [key(value.contractId, value.version), value]));
  if (bundles.size !== publication.ownedContracts.length) invalid("Owned publication identities are duplicated.");
  const snapshots = [...baseline.contracts.values()].flatMap(value => [...value.history, ...(value.current ? [value.current] : [])]);
  const matches = (snapshot: OwnedSnapshot, bundle: PublishedContract): boolean => snapshot.exposure === bundle.exposure
    && snapshot.json.descriptorSha256 === bundle.descriptorSha256 && snapshot.json.abiSha256 === bundle.abiSha256;
  for (const bundle of bundles.values()) {
    const snapshot = snapshots.find(value => value.descriptor.contractId === bundle.contractId && value.descriptor.version === bundle.version);
    if (!snapshot || snapshot.state === "draft" || !matches(snapshot, bundle)) invalid("Published owned contracts must match retained immutable baseline snapshots.");
    if (snapshot.state !== "published" && record(snapshot.json.packageRelease).packageVersion !== publication.packageVersion)
      invalid("The artifact differs from a contract's frozen package release.");
  }
  const utc = publication.publishedAtUtc.replace(/\+00:00$/u, "Z");
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.[0-9]{1,9})?Z$/u.test(utc)) invalid("Registry publication time must be UTC.");
  const timestamp = utc.replace(/\.([0-9]*?)0+Z$/u, (_, fraction: string) => (fraction ? "." + fraction : "") + "Z");
  const confirm = (snapshot: OwnedSnapshot): OwnedSnapshot => {
    if (snapshot.state === "draft" || record(snapshot.json.packageRelease).packageVersion !== publication.packageVersion) return snapshot;
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
    { ...value, ...(value.current ? { current: confirm(value.current) } : {}), history: value.history.map(confirm) }])));
}
