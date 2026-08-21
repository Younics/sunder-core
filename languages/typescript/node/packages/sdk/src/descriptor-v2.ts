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
  for (const [code, declaration] of Object.entries(descriptor.errors)) {
    const projected = jsonRecord();
    if (declaration.detailsSchema !== undefined) {
      projected.detailsSchema = cloneJsonValue(declaration.detailsSchema as unknown as JsonValue);
    }
    errors[code] = projected;
  }

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
    schemaProfile: descriptor.schemaProfile,
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
  const error = instanceError(schema, value, "$", descriptor.$defs, descriptor.descriptorVersion);
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
  const pending: JsonShapeWork[] = [{ kind: "visit", value, path }];
  while (pending.length > 0) {
    const work = pending.pop();
    if (work === undefined) break;
    if (work.kind === "leave") {
      active.delete(work.value);
      continue;
    }

    const item = work.value;
    if (item === null || typeof item === "boolean") continue;
    if (typeof item === "string") {
      if (!hasUnicodeScalars(item)) return `${work.path} contains an unpaired UTF-16 surrogate.`;
      continue;
    }
    if (typeof item === "number") {
      if (!Number.isFinite(item)) return `${work.path} must be a finite JSON number.`;
      continue;
    }
    if (typeof item !== "object") return `${work.path} contains a value that is not JSON.`;
    if (active.has(item)) return `${work.path} contains a cyclic object graph.`;
    active.add(item);
    pending.push({ kind: "leave", value: item });

    const children: { readonly value: unknown; readonly path: string }[] = [];
    if (Array.isArray(item)) {
      if (!isArrayPrototype(Object.getPrototypeOf(item) as object | null)) return `${work.path} must be a normal JSON array.`;
      let elementCount = 0;
      for (const name of Reflect.ownKeys(item)) {
        if (typeof name === "symbol") return `${work.path} contains a symbol-keyed array property.`;
        const descriptor = Object.getOwnPropertyDescriptor(item, name);
        if (descriptor === undefined || !("value" in descriptor)) return `${propertyPath(work.path, name)} must be a data property.`;
        if (name === "length") continue;
        if (!isArrayElementName(name, item.length)) return `${propertyPath(work.path, name)} is not a JSON array element.`;
        if (!descriptor.enumerable) return `${propertyPath(work.path, name)} must be an enumerable JSON array element.`;
        elementCount++;
        children.push({ value: descriptor.value, path: arrayItemPath(work.path, name) });
      }
      if (elementCount !== item.length) return `${work.path} must not be a sparse array.`;
    } else {
      const prototype = Object.getPrototypeOf(item) as object | null;
      if (!isPlainObjectPrototype(prototype)) return `${work.path} must be a plain object or null-prototype record.`;
      for (const name of Reflect.ownKeys(item)) {
        if (typeof name === "symbol") return `${work.path} contains a symbol-keyed property.`;
        const descriptor = Object.getOwnPropertyDescriptor(item, name);
        if (descriptor === undefined || !("value" in descriptor)) return `${propertyPath(work.path, name)} must be a data property.`;
        if (!descriptor.enumerable) return `${propertyPath(work.path, name)} must be an enumerable JSON property.`;
        children.push({ value: descriptor.value, path: propertyPath(work.path, name) });
      }
    }
    for (let index = children.length - 1; index >= 0; index--) {
      const child = children[index];
      if (child !== undefined) pending.push({ kind: "visit", value: child.value, path: child.path });
    }
  }
  return undefined;
}

type JsonShapeWork =
  | { readonly kind: "visit"; readonly value: unknown; readonly path: string }
  | { readonly kind: "leave"; readonly value: object };

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
): string | undefined {
  const pending: InstanceWork[] = [{ schema, value, path }];
  while (pending.length > 0) {
    const work = pending.pop();
    if (work === undefined) break;
    let currentSchema = work.schema;
    while (typeof currentSchema.$ref === "string") {
      const target = definitions[referenceName(currentSchema.$ref)];
      if (target === undefined) return `${work.path} references a missing schema.`;
      currentSchema = target;
    }
    if (Array.isArray(currentSchema.oneOf)) {
      const selected = selectUnionVariant(asSchemaArray(currentSchema.oneOf), work.value, work.path, definitions);
      if (typeof selected === "string") return selected;
      pending.push({ schema: selected, value: work.value, path: work.path });
      continue;
    }
    if (typeof currentSchema.logicalType === "string") {
      const error = logicalInstanceError(currentSchema.logicalType, work.value, work.path);
      if (error !== undefined) return error;
      continue;
    }

    const structuralNames = Object.keys(currentSchema).filter((name) => name !== "title" && name !== "description");
    if (structuralNames.length === 0) continue;

    const type = currentSchema.type;
    if (!matchesType(work.value, type)) return `${work.path} must be of type '${String(type)}'.`;
    if (has(currentSchema, "const") && !literalEquals(work.value, currentSchema.const, currentSchema)) {
      return `${work.path} does not match its const value.`;
    }
    if (Array.isArray(currentSchema.enum)
      && !currentSchema.enum.some((item) => literalEquals(work.value, item, currentSchema))) {
      return `${work.path} is not an allowed enum value.`;
    }

    switch (type) {
      case "object":
        if (structuralNames.every((name) => name === "type" || name === "maxProperties")) {
          if (typeof currentSchema.maxProperties === "number"
            && Object.keys(work.value as object).length > currentSchema.maxProperties) {
            return `${work.path} exceeds maxProperties ${currentSchema.maxProperties}.`;
          }
        } else {
          const error = queueObjectInstance(
            currentSchema,
            work.value as Record<string, unknown>,
            work.path,
            version,
            pending,
          );
          if (error !== undefined) return error;
        }
        break;
      case "array": {
        const error = queueArrayInstance(
          currentSchema,
          work.value as readonly unknown[],
          work.path,
          pending,
        );
        if (error !== undefined) return error;
        break;
      }
      case "string": {
        const text = work.value as string;
        if (!hasUnicodeScalars(text)) return `${work.path} contains an unpaired UTF-16 surrogate.`;
        if (typeof currentSchema.contentEncoding === "string") {
          const error = base64InstanceError(currentSchema, text, work.path);
          if (error !== undefined) return error;
          break;
        }
        if (typeof currentSchema.format === "string") {
          const error = formatInstanceError(currentSchema, text, work.path);
          if (error !== undefined) return error;
          break;
        }
        const minimum = typeof currentSchema.minLength === "number" ? currentSchema.minLength : 0;
        const maximum = typeof currentSchema.maxLength === "number" ? currentSchema.maxLength : Number.POSITIVE_INFINITY;
        const length = scalarLength(text);
        if (length < minimum || length > maximum) {
          return `${work.path} has ${length} Unicode scalar values outside its declared length bounds.`;
        }
        break;
      }
      case "integer":
      case "number": {
        const error = numberInstanceError(currentSchema, work.value as number, work.path);
        if (error !== undefined) return error;
        break;
      }
    }
  }
  return undefined;
}

interface InstanceWork {
  readonly schema: JsonSchema;
  readonly value: unknown;
  readonly path: string;
}

function selectUnionVariant(
  variants: readonly JsonSchema[],
  value: unknown,
  path: string,
  definitions: Readonly<Record<string, JsonSchema>>,
): JsonSchema | string {
  const resolved = variants.map((variant) => resolveSchema(variant, definitions));
  const nullIndex = resolved.findIndex((variant) => variant.type === "null");
  if (variants.length === 2 && nullIndex >= 0) {
    return variants[value === null ? nullIndex : 1 - nullIndex] ?? `${path} does not match a nullable union variant.`;
  }
  if (!isObject(value)) return `${path} does not match a tagged union variant.`;

  const discriminator = taggedUnionDiscriminator(resolved);
  const tag = value[discriminator];
  if (typeof tag !== "string") return `${path} is missing tagged union discriminator '${discriminator}'.`;
  const index = resolved.findIndex((variant) => discriminatorValue(variant, discriminator) === tag);
  return variants[index] ?? `${path} has an unknown tagged union discriminator value.`;
}

function queueObjectInstance(
  schema: JsonSchema,
  value: Record<string, unknown>,
  path: string,
  version: DescriptorVersion,
  pending: InstanceWork[],
): string | undefined {
  const entries = Object.entries(value);
  if (version === 1) {
    const folded = new Set<string>();
    for (const [name] of entries) {
      const key = v1CaseFold(name);
      if (folded.has(key)) return `${path} contains duplicate or case-colliding property '${name}'.`;
      folded.add(key);
    }
  }
  const minimum = typeof schema.minProperties === "number" ? schema.minProperties : 0;
  const properties = isObject(schema.properties) ? schema.properties : {};
  const maximum = typeof schema.maxProperties === "number"
    ? schema.maxProperties
    : version === 1 && schema.additionalProperties === false
      ? Object.keys(properties).length
      : Number.POSITIVE_INFINITY;
  if (entries.length < minimum || entries.length > maximum) {
    return `${path} has ${entries.length} properties outside its declared property-count bounds.`;
  }

  if (schema.additionalProperties === false) {
    for (const required of asStringArray(schema.required)) {
      if (!has(value, required)) return `${path} is missing required property '${required}'.`;
    }
    for (let index = entries.length - 1; index >= 0; index--) {
      const [name, item] = entries[index] ?? [];
      if (name === undefined) continue;
      const propertySchema = properties[name];
      if (!isObject(propertySchema)) return `${path} contains undeclared property '${name}'.`;
      pending.push({ schema: propertySchema, value: item, path: propertyPath(path, name) });
    }
    return undefined;
  }

  const keySchema = asSchema(schema.propertyNames);
  const itemSchema = asSchema(schema.additionalProperties);
  for (let index = entries.length - 1; index >= 0; index--) {
    const [name, item] = entries[index] ?? [];
    if (name === undefined) continue;
    pending.push({ schema: itemSchema, value: item, path: propertyPath(path, name) });
    pending.push({ schema: keySchema, value: name, path: propertyPath(path, "<key>") });
  }
  return undefined;
}

function queueArrayInstance(
  schema: JsonSchema,
  value: readonly unknown[],
  path: string,
  pending: InstanceWork[],
): string | undefined {
  const minimum = typeof schema.minItems === "number" ? schema.minItems : 0;
  const maximum = typeof schema.maxItems === "number" ? schema.maxItems : Number.POSITIVE_INFINITY;
  if (value.length < minimum || value.length > maximum) {
    return `${path} has ${value.length} items outside its declared item-count bounds.`;
  }
  const itemSchema = asSchema(schema.items);
  for (let index = value.length - 1; index >= 0; index--) {
    pending.push({ schema: itemSchema, value: value[index], path: arrayItemPath(path, String(index)) });
  }
  return undefined;
}

function numberInstanceError(schema: JsonSchema, value: number, path: string): string | undefined {
  const format = schema.format === "float32" ? "float32" : "float64";
  const number = schema.type === "number" && format === "float32" ? Math.fround(value) : value;
  if (!Number.isFinite(number)) return `${path} must be a finite ${format} value.`;
  const bound = (name: string): number | undefined => typeof schema[name] !== "number"
    ? undefined
    : schema.type === "number" && format === "float32"
      ? Math.fround(schema[name])
      : schema[name];
  const minimum = bound("minimum");
  const exclusiveMinimum = bound("exclusiveMinimum");
  const maximum = bound("maximum");
  const exclusiveMaximum = bound("exclusiveMaximum");
  if (minimum !== undefined && number < minimum
    || exclusiveMinimum !== undefined && number <= exclusiveMinimum
    || maximum !== undefined && number > maximum
    || exclusiveMaximum !== undefined && number >= exclusiveMaximum) {
    return `${path} is outside its numeric bounds.`;
  }
  return undefined;
}

function formatInstanceError(schema: JsonSchema, value: string, path: string): string | undefined {
  switch (schema.format) {
    case "uuid":
      return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value)
        ? undefined
        : `${path} must be a canonical lowercase UUID.`;
    case "date":
      return isDate(value) ? undefined : `${path} must be a canonical Gregorian date.`;
    case "date-time":
      return isOffsetDateTime(value) ? undefined : `${path} must be an asserted RFC 3339 date-time.`;
    case "duration":
      return isDuration(value) ? undefined : `${path} must be a canonical ISO 8601 duration.`;
    case "uri":
      return isUri(value) ? undefined : `${path} must be an absolute URI.`;
    case "big-integer":
      return exactNumericInstanceError(schema, value, path, false);
    case "decimal":
      return exactNumericInstanceError(schema, value, path, true);
    default:
      return `${path} uses unsupported asserted format '${String(schema.format)}'.`;
  }
}

function exactNumericInstanceError(
  schema: JsonSchema,
  value: string,
  path: string,
  decimal: boolean,
): string | undefined {
  const parsed = decimal ? parseCanonicalDecimal(value) : parseCanonicalBigInteger(value);
  if (parsed === undefined) return `${path} must be a canonical ${decimal ? "decimal" : "big-integer"} string.`;
  const precisionFacet = decimal ? schema.maxPrecision : schema.maxDigits;
  const maximumPrecision = typeof precisionFacet === "number" ? precisionFacet : Number.POSITIVE_INFINITY;
  const maximumScale = decimal && typeof schema.maxScale === "number"
    ? schema.maxScale
    : Number.POSITIVE_INFINITY;
  if (parsed.precision > maximumPrecision || parsed.scale > maximumScale) {
    return `${path} exceeds its declared ${decimal ? "precision or scale" : "digit"} limit.`;
  }

  for (const [name, exclusive, lower] of [
    ["valueMinimum", false, true],
    ["valueExclusiveMinimum", true, true],
    ["valueMaximum", false, false],
    ["valueExclusiveMaximum", true, false],
  ] as const) {
    const boundValue = schema[name];
    if (typeof boundValue !== "string") continue;
    const bound = decimal ? parseCanonicalDecimal(boundValue) : parseCanonicalBigInteger(boundValue);
    if (bound === undefined) return `${path} has an invalid schema value bound.`;
    const comparison = compareExactDecimals(parsed, bound);
    if (lower ? comparison < 0 || exclusive && comparison === 0 : comparison > 0 || exclusive && comparison === 0) {
      return `${path} is outside its exact numeric value bounds.`;
    }
  }
  return undefined;
}

function base64InstanceError(schema: JsonSchema, value: string, path: string): string | undefined {
  if (schema.contentEncoding !== "base64" || !isCanonicalBase64(value)) {
    return `${path} must be canonical padded RFC 4648 base64.`;
  }
  const padding = value.endsWith("==") ? 2 : value.endsWith("=") ? 1 : 0;
  const decodedBytes = value.length / 4 * 3 - padding;
  const maximum = typeof schema.maxDecodedBytes === "number"
    ? schema.maxDecodedBytes
    : Number.POSITIVE_INFINITY;
  return decodedBytes <= maximum
    ? undefined
    : `${path} decodes to ${decodedBytes} bytes, exceeding maxDecodedBytes ${maximum}.`;
}

function logicalInstanceError(logicalType: string, value: unknown, path: string): string | undefined {
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

function literalEquals(value: unknown, literal: JsonValue | undefined, schema: JsonSchema): boolean {
  if (typeof value === "number" && typeof literal === "number" && schema.format === "float32") {
    return Math.fround(value) === Math.fround(literal);
  }
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

function isDate(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(value);
  if (match === null) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || year > 9999 || month < 1 || month > 12) return false;
  const days = [31, isLeapYear(year) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return day >= 1 && day <= (days[month - 1] ?? 0);
}

function isOffsetDateTime(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.([0-9]{1,9}))?(Z|[+-]\d{2}:\d{2})$/u.exec(value);
  if (match === null || !isDate(`${match[1]}-${match[2]}-${match[3]}`)) return false;
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (hour > 23 || minute > 59 || second > 59) return false;
  const zone = match[8] ?? "";
  let offsetMinutes = 0;
  if (zone !== "Z") {
    const offsetHours = Number(zone.slice(1, 3));
    const minuteOffset = Number(zone.slice(4, 6));
    if (offsetHours > 14 || minuteOffset > 59 || offsetHours === 14 && minuteOffset !== 0) return false;
    offsetMinutes = (offsetHours * 60 + minuteOffset) * (zone[0] === "-" ? -1 : 1);
  }
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const localMinutes = (daysBeforeYear(year) + daysBeforeMonth(year, month) + day - 1) * 1440
    + hour * 60
    + minute;
  const utcMinutes = localMinutes - offsetMinutes;
  return utcMinutes >= 0 && utcMinutes < daysBeforeYear(10_000) * 1440;
}

function daysBeforeYear(year: number): number {
  const previous = year - 1;
  return previous * 365 + Math.floor(previous / 4) - Math.floor(previous / 100) + Math.floor(previous / 400);
}

function daysBeforeMonth(year: number, month: number): number {
  const days = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];
  return (days[month - 1] ?? 0) + (month > 2 && isLeapYear(year) ? 1 : 0);
}

function isDuration(value: string): boolean {
  const component = "(?:0|[1-9][0-9]*)";
  const seconds = `(?:${component})(?:\\.[0-9]*[1-9])?`;
  const expression = new RegExp(
    `^P(?=.+)(?:${component}Y)?(?:${component}M)?(?:${component}D)?(?:T(?=.+)(?:${component}H)?(?:${component}M)?(?:${seconds}S)?)?$`,
    "u",
  );
  return expression.test(value) && value !== "P" && !value.endsWith("T");
}

function isUri(value: string): boolean {
  if (!/^[A-Za-z][A-Za-z0-9+.-]*:/u.test(value)
    || /[^\x21-\x7e]/u.test(value)
    || value.includes("\\")
    || /%(?![0-9A-Fa-f]{2})/u.test(value)) return false;
  try {
    void new URL(value);
    return true;
  } catch {
    return false;
  }
}

function isCanonicalBase64(value: string): boolean {
  if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) return false;
  if (value.endsWith("==")) return base64Sextet(value[value.length - 3]) % 16 === 0;
  if (value.endsWith("=")) return base64Sextet(value[value.length - 2]) % 4 === 0;
  return true;
}

function base64Sextet(character: string | undefined): number {
  if (character === undefined) return -1;
  const code = character.charCodeAt(0);
  if (code >= 0x41 && code <= 0x5a) return code - 0x41;
  if (code >= 0x61 && code <= 0x7a) return code - 0x61 + 26;
  if (code >= 0x30 && code <= 0x39) return code - 0x30 + 52;
  return character === "+" ? 62 : character === "/" ? 63 : -1;
}

interface ExactDecimal {
  readonly negative: boolean;
  readonly integerPart: string;
  readonly fractionPart: string;
  readonly precision: number;
  readonly scale: number;
}

function parseCanonicalBigInteger(value: string): ExactDecimal | undefined {
  if (!/^(?:0|-[1-9][0-9]*|[1-9][0-9]*)$/u.test(value)) return undefined;
  const digits = value[0] === "-" ? value.slice(1) : value;
  return {
    negative: value[0] === "-",
    integerPart: digits,
    fractionPart: "",
    precision: digits.length,
    scale: 0,
  };
}

function parseCanonicalDecimal(value: string): ExactDecimal | undefined {
  if (!/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]*[1-9])?$/u.test(value) || value === "-0") return undefined;
  const negative = value[0] === "-";
  const unsigned = negative ? value.slice(1) : value;
  const [integer = "", fraction = ""] = unsigned.split(".");
  const digits = `${integer}${fraction}`;
  const significant = digits.replace(/^0+/u, "");
  return {
    negative,
    integerPart: integer,
    fractionPart: fraction,
    precision: significant.length === 0 ? 1 : significant.length,
    scale: fraction.length,
  };
}

function compareExactDecimals(left: ExactDecimal, right: ExactDecimal): number {
  const leftZero = left.integerPart === "0" && /^0*$/u.test(left.fractionPart);
  const rightZero = right.integerPart === "0" && /^0*$/u.test(right.fractionPart);
  if (leftZero && rightZero) return 0;
  if (left.negative !== right.negative) return left.negative ? -1 : 1;
  const magnitude = compareExactMagnitude(left, right);
  return left.negative ? -magnitude : magnitude;
}

function compareExactMagnitude(left: ExactDecimal, right: ExactDecimal): number {
  const leftIntegerLength = left.integerPart === "0" ? 0 : left.integerPart.length;
  const rightIntegerLength = right.integerPart === "0" ? 0 : right.integerPart.length;
  if (leftIntegerLength !== rightIntegerLength) return leftIntegerLength < rightIntegerLength ? -1 : 1;
  if (leftIntegerLength > 0) {
    const integerComparison = ordinal(left.integerPart, right.integerPart);
    if (integerComparison !== 0) return integerComparison;
  }
  const fractionLength = Math.max(left.fractionPart.length, right.fractionPart.length);
  for (let index = 0; index < fractionLength; index++) {
    const leftDigit = left.fractionPart[index] ?? "0";
    const rightDigit = right.fractionPart[index] ?? "0";
    if (leftDigit !== rightDigit) return leftDigit < rightDigit ? -1 : 1;
  }
  return 0;
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
  if (path.length >= 4096 || name.length >= 4096 - path.length) return path;
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/u.test(name) ? `${path}.${name}` : `${path}[${JSON.stringify(name)}]`;
}

function arrayItemPath(path: string, index: string): string {
  return path.length >= 4096 ? path : `${path}[${index}]`;
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
  let length = 0;
  for (let index = 0; index < value.length; index++) {
    if (value.charCodeAt(index) >= 0xd800 && value.charCodeAt(index) <= 0xdbff) index++;
    length++;
  }
  return length;
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
