import type { JsonSchema, JsonValue, RpcContractDescriptor } from "./types";
import { isPackageId, isSemanticVersion } from "./validation";

const MAXIMUM_DESCRIPTOR_BYTES = 4 * 1024 * 1024;
const MAXIMUM_DEPTH = 64;
// A schema edge can add an intervening JSON container such as properties or
// oneOf. This derived bound is deliberately above every valid depth-64 schema.
const MAXIMUM_JSON_DEPTH = MAXIMUM_DEPTH * 4 + 16;
const MAXIMUM_SCHEMA_NODES = 4096;
const MAXIMUM_COLLECTION_ITEMS = 1_000_000;

export const SUNDER_RPC_VALUE_SCHEMA_PROFILE = "sunder.rpc.values.v1" as const;

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
  const parser = new StrictJsonParser(text);
  const value = parser.parse();
  parser.validateNumberTokens(descriptorVersionOf(value));
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
  if (descriptorVersion === 1) validateV1DescriptorNumbers(value as JsonValue, "$", 0);
  validateObjectNameCollisions(root, "$", descriptorVersion);
  if (descriptorVersion === 1) {
    if (has(root, "errors") || has(root, "schemaProfile")) {
      fail("A descriptor using V2 root fields must set descriptorVersion to 2.", "descriptor-version");
    }
    only(root, "$", "$schema", "descriptorVersion", "contractId", "version", "services", "$defs");
  } else {
    only(root, "$", "$schema", "descriptorVersion", "schemaProfile", "contractId", "version", "errors", "services", "$defs");
    if (!has(root, "schemaProfile") || root.schemaProfile !== SUNDER_RPC_VALUE_SCHEMA_PROFILE) {
      fail(`$.schemaProfile must be exactly '${SUNDER_RPC_VALUE_SCHEMA_PROFILE}'.`, "schema-profile");
    }
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
    ? validateErrors(required(root, "errors", "$"), definitions)
    : new Set<string>();
  validateServices(required(root, "services", "$"), definitions, descriptorVersion, declaredErrors);
  const schemaState: SchemaState = { definitions, nodes: 0, version: descriptorVersion };
  for (const name of definitionNames) {
    validateSchema(objectValue(definitions[name], `$.$defs.${name}`, "a schema object"), `$.$defs.${name}`, schemaState, 1);
  }
  if (descriptorVersion === 2) validateProductiveReferences(definitionNames, definitions);
  else validateReferenceDepths(definitionNames, definitions);
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
    if (!Number.isFinite(value)) fail(`${path} contains a number that is not finite.`, "descriptor-number");
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

function descriptorVersionOf(value: JsonValue): DescriptorVersion | undefined {
  if (!isObjectValue(value)) return undefined;
  const version = value.descriptorVersion;
  return version === 1 || version === 2
    ? version
    : undefined;
}

function validateV1DescriptorNumbers(value: JsonValue, path: string, depth: number): void {
  if (depth > MAXIMUM_JSON_DEPTH) fail("The RPC descriptor exceeds its derived JSON traversal depth limit.");
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value) || Object.is(value, -0)) {
      fail(`${path} contains a number that is not a canonical IEEE-754 safe integer.`, "descriptor-number");
    }
    return;
  }
  if (value === null || typeof value !== "object") return;
  if (Array.isArray(value)) {
    value.forEach((item, index) => validateV1DescriptorNumbers(item, `${path}[${index}]`, depth + 1));
    return;
  }
  for (const [name, item] of Object.entries(value)) {
    validateV1DescriptorNumbers(item, `${path}.${name}`, depth + 1);
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
      if (!Number.isFinite(item)) throw new TypeError("Descriptor numbers must be finite IEEE-754 binary64 values.");
      return JSON.stringify(item);
    }
    if (Array.isArray(item)) return `[${item.map(write).join(",")}]`;
    const object = item as JsonObject;
    return `{${Object.keys(object).sort(ordinal).map((key) => `${JSON.stringify(key)}:${write(object[key] ?? null)}`).join(",")}}`;
  };
  return write(value);
}

function validateErrors(value: JsonValue, definitions: JsonObject): ReadonlySet<string> {
  if (Array.isArray(value)) fail("$.errors must be a keyed object.", "descriptor-representation");
  const errors = objectValue(value, "$.errors", "a keyed object");
  const codes = Object.keys(errors);
  if (codes.length > 256) fail("$.errors may contain at most 256 declarations.", "descriptor-representation");
  for (const code of codes) {
    if (!isMemberId(code)) fail(`$.errors contains invalid error code '${code}'.`, "descriptor-representation");
    const declaration = objectValue(errors[code], `$.errors.${code}`, "an object");
    onlyWithCode(declaration, `$.errors.${code}`, "descriptor-representation", "description", "detailsSchema");
    if (has(declaration, "description")) {
      const description = declaration.description;
      if (typeof description !== "string" || scalarLength(description) > 2048) {
        fail(`$.errors.${code}.description must be a string of at most 2048 Unicode scalar values.`);
      }
    }
    if (has(declaration, "detailsSchema")) {
      readSchemaReference(
        declaration.detailsSchema ?? null,
        `$.errors.${code}.detailsSchema`,
        definitions,
        true,
      );
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
  if (state.version === 2) {
    validateValueProfileSchema(schema, path, state, depth);
    return;
  }
  if (has(schema, "$ref")) {
    only(schema, path, "$ref", "title", "description");
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
    fail(`${path}.logicalType is outside descriptor version 1.`, "schema-profile");
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
      only(schema, path, "type", "items", "minItems", "maxItems", "enum", "const", "title", "description");
      validateOrderedBounds(schema, path, "minItems", "maxItems", MAXIMUM_COLLECTION_ITEMS);
      validateSchema(objectValue(required(schema, "items", path), `${path}.items`, "a schema object"), `${path}.items`, state, depth + 1);
      break;
    case "string":
      only(schema, path, "type", "minLength", "maxLength", "enum", "const", "title", "description");
      if (!has(schema, "minLength") || !has(schema, "maxLength")) {
        fail(`${path} must declare minLength and maxLength.`, "descriptor-invalid");
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

function validateValueProfileSchema(
  schema: JsonObject,
  path: string,
  state: SchemaState,
  depth: number,
): void {
  if (has(schema, "$ref")) {
    onlyWithCode(schema, path, "schema-reference-shape", "$ref");
    referenceNameValue(schema.$ref, path, state.definitions, "schema-reference-shape");
    return;
  }
  if (has(schema, "oneOf")) {
    onlyWithCode(schema, path, "schema-profile", "oneOf", "title", "description");
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

  const structuralNames = Object.keys(schema).filter((name) => name !== "title" && name !== "description");
  if (structuralNames.length === 0) return;

  if (has(schema, "logicalType")) {
    validateLogicalSchema(schema, path);
    return;
  }
  if (typeof schema.type !== "string" || schema.type.length === 0 || schema.type.length > 16) {
    fail(`${path}.type must be a supported schema type string.`, "schema-profile");
  }

  switch (schema.type) {
    case "object":
      if (structuralNames.every((name) => name === "type" || name === "maxProperties")) {
        onlyWithCode(schema, path, "schema-profile", "type", "maxProperties", "title", "description");
        if (has(schema, "maxProperties")) {
          integerBound(schema.maxProperties, `${path}.maxProperties`, 0, MAXIMUM_COLLECTION_ITEMS);
        }
      } else {
        validateObjectSchema(schema, path, state, depth);
      }
      break;
    case "array":
      onlyWithCode(schema, path, "schema-profile", "type", "items", "minItems", "maxItems", "title", "description");
      validateOrderedBounds(schema, path, "minItems", "maxItems", MAXIMUM_COLLECTION_ITEMS);
      validateSchema(
        objectValue(required(schema, "items", path), `${path}.items`, "a schema object"),
        `${path}.items`,
        state,
        depth + 1,
      );
      break;
    case "string":
      validateValueProfileStringSchema(schema, path);
      break;
    case "integer":
      validateValueProfileNumberSchema(schema, path, false);
      break;
    case "number":
      validateValueProfileNumberSchema(schema, path, true);
      break;
    case "boolean":
    case "null":
      onlyWithCode(schema, path, "schema-profile", "type", "enum", "const", "title", "description");
      validateEnumAndConst(schema, path, schema.type);
      break;
    default:
      fail(`${path}.type '${schema.type}' is outside the RPC schema profile.`, "schema-profile");
  }
}

function validateLogicalSchema(schema: JsonObject, path: string): void {
  const logicalType = stringValue(schema, "logicalType", path, 32);
  if (logicalType === "content-reference") {
    onlyWithCode(schema, path, "schema-profile", "type", "logicalType", "title", "description");
    if (schema.type !== "object") fail(`${path}.type must be 'object' for logical type 'content-reference'.`, "schema-profile");
    return;
  }
  fail(`${path}.logicalType '${logicalType}' is outside the RPC schema profile.`, "schema-profile");
}

function validateValueProfileStringSchema(schema: JsonObject, path: string): void {
  if (has(schema, "contentEncoding")) {
    onlyWithCode(
      schema,
      path,
      "schema-profile",
      "type",
      "contentEncoding",
      "maxDecodedBytes",
      "title",
      "description",
    );
    if (schema.contentEncoding !== "base64") fail(`${path}.contentEncoding must be exactly 'base64'.`, "schema-profile");
    if (!has(schema, "maxDecodedBytes")) fail(`${path}.maxDecodedBytes is required.`, "schema-profile");
    integerBound(schema.maxDecodedBytes, `${path}.maxDecodedBytes`, 0, MAXIMUM_COLLECTION_ITEMS);
    return;
  }

  if (has(schema, "format")) {
    const format = stringValue(schema, "format", path, 32);
    if (["uuid", "date", "date-time", "duration", "uri"].includes(format)) {
      onlyWithCode(schema, path, "schema-profile", "type", "format", "title", "description");
      return;
    }
    if (format === "big-integer") {
      onlyWithCode(
        schema,
        path,
        "schema-profile",
        "type",
        "format",
        "maxDigits",
        "valueMinimum",
        "valueExclusiveMinimum",
        "valueMaximum",
        "valueExclusiveMaximum",
        "title",
        "description",
      );
      const maxDigits = integerBound(required(schema, "maxDigits", path), `${path}.maxDigits`, 1, MAXIMUM_COLLECTION_ITEMS);
      validateExactNumericValueBounds(schema, path, maxDigits, undefined);
      return;
    }
    if (format === "decimal") {
      onlyWithCode(
        schema,
        path,
        "schema-profile",
        "type",
        "format",
        "maxPrecision",
        "maxScale",
        "valueMinimum",
        "valueExclusiveMinimum",
        "valueMaximum",
        "valueExclusiveMaximum",
        "title",
        "description",
      );
      const maxPrecision = integerBound(required(schema, "maxPrecision", path), `${path}.maxPrecision`, 1, MAXIMUM_COLLECTION_ITEMS);
      const maxScale = integerBound(required(schema, "maxScale", path), `${path}.maxScale`, 0, MAXIMUM_COLLECTION_ITEMS);
      validateExactNumericValueBounds(schema, path, maxPrecision, maxScale);
      return;
    }
    fail(`${path}.format '${format}' is outside the RPC schema profile.`, "schema-profile");
  }

  onlyWithCode(schema, path, "schema-profile", "type", "minLength", "maxLength", "enum", "const", "title", "description");
  if (!has(schema, "minLength") || !has(schema, "maxLength")) {
    fail(`${path} must declare minLength and maxLength.`, "schema-profile");
  }
  validateOrderedBounds(schema, path, "minLength", "maxLength", MAXIMUM_COLLECTION_ITEMS);
  validateEnumAndConst(schema, path, "string");
}

function validateValueProfileNumberSchema(schema: JsonObject, path: string, requiresFormat: boolean): void {
  onlyWithCode(
    schema,
    path,
    "schema-profile",
    "type",
    "format",
    "minimum",
    "maximum",
    "exclusiveMinimum",
    "exclusiveMaximum",
    "enum",
    "const",
    "title",
    "description",
  );
  let format: "float32" | "float64" | undefined;
  if (requiresFormat) {
    if (schema.format !== "float32" && schema.format !== "float64") {
      fail(`${path}.format must be exactly 'float32' or 'float64'.`, "schema-profile");
    }
    format = schema.format;
  } else if (has(schema, "format")) {
    fail(`${path}.format is not supported for safe integers.`, "schema-profile");
  }

  const lowerNames = ["minimum", "exclusiveMinimum"].filter((name) => has(schema, name));
  const upperNames = ["maximum", "exclusiveMaximum"].filter((name) => has(schema, name));
  if (lowerNames.length > 1 || upperNames.length > 1) fail(`${path} cannot mix inclusive and exclusive bounds on one side.`);
  const readNumber = format === undefined
    ? (value: JsonValue | undefined, valuePath: string): number => numberValue(value, valuePath, "descriptor-number")
    : (value: JsonValue | undefined, valuePath: string): number => formattedNumberValue(value, valuePath, format);
  const lower = lowerNames.length === 0 ? undefined : readNumber(schema[lowerNames[0] ?? ""], `${path}.${lowerNames[0] ?? "minimum"}`);
  const upper = upperNames.length === 0 ? undefined : readNumber(schema[upperNames[0] ?? ""], `${path}.${upperNames[0] ?? "maximum"}`);
  if (lower !== undefined && upper !== undefined && lower > upper) fail(`${path}'s lower numeric bound must not exceed its upper bound.`);
  validateEnumAndConst(schema, path, requiresFormat ? "number" : "integer");
}

function validateExactNumericValueBounds(
  schema: JsonObject,
  path: string,
  maximumDigitsOrPrecision: number,
  maximumScale: number | undefined,
): void {
  const lowerNames = ["valueMinimum", "valueExclusiveMinimum"].filter((name) => has(schema, name));
  const upperNames = ["valueMaximum", "valueExclusiveMaximum"].filter((name) => has(schema, name));
  if (lowerNames.length > 1 || upperNames.length > 1) fail(`${path} cannot mix inclusive and exclusive value bounds on one side.`, "schema-profile");

  const parsed = new Map<string, ExactDecimal>();
  for (const name of [...lowerNames, ...upperNames]) {
    const value = schema[name];
    if (typeof value !== "string") fail(`${path}.${name} must be a canonical numeric string.`, "schema-profile");
    const number = maximumScale === undefined ? parseCanonicalBigInteger(value) : parseCanonicalDecimal(value);
    if (number === undefined) fail(`${path}.${name} must be a canonical numeric string.`, "schema-profile");
    if (number.precision > maximumDigitsOrPrecision || maximumScale !== undefined && number.scale > maximumScale) {
      fail(`${path}.${name} exceeds the schema's declared digit limits.`, "schema-profile");
    }
    parsed.set(name, number);
  }
  const lower = lowerNames[0] === undefined ? undefined : parsed.get(lowerNames[0]);
  const upper = upperNames[0] === undefined ? undefined : parsed.get(upperNames[0]);
  if (lower !== undefined && upper !== undefined && compareExactDecimals(lower, upper) > 0) {
    fail(`${path}'s lower value bound must not exceed its upper bound.`, "schema-profile");
  }
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

function validateProductiveReferences(definitionNames: readonly string[], definitions: JsonObject): void {
  const edges = new Map<string, Set<string>>();
  for (const name of definitionNames) {
    edges.set(name, new Set());
  }
  for (const name of definitionNames) {
    collectRequiredReferenceEdges(
      objectValue(definitions[name], `$.$defs.${name}`, "a schema object"),
      name,
      definitions,
      edges,
      false,
    );
  }

  const completed = new Set<string>();
  const visiting = new Set<string>();
  const visit = (name: string): void => {
    if (completed.has(name)) return;
    if (visiting.has(name)) {
      fail(`$.$defs.${name} contains a non-productive local reference cycle.`, "schema-reference-cycle");
    }
    visiting.add(name);
    for (const target of edges.get(name) ?? []) visit(target);
    visiting.delete(name);
    completed.add(name);
  };
  for (const name of definitionNames) visit(name);

  for (const name of [...definitionNames].sort(ordinal)) {
    const height = valueProfileSchemaHeight(
      objectValue(definitions[name], `$.$defs.${name}`, "a schema object"),
      definitions,
      new Set([name]),
    );
    if (height > MAXIMUM_DEPTH) fail(`$.$defs.${name} exceeds the resolved RPC schema depth limit.`, "schema-profile");
  }
}

function collectRequiredReferenceEdges(
  schema: JsonObject,
  source: string,
  definitions: JsonObject,
  edges: ReadonlyMap<string, Set<string>>,
  hasProductiveBoundary: boolean,
): void {
  if (has(schema, "$ref")) {
    if (!hasProductiveBoundary) {
      edges.get(source)?.add(referenceNameValue(schema.$ref, "$ref", definitions));
    }
    return;
  }
  if (has(schema, "oneOf")) {
    for (const variant of arrayValue(schema.oneOf, "oneOf")) {
      collectRequiredReferenceEdges(
        objectValue(variant, "oneOf[]", "a schema object"),
        source,
        definitions,
        edges,
        hasProductiveBoundary,
      );
    }
  }
  if (isObjectValue(schema.properties)) {
    const required = new Set(asStringValues(schema.required));
    for (const [name, property] of Object.entries(schema.properties)) {
      collectRequiredReferenceEdges(
        objectValue(property, "properties[]", "a schema object"),
        source,
        definitions,
        edges,
        hasProductiveBoundary || !required.has(name),
      );
    }
  }
  if (isObjectValue(schema.items)) {
    collectRequiredReferenceEdges(
      schema.items,
      source,
      definitions,
      edges,
      hasProductiveBoundary || schema.minItems === 0,
    );
  }
  if (isObjectValue(schema.additionalProperties)) {
    collectRequiredReferenceEdges(
      schema.additionalProperties,
      source,
      definitions,
      edges,
      hasProductiveBoundary || schema.minProperties === 0,
    );
  }
}

function valueProfileSchemaHeight(
  schema: JsonObject,
  definitions: JsonObject,
  references: Set<string>,
): number {
  if (has(schema, "$ref")) {
    const name = referenceNameValue(schema.$ref, "$ref", definitions);
    if (references.has(name)) return 1;
    references.add(name);
    const height = 1 + valueProfileSchemaHeight(
      objectValue(definitions[name], `$.$defs.${name}`, "a schema object"),
      definitions,
      references,
    );
    references.delete(name);
    return height;
  }
  const children: JsonObject[] = [];
  if (Array.isArray(schema.oneOf)) children.push(...schema.oneOf.map((value) => objectValue(value, "oneOf[]", "a schema object")));
  if (isObjectValue(schema.properties)) children.push(...Object.values(schema.properties).map((value) => objectValue(value, "properties[]", "a schema object")));
  if (isObjectValue(schema.items)) children.push(schema.items);
  if (isObjectValue(schema.propertyNames)) children.push(schema.propertyNames);
  if (isObjectValue(schema.additionalProperties)) children.push(schema.additionalProperties);
  return 1 + Math.max(0, ...children.map((child) => valueProfileSchemaHeight(child, definitions, references)));
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
  return schema.type === "null"
    || Object.keys(schema).every((name) => name === "title" || name === "description");
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
      validateLiteralConstraints(schema, value, type, `${path}.enum`);
      const key = primitiveLiteralKey(schema, value);
      if (seen.has(key)) fail(`${path}.enum contains a duplicate value.`);
      seen.add(key);
    }
  }
  if (has(schema, "const")) {
    const value = schema.const ?? null;
    validateLiteralType(value, type, `${path}.const`);
    validateLiteralConstraints(schema, value, type, `${path}.const`);
  }
}

function validateLiteralType(value: JsonValue, type: string, path: string): void {
  const valid = type === "null" ? value === null
    : type === "integer" ? typeof value === "number" && Number.isSafeInteger(value)
      : type === "number" ? typeof value === "number" && Number.isFinite(value)
        : type === "string" || type === "boolean" ? typeof value === type
          : false;
  if (!valid) fail(`${path} contains a value incompatible with schema type '${type}'.`);
}

function validateLiteralConstraints(schema: JsonObject, value: JsonValue, type: string, path: string): void {
  if (type === "string" && typeof value === "string") {
    const length = scalarLength(value);
    if (typeof schema.minLength === "number" && length < schema.minLength
      || typeof schema.maxLength === "number" && length > schema.maxLength) {
      fail(`${path} is outside the schema's string length bounds.`);
    }
    return;
  }
  if ((type !== "integer" && type !== "number") || typeof value !== "number") return;
  const format = schema.format === "float32" || schema.format === "float64" ? schema.format : undefined;
  const number = format === undefined ? value : formattedNumberValue(value, path, format);
  const bound = (name: string): number | undefined => typeof schema[name] !== "number"
    ? undefined
    : format === undefined
      ? schema[name] as number
      : formattedNumberValue(schema[name], `${path}.${name}`, format);
  const minimum = bound("minimum");
  const exclusiveMinimum = bound("exclusiveMinimum");
  const maximum = bound("maximum");
  const exclusiveMaximum = bound("exclusiveMaximum");
  if (minimum !== undefined && number < minimum
    || exclusiveMinimum !== undefined && number <= exclusiveMinimum
    || maximum !== undefined && number > maximum
    || exclusiveMaximum !== undefined && number >= exclusiveMaximum) {
    fail(`${path} is outside the schema's numeric bounds.`);
  }
}

function primitiveLiteralKey(schema: JsonObject, value: JsonValue): string {
  if (typeof value !== "number") return canonicalizeDescriptor(value);
  const number = schema.format === "float32" ? Math.fround(value) : value;
  return `number:${number === 0 ? 0 : number}`;
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
  referenceNameValue(reference, path, definitions, closedObjectOnly ? "schema-reference-shape" : "descriptor-invalid");
  return reference;
}

function referenceNameValue(
  value: JsonValue | undefined,
  path: string,
  definitions: JsonObject,
  errorCode: RpcDescriptorErrorCode = "descriptor-invalid",
): string {
  if (typeof value !== "string" || !value.startsWith("#/$defs/")) fail(`${path} must reference an existing local schema.`, errorCode);
  const name = value.slice("#/$defs/".length);
  if (!isDefinitionName(name) || !has(definitions, name)) fail(`${path} must reference an existing local schema.`, errorCode);
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
  readonly #numberTokens: string[] = [];
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

  public validateNumberTokens(version: DescriptorVersion | undefined): void {
    if (version !== 1) return;
    for (const token of this.#numberTokens) {
      const value = Number(token);
      if (!/^-?(?:0|[1-9][0-9]*)$/u.test(token)
        || token === "-0"
        || !Number.isSafeInteger(value)) {
        fail(`Descriptor number '${token}' must be a canonical IEEE-754 safe integer.`, "descriptor-number");
      }
    }
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
    const value = Number(token);
    if (!Number.isFinite(value)) fail(`Descriptor number '${token}' must be finite IEEE-754 binary64.`, "descriptor-number");
    this.#numberTokens.push(token);
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

function isObjectValue(value: JsonValue | undefined): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function asStringValues(value: JsonValue | undefined): readonly string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
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

function numberValue(
  value: JsonValue | undefined,
  path: string,
  errorCode: RpcDescriptorErrorCode = "descriptor-invalid",
): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) fail(`${path} must be a safe integer.`, errorCode);
  return value;
}

function formattedNumberValue(
  value: JsonValue | undefined,
  path: string,
  format: "float32" | "float64",
): number {
  if (typeof value !== "number" || !Number.isFinite(value)) fail(`${path} must be a finite ${format} value.`, "descriptor-number");
  const formatted = format === "float32" ? Math.fround(value) : value;
  if (!Number.isFinite(formatted)) fail(`${path} must be a finite ${format} value.`, "descriptor-number");
  return formatted;
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
