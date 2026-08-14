import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";
import { runInNewContext } from "node:vm";
import {
  RpcDescriptorError,
  parseRpcContractDescriptor,
  validateRpcContractDescriptor,
} from "../src/descriptor";
import { computeRpcDescriptorSha256 as computeBrowserRpcDescriptorSha256 } from "../src/browser";
import {
  RpcInstanceError,
  SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  rpcAbiV2Projection,
  validateRpcInstance,
} from "../src/descriptor-v2";
import { generateTypeScriptBindings } from "../src/generator";
import type { JsonSchema, RpcContractDescriptorV2 } from "../src/types";

interface ConformanceExpectations {
  readonly formatVersion: number;
  readonly descriptorVersion: number;
  readonly hashes: {
    readonly abiDomainSeparatorHex: string;
  };
  readonly fixtures: readonly ConformanceFixture[];
}

interface ConformanceFixture {
  readonly path: string;
  readonly valid: boolean;
  readonly schemaValid: boolean;
  readonly descriptorSha256?: string;
  readonly abiSha256?: string;
  readonly canonicalDescriptorBytes?: number;
  readonly canonicalAbiProjectionBytes?: number;
  readonly errorCode?: string;
}

const conformanceRoot = resolve(__dirname, "../../../../../../../specs/rpc/v2/conformance");
const expectations = JSON.parse(
  readFileSync(resolve(conformanceRoot, "expectations.json"), "utf8"),
) as ConformanceExpectations;

assert.equal(expectations.formatVersion, 1);
assert.equal(expectations.descriptorVersion, 2);
assert.equal(
  Buffer.from(SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR, "utf8").toString("hex"),
  expectations.hashes.abiDomainSeparatorHex,
);

for (const fixture of expectations.fixtures) {
  test(`RPC V2 conformance: ${fixture.path}`, async () => {
    const source = readFileSync(resolve(conformanceRoot, fixture.path));
    if (!fixture.valid) {
      let failure: unknown;
      try {
        parseRpcContractDescriptor(source);
      } catch (error) {
        failure = error;
      }
      assert.ok(failure instanceof RpcDescriptorError, `Expected '${fixture.path}' to be rejected.`);
      assert.equal(failure.code, fixture.errorCode);
      return;
    }

    const descriptor = parseRpcContractDescriptor(source);
    assert.equal(descriptor.descriptorVersion, 2);
    if (descriptor.descriptorVersion !== 2) assert.fail("Expected a V2 descriptor.");

    const canonicalDescriptor = canonicalRpcDescriptorBytes(descriptor);
    const canonicalAbi = canonicalRpcAbiProjectionBytes(descriptor);
    assert.equal(canonicalDescriptor.byteLength, fixture.canonicalDescriptorBytes);
    assert.equal(canonicalAbi.byteLength, fixture.canonicalAbiProjectionBytes);
    assert.equal(await computeRpcDescriptorSha256(descriptor), fixture.descriptorSha256);
    assert.equal(await computeRpcAbiSha256(descriptor), fixture.abiSha256);
    assert.deepEqual(rpcAbiV2Projection(descriptor), rpcAbiV2Projection(descriptor));
  });
}

test("V2 ABI projection and hashes retain prototype-sensitive JSON keys", async () => {
  const descriptor = parseRpcContractDescriptor(readFileSync(resolve(
    __dirname,
    "../../test/fixtures/abi-special-property-names.rpc.json",
  )));
  if (descriptor.descriptorVersion !== 2) assert.fail("Expected a V2 descriptor.");

  const projection = rpcAbiV2Projection(descriptor);
  const definitions = (projection as unknown as {
    readonly $defs: Readonly<Record<string, unknown>>;
  }).$defs;
  assert.equal(Object.getPrototypeOf(definitions), null);
  assert.deepEqual(Object.keys(definitions).sort(), ["__proto__", "constructor", "prototype"]);

  const specialSchema = definitions["__proto__"] as {
    readonly properties: Readonly<Record<string, unknown>>;
  };
  assert.equal(Object.getPrototypeOf(specialSchema.properties), null);
  assert.deepEqual(Object.keys(specialSchema.properties).sort(), ["__proto__", "constructor", "prototype"]);

  const canonicalProjection = new TextDecoder().decode(canonicalRpcAbiProjectionBytes(descriptor));
  for (const name of ["__proto__", "constructor", "prototype"]) {
    assert.match(canonicalProjection, new RegExp(`"${name}"`, "u"));
  }
  assert.equal(await computeRpcDescriptorSha256(descriptor), "2f6e5ac04ddcaa9f0fb98b6445a7af51d73f6effec7edfb3ec90544d42ef50fa");
  assert.equal(await computeRpcAbiSha256(descriptor), "56450f51a3e705fe1d7ba3670bfb7f7f36cb6935490cdf213651cdaf774097d1");
  assert.equal(await computeBrowserRpcDescriptorSha256(descriptor), "2f6e5ac04ddcaa9f0fb98b6445a7af51d73f6effec7edfb3ec90544d42ef50fa");
});

test("nullable unions resolve null recursively and reject a second nested null-accepting branch", () => {
  const valid = descriptorWithDefinitions({
    Null: { type: "null" },
    NullAlias: { $ref: "#/$defs/Null" },
    Text: { type: "string", minLength: 0, maxLength: 16 },
    Nullable: {
      oneOf: [
        { $ref: "#/$defs/Text" },
        { $ref: "#/$defs/NullAlias" },
      ],
    },
  }, "Nullable");
  assert.doesNotThrow(() => validateRpcContractDescriptor(valid));
  const projectedDefinitions = (rpcAbiV2Projection(valid) as unknown as {
    readonly $defs: Readonly<Record<string, { readonly oneOf?: readonly unknown[] }>>;
  }).$defs;
  assert.deepEqual(projectedDefinitions.Nullable?.oneOf, [{ $ref: "#/$defs/NullAlias" }, { $ref: "#/$defs/Text" }]);

  const ambiguous = descriptorWithDefinitions({
    Null: { type: "null" },
    NullAlias: { $ref: "#/$defs/Null" },
    Text: { type: "string", minLength: 0, maxLength: 16 },
    NestedNullable: {
      oneOf: [
        { $ref: "#/$defs/NullAlias" },
        { $ref: "#/$defs/Text" },
      ],
    },
    Ambiguous: {
      oneOf: [
        { $ref: "#/$defs/Null" },
        { $ref: "#/$defs/NestedNullable" },
      ],
    },
  }, "Ambiguous");
  assert.throws(
    () => validateRpcContractDescriptor(ambiguous),
    (error: unknown) => error instanceof RpcDescriptorError
      && error.code === "schema-profile"
      && /exactly one branch accepting null/u.test(error.message),
  );
});

test("descriptor parsing admits the deepest legal schema and rejects semantic depth 65", () => {
  const legal = JSON.stringify(descriptorWithDefinitions({ Deep: nestedRecordSchema(64) }, "Deep"));
  assert.doesNotThrow(() => parseRpcContractDescriptor(legal));

  const tooDeep = JSON.stringify(descriptorWithDefinitions({ Deep: nestedRecordSchema(65) }, "Deep"));
  assert.throws(
    () => parseRpcContractDescriptor(tooDeep),
    (error: unknown) => error instanceof RpcDescriptorError
      && error.code === "descriptor-invalid"
      && /schema depth or node-count limit/u.test(error.message),
  );
});

test("descriptor parsing has no arbitrary token cap below the normative byte and semantic limits", () => {
  const errorCodes = Array.from({ length: 256 }, (_, index) => `e${index}`);
  const errors = Object.fromEntries(errorCodes.map((code) => [code, {}]));
  const methods = Object.fromEntries(Array.from({ length: 512 }, (_, index) => [
    `m${index}`,
    {
      kind: "unary",
      idempotency: "idempotent",
      errors: errorCodes,
      requestSchema: { $ref: "#/$defs/Empty" },
      responseSchema: { $ref: "#/$defs/Empty" },
    },
  ]));
  const source = JSON.stringify({
    descriptorVersion: 2,
    contractId: "example.rpc.v2.manytokens",
    version: "2.0.0",
    errors,
    services: { bulk: { methods } },
    $defs: {
      Empty: { type: "object", properties: {}, required: [], additionalProperties: false },
    },
  });
  assert.ok(Buffer.byteLength(source) < 4 * 1024 * 1024);
  const parsed = parseRpcContractDescriptor(source);
  if (parsed.descriptorVersion !== 2) assert.fail("Expected a V2 descriptor.");
  assert.equal(Object.keys(parsed.services.bulk?.methods ?? {}).length, 512);
});

test("descriptor member collisions retain V1 Unicode folding and use ASCII-only folding in V2", () => {
  const valueSchema: JsonSchema = {
    type: "object",
    properties: {
      k: { type: "boolean" },
      "\u212a": { type: "boolean" },
    },
    required: ["k", "\u212a"],
    additionalProperties: false,
  };
  const v1Source = JSON.stringify({
    contractId: "example.rpc.v1.casefold",
    version: "1.0.0",
    services: [{
      serviceId: "example",
      methods: [{
        methodId: "read",
        kind: "unary",
        requestSchema: { $ref: "#/$defs/Value" },
        responseSchema: { $ref: "#/$defs/Value" },
      }],
    }],
    $defs: { Value: valueSchema },
    descriptorVersion: 1,
  });
  assert.throws(
    () => parseRpcContractDescriptor(v1Source),
    (error: unknown) => error instanceof RpcDescriptorError
      && error.code === "descriptor-json"
      && /case-colliding property '\u212a'/iu.test(error.message),
  );

  const v2 = descriptorWithDefinitions({ Value: valueSchema }, "Value");
  const { descriptorVersion, ...v2BeforeVersion } = v2;
  assert.doesNotThrow(() => parseRpcContractDescriptor(JSON.stringify({ ...v2BeforeVersion, descriptorVersion })));
});

test("V2 runtime validation preserves presence and enforces binary64 and safe-integer semantics", () => {
  const descriptor = completeDescriptor();
  const content = validContentReference();
  const request = {
    correlationId: "00000000-0000-0000-0000-000000000000",
    observedAt: "2000-02-29T23:59:59.123456789Z",
    content,
    count: 0,
    priority: 1,
    ratio: 0.25,
    note: null,
    tags: [],
  };

  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Request", request));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Request", { ...request, optionalNote: null }));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Request", { ...request, optionalNote: "present" }));
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Request", { ...request, optionalLabel: null }), RpcInstanceError);
  const { note: _note, ...missingRequired } = request;
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Request", missingRequired), RpcInstanceError);

  for (const value of [0, -0, 9007199254740991]) {
    assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/SafeCount", value));
  }
  for (const value of [0.5, 9007199254740992, Number.NaN, Number.POSITIVE_INFINITY]) {
    assert.throws(() => validateRpcInstance(descriptor, "#/$defs/SafeCount", value), RpcInstanceError);
  }
  for (const value of [-0, 0.5, -999.75, 1000]) {
    assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Ratio", value));
  }
  for (const value of [Number.NaN, Number.NEGATIVE_INFINITY, 1000.5]) {
    assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Ratio", value), RpcInstanceError);
  }
});

test("V2 runtime validation enforces canonical UUID, UTC date-time, and content-reference values", () => {
  const descriptor = completeDescriptor();
  for (const value of [
    "00000000-0000-0000-0000-000000000000",
    "ffffffff-ffff-ffff-ffff-ffffffffffff",
  ]) assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Uuid", value));
  for (const value of [
    "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
    "ffffffffffffffffffffffffffffffff",
    "{ffffffff-ffff-ffff-ffff-ffffffffffff}",
    " ffffffff-ffff-ffff-ffff-ffffffffffff",
  ]) assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Uuid", value), RpcInstanceError);

  for (const value of [
    "0001-01-01T00:00:00Z",
    "2000-02-29T23:59:59.1Z",
    "9999-12-31T23:59:59.123456789Z",
  ]) assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Instant", value));
  for (const value of [
    "0000-01-01T00:00:00Z",
    "1900-02-29T00:00:00Z",
    "2000-02-29T23:59:60Z",
    "2000-02-29T23:59:59.10Z",
    "2000-02-29t23:59:59z",
    "2000-02-29T23:59:59+00:00",
  ]) assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Instant", value), RpcInstanceError);

  const content = validContentReference();
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Content", content));
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, extra: true }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, length: 9007199254740992 }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, sha256: "A".repeat(64) }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, mediaType: "text/plain\u{100}" }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, expiresAtUtc: "2030-01-02T03:04:05.0Z" }), RpcInstanceError);
});

test("runtime validation accepts only JSON object and array instances", () => {
  const descriptor = descriptorWithDefinitions({
    Empty: { type: "object", properties: {}, required: [], additionalProperties: false },
    Value: {
      type: "object",
      properties: { value: { type: "boolean" } },
      required: ["value"],
      additionalProperties: false,
    },
    Values: {
      type: "array",
      items: { type: "boolean" },
      minItems: 0,
      maxItems: 4,
    },
  }, "Value");

  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Value", { value: true }));
  const nullPrototype = Object.create(null) as Record<string, unknown>;
  nullPrototype.value = true;
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Value", nullPrototype));
  assert.doesNotThrow(() => validateRpcInstance(
    descriptor,
    "#/$defs/Value",
    runInNewContext("({ value: true })") as unknown,
  ));

  class ValueInstance {
    public readonly value = true;
  }
  assert.throws(
    () => validateRpcInstance(descriptor, "#/$defs/Value", new ValueInstance()),
    /plain object or null-prototype record/u,
  );
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Empty", new Date(0)), RpcInstanceError);

  let accessorReads = 0;
  const accessorValue = {};
  Object.defineProperty(accessorValue, "value", {
    enumerable: true,
    get: () => {
      accessorReads++;
      return true;
    },
  });
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Value", accessorValue), /data property/u);
  assert.equal(accessorReads, 0);

  const customSerialization = { value: true };
  Object.defineProperty(customSerialization, "toJSON", {
    value: () => ({ value: false }),
  });
  assert.throws(
    () => validateRpcInstance(descriptor, "#/$defs/Value", customSerialization),
    /enumerable JSON property/u,
  );

  const symbolKeyed = { value: true };
  Object.defineProperty(symbolKeyed, Symbol("hidden"), { value: false, enumerable: true });
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Value", symbolKeyed), /symbol-keyed property/u);

  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Values", [true, false]));
  assert.doesNotThrow(() => validateRpcInstance(
    descriptor,
    "#/$defs/Values",
    runInNewContext("[true, false]") as unknown,
  ));
  const customizedArray = [true];
  Object.defineProperty(customizedArray, "toJSON", { value: () => [] });
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Values", customizedArray), /JSON array element/u);
});

test("V2 descriptor member collision folding is ASCII-only and strings require Unicode scalar values", () => {
  const descriptor: RpcContractDescriptorV2 = {
    descriptorVersion: 2,
    contractId: "example.rpc.v2.casefold",
    version: "2.0.0",
    errors: {},
    services: {
      example: {
        methods: {
          read: {
            kind: "unary",
            idempotency: "idempotent",
            errors: [],
            requestSchema: { $ref: "#/$defs/Value" },
            responseSchema: { $ref: "#/$defs/Value" },
          },
        },
      },
    },
    $defs: {
      Value: {
        type: "object",
        properties: {
          k: { type: "boolean" },
          "\u212a": { type: "boolean" },
        },
        required: ["k", "\u212a"],
        additionalProperties: false,
        title: "Two distinct Unicode scalar names: \u{1f642}",
      },
    },
  };
  assert.doesNotThrow(() => validateRpcContractDescriptor(descriptor));

  const source = JSON.stringify(descriptor);
  assert.throws(() => parseRpcContractDescriptor(source.replaceAll("\u212a", "K")), /case-colliding/iu);
  const invalidScalarSource = source.replace(/"title":"[^"]*"/u, '"title":"\\ud800"');
  assert.notEqual(invalidScalarSource, source);
  assert.throws(
    () => parseRpcContractDescriptor(invalidScalarSource),
    /unpaired UTF-16 surrogate/u,
  );
});

test("the current TypeScript generator consumes validated V2 keyed descriptors", () => {
  const generated = generateTypeScriptBindings(completeDescriptor());
  assert.match(generated, /export const CONTRACT_ID = "example\.rpc\.v2\.conformance" as const;/u);
  assert.match(generated, /export interface IItemsProvider/u);
  assert.match(generated, /Submit\(request: Request, context: RpcInvocationContext\): Promise<Result>;/u);
  assert.match(generated, /Watch\(request: Request, context: RpcInvocationContext\): AsyncIterable<Event>;/u);
});

function completeDescriptor(): RpcContractDescriptorV2 {
  const descriptor = parseRpcContractDescriptor(
    readFileSync(resolve(conformanceRoot, "valid/complete.rpc.json")),
  );
  if (descriptor.descriptorVersion !== 2) throw new Error("The complete conformance descriptor is not V2.");
  return descriptor;
}

function descriptorWithDefinitions(
  definitions: Readonly<Record<string, JsonSchema>>,
  entryDefinition: string,
): RpcContractDescriptorV2 {
  return {
    descriptorVersion: 2,
    contractId: "example.rpc.v2.regression",
    version: "2.0.0",
    errors: {},
    services: {
      example: {
        methods: {
          read: {
            kind: "unary",
            idempotency: "idempotent",
            errors: [],
            requestSchema: { $ref: `#/$defs/${entryDefinition}` },
            responseSchema: { $ref: `#/$defs/${entryDefinition}` },
          },
        },
      },
    },
    $defs: definitions,
  };
}

function nestedRecordSchema(depth: number): JsonSchema {
  let schema: JsonSchema = { type: "boolean" };
  for (let level = 1; level < depth; level++) {
    schema = {
      type: "object",
      properties: { next: schema },
      required: ["next"],
      additionalProperties: false,
    };
  }
  return schema;
}

function validContentReference(): Readonly<Record<string, string | number>> {
  return {
    id: "opaque-content-id",
    length: 0,
    sha256: "0".repeat(64),
    mediaType: "application/octet-stream",
    fileName: "display-name.bin",
    expiresAtUtc: "2030-01-02T03:04:05Z",
    repeatability: "single-use",
  };
}
