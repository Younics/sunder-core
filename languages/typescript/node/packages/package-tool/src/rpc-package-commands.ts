import { randomUUID } from "node:crypto";
import { open, rename, unlink } from "node:fs/promises";
import { join } from "node:path";
import { isPackageId } from "@sunder/sdk";
import { cachePath, contractCacheFiles, DEFAULT_CONTRACT_CACHE } from "./rpc-contract-cache";
import { contractError, parseContractLock, type ContractLock, type RegistryOriginOptions } from "./rpc-contract-lock";
import { ordered, outerBytes, outerKeys, parseOuter, type Outer, type OuterObject } from "./rpc-baseline-json";
import { hydrateContractLock } from "./rpc-contract-restore";
import { sameReviewedSelection, selectPackageArtifact } from "./rpc-package-selection";
import { loadProjectWithOwnedFrame, loadProjectConfiguration } from "./model";
import { prepareInitialApiSelection } from "./rpc-authoring";
import { captureBaseline, verifyFreshOwnedSource } from "./rpc-baseline-store";
import { withOutputLeases } from "./output";
import { defaultPlatformKey, selectPlatformContract } from "./rpc-platform-catalogs";

export interface PackageApiRequest extends RegistryOriginOptions {
  readonly operation: "add" | "update" | "remove" | "list" | "require" | "unrequire" | "implement" | "unimplement"
    | "platform-implement" | "platform-update" | "platform-unimplement" | "platform-list";
  readonly packageId?: string; readonly contractId?: string; readonly versionRange?: string; readonly includePrerelease?: boolean;
  readonly registryOrigin?: string; readonly presence?: "required" | "optional"; readonly localName?: string; readonly candidate?: string; readonly platformCatalog?: string;
  readonly cacheRoot?: string; readonly fetchImplementation?: typeof fetch; readonly signal?: AbortSignal;
}
type Draft = { lockVersion: number; packageArtifacts: Record<string, OuterObject>; packageImports: Record<string, { localName: string; presence: string; requiredContracts: string[] }>;
  providerImports: Record<string, { packageId: string; contractId: string }>; platformImports: Record<string, OuterObject> };
export async function packageApiCommand(project: string, request: PackageApiRequest): Promise<{ changed: boolean; lock: ContractLock }> {
  try { return await execute(project, request); }
  catch (error) {
    if (error instanceof Error && /^(?:rpc-authoring|package-api|contract-cache|platform-api)\.[a-z-]+:/u.test(error.message)) throw error;
    throw new Error("package-api.public-inventory-invalid: Package API operation could not validate its bounded inputs; the prior lock was retained.", { cause: error });
  }
}
async function execute(project: string, request: PackageApiRequest): Promise<{ changed: boolean; lock: ContractLock }> {
  validateRequest(request); project = await cachePath(project);
  const path = join(project, "sunder-contracts.lock.json"); const cache = request.cacheRoot ?? DEFAULT_CONTRACT_CACHE;
  const original = await contractCacheFiles.read(path, 33_554_432); const before = parseContractLock(original, request.allowDevelopmentRegistryOrigin);
  if (request.operation === "list" || request.operation === "platform-list") return { changed: false, lock: before };
  const draft = clone(parseOuter(original)) as unknown as Draft; const id = request.packageId!;
  const verify = async (): Promise<void> => {
    if (!(await contractCacheFiles.read(path, 33_554_432)).equals(original)) contractError("rpc-authoring.evidence-mismatch", "Contract lock changed during selection; retry against current inputs.");
  };
  let hydrate = false;
  let providerKey: string | undefined;
  if (request.operation === "platform-implement") {
    const current = [...before.platformImports].find(([, value]) => value.contractId === request.contractId);
    providerKey = request.localName ?? current?.[0] ?? defaultPlatformKey(request.contractId!);
    if (current) {
      if (current[0] !== providerKey || request.registryOrigin !== undefined && request.registryOrigin !== current[1].registryOrigin)
        contractError("platform-api.local-name-collision", "Existing platform intent differs; use the current key and locked origin.");
      if (request.platformCatalog !== undefined && !outerBytes(current[1]).equals(outerBytes(await selectPlatformContract(request.contractId!,
        current[1].registryOrigin as string | undefined, request.fetchImplementation, request.signal, request.allowDevelopmentRegistryOrigin, request.platformCatalog))))
        contractError("platform-api.catalog-invalid", "The local catalog differs from the existing selection; use targeted update.");
      await verify(); return { changed: false, lock: before };
    }
    if (request.registryOrigin === undefined && request.platformCatalog === undefined)
      contractError("platform-api.local-input-required", "Select an explicit local catalog or a canonical Registry origin.");
    if ([...before.providerImports.keys(), ...before.platformImports.keys()].some(key => key.toLowerCase() === providerKey!.toLowerCase()))
      contractError("platform-api.local-name-collision", "The local provider key is already in use.");
    draft.platformImports[providerKey] = await selectPlatformContract(request.contractId!, request.registryOrigin, request.fetchImplementation, request.signal, request.allowDevelopmentRegistryOrigin, request.platformCatalog);
    hydrate = true;
  } else if (request.operation === "platform-update") {
    const selected = [...before.platformImports].filter(([, value]) => request.contractId === undefined || value.contractId === request.contractId);
    if (!selected.length) contractError("platform-api.import-not-found", "The selected platform intent does not exist.");
    for (const [key, value] of selected) {
      if (value.registryOrigin !== undefined && request.registryOrigin !== undefined)
        contractError("package-api.registry-origin-invalid", "Update retains an existing Registry origin and rejects an override.");
      draft.platformImports[key] = await selectPlatformContract(value.contractId as string, value.registryOrigin as string | undefined ?? request.registryOrigin,
        request.fetchImplementation, request.signal, request.allowDevelopmentRegistryOrigin, request.platformCatalog);
    }
    hydrate = true;
  } else if (request.operation === "platform-unimplement") {
    const current = [...before.platformImports].find(([, value]) => value.contractId === request.contractId);
    if (!current) contractError("platform-api.import-not-found", "The selected platform intent does not exist.");
    delete draft.platformImports[current[0]];
  } else if (request.operation === "add" || request.operation === "implement") {
    const existing = before.packageArtifacts.get(id);
    if (existing) sameReviewedSelection(existing, request);
    else {
      if (request.versionRange === undefined || request.registryOrigin === undefined && request.candidate === undefined)
        contractError("package-api.local-input-required", "A new artifact selection requires a reviewed version range and either a local candidate or Registry origin.");
      draft.packageArtifacts[id] = await selectPackageArtifact(id, { ...request, registryOrigin: request.registryOrigin, versionRange: request.versionRange, includePrerelease: request.includePrerelease ?? false });
    }
    if (existing && request.candidate !== undefined) {
      const candidate = await selectPackageArtifact(id, { ...request, registryOrigin: existing.registryOrigin, versionRange: existing.requestedVersionRange, includePrerelease: existing.includePrerelease });
      if (!outerBytes(candidate).equals(outerBytes(draft.packageArtifacts[id]!))) contractError("package-api.public-inventory-invalid", "Candidate differs from the shared selection; use targeted update.");
    }
    if (request.operation === "add") {
      const current = before.packageImports.get(id); const localName = request.localName ?? current?.localName ?? id.split(".").at(-1)!; const presence = request.presence ?? "required";
      if (current) {
        if (current.localName !== localName || current.presence !== presence) contractError("package-api.local-name-collision", "Existing consumer intent differs; use targeted update for presence changes.");
        await verify(); return { changed: false, lock: before };
      }
      draft.packageImports[id] = { localName, presence, requiredContracts: [] };
    } else {
      if (!isPublic((draft.packageArtifacts[id]!.contracts as OuterObject)[request.contractId!]))
        contractError("package-api.provider-contract-not-public", "Provider intent must select a public package-authority contract.");
      const current = [...before.providerImports.values()].find(value => value.packageId === id && value.contractId === request.contractId);
      providerKey = request.localName ?? current?.key ?? request.contractId!.split(".").at(-1)! + "Provider";
      if (current) {
        if (current.key !== providerKey) contractError("package-api.local-name-collision", "Provider tuple already has a different local key.");
        await verify(); return { changed: false, lock: before };
      }
      if ([...before.providerImports.keys(), ...before.platformImports.keys()].some(key => key.toLowerCase() === providerKey!.toLowerCase()))
        contractError("package-api.local-name-collision", "The requested local provider key is already in use.");
      draft.providerImports[providerKey] = { packageId: id, contractId: request.contractId! };
    }
    hydrate = true;
  } else if (request.operation === "update") {
    const ids = request.packageId === undefined ? [...before.packageArtifacts.keys()] : [id]; if (!ids.length && !before.platformImports.size) missing();
    for (const id of ids) {
      const previous = before.packageArtifacts.get(id); if (!previous) missing();
      if (previous.registryOrigin !== undefined && request.registryOrigin !== undefined)
        contractError("package-api.registry-origin-invalid", "Update retains an existing Registry origin and rejects an override.");
      if (request.presence !== undefined && !before.packageImports.has(id)) missing();
      const next = await selectPackageArtifact(id, { ...request, registryOrigin: previous.registryOrigin ?? request.registryOrigin, versionRange: request.versionRange ?? previous.requestedVersionRange, includePrerelease: request.includePrerelease ?? previous.includePrerelease });
      const contracts = next.contracts as OuterObject;
      for (const required of before.packageImports.get(id)?.requiredContracts ?? [])
        if (!isPublic(contracts[required])) contractError("package-api.required-contract-removed", "Run api unrequire before removing a required contract.");
      for (const provider of before.providerImports.values()) if (provider.packageId === id && !isPublic(contracts[provider.contractId]))
        contractError("package-api.provider-contract-removed", "Remove the provider source and intent before removing its contract.");
      draft.packageArtifacts[id] = next;
      if (request.presence !== undefined) draft.packageImports[id]!.presence = request.presence;
    }
    if (request.packageId === undefined) for (const [key, value] of before.platformImports)
      draft.platformImports[key] = await selectPlatformContract(value.contractId as string, value.registryOrigin as string | undefined, request.fetchImplementation, request.signal, request.allowDevelopmentRegistryOrigin, request.platformCatalog);
    hydrate = true;
  } else if (request.operation === "unimplement") {
    const current = [...before.providerImports.values()].find(value => value.packageId === id && value.contractId === request.contractId);
    if (!current) missing();
    providerKey = current.key; delete draft.providerImports[providerKey];
    if (!before.packageImports.has(id) && !Object.values(draft.providerImports).some(value => value.packageId === id)) delete draft.packageArtifacts[id];
  } else {
    const imported = draft.packageImports[id]; if (!Object.hasOwn(draft.packageImports, id) || !imported) missing();
    if (request.operation === "remove") {
      if (imported.requiredContracts.length) contractError("package-api.remove-still-referenced", "Run api unrequire for every required contract before removing the consumer intent.");
      delete draft.packageImports[id];
      if (![...before.providerImports.values()].some(value => value.packageId === id)) delete draft.packageArtifacts[id];
    } else {
      const contract = before.packageArtifacts.get(id)!.contracts.get(request.contractId!);
      if (!contract || contract.authorityKind !== "package") contractError("package-api.provider-contract-not-public", "Required contract must belong to the selected public package-authority API.");
      const required = new Set(imported.requiredContracts);
      if (request.operation === "require") required.add(request.contractId!); else required.delete(request.contractId!);
      imported.requiredContracts = [...required].sort();
    }
  }
  const map = (value: object): OuterObject => ordered(Object.entries(value).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0) as [string, Outer][]);
  const proposal = outerBytes({ lockVersion: 1, packageArtifacts: map(draft.packageArtifacts), packageImports: map(draft.packageImports), providerImports: map(draft.providerImports), platformImports: map(draft.platformImports) });
  const after = parseContractLock(proposal, request.allowDevelopmentRegistryOrigin); const changed = !proposal.equals(original);
  for (const artifact of after.packageArtifacts.values()) for (const contract of artifact.contracts.values())
    if (after.requirements.has(contract.descriptorSha256) && contract.authorityKind === "package" && !after.packageArtifacts.has(contract.authorityId))
      contractError("package-api.authority-release-conflict", "An ultimate package authority still needs an explicitly reviewed selection; retain its consumer or provider intent.");
  if (!changed && !hydrate) { await verify(); return { changed: false, lock: before }; }
  let verifySource: (() => Promise<void>) | undefined;
  if (hydrate || request.operation === "unimplement" || request.operation === "platform-unimplement") {
    const physicalBaseline = await captureBaseline(project);
    await verifyFreshOwnedSource(project, physicalBaseline);
    if (hydrate) await hydrateContractLock(after, { ...request, cacheRoot: cache }, verify);
    if (!physicalBaseline.baseline) {
      const frame = await loadProjectConfiguration(project);
      const revalidate = await prepareInitialApiSelection(frame.project, frame.configBytes, after, cache, physicalBaseline);
      verifySource = async () => { await revalidate(); await verifyFreshOwnedSource(project, physicalBaseline); };
    } else {
    let checked;
    try {
      checked = await loadProjectWithOwnedFrame(project, { contractLock: request.operation === "unimplement" ? before : after, contractCacheRoot: cache, physicalBaseline,
        ...(request.allowDevelopmentRegistryOrigin === undefined ? {} : { allowDevelopmentRegistryOrigin: request.allowDevelopmentRegistryOrigin }),
        ...(providerKey === undefined ? {} : { allowUnboundImportedProviderKeys: new Set([providerKey]) }) });
    } catch (error) {
      if (request.operation === "platform-unimplement") throw new Error("platform-api.remove-still-referenced: Remove provider bindings and resolve source diagnostics before unimplementing; the committed lock is unchanged.", { cause: error });
      throw error;
    }
    if (request.operation === "unimplement" && checked.ownedRpc!.importedProviders.some(binding => binding.key === providerKey))
      contractError("package-api.remove-still-referenced", "Remove local provider bindings before unimplementing this contract.");
    verifySource = async () => { await checked.ownedRpc!.revalidate(); await verifyFreshOwnedSource(project, physicalBaseline); };
    }
  }
  return withOutputLeases([path], async () => {
    request.signal?.throwIfAborted(); await verify(); await verifySource?.();
    if (!changed) return { changed: false, lock: before };
    const stage = join(project, ".sunder-contract-selection-" + randomUUID()); let created = false;
    try {
      const file = await open(stage, "wx", 0o644); created = true;
      try { await file.writeFile(proposal); await file.sync(); } finally { await file.close(); }
      if (!(await contractCacheFiles.read(stage, 33_554_432)).equals(proposal)) contractError("rpc-authoring.evidence-mismatch", "Staged lock changed before selection commit.");
      await verifySource?.(); await verify(); request.signal?.throwIfAborted(); await cachePath(path); await rename(stage, path); created = false;
      return { changed: true, lock: after };
    } finally {
      if (created) try { if ((await contractCacheFiles.read(stage, 33_554_432)).equals(proposal)) await unlink(stage); } catch { /* Preserve an unproven source-tree stage. */ }
    }
  }, 30_000);
}
function isPublic(value: Outer | undefined): boolean { return value !== null && typeof value === "object" && !Array.isArray(value) && value.authorityKind === "package"; }
function clone(value: Outer): Outer {
  if (Array.isArray(value)) return value.map(clone);
  if (typeof value === "object") return ordered(outerKeys(value).map(key => [key, clone(value[key]!)]));
  return value;
}
function missing(): never { return contractError("package-api.import-not-found", "The exact requested import or target does not exist."); }
function validateRequest(value: PackageApiRequest): void {
  if (Object.keys(value).some(key => !["operation", "packageId", "contractId", "versionRange", "includePrerelease", "registryOrigin", "presence", "localName", "candidate", "platformCatalog", "cacheRoot", "fetchImplementation", "signal", "allowDevelopmentRegistryOrigin"].includes(key))) missing();
  if (value.allowDevelopmentRegistryOrigin !== undefined && typeof value.allowDevelopmentRegistryOrigin !== "boolean")
    contractError("package-api.registry-origin-invalid", "Development Registry mode must be an explicit Boolean.");
  if (typeof value.operation === "string" && value.operation.startsWith("platform-")) {
    if (!["platform-implement", "platform-update", "platform-unimplement", "platform-list"].includes(value.operation)
      || [value.packageId, value.versionRange, value.includePrerelease, value.presence, value.candidate].some(input => input !== undefined))
      contractError("platform-api.import-not-found", "Platform commands do not accept package, range, prerelease, presence or candidate overrides.");
    for (const input of [value.contractId, value.registryOrigin, value.localName, value.cacheRoot, value.platformCatalog])
      if (input !== undefined && (typeof input !== "string" || !input.trim())) missing();
    if (value.operation === "platform-update" && value.contractId === undefined && value.registryOrigin !== undefined)
      contractError("package-api.registry-origin-invalid", "Binding a local selection to a Registry requires a specific platform contract.");
    if (value.contractId !== undefined && !isPackageId(value.contractId)
      || ["platform-implement", "platform-unimplement"].includes(value.operation) && value.contractId === undefined
      || value.operation === "platform-list" && value.contractId !== undefined
      || value.operation !== "platform-implement" && value.localName !== undefined
      || !["platform-implement", "platform-update"].includes(value.operation) && (value.registryOrigin !== undefined || value.platformCatalog !== undefined))
      contractError("platform-api.import-not-found", "Platform command target or options do not apply; update uses locked origins.");
    return;
  }
  if (!["add", "update", "remove", "list", "require", "unrequire", "implement", "unimplement"].includes(value.operation)) missing();
  for (const input of [value.packageId, value.contractId, value.versionRange, value.registryOrigin, value.presence, value.localName, value.candidate, value.platformCatalog, value.cacheRoot]) if (input !== undefined && (typeof input !== "string" || !input.trim())) missing();
  if (value.packageId !== undefined && !isPackageId(value.packageId) || value.contractId !== undefined && !isPackageId(value.contractId)) missing();
  if (!["update", "list"].includes(value.operation) && value.packageId === undefined || value.operation === "list" && value.packageId !== undefined) missing();
  if (["require", "unrequire", "implement", "unimplement"].includes(value.operation) !== (value.contractId !== undefined)) missing();
  if (value.presence !== undefined && value.presence !== "required" && value.presence !== "optional" || value.includePrerelease !== undefined && typeof value.includePrerelease !== "boolean") missing();
  if (!["add", "update", "implement"].includes(value.operation) && [value.versionRange, value.includePrerelease, value.registryOrigin, value.candidate, value.platformCatalog].some(value => value !== undefined)) missing();
  if (!["add", "update"].includes(value.operation) && value.presence !== undefined) missing();
  if (value.operation === "update" && value.packageId === undefined && value.registryOrigin !== undefined)
    contractError("package-api.registry-origin-invalid", "Binding a local selection to a Registry requires a specific package.");
  if (value.operation !== "add" && value.operation !== "implement" && value.localName !== undefined) missing();
  if (value.operation === "update" && value.packageId === undefined && [value.versionRange, value.includePrerelease, value.presence, value.candidate].some(value => value !== undefined)) missing();
}
