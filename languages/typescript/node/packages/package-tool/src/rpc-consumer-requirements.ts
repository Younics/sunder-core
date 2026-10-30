import { canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, parseRpcContractDescriptor, SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, type RpcContractDescriptor } from "@sunder/sdk";
import { createHash } from "node:crypto";
import { fields, object, outerBytes, type OuterObject } from "./rpc-baseline-json";

export interface RequiredMethod { readonly serviceId: string; readonly methodId: string }
export interface ConsumerRequirements {
  readonly source: RpcContractDescriptor;
  readonly methods: readonly RequiredMethod[];
  readonly outputMembers: Readonly<Record<string, readonly string[]>>;
  readonly exactOutputDefinitions: readonly string[];
}
const ordinal = (a: string, b: string): number => a < b ? -1 : a > b ? 1 : 0;
const bad = (message: string): never => { throw new Error("rpc-authoring.consumer-requirements-invalid: " + message); };
const hash = (value: Uint8Array): string => createHash("sha256").update(value).digest("hex");
const abiHash = (value: RpcContractDescriptor): string => createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR).update(canonicalRpcAbiProjectionBytes(value)).digest("hex");

// Selectors reference the complete authenticated source descriptor. They are not
// a replacement inventory and do not grant permission to discover or call it.
export function consumerRequirements(source: RpcContractDescriptor, methods: readonly RequiredMethod[],
  outputMembers: Readonly<Record<string, readonly string[]>> = {}, exactOutputDefinitions: readonly string[] = []): ConsumerRequirements {
  if (methods.length > 512 || methods.some(method => !method || !Object.hasOwn(source.services[method.serviceId]?.methods ?? {}, method.methodId))
    || new Set(methods.map(method => method.serviceId + "\0" + method.methodId)).size !== methods.length) bad("Select unique existing methods, at most 512 per contract.");
  if (exactOutputDefinitions.length > Object.keys(source.$defs).length || new Set(exactOutputDefinitions).size !== exactOutputDefinitions.length
    || exactOutputDefinitions.some(name => !Object.hasOwn(source.$defs, name))) bad("Exact output schemas must be unique existing definitions.");
  const result: ConsumerRequirements = { source, methods: Object.freeze([...methods].sort((a, b) => ordinal(a.serviceId, b.serviceId) || ordinal(a.methodId, b.methodId))
    .map(method => Object.freeze({ serviceId: method.serviceId, methodId: method.methodId }))), outputMembers: {},
    exactOutputDefinitions: Object.freeze([...exactOutputDefinitions].sort(ordinal)) };
  const outputs = closure(source, roots(result, source, false));
  if (Object.keys(outputMembers).length > 256) bad("Output view count exceeds the definition limit.");
  const views: Record<string, readonly string[]> = Object.create(null) as Record<string, readonly string[]>;
  for (const [name, members] of Object.entries(outputMembers).sort(([a], [b]) => ordinal(a, b))) {
    const schema = source.$defs[name] as Record<string, unknown> | undefined;
    const properties = schema?.properties as Record<string, unknown> | undefined;
    if (!outputs.has(name) || !properties || schema?.additionalProperties !== false || Object.hasOwn(schema, "logicalType")
      || Object.hasOwn(schema, "minProperties") || Object.hasOwn(schema, "maxProperties") || exactOutputDefinitions.includes(name)
      || !Array.isArray(members) || members.length > Object.keys(properties).length || new Set(members).size !== members.length
      || members.some(member => typeof member !== "string" || !Object.hasOwn(properties, member))) bad("Output views select existing members of reachable ordinary unbounded records; protocol/reference schemas stay exact.");
    views[name] = Object.freeze([...members].sort(ordinal));
  }
  const withViews = { ...result, outputMembers: Object.freeze(views) };
  const reachable = closure(source, roots(result, source, false), views);
  if (Object.keys(views).some(name => !reachable.has(name))) bad("Output views must remain reachable through the selected output view.");
  return Object.freeze(withViews);
}

export function consumerRequirementsOuter(value: ConsumerRequirements): OuterObject {
  return { schemaVersion: 1, contractId: value.source.contractId, contractVersion: value.source.version,
    descriptorSha256: hash(canonicalRpcDescriptorBytes(value.source)), abiSha256: abiHash(value.source),
    methods: value.methods.map(method => ({ serviceId: method.serviceId, methodId: method.methodId })),
    outputMembers: Object.fromEntries(Object.entries(value.outputMembers).map(([name, members]) => [name, [...members]])),
    exactOutputDefinitions: [...value.exactOutputDefinitions] };
}
export function readConsumerRequirements(input: OuterObject, source: RpcContractDescriptor): ConsumerRequirements {
  const value = fields(input, ["schemaVersion", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "methods", "outputMembers", "exactOutputDefinitions"]);
  if (value.schemaVersion !== 1 || value.contractId !== source.contractId || value.contractVersion !== source.version
    || value.descriptorSha256 !== hash(canonicalRpcDescriptorBytes(source)) || value.abiSha256 !== abiHash(source)) bad("Provenance differs from the exact imported descriptor.");
  if (!Array.isArray(value.methods) || value.methods.length > 512) bad("Methods must be a bounded array.");
  const methods = (value.methods as unknown[]).map(method => {
    const entry = fields(method as OuterObject, ["serviceId", "methodId"]);
    if (typeof entry.serviceId !== "string" || typeof entry.methodId !== "string") return bad("Method identifiers must be strings.");
    return { serviceId: entry.serviceId, methodId: entry.methodId };
  });
  const views = Object.fromEntries(Object.entries(object(value.outputMembers)).map(([name, members]) => [name, strings(members)]));
  const result = consumerRequirements(source, methods, views, strings(value.exactOutputDefinitions));
  if (!outerBytes(input).equals(outerBytes(consumerRequirementsOuter(result)))) bad("Selectors must use canonical order.");
  return result;
}

export function readConsumerRequirementEvidence(root: OuterObject): readonly ConsumerRequirements[] {
  if (root.consumerRequirements === undefined) return [];
  const values = root.consumerRequirements;
  if (!Array.isArray(values) || values.length < 1 || values.length > 128) bad("Present receipt requirements must be a nonempty bounded array.");
  const contracts = Array.isArray(root.contracts) ? root.contracts.map(object) : [];
  const bundles = (Array.isArray(root.bundles) ? root.bundles.map(object) : []).filter(bundle => bundle.source === "imported" && bundle.authorityKind === "package");
  const ids = bundles.map(bundle => String(bundle.contractKey)).sort(ordinal);
  const requirements = (values as OuterObject[]).map(input => {
    const value = object(input), contract = contracts.find(contract => contract.contractId === value.contractId && contract.contractKey === value.contractId);
    if (!contract || !ids.includes(String(value.contractId)) || contract.exposure !== "public" || typeof contract.canonicalDescriptor !== "string") return bad("Requirements must name an imported public package contract.");
    const source = parseRpcContractDescriptor(contract.canonicalDescriptor);
    const result = readConsumerRequirements(value, source);
    const canonical = Buffer.from(canonicalRpcDescriptorBytes(source));
    if (contract.contractVersion !== source.version || contract.descriptorSha256 !== hash(canonical) || contract.abiSha256 !== abiHash(source)
      || contract.canonicalDescriptor !== canonical.toString() || contract.descriptorBytes !== canonical.length) bad("Receipt provenance differs from its descriptor.");
    const exact = Object.keys(object(object(contract.modelProjection).serviceReferences)).sort(ordinal);
    if (exact.some(name => !result.exactOutputDefinitions.includes(name))) bad("Service-reference output schemas must retain exact protocol requirements.");
    return result;
  });
  if (JSON.stringify(requirements.map(value => value.source.contractId)) !== JSON.stringify(ids)) bad("Requirements must uniquely cover imported package contracts in ordinal order.");
  return Object.freeze(requirements);
}
function strings(value: unknown): string[] {
  if (!Array.isArray(value) || value.length > 256 || value.some(member => typeof member !== "string")) return bad("Selector members must be bounded string arrays.");
  return value as string[];
}

export function requiredDefinitions(value: ConsumerRequirements): readonly string[] {
  return [...new Set([...closure(value.source, roots(value, value.source, true)),
    ...closure(value.source, roots(value, value.source, false), value.outputMembers)])].sort(ordinal);
}

// Both complete descriptors have already passed the shared V1 parser. Project
// only the selected method and schema closure before invoking the bounded proof.
export function consumerProjection(value: ConsumerRequirements, descriptor: RpcContractDescriptor): unknown {
  const result = JSON.parse(Buffer.from(canonicalRpcAbiProjectionBytes(descriptor)).toString()) as {
    services: Record<string, { methods: Record<string, unknown> }>; errors: Record<string, unknown>; $defs: Record<string, unknown> };
  for (const [serviceId, service] of Object.entries(result.services)) {
    for (const methodId of Object.keys(service.methods))
      if (!value.methods.some(method => method.serviceId === serviceId && method.methodId === methodId)) delete service.methods[methodId];
    if (!Object.keys(service.methods).length && !value.methods.some(method => method.serviceId === serviceId)) delete result.services[serviceId];
  }
  const errors = new Set(selectedMethods(value, descriptor).flatMap(method => method.errors));
  for (const code of Object.keys(result.errors)) if (!errors.has(code)) delete result.errors[code];
  const required = closure(descriptor, [...roots(value, descriptor, true), ...roots(value, descriptor, false)]);
  for (const name of Object.keys(result.$defs)) if (!required.has(name)) delete result.$defs[name];
  return result;
}
function selectedMethods(value: ConsumerRequirements, descriptor: RpcContractDescriptor) {
  return value.methods.flatMap(method => Object.hasOwn(descriptor.services[method.serviceId]?.methods ?? {}, method.methodId)
    ? [descriptor.services[method.serviceId]!.methods[method.methodId]!] : []);
}
function roots(value: ConsumerRequirements, descriptor: RpcContractDescriptor, request: boolean): string[] {
  return selectedMethods(value, descriptor).flatMap(method => request ? [method.requestSchema.$ref.slice(8)]
    : [(method.kind === "unary" ? method.responseSchema.$ref : method.eventSchema.$ref).slice(8),
      ...method.errors.flatMap(code => descriptor.errors[code]?.detailsSchema ? [descriptor.errors[code]!.detailsSchema!.$ref.slice(8)] : [])]);
}
function closure(descriptor: RpcContractDescriptor, roots: readonly string[], views?: Readonly<Record<string, readonly string[]>>): Set<string> {
  const seen = new Set<string>(), pending = [...roots];
  const visit = (value: unknown): void => {
    if (Array.isArray(value)) { for (const item of value) visit(item); return; }
    if (value && typeof value === "object") {
      const schema = value as Record<string, unknown>;
      if (typeof schema.$ref === "string") { pending.push(schema.$ref.slice(8)); return; }
      if (schema.properties) for (const child of Object.values(schema.properties as object)) visit(child);
      for (const keyword of ["items", "additionalProperties", "propertyNames", "oneOf"]) if (schema[keyword] && typeof schema[keyword] === "object") visit(schema[keyword]);
    }
  };
  for (let index = 0; index < pending.length; index++) {
    const name = pending[index]!; if (seen.has(name)) continue; seen.add(name);
    const schema = descriptor.$defs[name]! as Record<string, unknown>;
    if (views && Object.hasOwn(views, name)) for (const member of views[name]!) visit((schema.properties as Record<string, unknown>)[member]);
    else visit(schema);
  }
  return seen;
}
