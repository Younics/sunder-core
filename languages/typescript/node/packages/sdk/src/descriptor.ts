import type { JsonValue, RpcContractDescriptor } from "./types";
import { isPackageId, isSemanticVersion } from "./validation";

const MAXIMUM_DESCRIPTOR_BYTES = 4 * 1024 * 1024;
const MAXIMUM_DEPTH = 64;
const MAXIMUM_SCHEMA_NODES = 4096;
const MAXIMUM_COLLECTION_ITEMS = 1_000_000;

type JsonObject = { readonly [key: string]: JsonValue };

export function parseRpcContractDescriptor(input: string | Uint8Array): RpcContractDescriptor {
  const text = typeof input === "string"
    ? checkedDescriptorText(input)
    : decodeDescriptor(input);
  const value = new StrictJsonParser(text).parse();
  validateRpcContractDescriptor(value);
  return value;
}

export function validateRpcContractDescriptor(value: unknown): asserts value is RpcContractDescriptor {
  validateJsonValue(value, "$", 0, { nodes: 0 });
  const root = objectValue(value, "$", "an object");
  only(root, "$", "$schema", "descriptorVersion", "contractId", "version", "services", "$defs");
  if (has(root, "$schema") && root.$schema !== "https://json-schema.org/draft/2020-12/schema") {
    fail("$.$schema must be the JSON Schema Draft 2020-12 URI.");
  }
  if (root.descriptorVersion !== 1) fail("$.descriptorVersion must be 1.");
  const contractId = stringValue(root, "contractId", "$", 128);
  if (!isPackageId(contractId)) fail("$.contractId must use lowercase dot-separated ASCII identifiers.");
  const version = stringValue(root, "version", "$", 256);
  if (!isSemanticVersion(version)) fail("$.version must be an independent strict SemVer 2.0 version.");

  const definitions = objectValue(required(root, "$defs", "$"), "$.$defs", "an object");
  const definitionNames = Object.keys(definitions);
  if (definitionNames.length < 1 || definitionNames.length > 256) {
    fail("$.$defs must contain between 1 and 256 schemas.");
  }
  for (const name of definitionNames) {
    if (!isDefinitionName(name)) fail(`$.$defs contains invalid schema name '${name}'.`);
  }

  validateServices(required(root, "services", "$"), definitions);
  const schemaState: SchemaState = { definitions, validated: new Set<string>(), nodes: 0 };
  for (const name of definitionNames) {
    validateSchema(objectValue(definitions[name], `$.$defs.${name}`, "a schema object"), `$.$defs.${name}`, schemaState, new Set([name]), 1);
    schemaState.validated.add(name);
  }
}

function validateJsonValue(
  value: unknown,
  path: string,
  depth: number,
  state: { nodes: number },
): void {
  if (depth > MAXIMUM_DEPTH || ++state.nodes > 100_000) fail("The RPC descriptor exceeds its JSON depth or token limit.");
  if (value === null || typeof value === "string" || typeof value === "boolean") return;
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) fail(`${path} contains a number that is not an IEEE-754 safe integer.`);
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => validateJsonValue(item, `${path}[${index}]`, depth + 1, state));
    return;
  }
  if (typeof value !== "object") fail(`${path} contains a value that is not JSON.`);
  const insensitive = new Set<string>();
  for (const [name, item] of Object.entries(value)) {
    const folded = name.toLocaleLowerCase("en-US");
    if (insensitive.has(folded)) fail(`${path} contains case-colliding property '${name}'.`);
    insensitive.add(folded);
    validateJsonValue(item, `${path}.${name}`, depth + 1, state);
  }
}

export function canonicalizeDescriptor(value: JsonValue): string {
  const write = (item: JsonValue): string => {
    if (item === null || typeof item === "boolean" || typeof item === "string") return JSON.stringify(item);
    if (typeof item === "number") {
      if (!Number.isSafeInteger(item)) throw new TypeError("Descriptor numbers must be IEEE-754 safe integers.");
      return String(item);
    }
    if (Array.isArray(item)) return `[${item.map(write).join(",")}]`;
    const object = item as JsonObject;
    return `{${Object.keys(object).sort(ordinal).map((key) => `${JSON.stringify(key)}:${write(object[key] ?? null)}`).join(",")}}`;
  };
  return write(value);
}

function validateServices(value: JsonValue, definitions: JsonObject): void {
  const entries: readonly [string | undefined, JsonValue][] = Array.isArray(value)
    ? value.map((service) => [undefined, service] as const)
    : Object.entries(objectValue(value, "$.services", "an array or object"));
  if (entries.length < 1 || entries.length > 64) fail("$.services must contain between 1 and 64 services.");
  const serviceIds = new Set<string>();
  let methodCount = 0;
  for (const [key, serviceValue] of entries) {
    const path = key === undefined ? "$.services[]" : `$.services.${key}`;
    const service = objectValue(serviceValue, path, "an object");
    only(service, path, "serviceId", "methods");
    const serviceId = key ?? stringValue(service, "serviceId", path, 128);
    if (has(service, "serviceId") && service.serviceId !== serviceId) fail(`${path}.serviceId must match its services object key.`);
    if (!isMemberId(serviceId)) fail(`${path}.serviceId is not a stable lowercase ASCII identifier.`);
    if (serviceIds.has(serviceId)) fail(`$.services declares serviceId '${serviceId}' more than once.`);
    serviceIds.add(serviceId);

    const methodsValue = required(service, "methods", path);
    const methodEntries: readonly [string | undefined, JsonValue][] = Array.isArray(methodsValue)
      ? methodsValue.map((method) => [undefined, method] as const)
      : Object.entries(objectValue(methodsValue, `${path}.methods`, "an array or object"));
    if (methodEntries.length === 0) fail(`${path}.methods must contain at least one method.`);
    methodCount += methodEntries.length;
    if (methodCount > 512) fail("An RPC descriptor may declare at most 512 methods.");
    const methodIds = new Set<string>();
    for (const [methodKey, methodValue] of methodEntries) {
      const methodPath = methodKey === undefined ? `${path}.methods[]` : `${path}.methods.${methodKey}`;
      const method = objectValue(methodValue, methodPath, "an object");
      only(method, methodPath, "methodId", "kind", "requestSchema", "responseSchema", "eventSchema");
      const methodId = methodKey ?? stringValue(method, "methodId", methodPath, 128);
      if (has(method, "methodId") && method.methodId !== methodId) fail(`${methodPath}.methodId must match its methods object key.`);
      if (!isMemberId(methodId)) fail(`${methodPath}.methodId is not a stable lowercase ASCII identifier.`);
      if (methodIds.has(methodId)) fail(`${path}.methods declares methodId '${methodId}' more than once.`);
      methodIds.add(methodId);
      const kind = stringValue(method, "kind", methodPath, 32);
      if (kind !== "unary" && kind !== "server-stream") fail(`${methodPath}.kind must be 'unary' or 'server-stream'.`);
      readSchemaReference(required(method, "requestSchema", methodPath), `${methodPath}.requestSchema`, definitions);
      const output = kind === "unary" ? "responseSchema" : "eventSchema";
      const unexpected = kind === "unary" ? "eventSchema" : "responseSchema";
      if (has(method, unexpected)) fail(`${methodPath}.${unexpected} is not valid for a ${kind} method.`);
      readSchemaReference(required(method, output, methodPath), `${methodPath}.${output}`, definitions);
    }
  }
}

interface SchemaState {
  readonly definitions: JsonObject;
  readonly validated: Set<string>;
  nodes: number;
}

function validateSchema(
  schema: JsonObject,
  path: string,
  state: SchemaState,
  referenceStack: Set<string>,
  depth: number,
): void {
  if (depth > MAXIMUM_DEPTH || ++state.nodes > MAXIMUM_SCHEMA_NODES) fail(`${path} exceeds the RPC schema depth or node-count limit.`);
  validateAnnotations(schema, path);
  if (has(schema, "$ref")) {
    only(schema, path, "$ref", "title", "description");
    const referenceName = referenceNameValue(schema.$ref, path, state.definitions);
    if (referenceStack.has(referenceName)) fail(`${path} contains a recursive $ref cycle.`);
    if (!state.validated.has(referenceName)) {
      referenceStack.add(referenceName);
      validateSchema(
        objectValue(state.definitions[referenceName], `$.$defs.${referenceName}`, "a schema object"),
        `$.$defs.${referenceName}`,
        state,
        referenceStack,
        depth + 1,
      );
      referenceStack.delete(referenceName);
      state.validated.add(referenceName);
    }
    return;
  }
  if (has(schema, "oneOf")) {
    only(schema, path, "oneOf", "title", "description");
    const variants = arrayValue(schema.oneOf, `${path}.oneOf`);
    if (variants.length < 2 || variants.length > 16) fail(`${path}.oneOf must contain between 2 and 16 variants.`);
    variants.forEach((variant, index) => validateSchema(
      objectValue(variant, `${path}.oneOf[${index}]`, "a schema object"),
      `${path}.oneOf[${index}]`,
      state,
      new Set(referenceStack),
      depth + 1,
    ));
    validateUnion(variants, path, state.definitions);
    return;
  }

  const type = stringValue(schema, "type", path, 16);
  switch (type) {
    case "object":
      validateObjectSchema(schema, path, state, referenceStack, depth);
      break;
    case "array":
      only(schema, path, "type", "items", "minItems", "maxItems", "enum", "const", "title", "description");
      validateOrderedBounds(schema, path, "minItems", "maxItems", MAXIMUM_COLLECTION_ITEMS);
      validateSchema(objectValue(required(schema, "items", path), `${path}.items`, "a schema object"), `${path}.items`, state, referenceStack, depth + 1);
      break;
    case "string":
      only(schema, path, "type", "minLength", "maxLength", "enum", "const", "title", "description");
      validateOrderedBounds(schema, path, "minLength", "maxLength", MAXIMUM_COLLECTION_ITEMS);
      break;
    case "integer":
    case "number":
      validateNumberSchema(schema, path, type);
      break;
    case "boolean":
    case "null":
      only(schema, path, "type", "enum", "const", "title", "description");
      break;
    default:
      fail(`${path}.type '${type}' is outside the RPC schema profile.`);
  }
  validateEnumAndConst(schema, path, type);
}

function validateObjectSchema(
  schema: JsonObject,
  path: string,
  state: SchemaState,
  referenceStack: Set<string>,
  depth: number,
): void {
  only(schema, path, "type", "properties", "required", "additionalProperties", "propertyNames", "minProperties", "maxProperties", "enum", "const", "title", "description");
  const additional = required(schema, "additionalProperties", path);
  if (additional === false) {
    const properties = objectValue(required(schema, "properties", path), `${path}.properties`, "an object");
    const propertyNames = Object.keys(properties);
    if (propertyNames.length > 256) fail(`${path}.properties exceeds the 256-property limit.`);
    const requiredNames = arrayValue(required(schema, "required", path), `${path}.required`);
    const seen = new Set<string>();
    for (const name of requiredNames) {
      if (typeof name !== "string" || name.length === 0 || seen.has(name)) fail(`${path}.required must contain unique non-empty property names.`);
      if (!has(properties, name)) fail(`${path}.required references undeclared property '${name}'.`);
      seen.add(name);
    }
    for (const name of propertyNames) {
      if (name.length === 0 || name.length > 128) fail(`${path}.properties contains an empty or overlong property name.`);
      validateSchema(objectValue(properties[name], `${path}.properties.${name}`, "a schema object"), `${path}.properties.${name}`, state, referenceStack, depth + 1);
    }
    const minimum = has(schema, "minProperties") ? integerBound(schema.minProperties, `${path}.minProperties`, 0, 256) : 0;
    const maximum = has(schema, "maxProperties") ? integerBound(schema.maxProperties, `${path}.maxProperties`, 0, 256) : propertyNames.length;
    if (minimum > maximum || maximum > propertyNames.length) fail(`${path}'s object property bounds are inconsistent with its closed properties.`);
    if (has(schema, "propertyNames")) fail(`${path}.propertyNames is only supported for bounded map schemas.`);
  } else {
    const additionalSchema = objectValue(additional, `${path}.additionalProperties`, "a schema object or false");
    if (has(schema, "properties") && Object.keys(objectValue(schema.properties, `${path}.properties`, "an object")).length !== 0) {
      fail(`${path} cannot mix named properties with map additionalProperties.`);
    }
    if (has(schema, "required") && arrayValue(schema.required, `${path}.required`).length !== 0) fail(`${path}.required must be absent or empty for a map schema.`);
    validateOrderedBounds(schema, path, "minProperties", "maxProperties", MAXIMUM_COLLECTION_ITEMS);
    const propertyNamesSchema = objectValue(required(schema, "propertyNames", path), `${path}.propertyNames`, "a schema object");
    validateSchema(propertyNamesSchema, `${path}.propertyNames`, state, referenceStack, depth + 1);
    if (propertyNamesSchema.type !== "string") fail(`${path}.propertyNames must be a bounded string schema.`);
    validateSchema(additionalSchema, `${path}.additionalProperties`, state, referenceStack, depth + 1);
  }
}

function validateNumberSchema(schema: JsonObject, path: string, type: string): void {
  only(schema, path, "type", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "enum", "const", "title", "description");
  const lowerNames = ["minimum", "exclusiveMinimum"].filter((name) => has(schema, name));
  const upperNames = ["maximum", "exclusiveMaximum"].filter((name) => has(schema, name));
  if (lowerNames.length !== 1 || upperNames.length !== 1) fail(`${path} must declare exactly one lower and one upper numeric bound.`);
  const lower = numberValue(schema[lowerNames[0] ?? ""], `${path}.${lowerNames[0] ?? "minimum"}`);
  const upper = numberValue(schema[upperNames[0] ?? ""], `${path}.${upperNames[0] ?? "maximum"}`);
  if (lower > upper) fail(`${path}'s lower numeric bound must not exceed its upper bound.`);
  if (type === "integer" && (!Number.isInteger(lower) || !Number.isInteger(upper))) fail(`${path} integer bounds must be integers.`);
}

function validateUnion(variants: readonly JsonValue[], path: string, definitions: JsonObject): void {
  const resolved = variants.map((variant, index) => resolveSchema(
    objectValue(variant, `${path}.oneOf[${index}]`, "a schema object"),
    definitions,
  ));
  if (resolved.filter((schema) => schema.type === "null").length === 1 && resolved.length === 2) return;
  if (resolved.some((schema) => schema.type !== "object" || schema.additionalProperties !== false)) {
    fail(`${path}.oneOf variants must resolve to closed object schemas.`);
  }
  let candidates: Set<string> | undefined;
  for (const schema of resolved) {
    const properties = objectValue(schema.properties, `${path}.properties`, "an object");
    const requiredNames = new Set(arrayValue(schema.required, `${path}.required`).filter((name): name is string => typeof name === "string"));
    const current = new Set(Object.keys(properties).filter((name) => {
      const property = objectValue(properties[name], `${path}.properties.${name}`, "a schema object");
      return requiredNames.has(name) && property.type === "string" && typeof property.const === "string";
    }));
    candidates = candidates === undefined ? current : new Set([...candidates].filter((name) => current.has(name)));
  }
  const valid = [...(candidates ?? [])].filter((name) => {
    const tags = new Set<string>();
    for (const schema of resolved) {
      const property = objectValue(objectValue(schema.properties, path, "an object")[name], path, "a schema object");
      if (typeof property.const !== "string" || tags.has(property.const)) return false;
      tags.add(property.const);
    }
    return true;
  });
  if (valid.length !== 1) fail(`${path}.oneOf must have exactly one unambiguous required string const discriminator.`);
}

function resolveSchema(schema: JsonObject, definitions: JsonObject): JsonObject {
  const seen = new Set<string>();
  while (has(schema, "$ref")) {
    const name = referenceNameValue(schema.$ref, "$ref", definitions);
    if (seen.has(name)) fail("An RPC schema contains a recursive $ref cycle.");
    seen.add(name);
    schema = objectValue(definitions[name], `$.$defs.${name}`, "a schema object");
  }
  return schema;
}

function validateEnumAndConst(schema: JsonObject, path: string, type: string): void {
  if (has(schema, "enum") && has(schema, "const")) fail(`${path} cannot declare both enum and const.`);
  if (has(schema, "enum")) {
    const values = arrayValue(schema.enum, `${path}.enum`);
    if (values.length < 1 || values.length > 256) fail(`${path}.enum must contain between 1 and 256 values.`);
    const seen = new Set<string>();
    for (const value of values) {
      validateLiteralType(value, type, `${path}.enum`);
      const key = canonicalizeDescriptor(value);
      if (seen.has(key)) fail(`${path}.enum contains a duplicate value.`);
      seen.add(key);
    }
  }
  if (has(schema, "const")) validateLiteralType(schema.const ?? null, type, `${path}.const`);
}

function validateLiteralType(value: JsonValue, type: string, path: string): void {
  const valid = type === "null" ? value === null
    : type === "integer" ? typeof value === "number" && Number.isInteger(value)
      : type === "number" ? typeof value === "number"
        : type === "string" || type === "boolean" ? typeof value === type
          : false;
  if (!valid) fail(`${path} contains a value incompatible with schema type '${type}'.`);
}

function validateAnnotations(schema: JsonObject, path: string): void {
  for (const name of ["title", "description"] as const) {
    if (has(schema, name) && (typeof schema[name] !== "string" || schema[name].length > 2048)) {
      fail(`${path}.${name} must be a string of at most 2048 characters.`);
    }
  }
}

function validateOrderedBounds(schema: JsonObject, path: string, minimumName: string, maximumName: string, maximumAllowed: number): void {
  const minimum = integerBound(required(schema, minimumName, path), `${path}.${minimumName}`, 0, maximumAllowed);
  const maximum = integerBound(required(schema, maximumName, path), `${path}.${maximumName}`, 0, maximumAllowed);
  if (minimum > maximum) fail(`${path}.${minimumName} must not exceed ${maximumName}.`);
}

function readSchemaReference(value: JsonValue, path: string, definitions: JsonObject): string {
  const reference = typeof value === "string"
    ? value
    : stringValue(objectWithOnly(value, path, "$ref"), "$ref", path, 256);
  referenceNameValue(reference, path, definitions);
  return reference;
}

function referenceNameValue(value: JsonValue | undefined, path: string, definitions: JsonObject): string {
  if (typeof value !== "string" || !value.startsWith("#/$defs/")) fail(`${path} must reference an existing local schema.`);
  const name = value.slice("#/$defs/".length);
  if (!isDefinitionName(name) || !has(definitions, name)) fail(`${path} must reference an existing local schema.`);
  return name;
}

function checkedDescriptorText(text: string): string {
  if (text.charCodeAt(0) === 0xfeff) fail("An RPC descriptor must be UTF-8 without a byte-order mark.");
  const length = new TextEncoder().encode(text).byteLength;
  if (length === 0 || length > MAXIMUM_DESCRIPTOR_BYTES) fail(`An RPC descriptor must be non-empty and no larger than ${MAXIMUM_DESCRIPTOR_BYTES} bytes.`);
  return text;
}

function decodeDescriptor(bytes: Uint8Array): string {
  if (bytes.byteLength === 0 || bytes.byteLength > MAXIMUM_DESCRIPTOR_BYTES) fail(`An RPC descriptor must be non-empty and no larger than ${MAXIMUM_DESCRIPTOR_BYTES} bytes.`);
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch (error) {
    throw new TypeError(`The RPC descriptor is not strict UTF-8: ${error instanceof Error ? error.message : String(error)}`);
  }
  return checkedDescriptorText(text);
}

class StrictJsonParser {
  readonly #text: string;
  #index = 0;
  #nodes = 0;

  public constructor(text: string) {
    this.#text = text;
  }

  public parse(): JsonValue {
    this.skipWhitespace();
    const value = this.parseValue(0);
    this.skipWhitespace();
    if (this.#index !== this.#text.length) fail(`Unexpected JSON token at character ${this.#index}.`);
    return value;
  }

  private parseValue(depth: number): JsonValue {
    if (depth > MAXIMUM_DEPTH || ++this.#nodes > 100_000) fail("The RPC descriptor exceeds its JSON depth or token limit.");
    const character = this.#text[this.#index];
    if (character === "{") return this.parseObject(depth + 1);
    if (character === "[") return this.parseArray(depth + 1);
    if (character === "\"") return this.parseString();
    if (character === "t" && this.consume("true")) return true;
    if (character === "f" && this.consume("false")) return false;
    if (character === "n" && this.consume("null")) return null;
    return this.parseNumber();
  }

  private parseObject(depth: number): JsonObject {
    this.#index++;
    this.skipWhitespace();
    const output: Record<string, JsonValue> = Object.create(null) as Record<string, JsonValue>;
    const insensitive = new Set<string>();
    if (this.#text[this.#index] === "}") {
      this.#index++;
      return output;
    }
    while (true) {
      if (this.#text[this.#index] !== "\"") fail(`Expected a JSON property name at character ${this.#index}.`);
      const name = this.parseString();
      const folded = name.toLocaleLowerCase("en-US");
      if (has(output, name)) fail(`Duplicate JSON property '${name}' is not allowed.`);
      if (insensitive.has(folded)) fail(`Case-colliding JSON property '${name}' is not allowed.`);
      insensitive.add(folded);
      this.skipWhitespace();
      this.expect(":");
      this.skipWhitespace();
      output[name] = this.parseValue(depth);
      this.skipWhitespace();
      const next = this.#text[this.#index++];
      if (next === "}") return output;
      if (next !== ",") fail(`Expected ',' or '}' at character ${this.#index - 1}.`);
      this.skipWhitespace();
    }
  }

  private parseArray(depth: number): readonly JsonValue[] {
    this.#index++;
    this.skipWhitespace();
    const output: JsonValue[] = [];
    if (this.#text[this.#index] === "]") {
      this.#index++;
      return output;
    }
    while (true) {
      output.push(this.parseValue(depth));
      this.skipWhitespace();
      const next = this.#text[this.#index++];
      if (next === "]") return output;
      if (next !== ",") fail(`Expected ',' or ']' at character ${this.#index - 1}.`);
      this.skipWhitespace();
    }
  }

  private parseString(): string {
    const start = this.#index++;
    while (this.#index < this.#text.length) {
      const character = this.#text[this.#index++];
      if (character === "\"") {
        try {
          return JSON.parse(this.#text.slice(start, this.#index)) as string;
        } catch (error) {
          throw new TypeError(`The RPC descriptor contains an invalid JSON string: ${error instanceof Error ? error.message : String(error)}`);
        }
      }
      if (character === "\\") {
        const escape = this.#text[this.#index++];
        if (escape === "u") {
          const hex = this.#text.slice(this.#index, this.#index + 4);
          if (!/^[0-9a-fA-F]{4}$/u.test(hex)) fail(`Invalid Unicode escape at character ${this.#index}.`);
          this.#index += 4;
        } else if (escape === undefined || !/^["\\/bfnrt]$/u.test(escape)) {
          fail(`Invalid JSON escape at character ${this.#index - 1}.`);
        }
      } else if (character !== undefined && character.charCodeAt(0) < 0x20) {
        fail(`Unescaped control character at character ${this.#index - 1}.`);
      }
    }
    fail("The RPC descriptor contains an unterminated JSON string.");
  }

  private parseNumber(): number {
    const match = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/u.exec(this.#text.slice(this.#index));
    if (match === null) fail(`Unexpected JSON token at character ${this.#index}.`);
    const token = match[0];
    this.#index += token.length;
    if (!/^-?(?:0|[1-9][0-9]*)$/u.test(token)) fail(`Descriptor number '${token}' must be a canonical IEEE-754 safe integer.`);
    const value = Number(token);
    if (!Number.isSafeInteger(value)) fail(`Descriptor number '${token}' must be a canonical IEEE-754 safe integer.`);
    return value;
  }

  private consume(value: string): boolean {
    if (!this.#text.startsWith(value, this.#index)) return false;
    this.#index += value.length;
    return true;
  }

  private expect(value: string): void {
    if (this.#text[this.#index++] !== value) fail(`Expected '${value}' at character ${this.#index - 1}.`);
  }

  private skipWhitespace(): void {
    while (this.#text[this.#index] === " "
      || this.#text[this.#index] === "\t"
      || this.#text[this.#index] === "\r"
      || this.#text[this.#index] === "\n") this.#index++;
  }
}

function required(object: JsonObject, name: string, path: string): JsonValue {
  if (!has(object, name)) fail(`${path} is missing required property '${name}'.`);
  return object[name] ?? null;
}

function stringValue(object: JsonObject, name: string, path: string, maximumLength: number): string {
  const value = required(object, name, path);
  if (typeof value !== "string" || value.length === 0 || value.length > maximumLength) {
    fail(`${path}.${name} must be a non-empty string of at most ${maximumLength} characters.`);
  }
  return value;
}

function objectValue(value: unknown, path: string, description: string): JsonObject {
  if (value === null || typeof value !== "object" || Array.isArray(value)) fail(`${path} must be ${description}.`);
  return value as JsonObject;
}

function objectWithOnly(value: JsonValue, path: string, ...properties: readonly string[]): JsonObject {
  const object = objectValue(value, path, "an object");
  only(object, path, ...properties);
  return object;
}

function arrayValue(value: JsonValue | undefined, path: string): readonly JsonValue[] {
  if (!Array.isArray(value)) fail(`${path} must be an array.`);
  return value;
}

function numberValue(value: JsonValue | undefined, path: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) fail(`${path} must be a safe integer.`);
  return value;
}

function integerBound(value: JsonValue | undefined, path: string, minimum: number, maximum: number): number {
  const number = numberValue(value, path);
  if (number < minimum || number > maximum) fail(`${path} must be an integer from ${minimum} through ${maximum}.`);
  return number;
}

function only(object: JsonObject, path: string, ...allowed: readonly string[]): void {
  const names = new Set(allowed);
  for (const name of Object.keys(object)) if (!names.has(name)) fail(`${path} contains unsupported property '${name}'.`);
}

function has(object: JsonObject, name: string): boolean {
  return Object.prototype.hasOwnProperty.call(object, name);
}

function isMemberId(value: string): boolean {
  return value.length <= 128 && /^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$/u.test(value);
}

function isDefinitionName(value: string): boolean {
  return value.length > 0 && value.length <= 128 && /^[A-Za-z0-9._-]+$/u.test(value);
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function fail(message: string): never {
  throw new TypeError(message);
}
