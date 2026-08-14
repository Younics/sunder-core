import type { JsonSchema, JsonValue, RpcContractDescriptor } from "./types";
import { isPackageId, isSemanticVersion } from "./validation";

const MAXIMUM_DESCRIPTOR_BYTES = 4 * 1024 * 1024;
const MAXIMUM_DEPTH = 64;
// A schema edge can add an intervening JSON container such as properties or
// oneOf. This derived bound is deliberately above every valid depth-64 schema.
const MAXIMUM_JSON_DEPTH = MAXIMUM_DEPTH * 4 + 16;
const MAXIMUM_SCHEMA_NODES = 4096;
const MAXIMUM_COLLECTION_ITEMS = 1_000_000;

type JsonObject = { readonly [key: string]: JsonValue };
type DescriptorVersion = 1 | 2;

export type RpcDescriptorErrorCode =
  | "descriptor-invalid"
  | "descriptor-json"
  | "descriptor-number"
  | "descriptor-representation"
  | "descriptor-version"
  | "domain-error-reference"
  | "method-shape"
  | "schema-profile"
  | "schema-reference-cycle"
  | "schema-reference-shape";

export class RpcDescriptorError extends TypeError {
  public readonly code: RpcDescriptorErrorCode;

  public constructor(code: RpcDescriptorErrorCode, message: string) {
    super(message);
    this.name = "RpcDescriptorError";
    this.code = code;
  }
}

export function parseRpcContractDescriptor(input: string | Uint8Array): RpcContractDescriptor {
  const text = typeof input === "string"
    ? checkedDescriptorText(input)
    : decodeDescriptor(input);
  const value = new StrictJsonParser(text).parse();
  validateRpcContractDescriptor(value);
  return value;
}

export function validateRpcContractDescriptor(value: unknown): asserts value is RpcContractDescriptor {
  validateJsonValue(value, "$", 0);
  const root = objectValue(value, "$", "an object");
  const descriptorVersion = required(root, "descriptorVersion", "$");
  if (descriptorVersion !== 1 && descriptorVersion !== 2) {
    fail("$.descriptorVersion must be 1 or 2.", "descriptor-version");
  }
  validateObjectNameCollisions(root, "$", descriptorVersion);
  if (descriptorVersion === 1) {
    if (has(root, "errors")) {
      fail("A descriptor using V2 root fields must set descriptorVersion to 2.", "descriptor-version");
    }
    only(root, "$", "$schema", "descriptorVersion", "contractId", "version", "services", "$defs");
  } else {
    only(root, "$", "$schema", "descriptorVersion", "contractId", "version", "errors", "services", "$defs");
  }
  if (has(root, "$schema") && root.$schema !== "https://json-schema.org/draft/2020-12/schema") {
    fail("$.$schema must be the JSON Schema Draft 2020-12 URI.");
  }
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

  const declaredErrors = descriptorVersion === 2
    ? validateErrors(required(root, "errors", "$"))
    : new Set<string>();
  validateServices(required(root, "services", "$"), definitions, descriptorVersion, declaredErrors);
  const schemaState: SchemaState = { definitions, nodes: 0, version: descriptorVersion };
  for (const name of definitionNames) {
    validateSchema(objectValue(definitions[name], `$.$defs.${name}`, "a schema object"), `$.$defs.${name}`, schemaState, 1);
  }
  validateReferenceDepths(definitionNames, definitions);
}

function validateJsonValue(
  value: unknown,
  path: string,
  depth: number,
): void {
  if (depth > MAXIMUM_JSON_DEPTH) fail("The RPC descriptor exceeds its derived JSON traversal depth limit.");
  if (value === null || typeof value === "boolean") return;
  if (typeof value === "string") {
    validateUnicodeScalars(value, path);
    return;
  }
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value) || Object.is(value, -0)) {
      fail(`${path} contains a number that is not a canonical IEEE-754 safe integer.`, "descriptor-number");
    }
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => validateJsonValue(item, `${path}[${index}]`, depth + 1));
    return;
  }
  if (typeof value !== "object") fail(`${path} contains a value that is not JSON.`);
  for (const [name, item] of Object.entries(value)) {
    validateUnicodeScalars(name, `${path} property name`);
    validateJsonValue(item, `${path}.${name}`, depth + 1);
  }
}

function validateObjectNameCollisions(value: JsonValue, path: string, version: DescriptorVersion): void {
  if (value === null || typeof value !== "object") return;
  if (Array.isArray(value)) {
    value.forEach((item, index) => validateObjectNameCollisions(item, `${path}[${index}]`, version));
    return;
  }
  const insensitive = new Set<string>();
  for (const [name, item] of Object.entries(value)) {
    const folded = version === 2 ? asciiCaseFold(name) : v1CaseFold(name);
    if (insensitive.has(folded)) fail(`${path} contains case-colliding property '${name}'.`, "descriptor-json");
    insensitive.add(folded);
    validateObjectNameCollisions(item, `${path}.${name}`, version);
  }
}

export function canonicalizeDescriptor(value: JsonValue): string {
  const write = (item: JsonValue): string => {
    if (item === null || typeof item === "boolean") return JSON.stringify(item);
    if (typeof item === "string") {
      validateUnicodeScalars(item, "A canonical JSON string");
      return JSON.stringify(item);
    }
    if (typeof item === "number") {
      if (!Number.isSafeInteger(item) || Object.is(item, -0)) {
        throw new TypeError("Descriptor numbers must be canonical IEEE-754 safe integers.");
      }
      return String(item);
    }
    if (Array.isArray(item)) return `[${item.map(write).join(",")}]`;
    const object = item as JsonObject;
    return `{${Object.keys(object).sort(ordinal).map((key) => `${JSON.stringify(key)}:${write(object[key] ?? null)}`).join(",")}}`;
  };
  return write(value);
}

function validateErrors(value: JsonValue): ReadonlySet<string> {
  if (Array.isArray(value)) fail("$.errors must be a keyed object.", "descriptor-representation");
  const errors = objectValue(value, "$.errors", "a keyed object");
  const codes = Object.keys(errors);
  if (codes.length > 256) fail("$.errors may contain at most 256 declarations.", "descriptor-representation");
  for (const code of codes) {
    if (!isMemberId(code)) fail(`$.errors contains invalid error code '${code}'.`, "descriptor-representation");
    const declaration = objectValue(errors[code], `$.errors.${code}`, "an object");
    only(declaration, `$.errors.${code}`, "description");
    if (has(declaration, "description")) {
      const description = declaration.description;
      if (typeof description !== "string" || scalarLength(description) > 2048) {
        fail(`$.errors.${code}.description must be a string of at most 2048 Unicode scalar values.`);
      }
    }
  }
  return new Set(codes);
}

function validateServices(
  value: JsonValue,
  definitions: JsonObject,
  version: DescriptorVersion,
  declaredErrors: ReadonlySet<string>,
): void {
  if (version === 2 && Array.isArray(value)) {
    fail("$.services must be a keyed object in descriptor version 2.", "descriptor-representation");
  }
  const entries: readonly [string | undefined, JsonValue][] = version === 1 && Array.isArray(value)
    ? value.map((service) => [undefined, service] as const)
    : Object.entries(objectValue(value, "$.services", version === 2 ? "a keyed object" : "an array or object"));
  if (entries.length < 1 || entries.length > 64) fail("$.services must contain between 1 and 64 services.");
  const serviceIds = new Set<string>();
  let methodCount = 0;
  for (const [key, serviceValue] of entries) {
    const path = key === undefined ? "$.services[]" : `$.services.${key}`;
    const service = objectValue(serviceValue, path, "an object");
    if (version === 1) only(service, path, "serviceId", "methods");
    else onlyWithCode(service, path, "descriptor-representation", "methods");
    const serviceId = key ?? stringValue(service, "serviceId", path, 128);
    if (has(service, "serviceId") && service.serviceId !== serviceId) fail(`${path}.serviceId must match its services object key.`);
    if (!isMemberId(serviceId)) fail(`${path}.serviceId is not a stable lowercase ASCII identifier.`);
    if (serviceIds.has(serviceId)) fail(`$.services declares serviceId '${serviceId}' more than once.`);
    serviceIds.add(serviceId);

    const methodsValue = required(service, "methods", path);
    if (version === 2 && Array.isArray(methodsValue)) {
      fail(`${path}.methods must be a keyed object in descriptor version 2.`, "descriptor-representation");
    }
    const methodEntries: readonly [string | undefined, JsonValue][] = version === 1 && Array.isArray(methodsValue)
      ? methodsValue.map((method) => [undefined, method] as const)
      : Object.entries(objectValue(methodsValue, `${path}.methods`, version === 2 ? "a keyed object" : "an array or object"));
    if (methodEntries.length === 0) fail(`${path}.methods must contain at least one method.`);
    methodCount += methodEntries.length;
    if (methodCount > 512) fail("An RPC descriptor may declare at most 512 methods.");
    const methodIds = new Set<string>();
    for (const [methodKey, methodValue] of methodEntries) {
      const methodPath = methodKey === undefined ? `${path}.methods[]` : `${path}.methods.${methodKey}`;
      const method = objectValue(methodValue, methodPath, "an object");
      if (version === 1) {
        only(method, methodPath, "methodId", "kind", "requestSchema", "responseSchema", "eventSchema");
      } else {
        onlyWithCode(
          method,
          methodPath,
          has(method, "responseSchema") && method.kind === "server-stream"
            || has(method, "eventSchema") && method.kind === "unary"
            ? "method-shape"
            : "descriptor-representation",
          "kind",
          "idempotency",
          "errors",
          "requestSchema",
          "responseSchema",
          "eventSchema",
        );
      }
      const methodId = methodKey ?? stringValue(method, "methodId", methodPath, 128);
      if (has(method, "methodId") && method.methodId !== methodId) fail(`${methodPath}.methodId must match its methods object key.`);
      if (!isMemberId(methodId)) fail(`${methodPath}.methodId is not a stable lowercase ASCII identifier.`);
      if (methodIds.has(methodId)) fail(`${path}.methods declares methodId '${methodId}' more than once.`);
      methodIds.add(methodId);
      const kind = stringValue(method, "kind", methodPath, 32);
      if (kind !== "unary" && kind !== "server-stream") fail(`${methodPath}.kind must be 'unary' or 'server-stream'.`);
      if (version === 2) {
        const idempotency = stringValue(method, "idempotency", methodPath, 16);
        if (idempotency !== "none" && idempotency !== "idempotent") {
          fail(`${methodPath}.idempotency must be 'none' or 'idempotent'.`, "method-shape");
        }
        const errorValues = arrayValue(required(method, "errors", methodPath), `${methodPath}.errors`);
        if (errorValues.length > 256) fail(`${methodPath}.errors may contain at most 256 codes.`, "domain-error-reference");
        const seenErrors = new Set<string>();
        for (const errorValue of errorValues) {
          if (typeof errorValue !== "string" || !isMemberId(errorValue) || seenErrors.has(errorValue)) {
            fail(`${methodPath}.errors must contain distinct valid error codes.`, "domain-error-reference");
          }
          if (!declaredErrors.has(errorValue)) {
            fail(`${methodPath}.errors references undeclared error '${errorValue}'.`, "domain-error-reference");
          }
          seenErrors.add(errorValue);
        }
      }
      readSchemaReference(
        required(method, "requestSchema", methodPath),
        `${methodPath}.requestSchema`,
        definitions,
        version === 2,
      );
      const output = kind === "unary" ? "responseSchema" : "eventSchema";
      const unexpected = kind === "unary" ? "eventSchema" : "responseSchema";
      if (has(method, unexpected)) {
        fail(`${methodPath}.${unexpected} is not valid for a ${kind} method.`, version === 2 ? "method-shape" : "descriptor-invalid");
      }
      readSchemaReference(
        required(method, output, methodPath),
        `${methodPath}.${output}`,
        definitions,
        version === 2,
      );
    }
  }
}

interface SchemaState {
  readonly definitions: JsonObject;
  readonly version: DescriptorVersion;
  nodes: number;
}

function validateSchema(
  schema: JsonObject,
  path: string,
  state: SchemaState,
  depth: number,
): void {
  if (depth > MAXIMUM_DEPTH || ++state.nodes > MAXIMUM_SCHEMA_NODES) fail(`${path} exceeds the RPC schema depth or node-count limit.`);
  validateAnnotations(schema, path);
  if (has(schema, "$ref")) {
    if (state.version === 2) onlyWithCode(schema, path, "schema-reference-shape", "$ref");
    else only(schema, path, "$ref", "title", "description");
    referenceNameValue(schema.$ref, path, state.definitions);
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
      depth + 1,
    ));
    validateUnion(variants, path, state.definitions);
    return;
  }

  if (has(schema, "logicalType")) {
    if (state.version !== 2) fail(`${path}.logicalType is outside descriptor version 1.`, "schema-profile");
    validateLogicalSchema(schema, path);
    return;
  }

  if (typeof schema.type !== "string" || schema.type.length === 0 || schema.type.length > 16) {
    fail(`${path}.type must be a supported schema type string.`, "schema-profile");
  }
  const type = schema.type;
  switch (type) {
    case "object":
      validateObjectSchema(schema, path, state, depth);
      break;
    case "array":
      if (state.version === 2) {
        onlyWithCode(schema, path, "schema-profile", "type", "items", "minItems", "maxItems", "title", "description");
      } else {
        only(schema, path, "type", "items", "minItems", "maxItems", "enum", "const", "title", "description");
      }
      validateOrderedBounds(schema, path, "minItems", "maxItems", MAXIMUM_COLLECTION_ITEMS);
      validateSchema(objectValue(required(schema, "items", path), `${path}.items`, "a schema object"), `${path}.items`, state, depth + 1);
      break;
    case "string":
      only(schema, path, "type", "minLength", "maxLength", "enum", "const", "title", "description");
      if (!has(schema, "minLength") || !has(schema, "maxLength")) {
        fail(`${path} must declare minLength and maxLength.`, state.version === 2 ? "schema-profile" : "descriptor-invalid");
      }
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
      fail(`${path}.type '${type}' is outside the RPC schema profile.`, "schema-profile");
  }
  validateEnumAndConst(schema, path, type);
}

function validateLogicalSchema(schema: JsonObject, path: string): void {
  const logicalType = stringValue(schema, "logicalType", path, 32);
  if (logicalType === "uuid" || logicalType === "utc-date-time") {
    onlyWithCode(schema, path, "schema-profile", "type", "logicalType", "title", "description");
    if (schema.type !== "string") fail(`${path}.type must be 'string' for logical type '${logicalType}'.`, "schema-profile");
    return;
  }
  if (logicalType === "content-reference") {
    onlyWithCode(schema, path, "schema-profile", "type", "logicalType", "title", "description");
    if (schema.type !== "object") fail(`${path}.type must be 'object' for logical type 'content-reference'.`, "schema-profile");
    return;
  }
  fail(`${path}.logicalType '${logicalType}' is outside the RPC schema profile.`, "schema-profile");
}

function validateObjectSchema(
  schema: JsonObject,
  path: string,
  state: SchemaState,
  depth: number,
): void {
  if (state.version === 2) {
    onlyWithCode(
      schema,
      path,
      "schema-profile",
      "type",
      "properties",
      "required",
      "additionalProperties",
      "propertyNames",
      "minProperties",
      "maxProperties",
      "title",
      "description",
    );
  } else {
    only(schema, path, "type", "properties", "required", "additionalProperties", "propertyNames", "minProperties", "maxProperties", "enum", "const", "title", "description");
  }
  const additional = required(schema, "additionalProperties", path);
  if (additional === false) {
    const properties = objectValue(required(schema, "properties", path), `${path}.properties`, "an object");
    const propertyNames = Object.keys(properties);
    if (propertyNames.length > 256) fail(`${path}.properties exceeds the 256-property limit.`);
    const requiredNames = arrayValue(required(schema, "required", path), `${path}.required`);
    const seen = new Set<string>();
    for (const name of requiredNames) {
      if (typeof name !== "string" || scalarLength(name) === 0 || scalarLength(name) > 128 || seen.has(name)) {
        fail(`${path}.required must contain unique property names of at most 128 Unicode scalar values.`);
      }
      if (!has(properties, name)) fail(`${path}.required references undeclared property '${name}'.`);
      seen.add(name);
    }
    for (const name of propertyNames) {
      if (scalarLength(name) === 0 || scalarLength(name) > 128) fail(`${path}.properties contains an empty or overlong property name.`);
      validateSchema(objectValue(properties[name], `${path}.properties.${name}`, "a schema object"), `${path}.properties.${name}`, state, depth + 1);
    }
    const minimum = has(schema, "minProperties") ? integerBound(schema.minProperties, `${path}.minProperties`, 0, 256) : 0;
    const maximum = has(schema, "maxProperties") ? integerBound(schema.maxProperties, `${path}.maxProperties`, 0, 256) : propertyNames.length;
    if (minimum > maximum || maximum > propertyNames.length) fail(`${path}'s object property bounds are inconsistent with its closed properties.`);
    if (has(schema, "propertyNames")) fail(`${path}.propertyNames is only supported for bounded map schemas.`);
  } else {
    if (additional === null || typeof additional !== "object" || Array.isArray(additional)) {
      fail(`${path}.additionalProperties must be false or a schema object.`, state.version === 2 ? "schema-profile" : "descriptor-invalid");
    }
    const additionalSchema = objectValue(additional, `${path}.additionalProperties`, "a schema object or false");
    if (has(schema, "properties") && Object.keys(objectValue(schema.properties, `${path}.properties`, "an object")).length !== 0) {
      fail(`${path} cannot mix named properties with map additionalProperties.`);
    }
    if (has(schema, "required") && arrayValue(schema.required, `${path}.required`).length !== 0) fail(`${path}.required must be absent or empty for a map schema.`);
    validateOrderedBounds(schema, path, "minProperties", "maxProperties", MAXIMUM_COLLECTION_ITEMS);
    const propertyNamesSchema = objectValue(required(schema, "propertyNames", path), `${path}.propertyNames`, "a schema object");
    validateSchema(propertyNamesSchema, `${path}.propertyNames`, state, depth + 1);
    if (propertyNamesSchema.type !== "string"
      || has(propertyNamesSchema, "logicalType")
      || !has(propertyNamesSchema, "minLength")
      || !has(propertyNamesSchema, "maxLength")) {
      fail(`${path}.propertyNames must be a bounded string schema.`, state.version === 2 ? "schema-profile" : "descriptor-invalid");
    }
    validateSchema(additionalSchema, `${path}.additionalProperties`, state, depth + 1);
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

function validateReferenceDepths(definitionNames: readonly string[], definitions: JsonObject): void {
  const memo = new Map<string, number>();
  for (const name of definitionNames) {
    const depth = definitionDepth(name, definitions, memo, new Set());
    if (depth > MAXIMUM_DEPTH) fail(`$.$defs.${name} exceeds the RPC schema depth limit.`, "schema-profile");
  }
}

function definitionDepth(
  name: string,
  definitions: JsonObject,
  memo: Map<string, number>,
  visiting: Set<string>,
): number {
  const known = memo.get(name);
  if (known !== undefined) return known;
  if (visiting.has(name)) fail(`$.$defs.${name} contains a recursive $ref cycle.`, "schema-reference-cycle");
  visiting.add(name);
  const depth = schemaDepth(
    objectValue(definitions[name], `$.$defs.${name}`, "a schema object"),
    definitions,
    memo,
    visiting,
  );
  visiting.delete(name);
  memo.set(name, depth);
  return depth;
}

function schemaDepth(
  schema: JsonObject,
  definitions: JsonObject,
  memo: Map<string, number>,
  visiting: Set<string>,
): number {
  if (has(schema, "$ref")) {
    return 1 + definitionDepth(referenceNameValue(schema.$ref, "$ref", definitions), definitions, memo, visiting);
  }
  const children: JsonObject[] = [];
  if (has(schema, "oneOf")) {
    for (const variant of arrayValue(schema.oneOf, "oneOf")) {
      children.push(objectValue(variant, "oneOf[]", "a schema object"));
    }
  } else if (!has(schema, "logicalType") && schema.type === "array") {
    children.push(objectValue(schema.items, "items", "a schema object"));
  } else if (!has(schema, "logicalType") && schema.type === "object") {
    if (schema.additionalProperties === false) {
      for (const child of Object.values(objectValue(schema.properties, "properties", "an object"))) {
        children.push(objectValue(child, "properties[]", "a schema object"));
      }
    } else {
      children.push(objectValue(schema.propertyNames, "propertyNames", "a schema object"));
      children.push(objectValue(schema.additionalProperties, "additionalProperties", "a schema object"));
    }
  }
  return 1 + Math.max(0, ...children.map((child) => schemaDepth(child, definitions, memo, visiting)));
}

function validateUnion(variants: readonly JsonValue[], path: string, definitions: JsonObject): void {
  const schemas = variants.map((variant, index) => objectValue(
    variant,
    `${path}.oneOf[${index}]`,
    "a schema object",
  ));
  const resolved = schemas.map((schema) => resolveSchema(schema, definitions));
  if (resolved.length === 2 && resolved.filter((schema) => schema.type === "null").length === 1) {
    const nullAcceptingBranches = schemas.filter((schema) => schemaAcceptsNull(schema, definitions, new Set())).length;
    if (nullAcceptingBranches !== 1) {
      fail(`${path}.oneOf nullable union must have exactly one branch accepting null.`, "schema-profile");
    }
    return;
  }
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

export function rpcSchemaAcceptsNull(
  schema: JsonSchema,
  definitions: Readonly<Record<string, JsonSchema>>,
): boolean {
  return schemaAcceptsNull(schema, definitions, new Set());
}

function schemaAcceptsNull(
  schema: JsonObject,
  definitions: JsonObject,
  references: Set<string>,
): boolean {
  if (has(schema, "$ref")) {
    const name = referenceNameValue(schema.$ref, "$ref", definitions);
    if (references.has(name)) fail("An RPC schema contains a recursive $ref cycle.", "schema-reference-cycle");
    references.add(name);
    const acceptsNull = schemaAcceptsNull(
      objectValue(definitions[name], `$.$defs.${name}`, "a schema object"),
      definitions,
      references,
    );
    references.delete(name);
    return acceptsNull;
  }
  if (has(schema, "oneOf")) {
    let nullAcceptingBranches = 0;
    for (const variant of arrayValue(schema.oneOf, "oneOf")) {
      if (schemaAcceptsNull(objectValue(variant, "oneOf[]", "a schema object"), definitions, references)) {
        nullAcceptingBranches++;
      }
    }
    return nullAcceptingBranches === 1;
  }
  return schema.type === "null";
}

function resolveSchema(schema: JsonObject, definitions: JsonObject): JsonObject {
  const seen = new Set<string>();
  while (has(schema, "$ref")) {
    const name = referenceNameValue(schema.$ref, "$ref", definitions);
    if (seen.has(name)) fail("An RPC schema contains a recursive $ref cycle.", "schema-reference-cycle");
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
    if (has(schema, name) && (typeof schema[name] !== "string" || scalarLength(schema[name]) > 2048)) {
      fail(`${path}.${name} must be a string of at most 2048 Unicode scalar values.`);
    }
  }
}

function validateOrderedBounds(schema: JsonObject, path: string, minimumName: string, maximumName: string, maximumAllowed: number): void {
  const minimum = integerBound(required(schema, minimumName, path), `${path}.${minimumName}`, 0, maximumAllowed);
  const maximum = integerBound(required(schema, maximumName, path), `${path}.${maximumName}`, 0, maximumAllowed);
  if (minimum > maximum) fail(`${path}.${minimumName} must not exceed ${maximumName}.`);
}

function readSchemaReference(value: JsonValue, path: string, definitions: JsonObject, closedObjectOnly: boolean): string {
  if (closedObjectOnly && (value === null || typeof value !== "object" || Array.isArray(value))) {
    fail(`${path} must be the closed object { "$ref": "#/$defs/<name>" }.`, "schema-reference-shape");
  }
  let reference: string;
  if (typeof value === "string") {
    reference = value;
  } else {
    const object = objectValue(value, path, "an object");
    if (closedObjectOnly) onlyWithCode(object, path, "schema-reference-shape", "$ref");
    else only(object, path, "$ref");
    reference = stringValue(object, "$ref", path, 256);
  }
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
  if (text.charCodeAt(0) === 0xfeff) fail("An RPC descriptor must be UTF-8 without a byte-order mark.", "descriptor-json");
  const length = new TextEncoder().encode(text).byteLength;
  if (length === 0 || length > MAXIMUM_DESCRIPTOR_BYTES) fail(`An RPC descriptor must be non-empty and no larger than ${MAXIMUM_DESCRIPTOR_BYTES} bytes.`);
  return text;
}

function decodeDescriptor(bytes: Uint8Array): string {
  if (bytes.byteLength === 0 || bytes.byteLength > MAXIMUM_DESCRIPTOR_BYTES) fail(`An RPC descriptor must be non-empty and no larger than ${MAXIMUM_DESCRIPTOR_BYTES} bytes.`);
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    fail("An RPC descriptor must be UTF-8 without a byte-order mark.", "descriptor-json");
  }
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch (error) {
    fail(`The RPC descriptor is not strict UTF-8: ${error instanceof Error ? error.message : String(error)}`, "descriptor-json");
  }
  return checkedDescriptorText(text);
}

class StrictJsonParser {
  readonly #text: string;
  #index = 0;

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
    if (depth > MAXIMUM_JSON_DEPTH) fail("The RPC descriptor exceeds its derived JSON traversal depth limit.");
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
    if (this.#text[this.#index] === "}") {
      this.#index++;
      return output;
    }
    while (true) {
      if (this.#text[this.#index] !== "\"") fail(`Expected a JSON property name at character ${this.#index}.`);
      const name = this.parseString();
      if (has(output, name)) fail(`Duplicate JSON property '${name}' is not allowed.`, "descriptor-json");
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
        let value: string;
        try {
          value = JSON.parse(this.#text.slice(start, this.#index)) as string;
        } catch (error) {
          fail(`The RPC descriptor contains an invalid JSON string: ${error instanceof Error ? error.message : String(error)}`, "descriptor-json");
        }
        validateUnicodeScalars(value, "A JSON string");
        return value;
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
    if (!/^-?(?:0|[1-9][0-9]*)$/u.test(token) || token === "-0") {
      fail(`Descriptor number '${token}' must be a canonical IEEE-754 safe integer.`, "descriptor-number");
    }
    const value = Number(token);
    if (!Number.isSafeInteger(value)) {
      fail(`Descriptor number '${token}' must be a canonical IEEE-754 safe integer.`, "descriptor-number");
    }
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

function onlyWithCode(
  object: JsonObject,
  path: string,
  code: RpcDescriptorErrorCode,
  ...allowed: readonly string[]
): void {
  const names = new Set(allowed);
  for (const name of Object.keys(object)) {
    if (!names.has(name)) fail(`${path} contains unsupported property '${name}'.`, code);
  }
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

function asciiCaseFold(value: string): string {
  return value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 0x20));
}

function v1CaseFold(value: string): string {
  return value.toLocaleLowerCase("en-US");
}

function scalarLength(value: string): number {
  validateUnicodeScalars(value, "A JSON string");
  return [...value].length;
}

function validateUnicodeScalars(value: string, path: string): void {
  for (let index = 0; index < value.length; index++) {
    const current = value.charCodeAt(index);
    if (current >= 0xd800 && current <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) fail(`${path} contains an unpaired UTF-16 surrogate.`, "descriptor-json");
      index++;
    } else if (current >= 0xdc00 && current <= 0xdfff) {
      fail(`${path} contains an unpaired UTF-16 surrogate.`, "descriptor-json");
    }
  }
}

function fail(message: string, code: RpcDescriptorErrorCode = "descriptor-invalid"): never {
  throw new RpcDescriptorError(code, message);
}
