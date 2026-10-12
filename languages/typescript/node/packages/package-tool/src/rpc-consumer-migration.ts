import { randomUUID } from "node:crypto";
import { open, rename, unlink } from "node:fs/promises";
import { resolve } from "node:path";
import { fields, object, ordered, outerBytes, outerKeys, parseOuter } from "./rpc-baseline-json";
import { contractError, consumerRoles, parseContractLock, type ConsumerRole, type RegistryOriginOptions, type ContractLock } from "./rpc-contract-lock";
import { contractCacheFiles, DEFAULT_CONTRACT_CACHE } from "./rpc-contract-cache";
import { loadProjectWithOwnedFrame } from "./model";
import { withOutputLeases } from "./output";
import { exists } from "./rpc-baseline-store";

/** Only this migration reader accepts missing role intent. Final readers stay strict. */
export interface ConsumerRoleChange { readonly packageId: string; readonly before: readonly ConsumerRole[] | null; readonly after: readonly ConsumerRole[] }
export async function migrateConsumerRoles(root: string, options: RegistryOriginOptions & { packageId?: string; consumerRoles?: readonly ConsumerRole[]; cacheRoot?: string; signal?: AbortSignal }): Promise<{ changed: boolean; lock: ContractLock; roleChanges: readonly ConsumerRoleChange[] }> {
  const path = resolve(root, "sunder-contracts.lock.json"); const original = await contractCacheFiles.read(path, 33_554_432);
  const input = fields(parseOuter(original), ["lockVersion", "packageArtifacts", "packageImports", "providerImports", "platformImports"]);
  const selections = object(input.packageImports); const selected = options.packageId;
  if (selected !== undefined && !Object.hasOwn(selections, selected)) contractError("package-api.import-not-found", `No package API '${selected}' exists.`);
  const entries = outerKeys(selections).map(packageId => {
    const old = object(selections[packageId]); const missing = !Object.hasOwn(old, "consumerRoles");
    fields(old, ["localName", "presence", "requiredContracts", ...(missing ? [] : ["consumerRoles"])]);
    if (!missing) consumerRoles(old.consumerRoles, packageId);
    return [packageId, ordered([["localName", old.localName!], ["presence", old.presence!], ["requiredContracts", old.requiredContracts!], ["consumerRoles", selected === packageId && options.consumerRoles ? [...options.consumerRoles] : old.consumerRoles ?? ["runtime"]]])] as const;
  });
  const proposal = outerBytes(ordered(outerKeys(input).map(key => [key, key === "packageImports" ? ordered(entries) : input[key]!])));
  const after = parseContractLock(proposal, options.allowDevelopmentRegistryOrigin);
  const roleChanges = outerKeys(selections).map(packageId => {
    const old = object(selections[packageId]);
    return { packageId, before: Object.hasOwn(old, "consumerRoles") ? consumerRoles(old.consumerRoles, packageId) : null, after: after.packageImports.get(packageId)!.consumerRoles };
  });
  const retained = new Map<string, Buffer>();
  for (const name of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json"]) {
    const file = resolve(root, ".sunder/generated/rpc", name); if (!await exists(file)) continue;
    const bytes = await contractCacheFiles.read(file, 67_108_864); retained.set(file, bytes);
    const receipt = JSON.parse(bytes.toString()) as { packageApiImports?: Array<{ packageId: string; useRefs: Array<{ role: string; contractId: string }> }>; uses?: Array<{ role: string; contractKey: string; actions: string[] }>; contracts?: Array<{ contractKey: string; contractId: string; canonicalDescriptor: string }> };
    for (const group of receipt.packageApiImports ?? []) {
      const old = selections[group.packageId]; if (!old || Array.isArray(old) || typeof old !== "object") continue;
      const prior = Object.hasOwn(old, "consumerRoles") ? old.consumerRoles : ["runtime"];
      if (group.useRefs.some(ref => !Array.isArray(prior) || !prior.includes(ref.role))) contractError("package-api.consumer-roles-migration-ambiguous", `${group.packageId}: retained role grants conflict with original intent; reconcile explicitly before migration.`);
      for (const ref of group.useRefs) {
        const contract = receipt.contracts?.find(item => item.contractId === ref.contractId);
        const use = receipt.uses?.find(item => item.role === ref.role && item.contractKey === contract?.contractKey);
        if (!contract || !use) contractError("package-api.consumer-roles-migration-ambiguous", `${group.packageId}: retained use evidence is incomplete.`);
        const descriptor = JSON.parse(contract.canonicalDescriptor) as { services: Record<string, { methods: Record<string, { kind: string }> }> };
        const methods = Object.values(descriptor.services).flatMap(service => Object.values(service.methods));
        const applicable = ["discover", ...(methods.some(method => method.kind === "unary") ? ["invoke"] : []), ...(methods.some(method => method.kind === "server-stream") ? ["subscribe"] : [])];
        if (JSON.stringify(use.actions) !== JSON.stringify(applicable)) contractError("package-api.consumer-roles-migration-ambiguous", `${group.packageId}: narrowed retained actions cannot be represented losslessly; migration will not broaden them.`);
      }
    }
  }
  const project = await loadProjectWithOwnedFrame(root, { contractLock: after, contractCacheRoot: options.cacheRoot ?? DEFAULT_CONTRACT_CACHE,
    ...(options.allowDevelopmentRegistryOrigin === undefined ? {} : { allowDevelopmentRegistryOrigin: options.allowDevelopmentRegistryOrigin }) });
  const verify = async (): Promise<void> => {
    options.signal?.throwIfAborted(); await project.ownedRpc?.revalidate();
    if (!(await contractCacheFiles.read(path, 33_554_432)).equals(original)) contractError("rpc-authoring.evidence-mismatch", "Consumer-role lock changed during migration.");
    for (const [file, bytes] of retained) if (!(await contractCacheFiles.read(file, 67_108_864)).equals(bytes)) contractError("package-api.consumer-roles-migration-ambiguous", "Retained consumer evidence changed during migration.");
  };
  return withOutputLeases([path], async () => {
    await verify(); if (proposal.equals(original)) return { changed: false, lock: after, roleChanges };
    const stage = resolve(root, `.sunder-consumer-roles-${randomUUID()}`); let created = false;
    try {
      const file = await open(stage, "wx", 0o644); created = true; try { await file.writeFile(proposal); await file.sync(); } finally { await file.close(); }
      await verify(); if (!(await contractCacheFiles.read(stage, 33_554_432)).equals(proposal)) contractError("rpc-authoring.evidence-mismatch", "Consumer migration stage changed.");
      await rename(stage, path); created = false; return { changed: true, lock: after, roleChanges };
    } finally { if (created) try { if ((await contractCacheFiles.read(stage, 33_554_432)).equals(proposal)) await unlink(stage); } catch { /* Preserve unproven stage. */ } }
  });
}
