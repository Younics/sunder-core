import {
  canonicalizeDescriptor,
  rpcSchemaAcceptsNull,
  validateRpcContractDescriptor,
} from "./descriptor";
import type {
  JsonSchema,
  JsonValue,
  RpcContractDescriptor,
  RpcContractDescriptorV2,
  RpcSchemaReference,
} from "./types";

const MAXIMUM_SCHEMA_DEPTH = 64;
const REFERENCE_PREFIX = "#/$defs/";
const UTF8 = new TextEncoder();

export const SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR = "sunder.rpc.abi.v2\0" as const;

type JsonObject = { readonly [key: string]: JsonValue };
type DescriptorVersion = RpcContractDescriptor["descriptorVersion"];

export class RpcInstanceError extends TypeError {
  public constructor(message: string) {
    super(message);
    this.name = "RpcInstanceError";
  }
}

export function canonicalRpcDescriptorBytes(descriptor: RpcContractDescriptor): Uint8Array {
  validateRpcContractDescriptor(descriptor);
  return UTF8.encode(canonicalizeDescriptor(descriptor as unknown as JsonValue));
}

export function rpcAbiV2Projection(descriptor: RpcContractDescriptorV2): JsonValue {
  requireV2Descriptor(descriptor);
  const errors = jsonRecord();
  for (const code of Object.keys(descriptor.errors)) errors[code] = {};

  const services = jsonRecord();
  for (const [serviceId, service] of Object.entries(descriptor.services)) {
    const methods = jsonRecord();
    for (const [methodId, method] of Object.entries(service.methods)) {
      const projected = jsonRecord();
      projected.kind = method.kind;
      projected.idempotency = method.idempotency;
      projected.errors = [...method.errors].sort(ordinal);
      projected.requestSchema = cloneJsonValue(method.requestSchema as unknown as JsonValue);
      if (method.responseSchema !== undefined) {
        projected.responseSchema = cloneJsonValue(method.responseSchema as unknown as JsonValue);
      }
      if (method.eventSchema !== undefined) {
        projected.eventSchema = cloneJsonValue(method.eventSchema as unknown as JsonValue);
      }
      methods[methodId] = projected;
    }
    services[serviceId] = { methods };
  }

  const definitions = jsonRecord();
  for (const [name, schema] of Object.entries(descriptor.$defs)) {
    definitions[name] = projectSchema(schema, descriptor.$defs);
  }

  return {
    descriptorVersion: 2,
    contractId: descriptor.contractId,
    errors,
    services,
    $defs: definitions,
  };
}

export function canonicalRpcAbiProjectionBytes(descriptor: RpcContractDescriptorV2): Uint8Array {
  return UTF8.encode(canonicalizeDescriptor(rpcAbiV2Projection(descriptor)));
}

export async function computeRpcDescriptorSha256(descriptor: RpcContractDescriptor): Promise<string> {
  return sha256(canonicalRpcDescriptorBytes(descriptor));
}

export async function computeRpcAbiSha256(descriptor: RpcContractDescriptorV2): Promise<string> {
  const projection = canonicalRpcAbiProjectionBytes(descriptor);
  const domain = UTF8.encode(SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR);
  const input = new Uint8Array(domain.byteLength + projection.byteLength);
  input.set(domain);
  input.set(projection, domain.byteLength);
  return sha256(input);
}

export function validateRpcInstance(
  descriptor: RpcContractDescriptor,
  schemaReference: string | RpcSchemaReference,
  value: unknown,
): asserts value is JsonValue {
  validateRpcContractDescriptor(descriptor);
  const reference = typeof schemaReference === "string"
    ? schemaReference
    : readApiReference(schemaReference);
  const name = referenceName(reference);
  const schema = descriptor.$defs[name];
  if (schema === undefined) throw new RpcInstanceError(`Schema reference '${reference}' does not exist in this descriptor.`);
  let jsonError: string | undefined;
  try {
    jsonError = jsonValueShapeError(value, "$", new WeakSet());
  } catch {
    jsonError = "$ cannot be safely inspected as a JSON value.";
  }
  if (jsonError !== undefined) throw new RpcInstanceError(jsonError);
  const error = instanceError(schema, value, "$", descriptor.$defs, descriptor.descriptorVersion, 1);
  if (error !== undefined) throw new RpcInstanceError(error);
}

function projectSchema(schema: JsonSchema, definitions: Readonly<Record<string, JsonSchema>>): JsonValue {
  if (typeof schema.$ref === "string") return { $ref: schema.$ref };

  const output = jsonRecord();
  for (const [name, value] of Object.entries(schema)) {
    if (name === "title" || name === "description") continue;
    switch (name) {
      case "properties": {
        const properties = jsonRecord();
        for (const [propertyName, propertySchema] of Object.entries(asSchemaMap(value))) {
          properties[propertyName] = projectSchema(propertySchema, definitions);
        }
        output.properties = properties;
        break;
      }
      case "items":
      case "propertyNames":
        output[name] = projectSchema(asSchema(value), definitions);
        break;
      case "additionalProperties":
        output.additionalProperties = value === false ? false : projectSchema(asSchema(value), definitions);
        break;
      case "required":
        output.required = schema.type === "object" && schema.additionalProperties === false
          ? [...asStringArray(value)].sort(ordinal)
          : cloneJsonValue(value);
        break;
      case "enum":
        output.enum = sortPrimitiveEnum(asJsonArray(value));
        break;
      case "oneOf":
        output.oneOf = projectUnion(asSchemaArray(value), definitions);
        break;
      default:
        output[name] = cloneJsonValue(value);
        break;
    }
  }
  return output;
}

function projectUnion(
  variants: readonly JsonSchema[],
  definitions: Readonly<Record<string, JsonSchema>>,
): readonly JsonValue[] {
  const projected = variants.map((variant) => ({
    original: variant,
    projected: projectSchema(variant, definitions),
    resolved: resolveSchema(variant, definitions),
    acceptsNull: rpcSchemaAcceptsNull(variant, definitions),
  }));
  const directlyNullCount = projected.filter((variant) => variant.resolved.type === "null").length;
  const nullAcceptingCount = projected.filter((variant) => variant.acceptsNull).length;
  if (variants.length === 2 && directlyNullCount === 1) {
    if (nullAcceptingCount !== 1) throw new TypeError("The validated nullable union has ambiguous null acceptance.");
    projected.sort((left, right) => Number(right.acceptsNull) - Number(left.acceptsNull));
  } else {
    const discriminator = taggedUnionDiscriminator(projected.map((variant) => variant.resolved));
    projected.sort((left, right) => ordinal(
      discriminatorValue(left.resolved, discriminator),
      discriminatorValue(right.resolved, discriminator),
    ));
  }
  return projected.map((variant) => variant.projected);
}

function sortPrimitiveEnum(values: readonly JsonValue[]): readonly JsonValue[] {
  return values
    .map((value) => ({ value, bytes: UTF8.encode(canonicalizeDescriptor(value)) }))
    .sort((left, right) => compareBytes(left.bytes, right.bytes))
    .map((entry) => cloneJsonValue(entry.value));
}

function taggedUnionDiscriminator(variants: readonly JsonSchema[]): string {
  let candidates: Set<string> | undefined;
  for (const schema of variants) {
    const properties = asSchemaMap(schema.properties);
    const required = new Set(asStringArray(schema.required));
    const current = new Set(Object.keys(properties).filter((name) => {
      const property = properties[name];
      return property !== undefined
        && required.has(name)
        && property.type === "string"
        && typeof property.const === "string";
    }));
    candidates = candidates === undefined
      ? current
      : new Set([...candidates].filter((name) => current.has(name)));
  }
  const valid = [...(candidates ?? [])].filter((name) => {
    const values = variants.map((variant) => discriminatorValue(variant, name));
    return new Set(values).size === variants.length;
  });
  if (valid.length !== 1 || valid[0] === undefined) throw new TypeError("The validated tagged union has no unique discriminator.");
  return valid[0];
}

function discriminatorValue(schema: JsonSchema, discriminator: string): string {
  const property = asSchemaMap(schema.properties)[discriminator];
  if (property === undefined || typeof property.const !== "string") {
    throw new TypeError("The validated tagged union has an invalid discriminator value.");
  }
  return property.const;
}

function resolveSchema(
  input: JsonSchema,
  definitions: Readonly<Record<string, JsonSchema>>,
): JsonSchema {
  let schema = input;
  const seen = new Set<string>();
  while (typeof schema.$ref === "string") {
    const name = referenceName(schema.$ref);
    if (seen.has(name)) throw new TypeError("The validated descriptor contains a recursive schema reference.");
    seen.add(name);
    const target = definitions[name];
    if (target === undefined) throw new TypeError(`The validated descriptor is missing definition '${name}'.`);
    schema = target;
  }
  return schema;
}

function cloneJsonValue(value: JsonValue): JsonValue {
  if (value === null || typeof value !== "object") return value;
  if (Array.isArray(value)) return value.map(cloneJsonValue);
  const output = jsonRecord();
  for (const [name, item] of Object.entries(value)) output[name] = cloneJsonValue(item);
  return output;
}

function jsonRecord(): Record<string, JsonValue> {
  return Object.create(null) as Record<string, JsonValue>;
}

function jsonValueShapeError(value: unknown, path: string, active: WeakSet<object>): string | undefined {
  if (value === null || typeof value === "boolean") return undefined;
  if (typeof value === "string") {
    return hasUnicodeScalars(value) ? undefined : `${path} contains an unpaired UTF-16 surrogate.`;
  }
  if (typeof value === "number") return Number.isFinite(value) ? undefined : `${path} must be a finite JSON number.`;
  if (typeof value !== "object") return `${path} contains a value that is not JSON.`;
  if (active.has(value)) return `${path} contains a cyclic object graph.`;

  active.add(value);
  try {
    return Array.isArray(value)
      ? jsonArrayShapeError(value, path, active)
      : jsonObjectShapeError(value, path, active);
  } finally {
    active.delete(value);
  }
}

function jsonObjectShapeError(value: object, path: string, active: WeakSet<object>): string | undefined {
  const prototype = Object.getPrototypeOf(value) as object | null;
  if (!isPlainObjectPrototype(prototype)) {
    return `${path} must be a plain object or null-prototype record.`;
  }
  for (const name of Reflect.ownKeys(value)) {
    if (typeof name === "symbol") return `${path} contains a symbol-keyed property.`;
    const descriptor = Object.getOwnPropertyDescriptor(value, name);
    if (descriptor === undefined || !("value" in descriptor)) return `${propertyPath(path, name)} must be a data property.`;
    if (!descriptor.enumerable) return `${propertyPath(path, name)} must be an enumerable JSON property.`;
    const error = jsonValueShapeError(descriptor.value, propertyPath(path, name), active);
    if (error !== undefined) return error;
  }
  return undefined;
}

function jsonArrayShapeError(value: readonly unknown[], path: string, active: WeakSet<object>): string | undefined {
  if (!isArrayPrototype(Object.getPrototypeOf(value) as object | null)) return `${path} must be a normal JSON array.`;
  let elementCount = 0;
  for (const name of Reflect.ownKeys(value)) {
    if (typeof name === "symbol") return `${path} contains a symbol-keyed array property.`;
    const descriptor = Object.getOwnPropertyDescriptor(value, name);
    if (descriptor === undefined || !("value" in descriptor)) return `${propertyPath(path, name)} must be a data property.`;
    if (name === "length") continue;
    if (!isArrayElementName(name, value.length)) return `${propertyPath(path, name)} is not a JSON array element.`;
    if (!descriptor.enumerable) return `${propertyPath(path, name)} must be an enumerable JSON array element.`;
    elementCount++;
    const error = jsonValueShapeError(descriptor.value, `${path}[${name}]`, active);
    if (error !== undefined) return error;
  }
  return elementCount === value.length ? undefined : `${path} must not be a sparse array.`;
}

function isArrayElementName(name: string, length: number): boolean {
  const index = Number(name);
  return Number.isInteger(index) && index >= 0 && index < length && String(index) === name;
}

function isPlainObjectPrototype(prototype: object | null): boolean {
  if (prototype === null || prototype === Object.prototype) return true;
  if (Object.getPrototypeOf(prototype) !== null) return false;
  return hasIntrinsicConstructor(prototype, "Object");
}

function isArrayPrototype(prototype: object | null): boolean {
  return prototype === Array.prototype
    || prototype !== null && Array.isArray(prototype) && hasIntrinsicConstructor(prototype, "Array");
}

function hasIntrinsicConstructor(prototype: object, name: "Array" | "Object"): boolean {
  const descriptor = Object.getOwnPropertyDescriptor(prototype, "constructor");
  return descriptor !== undefined
    && "value" in descriptor
    && typeof descriptor.value === "function"
    && descriptor.value.name === name
    && descriptor.value.prototype === prototype;
}

function instanceError(
  schema: JsonSchema,
  value: unknown,
  path: string,
  definitions: Readonly<Record<string, JsonSchema>>,
  version: DescriptorVersion,
  depth: number,
): string | undefined {
  if (depth > MAXIMUM_SCHEMA_DEPTH) return `${path} exceeds the RPC value depth limit.`;
  if (typeof schema.$ref === "string") {
    const target = definitions[referenceName(schema.$ref)];
    return target === undefined
      ? `${path} references a missing schema.`
      : instanceError(target, value, path, definitions, version, depth + 1);
  }
  if (Array.isArray(schema.oneOf)) {
    let matches = 0;
    let lastError: string | undefined;
    for (const variant of asSchemaArray(schema.oneOf)) {
      const error = instanceError(variant, value, path, definitions, version, depth + 1);
      if (error === undefined) matches++;
      else lastError = error;
    }
    if (matches === 1) return undefined;
    return matches === 0 ? lastError ?? `${path} does not match a union variant.` : `${path} matches multiple union variants.`;
  }
  if (typeof schema.logicalType === "string") return logicalInstanceError(schema.logicalType, value, path);

  const type = schema.type;
  if (!matchesType(value, type)) return `${path} must be of type '${String(type)}'.`;
  if (has(schema, "const") && !literalEquals(value, schema.const)) return `${path} does not match its const value.`;
  if (Array.isArray(schema.enum) && !schema.enum.some((item) => literalEquals(value, item))) {
    return `${path} is not an allowed enum value.`;
  }

  switch (type) {
    case "object":
      return objectInstanceError(schema, value as Record<string, unknown>, path, definitions, version, depth);
    case "array":
      return arrayInstanceError(schema, value as readonly unknown[], path, definitions, version, depth);
    case "string": {
      const text = value as string;
      if (!hasUnicodeScalars(text)) return `${path} contains an unpaired UTF-16 surrogate.`;
      const length = [...text].length;
      if (length < Number(schema.minLength) || length > Number(schema.maxLength)) {
        return `${path} has ${length} Unicode scalar values; expected ${String(schema.minLength)} through ${String(schema.maxLength)}.`;
      }
      return undefined;
    }
    case "integer":
    case "number":
      return numberInstanceError(schema, value as number, path);
    default:
      return undefined;
  }
}

function objectInstanceError(
  schema: JsonSchema,
  value: Record<string, unknown>,
  path: string,
  definitions: Readonly<Record<string, JsonSchema>>,
  version: DescriptorVersion,
  depth: number,
): string | undefined {
  const entries = Object.entries(value);
  const folded = new Set<string>();
  for (const [name] of entries) {
    if (!hasUnicodeScalars(name)) return `${path} contains a property name with an unpaired UTF-16 surrogate.`;
    const key = version === 2 ? asciiCaseFold(name) : v1CaseFold(name);
    if (folded.has(key)) return `${path} contains duplicate or case-colliding property '${name}'.`;
    folded.add(key);
  }
  const minimum = typeof schema.minProperties === "number" ? schema.minProperties : 0;
  const properties = isObject(schema.properties) ? schema.properties : {};
  const maximum = typeof schema.maxProperties === "number" ? schema.maxProperties : Object.keys(properties).length;
  if (entries.length < minimum || entries.length > maximum) {
    return `${path} has ${entries.length} properties; expected ${minimum} through ${maximum}.`;
  }

  if (schema.additionalProperties === false) {
    for (const required of asStringArray(schema.required)) {
      if (!has(value, required)) return `${path} is missing required property '${required}'.`;
    }
    for (const [name, item] of entries) {
      const propertySchema = properties[name];
      if (!isObject(propertySchema)) return `${path} contains undeclared property '${name}'.`;
      const error = instanceError(propertySchema, item, propertyPath(path, name), definitions, version, depth + 1);
      if (error !== undefined) return error;
    }
    return undefined;
  }

  const keySchema = asSchema(schema.propertyNames);
  const itemSchema = asSchema(schema.additionalProperties);
  for (const [name, item] of entries) {
    const keyError = instanceError(keySchema, name, `${path}.<key>`, definitions, version, depth + 1);
    if (keyError !== undefined) return keyError;
    const itemError = instanceError(itemSchema, item, propertyPath(path, name), definitions, version, depth + 1);
    if (itemError !== undefined) return itemError;
  }
  return undefined;
}

function arrayInstanceError(
  schema: JsonSchema,
  value: readonly unknown[],
  path: string,
  definitions: Readonly<Record<string, JsonSchema>>,
  version: DescriptorVersion,
  depth: number,
): string | undefined {
  const minimum = Number(schema.minItems);
  const maximum = Number(schema.maxItems);
  if (value.length < minimum || value.length > maximum) {
    return `${path} has ${value.length} items; expected ${minimum} through ${maximum}.`;
  }
  const itemSchema = asSchema(schema.items);
  for (let index = 0; index < value.length; index++) {
    const error = instanceError(itemSchema, value[index], `${path}[${index}]`, definitions, version, depth + 1);
    if (error !== undefined) return error;
  }
  return undefined;
}

function numberInstanceError(schema: JsonSchema, value: number, path: string): string | undefined {
  if (typeof schema.minimum === "number" && value < schema.minimum
    || typeof schema.exclusiveMinimum === "number" && value <= schema.exclusiveMinimum
    || typeof schema.maximum === "number" && value > schema.maximum
    || typeof schema.exclusiveMaximum === "number" && value >= schema.exclusiveMaximum) {
    return `${path} is outside its numeric bounds.`;
  }
  return undefined;
}

function logicalInstanceError(logicalType: string, value: unknown, path: string): string | undefined {
  if (logicalType === "uuid") {
    return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value)
      ? undefined
      : `${path} must be a canonical lowercase UUID.`;
  }
  if (logicalType === "utc-date-time") {
    return typeof value === "string" && isUtcDateTime(value)
      ? undefined
      : `${path} must be a canonical UTC date-time.`;
  }
  if (logicalType === "content-reference") return contentReferenceError(value, path);
  return `${path} uses unsupported logical type '${logicalType}'.`;
}

function contentReferenceError(value: unknown, path: string): string | undefined {
  if (!isObject(value)) return `${path} must be a content-reference object.`;
  const required = ["id", "length", "sha256", "mediaType", "fileName", "expiresAtUtc", "repeatability"] as const;
  const names = Object.keys(value);
  if (names.length !== required.length || required.some((name) => !has(value, name))) {
    return `${path} must contain exactly the seven content-reference members.`;
  }
  for (const name of names) {
    if (!(required as readonly string[]).includes(name)) return `${path} contains unsupported property '${name}'.`;
  }
  if (typeof value.id !== "string" || !hasUnicodeScalars(value.id) || scalarLength(value.id) < 1 || scalarLength(value.id) > 256) {
    return `${path}.id must contain 1 through 256 Unicode scalar values.`;
  }
  if (typeof value.length !== "number" || !Number.isSafeInteger(value.length) || value.length < 0) {
    return `${path}.length must be a non-negative safe integer.`;
  }
  if (typeof value.sha256 !== "string" || !/^[0-9a-f]{64}$/u.test(value.sha256)) {
    return `${path}.sha256 must be 64 lowercase hexadecimal characters.`;
  }
  if (typeof value.mediaType !== "string" || !/^[\x00-\x7f]{1,256}$/u.test(value.mediaType)) {
    return `${path}.mediaType must contain 1 through 256 ASCII characters.`;
  }
  if (typeof value.fileName !== "string" || !hasUnicodeScalars(value.fileName) || scalarLength(value.fileName) > 1024) {
    return `${path}.fileName must contain at most 1024 Unicode scalar values.`;
  }
  if (typeof value.expiresAtUtc !== "string" || !isUtcDateTime(value.expiresAtUtc)) {
    return `${path}.expiresAtUtc must be a canonical UTC date-time.`;
  }
  if (value.repeatability !== "single-use" && value.repeatability !== "repeatable") {
    return `${path}.repeatability must be 'single-use' or 'repeatable'.`;
  }
  return undefined;
}

function matchesType(value: unknown, type: JsonValue | undefined): boolean {
  switch (type) {
    case "object": return isObject(value);
    case "array": return Array.isArray(value);
    case "string": return typeof value === "string";
    case "integer": return typeof value === "number" && Number.isSafeInteger(value);
    case "number": return typeof value === "number" && Number.isFinite(value);
    case "boolean": return typeof value === "boolean";
    case "null": return value === null;
    default: return false;
  }
}

function literalEquals(value: unknown, literal: JsonValue | undefined): boolean {
  return value === literal;
}

function isUtcDateTime(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.([0-9]{0,8}[1-9]))?Z$/u.exec(value);
  if (match === null) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (year < 1 || year > 9999 || month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59) return false;
  const days = [31, isLeapYear(year) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return day >= 1 && day <= (days[month - 1] ?? 0);
}

function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

function readApiReference(reference: RpcSchemaReference): string {
  if (Object.keys(reference).length !== 1 || typeof reference.$ref !== "string") {
    throw new RpcInstanceError("A schema reference must be a closed $ref object.");
  }
  return reference.$ref;
}

function referenceName(reference: string): string {
  if (!reference.startsWith(REFERENCE_PREFIX)) throw new RpcInstanceError(`Schema reference '${reference}' is not local.`);
  const name = reference.slice(REFERENCE_PREFIX.length);
  if (!/^[A-Za-z0-9._-]{1,128}$/u.test(name)) throw new RpcInstanceError(`Schema reference '${reference}' is malformed.`);
  return name;
}

function requireV2Descriptor(descriptor: RpcContractDescriptor): asserts descriptor is RpcContractDescriptorV2 {
  validateRpcContractDescriptor(descriptor);
  if (descriptor.descriptorVersion !== 2) throw new TypeError("The ABI projection is defined only for descriptor version 2.");
}

async function sha256(value: Uint8Array): Promise<string> {
  if (globalThis.crypto?.subtle === undefined) throw new TypeError("SHA-256 requires the global Web Crypto API.");
  const digest = await globalThis.crypto.subtle.digest("SHA-256", Uint8Array.from(value));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function compareBytes(left: Uint8Array, right: Uint8Array): number {
  for (let index = 0; index < Math.min(left.length, right.length); index++) {
    const comparison = (left[index] ?? 0) - (right[index] ?? 0);
    if (comparison !== 0) return comparison;
  }
  return left.length - right.length;
}

function asSchema(value: JsonValue | undefined): JsonSchema {
  if (!isObject(value)) throw new TypeError("The validated descriptor contains an invalid schema object.");
  return value as JsonSchema;
}

function asSchemaArray(value: JsonValue | undefined): readonly JsonSchema[] {
  if (!Array.isArray(value)) throw new TypeError("The validated descriptor contains an invalid schema array.");
  return value.map(asSchema);
}

function asSchemaMap(value: JsonValue | undefined): Readonly<Record<string, JsonSchema>> {
  if (!isObject(value)) throw new TypeError("The validated descriptor contains an invalid schema map.");
  return value as unknown as Readonly<Record<string, JsonSchema>>;
}

function asStringArray(value: JsonValue | undefined): readonly string[] {
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string")) {
    throw new TypeError("The validated descriptor contains an invalid string array.");
  }
  return value as readonly string[];
}

function asJsonArray(value: JsonValue | undefined): readonly JsonValue[] {
  if (!Array.isArray(value)) throw new TypeError("The validated descriptor contains an invalid array.");
  return value;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function has(object: object, name: string): boolean {
  return Object.prototype.hasOwnProperty.call(object, name);
}

function propertyPath(path: string, name: string): string {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/u.test(name) ? `${path}.${name}` : `${path}[${JSON.stringify(name)}]`;
}

function asciiCaseFold(value: string): string {
  return value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 0x20));
}

function v1CaseFold(value: string): string {
  return value.toLocaleLowerCase("en-US");
}

function hasUnicodeScalars(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const current = value.charCodeAt(index);
    if (current >= 0xd800 && current <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      index++;
    } else if (current >= 0xdc00 && current <= 0xdfff) {
      return false;
    }
  }
  return true;
}

function scalarLength(value: string): number {
  return [...value].length;
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
