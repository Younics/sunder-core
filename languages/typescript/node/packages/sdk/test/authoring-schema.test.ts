import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, readdir } from "node:fs/promises";
import { basename, dirname, relative, resolve } from "node:path";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020";
import type { ValidateFunction } from "ajv";
import {
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  isPackageVersionRange,
  isSemanticVersion,
  isVersionInRange,
  parseRpcContractDescriptor,
} from "../src/index";
import type { RpcContractDescriptor } from "../src/types";

type JsonValue = null | boolean | number | string | JsonValue[] | JsonObject;
type JsonObject = { [key: string]: JsonValue };

interface FixtureExpectation {
  readonly schemaPath: string;
  readonly fixturePath?: string;
  readonly recipe?: JsonObject;
  readonly schemaValid: boolean;
  readonly semanticValid: boolean;
  readonly expectedSchemaKeyword?: string;
  readonly expectedSchemaErrors?: readonly string[];
  readonly expectedSemanticDiagnosticId: string | null;
  readonly semanticContextFixturePaths?: readonly string[];
  readonly semanticDescriptorContexts?: readonly DescriptorContextExpectation[];
  readonly expectedFileSha256?: string;
  readonly expectedDerivedSha256?: string;
  readonly expectedCanonicalContentSha256?: string;
}

interface DescriptorContextExpectation {
  readonly fixturePath: string;
  readonly contextPath: string;
}

interface ImmutableArtifactEvidence {
  readonly inventoryFixturePath: string;
  readonly packageId: string;
  readonly packageVersion: string;
  readonly packageContentIdentity: string;
  readonly publicContractCount: number;
  readonly publicInventorySha256: string;
}

interface CanonicalSourceExpectation {
  readonly descriptorPath?: string;
  readonly expectationsPath?: string;
  readonly derivedFrom?: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorBytes: number;
  readonly descriptorSha256: string;
  readonly abiProjectionBytes: number;
  readonly abiSha256: string;
}

interface AuthoringExpectations {
  readonly formatVersion: number;
  readonly schemaDialect: string;
  readonly documentEncoding: {
    readonly encoding: string;
    readonly bom: boolean;
    readonly indentSpaces: number;
    readonly lineEnding: string;
    readonly terminalLf: boolean;
  };
  readonly canonicalSources: Readonly<Record<string, CanonicalSourceExpectation>>;
  readonly immutableArtifactEvidence: readonly ImmutableArtifactEvidence[];
  readonly fixtures: readonly FixtureExpectation[];
}

interface CompiledSchema {
  readonly ajv: Ajv2020;
  readonly id: string;
  readonly root: JsonObject;
  readonly validate: ValidateFunction;
}

interface DescriptorIdentity {
  readonly descriptor: RpcContractDescriptor;
  readonly canonicalDescriptor: string;
  readonly descriptorBytes: number;
  readonly descriptorSha256: string;
  readonly canonicalAbiProjection: string;
  readonly abiProjectionBytes: number;
  readonly abiSha256: string;
}

interface LoadedDocument {
  readonly path: string;
  readonly schemaPath: string;
  readonly value: JsonValue;
  readonly bytes: Buffer;
  readonly descriptorIdentity?: DescriptorIdentity;
}

interface MaterializationGraph {
  readonly bundles: readonly JsonObject[];
  readonly uses: readonly JsonObject[];
  readonly providers: readonly JsonObject[];
  readonly packageDependencies: readonly JsonObject[];
}

interface LoadedCase {
  readonly expectation: FixtureExpectation;
  readonly value: JsonValue;
  readonly bytes?: Buffer;
  readonly identities: ReadonlyMap<string, DescriptorIdentity>;
  readonly generatedContexts?: readonly LoadedDocument[];
  readonly materialization?: MaterializationGraph;
  readonly expectedAggregateCounts?: JsonObject;
}

interface SemanticWorld {
  readonly schemas: ReadonlyMap<string, CompiledSchema>;
  readonly contexts: readonly LoadedDocument[];
  readonly consumedContexts: Set<string>;
  readonly identities: ReadonlyMap<string, DescriptorIdentity>;
  readonly immutableArtifacts: readonly ImmutableArtifactEvidence[];
  readonly materialization?: MaterializationGraph;
}

const authoringRoot = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring");
const conformanceRoot = resolve(authoringRoot, "conformance");
const EXPECTATIONS_PATH = resolve(conformanceRoot, "expectations.json");
const SCHEMA_DIALECT = "https://json-schema.org/draft/2020-12/schema";
const INVENTORY_DOMAIN = "sunder.package-public-api-inventory.v1\0";
const EXPECTED_AUTHORING_CASE_COUNT = 59;
const EXPECTED_AUTHORING_INDEX_SHA256 = "0aebae9087e88c162acb17f4b17ef1511fb419f2b490cf7421177b526dbc488d";

const byteLimits: Readonly<Record<string, number>> = {
  "authoring-receipt.schema.json": 67_108_864,
  "cache-entry-receipt.schema.json": 16_384,
  "contract-lock.schema.json": 33_554_432,
  "owned-contracts.schema.json": 268_435_456,
  "package-project-export.schema.json": 67_108_864,
  "package-public-inventory.schema.json": 1_048_576,
  "platform-catalog.schema.json": 67_108_864,
};

class StrictJsonParser {
  readonly #text: string;
  readonly #maximumDepth: number;
  #offset = 0;

  public constructor(text: string, maximumDepth: number) {
    this.#text = text;
    this.#maximumDepth = maximumDepth;
  }

  public parse(): JsonValue {
    this.#skipWhitespace();
    const value = this.#parseValue(0);
    this.#skipWhitespace();
    if (this.#offset !== this.#text.length) this.#fail("trailing content");
    return value;
  }

  #parseValue(containerDepth: number): JsonValue {
    const character = this.#text[this.#offset];
    if (character === "{") return this.#parseObject(containerDepth + 1);
    if (character === "[") return this.#parseArray(containerDepth + 1);
    if (character === "\"") return this.#parseString();
    if (character === "t") return this.#parseLiteral("true", true);
    if (character === "f") return this.#parseLiteral("false", false);
    if (character === "n") return this.#parseLiteral("null", null);
    if (character === "-" || (character !== undefined && character >= "0" && character <= "9")) {
      return this.#parseNumber();
    }
    this.#fail("a JSON value");
  }

  #parseObject(depth: number): JsonObject {
    this.#checkDepth(depth);
    this.#offset++;
    const result: JsonObject = {};
    const exact = new Set<string>();
    const folded = new Set<string>();
    this.#skipWhitespace();
    if (this.#consume("}")) return result;
    while (true) {
      if (this.#text[this.#offset] !== "\"") this.#fail("an object member name");
      const key = this.#parseString();
      if (exact.has(key)) throw new SyntaxError(`Duplicate JSON member '${key}'.`);
      const insensitive = asciiCaseFold(key);
      if (folded.has(insensitive)) throw new SyntaxError(`ASCII-case-colliding JSON member '${key}'.`);
      exact.add(key);
      folded.add(insensitive);
      this.#skipWhitespace();
      if (!this.#consume(":")) this.#fail("':'");
      this.#skipWhitespace();
      Object.defineProperty(result, key, {
        value: this.#parseValue(depth),
        enumerable: true,
        configurable: true,
        writable: true,
      });
      this.#skipWhitespace();
      if (this.#consume("}")) return result;
      if (!this.#consume(",")) this.#fail("',' or '}'");
      this.#skipWhitespace();
    }
  }

  #parseArray(depth: number): JsonValue[] {
    this.#checkDepth(depth);
    this.#offset++;
    const result: JsonValue[] = [];
    this.#skipWhitespace();
    if (this.#consume("]")) return result;
    while (true) {
      result.push(this.#parseValue(depth));
      this.#skipWhitespace();
      if (this.#consume("]")) return result;
      if (!this.#consume(",")) this.#fail("',' or ']'");
      this.#skipWhitespace();
    }
  }

  #parseString(): string {
    const start = this.#offset;
    this.#offset++;
    while (this.#offset < this.#text.length) {
      const code = this.#text.charCodeAt(this.#offset);
      if (code === 0x22) {
        this.#offset++;
        const value = JSON.parse(this.#text.slice(start, this.#offset)) as string;
        assertUnicodeScalars(value);
        return value;
      }
      if (code < 0x20) this.#fail("an escaped string control character");
      if (code === 0x5c) {
        this.#offset++;
        const escape = this.#text[this.#offset];
        if (escape === "u") {
          if (!/^[0-9a-fA-F]{4}$/u.test(this.#text.slice(this.#offset + 1, this.#offset + 5))) {
            this.#fail("a four-digit Unicode escape");
          }
          this.#offset += 5;
          continue;
        }
        if (escape === undefined || !/^["\\/bfnrt]$/u.test(escape)) this.#fail("a JSON string escape");
      }
      this.#offset++;
    }
    this.#fail("a closing quotation mark");
  }

  #parseNumber(): number {
    const expression = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/uy;
    expression.lastIndex = this.#offset;
    const match = expression.exec(this.#text);
    if (match === null) this.#fail("a JSON number");
    const token = match[0];
    this.#offset = expression.lastIndex;
    if (!/^(?:0|-[1-9][0-9]*|[1-9][0-9]*)$/u.test(token)) {
      throw new SyntaxError(`Outer JSON number '${token}' is not a canonical integer.`);
    }
    const value = Number(token);
    if (!Number.isSafeInteger(value) || Object.is(value, -0)) {
      throw new SyntaxError(`Outer JSON number '${token}' is not a safe canonical integer.`);
    }
    return value;
  }

  #parseLiteral<T extends null | boolean>(token: string, value: T): T {
    if (this.#text.slice(this.#offset, this.#offset + token.length) !== token) this.#fail(`'${token}'`);
    this.#offset += token.length;
    return value;
  }

  #skipWhitespace(): void {
    while (/^[\u0009\u000a\u000d\u0020]$/u.test(this.#text[this.#offset] ?? "")) this.#offset++;
  }

  #consume(character: string): boolean {
    if (this.#text[this.#offset] !== character) return false;
    this.#offset++;
    return true;
  }

  #checkDepth(depth: number): void {
    if (depth > this.#maximumDepth) {
      throw new SyntaxError(`Outer JSON nesting depth exceeds ${this.#maximumDepth}.`);
    }
  }

  #fail(expected: string): never {
    throw new SyntaxError(`Expected ${expected} at UTF-16 offset ${this.#offset}.`);
  }
}

function parseOuterJson(bytes: Uint8Array, maximumBytes: number, maximumDepth = 16): JsonValue {
  if (bytes.byteLength === 0) throw new SyntaxError("Outer JSON is empty.");
  if (bytes.byteLength > maximumBytes) throw new SyntaxError(`Outer JSON exceeds ${maximumBytes} bytes.`);
  const view = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf]))) {
    throw new SyntaxError("Outer JSON must not contain a UTF-8 BOM.");
  }
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    throw new SyntaxError("Outer JSON is not strict UTF-8.");
  }
  return new StrictJsonParser(text, maximumDepth).parse();
}

function assertUnicodeScalars(value: string): void {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code < 0xd800 || code > 0xdfff) continue;
    if (code > 0xdbff || index + 1 >= value.length) throw new SyntaxError("JSON string contains an unpaired surrogate.");
    const low = value.charCodeAt(++index);
    if (low < 0xdc00 || low > 0xdfff) throw new SyntaxError("JSON string contains an unpaired surrogate.");
  }
}

function asciiCaseFold(value: string): string {
  let result = "";
  for (const character of value) {
    const code = character.charCodeAt(0);
    result += code >= 0x41 && code <= 0x5a ? String.fromCharCode(code + 0x20) : character;
  }
  return result;
}

interface SchemaDescription {
  readonly propertyOrder: string[];
  readonly properties: Map<string, JsonValue>;
  additionalProperties?: JsonValue;
  items?: JsonValue;
}

function describeSchema(schema: JsonValue, value: JsonValue, root: JsonObject): SchemaDescription {
  const description: SchemaDescription = { propertyOrder: [], properties: new Map() };
  mergeSchemaDescription(description, schema, value, root, new Set());
  return description;
}

function mergeSchemaDescription(
  target: SchemaDescription,
  schema: JsonValue,
  value: JsonValue,
  root: JsonObject,
  active: Set<JsonObject>,
): void {
  if (typeof schema === "boolean" || !isObject(schema) || active.has(schema)) return;
  active.add(schema);
  const reference = schema.$ref;
  if (typeof reference === "string") {
    mergeSchemaDescription(target, resolveInternalReference(root, reference), value, root, active);
  }
  if (isObject(schema.properties)) {
    for (const [key, child] of Object.entries(schema.properties)) {
      if (!target.properties.has(key)) target.propertyOrder.push(key);
      target.properties.set(key, child);
    }
  }
  if (schema.additionalProperties !== undefined) target.additionalProperties = schema.additionalProperties;
  if (schema.items !== undefined) target.items = schema.items;
  if (Array.isArray(schema.allOf)) {
    for (const member of schema.allOf) mergeConditionalDescription(target, member, value, root, active);
  }
  mergeConditionalDescription(target, schema, value, root, active, true);
  active.delete(schema);
}

function mergeConditionalDescription(
  target: SchemaDescription,
  schema: JsonValue,
  value: JsonValue,
  root: JsonObject,
  active: Set<JsonObject>,
  onlyConditional = false,
): void {
  if (!isObject(schema)) return;
  if (!onlyConditional && schema.if === undefined) {
    mergeSchemaDescription(target, schema, value, root, active);
    return;
  }
  if (schema.if === undefined) return;
  const branch = matchesSchemaCondition(value, schema.if, root) ? schema.then : schema.else;
  if (branch !== undefined) mergeSchemaDescription(target, branch, value, root, active);
}

function matchesSchemaCondition(value: JsonValue, schema: JsonValue, root: JsonObject): boolean {
  if (schema === true) return true;
  if (schema === false || !isObject(schema)) return false;
  if (typeof schema.$ref === "string" && !matchesSchemaCondition(value, resolveInternalReference(root, schema.$ref), root)) {
    return false;
  }
  if (schema.not !== undefined && matchesSchemaCondition(value, schema.not, root)) return false;
  if (schema.const !== undefined && !jsonEqual(value, schema.const)) return false;
  if (Array.isArray(schema.enum) && !schema.enum.some(item => jsonEqual(value, item))) return false;
  if (schema.type === "object" && !isObject(value)) return false;
  if (Array.isArray(schema.required)) {
    if (!isObject(value) || schema.required.some(key => typeof key !== "string" || value[key] === undefined)) return false;
  }
  if (isObject(schema.properties) && isObject(value)) {
    for (const [key, childSchema] of Object.entries(schema.properties)) {
      const child = value[key];
      if (child !== undefined && !matchesSchemaCondition(child, childSchema, root)) return false;
    }
  }
  if (Array.isArray(schema.allOf) && !schema.allOf.every(item => matchesSchemaCondition(value, item, root))) return false;
  return true;
}

function resolveInternalReference(root: JsonObject, reference: string): JsonValue {
  assert.ok(reference.startsWith("#/"), `External schema reference '${reference}' is not standalone.`);
  let value: JsonValue = root;
  for (const encoded of reference.slice(2).split("/")) {
    const key = encoded.replaceAll("~1", "/").replaceAll("~0", "~");
    assert.ok(isObject(value) && value[key] !== undefined, `Unresolved schema reference '${reference}'.`);
    value = value[key];
  }
  return value;
}

function schemaAtPath(compiled: CompiledSchema, schemaPath: string): JsonValue {
  const marker = schemaPath.indexOf("#");
  return marker < 0 ? compiled.root : resolveInternalReference(compiled.root, `#${schemaPath.slice(marker + 1)}`);
}

function writeDocument(value: JsonValue, schema: JsonValue, root: JsonObject): string {
  return `${writeJson(value, schema, root, 0, false)}\n`;
}

function writeCompact(value: JsonValue, schema: JsonValue, root: JsonObject): string {
  return writeJson(value, schema, root, 0, true);
}

function writeJson(value: JsonValue, schema: JsonValue, root: JsonObject, depth: number, compact: boolean): string {
  if (value === null || typeof value === "boolean") return JSON.stringify(value);
  if (typeof value === "string") {
    assertUnicodeScalars(value);
    return JSON.stringify(value);
  }
  if (typeof value === "number") {
    assert.ok(Number.isSafeInteger(value) && !Object.is(value, -0), "Outer JSON numbers must be safe canonical integers.");
    return String(value);
  }
  const description = describeSchema(schema, value, root);
  if (Array.isArray(value)) {
    if (value.length === 0) return "[]";
    const itemSchema = description.items ?? true;
    const values = value.map(item => writeJson(item, itemSchema, root, depth + 1, compact));
    if (compact) return `[${values.join(",")}]`;
    const indentation = "  ".repeat(depth + 1);
    return `[\n${values.map(item => `${indentation}${item}`).join(",\n")}\n${"  ".repeat(depth)}]`;
  }
  const sourceKeys = Object.keys(value);
  const known = description.propertyOrder.filter(key => value[key] !== undefined);
  const unknown = sourceKeys.filter(key => !description.properties.has(key)).sort(ordinal);
  const keys = description.propertyOrder.length === 0 && description.additionalProperties !== undefined
    ? [...sourceKeys].sort(ordinal)
    : [...known, ...unknown];
  assert.equal(new Set(keys).size, sourceKeys.length, "Schema-aware writer did not cover every object member.");
  if (keys.length === 0) return "{}";
  const members = keys.map((key) => {
    const childSchema = description.properties.get(key) ?? description.additionalProperties ?? true;
    return `${JSON.stringify(key)}${compact ? ":" : ": "}${writeJson(value[key]!, childSchema, root, depth + 1, compact)}`;
  });
  if (compact) return `{${members.join(",")}}`;
  const indentation = "  ".repeat(depth + 1);
  return `{\n${members.map(member => `${indentation}${member}`).join(",\n")}\n${"  ".repeat(depth)}}`;
}

function jsonEqual(left: JsonValue, right: JsonValue): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

function cloneJson<T extends JsonValue>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function isObject(value: JsonValue | undefined): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function objectValue(value: JsonValue | undefined, label: string): JsonObject {
  assert.ok(isObject(value), `${label} must be an object.`);
  return value;
}

function arrayValue(value: JsonValue | undefined, label: string): JsonValue[] {
  assert.ok(Array.isArray(value), `${label} must be an array.`);
  return value;
}

function stringValue(value: JsonValue | undefined, label: string): string {
  assert.equal(typeof value, "string", `${label} must be a string.`);
  return value as string;
}

function numberValue(value: JsonValue | undefined, label: string): number {
  assert.equal(typeof value, "number", `${label} must be a number.`);
  return value as number;
}

function booleanValue(value: JsonValue | undefined, label: string): boolean {
  assert.equal(typeof value, "boolean", `${label} must be a boolean.`);
  return value as boolean;
}

function schemaFileName(schemaPath: string): string {
  const fileName = basename(schemaPath.split("#", 1)[0]!);
  assert.ok(byteLimits[fileName] !== undefined, `Unknown authoring schema '${schemaPath}'.`);
  return fileName;
}

function sha256(value: Uint8Array | string): string {
  return createHash("sha256").update(value).digest("hex");
}

function domainHash(domain: string, compactJson: string): string {
  return sha256(Buffer.concat([Buffer.from(domain, "utf8"), Buffer.from(compactJson, "utf8")]));
}

function descriptorKey(contractId: string, version: string): string {
  return `${contractId}\0${version}`;
}

async function compileSchemas(): Promise<Map<string, CompiledSchema>> {
  const names = (await readdir(authoringRoot))
    .filter(name => name.endsWith(".schema.json"))
    .sort(ordinal);
  assert.equal(names.length, 7, "The authoring format must expose exactly seven standalone schemas.");
  const result = new Map<string, CompiledSchema>();
  for (const name of names) {
    const bytes = await readFile(resolve(authoringRoot, name));
    const root = objectValue(parseOuterJson(bytes, bytes.byteLength, 512), name);
    assert.equal(root.$schema, SCHEMA_DIALECT, name);
    const id = stringValue(root.$id, `${name}.$id`);
    const ajv = new Ajv2020({ strict: true, allErrors: true });
    const validate = ajv.compile(root);
    result.set(name, { ajv, id, root, validate });
  }
  return result;
}

function validatorFor(schemas: ReadonlyMap<string, CompiledSchema>, schemaPath: string): ValidateFunction {
  const compiled = schemas.get(schemaFileName(schemaPath));
  assert.ok(compiled, schemaPath);
  const marker = schemaPath.indexOf("#");
  if (marker < 0) return compiled.validate;
  const validate = compiled.ajv.getSchema(`${compiled.id}${schemaPath.slice(marker)}`);
  assert.ok(validate, `Ajv did not compile schema fragment '${schemaPath}'.`);
  return validate;
}

function ajvErrorSignatures(validate: ValidateFunction): string[] {
  return (validate.errors ?? []).map(error => [
    error.keyword,
    error.instancePath,
    error.schemaPath,
    JSON.stringify(error.params),
  ].join("|"));
}

async function descriptorIdentity(input: string | Uint8Array): Promise<DescriptorIdentity> {
  const descriptor = parseRpcContractDescriptor(input);
  const descriptorBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
  const abiProjectionBytes = Buffer.from(canonicalRpcAbiProjectionBytes(descriptor));
  const descriptorSha256 = await computeRpcDescriptorSha256(descriptor);
  const abiSha256 = await computeRpcAbiSha256(descriptor);
  assert.equal(descriptorSha256, sha256(descriptorBytes));
  assert.equal(
    abiSha256,
    sha256(Buffer.concat([Buffer.from(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8"), abiProjectionBytes])),
  );
  return {
    descriptor,
    canonicalDescriptor: descriptorBytes.toString("utf8"),
    descriptorBytes: descriptorBytes.byteLength,
    descriptorSha256,
    canonicalAbiProjection: abiProjectionBytes.toString("utf8"),
    abiProjectionBytes: abiProjectionBytes.byteLength,
    abiSha256,
  };
}

async function loadCanonicalIdentities(
  expectations: AuthoringExpectations,
): Promise<{ byKey: Map<string, DescriptorIdentity>; byName: Map<string, DescriptorIdentity> }> {
  assert.equal(
    Buffer.from(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8").toString("hex"),
    "73756e6465722e7270632e6162692e763100",
  );
  const byKey = new Map<string, DescriptorIdentity>();
  const byName = new Map<string, DescriptorIdentity>();
  const pending = new Map(Object.entries(expectations.canonicalSources));
  while (pending.size > 0) {
    let progressed = false;
    for (const [name, expected] of pending) {
      let identity: DescriptorIdentity;
      if (expected.descriptorPath !== undefined) {
        const descriptorPath = resolve(conformanceRoot, expected.descriptorPath);
        identity = await descriptorIdentity(await readFile(descriptorPath));
        if (expected.expectationsPath !== undefined) {
          const sharedPath = resolve(conformanceRoot, expected.expectationsPath);
          const shared = objectValue(
            parseOuterJson(await readFile(sharedPath), 4 * 1024 * 1024, 64),
            expected.expectationsPath,
          );
          const fixtures = arrayValue(shared.fixtures, `${expected.expectationsPath}.fixtures`);
          const relativeDescriptorPath = relative(dirname(sharedPath), descriptorPath).replaceAll("\\", "/");
          const sharedFixture = fixtures
            .map((value, index) => objectValue(value, `shared fixture ${index}`))
            .find(value => value.path === relativeDescriptorPath);
          assert.ok(sharedFixture, `Shared RPC expectations do not index '${relativeDescriptorPath}'.`);
          assert.equal(sharedFixture.valid, true);
          assert.equal(sharedFixture.schemaValid, true);
          assert.equal(sharedFixture.descriptorSha256, identity.descriptorSha256);
          assert.equal(sharedFixture.abiSha256, identity.abiSha256);
          assert.equal(sharedFixture.canonicalDescriptorBytes, identity.descriptorBytes);
          assert.equal(sharedFixture.canonicalAbiProjectionBytes, identity.abiProjectionBytes);
        }
      } else {
        assert.ok(expected.derivedFrom, `${name} has neither descriptorPath nor derivedFrom.`);
        const source = byName.get(expected.derivedFrom);
        if (source === undefined) continue;
        const derived = JSON.parse(JSON.stringify(source.descriptor)) as JsonObject;
        derived.contractId = expected.contractId;
        identity = await descriptorIdentity(JSON.stringify(derived));
      }
      assert.equal(identity.descriptor.contractId, expected.contractId, name);
      assert.equal(identity.descriptor.version, expected.contractVersion, name);
      assert.equal(identity.descriptorBytes, expected.descriptorBytes, name);
      assert.equal(identity.descriptorSha256, expected.descriptorSha256, name);
      assert.equal(identity.abiProjectionBytes, expected.abiProjectionBytes, name);
      assert.equal(identity.abiSha256, expected.abiSha256, name);
      const key = descriptorKey(expected.contractId, expected.contractVersion);
      const prior = byKey.get(key);
      if (prior !== undefined) assert.equal(prior.descriptorSha256, identity.descriptorSha256, key);
      else byKey.set(key, identity);
      byName.set(name, identity);
      pending.delete(name);
      progressed = true;
    }
    assert.equal(progressed, true, `Unresolvable canonical source derivations: ${[...pending.keys()].join(", ")}`);
  }
  return { byKey, byName };
}

async function loadFixtureDocument(
  fixturePath: string,
  schemaPath: string,
): Promise<LoadedDocument> {
  const bytes = await readFile(resolve(conformanceRoot, fixturePath));
  const maximumBytes = byteLimits[schemaFileName(schemaPath)]!;
  const value = parseOuterJson(bytes, maximumBytes, 16);
  return { path: fixturePath, schemaPath, value, bytes };
}

async function generateRecipe(
  recipe: JsonObject,
  schemas: ReadonlyMap<string, CompiledSchema>,
  identitiesByName: ReadonlyMap<string, DescriptorIdentity>,
): Promise<Omit<LoadedCase, "expectation">> {
  if (recipe.operation === "replace-array") {
    const baseFixturePath = stringValue(recipe.baseFixturePath, "recipe.baseFixturePath");
    const base = objectValue(
      parseOuterJson(await readFile(resolve(conformanceRoot, baseFixturePath)), byteLimits["package-project-export.schema.json"]!, 16),
      baseFixturePath,
    );
    assert.equal(recipe.jsonPointer, "/contracts");
    const count = numberValue(recipe.count, "recipe.count");
    const start = numberValue(recipe.indexStart, "recipe.indexStart");
    const format = stringValue(recipe.contractKeyFormat, "recipe.contractKeyFormat");
    const sourceName = stringValue(recipe.contractSource, "recipe.contractSource");
    const source = identitiesByName.get(sourceName);
    assert.ok(source, sourceName);
    const fixed = objectValue(recipe.fixedMembers, "recipe.fixedMembers");
    const authorityId = stringValue(fixed.authorityId, "recipe.fixedMembers.authorityId");
    base.contracts = Array.from({ length: count }, (_, offset): JsonValue => {
      const contractKey = formatIndex(format, start + offset);
      return {
        contractKey,
        contractId: `${authorityId}.${contractKey}`,
        contractVersion: source.descriptor.version,
        exposure: stringValue(fixed.exposure, "recipe.fixedMembers.exposure"),
        source: stringValue(fixed.source, "recipe.fixedMembers.source"),
        authorityKind: stringValue(fixed.authorityKind, "recipe.fixedMembers.authorityKind"),
        authorityId,
        descriptorBytes: source.descriptorBytes,
        canonicalDescriptor: source.canonicalDescriptor,
        descriptorSha256: source.descriptorSha256,
        abiSha256: source.abiSha256,
      };
    });
    return { value: base, identities: new Map() };
  }

  if (recipe.operation === "materialization-boundary") {
    return generateMaterializationRecipe(recipe);
  }
  throw new Error(`Unsupported authoring recipe shape '${String(recipe.id)}'.`);
}

async function generateMaterializationRecipe(
  recipe: JsonObject,
): Promise<Omit<LoadedCase, "expectation">> {
  const basePath = stringValue(recipe.baseFixturePath, "recipe.baseFixturePath");
  const base = objectValue(
    parseOuterJson(await readFile(resolve(conformanceRoot, basePath)), byteLimits["contract-lock.schema.json"]!, 16),
    basePath,
  );
  const target = stringValue(recipe.target, "recipe.target");
  assert.ok(["bundles", "uses", "providers", "packageDependencies"].includes(target));
  const count = numberValue(recipe.count, "recipe.count");
  const records = Array.from({ length: count }, (_, index): JsonObject => {
    const suffix = String(index).padStart(3, "0");
    if (target === "bundles") return {
      contractId: `example.aggregate.c${suffix}`,
      contractVersion: "1.0.0",
      evidenceFingerprint: sha256(`bundle:${suffix}`),
    };
    if (target === "uses") return {
      role: index % 2 === 0 ? "app" : "runtime",
      contractId: `example.aggregate.c${suffix}`,
      contractVersion: "1.0.0",
      evidenceFingerprint: sha256(`use:${suffix}`),
    };
    if (target === "providers") return { role: "runtime", providerId: `example.aggregate.p${suffix}` };
    return {
      packageId: `example.aggregate.d${suffix}`,
      evidenceFingerprint: sha256(`dependency:${suffix}`),
      presence: "required",
    };
  });
  const graph: MaterializationGraph = {
    bundles: target === "bundles" ? records : [],
    uses: target === "uses" ? records : [],
    providers: target === "providers" ? records : [],
    packageDependencies: target === "packageDependencies" ? records : [],
  };
  const expectedAggregateCounts = objectValue(recipe.expectedAggregateCounts, "recipe.expectedAggregateCounts");
  const actualCounts = materializedCounts(base, graph);
  for (const key of ["contractBundles", "roleScopedUses", "providers", "packageDependencies"]) {
    assert.equal(actualCounts[key], expectedAggregateCounts[key], `Aggregate recipe count '${key}'.`);
  }
  return { value: base, identities: new Map(), materialization: graph, expectedAggregateCounts };
}

function formatIndex(format: string, index: number, packageId?: string): string {
  return format
    .replaceAll("{packageId}", packageId ?? "")
    .replaceAll("{index:000}", String(index).padStart(3, "0"))
    .replaceAll("{index}", String(index));
}

function inventoryContract(contractId: string, metadata: JsonObject): JsonObject {
  const result: JsonObject = {
    contractId,
    contractVersion: metadata.resolvedContractVersion!,
    descriptorBytes: metadata.descriptorBytes!,
    descriptorSha256: metadata.descriptorSha256!,
    abiSha256: metadata.abiSha256!,
    artifactSource: metadata.artifactSource!,
    authorityKind: metadata.authorityKind!,
    authorityId: metadata.authorityId!,
    selectedAuthorityRelease: metadata.selectedAuthorityRelease!,
    selectedAuthorityContentIdentity: metadata.selectedAuthorityContentIdentity!,
  };
  return result;
}

function isSortedUnique(values: readonly string[]): boolean {
  return new Set(values).size === values.length
    && values.every((value, index) => index === 0 || ordinal(values[index - 1]!, value) < 0);
}

function optionalString(value: JsonValue | undefined): string | undefined {
  return typeof value === "string" ? value : undefined;
}

async function identityDiagnostic(
  record: JsonObject,
  contractId: string,
  contractVersion: string,
  identities: ReadonlyMap<string, DescriptorIdentity>,
): Promise<string | null> {
  let identity: DescriptorIdentity | undefined;
  if (typeof record.canonicalDescriptor === "string") {
    try {
      identity = await descriptorIdentity(record.canonicalDescriptor);
    } catch {
      return "rpc-authoring.hash-mismatch";
    }
    if (identity.canonicalDescriptor !== record.canonicalDescriptor) return "rpc-authoring.hash-mismatch";
  } else {
    identity = identities.get(descriptorKey(contractId, contractVersion));
  }
  if (identity === undefined) return "rpc-authoring.hash-mismatch";
  if (identity.descriptor.contractId !== contractId || identity.descriptor.version !== contractVersion) {
    return "rpc-authoring.root-mismatch";
  }
  if (record.descriptorBytes !== undefined && record.descriptorBytes !== identity.descriptorBytes) {
    return "rpc-authoring.hash-mismatch";
  }
  if (record.descriptorSha256 !== undefined && record.descriptorSha256 !== identity.descriptorSha256) {
    return "rpc-authoring.hash-mismatch";
  }
  if (record.abiProjectionBytes !== undefined && record.abiProjectionBytes !== identity.abiProjectionBytes) {
    return "rpc-authoring.hash-mismatch";
  }
  if (record.canonicalAbiProjection !== undefined && record.canonicalAbiProjection !== identity.canonicalAbiProjection) {
    return "rpc-authoring.hash-mismatch";
  }
  if (record.abiSha256 !== undefined && record.abiSha256 !== identity.abiSha256) {
    return "rpc-authoring.hash-mismatch";
  }
  if (record.etag !== undefined && record.etag !== `"${identity.descriptorSha256}"`) {
    return "rpc-authoring.hash-mismatch";
  }
  return null;
}

function authoringContext(
  document: LoadedDocument,
  fileName: string,
  fragment?: string,
): boolean {
  if (document.schemaPath === "rpc-v1-descriptor") return false;
  return schemaFileName(document.schemaPath) === fileName
    && (fragment === undefined ? !document.schemaPath.includes("#") : document.schemaPath.endsWith(fragment));
}

function resolveContexts(
  world: SemanticWorld,
  predicate: (document: LoadedDocument) => boolean,
): LoadedDocument[] {
  const matches = world.contexts.filter(predicate);
  if (matches.length === 1) world.consumedContexts.add(matches[0]!.path);
  return matches;
}

function compactFor(
  value: JsonValue,
  schemaPath: string,
  schemas: ReadonlyMap<string, CompiledSchema>,
): string {
  const compiled = schemas.get(schemaFileName(schemaPath));
  assert.ok(compiled);
  return writeCompact(value, schemaAtPath(compiled, schemaPath), compiled.root);
}

function catalogContentIdentity(catalog: JsonObject, schemas: ReadonlyMap<string, CompiledSchema>): string {
  return sha256(Buffer.from(compactFor(catalog, "../platform-catalog.schema.json", schemas), "utf8"));
}

async function catalogDiagnostic(payload: JsonObject, world: SemanticWorld): Promise<string | null> {
  const contracts = arrayValue(payload.contracts, "catalog contracts")
    .map((value, index) => objectValue(value, `catalog contract ${index}`));
  if (payload.contractCount !== contracts.length) return "rpc-authoring.count-mismatch";
  const contractKeys = contracts.map(contract => `${String(contract.contractId)}\0${String(contract.contractVersion)}`);
  if (!isSortedUnique(contractKeys)) return "rpc-authoring.order-mismatch";
  if (new Set(contracts.map(contract => contract.contractId)).size !== contracts.length) {
    return "rpc-authoring.duplicate-contract-id";
  }
  if (contracts.filter(contract => contract.contractId === "sunder.stack.contributor").length !== 1) {
    return "rpc-authoring.mandatory-platform-contract-missing";
  }
  for (const contract of contracts) {
    const diagnostic = await identityDiagnostic(
      contract,
      stringValue(contract.contractId, "catalog contract id"),
      stringValue(contract.contractVersion, "catalog contract version"),
      world.identities,
    );
    if (diagnostic !== null) return diagnostic;
  }
  return null;
}

function matchingCatalogs(
  authorityRelease: string,
  authorityContentIdentity: string,
  world: SemanticWorld,
): JsonObject[] {
  const matches = world.contexts
    .filter(document => authoringContext(document, "platform-catalog.schema.json"))
    .map(document => objectValue(document.value, document.path))
    .filter(catalog => catalog.releaseVersion === authorityRelease
      && catalogContentIdentity(catalog, world.schemas) === authorityContentIdentity);
  if (matches.length === 1) {
    const selected = matches[0]!;
    const document = world.contexts.find(candidate => candidate.value === selected);
    assert.ok(document);
    world.consumedContexts.add(document.path);
  }
  return matches;
}

async function platformProvenanceDiagnostic(record: JsonObject, world: SemanticWorld): Promise<string | null> {
  if (record.authorityKind !== "platform") return null;
  if (record.authorityId !== "sunder.core") return "rpc-authoring.authority-mismatch";
  const release = stringValue(record.selectedAuthorityRelease, "platform authority release");
  const content = stringValue(record.selectedAuthorityContentIdentity, "platform authority content");
  const catalogs = matchingCatalogs(release, content, world);
  if (catalogs.length !== 1) return "rpc-authoring.authority-mismatch";
  const catalog = catalogs[0]!;
  const catalogError = await catalogDiagnostic(catalog, world);
  if (catalogError !== null) return catalogError;
  const contractId = optionalString(record.contractId);
  const version = optionalString(record.contractVersion) ?? optionalString(record.resolvedContractVersion);
  if (contractId !== undefined && version !== undefined) {
    const match = arrayValue(catalog.contracts, "catalog contracts")
      .map(value => objectValue(value, "catalog contract"))
      .find(value => value.contractId === contractId && value.contractVersion === version);
    if (match === undefined
      || match.descriptorBytes !== record.descriptorBytes
      || match.descriptorSha256 !== record.descriptorSha256
      || match.abiSha256 !== record.abiSha256) return "rpc-authoring.hash-mismatch";
  }
  return null;
}

async function inventoryDiagnostic(inventory: JsonObject, world: SemanticWorld): Promise<string | null> {
  const contracts = arrayValue(inventory.contracts, "inventory contracts")
    .map((value, index) => objectValue(value, `inventory contract ${index}`));
  if (inventory.publicContractCount !== contracts.length) return "rpc-authoring.count-mismatch";
  const keys = contracts.map(contract => `${String(contract.contractId)}\0${String(contract.contractVersion)}`);
  if (!isSortedUnique(keys)) return "rpc-authoring.order-mismatch";
  if (new Set(contracts.map(contract => contract.contractId)).size !== contracts.length) {
    return "rpc-authoring.duplicate-contract-id";
  }
  for (const contract of contracts) {
    const contractId = stringValue(contract.contractId, "inventory contract id");
    const version = stringValue(contract.contractVersion, "inventory contract version");
    const identityError = await identityDiagnostic(contract, contractId, version, world.identities);
    if (identityError !== null) return identityError;
    if (contract.artifactSource === "owned" && (contract.authorityKind !== "package"
      || contract.authorityId !== inventory.packageId
      || contract.selectedAuthorityRelease !== inventory.packageVersion
      || contract.selectedAuthorityContentIdentity !== inventory.packageContentIdentity)) {
      return "rpc-authoring.authority-mismatch";
    }
    const provenanceError = await platformProvenanceDiagnostic(contract, world);
    if (provenanceError !== null) return provenanceError;
  }
  return null;
}

function inventoryFromArtifact(packageId: string, artifact: JsonObject): JsonObject {
  const contracts = objectValue(artifact.contracts, `package artifact '${packageId}' contracts`);
  return {
    schemaVersion: 1,
    packageId,
    packageVersion: artifact.selectedPackageVersion!,
    packageContentIdentity: artifact.selectedPackageContentIdentity!,
    publicContractCount: artifact.publicContractCount!,
    contracts: Object.entries(contracts).map(([contractId, value]) => inventoryContract(
      contractId,
      objectValue(value, `package artifact contract '${contractId}'`),
    )),
  };
}

function authorityAgreementDiagnostic(records: readonly JsonObject[]): string | null {
  const authorities = new Map<string, string>();
  for (const record of records) {
    const authorityKind = stringValue(record.authorityKind, "authority kind");
    const authorityId = stringValue(record.authorityId, "authority id");
    const agreement = JSON.stringify({
      selectedAuthorityRelease: record.selectedAuthorityRelease,
      selectedAuthorityContentIdentity: record.selectedAuthorityContentIdentity,
    });
    const key = `${authorityKind}\0${authorityId}`;
    if (authorities.has(key) && authorities.get(key) !== agreement) return "rpc-authoring.authority-mismatch";
    authorities.set(key, agreement);
  }
  return null;
}

async function lockDiagnostic(lock: JsonObject, world: SemanticWorld): Promise<string | null> {
  const artifacts = objectValue(lock.packageArtifacts, "lock packageArtifacts");
  const packageImports = objectValue(lock.packageImports, "lock packageImports");
  const providerImports = objectValue(lock.providerImports, "lock providerImports");
  const platformImports = objectValue(lock.platformImports, "lock platformImports");
  for (const map of [artifacts, packageImports, providerImports, platformImports]) {
    if (!isSortedUnique(Object.keys(map))) return "rpc-authoring.order-mismatch";
  }
  const inventorySchema = world.schemas.get("package-public-inventory.schema.json");
  assert.ok(inventorySchema);
  const inventoryDocuments = world.contexts
    .filter(document => authoringContext(document, "package-public-inventory.schema.json"));
  const inventories = inventoryDocuments.map(document => objectValue(document.value, document.path));
  const authorityRecords: JsonObject[] = [];
  if (inventories.length !== Object.keys(artifacts).length) return "rpc-authoring.evidence-mismatch";
  for (const [packageId, value] of Object.entries(artifacts)) {
    const artifact = objectValue(value, `package artifact '${packageId}'`);
    authorityRecords.push({
      authorityKind: "package",
      authorityId: packageId,
      selectedAuthorityRelease: artifact.selectedPackageVersion!,
      selectedAuthorityContentIdentity: artifact.selectedPackageContentIdentity!,
    });
    const contracts = objectValue(artifact.contracts, `package artifact '${packageId}' contracts`);
    const matchingInventories = inventories.filter(inventory => inventory.packageId === packageId);
    if (matchingInventories.length !== 1) return "rpc-authoring.evidence-mismatch";
    const inventory = matchingInventories[0]!;
    const inventoryDocument = inventoryDocuments.find(document => document.value === inventory);
    assert.ok(inventoryDocument);
    world.consumedContexts.add(inventoryDocument.path);
    if (artifact.publicContractCount !== Object.keys(contracts).length) return "rpc-authoring.count-mismatch";
    if (!isSortedUnique(Object.keys(contracts))) return "rpc-authoring.order-mismatch";
    const selected = stringValue(artifact.selectedPackageVersion, "selected package version");
    const range = stringValue(artifact.requestedVersionRange, "requested version range");
    if (!isSemanticVersion(selected) || !isPackageVersionRange(range) || !isVersionInRange(selected, range)) {
      return "rpc-authoring.version-selection-mismatch";
    }
    if (!booleanValue(artifact.includePrerelease, "includePrerelease") && hasSemanticPrerelease(selected)) {
      return "rpc-authoring.version-selection-mismatch";
    }
    if (artifact.registryOrigin !== undefined && !canonicalRegistryOrigin(stringValue(artifact.registryOrigin, "registry origin"))) {
      return "rpc-authoring.registry-origin-invalid";
    }
    const immutableEvidence = world.immutableArtifacts.filter(evidence => evidence.packageId === packageId
      && evidence.packageVersion === artifact.selectedPackageVersion
      && evidence.packageContentIdentity === artifact.selectedPackageContentIdentity
      && evidence.publicContractCount === artifact.publicContractCount
      && evidence.publicInventorySha256 === artifact.publicInventorySha256);
    if (immutableEvidence.length !== 1) {
      return "rpc-authoring.evidence-mismatch";
    }
    const projectedInventory = inventoryFromArtifact(packageId, artifact);
    const digest = domainHash(INVENTORY_DOMAIN, writeCompact(inventory, inventorySchema.root, inventorySchema.root));
    if (artifact.publicInventorySha256 !== digest) return "rpc-authoring.hash-mismatch";
    if (!jsonEqual(projectedInventory, inventory)) return "rpc-authoring.evidence-mismatch";
    for (const [contractId, contractValue] of Object.entries(contracts)) {
      const contract = objectValue(contractValue, `artifact contract '${contractId}'`);
      authorityRecords.push(contract);
      const version = stringValue(contract.resolvedContractVersion, "resolved contract version");
      const identityError = await identityDiagnostic(contract, contractId, version, world.identities);
      if (identityError !== null) return identityError;
      if (contract.artifactSource === "owned" && (contract.authorityKind !== "package"
        || contract.authorityId !== packageId
        || contract.selectedAuthorityRelease !== artifact.selectedPackageVersion
        || contract.selectedAuthorityContentIdentity !== artifact.selectedPackageContentIdentity)) {
        return "rpc-authoring.authority-mismatch";
      }
      const provenanceError = await platformProvenanceDiagnostic({ contractId, ...contract }, world);
      if (provenanceError !== null) return provenanceError;
    }
  }
  const referenced = new Set<string>();
  for (const [packageId, value] of Object.entries(packageImports)) {
    referenced.add(packageId);
    const artifact = objectValue(artifacts[packageId], `package import artifact '${packageId}'`);
    const contracts = objectValue(artifact.contracts, `package import artifact '${packageId}' contracts`);
    const packageContracts = Object.entries(contracts)
      .filter(([, contract]) => objectValue(contract, "package contract").authorityKind === "package")
      .map(([contractId]) => contractId);
    if (packageContracts.length === 0) return "package-api.consumer-api-empty";
    const required = arrayValue(objectValue(value, `package import '${packageId}'`).requiredContracts, "requiredContracts")
      .map(item => stringValue(item, "required contract"));
    if (!isSortedUnique(required) || required.some(contractId => !packageContracts.includes(contractId))) {
      return "rpc-authoring.root-mismatch";
    }
  }
  const providerTuples = new Set<string>();
  for (const value of Object.values(providerImports)) {
    const provider = objectValue(value, "provider import");
    const packageId = stringValue(provider.packageId, "provider package id");
    const contractId = stringValue(provider.contractId, "provider contract id");
    const tuple = `${packageId}\0${contractId}`;
    if (providerTuples.has(tuple)) return "rpc-authoring.duplicate-projected-key";
    providerTuples.add(tuple);
    referenced.add(packageId);
    const artifact = objectValue(artifacts[packageId], `provider artifact '${packageId}'`);
    const contract = objectValue(objectValue(artifact.contracts, "provider artifact contracts")[contractId], "provider contract");
    if (contract.authorityKind !== "package") return "rpc-authoring.authority-mismatch";
  }
  const platformTuples = new Set<string>();
  for (const value of Object.values(platformImports)) {
    const platform = objectValue(value, "platform import");
    authorityRecords.push(platform);
    const tuple = projectedKey(platform, "authorityId", "contractId");
    if (platformTuples.has(tuple)) return "rpc-authoring.duplicate-projected-key";
    platformTuples.add(tuple);
    const selected = stringValue(platform.selectedAuthorityRelease, "platform selected release");
    const range = stringValue(platform.authorityReleaseRange, "platform authority range");
    if (!isSemanticVersion(selected) || !isPackageVersionRange(range) || !isVersionInRange(selected, range)
      || (!booleanValue(platform.includePrerelease, "platform includePrerelease") && hasSemanticPrerelease(selected))) {
      return "rpc-authoring.version-selection-mismatch";
    }
    if (platform.registryOrigin !== undefined && !canonicalRegistryOrigin(stringValue(platform.registryOrigin, "platform registry origin"))) {
      return "rpc-authoring.registry-origin-invalid";
    }
    const identityError = await identityDiagnostic(
      platform,
      stringValue(platform.contractId, "platform contract id"),
      stringValue(platform.resolvedContractVersion, "platform contract version"),
      world.identities,
    );
    if (identityError !== null) return identityError;
    const provenanceError = await platformProvenanceDiagnostic(platform, world);
    if (provenanceError !== null) return provenanceError;
  }
  const authorityError = authorityAgreementDiagnostic(authorityRecords);
  if (authorityError !== null) return authorityError;
  const unreferenced = Object.keys(artifacts).find(packageId => !referenced.has(packageId));
  if (unreferenced !== undefined) return "rpc-authoring.unreferenced-artifact";
  const materialization = world.materialization ?? materializationFromLock(lock);
  if (materializationHasConflict(materialization)) {
    return "rpc-authoring.evidence-mismatch";
  }
  const counts = materializedCounts(lock, materialization);
  if (Object.entries(counts).some(([key, value]) => numberValue(value, `materialized count '${key}'`) > 128)) {
    return "rpc-authoring.aggregate-materializability-overflow";
  }
  return null;
}

function canonicalRegistryOrigin(value: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    return false;
  }
  if (parsed.toString() !== value || parsed.username !== "" || parsed.password !== ""
    || parsed.pathname !== "/" || parsed.search !== "" || parsed.hash !== "") return false;
  if (parsed.protocol === "http:") return parsed.hostname === "localhost";
  if (parsed.protocol !== "https:" || parsed.hostname !== parsed.hostname.toLowerCase()
    || !/^[a-z0-9.-]+$/u.test(parsed.hostname)) return false;
  return parsed.hostname !== "localhost" && !/^\[|^[0-9]+(?:\.[0-9]+){3}$/u.test(parsed.hostname);
}

function hasSemanticPrerelease(version: string): boolean {
  return version.split("+", 1)[0]!.includes("-");
}

function materializedCounts(lock: JsonObject, graph?: MaterializationGraph): JsonObject {
  const materialization = graph ?? materializationFromLock(lock);
  return {
    contractBundles: new Set(materialization.bundles.map(value => projectedKey(value, "contractId", "contractVersion"))).size,
    roleScopedUses: new Set(materialization.uses.map(value => projectedKey(value, "role", "contractId", "contractVersion"))).size,
    providers: new Set(materialization.providers.map(value => projectedKey(value, "role", "providerId"))).size,
    packageDependencies: normalizePackageDependencies(materialization.packageDependencies).length,
  };
}

function normalizePackageDependencies(records: readonly JsonObject[]): JsonObject[] {
  const normalized = new Map<string, JsonObject>();
  for (const record of records) {
    const packageId = stringValue(record.packageId, "dependency package id");
    const prior = normalized.get(packageId);
    if (prior === undefined) {
      normalized.set(packageId, cloneJson(record));
      continue;
    }
    if (record.presence === "required") prior.presence = "required";
  }
  return [...normalized.values()].sort((left, right) => ordinal(
    stringValue(left.packageId, "dependency package id"),
    stringValue(right.packageId, "dependency package id"),
  ));
}

function materializationFromLock(lock: JsonObject): MaterializationGraph {
  const artifacts = objectValue(lock.packageArtifacts, "lock packageArtifacts");
  const packageImports = objectValue(lock.packageImports, "lock packageImports");
  const providerImports = objectValue(lock.providerImports, "lock providerImports");
  const platformImports = objectValue(lock.platformImports, "lock platformImports");
  const bundles: JsonObject[] = [];
  const uses: JsonObject[] = [];
  const providers: JsonObject[] = [];
  const packageDependencies: JsonObject[] = [];
  const addSelectedArtifactDependency = (packageId: string, artifact: JsonObject, presence: string): void => {
    packageDependencies.push({
      packageId,
      selectedAuthorityRelease: artifact.selectedPackageVersion!,
      selectedAuthorityContentIdentity: artifact.selectedPackageContentIdentity!,
      presence,
    });
  };
  const addAuthorityDependency = (contract: JsonObject, presence: string): void => {
    if (contract.authorityKind !== "package") return;
    packageDependencies.push({
      packageId: contract.authorityId!,
      selectedAuthorityRelease: contract.selectedAuthorityRelease!,
      selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity!,
      presence,
    });
  };
  const bundleRecord = (contractId: string, contract: JsonObject): JsonObject => {
    const result: JsonObject = {
      contractId,
      contractVersion: contract.resolvedContractVersion!,
      descriptorBytes: contract.descriptorBytes!,
      descriptorSha256: contract.descriptorSha256!,
      abiSha256: contract.abiSha256!,
      exposure: "public",
      artifactSource: contract.artifactSource ?? "imported",
      authorityKind: contract.authorityKind!,
      authorityId: contract.authorityId!,
      selectedAuthorityRelease: contract.selectedAuthorityRelease!,
      selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity!,
    };
    return result;
  };
  for (const [packageId, importValue] of Object.entries(packageImports)) {
    const artifact = objectValue(artifacts[packageId], `materialized artifact '${packageId}'`);
    const intent = objectValue(importValue, "package import");
    const presence = stringValue(intent.presence, "package presence");
    addSelectedArtifactDependency(packageId, artifact, presence);
    for (const [contractId, value] of Object.entries(objectValue(artifact.contracts, "materialized contracts"))) {
      const contract = objectValue(value, "materialized contract");
      if (contract.authorityKind !== "package") continue;
      addAuthorityDependency(contract, presence);
      const bundle = bundleRecord(contractId, contract);
      bundles.push(bundle);
      uses.push({
        role: "runtime",
        ...bundle,
        required: arrayValue(intent.requiredContracts, "required contracts").includes(contractId),
      });
    }
  }
  for (const [key, value] of Object.entries(providerImports)) {
    const provider = objectValue(value, "materialized provider import");
    const artifact = objectValue(artifacts[stringValue(provider.packageId, "materialized provider package")], "provider artifact");
    const contractId = stringValue(provider.contractId, "provider contract id");
    const contract = objectValue(objectValue(artifact.contracts, "provider artifact contracts")[contractId], "provider contract");
    addSelectedArtifactDependency(stringValue(provider.packageId, "materialized provider package"), artifact, "required");
    addAuthorityDependency(contract, "required");
    bundles.push(bundleRecord(contractId, contract));
    providers.push({ role: "runtime", providerId: key, ...bundleRecord(contractId, contract) });
  }
  for (const [key, value] of Object.entries(platformImports)) {
    const platform = objectValue(value, "materialized platform import");
    const contractId = stringValue(platform.contractId, "platform contract id");
    bundles.push(bundleRecord(contractId, platform));
    providers.push({ role: "runtime", providerId: key, ...bundleRecord(contractId, platform) });
  }
  return {
    bundles,
    uses,
    providers,
    packageDependencies,
  };
}

function materializationHasConflict(graph: MaterializationGraph): boolean {
  for (const [records, members] of [
    [graph.bundles, ["contractId", "contractVersion"]],
    [graph.uses, ["role", "contractId", "contractVersion"]],
    [graph.providers, ["role", "providerId"]],
    [graph.packageDependencies, ["packageId"]],
  ] as const) {
    const seen = new Map<string, string>();
    for (const record of records) {
      const key = projectedKey(record, ...members);
      const comparable = cloneJson(record);
      if (records === graph.uses) delete comparable.required;
      if (records === graph.packageDependencies) delete comparable.presence;
      const bytes = JSON.stringify(comparable);
      if (seen.has(key) && seen.get(key) !== bytes) return true;
      seen.set(key, bytes);
    }
  }
  return false;
}

async function baselineDiagnostic(baseline: JsonObject, world: SemanticWorld): Promise<string | null> {
  const authorityKind = stringValue(baseline.authorityKind, "baseline authority kind");
  const authorityId = stringValue(baseline.authorityId, "baseline authority id");
  if (authorityKind === "package" && authorityId !== baseline.packageId) {
    return "rpc-authoring.authority-mismatch";
  }
  const contracts = objectValue(baseline.contracts, "baseline contracts");
  if (!isSortedUnique(Object.keys(contracts))) return "rpc-authoring.order-mismatch";
  const representedIds = new Set<string>();
  const publicationEvents = new Map<string, string>();
  for (const [contractKey, value] of Object.entries(contracts)) {
    const contract = objectValue(value, `baseline contract '${contractKey}'`);
    const contractId = stringValue(contract.contractId, "baseline contract id");
    if (representedIds.has(contractId)) return "rpc-authoring.root-mismatch";
    representedIds.add(contractId);
    const history = arrayValue(contract.history, "baseline history")
      .map((snapshot, index) => objectValue(snapshot, `baseline history ${index}`));
    const current = contract.current === undefined ? undefined : objectValue(contract.current, "baseline current snapshot");
    const snapshots: JsonObject[] = [...history, ...(current === undefined ? [] : [current])];
    const versions = snapshots.map(snapshot => stringValue(snapshot.contractVersion, "snapshot version"));
    const historyVersions = history.map(snapshot => stringValue(snapshot.contractVersion, "history version"));
    if (!historyVersions.every((version, index) => index === 0
      || compareSemanticVersions(historyVersions[index - 1]!, version) < 0)) return "rpc-authoring.order-mismatch";
    if (current !== undefined && historyVersions.length > 0
      && compareSemanticVersions(historyVersions.at(-1)!, stringValue(current.contractVersion, "current version")) >= 0) {
      return "rpc-authoring.order-mismatch";
    }
    if (new Set(versions).size !== versions.length) return "rpc-authoring.order-mismatch";
    for (const snapshot of snapshots) {
      const version = stringValue(snapshot.contractVersion, "snapshot contract version");
      const identityError = await identityDiagnostic(snapshot, contractId, version, world.identities);
      if (identityError !== null) return identityError;
      if (authorityKind === "package") {
        const release = snapshot.packageRelease === undefined
          ? undefined
          : objectValue(snapshot.packageRelease, "package release");
        const publication = snapshot.publication === undefined
          ? undefined
          : objectValue(snapshot.publication, "package publication");
        if ((release !== undefined && release.packageId !== baseline.packageId)
          || (publication !== undefined && publication.packageId !== baseline.packageId)
          || (release !== undefined && publication !== undefined
            && release.packageVersion !== publication.packageVersion)) {
          return "rpc-authoring.evidence-mismatch";
        }
        if (publication !== undefined) {
          if (release === undefined
            || !canonicalRegistryOrigin(stringValue(publication.registryOrigin, "publication registry origin"))
            || !validUtcTimestamp(stringValue(publication.publishedAtUtc, "publishedAtUtc"))
            || publication.etag !== `"${String(snapshot.descriptorSha256)}"`) {
            return "rpc-authoring.evidence-mismatch";
          }
          const eventKey = `${String(publication.packageId)}\0${String(publication.packageVersion)}`;
          const eventValue = JSON.stringify({
            registryOrigin: publication.registryOrigin,
            packageId: publication.packageId,
            packageVersion: publication.packageVersion,
            packageContentIdentity: publication.packageContentIdentity,
            publishedAtUtc: publication.publishedAtUtc,
          });
          if (publicationEvents.has(eventKey) && publicationEvents.get(eventKey) !== eventValue) {
            return "rpc-authoring.evidence-mismatch";
          }
          publicationEvents.set(eventKey, eventValue);
          const matchingInventories = world.contexts.filter(document => {
            if (!authoringContext(document, "package-public-inventory.schema.json")) return false;
            const inventory = objectValue(document.value, document.path);
            return inventory.packageId === publication.packageId
              && inventory.packageVersion === publication.packageVersion
              && inventory.packageContentIdentity === publication.packageContentIdentity;
          });
          if (matchingInventories.length !== 1) return "rpc-authoring.evidence-mismatch";
          world.consumedContexts.add(matchingInventories[0]!.path);
          if (snapshot.exposure === "public") {
            const inventory = objectValue(matchingInventories[0]!.value, matchingInventories[0]!.path);
            const inventoryContract = arrayValue(inventory.contracts, "publication inventory contracts")
              .map(value => objectValue(value, "publication inventory contract"))
              .find(value => value.contractId === contractId && value.contractVersion === snapshot.contractVersion);
            if (inventoryContract === undefined
              || inventoryContract.descriptorBytes !== snapshot.descriptorBytes
              || inventoryContract.descriptorSha256 !== snapshot.descriptorSha256
              || inventoryContract.abiSha256 !== snapshot.abiSha256) return "rpc-authoring.evidence-mismatch";
          }
        }
      } else {
        const release = snapshot.catalogRelease === undefined
          ? undefined
          : objectValue(snapshot.catalogRelease, "catalog release");
        const publication = snapshot.publication === undefined
          ? undefined
          : objectValue(snapshot.publication, "platform publication");
        if ((release !== undefined && release.authorityId !== "sunder.core")
          || (publication !== undefined && publication.authorityId !== "sunder.core")
          || (release !== undefined && publication !== undefined
            && release.releaseVersion !== publication.catalogRelease)) {
          return "rpc-authoring.evidence-mismatch";
        }
        if (publication !== undefined) {
          if (release === undefined
            || !canonicalRegistryOrigin(stringValue(publication.registryOrigin, "platform publication registry origin"))
            || !validUtcTimestamp(stringValue(publication.publishedAtUtc, "platform publishedAtUtc"))
            || publication.etag !== `"${String(snapshot.descriptorSha256)}"`) {
            return "rpc-authoring.evidence-mismatch";
          }
          const provenanceError = await platformProvenanceDiagnostic({
            authorityKind: "platform",
            authorityId: "sunder.core",
            selectedAuthorityRelease: publication.catalogRelease!,
            selectedAuthorityContentIdentity: publication.catalogContentIdentity!,
            contractId,
            contractVersion: snapshot.contractVersion!,
            descriptorBytes: snapshot.descriptorBytes!,
            descriptorSha256: snapshot.descriptorSha256!,
            abiSha256: snapshot.abiSha256!,
          }, world);
          if (provenanceError !== null) return provenanceError;
          const eventKey = `${String(publication.authorityId)}\0${String(publication.catalogRelease)}`;
          const eventValue = JSON.stringify({
            registryOrigin: publication.registryOrigin,
            authorityId: publication.authorityId,
            catalogRelease: publication.catalogRelease,
            catalogContentIdentity: publication.catalogContentIdentity,
            publishedAtUtc: publication.publishedAtUtc,
          });
          if (publicationEvents.has(eventKey) && publicationEvents.get(eventKey) !== eventValue) {
            return "rpc-authoring.evidence-mismatch";
          }
          publicationEvents.set(eventKey, eventValue);
        }
      }
    }
  }
  if (authorityKind === "platform") {
    const mandatory = Object.values(contracts)
      .map(value => objectValue(value, "platform baseline contract"))
      .filter(contract => contract.contractId === "sunder.stack.contributor" && contract.current !== undefined);
    if (mandatory.length !== 1) return "rpc-authoring.mandatory-platform-contract-missing";
  }
  return null;
}

function validUtcTimestamp(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?Z$/u.exec(value);
  if (match === null || match[1] === "0000" || match[7]?.endsWith("0")) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59) return false;
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return day >= 1 && day <= days[month - 1]!;
}

function compareSemanticVersions(left: string, right: string): number {
  assert.ok(isSemanticVersion(left) && isSemanticVersion(right), "History versions must be strict SemVer.");
  const parse = (value: string): { core: bigint[]; prerelease?: string[] } => {
    const withoutBuild = value.split("+", 1)[0]!;
    const separator = withoutBuild.indexOf("-");
    const core = (separator < 0 ? withoutBuild : withoutBuild.slice(0, separator)).split(".").map(BigInt);
    return { core, prerelease: separator < 0 ? undefined : withoutBuild.slice(separator + 1).split(".") };
  };
  const a = parse(left);
  const b = parse(right);
  for (let index = 0; index < 3; index++) {
    if (a.core[index]! < b.core[index]!) return -1;
    if (a.core[index]! > b.core[index]!) return 1;
  }
  if (a.prerelease === undefined) return b.prerelease === undefined ? 0 : 1;
  if (b.prerelease === undefined) return -1;
  for (let index = 0; index < Math.min(a.prerelease.length, b.prerelease.length); index++) {
    const av = a.prerelease[index]!;
    const bv = b.prerelease[index]!;
    const an = /^[0-9]+$/u.test(av);
    const bn = /^[0-9]+$/u.test(bv);
    if (an && bn) {
      if (BigInt(av) < BigInt(bv)) return -1;
      if (BigInt(av) > BigInt(bv)) return 1;
    } else if (an !== bn) return an ? -1 : 1;
    else if (av !== bv) return ordinal(av, bv);
  }
  return Math.sign(a.prerelease.length - b.prerelease.length);
}

async function projectExportDiagnostic(project: JsonObject, world: SemanticWorld): Promise<string | null> {
  const baselineDocuments = resolveContexts(
    world,
    document => authoringContext(document, "owned-contracts.schema.json"),
  );
  if (baselineDocuments.length !== 1) return "rpc-authoring.evidence-mismatch";
  const baseline = objectValue(baselineDocuments[0]!.value, baselineDocuments[0]!.path);
  if (baseline.authorityKind !== "package" || baseline.packageId !== project.packageId
    || baseline.authorityId !== project.packageId) return "rpc-authoring.authority-mismatch";
  const contracts = arrayValue(project.contracts, "project export contracts")
    .map((value, index) => objectValue(value, `project export contract ${index}`));
  const keys = contracts.map(contract => stringValue(contract.contractKey, "export contract key"));
  if (!isSortedUnique(keys)) return "rpc-authoring.order-mismatch";
  const identities = contracts.map(contract => `${String(contract.contractId)}\0${String(contract.contractVersion)}`);
  if (new Set(identities).size !== identities.length) return "rpc-authoring.duplicate-projected-key";
  for (const contract of contracts) {
    if (contract.source !== "owned" || contract.authorityKind !== "package"
      || contract.authorityId !== project.packageId) return "rpc-authoring.authority-mismatch";
    const diagnostic = await identityDiagnostic(
      contract,
      stringValue(contract.contractId, "export contract id"),
      stringValue(contract.contractVersion, "export contract version"),
      world.identities,
    );
    if (diagnostic !== null) return diagnostic;
  }
  const expectedContracts: JsonValue[] = [];
  for (const [contractKey, value] of Object.entries(objectValue(baseline.contracts, "project baseline contracts"))) {
    const contract = objectValue(value, `project baseline contract '${contractKey}'`);
    if (contract.current === undefined) continue;
    const snapshot = objectValue(contract.current, `project baseline current '${contractKey}'`);
    expectedContracts.push({
      contractKey,
      contractId: contract.contractId!,
      contractVersion: snapshot.contractVersion!,
      exposure: snapshot.exposure!,
      source: "owned",
      authorityKind: "package",
      authorityId: project.packageId!,
      descriptorBytes: snapshot.descriptorBytes!,
      canonicalDescriptor: snapshot.canonicalDescriptor!,
      descriptorSha256: snapshot.descriptorSha256!,
      abiSha256: snapshot.abiSha256!,
    });
  }
  if (!jsonEqual(project.contracts!, expectedContracts)) return "rpc-authoring.evidence-mismatch";
  return null;
}

async function cacheReceiptDiagnostic(receipt: JsonObject, world: SemanticWorld): Promise<string | null> {
  const descriptors = resolveContexts(world, document => document.schemaPath === "rpc-v1-descriptor");
  if (descriptors.length !== 1) return "rpc-authoring.evidence-mismatch";
  const evidence = descriptors[0]!;
  const identity = evidence.descriptorIdentity;
  if (identity === undefined) return "rpc-authoring.evidence-mismatch";
  const canonicalBytes = Buffer.from(identity.canonicalDescriptor, "utf8");
  const evidenceSha256 = sha256(evidence.bytes);
  const expectedPath = `~/.sunder/contracts/v1/sha256/${evidenceSha256}.json`;
  if (!evidence.bytes.equals(canonicalBytes)
    || evidence.bytes.byteLength !== identity.descriptorBytes
    || evidenceSha256 !== identity.descriptorSha256
    || evidence.path !== expectedPath) return "rpc-authoring.evidence-mismatch";
  if (receipt.contractId !== identity.descriptor.contractId
    || receipt.contractVersion !== identity.descriptor.version
    || receipt.descriptorBytes !== identity.descriptorBytes
    || receipt.descriptorSha256 !== identity.descriptorSha256
    || receipt.abiSha256 !== identity.abiSha256) return "rpc-authoring.evidence-mismatch";
  return null;
}

function projectedKey(value: JsonObject, ...members: string[]): string {
  return members.map(member => String(value[member])).join("\0");
}

function orderedUniqueProjection(values: JsonObject[], ...members: string[]): "duplicate" | "order" | null {
  const keys = values.map(value => projectedKey(value, ...members));
  if (new Set(keys).size !== keys.length) return "duplicate";
  return keys.every((value, index) => index === 0 || ordinal(keys[index - 1]!, value) < 0) ? null : "order";
}

function descriptorActions(descriptor: RpcContractDescriptor): JsonValue[] {
  const kinds = new Set<string>();
  for (const service of Object.values(descriptor.services)) {
    for (const method of Object.values(service.methods)) kinds.add(method.kind);
  }
  const actions: JsonValue[] = ["discover"];
  if (kinds.has("unary")) actions.push("invoke");
  if (kinds.has("server-stream")) actions.push("subscribe");
  return actions;
}

function importedBundleProjection(contractKey: string, record: JsonObject): JsonObject {
  const projection: JsonObject = {
    contractKey,
    source: "imported",
    authorityKind: record.authorityKind!,
    authorityId: record.authorityId!,
    selectedAuthorityRelease: record.selectedAuthorityRelease!,
    selectedAuthorityContentIdentity: record.selectedAuthorityContentIdentity!,
  };
  return projection;
}

function receiptArtifactInventory(artifact: JsonObject): JsonObject {
  return {
    schemaVersion: 1,
    packageId: artifact.packageId!,
    packageVersion: artifact.selectedPackageVersion!,
    packageContentIdentity: artifact.selectedPackageContentIdentity!,
    publicContractCount: artifact.publicContractCount!,
    contracts: arrayValue(artifact.contracts, "receipt artifact contracts").map(value => {
      const contract = objectValue(value, "receipt artifact contract");
      return inventoryContract(stringValue(contract.contractId, "receipt artifact contract id"), contract);
    }),
  };
}

function receiptArtifactsFromLock(lock: JsonObject): JsonValue[] {
  return Object.entries(objectValue(lock.packageArtifacts, "lock package artifacts")).map(([packageId, value]) => {
    const artifact = objectValue(value, `lock package artifact '${packageId}'`);
    return {
      packageId,
      requestedVersionRange: artifact.requestedVersionRange!,
      includePrerelease: artifact.includePrerelease!,
      selectedPackageVersion: artifact.selectedPackageVersion!,
      selectedPackageContentIdentity: artifact.selectedPackageContentIdentity!,
      ...(artifact.registryOrigin === undefined ? {} : { registryOrigin: artifact.registryOrigin }),
      publicContractCount: artifact.publicContractCount!,
      publicInventorySha256: artifact.publicInventorySha256!,
      contracts: Object.entries(objectValue(artifact.contracts, `lock artifact '${packageId}' contracts`))
        .map(([contractId, contract]): JsonValue => ({
          contractId,
          ...objectValue(contract, `lock artifact contract '${contractId}'`),
        })),
    };
  });
}

function expectedAuthorityConstraints(artifact: JsonObject): Map<string, JsonObject> {
  const result = new Map<string, JsonObject>();
  for (const value of arrayValue(artifact.contracts, "receipt artifact contracts")) {
    const contract = objectValue(value, "receipt artifact contract");
    const key = projectedKey(contract, "authorityKind", "authorityId");
    const projection: JsonObject = {
      authorityKind: contract.authorityKind!,
      authorityId: contract.authorityId!,
      selectedAuthorityRelease: contract.selectedAuthorityRelease!,
      selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity!,
    };
    const prior = result.get(key);
    if (prior !== undefined && JSON.stringify(prior) !== JSON.stringify(projection)) {
      throw new Error(`Conflicting receipt authority projection '${key}'.`);
    }
    result.set(key, projection);
  }
  return result;
}

async function receiptDiagnostic(receipt: JsonObject, world: SemanticWorld): Promise<string | null> {
  const lockDocuments = resolveContexts(world, document => authoringContext(document, "contract-lock.schema.json"));
  if (lockDocuments.length !== 1) return "rpc-authoring.evidence-mismatch";
  const lockDocument = lockDocuments[0]!;
  const lock = objectValue(lockDocument.value, lockDocument.path);
  const lockError = await lockDiagnostic(lock, world);
  if (lockError !== null) return lockError;
  if (receipt.lockSha256 !== sha256(lockDocument.bytes)) return "rpc-authoring.hash-mismatch";
  if (!jsonEqual(receipt.packageArtifacts!, receiptArtifactsFromLock(lock))) {
    return "rpc-authoring.evidence-mismatch";
  }
  const collectionSpecifications: ReadonlyArray<readonly [string, readonly string[]]> = [
    ["contracts", ["contractKey"]],
    ["packageArtifacts", ["packageId"]],
    ["bundles", ["contractKey"]],
    ["packageArtifactImports", ["packageId"]],
    ["packageApiImports", ["packageId"]],
    ["providerImports", ["key"]],
    ["platformImports", ["key"]],
    ["providers", ["role", "providerId"]],
    ["uses", ["role", "contractKey"]],
    ["generatedPackageFacades", ["packageId"]],
    ["generatedBindings", ["contractKey"]],
  ];
  for (const [name, members] of collectionSpecifications) {
    const values = arrayValue(receipt[name], `receipt.${name}`)
      .map((value, index) => objectValue(value, `receipt.${name}[${index}]`));
    const state = orderedUniqueProjection(values, ...members);
    if (state === "duplicate") return "rpc-authoring.duplicate-projected-key";
    if (state === "order") return "rpc-authoring.order-mismatch";
  }
  const contracts = arrayValue(receipt.contracts, "receipt contracts")
    .map(value => objectValue(value, "receipt contract"));
  const contractTuples = contracts.map(contract => projectedKey(contract, "contractId", "contractVersion"));
  if (new Set(contractTuples).size !== contractTuples.length) return "rpc-authoring.duplicate-projected-key";
  const contractByKey = new Map<string, JsonObject>();
  const identityByContractKey = new Map<string, DescriptorIdentity>();
  for (const contract of contracts) {
    const contractKey = stringValue(contract.contractKey, "receipt contract key");
    contractByKey.set(contractKey, contract);
    const diagnostic = await identityDiagnostic(
      contract,
      stringValue(contract.contractId, "receipt contract id"),
      stringValue(contract.contractVersion, "receipt contract version"),
      world.identities,
    );
    if (diagnostic !== null) return diagnostic;
    identityByContractKey.set(
      contractKey,
      await descriptorIdentity(stringValue(contract.canonicalDescriptor, "receipt canonical descriptor")),
    );
  }

  const artifactImports = new Map(
    arrayValue(receipt.packageArtifactImports, "receipt packageArtifactImports")
      .map(value => objectValue(value, "receipt packageArtifactImport"))
      .map(value => [stringValue(value.packageId, "artifact import package id"), value] as const),
  );
  const inventorySchema = world.schemas.get("package-public-inventory.schema.json");
  assert.ok(inventorySchema);
  for (const value of arrayValue(receipt.packageArtifacts, "receipt packageArtifacts")) {
    const artifact = objectValue(value, "receipt packageArtifact");
    const packageId = stringValue(artifact.packageId, "receipt artifact package id");
    const contractsInArtifact = arrayValue(artifact.contracts, "receipt artifact contracts")
      .map(item => objectValue(item, "receipt artifact contract"));
    if (artifact.publicContractCount !== contractsInArtifact.length) return "rpc-authoring.count-mismatch";
    const artifactKeys = contractsInArtifact.map(contract => projectedKey(contract, "contractId", "resolvedContractVersion"));
    if (!isSortedUnique(artifactKeys)) return "rpc-authoring.order-mismatch";
    const inventory = receiptArtifactInventory(artifact);
    const inventoryDigest = domainHash(INVENTORY_DOMAIN, writeCompact(inventory, inventorySchema.root, inventorySchema.root));
    if (artifact.publicInventorySha256 !== inventoryDigest) return "rpc-authoring.hash-mismatch";
    for (const contract of contractsInArtifact) {
      const diagnostic = await identityDiagnostic(
        contract,
        stringValue(contract.contractId, "artifact contract id"),
        stringValue(contract.resolvedContractVersion, "artifact contract version"),
        world.identities,
      );
      if (diagnostic !== null) return diagnostic;
      const provenanceError = await platformProvenanceDiagnostic(contract, world);
      if (provenanceError !== null) return provenanceError;
    }
    const artifactImport = artifactImports.get(packageId);
    if (artifactImport === undefined) return "rpc-authoring.authority-projection-omission";
    for (const member of [
      "requestedVersionRange",
      "includePrerelease",
      "selectedPackageVersion",
      "selectedPackageContentIdentity",
      "publicContractCount",
      "publicInventorySha256",
    ]) {
      if (artifactImport[member] !== artifact[member]) return "rpc-authoring.evidence-mismatch";
    }
    const expected = expectedAuthorityConstraints(artifact);
    const actualValues = arrayValue(artifactImport.authorityConstraints, "authorityConstraints")
      .map(item => objectValue(item, "authorityConstraint"));
    const actualKeys = actualValues.map(item => projectedKey(item, "authorityKind", "authorityId"));
    if (!isSortedUnique(actualKeys)) return "rpc-authoring.duplicate-projected-key";
    const actual = new Map(actualValues.map(item => [projectedKey(item, "authorityKind", "authorityId"), item] as const));
    if ([...actual.keys()].some(key => !expected.has(key))) return "rpc-authoring.authority-projection-extra";
    if ([...expected.keys()].some(key => !actual.has(key))) return "rpc-authoring.authority-projection-omission";
    for (const [key, expectedValue] of expected) {
      if (JSON.stringify(actual.get(key)) !== JSON.stringify(expectedValue)) {
        return "rpc-authoring.authority-mismatch";
      }
    }
  }
  if (artifactImports.size !== arrayValue(receipt.packageArtifacts, "receipt packageArtifacts").length) {
    return "rpc-authoring.authority-projection-extra";
  }

  const packageApiGroups = new Map(arrayValue(receipt.packageApiImports, "receipt packageApiImports")
    .map(value => objectValue(value, "receipt packageApiImport"))
    .map(value => [stringValue(value.packageId, "package API package id"), value] as const));
  const facades = new Map(arrayValue(receipt.generatedPackageFacades, "receipt generatedPackageFacades")
    .map(value => objectValue(value, "generated package facade"))
    .map(value => [stringValue(value.packageId, "facade package id"), value] as const));
  const receiptUses = arrayValue(receipt.uses, "receipt uses").map(value => objectValue(value, "receipt use"));
  const bundles = arrayValue(receipt.bundles, "receipt bundles")
    .map(value => objectValue(value, "receipt bundle"));
  const packageImports = objectValue(lock.packageImports, "lock packageImports");
  const expectedUsesByTuple = new Map<string, { contractId: string; contractVersion: string; required: boolean }>();
  const selectedImportEvidence = new Map<string, JsonObject[]>();
  const addSelectedImportEvidence = (
    contractId: string,
    contractVersion: string,
    evidence: JsonObject,
  ): void => {
    const tuple = `${contractId}\0${contractVersion}`;
    const records = selectedImportEvidence.get(tuple) ?? [];
    records.push(evidence);
    selectedImportEvidence.set(tuple, records);
  };
  if (packageApiGroups.size !== Object.keys(packageImports).length || facades.size !== Object.keys(packageImports).length) {
    return "rpc-authoring.evidence-mismatch";
  }
  for (const [packageId, value] of Object.entries(packageImports)) {
    const intent = objectValue(value, `lock package import '${packageId}'`);
    const group = packageApiGroups.get(packageId);
    const facade = facades.get(packageId);
    if (group === undefined || facade === undefined || facade.localName !== intent.localName
      || group.presence !== intent.presence) {
      return "rpc-authoring.evidence-mismatch";
    }
    const artifact = objectValue(objectValue(lock.packageArtifacts, "lock packageArtifacts")[packageId], `artifact '${packageId}'`);
    const importedContracts = Object.entries(objectValue(artifact.contracts, `artifact '${packageId}' contracts`))
      .filter(([, contract]) => objectValue(contract, "package API contract").authorityKind === "package")
      .map(([contractId, contract]) => ({ contractId, contract: objectValue(contract, "package API contract") }));
    for (const { contractId, contract } of importedContracts) {
      addSelectedImportEvidence(
        contractId,
        stringValue(contract.resolvedContractVersion, "package API contract version"),
        contract,
      );
    }
    const expectedBundleRefs: JsonValue[] = importedContracts.map(({ contractId, contract }) => ({
      contractId,
      contractVersion: contract.resolvedContractVersion!,
    })).sort((left, right) => ordinal(
      projectedKey(objectValue(left, "expected bundle ref"), "contractId", "contractVersion"),
      projectedKey(objectValue(right, "expected bundle ref"), "contractId", "contractVersion"),
    ));
    if (!jsonEqual(group.bundleRefs!, expectedBundleRefs)) return "rpc-authoring.evidence-mismatch";
    const requiredContracts = new Set(arrayValue(intent.requiredContracts, "required contracts")
      .map(item => stringValue(item, "required contract")));
    const expectedUseRefs: JsonValue[] = importedContracts.map(({ contractId, contract }) => {
      const contractVersion = stringValue(contract.resolvedContractVersion, "package API contract version");
      const tuple = `${contractId}\0${contractVersion}`;
      const prior = expectedUsesByTuple.get(tuple);
      expectedUsesByTuple.set(tuple, {
        contractId,
        contractVersion,
        required: (prior?.required ?? false) || requiredContracts.has(contractId),
      });
      return { role: "runtime", contractId, contractVersion };
    });
    expectedUseRefs.sort((left, right) => ordinal(
      projectedKey(objectValue(left, "expected use ref"), "role", "contractId", "contractVersion"),
      projectedKey(objectValue(right, "expected use ref"), "role", "contractId", "contractVersion"),
    ));
    if (!jsonEqual(group.useRefs!, expectedUseRefs)) return "rpc-authoring.evidence-mismatch";
  }
  const lockProviderImports = objectValue(lock.providerImports, "lock providerImports");
  for (const value of Object.values(lockProviderImports)) {
    const intent = objectValue(value, "lock provider import");
    const packageId = stringValue(intent.packageId, "provider package id");
    const contractId = stringValue(intent.contractId, "provider contract id");
    const artifact = objectValue(objectValue(lock.packageArtifacts, "lock package artifacts")[packageId], "provider package artifact");
    const contract = objectValue(objectValue(artifact.contracts, "provider package contracts")[contractId], "provider package contract");
    addSelectedImportEvidence(
      contractId,
      stringValue(contract.resolvedContractVersion, "provider contract version"),
      contract,
    );
  }
  const lockPlatformImports = objectValue(lock.platformImports, "lock platformImports");
  for (const value of Object.values(lockPlatformImports)) {
    const intent = objectValue(value, "lock platform import");
    addSelectedImportEvidence(
      stringValue(intent.contractId, "platform contract id"),
      stringValue(intent.resolvedContractVersion, "platform contract version"),
      intent,
    );
  }
  const bundleByContract = new Map(bundles.map(bundle => [stringValue(bundle.contractKey, "bundle contract key"), bundle] as const));
  if (bundleByContract.size !== contractByKey.size) return "rpc-authoring.orphan-import";
  const derivedBundleSources = new Map<string, "owned" | "imported">();
  for (const contract of contracts) {
    const contractKey = stringValue(contract.contractKey, "bundle contract key");
    const bundle = bundleByContract.get(contractKey);
    if (bundle === undefined) return "rpc-authoring.orphan-import";
    const tuple = projectedKey(contract, "contractId", "contractVersion");
    const importEvidence = selectedImportEvidence.get(tuple) ?? [];
    if (importEvidence.length === 0) {
      if (bundle.source === "imported") return "rpc-authoring.orphan-import";
      if (!jsonEqual(bundle, {
        contractKey,
        source: "owned",
        authorityKind: "package",
        authorityId: receipt.packageId!,
      })) return "rpc-authoring.authority-mismatch";
      derivedBundleSources.set(contractKey, "owned");
      continue;
    }
    if (contract.exposure !== "public") return "rpc-authoring.visibility-audience-mismatch";
    if (importEvidence.some(evidence => evidence.descriptorBytes !== contract.descriptorBytes
      || evidence.descriptorSha256 !== contract.descriptorSha256
      || evidence.abiSha256 !== contract.abiSha256)) return "rpc-authoring.evidence-mismatch";
    const expectedBundles = importEvidence.map(evidence => importedBundleProjection(contractKey, evidence));
    if (expectedBundles.some(expected => !jsonEqual(expected, expectedBundles[0]!))
      || !jsonEqual(bundle, expectedBundles[0]!)) return "rpc-authoring.evidence-mismatch";
    derivedBundleSources.set(contractKey, "imported");
  }
  const expectedReceiptUses: JsonValue[] = [];
  for (const [tuple, expected] of expectedUsesByTuple) {
    const contract = contracts.find(candidate => projectedKey(candidate, "contractId", "contractVersion") === tuple);
    if (contract === undefined) return "rpc-authoring.orphan-import";
    const contractKey = stringValue(contract.contractKey, "expected use contract key");
    const identity = identityByContractKey.get(contractKey);
    assert.ok(identity);
    expectedReceiptUses.push({
      role: "runtime",
      required: expected.required,
      actions: descriptorActions(identity.descriptor),
      contractKey,
    });
  }
  expectedReceiptUses.sort((left, right) => ordinal(
    projectedKey(objectValue(left, "expected receipt use"), "role", "contractKey"),
    projectedKey(objectValue(right, "expected receipt use"), "role", "contractKey"),
  ));
  const unmatchedPackageUses = new Map(expectedReceiptUses.map(value => {
    const use = objectValue(value, "expected receipt use");
    return [projectedKey(use, "role", "contractKey"), use] as const;
  }));
  for (const use of receiptUses) {
    const contractKey = stringValue(use.contractKey, "use contract key");
    const contract = contractByKey.get(contractKey);
    const identity = identityByContractKey.get(contractKey);
    if (contract === undefined || identity === undefined) return "rpc-authoring.orphan-import";
    const useKey = projectedKey(use, "role", "contractKey");
    const expectedPackageUse = unmatchedPackageUses.get(useKey);
    if (expectedPackageUse !== undefined) {
      if (!jsonEqual(use.actions!, expectedPackageUse.actions!)) {
        return "rpc-authoring.descriptor-action-mismatch";
      }
      if (!jsonEqual(use, expectedPackageUse)) return "rpc-authoring.evidence-mismatch";
      unmatchedPackageUses.delete(useKey);
      continue;
    }
    if (derivedBundleSources.get(contractKey) !== "owned") {
      return "rpc-authoring.evidence-mismatch";
    }
    const supportedActions = descriptorActions(identity.descriptor);
    const actions = arrayValue(use.actions, "local use actions");
    const canonicalSubset = supportedActions.filter(action => actions.includes(action));
    if (actions.length === 0 || !jsonEqual(actions, canonicalSubset)) {
      return "rpc-authoring.descriptor-action-mismatch";
    }
  }
  if (unmatchedPackageUses.size !== 0) return "rpc-authoring.evidence-mismatch";

  const receiptProviders = arrayValue(receipt.providers, "receipt providers").map(value => objectValue(value, "receipt provider"));
  const importedProviderReferences = new Set<string>();
  const providerRefsDiagnostic = (
    group: JsonObject,
    expectedContractId: JsonValue,
    expectedVersion: JsonValue,
  ): string | null => {
    const refs = arrayValue(group.providerRefs, "provider refs").map(value => objectValue(value, "provider ref"));
    const state = orderedUniqueProjection(refs, "role", "providerId");
    if (state === "duplicate") return "rpc-authoring.duplicate-projected-key";
    if (state === "order") return "rpc-authoring.order-mismatch";
    const expectedRefs: JsonValue[] = receiptProviders
      .filter(provider => {
        const contract = contractByKey.get(stringValue(provider.contractKey, "provider contract key"));
        return contract?.contractId === expectedContractId && contract.contractVersion === expectedVersion;
      })
      .map(provider => ({
        role: provider.role!,
        providerId: provider.providerId!,
        contractId: expectedContractId,
        contractVersion: expectedVersion,
      }));
    if (expectedRefs.length === 0 || !jsonEqual(refs, expectedRefs)) return "rpc-authoring.evidence-mismatch";
    for (const reference of refs) {
      importedProviderReferences.add(projectedKey(reference, "role", "providerId", "contractId", "contractVersion"));
    }
    return null;
  };
  const providerGroups = new Map(arrayValue(receipt.providerImports, "receipt providerImports")
    .map(value => objectValue(value, "receipt provider import"))
    .map(value => [stringValue(value.key, "provider import key"), value] as const));
  if (providerGroups.size !== Object.keys(lockProviderImports).length) return "rpc-authoring.evidence-mismatch";
  for (const [key, value] of Object.entries(lockProviderImports)) {
    const intent = objectValue(value, `lock provider import '${key}'`);
    const group = providerGroups.get(key);
    if (group === undefined || group.packageId !== intent.packageId || group.contractId !== intent.contractId) {
      return "rpc-authoring.evidence-mismatch";
    }
    const artifact = objectValue(objectValue(lock.packageArtifacts, "lock packageArtifacts")[stringValue(intent.packageId, "provider package id")], "provider artifact");
    const contract = objectValue(objectValue(artifact.contracts, "provider contracts")[stringValue(intent.contractId, "provider contract id")], "provider contract");
    if (group.contractVersion !== contract.resolvedContractVersion
      || !jsonEqual(group.bundleRef!, { contractId: intent.contractId!, contractVersion: contract.resolvedContractVersion! })) {
      return "rpc-authoring.evidence-mismatch";
    }
    const providerRefsError = providerRefsDiagnostic(group, intent.contractId!, contract.resolvedContractVersion!);
    if (providerRefsError !== null) return providerRefsError;
  }

  const platformGroups = new Map(arrayValue(receipt.platformImports, "receipt platformImports")
    .map(value => objectValue(value, "receipt platform import"))
    .map(value => [stringValue(value.key, "platform import key"), value] as const));
  if (platformGroups.size !== Object.keys(lockPlatformImports).length) return "rpc-authoring.evidence-mismatch";
  for (const [key, value] of Object.entries(lockPlatformImports)) {
    const intent = objectValue(value, `lock platform import '${key}'`);
    const group = platformGroups.get(key);
    if (group === undefined) return "rpc-authoring.evidence-mismatch";
    for (const member of [
      "authorityKind",
      "authorityId",
      "authorityReleaseRange",
      "includePrerelease",
      "selectedAuthorityRelease",
      "selectedAuthorityContentIdentity",
      "contractId",
    ]) {
      if (group[member] !== intent[member]) return "rpc-authoring.evidence-mismatch";
    }
    if (group.contractVersion !== intent.resolvedContractVersion
      || !jsonEqual(group.bundleRef!, { contractId: intent.contractId!, contractVersion: intent.resolvedContractVersion! })) {
      return "rpc-authoring.evidence-mismatch";
    }
    const providerRefsError = providerRefsDiagnostic(group, intent.contractId!, intent.resolvedContractVersion!);
    if (providerRefsError !== null) return providerRefsError;
  }

  const receiptAuthorityRecords: JsonObject[] = [];
  for (const artifactValue of arrayValue(receipt.packageArtifacts, "receipt package artifacts")) {
    const artifact = objectValue(artifactValue, "receipt package artifact");
    receiptAuthorityRecords.push({
      authorityKind: "package",
      authorityId: artifact.packageId!,
      selectedAuthorityRelease: artifact.selectedPackageVersion!,
      selectedAuthorityContentIdentity: artifact.selectedPackageContentIdentity!,
    });
    receiptAuthorityRecords.push(...arrayValue(artifact.contracts, "receipt artifact contracts")
      .map(value => objectValue(value, "receipt artifact contract")));
  }
  for (const importValue of arrayValue(receipt.packageArtifactImports, "receipt artifact imports")) {
    receiptAuthorityRecords.push(...arrayValue(
      objectValue(importValue, "receipt artifact import").authorityConstraints,
      "receipt authority constraints",
    ).map(value => objectValue(value, "receipt authority constraint")));
  }
  receiptAuthorityRecords.push(...bundles.filter(bundle =>
    derivedBundleSources.get(stringValue(bundle.contractKey, "authority bundle contract key")) === "imported"));
  receiptAuthorityRecords.push(...arrayValue(receipt.platformImports, "receipt platform imports")
    .map(value => objectValue(value, "receipt platform import")));
  const receiptAuthorityError = authorityAgreementDiagnostic(receiptAuthorityRecords);
  if (receiptAuthorityError !== null) return receiptAuthorityError;
  const generatedBindingKeys = arrayValue(receipt.generatedBindings, "receipt generatedBindings")
    .map(value => stringValue(objectValue(value, "generated binding").contractKey, "generated binding contract key"));
  if (!jsonEqual(generatedBindingKeys, [...contractByKey.keys()])) return "rpc-authoring.evidence-mismatch";
  const referencedImports = new Set<string>();
  for (const groupName of ["packageApiImports", "providerImports", "platformImports"]) {
    for (const value of arrayValue(receipt[groupName], `receipt.${groupName}`)) {
      const group = objectValue(value, `${groupName} group`);
      if (groupName === "packageApiImports") {
        for (const reference of arrayValue(group.bundleRefs, "package API bundle refs")) {
          const item = objectValue(reference, "package API bundle ref");
          referencedImports.add(projectedKey(item, "contractId", "contractVersion"));
        }
      } else {
        const item = objectValue(group.bundleRef, `${groupName} bundle ref`);
        referencedImports.add(projectedKey(item, "contractId", "contractVersion"));
      }
    }
  }
  for (const bundle of bundles.filter(value =>
    derivedBundleSources.get(stringValue(value.contractKey, "referenced bundle contract key")) === "imported")) {
    const contract = contractByKey.get(stringValue(bundle.contractKey, "imported bundle key"))!;
    if (!referencedImports.has(projectedKey(contract, "contractId", "contractVersion"))) {
      return "rpc-authoring.orphan-import";
    }
    const contractId = stringValue(contract.contractId, "imported contract id");
    if (contract.contractKey !== contractId) return "rpc-authoring.evidence-mismatch";
    const symbol = `C${contractId.split(".").map(segment => segment[0]!.toUpperCase() + segment.slice(1)).join("_")}`;
    const binding = arrayValue(receipt.generatedBindings, "receipt generatedBindings")
      .map(value => objectValue(value, "generated binding"))
      .find(value => value.contractKey === contractId)!;
    if (binding.logicalName !== `Sunder.Generated.Rpc.Imports.${symbol}`) return "rpc-authoring.evidence-mismatch";
  }
  for (const value of arrayValue(receipt.providers, "receipt providers")) {
    const provider = objectValue(value, "receipt provider");
    const contractKey = stringValue(provider.contractKey, "provider contract key");
    const contract = contractByKey.get(contractKey);
    if (contract === undefined) return "rpc-authoring.orphan-import";
    if (provider.audience === "public" && contract.exposure !== "public") {
      return "rpc-authoring.visibility-audience-mismatch";
    }
    if (derivedBundleSources.get(contractKey) === "imported") {
      const reference: JsonObject = {
        role: provider.role!,
        providerId: provider.providerId!,
        contractId: contract.contractId!,
        contractVersion: contract.contractVersion!,
      };
      if (!importedProviderReferences.has(projectedKey(
        reference,
        "role",
        "providerId",
        "contractId",
        "contractVersion",
      ))) return "rpc-authoring.evidence-mismatch";
    }
  }
  return null;
}

async function semanticDiagnosticForDocument(
  schemaPath: string,
  value: JsonValue,
  world: SemanticWorld,
): Promise<string | null> {
  if (schemaPath === "rpc-v1-descriptor") return null;
  const document = objectValue(value, schemaPath);
  const fileName = schemaFileName(schemaPath);
  if (fileName === "contract-lock.schema.json") return lockDiagnostic(document, world);
  if (fileName === "owned-contracts.schema.json") return baselineDiagnostic(document, world);
  if (fileName === "package-public-inventory.schema.json") return inventoryDiagnostic(document, world);
  if (fileName === "package-project-export.schema.json") return projectExportDiagnostic(document, world);
  if (fileName === "cache-entry-receipt.schema.json") return cacheReceiptDiagnostic(document, world);
  if (fileName === "authoring-receipt.schema.json") return receiptDiagnostic(document, world);
  return catalogDiagnostic(document, world);
}

async function semanticDiagnostics(
  loaded: LoadedCase,
  contexts: readonly LoadedDocument[],
  schemas: ReadonlyMap<string, CompiledSchema>,
  canonicalIdentities: ReadonlyMap<string, DescriptorIdentity>,
  immutableArtifacts: readonly ImmutableArtifactEvidence[],
): Promise<{ diagnostics: string[]; consumedContexts: Set<string> }> {
  const identities = new Map(canonicalIdentities);
  for (const [key, value] of loaded.identities) identities.set(key, value);
  const main: LoadedDocument = {
    path: loaded.expectation.fixturePath ?? `recipe:${String(loaded.expectation.recipe?.id)}`,
    schemaPath: loaded.expectation.schemaPath,
    value: loaded.value,
    bytes: loaded.bytes ?? Buffer.from("", "utf8"),
  };
  const allContexts = [...contexts, ...(loaded.generatedContexts ?? [])];
  const selectedImmutableArtifacts = immutableArtifacts.filter(evidence =>
    allContexts.some(context => context.path === evidence.inventoryFixturePath));
  for (const context of allContexts) {
    if (context.schemaPath !== "rpc-v1-descriptor") {
      const validate = validatorFor(schemas, context.schemaPath);
      assert.equal(validate(context.value), true, `Semantic context '${context.path}' is structurally invalid: ${JSON.stringify(validate.errors)}`);
    } else {
      assert.ok(context.descriptorIdentity, `Descriptor context '${context.path}' has no validated identity.`);
    }
    const contextDiagnostic = await semanticDiagnosticForDocument(context.schemaPath, context.value, {
      schemas,
      contexts: allContexts.filter(candidate => candidate !== context),
      consumedContexts: new Set(),
      identities,
      immutableArtifacts: selectedImmutableArtifacts,
    });
    assert.equal(contextDiagnostic, null, `Semantic context '${context.path}' failed independently with '${contextDiagnostic}'.`);
  }
  const consumedContexts = new Set<string>();
  const world: SemanticWorld = {
    schemas,
    contexts: allContexts,
    consumedContexts,
    identities,
    immutableArtifacts: selectedImmutableArtifacts,
    materialization: loaded.materialization,
  };
  const diagnostic = await semanticDiagnosticForDocument(main.schemaPath, main.value, world);
  const diagnostics = diagnostic === null ? [] : [diagnostic];
  return { diagnostics, consumedContexts };
}

async function jsonPaths(directory: string, prefix = ""): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = prefix === "" ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) result.push(...await jsonPaths(resolve(directory, entry.name), path));
    else if (entry.isFile() && entry.name.endsWith(".json")) result.push(path);
  }
  return result.sort(ordinal);
}

function assertStandaloneReferences(value: JsonValue, path: string): void {
  if (Array.isArray(value)) {
    value.forEach((item, index) => assertStandaloneReferences(item, `${path}[${index}]`));
    return;
  }
  if (!isObject(value)) return;
  if (value.$ref !== undefined) {
    assert.equal(typeof value.$ref, "string", `${path}.$ref`);
    assert.ok((value.$ref as string).startsWith("#/$defs/"), `${path} has external $ref '${String(value.$ref)}'.`);
  }
  for (const [key, child] of Object.entries(value)) assertStandaloneReferences(child, `${path}.${key}`);
}

test("Node independently enforces the durable RPC V1 authoring schema oracle", async () => {
  const expectationBytes = await readFile(EXPECTATIONS_PATH);
  const expectations = parseOuterJson(expectationBytes, 4 * 1024 * 1024, 64) as unknown as AuthoringExpectations;
  assert.equal(expectations.formatVersion, 1);
  assert.equal(expectations.schemaDialect, SCHEMA_DIALECT);
  assert.equal(JSON.stringify(expectations.documentEncoding), JSON.stringify({
    encoding: "utf-8",
    bom: false,
    indentSpaces: 2,
    lineEnding: "lf",
    terminalLf: true,
  }));
  assert.equal(Buffer.from(INVENTORY_DOMAIN, "utf8").toString("hex"), "73756e6465722e7061636b6167652d7075626c69632d6170692d696e76656e746f72792e763100");

  const schemas = await compileSchemas();
  for (const [name, schema] of schemas) assertStandaloneReferences(schema.root, name);
  const canonical = await loadCanonicalIdentities(expectations);
  const immutableArtifactKeys = expectations.immutableArtifactEvidence.map(evidence => evidence.inventoryFixturePath);
  assert.ok(isSortedUnique(immutableArtifactKeys), "Immutable artifact evidence must have unique canonical ordering.");
  for (const evidence of expectations.immutableArtifactEvidence) {
    assert.match(evidence.inventoryFixturePath, /^valid\/.+\.json$/u);
    assert.ok(isSemanticVersion(evidence.packageVersion), `Invalid immutable package version '${evidence.packageVersion}'.`);
    assert.match(evidence.packageContentIdentity, /^[0-9a-f]{64}$/u);
    assert.ok(Number.isSafeInteger(evidence.publicContractCount) && evidence.publicContractCount >= 0 && evidence.publicContractCount <= 128);
    assert.match(evidence.publicInventorySha256, /^[0-9a-f]{64}$/u);
  }
  const indexedPaths = new Set<string>();
  const documents = new Map<string, LoadedDocument>();
  const cases: LoadedCase[] = [];
  const representation = new Map<string, { valid: boolean; invalid: boolean }>();

  for (const fixture of expectations.fixtures) {
    assert.equal(fixture.fixturePath === undefined, fixture.recipe !== undefined, "Each case must have exactly one fixture or recipe.");
    const name = schemaFileName(fixture.schemaPath);
    const prior = representation.get(name) ?? { valid: false, invalid: false };
    representation.set(name, {
      valid: prior.valid || fixture.schemaValid,
      invalid: prior.invalid || !fixture.schemaValid,
    });
    if (fixture.fixturePath !== undefined) {
      assert.equal(indexedPaths.has(fixture.fixturePath), false, `Duplicate fixture index '${fixture.fixturePath}'.`);
      indexedPaths.add(fixture.fixturePath);
      const document = await loadFixtureDocument(fixture.fixturePath, fixture.schemaPath);
      documents.set(fixture.fixturePath, document);
      cases.push({ expectation: fixture, value: document.value, bytes: document.bytes, identities: new Map() });
    } else {
      const generated = await generateRecipe(fixture.recipe!, schemas, canonical.byName);
      cases.push({ expectation: fixture, ...generated });
    }
  }
  assert.equal(cases.length, EXPECTED_AUTHORING_CASE_COUNT, "Authoring case count drifted.");
  const validInventoryPaths = cases
    .filter(loaded => loaded.expectation.fixturePath?.startsWith("valid/") === true
      && schemaFileName(loaded.expectation.schemaPath) === "package-public-inventory.schema.json")
    .map(loaded => loaded.expectation.fixturePath!);
  assert.deepEqual(immutableArtifactKeys, validInventoryPaths.sort(ordinal));
  const inventorySchema = schemas.get("package-public-inventory.schema.json");
  assert.ok(inventorySchema);
  for (const evidence of expectations.immutableArtifactEvidence) {
    const document = documents.get(evidence.inventoryFixturePath);
    assert.ok(document, `Missing immutable inventory fixture '${evidence.inventoryFixturePath}'.`);
    const inventory = objectValue(document.value, evidence.inventoryFixturePath);
    assert.equal(inventory.packageId, evidence.packageId);
    assert.equal(inventory.packageVersion, evidence.packageVersion);
    assert.equal(inventory.packageContentIdentity, evidence.packageContentIdentity);
    assert.equal(inventory.publicContractCount, evidence.publicContractCount);
    assert.equal(
      domainHash(INVENTORY_DOMAIN, writeCompact(inventory, inventorySchema.root, inventorySchema.root)),
      evidence.publicInventorySha256,
    );
  }
  const frozenIndex = {
    immutableArtifactEvidence: expectations.immutableArtifactEvidence,
    cases: cases.map(loaded => {
      const expected = loaded.expectation;
      const compiled = schemas.get(schemaFileName(expected.schemaPath));
      assert.ok(compiled);
      const generatedBytes = loaded.bytes ?? Buffer.from(
        writeDocument(loaded.value, schemaAtPath(compiled, expected.schemaPath), compiled.root),
        "utf8",
      );
      return {
        schemaPath: expected.schemaPath,
        case: expected.fixturePath ?? `recipe:${String(expected.recipe?.id)}`,
        sourceSha256: sha256(generatedBytes),
        recipeSha256: expected.recipe === undefined ? null : sha256(JSON.stringify(expected.recipe)),
        semanticContextFixturePaths: expected.semanticContextFixturePaths ?? [],
        semanticDescriptorContexts: expected.semanticDescriptorContexts ?? [],
        schemaValid: expected.schemaValid,
        expectedSchemaErrors: expected.expectedSchemaErrors ?? [],
        semanticValid: expected.semanticValid,
        expectedSemanticDiagnosticId: expected.expectedSemanticDiagnosticId,
      };
    }),
  };
  assert.equal(
    sha256(JSON.stringify(frozenIndex)),
    EXPECTED_AUTHORING_INDEX_SHA256,
    "Authoring path/hash/diagnostic index drifted.",
  );

  const actualFixturePaths = (await jsonPaths(conformanceRoot)).filter(path => path !== "expectations.json");
  assert.deepEqual(actualFixturePaths, [...indexedPaths].sort(ordinal));
  for (const name of schemas.keys()) {
    const represented = representation.get(name);
    assert.ok(represented?.valid, `Schema '${name}' has no valid indexed case.`);
    assert.ok(represented.invalid, `Schema '${name}' has no invalid indexed case.`);
  }

  const corpusPaths = [
    ...(await readdir(authoringRoot)).filter(path => path.endsWith(".schema.json")),
    ...(await jsonPaths(conformanceRoot)).map(path => `conformance/${path}`),
  ].sort(ordinal);
  for (const path of corpusPaths) {
    const bytes = await readFile(resolve(authoringRoot, path));
    assert.ok(bytes.byteLength > 0, path);
    assert.equal(bytes.at(-1), 0x0a, `${path} must have one terminal LF.`);
    assert.equal(bytes.includes(0x0d), false, `${path} must use LF line endings.`);
    assert.equal(bytes.some(value => value > 0x7f), false, `${path} must be ASCII corpus data.`);
    assert.equal(bytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])), false, `${path} must not have a BOM.`);
    const value = parseOuterJson(bytes, bytes.byteLength, 512);
    assert.equal(bytes.toString("utf8"), `${JSON.stringify(value, null, 2)}\n`, `${path} must use deterministic two-space JSON.`);
  }

  for (const loaded of cases) {
    const fixture = loaded.expectation;
    const validate = validatorFor(schemas, fixture.schemaPath);
    const valid = validate(loaded.value) as boolean;
    assert.equal(valid, fixture.schemaValid, fixture.fixturePath ?? String(fixture.recipe?.id));
    if (!fixture.schemaValid) {
      assert.ok(fixture.expectedSchemaKeyword, "Invalid structural cases must name their expected keyword.");
      assert.ok(
        validate.errors?.some(error => error.keyword === fixture.expectedSchemaKeyword),
        `${fixture.fixturePath ?? String(fixture.recipe?.id)} expected '${fixture.expectedSchemaKeyword}': ${JSON.stringify(validate.errors)}`,
      );
      const signatures = ajvErrorSignatures(validate);
      assert.ok(fixture.expectedSchemaErrors, "Invalid structural cases must freeze their exact Ajv errors.");
      assert.deepEqual(signatures, fixture.expectedSchemaErrors, fixture.fixturePath ?? String(fixture.recipe?.id));
    } else {
      assert.equal(fixture.expectedSchemaKeyword, undefined);
      assert.equal(fixture.expectedSchemaErrors, undefined);
      if (loaded.bytes !== undefined) {
        const compiled = schemas.get(schemaFileName(fixture.schemaPath));
        assert.ok(compiled);
        const deterministic = writeDocument(loaded.value, schemaAtPath(compiled, fixture.schemaPath), compiled.root);
        if (fixture.expectedSemanticDiagnosticId === "rpc-authoring.order-mismatch") {
          assert.notEqual(loaded.bytes.toString("utf8"), deterministic, fixture.fixturePath);
        } else {
          assert.equal(loaded.bytes.toString("utf8"), deterministic, fixture.fixturePath);
        }
      }
    }
  }

  let expectedFileHashes = 0;
  let expectedInventoryHashes = 0;
  let expectedEnvelopeHashes = 0;
  for (const loaded of cases) {
    const expected = loaded.expectation;
    if (expected.expectedFileSha256 !== undefined) {
      expectedFileHashes++;
      assert.ok(loaded.bytes);
      assert.equal(sha256(loaded.bytes), expected.expectedFileSha256, expected.fixturePath);
    }
    if (expected.expectedDerivedSha256 !== undefined) {
      expectedInventoryHashes++;
      assert.equal(
        domainHash(INVENTORY_DOMAIN, compactFor(loaded.value, expected.schemaPath, schemas)),
        expected.expectedDerivedSha256,
        expected.fixturePath,
      );
    }
    if (expected.expectedCanonicalContentSha256 !== undefined) {
      expectedEnvelopeHashes++;
      assert.equal(
        sha256(Buffer.from(compactFor(loaded.value, expected.schemaPath, schemas), "utf8")),
        expected.expectedCanonicalContentSha256,
        expected.fixturePath,
      );
    }
  }
  assert.deepEqual(
    [expectedFileHashes, expectedInventoryHashes, expectedEnvelopeHashes],
    [4, 4, 1],
  );

  for (const loaded of cases) {
    const expected = loaded.expectation;
    const contextPaths = expected.semanticContextFixturePaths ?? [];
    const descriptorContexts = expected.semanticDescriptorContexts ?? [];
    const contexts: LoadedDocument[] = [];
    for (const path of contextPaths) {
      let context = documents.get(path);
      if (context === undefined && path.endsWith(".rpc.json")) {
        const bytes = await readFile(resolve(conformanceRoot, path));
        const identity = await descriptorIdentity(bytes);
        context = {
          path,
          schemaPath: "rpc-v1-descriptor",
          value: identity.descriptor as unknown as JsonValue,
          bytes,
          descriptorIdentity: identity,
        };
      }
      assert.ok(context, `Semantic context '${path}' is neither an indexed fixture nor an RPC descriptor.`);
      contexts.push(context);
    }
    for (const descriptorContext of descriptorContexts) {
      const bytes = await readFile(resolve(conformanceRoot, descriptorContext.fixturePath));
      const identity = await descriptorIdentity(bytes);
      assert.equal(
        descriptorContext.contextPath,
        `~/.sunder/contracts/v1/sha256/${identity.descriptorSha256}.json`,
        `Descriptor context '${descriptorContext.fixturePath}' has a noncanonical cache path.`,
      );
      contexts.push({
        path: descriptorContext.contextPath,
        schemaPath: "rpc-v1-descriptor",
        value: identity.descriptor as unknown as JsonValue,
        bytes: Buffer.from(identity.canonicalDescriptor, "utf8"),
        descriptorIdentity: identity,
      });
    }
    const expectedConsumedContextPaths = [
      ...contextPaths,
      ...descriptorContexts.map(context => context.contextPath),
    ];
    assert.equal(new Set(expectedConsumedContextPaths).size, expectedConsumedContextPaths.length, "Duplicate semantic context path.");
    if (!expected.schemaValid) {
      assert.equal(expected.semanticValid, false);
      assert.equal(expected.expectedSemanticDiagnosticId, null);
      assert.equal(contexts.length, 0);
      continue;
    }
    const result = await semanticDiagnostics(
      loaded,
      contexts,
      schemas,
      canonical.byKey,
      expectations.immutableArtifactEvidence,
    );
    assert.deepEqual([...result.consumedContexts].sort(ordinal), expectedConsumedContextPaths.sort(ordinal));
    assert.equal(result.diagnostics.length === 0, expected.semanticValid, expected.fixturePath ?? String(expected.recipe?.id));
    assert.deepEqual(
      result.diagnostics,
      expected.expectedSemanticDiagnosticId === null ? [] : [expected.expectedSemanticDiagnosticId],
      expected.fixturePath ?? String(expected.recipe?.id),
    );
    if (expected.expectedSemanticDiagnosticId === "rpc-authoring.aggregate-materializability-overflow") {
      assert.deepEqual(result.diagnostics, ["rpc-authoring.aggregate-materializability-overflow"]);
      const counts = materializedCounts(objectValue(loaded.value, "aggregate lock"), loaded.materialization);
      const expectedCounts = loaded.expectedAggregateCounts;
      assert.ok(expectedCounts);
      for (const key of Object.keys(expectedCounts)) assert.equal(counts[key], expectedCounts[key]);
    }
  }

  const catalogCase = cases.find(value => value.expectation.fixturePath === "valid/platform-catalog.json");
  const catalogDocument = documents.get("valid/platform-catalog.json");
  const mixedLockDocument = documents.get("valid/mixed-contract-lock.json");
  assert.ok(catalogCase && catalogDocument && mixedLockDocument);
  const catalog = objectValue(catalogCase.value, "valid catalog");
  const catalogValidator = validatorFor(schemas, "../platform-catalog.schema.json");
  const contextDocument = (path: string, schemaPath: string, value: JsonObject): LoadedDocument => ({
    path,
    schemaPath,
    value,
    bytes: Buffer.from(JSON.stringify(value), "utf8"),
  });
  const catalogContext = (value: JsonObject, path = "in-memory-catalog"): LoadedDocument => contextDocument(
    path,
    "../platform-catalog.schema.json",
    value,
  );
  const mutationWorld = (
    contexts: readonly LoadedDocument[],
    identities: ReadonlyMap<string, DescriptorIdentity> = canonical.byKey,
    immutableArtifacts?: readonly ImmutableArtifactEvidence[],
  ): SemanticWorld => ({
    schemas,
    contexts,
    consumedContexts: new Set(),
    identities,
    immutableArtifacts: immutableArtifacts ?? expectations.immutableArtifactEvidence.filter(evidence =>
      contexts.some(context => context.path === evidence.inventoryFixturePath)),
  });
  const assertCatalog = async (
    value: JsonObject,
    contexts: readonly LoadedDocument[],
    expectedDiagnostic: string | null,
    expectedConsumed: readonly string[],
  ): Promise<void> => {
    assert.equal(catalogValidator(value), true, JSON.stringify(catalogValidator.errors));
    const world = mutationWorld(contexts);
    assert.equal(await catalogDiagnostic(value, world), expectedDiagnostic);
    assert.deepEqual([...world.consumedContexts].sort(ordinal), [...expectedConsumed].sort(ordinal));
  };
  await assertCatalog(catalog, [], null, []);
  const platformRecord = cloneJson(objectValue(
    objectValue(objectValue(mixedLockDocument.value, "mixed lock").platformImports, "mixed platform imports").stackContributor,
    "mixed platform import",
  ));
  const lowestWorld = mutationWorld([catalogDocument]);
  assert.equal(await platformProvenanceDiagnostic(platformRecord, lowestWorld), null);
  assert.deepEqual([...lowestWorld.consumedContexts], [catalogDocument.path]);
  const unrelatedCatalog = cloneJson(catalog);
  unrelatedCatalog.releaseVersion = "1.2.0";
  const unrelatedCatalogDocument = catalogContext(unrelatedCatalog, "unrelated-catalog");
  const selectedCatalogWorld = mutationWorld([catalogDocument, unrelatedCatalogDocument]);
  assert.equal(await platformProvenanceDiagnostic(platformRecord, selectedCatalogWorld), null);
  assert.deepEqual(
    [...selectedCatalogWorld.consumedContexts].sort(ordinal),
    [catalogDocument.path],
  );
  const noCatalogWorld = mutationWorld([]);
  assert.equal(await platformProvenanceDiagnostic(platformRecord, noCatalogWorld), "rpc-authoring.authority-mismatch");
  assert.deepEqual([...noCatalogWorld.consumedContexts], []);
  const duplicateCatalogWorld = mutationWorld([catalogDocument, catalogContext(cloneJson(catalog), "duplicate-catalog")]);
  assert.equal(await platformProvenanceDiagnostic(platformRecord, duplicateCatalogWorld), "rpc-authoring.authority-mismatch");
  assert.deepEqual([...duplicateCatalogWorld.consumedContexts], []);

  const receiptDocument = documents.get("valid/authoring-receipt-mixed.json");
  const inventoryDocument = documents.get("valid/package-public-inventory-mixed-authority.json");
  assert.ok(receiptDocument && inventoryDocument);
  const receiptValidator = validatorFor(schemas, "../authoring-receipt.schema.json");
  const lockValidator = validatorFor(schemas, "../contract-lock.schema.json");
  const contractLockSchema = schemas.get("contract-lock.schema.json");
  const packageInventorySchema = schemas.get("package-public-inventory.schema.json");
  assert.ok(contractLockSchema && packageInventorySchema);
  const receiptContexts = [mixedLockDocument, inventoryDocument, catalogDocument];
  const localLock = cloneJson(objectValue(mixedLockDocument.value, "mixed lock"));
  for (const value of Object.values(objectValue(localLock.packageArtifacts, "local package artifacts")))
    delete objectValue(value, "local artifact").registryOrigin;
  for (const value of Object.values(objectValue(localLock.platformImports, "local platform imports")))
    delete objectValue(value, "local platform import").registryOrigin;
  const localLockBytes = Buffer.from(writeDocument(localLock, contractLockSchema.root, contractLockSchema.root), "utf8");
  assert.equal(lockValidator(localLock), true, JSON.stringify(lockValidator.errors));
  assert.equal(await lockDiagnostic(localLock, mutationWorld([inventoryDocument, catalogDocument])), null);
  const localReceipt = cloneJson(objectValue(receiptDocument.value, "mixed receipt"));
  localReceipt.lockSha256 = sha256(localLockBytes);
  for (const value of arrayValue(localReceipt.packageArtifacts, "local receipt artifacts"))
    delete objectValue(value, "local receipt artifact").registryOrigin;
  assert.equal(receiptValidator(localReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(await receiptDiagnostic(localReceipt, mutationWorld([
    { ...mixedLockDocument, value: localLock, bytes: localLockBytes }, inventoryDocument, catalogDocument,
  ])), null);
  for (const origin of [null, "", "file:///local", "https://registry.example/path"]) {
    const invalidOrigin = cloneJson(localLock);
    objectValue(Object.values(objectValue(invalidOrigin.packageArtifacts, "local artifacts"))[0], "local artifact").registryOrigin = origin;
    assert.equal(lockValidator(invalidOrigin), false, "Present origin metadata must not fall back to local selection.");
  }
  assert.equal(
    await receiptDiagnostic(objectValue(receiptDocument.value, "valid receipt"), mutationWorld([])),
    "rpc-authoring.evidence-mismatch",
  );
  const duplicateLockWorld = mutationWorld([
    mixedLockDocument,
    { ...mixedLockDocument, path: "duplicate-lock" },
    inventoryDocument,
    catalogDocument,
  ]);
  assert.equal(
    await receiptDiagnostic(objectValue(receiptDocument.value, "valid receipt"), duplicateLockWorld),
    "rpc-authoring.evidence-mismatch",
  );
  assert.deepEqual([...duplicateLockWorld.consumedContexts], []);

  const minimalLockDocument = documents.get("valid/minimal-contract-lock.json");
  assert.ok(minimalLockDocument);
  const parityExpectationBytes = await readFile(resolve(authoringRoot, "parity/expectations.json"));
  const parityExpectations = objectValue(
    parseOuterJson(parityExpectationBytes, parityExpectationBytes.byteLength, 512),
    "parity expectations",
  );
  const samePackageShape = arrayValue(parityExpectations.executableShapes, "parity executable shapes")
    .map(value => objectValue(value, "parity executable shape"))
    .find(value => value.id === "same-package-app-use");
  assert.ok(samePackageShape);
  const samePackageInput = objectValue(samePackageShape.input, "same-package input");
  const samePackageRecords = objectValue(samePackageShape.expectedRecords, "same-package records");
  assert.deepEqual(samePackageRecords.uses, [samePackageInput.appPolicy!]);
  const samePackageBundle = objectValue(arrayValue(samePackageRecords.bundles, "same-package bundles")[0], "same-package bundle");
  const samePackageReceipt: JsonObject = {
    schemaVersion: 1,
    producer: { name: "Sunder.Package.Build", version: "1.0.0", language: "csharp" },
    inputFingerprint: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
    lockSha256: sha256(minimalLockDocument.bytes),
    packageId: samePackageBundle.authorityId!,
    packageVersion: "1.0.0",
    ...cloneJson(samePackageRecords),
  };
  assert.equal(receiptValidator(samePackageReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(await receiptDiagnostic(samePackageReceipt, mutationWorld([minimalLockDocument])), null);
  const narrowedOwnedUseReceipt = cloneJson(samePackageReceipt);
  const narrowedOwnedUse = objectValue(arrayValue(narrowedOwnedUseReceipt.uses, "narrowed owned uses")[0], "narrowed owned use");
  narrowedOwnedUse.required = false;
  narrowedOwnedUse.actions = ["discover", "subscribe"];
  assert.equal(receiptValidator(narrowedOwnedUseReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(await receiptDiagnostic(narrowedOwnedUseReceipt, mutationWorld([minimalLockDocument])), null);

  const receiptMutations: ReadonlyArray<readonly [string, (receipt: JsonObject) => void]> = [
    ["selected version 9.9.9", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").selectedPackageVersion = "9.9.9";
    }],
    ["requested range", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").requestedVersionRange = "1.2.3";
    }],
    ["prerelease policy", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").includePrerelease = true;
    }],
    ["selected content", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").selectedPackageContentIdentity = "2222222222222222222222222222222222222222222222222222222222222222";
    }],
    ["registry origin", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").registryOrigin = "https://other.example/";
    }],
    ["public count", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").publicContractCount = 1;
    }],
    ["inventory hash", value => {
      objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact").publicInventorySha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    }],
    ["nested contract ETag", value => {
      const artifact = objectValue(arrayValue(value.packageArtifacts, "package artifacts")[0], "package artifact");
      objectValue(arrayValue(artifact.contracts, "artifact contracts")[0], "artifact contract").etag = `"${"f".repeat(64)}"`;
    }],
    ["artifact-import selection", value => {
      objectValue(arrayValue(value.packageArtifactImports, "artifact imports")[0], "artifact import").selectedPackageVersion = "9.9.9";
    }],
    ["package API bundle", value => {
      const group = objectValue(arrayValue(value.packageApiImports, "package API imports")[0], "package API import");
      objectValue(arrayValue(group.bundleRefs, "bundle refs")[0], "bundle ref").contractVersion = "9.9.9";
    }],
    ["package API presence", value => {
      const group = objectValue(arrayValue(value.packageApiImports, "package API imports")[0], "package API import");
      group.presence = "required";
    }],
    ["platform import selection", value => {
      objectValue(arrayValue(value.platformImports, "platform imports")[0], "platform import").selectedAuthorityRelease = "9.9.9";
    }],
    ["generated facade", value => {
      objectValue(arrayValue(value.generatedPackageFacades, "generated facades")[0], "generated facade").localName = "otherName";
    }],
    ["imported binding namespace", value => {
      const binding = arrayValue(value.generatedBindings, "generated bindings")
        .map(item => objectValue(item, "generated binding"))
        .find(item => item.contractKey === "example.rpc.v1.minimal")!;
      binding.logicalName = "Sunder.Generated.Rpc.Imports.ArbitraryAlias";
    }],
    ["per-contract alias", value => {
      for (const name of ["contracts", "bundles", "uses", "generatedBindings"]) {
        for (const item of arrayValue(value[name], name).map(item => objectValue(item, name))) {
          if (item.contractKey === "example.rpc.v1.minimal") item.contractKey = "example.rpc.v1.alias";
        }
      }
    }],
  ];
  for (const [label, mutate] of receiptMutations) {
    const receipt: JsonObject = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
    mutate(receipt);
    assert.equal(receiptValidator(receipt), true, `${label}: ${JSON.stringify(receiptValidator.errors)}`);
    const world = mutationWorld(receiptContexts);
    assert.equal(await receiptDiagnostic(receipt, world), "rpc-authoring.evidence-mismatch", label);
    assert.deepEqual([...world.consumedContexts].sort(ordinal), receiptContexts.map(value => value.path).sort(ordinal), label);
  }
  const missingInvokeReceipt = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
  objectValue(arrayValue(missingInvokeReceipt.uses, "missing invoke uses")[0], "missing invoke use").actions = ["discover"];
  assert.equal(receiptValidator(missingInvokeReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(missingInvokeReceipt, mutationWorld(receiptContexts)),
    "rpc-authoring.descriptor-action-mismatch",
  );
  const importedExtraUseReceipt = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
  arrayValue(importedExtraUseReceipt.uses, "imported extra uses").unshift({
    role: "app",
    required: false,
    actions: ["discover"],
    contractKey: "example.rpc.v1.minimal",
  });
  assert.equal(receiptValidator(importedExtraUseReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(importedExtraUseReceipt, mutationWorld(receiptContexts)),
    "rpc-authoring.evidence-mismatch",
  );
  const importedAsOwnedReceipt = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
  const importedAsOwnedBundle = arrayValue(importedAsOwnedReceipt.bundles, "imported-as-owned bundles")
    .map(value => objectValue(value, "imported-as-owned bundle"))
    .find(bundle => bundle.contractKey === "example.rpc.v1.minimal");
  assert.ok(importedAsOwnedBundle);
  importedAsOwnedBundle.source = "owned";
  importedAsOwnedBundle.authorityKind = "package";
  importedAsOwnedBundle.authorityId = importedAsOwnedReceipt.packageId!;
  delete importedAsOwnedBundle.selectedAuthorityRelease;
  delete importedAsOwnedBundle.selectedAuthorityContentIdentity;
  assert.equal(receiptValidator(importedAsOwnedReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(importedAsOwnedReceipt, mutationWorld(receiptContexts)),
    "rpc-authoring.evidence-mismatch",
  );
  const packageApiProviderReceipt = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
  arrayValue(packageApiProviderReceipt.providers, "package API providers").splice(1, 0, {
    providerId: "example.consumer.minimal",
    role: "runtime",
    audience: "public",
    contractKey: "example.rpc.v1.minimal",
  });
  assert.equal(receiptValidator(packageApiProviderReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(packageApiProviderReceipt, mutationWorld(receiptContexts)),
    "rpc-authoring.evidence-mismatch",
  );
  const platformUseReceipt = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
  arrayValue(platformUseReceipt.uses, "platform uses").push({
    role: "runtime",
    required: false,
    actions: ["discover", "invoke"],
    contractKey: "sunder.stack.contributor",
  });
  assert.equal(receiptValidator(platformUseReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(platformUseReceipt, mutationWorld(receiptContexts)),
    "rpc-authoring.evidence-mismatch",
  );

  const providerLock = cloneJson(objectValue(mixedLockDocument.value, "mixed lock"));
  providerLock.providerImports = {
    minimalProvider: {
      packageId: "example.dependency",
      contractId: "example.rpc.v1.minimal",
    },
  };
  assert.deepEqual(normalizePackageDependencies(materializationFromLock(providerLock).packageDependencies), [{
    packageId: "example.dependency",
    selectedAuthorityRelease: "1.2.3",
    selectedAuthorityContentIdentity: "1111111111111111111111111111111111111111111111111111111111111111",
    presence: "required",
  }]);
  const providerLockBytes = Buffer.from(writeDocument(providerLock, contractLockSchema.root, contractLockSchema.root), "utf8");
  const providerLockContext: LoadedDocument = {
    path: "in-memory-provider-lock",
    schemaPath: "../contract-lock.schema.json",
    value: providerLock,
    bytes: providerLockBytes,
  };
  const providerReceipt = cloneJson(objectValue(receiptDocument.value, "valid receipt"));
  providerReceipt.lockSha256 = sha256(providerLockBytes);
  providerReceipt.providerImports = [{
    key: "minimalProvider",
    packageId: "example.dependency",
    contractId: "example.rpc.v1.minimal",
    contractVersion: "2.0.0",
    bundleRef: { contractId: "example.rpc.v1.minimal", contractVersion: "2.0.0" },
    providerRefs: [{
      role: "runtime",
      providerId: "example.consumer.minimal",
      contractId: "example.rpc.v1.minimal",
      contractVersion: "2.0.0",
    }, {
      role: "runtime",
      providerId: "example.consumer.minimalz",
      contractId: "example.rpc.v1.minimal",
      contractVersion: "2.0.0",
    }],
  }];
  const providerRecords = arrayValue(providerReceipt.providers, "receipt providers");
  providerRecords.splice(1, 0, {
    providerId: "example.consumer.minimal",
    role: "runtime",
    audience: "public",
    contractKey: "example.rpc.v1.minimal",
  });
  providerRecords.splice(2, 0, {
    providerId: "example.consumer.minimalz",
    role: "runtime",
    audience: "public",
    contractKey: "example.rpc.v1.minimal",
  });
  assert.equal(receiptValidator(providerReceipt), true, JSON.stringify(receiptValidator.errors));
  const providerReceiptWorld = mutationWorld([providerLockContext, inventoryDocument, catalogDocument]);
  assert.equal(await receiptDiagnostic(providerReceipt, providerReceiptWorld), null);
  const changedProviderReceipt = cloneJson(providerReceipt);
  objectValue(arrayValue(changedProviderReceipt.providerImports, "provider imports")[0], "provider import").contractVersion = "9.9.9";
  assert.equal(receiptValidator(changedProviderReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(changedProviderReceipt, mutationWorld([providerLockContext, inventoryDocument, catalogDocument])),
    "rpc-authoring.evidence-mismatch",
  );
  const missingProviderRefReceipt = cloneJson(providerReceipt);
  arrayValue(
    objectValue(arrayValue(missingProviderRefReceipt.providerImports, "provider imports")[0], "provider import").providerRefs,
    "provider refs",
  ).pop();
  assert.equal(receiptValidator(missingProviderRefReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(
      missingProviderRefReceipt,
      mutationWorld([providerLockContext, inventoryDocument, catalogDocument]),
    ),
    "rpc-authoring.evidence-mismatch",
  );
  const reversedProviderRefsReceipt = cloneJson(providerReceipt);
  arrayValue(
    objectValue(arrayValue(reversedProviderRefsReceipt.providerImports, "provider imports")[0], "provider import").providerRefs,
    "provider refs",
  ).reverse();
  assert.equal(receiptValidator(reversedProviderRefsReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(
      reversedProviderRefsReceipt,
      mutationWorld([providerLockContext, inventoryDocument, catalogDocument]),
    ),
    "rpc-authoring.order-mismatch",
  );
  const providerOnlyLock = cloneJson(providerLock);
  providerOnlyLock.packageImports = {};
  const providerOnlyLockBytes = Buffer.from(
    writeDocument(providerOnlyLock, contractLockSchema.root, contractLockSchema.root),
    "utf8",
  );
  const providerOnlyLockDocument: LoadedDocument = {
    path: "in-memory-provider-only-lock",
    schemaPath: "../contract-lock.schema.json",
    value: providerOnlyLock,
    bytes: providerOnlyLockBytes,
  };
  const providerOnlyExtraUseReceipt = cloneJson(providerReceipt);
  providerOnlyExtraUseReceipt.lockSha256 = sha256(providerOnlyLockBytes);
  providerOnlyExtraUseReceipt.packageApiImports = [];
  providerOnlyExtraUseReceipt.generatedPackageFacades = [];
  providerOnlyExtraUseReceipt.uses = [{
    role: "runtime",
    required: false,
    actions: ["discover", "invoke"],
    contractKey: "example.rpc.v1.minimal",
  }];
  assert.equal(receiptValidator(providerOnlyExtraUseReceipt), true, JSON.stringify(receiptValidator.errors));
  assert.equal(
    await receiptDiagnostic(
      providerOnlyExtraUseReceipt,
      mutationWorld([providerOnlyLockDocument, inventoryDocument, catalogDocument]),
    ),
    "rpc-authoring.evidence-mismatch",
  );

  const packageLockDocument = documents.get("valid/package-api-contract-lock.json");
  const packageInventoryDocument = documents.get("valid/package-public-inventory-nonempty.json");
  assert.ok(packageLockDocument && packageInventoryDocument);
  const packageLockCase = cases.find(value => value.expectation.fixturePath === "valid/package-api-contract-lock.json");
  const invalidInventoryDocument = documents.get("semantic-invalid/package-inventory-count-mismatch.json");
  assert.ok(packageLockCase && invalidInventoryDocument);
  await assert.rejects(
    semanticDiagnostics(
      packageLockCase,
      [invalidInventoryDocument],
      schemas,
      canonical.byKey,
      expectations.immutableArtifactEvidence,
    ),
    /Semantic context 'semantic-invalid\/package-inventory-count-mismatch\.json' failed independently/u,
  );
  const structurallyInvalidInventory = documents.get("invalid/package-inventory-missing-required.json");
  assert.ok(structurallyInvalidInventory);
  await assert.rejects(
    semanticDiagnostics(
      packageLockCase,
      [structurallyInvalidInventory],
      schemas,
      canonical.byKey,
      expectations.immutableArtifactEvidence,
    ),
    /is structurally invalid/u,
  );
  const changedInventory = cloneJson(objectValue(packageInventoryDocument.value, "package inventory"));
  changedInventory.packageVersion = "1.2.4";
  objectValue(arrayValue(changedInventory.contracts, "inventory contracts")[0], "inventory contract").selectedAuthorityRelease = "1.2.4";
  const inventoryContext = contextDocument("changed-inventory", "../package-public-inventory.schema.json", changedInventory);
  const inventoryWorld = mutationWorld(
    [inventoryContext],
    canonical.byKey,
    expectations.immutableArtifactEvidence.filter(evidence => evidence.inventoryFixturePath === packageInventoryDocument.path),
  );
  assert.equal(await lockDiagnostic(objectValue(packageLockDocument.value, "package lock"), inventoryWorld), "rpc-authoring.hash-mismatch");
  assert.deepEqual([...inventoryWorld.consumedContexts], ["changed-inventory"]);
  const missingInventoryWorld = mutationWorld([]);
  assert.equal(await lockDiagnostic(objectValue(packageLockDocument.value, "package lock"), missingInventoryWorld), "rpc-authoring.evidence-mismatch");
  assert.deepEqual([...missingInventoryWorld.consumedContexts], []);
  const duplicateInventoryWorld = mutationWorld([packageInventoryDocument, contextDocument(
    "duplicate-inventory",
    "../package-public-inventory.schema.json",
    cloneJson(objectValue(packageInventoryDocument.value, "package inventory")),
  )]);
  assert.equal(await lockDiagnostic(objectValue(packageLockDocument.value, "package lock"), duplicateInventoryWorld), "rpc-authoring.evidence-mismatch");
  assert.deepEqual([...duplicateInventoryWorld.consumedContexts], []);

  const deletedArtifactLock = cloneJson(objectValue(mixedLockDocument.value, "mixed lock"));
  const deletedArtifact = objectValue(
    objectValue(deletedArtifactLock.packageArtifacts, "deleted package artifacts")["example.dependency"],
    "deleted package artifact",
  );
  delete objectValue(deletedArtifact.contracts, "deleted artifact contracts")["sunder.stack.contributor"];
  deletedArtifact.publicContractCount = 1;
  deletedArtifactLock.platformImports = {};
  const deletedInventory = cloneJson(objectValue(inventoryDocument.value, "deleted inventory"));
  deletedInventory.contracts = arrayValue(deletedInventory.contracts, "deleted inventory contracts")
    .filter(value => objectValue(value, "deleted inventory contract").contractId !== "sunder.stack.contributor");
  deletedInventory.publicContractCount = 1;
  deletedArtifact.publicInventorySha256 = domainHash(
    INVENTORY_DOMAIN,
    writeCompact(deletedInventory, packageInventorySchema.root, packageInventorySchema.root),
  );
  assert.equal(lockValidator(deletedArtifactLock), true, JSON.stringify(lockValidator.errors));
  assert.equal(
    validatorFor(schemas, "../package-public-inventory.schema.json")(deletedInventory),
    true,
  );
  assert.equal(
    await lockDiagnostic(deletedArtifactLock, mutationWorld([
      contextDocument(inventoryDocument.path, "../package-public-inventory.schema.json", deletedInventory),
    ])),
    "rpc-authoring.evidence-mismatch",
  );

  assert.equal(hasSemanticPrerelease("1.2.3+build-meta"), false);
  assert.equal(hasSemanticPrerelease("1.2.3-rc.1+build-meta"), true);
  const buildMetadataLock = cloneJson(objectValue(packageLockDocument.value, "build metadata lock"));
  const buildMetadataArtifact = objectValue(
    objectValue(buildMetadataLock.packageArtifacts, "build metadata artifacts")["example.dependency"],
    "build metadata artifact",
  );
  buildMetadataArtifact.selectedPackageVersion = "1.2.3+build-meta";
  objectValue(
    objectValue(buildMetadataArtifact.contracts, "build metadata contracts")["example.rpc.v1.minimal"],
    "build metadata contract",
  ).selectedAuthorityRelease = "1.2.3+build-meta";
  const buildMetadataInventory = inventoryFromArtifact("example.dependency", buildMetadataArtifact);
  buildMetadataArtifact.publicInventorySha256 = domainHash(
    INVENTORY_DOMAIN,
    writeCompact(buildMetadataInventory, packageInventorySchema.root, packageInventorySchema.root),
  );
  const buildMetadataEvidence: ImmutableArtifactEvidence = {
    inventoryFixturePath: "build-metadata-inventory",
    packageId: "example.dependency",
    packageVersion: "1.2.3+build-meta",
    packageContentIdentity: stringValue(buildMetadataArtifact.selectedPackageContentIdentity, "build metadata content identity"),
    publicContractCount: 1,
    publicInventorySha256: stringValue(buildMetadataArtifact.publicInventorySha256, "build metadata inventory hash"),
  };
  assert.equal(lockValidator(buildMetadataLock), true, JSON.stringify(lockValidator.errors));
  assert.equal(
    await lockDiagnostic(
      buildMetadataLock,
      mutationWorld([
        contextDocument("build-metadata-inventory", "../package-public-inventory.schema.json", buildMetadataInventory),
      ], canonical.byKey, [buildMetadataEvidence]),
    ),
    null,
  );

  const coalescedUseLock = cloneJson(objectValue(mixedLockDocument.value, "coalesced use lock"));
  const coalescedArtifacts = objectValue(coalescedUseLock.packageArtifacts, "coalesced use artifacts");
  const coalescedDependencyArtifact = objectValue(coalescedArtifacts["example.dependency"], "coalesced dependency artifact");
  const coalescedContract = objectValue(
    objectValue(coalescedDependencyArtifact.contracts, "coalesced dependency contracts")["example.rpc.v1.minimal"],
    "coalesced dependency contract",
  );
  coalescedContract.artifactSource = "imported";
  const coalescedDependencyInventory = inventoryFromArtifact("example.dependency", coalescedDependencyArtifact);
  coalescedDependencyArtifact.publicInventorySha256 = domainHash(
    INVENTORY_DOMAIN,
    writeCompact(coalescedDependencyInventory, packageInventorySchema.root, packageInventorySchema.root),
  );
  const reexportArtifact: JsonObject = {
    requestedVersionRange: ">=1.0.0 <2.0.0",
    includePrerelease: false,
    selectedPackageVersion: "1.0.0",
    selectedPackageContentIdentity: "4444444444444444444444444444444444444444444444444444444444444444",
    registryOrigin: "https://registry.example/",
    publicContractCount: 1,
    publicInventorySha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    contracts: {
      "example.rpc.v1.minimal": cloneJson(coalescedContract),
    },
  };
  const reexportInventory = inventoryFromArtifact("example.reexport", reexportArtifact);
  reexportArtifact.publicInventorySha256 = domainHash(
    INVENTORY_DOMAIN,
    writeCompact(reexportInventory, packageInventorySchema.root, packageInventorySchema.root),
  );
  coalescedArtifacts["example.reexport"] = reexportArtifact;
  const coalescedPackageImports = objectValue(coalescedUseLock.packageImports, "coalesced package imports");
  objectValue(coalescedPackageImports["example.dependency"], "coalesced dependency import").requiredContracts = [];
  coalescedPackageImports["example.reexport"] = {
    localName: "reexport",
    presence: "required",
    requiredContracts: ["example.rpc.v1.minimal"],
  };
  const coalescedEvidence: ImmutableArtifactEvidence[] = [
    {
      inventoryFixturePath: "coalesced-dependency-inventory",
      packageId: "example.dependency",
      packageVersion: stringValue(coalescedDependencyArtifact.selectedPackageVersion, "coalesced package version"),
      packageContentIdentity: stringValue(coalescedDependencyArtifact.selectedPackageContentIdentity, "coalesced package content"),
      publicContractCount: numberValue(coalescedDependencyArtifact.publicContractCount, "coalesced package count"),
      publicInventorySha256: stringValue(coalescedDependencyArtifact.publicInventorySha256, "coalesced package inventory hash"),
    },
    {
      inventoryFixturePath: "coalesced-reexport-inventory",
      packageId: "example.reexport",
      packageVersion: stringValue(reexportArtifact.selectedPackageVersion, "reexport package version"),
      packageContentIdentity: stringValue(reexportArtifact.selectedPackageContentIdentity, "reexport package content"),
      publicContractCount: numberValue(reexportArtifact.publicContractCount, "reexport package count"),
      publicInventorySha256: stringValue(reexportArtifact.publicInventorySha256, "reexport package inventory hash"),
    },
  ];
  assert.equal(lockValidator(coalescedUseLock), true, JSON.stringify(lockValidator.errors));
  const coalescedUseLockBytes = Buffer.from(
    writeDocument(coalescedUseLock, contractLockSchema.root, contractLockSchema.root),
    "utf8",
  );
  const coalescedUseLockDocument: LoadedDocument = {
    path: "in-memory-coalesced-use-lock",
    schemaPath: "../contract-lock.schema.json",
    value: coalescedUseLock,
    bytes: coalescedUseLockBytes,
  };
  const coalescedInventoryDocuments = [
    contextDocument("coalesced-dependency-inventory", "../package-public-inventory.schema.json", coalescedDependencyInventory),
    contextDocument("coalesced-reexport-inventory", "../package-public-inventory.schema.json", reexportInventory),
  ];
  assert.equal(
    await lockDiagnostic(
      coalescedUseLock,
      mutationWorld([...coalescedInventoryDocuments, catalogDocument], canonical.byKey, coalescedEvidence),
    ),
    null,
  );
  assert.deepEqual(normalizePackageDependencies(materializationFromLock(coalescedUseLock).packageDependencies), [{
    packageId: "example.dependency",
    selectedAuthorityRelease: "1.2.3",
    selectedAuthorityContentIdentity: "1111111111111111111111111111111111111111111111111111111111111111",
    presence: "required",
  }, {
    packageId: "example.reexport",
    selectedAuthorityRelease: "1.0.0",
    selectedAuthorityContentIdentity: "4444444444444444444444444444444444444444444444444444444444444444",
    presence: "required",
  }]);
  const coalescedUseReceipt = cloneJson(objectValue(receiptDocument.value, "coalesced use receipt"));
  coalescedUseReceipt.lockSha256 = sha256(coalescedUseLockBytes);
  coalescedUseReceipt.packageArtifacts = receiptArtifactsFromLock(coalescedUseLock);
  coalescedUseReceipt.packageArtifactImports = arrayValue(coalescedUseReceipt.packageArtifacts, "coalesced receipt artifacts")
    .map(value => {
      const artifact = objectValue(value, "coalesced receipt artifact");
      return {
        packageId: artifact.packageId!,
        requestedVersionRange: artifact.requestedVersionRange!,
        includePrerelease: artifact.includePrerelease!,
        selectedPackageVersion: artifact.selectedPackageVersion!,
        selectedPackageContentIdentity: artifact.selectedPackageContentIdentity!,
        publicContractCount: artifact.publicContractCount!,
        publicInventorySha256: artifact.publicInventorySha256!,
        authorityConstraints: [...expectedAuthorityConstraints(artifact).values()],
      };
    });
  arrayValue(coalescedUseReceipt.packageApiImports, "coalesced package API imports").push({
    packageId: "example.reexport",
    presence: "required",
    bundleRefs: [{ contractId: "example.rpc.v1.minimal", contractVersion: "2.0.0" }],
    useRefs: [{ role: "runtime", contractId: "example.rpc.v1.minimal", contractVersion: "2.0.0" }],
  });
  arrayValue(coalescedUseReceipt.generatedPackageFacades, "coalesced generated facades").push({
    packageId: "example.reexport",
    localName: "reexport",
    logicalName: "Sunder.Generated.Rpc.ReexportPackageApi",
  });
  assert.equal(receiptValidator(coalescedUseReceipt), true, JSON.stringify(receiptValidator.errors));
  const coalescedReceiptContexts = [
    coalescedUseLockDocument,
    ...coalescedInventoryDocuments,
    catalogDocument,
  ];
  assert.equal(
    await receiptDiagnostic(
      coalescedUseReceipt,
      mutationWorld(coalescedReceiptContexts, canonical.byKey, coalescedEvidence),
    ),
    null,
  );
  const falseCoalescedRequiredReceipt = cloneJson(coalescedUseReceipt);
  objectValue(arrayValue(falseCoalescedRequiredReceipt.uses, "coalesced uses")[0], "coalesced use").required = false;
  assert.equal(
    await receiptDiagnostic(
      falseCoalescedRequiredReceipt,
      mutationWorld(coalescedReceiptContexts, canonical.byKey, coalescedEvidence),
    ),
    "rpc-authoring.evidence-mismatch",
  );

  const duplicateProviderLock = cloneJson(objectValue(packageLockDocument.value, "package lock"));
  duplicateProviderLock.packageImports = {};
  duplicateProviderLock.providerImports = {
    first: { packageId: "example.dependency", contractId: "example.rpc.v1.minimal" },
    second: { packageId: "example.dependency", contractId: "example.rpc.v1.minimal" },
  };
  assert.equal(lockValidator(duplicateProviderLock), true, JSON.stringify(lockValidator.errors));
  assert.equal(await lockDiagnostic(duplicateProviderLock, mutationWorld([packageInventoryDocument])), "rpc-authoring.duplicate-projected-key");
  const duplicatePlatformLock = cloneJson(objectValue(mixedLockDocument.value, "mixed lock"));
  const stackIntent = objectValue(objectValue(duplicatePlatformLock.platformImports, "platform imports").stackContributor, "platform intent");
  objectValue(duplicatePlatformLock.platformImports, "platform imports").stackDuplicate = cloneJson(stackIntent);
  assert.equal(lockValidator(duplicatePlatformLock), true, JSON.stringify(lockValidator.errors));
  assert.equal(
    await lockDiagnostic(duplicatePlatformLock, mutationWorld([inventoryDocument, catalogDocument])),
    "rpc-authoring.duplicate-projected-key",
  );

  const platformLockMutations: ReadonlyArray<readonly [string, (platform: JsonObject) => void, string]> = [
    ["platform range", platform => {
      platform.authorityReleaseRange = ">=2.0.0 <3.0.0";
    }, "rpc-authoring.version-selection-mismatch"],
    ["platform prerelease", platform => {
      platform.selectedAuthorityRelease = "1.1.0-rc.1";
    }, "rpc-authoring.version-selection-mismatch"],
    ["platform registry origin", platform => {
      platform.registryOrigin = "https://0x7f.0.0.1/";
    }, "rpc-authoring.registry-origin-invalid"],
  ];
  for (const [label, mutate, expectedDiagnostic] of platformLockMutations) {
    const changedPlatformLock: JsonObject = cloneJson(objectValue(mixedLockDocument.value, "changed platform lock"));
    mutate(objectValue(
      objectValue(changedPlatformLock.platformImports, "changed platform imports").stackContributor,
      "changed platform import",
    ));
    assert.equal(lockValidator(changedPlatformLock), true, `${label}: ${JSON.stringify(lockValidator.errors)}`);
    assert.equal(
      await lockDiagnostic(changedPlatformLock, mutationWorld([inventoryDocument, catalogDocument])),
      expectedDiagnostic,
      label,
    );
  }

  const secondReleaseCatalog = cloneJson(catalog);
  secondReleaseCatalog.releaseVersion = "1.2.0";
  assert.equal(catalogValidator(secondReleaseCatalog), true, JSON.stringify(catalogValidator.errors));
  assert.equal(await catalogDiagnostic(secondReleaseCatalog, mutationWorld([])), null);
  const mixedAuthorityLock = cloneJson(objectValue(mixedLockDocument.value, "mixed authority lock"));
  const mixedAuthorityPlatform = objectValue(
    objectValue(mixedAuthorityLock.platformImports, "mixed authority platform imports").stackContributor,
    "mixed authority platform import",
  );
  mixedAuthorityPlatform.selectedAuthorityRelease = "1.2.0";
  mixedAuthorityPlatform.selectedAuthorityContentIdentity = catalogContentIdentity(secondReleaseCatalog, schemas);
  assert.equal(lockValidator(mixedAuthorityLock), true, JSON.stringify(lockValidator.errors));
  const secondReleaseCatalogDocument = catalogContext(secondReleaseCatalog, "in-memory-second-release-catalog");
  const mixedAuthorityContexts = [inventoryDocument, catalogDocument, secondReleaseCatalogDocument];
  assert.equal(
    await lockDiagnostic(mixedAuthorityLock, mutationWorld(mixedAuthorityContexts)),
    "rpc-authoring.authority-mismatch",
  );
  const artifactPlatformContract = objectValue(
    objectValue(
      objectValue(mixedAuthorityLock.packageArtifacts, "mixed authority artifacts")["example.dependency"],
      "mixed authority artifact",
    ).contracts,
    "mixed authority artifact contracts",
  )["sunder.stack.contributor"];
  assert.equal(
    authorityAgreementDiagnostic([objectValue(artifactPlatformContract, "artifact platform contract"), mixedAuthorityPlatform]),
    "rpc-authoring.authority-mismatch",
  );
  const mixedAuthorityLockBytes = Buffer.from(
    writeDocument(mixedAuthorityLock, contractLockSchema.root, contractLockSchema.root),
    "utf8",
  );
  const mixedAuthorityLockDocument: LoadedDocument = {
    path: "in-memory-mixed-authority-lock",
    schemaPath: "../contract-lock.schema.json",
    value: mixedAuthorityLock,
    bytes: mixedAuthorityLockBytes,
  };
  assert.equal(
    await receiptDiagnostic(
      objectValue(receiptDocument.value, "valid receipt"),
      mutationWorld([mixedAuthorityLockDocument, ...mixedAuthorityContexts]),
    ),
    "rpc-authoring.authority-mismatch",
  );

  const conflictingBundleLock = cloneJson(objectValue(packageLockDocument.value, "package lock"));
  const conflictingArtifacts = objectValue(conflictingBundleLock.packageArtifacts, "package artifacts");
  const otherArtifact = cloneJson(objectValue(conflictingArtifacts["example.dependency"], "dependency artifact"));
  otherArtifact.selectedPackageContentIdentity = "2222222222222222222222222222222222222222222222222222222222222222";
  const otherArtifactContract = objectValue(objectValue(otherArtifact.contracts, "other contracts")["example.rpc.v1.minimal"], "other contract");
  otherArtifactContract.authorityId = "example.other";
  otherArtifactContract.selectedAuthorityContentIdentity = otherArtifact.selectedPackageContentIdentity!;
  const otherInventory = inventoryFromArtifact("example.other", otherArtifact);
  otherArtifact.publicInventorySha256 = domainHash(
    INVENTORY_DOMAIN,
    writeCompact(otherInventory, packageInventorySchema.root, packageInventorySchema.root),
  );
  conflictingArtifacts["example.other"] = otherArtifact;
  objectValue(conflictingBundleLock.packageImports, "package imports")["example.other"] = {
    localName: "other",
    presence: "required",
    requiredContracts: ["example.rpc.v1.minimal"],
  };
  assert.equal(lockValidator(conflictingBundleLock), true, JSON.stringify(lockValidator.errors));
  const otherArtifactEvidence: ImmutableArtifactEvidence = {
    inventoryFixturePath: "other-inventory",
    packageId: "example.other",
    packageVersion: stringValue(otherArtifact.selectedPackageVersion, "other package version"),
    packageContentIdentity: stringValue(otherArtifact.selectedPackageContentIdentity, "other package content"),
    publicContractCount: numberValue(otherArtifact.publicContractCount, "other public contract count"),
    publicInventorySha256: stringValue(otherArtifact.publicInventorySha256, "other inventory hash"),
  };
  const conflictingBundleWorld = mutationWorld([
    packageInventoryDocument,
    contextDocument("other-inventory", "../package-public-inventory.schema.json", otherInventory),
  ], canonical.byKey, [
    ...expectations.immutableArtifactEvidence.filter(evidence => evidence.inventoryFixturePath === packageInventoryDocument.path),
    otherArtifactEvidence,
  ]);
  assert.equal(await lockDiagnostic(conflictingBundleLock, conflictingBundleWorld), "rpc-authoring.evidence-mismatch");
  assert.deepEqual(
    [...conflictingBundleWorld.consumedContexts].sort(ordinal),
    [packageInventoryDocument.path, "other-inventory"].sort(ordinal),
  );

  const duplicateInventoryContract = cloneJson(objectValue(packageInventoryDocument.value, "package inventory"));
  const secondInventoryContract = cloneJson(objectValue(
    arrayValue(duplicateInventoryContract.contracts, "inventory contracts")[0],
    "inventory contract",
  ));
  secondInventoryContract.contractVersion = "2.0.1";
  arrayValue(duplicateInventoryContract.contracts, "inventory contracts").push(secondInventoryContract);
  duplicateInventoryContract.publicContractCount = 2;
  assert.equal(
    validatorFor(schemas, "../package-public-inventory.schema.json")(duplicateInventoryContract),
    true,
  );
  assert.equal(await inventoryDiagnostic(duplicateInventoryContract, mutationWorld([])), "rpc-authoring.duplicate-contract-id");
  const duplicateCatalogContract = cloneJson(catalog);
  const duplicateCatalogPayload = duplicateCatalogContract;
  const mandatoryCatalogContract = objectValue(
    arrayValue(duplicateCatalogPayload.contracts, "catalog contracts")[0],
    "catalog contract",
  );
  const firstCatalogContract = cloneJson(mandatoryCatalogContract);
  firstCatalogContract.contractId = "example.duplicate";
  const secondCatalogContract = cloneJson(firstCatalogContract);
  secondCatalogContract.contractVersion = "2.0.1";
  duplicateCatalogPayload.contracts = [firstCatalogContract, secondCatalogContract, mandatoryCatalogContract];
  duplicateCatalogPayload.contractCount = 3;
  assert.equal(catalogValidator(duplicateCatalogContract), true, JSON.stringify(catalogValidator.errors));
  assert.equal(await catalogDiagnostic(duplicateCatalogContract, mutationWorld([])), "rpc-authoring.duplicate-contract-id");

  const packageProjectDocument = documents.get("valid/package-project-export-owned.json");
  const packageBaselineDocument = documents.get("valid/package-owned-baseline.json");
  assert.ok(packageProjectDocument && packageBaselineDocument);
  assert.equal(
    await projectExportDiagnostic(objectValue(packageProjectDocument.value, "package export"), mutationWorld([])),
    "rpc-authoring.evidence-mismatch",
  );
  const duplicateBaselineWorld = mutationWorld([
    packageBaselineDocument,
    contextDocument("duplicate-baseline", "../owned-contracts.schema.json", cloneJson(objectValue(packageBaselineDocument.value, "baseline"))),
  ]);
  assert.equal(
    await projectExportDiagnostic(objectValue(packageProjectDocument.value, "package export"), duplicateBaselineWorld),
    "rpc-authoring.evidence-mismatch",
  );
  assert.deepEqual([...duplicateBaselineWorld.consumedContexts], []);
  const changedBaseline = cloneJson(objectValue(packageBaselineDocument.value, "package baseline"));
  const changedSnapshot = objectValue(objectValue(objectValue(changedBaseline.contracts, "baseline contracts").minimal, "baseline contract").current, "current snapshot");
  changedSnapshot.exposure = "package";
  assert.equal(
    await projectExportDiagnostic(objectValue(packageProjectDocument.value, "package export"), mutationWorld([
      contextDocument("changed-baseline", "../owned-contracts.schema.json", changedBaseline),
    ])),
    "rpc-authoring.evidence-mismatch",
  );

  const cacheDocument = documents.get("valid/cache-entry-receipt.json");
  const cacheIdentity = canonical.byName.get("minimalDescriptor");
  const unboundedIdentity = canonical.byName.get("unboundedBase64Descriptor");
  assert.ok(cacheDocument && cacheIdentity && unboundedIdentity);
  const cacheDescriptorPath = `~/.sunder/contracts/v1/sha256/${cacheIdentity.descriptorSha256}.json`;
  const cacheDescriptorContext: LoadedDocument = {
    path: cacheDescriptorPath,
    schemaPath: "rpc-v1-descriptor",
    value: cacheIdentity.descriptor as unknown as JsonValue,
    bytes: Buffer.from(cacheIdentity.canonicalDescriptor, "utf8"),
    descriptorIdentity: cacheIdentity,
  };
  const cacheWorld = mutationWorld([cacheDescriptorContext]);
  assert.equal(await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), cacheWorld), null);
  assert.deepEqual([...cacheWorld.consumedContexts], [cacheDescriptorContext.path]);
  const trailingNewlineBytes = Buffer.concat([cacheDescriptorContext.bytes, Buffer.from("\n", "utf8")]);
  const trailingNewlineCacheContext: LoadedDocument = {
    ...cacheDescriptorContext,
    bytes: trailingNewlineBytes,
    descriptorIdentity: await descriptorIdentity(trailingNewlineBytes),
  };
  assert.equal(
    await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), mutationWorld([trailingNewlineCacheContext])),
    "rpc-authoring.evidence-mismatch",
  );
  const noncanonicalCacheBytes = Buffer.from(cacheIdentity.canonicalDescriptor.replace('{"$defs"', '{ "$defs"'), "utf8");
  assert.notEqual(noncanonicalCacheBytes.toString("utf8"), cacheIdentity.canonicalDescriptor);
  const noncanonicalBytesCacheContext: LoadedDocument = {
    ...cacheDescriptorContext,
    bytes: noncanonicalCacheBytes,
    descriptorIdentity: await descriptorIdentity(noncanonicalCacheBytes),
  };
  assert.equal(
    await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), mutationWorld([noncanonicalBytesCacheContext])),
    "rpc-authoring.evidence-mismatch",
  );
  const noncanonicalDescriptorBytes = await readFile(resolve(conformanceRoot, "../../conformance/valid/complete.rpc.json"));
  const noncanonicalIdentity = await descriptorIdentity(noncanonicalDescriptorBytes);
  assert.equal(
    canonical.byKey.has(descriptorKey(noncanonicalIdentity.descriptor.contractId, noncanonicalIdentity.descriptor.version)),
    false,
  );
  const noncanonicalCacheReceipt: JsonObject = {
    schemaVersion: 1,
    contractId: noncanonicalIdentity.descriptor.contractId,
    contractVersion: noncanonicalIdentity.descriptor.version,
    descriptorBytes: noncanonicalIdentity.descriptorBytes,
    descriptorSha256: noncanonicalIdentity.descriptorSha256,
    abiSha256: noncanonicalIdentity.abiSha256,
  };
  const noncanonicalCacheContext: LoadedDocument = {
    path: `~/.sunder/contracts/v1/sha256/${noncanonicalIdentity.descriptorSha256}.json`,
    schemaPath: "rpc-v1-descriptor",
    value: noncanonicalIdentity.descriptor as unknown as JsonValue,
    bytes: Buffer.from(noncanonicalIdentity.canonicalDescriptor, "utf8"),
    descriptorIdentity: noncanonicalIdentity,
  };
  const cacheReceiptValidator = validatorFor(schemas, "../cache-entry-receipt.schema.json");
  assert.equal(cacheReceiptValidator(noncanonicalCacheReceipt), true, JSON.stringify(cacheReceiptValidator.errors));
  assert.equal(await cacheReceiptDiagnostic(noncanonicalCacheReceipt, mutationWorld([noncanonicalCacheContext])), null);
  assert.equal(
    await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), mutationWorld([])),
    "rpc-authoring.evidence-mismatch",
  );
  const duplicateCacheWorld = mutationWorld([
    cacheDescriptorContext,
    { ...cacheDescriptorContext, path: "duplicate-cache-descriptor-path" },
  ]);
  assert.equal(
    await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), duplicateCacheWorld),
    "rpc-authoring.evidence-mismatch",
  );
  assert.deepEqual([...duplicateCacheWorld.consumedContexts], []);
  const wrongCachePathContext = { ...cacheDescriptorContext, path: "~/.sunder/contracts/v1/sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json" };
  const wrongCachePathWorld = mutationWorld([wrongCachePathContext]);
  assert.equal(
    await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), wrongCachePathWorld),
    "rpc-authoring.evidence-mismatch",
  );
  assert.deepEqual([...wrongCachePathWorld.consumedContexts], [wrongCachePathContext.path]);
  const wrongDescriptorContext: LoadedDocument = {
    path: cacheDescriptorPath,
    schemaPath: "rpc-v1-descriptor",
    value: unboundedIdentity.descriptor as unknown as JsonValue,
    bytes: Buffer.from(unboundedIdentity.canonicalDescriptor, "utf8"),
    descriptorIdentity: unboundedIdentity,
  };
  assert.equal(
    await cacheReceiptDiagnostic(objectValue(cacheDocument.value, "cache receipt"), mutationWorld([wrongDescriptorContext])),
    "rpc-authoring.evidence-mismatch",
  );

  const coalescedRecord: JsonObject = {
    contractId: "example.aggregate.shared",
    contractVersion: "1.0.0",
    evidenceFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  };
  const coalescedGraph: MaterializationGraph = {
    bundles: [coalescedRecord, cloneJson(coalescedRecord)],
    uses: [],
    providers: [],
    packageDependencies: [],
  };
  assert.equal(materializedCounts({}, coalescedGraph).contractBundles, 1);
  assert.equal(materializationHasConflict(coalescedGraph), false);
  const requiredUse: JsonObject = {
    role: "runtime",
    contractId: "example.aggregate.shared",
    contractVersion: "1.0.0",
    required: false,
    evidenceFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  };
  assert.equal(materializationHasConflict({
    bundles: [],
    uses: [requiredUse, { ...requiredUse, required: true }],
    providers: [],
    packageDependencies: [],
  }), false);
  const conflictingGraph: MaterializationGraph = {
    ...coalescedGraph,
    bundles: [coalescedRecord, { ...coalescedRecord, evidenceFingerprint: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }],
  };
  assert.equal(materializationHasConflict(conflictingGraph), true);

  assert.equal(compareSemanticVersions("1.9.0", "1.10.0"), -1);
  assert.equal(compareSemanticVersions("1.0.0-alpha.2", "1.0.0-alpha.10"), -1);
  assert.equal(compareSemanticVersions("1.0.0-alpha", "1.0.0"), -1);
  assert.equal(compareSemanticVersions("1.0.0+first", "1.0.0+second"), 0);
  assert.equal(validUtcTimestamp("0001-01-01T00:00:00Z"), true);
  assert.equal(validUtcTimestamp("0099-12-31T23:59:59.123456789Z"), true);
  assert.equal(validUtcTimestamp("0001-02-29T00:00:00Z"), false);
  assert.equal(validUtcTimestamp("0099-02-29T00:00:00Z"), false);

  const historySourceIdentity = canonical.byName.get("minimalDescriptor");
  assert.ok(historySourceIdentity);
  const baselineValidator = validatorFor(schemas, "../owned-contracts.schema.json");
  const versionedIdentity = async (version: string): Promise<DescriptorIdentity> => {
    const descriptor = cloneJson(historySourceIdentity.descriptor as unknown as JsonObject);
    descriptor.version = version;
    return descriptorIdentity(JSON.stringify(descriptor));
  };
  const historyIdentities = await Promise.all([versionedIdentity("1.9.0"), versionedIdentity("1.10.0")]);
  const historyIdentityMap = new Map(canonical.byKey);
  for (const identity of historyIdentities) {
    historyIdentityMap.set(descriptorKey(identity.descriptor.contractId, identity.descriptor.version), identity);
  }
  const historySnapshot = (identity: DescriptorIdentity): JsonObject => ({
    state: "release-frozen",
    exposure: "public",
    contractVersion: identity.descriptor.version,
    descriptorBytes: identity.descriptorBytes,
    canonicalDescriptor: identity.canonicalDescriptor,
    descriptorSha256: identity.descriptorSha256,
    abiProjectionBytes: identity.abiProjectionBytes,
    canonicalAbiProjection: identity.canonicalAbiProjection,
    abiSha256: identity.abiSha256,
    packageRelease: { packageId: "example.owner", packageVersion: identity.descriptor.version },
  });
  const semverHistoryBaseline = cloneJson(objectValue(packageBaselineDocument.value, "package baseline"));
  const semverHistoryContract = objectValue(objectValue(semverHistoryBaseline.contracts, "contracts").minimal, "contract");
  semverHistoryContract.history = historyIdentities.map(historySnapshot);
  const historyWorld = (value: JsonObject): Promise<string | null> => baselineDiagnostic(value, {
    schemas,
    contexts: [],
    consumedContexts: new Set(),
    identities: historyIdentityMap,
    immutableArtifacts: expectations.immutableArtifactEvidence,
  });
  assert.equal(baselineValidator(semverHistoryBaseline), true, JSON.stringify(baselineValidator.errors));
  assert.equal(await historyWorld(semverHistoryBaseline), null);
  const reversedSemverHistory = cloneJson(semverHistoryBaseline);
  objectValue(objectValue(reversedSemverHistory.contracts, "contracts").minimal, "contract").history = [
    historySnapshot(historyIdentities[1]!),
    historySnapshot(historyIdentities[0]!),
  ];
  assert.equal(baselineValidator(reversedSemverHistory), true, JSON.stringify(baselineValidator.errors));
  assert.equal(await historyWorld(reversedSemverHistory), "rpc-authoring.order-mismatch");

  const publishedPackageBaseline = cloneJson(objectValue(packageBaselineDocument.value, "package baseline"));
  const publishedPackageSnapshot = objectValue(
    objectValue(objectValue(publishedPackageBaseline.contracts, "baseline contracts").minimal, "baseline contract").current,
    "current snapshot",
  );
  publishedPackageSnapshot.state = "published";
  publishedPackageSnapshot.packageRelease = { packageId: "example.owner", packageVersion: "1.0.0" };
  publishedPackageSnapshot.publication = {
    registryOrigin: "https://registry.example/",
    packageId: "example.owner",
    packageVersion: "1.0.0",
    packageContentIdentity: "3333333333333333333333333333333333333333333333333333333333333333",
    publishedAtUtc: "0001-01-01T00:00:00Z",
    etag: `"${String(publishedPackageSnapshot.descriptorSha256)}"`,
  };
  const publicationInventory = cloneJson(objectValue(packageInventoryDocument.value, "publication inventory"));
  publicationInventory.packageId = "example.owner";
  publicationInventory.packageVersion = "1.0.0";
  publicationInventory.packageContentIdentity = "3333333333333333333333333333333333333333333333333333333333333333";
  const publicationInventoryContract = objectValue(arrayValue(publicationInventory.contracts, "publication contracts")[0], "publication contract");
  publicationInventoryContract.authorityId = "example.owner";
  publicationInventoryContract.selectedAuthorityRelease = "1.0.0";
  publicationInventoryContract.selectedAuthorityContentIdentity = publicationInventory.packageContentIdentity!;
  assert.equal(baselineValidator(publishedPackageBaseline), true, JSON.stringify(baselineValidator.errors));
  const publishedWorld = mutationWorld([
    contextDocument("publication-inventory", "../package-public-inventory.schema.json", publicationInventory),
  ]);
  assert.equal(await baselineDiagnostic(publishedPackageBaseline, publishedWorld), null);
  assert.deepEqual([...publishedWorld.consumedContexts], ["publication-inventory"]);
  const invalidDateBaseline = cloneJson(publishedPackageBaseline);
  objectValue(objectValue(
    objectValue(objectValue(invalidDateBaseline.contracts, "contracts").minimal, "contract").current,
    "snapshot",
  ).publication, "publication").publishedAtUtc = "0001-02-29T00:00:00Z";
  assert.equal(baselineValidator(invalidDateBaseline), true, JSON.stringify(baselineValidator.errors));
  assert.equal(await baselineDiagnostic(invalidDateBaseline, mutationWorld([
    contextDocument("publication-inventory", "../package-public-inventory.schema.json", publicationInventory),
  ])), "rpc-authoring.evidence-mismatch");
  const wrongPackageEtagBaseline = cloneJson(publishedPackageBaseline);
  objectValue(objectValue(
    objectValue(objectValue(wrongPackageEtagBaseline.contracts, "contracts").minimal, "contract").current,
    "snapshot",
  ).publication, "publication").etag = `"${"f".repeat(64)}"`;
  assert.equal(baselineValidator(wrongPackageEtagBaseline), true, JSON.stringify(baselineValidator.errors));
  assert.equal(await baselineDiagnostic(wrongPackageEtagBaseline, mutationWorld([
    contextDocument("publication-inventory", "../package-public-inventory.schema.json", publicationInventory),
  ])), "rpc-authoring.evidence-mismatch");

  const platformBaselineDocument = documents.get("valid/platform-owned-baseline.json");
  assert.ok(platformBaselineDocument);
  const publishedPlatformBaseline = cloneJson(objectValue(platformBaselineDocument.value, "platform baseline"));
  const publishedPlatformSnapshot = objectValue(
    objectValue(objectValue(publishedPlatformBaseline.contracts, "platform contracts").stackContributor, "platform contract").current,
    "platform snapshot",
  );
  publishedPlatformSnapshot.state = "published";
  publishedPlatformSnapshot.catalogRelease = { authorityId: "sunder.core", releaseVersion: "1.1.0" };
  publishedPlatformSnapshot.publication = {
    registryOrigin: "https://registry.example/",
    authorityId: "sunder.core",
    catalogRelease: "1.1.0",
    catalogContentIdentity: catalogContentIdentity(catalog, schemas),
    publishedAtUtc: "0099-12-31T23:59:59.123456789Z",
    etag: `"${String(publishedPlatformSnapshot.descriptorSha256)}"`,
  };
  assert.equal(baselineValidator(publishedPlatformBaseline), true, JSON.stringify(baselineValidator.errors));
  const platformBaselineWorld = mutationWorld([catalogDocument]);
  assert.equal(await baselineDiagnostic(publishedPlatformBaseline, platformBaselineWorld), null);
  assert.deepEqual([...platformBaselineWorld.consumedContexts], [catalogDocument.path]);
  const wrongCatalogBaseline = cloneJson(publishedPlatformBaseline);
  objectValue(objectValue(
    objectValue(objectValue(wrongCatalogBaseline.contracts, "platform contracts").stackContributor, "platform contract").current,
    "platform snapshot",
  ).publication, "platform publication").catalogContentIdentity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  assert.equal(
    await baselineDiagnostic(wrongCatalogBaseline, mutationWorld([catalogDocument])),
    "rpc-authoring.authority-mismatch",
  );
  const wrongPlatformEtagBaseline = cloneJson(publishedPlatformBaseline);
  objectValue(objectValue(
    objectValue(objectValue(wrongPlatformEtagBaseline.contracts, "platform contracts").stackContributor, "platform contract").current,
    "platform snapshot",
  ).publication, "platform publication").etag = `"${"f".repeat(64)}"`;
  assert.equal(
    await baselineDiagnostic(wrongPlatformEtagBaseline, mutationWorld([catalogDocument])),
    "rpc-authoring.evidence-mismatch",
  );

  const baselineSchema = schemas.get("owned-contracts.schema.json");
  assert.ok(baselineSchema);
  const reorderedBaseline: JsonObject = {
    contracts: cloneJson(objectValue(objectValue(packageBaselineDocument.value, "baseline").contracts, "contracts")),
    packageId: "example.owner",
    authorityId: "example.owner",
    authorityKind: "package",
    schemaVersion: 1,
  };
  assert.equal(
    writeDocument(reorderedBaseline, baselineSchema.root, baselineSchema.root),
    packageBaselineDocument.bytes.toString("utf8"),
  );
  const unorderedMapDocument = documents.get("semantic-invalid/baseline-order-mismatch.json");
  assert.ok(unorderedMapDocument);
  const orderedMapBytes = writeDocument(
    unorderedMapDocument.value,
    baselineSchema.root,
    baselineSchema.root,
  );
  assert.ok(orderedMapBytes.indexOf('"aBase64"') < orderedMapBytes.indexOf('"zMinimal"'));
  assert.equal(
    writeDocument(parseOuterJson(Buffer.from(orderedMapBytes, "utf8"), orderedMapBytes.length, 16), baselineSchema.root, baselineSchema.root),
    orderedMapBytes,
  );

  const stringSchema: JsonObject = {
    type: "object",
    additionalProperties: false,
    properties: { text: { type: "string" } },
  };
  const scalarText = `"\\\b\t\n\f\r\u0000\u001f/<> &\u00e9\u{1f600}`;
  const scalarJson = writeCompact({ text: scalarText }, stringSchema, stringSchema);
  assert.equal(scalarJson, `{"text":"\\"\\\\\\b\\t\\n\\f\\r\\u0000\\u001f/<> &\u00e9\u{1f600}"}`);
  assert.equal(scalarJson.includes("\\/"), false);
  assert.equal(scalarJson.includes("\\u00e9"), false);
  assert.equal(scalarJson.includes("\\ud83d"), false);
  assert.deepEqual(parseOuterJson(Buffer.from(scalarJson, "utf8"), 1024), { text: scalarText });

  assert.throws(() => parseOuterJson(Buffer.from([0xef, 0xbb, 0xbf, 0x7b, 0x7d]), 16), /BOM/u);
  assert.throws(() => parseOuterJson(Buffer.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d]), 16), /UTF-8/u);
  assert.throws(() => parseOuterJson(Buffer.alloc(byteLimits["cache-entry-receipt.schema.json"]! + 1, 0x20), byteLimits["cache-entry-receipt.schema.json"]!), /exceeds/u);
  assert.throws(() => parseOuterJson(Buffer.from(`{"x":${"[".repeat(16)}0${"]".repeat(16)}}`), 1024), /depth/u);
  assert.throws(() => parseOuterJson(Buffer.from("{\"x\":1,\"x\":2}"), 1024), /Duplicate/u);
  assert.throws(() => parseOuterJson(Buffer.from("{\"Name\":1,\"name\":2}"), 1024), /case-colliding/u);
  assert.throws(() => parseOuterJson(Buffer.from("{\"A\":1,\"\\u0061\":2}"), 1024), /case-colliding/u);
  assert.throws(() => parseOuterJson(Buffer.from("{\"x\":\"\\ud800\"}"), 1024), /surrogate/u);
  assert.throws(() => parseOuterJson(Buffer.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0x1f, 0x22, 0x7d]), 1024), /control character/u);
  assert.throws(() => parseOuterJson(Buffer.from("{\"x\":1e0}"), 1024), /canonical integer/u);
});
