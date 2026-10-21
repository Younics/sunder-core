import type { ContractUseConfig, LoadedContract, PackageRole, ProviderConfig } from "./model";
import { isPackageId, isSemanticVersion, parseRpcContractDescriptor, canonicalRpcDescriptorBytes, canonicalRpcAbiProjectionBytes, SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR } from "@sunder/sdk";
import { createHash } from "node:crypto";
import { fields, object, outerBytes, type Outer, type OuterObject } from "./rpc-baseline-json";
import { contractSymbol } from "./rpc-package-imports";

export interface SourceRoleUse {
  readonly role:"runtime"; readonly contractId:string; readonly contractVersion:string;
  readonly descriptorSha256:string; readonly abiSha256:string; readonly serviceId:string; readonly serviceType:string;
  readonly required:boolean; readonly actions:ContractUseConfig["actions"];
}
/** Canonical shared projection identity, independent of Node aliases/source paths. */
export function canonicalServiceType(authorityId:string,contractId:string,serviceId:string):string{
  let stem=authorityId.split(".").map(value=>value[0]!.toUpperCase()+value.slice(1)).join("_");if(/^[0-9]/u.test(stem))stem="_"+stem;
  let service=serviceId.split(/[^A-Za-z0-9]+/u).map(value=>value?value[0]!.toUpperCase()+value.slice(1):"").join("")||"Value";
  if(/^[0-9]/u.test(service))service="_"+service;
  return `Sunder.Generated.Packages.${stem}_Services.${contractSymbol(contractId)}.I${service}${service.endsWith("Service")?"":"Service"}`;
}
export function roleUsesOuter(uses:readonly SourceRoleUse[]):OuterObject{
  return uses.length?{roleUses:[...uses].sort((a,b)=>ordinal(a.contractId,b.contractId)||ordinal(a.serviceId,b.serviceId)).map(use=>({
    role:use.role,contractId:use.contractId,contractVersion:use.contractVersion,descriptorSha256:use.descriptorSha256,abiSha256:use.abiSha256,
    serviceId:use.serviceId,serviceType:use.serviceType,required:use.required,actions:[...use.actions],
  }))}:{};
}
export function roleUsesBytes(uses:readonly SourceRoleUse[]):Buffer{return outerBytes(roleUsesOuter(uses));}
export function readRoleUses(value:Outer|undefined):readonly SourceRoleUse[]{
  if(value===undefined)return [];
  const bad=():never=>{throw new Error("rpc-authoring.use-invalid: Source role uses require exact, unique, ordered Runtime service tuples and consistent policy.");};
  if(!Array.isArray(value)||value.length<1||value.length>128)return bad();
  let previous="";const policies=new Map<string,string>();
  return Object.freeze(value.map(item=>{
    const use=fields(item,["role","contractId","contractVersion","descriptorSha256","abiSha256","serviceId","serviceType","required","actions"]);
    if(use.role!=="runtime"||typeof use.contractId!=="string"||!isPackageId(use.contractId)||typeof use.contractVersion!=="string"||!isSemanticVersion(use.contractVersion)
      ||typeof use.descriptorSha256!=="string"||!/^[a-f0-9]{64}$/u.test(use.descriptorSha256)||typeof use.abiSha256!=="string"||!/^[a-f0-9]{64}$/u.test(use.abiSha256)
      ||typeof use.serviceId!=="string"||!/^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$/u.test(use.serviceId)||use.serviceId.length>128
      ||typeof use.serviceType!=="string"||!use.serviceType.trim()||use.serviceType.length>1024||typeof use.required!=="boolean"||!Array.isArray(use.actions))return bad();
    const authoredActions=use.actions;const actions=["discover","invoke","subscribe"].filter(action=>authoredActions.includes(action));
    if(!actions.length||JSON.stringify(actions)!==JSON.stringify(use.actions))return bad();
    const key=use.contractId+"\0"+use.serviceId;if(previous>=key)return bad();previous=key;
    const policy=JSON.stringify([use.required,actions]);if(policies.has(use.contractId)&&policies.get(use.contractId)!==policy)return bad();policies.set(use.contractId,policy);
    return Object.freeze({...use,actions:Object.freeze(actions)}) as unknown as SourceRoleUse;
  }));
}
export function coalesceRoleUses(uses:readonly SourceRoleUse[]):readonly SourceRoleUse[]{
  const values=new Map<string,SourceRoleUse>(),policies=new Map<string,string>();
  for(const use of uses){const key=use.contractId+"\0"+use.serviceId,previous=values.get(key);
    const policy=JSON.stringify([use.required,use.actions]);
    if(policies.has(use.contractId)&&policies.get(use.contractId)!==policy||previous&&!roleUsesBytes([previous]).equals(roleUsesBytes([use])))throw new Error("rpc-authoring.runtime-use-invalid: Conflicting typed Runtime intent.");
    values.set(key,use);policies.set(use.contractId,policy);if(values.size>128)throw new Error("rpc-authoring.source-limit-exceeded: Typed Runtime uses exceed 128 entries.");}
  return readRoleUses(roleUsesOuter([...values.values()]).roleUses);
}
/** Returns only additional uses; overlap with a whole-package API must agree. */
export function importedRuntimeUses(uses:readonly SourceRoleUse[],contracts:readonly LoadedContract[],existing:readonly ContractUseConfig[]):readonly ContractUseConfig[]{
  const result=new Map<string,ContractUseConfig>();
  for(const use of uses){const contract=contracts.find(contract=>contract.descriptor.contractId===use.contractId);
    if(!contract?.imported||contract.imported.authorityKind!=="package"||contract.exposure!=="public"||contract.descriptor.version!==use.contractVersion
      ||contract.descriptorSha256!==use.descriptorSha256||contract.abiSha256!==use.abiSha256||!Object.hasOwn(contract.descriptor.services,use.serviceId)
      ||canonicalServiceType(contract.imported.authorityId,use.contractId,use.serviceId)!==use.serviceType||use.actions.some(action=>!applicableUseActions(contract).includes(action)))
      throw new Error("rpc-authoring.evidence-mismatch: Source role intent differs from its exact public imported service tuple.");
    const previous=existing.find(value=>value.role==="runtime"&&value.contractId===use.contractId)??result.get(use.contractId);
    if(previous&&(previous.required!==use.required||JSON.stringify(previous.actions)!==JSON.stringify(use.actions)))throw new Error("rpc-authoring.runtime-use-invalid: Conflicting Runtime policy for one contract.");
    if(!previous)result.set(use.contractId,{role:"runtime",contractId:use.contractId,contractVersion:use.contractVersion,required:use.required,actions:use.actions});
  }
  return [...result.values()].sort((a,b)=>ordinal(a.contractId,b.contractId));
}
/** Validate generated source evidence; a retained JSON document never grants use. */
export function validateRoleUseEvidence(root:OuterObject,kind:"receipt"|"export",expected?:readonly SourceRoleUse[]):readonly SourceRoleUse[]{
  if(root.roleUses!==undefined){
    const required=kind==="export"?["schemaVersion","producer","projectIdentity","configuration","targetFramework","inputFingerprint","lockSha256","packageId","packageVersion","contracts"]
      :["schemaVersion","producer","inputFingerprint","lockSha256","packageId","packageVersion","contracts","packageArtifacts","bundles","packageArtifactImports","packageApiImports","providerImports","platformImports","providers","uses","generatedPackageFacades","generatedBindings"];
    const optional=kind==="export"?["roleUses"]:["role","roleUses"],allowed=new Set([...required,...optional]);
    fields(root,required,optional,Object.keys(root).filter(key=>allowed.has(key)));
  }
  const uses=readRoleUses(root.roleUses),bad=():never=>{throw new Error("rpc-authoring.evidence-mismatch: Source role evidence differs from the exact imported tuple or effective Runtime use.");};
  if(expected&&!roleUsesBytes(uses).equals(roleUsesBytes(expected)))return bad();
  if(kind==="export")return uses;
  if(root.role==="app"&&uses.length)return bad();
  const contracts=Array.isArray(root.contracts)?root.contracts.map(object):[];
  const bundles=Array.isArray(root.bundles)?root.bundles.map(object):[];
  const effective=Array.isArray(root.uses)?root.uses.map(object):[];
  for(const use of uses){
    const contract=contracts.find(item=>item.contractKey===use.contractId),bundle=bundles.find(item=>item.contractKey===use.contractId);
    if(!contract||!bundle||bundle.source!=="imported"||bundle.authorityKind!=="package"||typeof bundle.authorityId!=="string"
      ||contract.exposure!=="public"||contract.contractId!==use.contractId||contract.contractVersion!==use.contractVersion||contract.descriptorSha256!==use.descriptorSha256||contract.abiSha256!==use.abiSha256
      ||use.serviceType!==canonicalServiceType(bundle.authorityId,use.contractId,use.serviceId)||typeof contract.canonicalDescriptor!=="string")return bad();
    const descriptor=parseRpcContractDescriptor(contract.canonicalDescriptor),bytes=Buffer.from(canonicalRpcDescriptorBytes(descriptor));
    if(descriptor.contractId!==use.contractId||descriptor.version!==use.contractVersion||bytes.toString()!==contract.canonicalDescriptor||bytes.length!==contract.descriptorBytes
      ||createHash("sha256").update(bytes).digest("hex")!==use.descriptorSha256
      ||createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR).update(canonicalRpcAbiProjectionBytes(descriptor)).digest("hex")!==use.abiSha256
      ||!Object.hasOwn(descriptor.services,use.serviceId)||use.actions.some(action=>!applicableUseActions({descriptor}).includes(action)))return bad();
    const matches=effective.filter(item=>item.role==="runtime"&&item.contractKey===use.contractId);
    if(matches.length!==1||matches[0]!.required!==use.required||JSON.stringify(matches[0]!.actions)!==JSON.stringify(use.actions))return bad();
  }
  const groups=Array.isArray(root.packageApiImports)?root.packageApiImports.map(object):[];
  for(const use of effective){const bundle=bundles.find(item=>item.contractKey===use.contractKey);if(bundle?.source!=="imported")continue;
    const intent=uses.find(item=>item.contractId===use.contractKey&&item.role===use.role);
    const grouped=groups.some(group=>Array.isArray(group.useRefs)&&group.useRefs.some(value=>{const ref=object(value);return ref.role===use.role&&ref.contractId===use.contractKey;}));
    if(!intent&&!grouped)return bad();
  }
  return uses;
}
function ordinal(a:string,b:string):number{return a<b?-1:a>b?1:0;}

export function applicableUseActions(contract: Pick<LoadedContract, "descriptor">): ("discover" | "invoke" | "subscribe")[] {
  const methods = Object.values(contract.descriptor.services).flatMap((service) => Object.values(service.methods));
  const actions: ("discover" | "invoke" | "subscribe")[] = ["discover"];
  if (methods.some((method) => method.kind === "unary")) actions.push("invoke");
  if (methods.some((method) => method.kind === "server-stream")) actions.push("subscribe");
  return actions;
}

/** Only an explicit owning-service policy grants a Runtime consumer use. */
export function ownedRuntimeUses(contracts: readonly LoadedContract[]): readonly ContractUseConfig[] {
  return contracts.flatMap(contract => contract.imported || contract.runtimeUse === undefined ? [] : [{
    role: "runtime" as const, contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
    required: contract.runtimeUse.required, actions: contract.runtimeUse.actions,
  }]);
}

/** Full graph validation precedes this role-specific execution closure. Merely
 * materializing public contracts/Models or discovering providers grants no calls. */
export function validateServiceReferenceRoleUses(
  contracts: readonly Pick<LoadedContract, "descriptor" | "modelProjection">[],
  uses: readonly ContractUseConfig[],
  providers: readonly Pick<ProviderConfig, "role" | "contractId">[],
  roles: ReadonlySet<PackageRole>,
): void {
  const materialized = new Map(contracts.map(contract => [contract.descriptor.contractId, contract]));
  for (const role of roles) {
    const reviewed = new Map(uses.filter(use => use.role === role).map(use => [use.contractId, use]));
    const provided = new Set(providers.filter(provider => provider.role === role).map(provider => provider.contractId));
    for (const { descriptor, modelProjection } of contracts) {
      const use = reviewed.get(descriptor.contractId);
      const methods = Object.values(descriptor.services).flatMap(service => Object.values(service.methods)).filter(method =>
        provided.has(descriptor.contractId)
        || method.kind === "unary" && use?.actions.includes("invoke")
        || method.kind === "server-stream" && use?.actions.includes("subscribe"));
      const pending = methods.flatMap(method => [method.requestSchema.$ref,
        method.kind === "unary" ? method.responseSchema.$ref : method.eventSchema.$ref]);
      for (const code of new Set(methods.flatMap(method => method.errors))) {
        const details = descriptor.errors[code]!.detailsSchema;
        if (details) pending.push(details.$ref);
      }
      const visited = new Set<string>();
      const visit = (value: unknown): void => {
        if (Array.isArray(value)) { for (const item of value) visit(item); }
        else if (value && typeof value === "object") {
          const record = value as Record<string, unknown>;
          if (typeof record.$ref === "string") pending.push(record.$ref);
          else for (const item of Object.values(record)) visit(item);
        }
      };
      // Definitions are visited once; cycles cannot grow the queue indefinitely.
      for (let index = 0; index < pending.length; index++) {
        const reference = pending[index]!;
        const definition = /^#\/\$defs\/([A-Za-z0-9._-]{1,128})$/u.exec(reference)?.[1];
        if (!definition || !Object.hasOwn(descriptor.$defs, definition))
          throw new Error("rpc-authoring.model-projection-invalid: Invalid reference in a validated service value graph.");
        if (visited.has(definition)) continue;
        visited.add(definition);
        const target = modelProjection.serviceReferences[definition];
        if (target) {
          if (!Object.hasOwn(materialized.get(target.contractId)?.descriptor.services ?? {}, target.serviceId))
            throw new Error("rpc-authoring.model-projection-invalid: Reachable service target is not materialized: " + target.contractId);
          if (!reviewed.get(target.contractId)?.actions.includes("discover"))
            throw new Error(`rpc-authoring.service-reference-use-missing: Role '${role}' has no reviewed discover use of '${target.contractId}' referenced by reachable contract '${descriptor.contractId}'; a projected service reference cannot widen grants.`);
        }
        visit(descriptor.$defs[definition]);
      }
    }
  }
}
