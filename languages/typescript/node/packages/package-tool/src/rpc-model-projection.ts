import { canonicalizeDescriptor, canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, isPackageId, isSemanticVersion, type JsonValue, type RpcContractDescriptor, SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR } from "@sunder/sdk";
import { createHash } from "node:crypto";
import { fields, object, ordered, outerBytes, outerKeys, parseOuter, type Outer, type OuterObject } from "./rpc-baseline-json";

export interface RpcServiceReferenceProjection { readonly contractId: string; readonly serviceId: string }
export interface RpcModelProjection {
  readonly models: Readonly<Record<string, string>>;
  readonly serviceReferences: Readonly<Record<string, RpcServiceReferenceProjection>>;
  readonly optionalServiceReferenceMembers?: Readonly<Record<string, readonly string[]>>;
}
export interface RpcModelProjectionContract {
  readonly contractId: string; readonly contractVersion: string; readonly descriptorSha256: string; readonly abiSha256: string;
  readonly modelProjection: RpcModelProjection;
  readonly methodPolicies?: RpcMethodPolicies;
}
export type RpcMethodPolicies = Readonly<Record<string, Readonly<Record<string, {readonly timeoutSeconds:number}>>>>;
export function methodPoliciesOuter(value: RpcMethodPolicies | undefined): OuterObject {
  return value === undefined ? {} : {methodPolicies: ordered(Object.entries(value).sort(([a],[b])=>ordinal(a,b)).map(([service,methods])=>[service,
    ordered(Object.entries(methods).sort(([a],[b])=>ordinal(a,b)).map(([method,policy])=>[method,{timeoutSeconds:policy.timeoutSeconds}]))]))};
}
export function methodPoliciesBytes(value: RpcMethodPolicies | undefined): Buffer { return outerBytes(methodPoliciesOuter(value)); }
export function readMethodPolicies(value: Outer | undefined, descriptor?: RpcContractDescriptor): RpcMethodPolicies | undefined {
  if(value===undefined)return undefined;
  return projectionBoundary(()=>{
    let total=0;
    const keys=(map:Outer|undefined,maximum:number)=>{const names=outerKeys(object(map));if(!names.length||names.length>maximum||names.some((name,index)=>name.length>128||!/^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$/u.test(name)||index>0&&names[index-1]!>=name))invalid("Method policy maps require bounded, nonempty ordinal member IDs.");return names;};
    const services=object(value);
    return Object.freeze(ordered(keys(services,64).map(service=>{const methods=object(services[service]);const names=keys(methods,512-total);total+=names.length;
      return [service,Object.freeze(ordered(names.map(method=>{const policy=fields(methods[method],["timeoutSeconds"]),timeout=policy.timeoutSeconds;
        if(typeof timeout!=="number"||!Number.isInteger(timeout)||timeout<1||timeout>2100)invalid("Method timeoutSeconds must be 1..2100; omit defaults.");
        if(descriptor&&!Object.hasOwn(descriptor.services[service]?.methods??{},method))invalid("Method policy is absent from the exact descriptor.");
        return [method,Object.freeze({timeoutSeconds:timeout})];})))];}))) as RpcMethodPolicies;
  });
}
export interface RpcModelProjectionDocument {
  readonly schemaVersion: 1; readonly packageId: string; readonly packageVersion: string;
  readonly contracts: readonly RpcModelProjectionContract[];
}
export interface ProjectionEvidence extends RpcModelProjectionContract {
  readonly descriptor: RpcContractDescriptor; readonly authorityKind: string; readonly authorityId: string;
}
export const MODEL_PROJECTION_FILE = "rpc-model-projection.v1.json";
export const MODEL_PROJECTION_PATH = "contracts/" + MODEL_PROJECTION_FILE;
export const MODEL_PROJECTION_ARCHIVE_PATH = "payload/shared/" + MODEL_PROJECTION_PATH;
export const MAX_MODEL_PROJECTION_BYTES = 4_194_304;
function invalid(message: string): never { throw new Error("rpc-authoring.model-projection-invalid: " + message); }
const ordinal = (a: string, b: string): number => a < b ? -1 : a > b ? 1 : 0;
const digest = (value: unknown): value is string => typeof value === "string" && /^[a-f0-9]{64}$/u.test(value);
const id = (value: unknown): value is string => typeof value === "string" && isPackageId(value);
const version = (value: unknown): value is string => typeof value === "string" && isSemanticVersion(value);
// Backend keywords must not turn a portable public model into an escaped/renamed type.
const reserved = new Set([
  "abstract", "any", "as", "await", "base", "bigint", "bool", "boolean", "break", "byte", "case", "catch",
  "char", "checked", "class", "const", "constructor", "continue", "debugger", "decimal", "declare", "default",
  "delegate", "delete", "do", "double", "else", "enum", "event", "explicit", "export", "extends", "extern",
  "false", "finally", "fixed", "float", "for", "foreach", "function", "global", "goto", "if", "implements",
  "implicit", "import", "in", "instanceof", "int", "interface", "internal", "is", "let", "lock", "long",
  "module", "namespace", "never", "new", "null", "number", "object", "operator", "out", "override", "package",
  "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
  "stackalloc", "static", "string", "struct", "super", "switch", "symbol", "this", "throw", "true", "try",
  "type", "typeof", "uint", "ulong", "unchecked", "undefined", "unknown", "unsafe", "ushort", "using", "var",
  "virtual", "void", "volatile", "while", "with", "yield",
]);
export function portableModelName(value: unknown): value is string {
  return typeof value === "string" && /^[A-Za-z_][A-Za-z0-9_]{0,127}$/u.test(value) && !reserved.has(value);
}
export function emptyModelProjection(): RpcModelProjection {
  return Object.freeze({ models: Object.freeze(ordered([])) as Readonly<Record<string, string>>,
    serviceReferences: Object.freeze(ordered([])) as unknown as Readonly<Record<string, RpcServiceReferenceProjection>> });
}
export function modelProjectionOuter(projection: RpcModelProjection): OuterObject {
  return { models: ordered(Object.entries(projection.models).sort(([a], [b]) => ordinal(a, b))),
    serviceReferences: ordered(Object.entries(projection.serviceReferences).sort(([a], [b]) => ordinal(a, b)).map(([key, target]) => [key, { contractId: target.contractId, serviceId: target.serviceId }])),
    ...(Object.keys(projection.optionalServiceReferenceMembers??{}).length?{optionalServiceReferenceMembers:ordered(Object.entries(projection.optionalServiceReferenceMembers!).sort(([a],[b])=>ordinal(a,b)).map(([key,members])=>[key,[...members].sort(ordinal)]))}:{}) };
}
export function modelProjectionBytes(projection: RpcModelProjection): Buffer { return outerBytes(modelProjectionOuter(projection)); }
export function readModelProjection(value: Outer | undefined, descriptor?: RpcContractDescriptor): RpcModelProjection {
  return projectionBoundary(() => readProjection(value, descriptor));
}
function readProjection(value: Outer | undefined, descriptor?: RpcContractDescriptor): RpcModelProjection {
  const root = fields(value, ["models", "serviceReferences"], ["optionalServiceReferenceMembers"]);
  const names = new Map<string, string>(); const definitions = new Set<string>();
  const entries = (field: string): readonly string[] => {
    const map = object(root[field]); const keys = outerKeys(map);
    if (keys.length > 256 || keys.some((key, index) => !/^[A-Za-z0-9._-]{1,128}$/u.test(key) || index > 0 && keys[index - 1]! >= key)) invalid("Definition maps must be bounded ordinal sets of bare definition keys.");
    for (const key of keys) {
      if (definitions.has(key.toLowerCase()) || descriptor && !Object.hasOwn(descriptor.$defs, key)) invalid("Unknown or overlapping definition key: " + key);
      definitions.add(key.toLowerCase());
    }
    return keys;
  };
  const models = ordered(entries("models").map(key => {
    const name = object(root.models)[key];
    if (!portableModelName(name)) invalid("Invalid public model name: " + String(name));
    const previous = names.get(name.toLowerCase());
    if (previous !== undefined && previous !== name) invalid("Public model names collide under ASCII case folding: " + name);
    names.set(name.toLowerCase(), name); return [key, name];
  }));
  const references = ordered(entries("serviceReferences").map(key => {
    const target = fields(object(root.serviceReferences)[key], ["contractId", "serviceId"]);
    if (!id(target.contractId) || typeof target.serviceId !== "string" || !/^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$/u.test(target.serviceId) || target.serviceId.length > 128) invalid("Invalid service target.");
    return [key, Object.freeze({ contractId: target.contractId, serviceId: target.serviceId })];
  }));
  if (definitions.size > 256 || descriptor && definitions.size > Object.keys(descriptor.$defs).length) invalid("Projection exceeds its definition count.");
  let optional:OuterObject|undefined;
  if(root.optionalServiceReferenceMembers!==undefined){
    const map=object(root.optionalServiceReferenceMembers),keys=outerKeys(map);let total=0;
    if(!keys.length||keys.length>256||new Set(keys.map(key=>key.toLowerCase())).size!==keys.length||keys.some((key,index)=>!/^[A-Za-z0-9._-]{1,128}$/u.test(key)||index>0&&keys[index-1]!>=key))invalid("Optional reference owners must be a nonempty bounded ordinal map.");
    optional=ordered(keys.map(key=>{const members=map[key];
      if(!Array.isArray(members)||!members.length||members.length>256||members.some((name,index)=>typeof name!=="string"||!name.length||name.length>256||[...name].length>128||/[\ud800-\udfff]/u.test(name)||index>0&&String(members[index-1])>=name)||new Set((members as string[]).map(name=>name.replace(/[A-Z]/gu,char=>char.toLowerCase()))).size!==members.length||(total+=members.length)>4096)invalid("Optional reference members must be bounded unique ordinal member names.");
      if(descriptor){
        const owner=descriptor.$defs[key] as Record<string,unknown>|undefined;
        if(!owner||owner.type!=="object"||!owner.properties||typeof owner.properties!=="object"||Array.isArray(owner.properties))invalid("Optional reference owner must be a concrete record definition.");
        for(const member of members as string[]){
          if(!Array.isArray(owner.required)||!owner.required.includes(member)||!Object.hasOwn(owner.properties,member))invalid("Optional reference member must remain wire-required.");
          if(!nullableServiceReferenceTarget((owner.properties as Record<string,unknown>)[member],descriptor,references as unknown as RpcModelProjection["serviceReferences"]))invalid("Optional reference member must be a direct nullable projected service reference.");
        }
      }
      return [key,Object.freeze([...(members as string[])]) as unknown as Outer];
    }));
  }
  if (descriptor) {
    const reachable = new Set<string>();
    const visit = (value: unknown): void => {
      if (Array.isArray(value)) { for (const child of value) visit(child); return; }
      if (!value || typeof value !== "object") return;
      const record = value as Record<string, unknown>;
      if (typeof record.$ref === "string") {
        const key = record.$ref.slice("#/$defs/".length);
        if (!reachable.has(key)) { reachable.add(key); visit(descriptor.$defs[key]); }
      } else for (const child of Object.values(record)) visit(child);
    };
    visit(descriptor.services); visit(descriptor.errors);
    if ([...outerKeys(models), ...outerKeys(references),...optional?outerKeys(optional):[]].some(key => !reachable.has(key))) invalid("Projection exposes an unreachable definition.");
    for (const key of outerKeys(references)) validateServiceReferenceSchema(descriptor.$defs[key]);
  }
  return Object.freeze({ models: Object.freeze(models), serviceReferences: Object.freeze(references),...(optional?{optionalServiceReferenceMembers:Object.freeze(optional)}:{}) }) as unknown as RpcModelProjection;
}
export function nullableServiceReferenceTarget(value:unknown,descriptor:RpcContractDescriptor,references:RpcModelProjection["serviceReferences"]):RpcServiceReferenceProjection|undefined{
  const resolve=(input:unknown,seen=new Set<string>()):{schema:Record<string,unknown>;key?:string}|undefined=>{
    if(!input||typeof input!=="object"||Array.isArray(input))return;
    const schema=input as Record<string,unknown>;
    if(typeof schema.$ref!=="string")return {schema};
    const key=schema.$ref.slice("#/$defs/".length);if(seen.has(key))return;seen.add(key);
    if(Object.hasOwn(references,key))return {schema:descriptor.$defs[key] as Record<string,unknown>,key};
    return resolve(descriptor.$defs[key],seen);
  };
  const root=resolve(value)?.schema;if(!root||!Array.isArray(root.oneOf)||root.oneOf.length!==2)return;
  const branches=root.oneOf.map(item=>resolve(item));
  if(branches.filter(item=>item?.schema.type==="null").length!==1)return;
  const target=branches.find(item=>item?.key)?.key;return target?references[target]:undefined;
}
export function parseModelProjectionDocument(bytes: Buffer): RpcModelProjectionDocument {
  return projectionBoundary(() => parseProjectionDocument(bytes));
}
function parseProjectionDocument(bytes: Buffer): RpcModelProjectionDocument {
  const root = fields(parseOuter(bytes, { bytes: MAX_MODEL_PROJECTION_BYTES, values: 524_288 }), ["schemaVersion", "packageId", "packageVersion", "contracts"]);
  if (root.schemaVersion !== 1 || !id(root.packageId) || !version(root.packageVersion) || !Array.isArray(root.contracts) || root.contracts.length > 128) invalid("Invalid sidecar identity, version or contract count.");
  let previous = "";
  const contracts = root.contracts.map(item => {
    const entry = fields(item, ["contractId", "contractVersion", "descriptorSha256", "abiSha256", "modelProjection"], ["methodPolicies"]);
    if (!id(entry.contractId) || entry.contractId <= previous || !version(entry.contractVersion) || !digest(entry.descriptorSha256) || !digest(entry.abiSha256)) invalid("Sidecar contracts must have unique sorted IDs and exact version/hash evidence.");
    previous = entry.contractId;
    return Object.freeze({ contractId: entry.contractId, contractVersion: entry.contractVersion, descriptorSha256: entry.descriptorSha256,
      abiSha256: entry.abiSha256, modelProjection: readModelProjection(entry.modelProjection),...(entry.methodPolicies===undefined?{}:{methodPolicies:readMethodPolicies(entry.methodPolicies)}) });
  });
  return Object.freeze({ schemaVersion: 1, packageId: root.packageId, packageVersion: root.packageVersion, contracts: Object.freeze(contracts) });
}
export function projectionDocumentBytes(packageId: string, packageVersion: string, contracts: readonly RpcModelProjectionContract[]): Buffer {
  const bytes = outerBytes({ schemaVersion: 1, packageId, packageVersion, contracts: [...contracts].sort((a, b) => ordinal(a.contractId, b.contractId)).map(entry => ({
    contractId: entry.contractId, contractVersion: entry.contractVersion, descriptorSha256: entry.descriptorSha256, abiSha256: entry.abiSha256, modelProjection: modelProjectionOuter(entry.modelProjection),
    ...methodPoliciesOuter(entry.methodPolicies),
  })) });
  parseModelProjectionDocument(bytes); return bytes;
}
export function validateProjectionDocument(document: RpcModelProjectionDocument, packageId: string, packageVersion: string, expected: readonly Omit<ProjectionEvidence, "modelProjection">[]): readonly ProjectionEvidence[] {
  if (document.packageId !== packageId || document.packageVersion !== packageVersion || document.contracts.length !== expected.length) invalid("Sidecar package identity or exact public bundle coverage differs from the artifact.");
  const result = document.contracts.map(entry => {
    const evidence = expected.find(value => value.contractId === entry.contractId);
    if (!evidence || evidence.contractVersion !== entry.contractVersion || evidence.descriptorSha256 !== entry.descriptorSha256 || evidence.abiSha256 !== entry.abiSha256) invalid("Sidecar descriptor/ABI evidence differs from the public bundle.");
    if(Object.hasOwn(evidence,"methodPolicies")&&!methodPoliciesBytes(evidence.methodPolicies).equals(methodPoliciesBytes(entry.methodPolicies)))invalid("Sidecar method policies differ from the selected source evidence.");
    if (!["package", "platform"].includes(evidence.authorityKind)) invalid("Projection requires validated bundle authority.");
    if (evidence.authorityKind === "platform" && (Object.keys(entry.modelProjection.models).length || Object.keys(entry.modelProjection.serviceReferences).length||Object.keys(entry.modelProjection.optionalServiceReferenceMembers??{}).length)) invalid("Platform-only bundles require explicit empty projections.");
    return { ...evidence, modelProjection: readModelProjection(modelProjectionOuter(entry.modelProjection), evidence.descriptor),methodPolicies:readMethodPolicies(methodPoliciesOuter(entry.methodPolicies).methodPolicies,evidence.descriptor) };
  });
  validateModelProjectionSet(result); return result;
}
/** Compare complete value closures using authority/model identities, not per-contract spelling. */
export function validateModelProjectionSet(contracts: readonly ProjectionEvidence[]): void {
  if (contracts.length > 128 || new Set(contracts.map(entry => entry.contractId)).size !== contracts.length) invalid("Projection graph requires at most 128 unique contracts.");
  const contexts = new Map<string, { contract: ProjectionEvidence; definitions: Record<string, unknown> }>(); let totalBytes = 0;
  for (const contract of contracts) {
    const bytes = canonicalRpcAbiProjectionBytes(contract.descriptor); totalBytes += bytes.length;
    if (totalBytes > 67_108_864) invalid("Projection graph exceeds aggregate materializability.");
    if (contract.contractId !== contract.descriptor.contractId || contract.contractVersion !== contract.descriptor.version || !id(contract.authorityId)
      || contract.descriptorSha256 !== createHash("sha256").update(canonicalRpcDescriptorBytes(contract.descriptor)).digest("hex")
      || contract.abiSha256 !== createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR).update(bytes).digest("hex")) invalid("Projection graph requires exact descriptor and authority association.");
    contexts.set(contract.contractId, { contract, definitions: JSON.parse(new TextDecoder().decode(bytes)).$defs as Record<string, unknown> });
    readModelProjection(modelProjectionOuter(contract.modelProjection), contract.descriptor);
    readMethodPolicies(methodPoliciesOuter(contract.methodPolicies).methodPolicies,contract.descriptor);
    for (const [key, target] of Object.entries(contract.modelProjection.serviceReferences)) {
      const targets = contracts.filter(item => item.contractId === target.contractId && Object.hasOwn(item.descriptor.services, target.serviceId));
      if (targets.length !== 1) invalid("Service target must resolve exactly once in the validated contract set: " + target.contractId);
      validateServiceReferenceSchema(contract.descriptor.$defs[key]);
    }
  }
  type Context = (typeof contexts extends Map<string, infer T> ? T : never);
  const authority = (context: Context): string => context.contract.authorityKind + ":" + context.contract.authorityId;
  const identities = new Map<string, { context: Context; key: string }>(); const portableNames = new Map<string, string>(); const compared = new Set<string>(); let steps = 0;
  const reference = (value: unknown): string | undefined => value && typeof value === "object" && "$ref" in value && typeof value.$ref === "string" ? value.$ref.slice("#/$defs/".length) : undefined;
  const identity = (context: Context, key: string | undefined): string | undefined => {
    if (key === undefined) return undefined;
    const model = context.contract.modelProjection.models[key]; const service = context.contract.modelProjection.serviceReferences[key];
    return model !== undefined ? `model\0${authority(context)}\0${model}` : service ? `service\0${authority(contexts.get(service.contractId)!)}\0${service.contractId}\0${service.serviceId}` : undefined;
  };
  const definition = (left: Context, a: string, right: Context, b: string, depth: number): boolean => {
    if(JSON.stringify(left.contract.modelProjection.optionalServiceReferenceMembers?.[a]??[])!==JSON.stringify(right.contract.modelProjection.optionalServiceReferenceMembers?.[b]??[]))return false;
    const pair = JSON.stringify([left.contract.contractId, a, right.contract.contractId, b]);
    if (compared.has(pair)) return true; compared.add(pair);
    return same(left, left.definitions[a], right, right.definitions[b], depth + 1);
  };
  const same = (left: Context, a: unknown, right: Context, b: unknown, depth: number): boolean => {
    if (++steps > 4_194_304 || depth > 256) invalid("Model closure exceeds its analysis bound.");
    const ar = reference(a); const br = reference(b);
    if (ar !== undefined || br !== undefined) {
      const ai = identity(left, ar); const bi = identity(right, br);
      if (ai !== undefined || bi !== undefined) return ai === bi;
      if (ar !== undefined && br !== undefined) return definition(left, ar, right, br, depth);
      return same(left, ar === undefined ? a : left.definitions[ar], right, br === undefined ? b : right.definitions[br], depth + 1);
    }
    if (Array.isArray(a) || Array.isArray(b)) return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((child, index) => same(left, child, right, b[index], depth + 1));
    if (a && typeof a === "object" || b && typeof b === "object") {
      if (!a || typeof a !== "object" || !b || typeof b !== "object") return false;
      const ak = Object.keys(a).sort(ordinal); const bk = Object.keys(b).sort(ordinal);
      return ak.length === bk.length && ak.every((key, index) => key === bk[index] && same(left, (a as Record<string, unknown>)[key], right, (b as Record<string, unknown>)[key], depth + 1));
    }
    return a === b;
  };
  for (const context of contexts.values()) for (const [key, name] of Object.entries(context.contract.modelProjection.models).sort(([a],[b])=>ordinal(a,b))) {
    const portable = authority(context) + "/" + name.toLowerCase(); const spelling = portableNames.get(portable);
    if (spelling !== undefined && spelling !== name) invalid("Cross-contract public model names collide under ASCII case folding.");
    portableNames.set(portable, name);
    const identity = authority(context) + "/" + name; const previous = identities.get(identity);
    if (previous && !definition(previous.context, previous.key, context, key, 0)) invalid(`Incompatible model '${identity}' in ${previous.context.contract.contractId} and ${context.contract.contractId}.`);
    if (!previous) identities.set(identity, { context, key });
  }
}
function projectionBoundary<T>(read: () => T): T {
  try { return read(); }
  catch (error) {
    if (error instanceof Error && error.message.startsWith("rpc-authoring.model-projection-invalid:")) throw error;
    throw new Error("rpc-authoring.model-projection-invalid: Malformed or noncanonical model projection metadata.", {cause:error});
  }
}
const locatorFields = ["packageId", "packageVersion", "providerId", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "activationId", "activationEpoch", "sessionGeneration", "endpoint"];
export function serviceReferenceSchema(): Record<string, unknown> {
  const text = (minLength: number, maxLength: number) => ({ type: "string", minLength, maxLength });
  const integer = () => ({ type: "integer", minimum: 0, maximum: Number.MAX_SAFE_INTEGER });
  return { type: "object", properties: { packageId: text(1, 128), packageVersion: text(5, 256), providerId: text(1, 128), contractId: text(1, 128), contractVersion: text(5, 256),
    descriptorSha256: text(64, 64), abiSha256: text(64, 64), activationId: { type: "string", format: "uuid" }, activationEpoch: integer(), sessionGeneration: integer(), endpoint: text(1, 256) },
    required: [...locatorFields], additionalProperties: false };
}
export function validateServiceReferenceSchema(value: unknown): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) invalid("Service references require a closed eleven-field locator schema.");
  const normalize = (value: unknown): unknown => {
    if (!value || typeof value !== "object" || Array.isArray(value)) return value;
    return Object.fromEntries(Object.entries(value).filter(([key]) => key !== "title" && key !== "description").map(([key, child]) => [key,
      key === "required" && Array.isArray(child) ? [...child].sort() : key === "properties" && child && typeof child === "object"
        ? Object.fromEntries(Object.entries(child).map(([name, schema]) => [name, normalize(schema)])) : child]));
  };
  if (canonicalizeDescriptor(normalize(value) as JsonValue) !== canonicalizeDescriptor(normalize(serviceReferenceSchema()) as JsonValue))
    invalid("Service references require the exact closed eleven-field locator schema and bounds.");
}
