import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
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
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  rpcAbiV1Projection,
  validateRpcInstance,
} from "../src/descriptor-v1";
import type { JsonSchema, RpcContractDescriptor } from "../src/types";

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

const conformanceRoot = resolve(__dirname, "../../../../../../../specs/rpc/v1/conformance");

test("published SDK build excludes the removed RPC V2 module", () => {
  const distRoot = resolve(__dirname, "..", "..", "dist");
  for (const fileName of ["descriptor-v2.js", "descriptor-v2.d.ts", "descriptor-v2.js.map"]) {
    assert.equal(existsSync(resolve(distRoot, fileName)), false);
  }
});
const expectations = readConformanceExpectations();
const hasValueProfileCorpus = (() => {
  try {
    return expectations.fixtures.some((fixture) => fixture.path === "valid/value-profile.rpc.json")
      && expectations.fixtures.every((fixture) => {
        const value = JSON.parse(readFileSync(resolve(conformanceRoot, fixture.path), "utf8")) as { readonly schemaProfile?: unknown };
        return !fixture.valid || value.schemaProfile === "sunder.rpc.values.v1";
      });
  } catch {
    return false;
  }
})();

assert.equal(expectations.formatVersion, 1);
assert.equal(expectations.descriptorVersion, 1);
assert.equal(
  Buffer.from(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8").toString("hex"),
  expectations.hashes.abiDomainSeparatorHex,
);

function readConformanceExpectations(): ConformanceExpectations {
  try {
    return JSON.parse(readFileSync(resolve(conformanceRoot, "expectations.json"), "utf8")) as ConformanceExpectations;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    return {
      formatVersion: 1,
      descriptorVersion: 1,
      hashes: { abiDomainSeparatorHex: "73756e6465722e7270632e6162692e763100" },
      fixtures: [],
    };
  }
}

for (const fixture of expectations.fixtures) {
  test(`RPC V1 conformance: ${fixture.path}`, {
    skip: hasValueProfileCorpus ? false : "Shared value-profile fixtures have not landed yet.",
  }, async () => {
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
    assert.equal(descriptor.descriptorVersion, 1);

    const canonicalDescriptor = canonicalRpcDescriptorBytes(descriptor);
    const canonicalAbi = canonicalRpcAbiProjectionBytes(descriptor);
    assert.equal(canonicalDescriptor.byteLength, fixture.canonicalDescriptorBytes);
    assert.equal(canonicalAbi.byteLength, fixture.canonicalAbiProjectionBytes);
    assert.equal(await computeRpcDescriptorSha256(descriptor), fixture.descriptorSha256);
    assert.equal(await computeRpcAbiSha256(descriptor), fixture.abiSha256);
    assert.deepEqual(rpcAbiV1Projection(descriptor), rpcAbiV1Projection(descriptor));
  });
}

test("V1 ABI projection and hashes retain prototype-sensitive JSON keys", async () => {
  const descriptor = parseRpcContractDescriptor(readFileSync(resolve(
    __dirname,
    "../../test/fixtures/abi-special-property-names.rpc.json",
  )));
  const projection = rpcAbiV1Projection(descriptor);
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
  assert.equal(await computeRpcDescriptorSha256(descriptor), "233dffef18f2f14c2316d4f075022a9d3f9c1fd80c1d31a404f49b939b811995");
  assert.equal(await computeRpcAbiSha256(descriptor), "8ccaa10fa8a1939d41337b424e2aa79031138310907f7d147f80529913ee5438");
  assert.equal(await computeBrowserRpcDescriptorSha256(descriptor), "233dffef18f2f14c2316d4f075022a9d3f9c1fd80c1d31a404f49b939b811995");
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
  const projectedDefinitions = (rpcAbiV1Projection(valid) as unknown as {
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

  const unconstrainedAmbiguous = descriptorWithDefinitions({
    Ambiguous: { oneOf: [{ type: "null" }, {}] },
  }, "Ambiguous");
  assert.throws(
    () => validateRpcContractDescriptor(unconstrainedAmbiguous),
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

test("nested nullable unions count each resolved schema edge once", () => {
  const legal = JSON.stringify(descriptorWithDefinitions({ Deep: nestedNullableRecordSchema(22) }, "Deep"));
  assert.doesNotThrow(() => parseRpcContractDescriptor(legal));

  const tooDeep = JSON.stringify(descriptorWithDefinitions({ Deep: nestedNullableRecordSchema(32) }, "Deep"));
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
    descriptorVersion: 1,
    schemaProfile: "sunder.rpc.values.v1",
    contractId: "example.rpc.v1.manytokens",
    version: "2.0.0",
    errors,
    services: { bulk: { methods } },
    $defs: {
      Empty: { type: "object", properties: {}, required: [], additionalProperties: false },
    },
  });
  assert.ok(Buffer.byteLength(source) < 4 * 1024 * 1024);
  const parsed = parseRpcContractDescriptor(source);
  assert.equal(Object.keys(parsed.services.bulk?.methods ?? {}).length, 512);
});

test("descriptor parsing rejects impossible oversized JSON containers before materializing their values", () => {
  const source = `{"unexpected":[${Array.from({ length: 513 }, () => "{}").join(",")}]}`;
  assert.throws(
    () => parseRpcContractDescriptor(source),
    (error: unknown) => error instanceof RpcDescriptorError
      && error.code === "descriptor-representation"
      && /at most 512 entries/u.test(error.message),
  );
});

test("descriptor member collisions use ASCII-only folding", () => {
  const valueSchema: JsonSchema = {
    type: "object",
    properties: {
      k: { type: "boolean" },
      "\u212a": { type: "boolean" },
    },
    required: ["k", "\u212a"],
    additionalProperties: false,
  };
  const descriptor = descriptorWithDefinitions({ Value: valueSchema }, "Value");
  assert.doesNotThrow(() => parseRpcContractDescriptor(JSON.stringify(descriptor)));
  assert.throws(
    () => parseRpcContractDescriptor(JSON.stringify(descriptor).replace('"\u212a"', '"K"')),
    (error: unknown) => error instanceof RpcDescriptorError
      && error.code === "descriptor-json"
      && /case-colliding property 'K'/u.test(error.message),
  );
});

test("V1 runtime validation preserves presence and enforces binary64 and safe-integer semantics", () => {
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
  for (const value of [Number.NaN, Number.NEGATIVE_INFINITY, 1001]) {
    assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Ratio", value), RpcInstanceError);
  }
});

test("V1 runtime validation enforces asserted UUID/date-time and canonical content-reference values", () => {
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
    "2030-01-02T03:04:05.10Z",
    "2030-01-02T03:04:05+00:00",
    "2030-01-02T03:04:05-14:00",
  ]) assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Instant", value));
  for (const value of [
    "0000-01-01T00:00:00Z",
    "1900-02-29T00:00:00Z",
    "2000-02-29T23:59:60Z",
    "2000-02-29t23:59:59z",
    "2000-02-29T23:59:59+14:01",
    "2000-02-29T23:59:59.1234567890Z",
  ]) assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Instant", value), RpcInstanceError);

  const content = validContentReference();
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Content", content));
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, extra: true }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, length: 9007199254740992 }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, sha256: "A".repeat(64) }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, mediaType: "text/plain\u{100}" }), RpcInstanceError);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Content", { ...content, expiresAtUtc: "2030-01-02T03:04:05.0Z" }), RpcInstanceError);
});

test("V1 URI validation uses the frozen RFC 3986 lexical profile", () => {
  const descriptor = descriptorWithDefinitions({ Uri: { type: "string", format: "uri" } }, "Uri");
  for (const value of [
    "https://example.test/a%20b",
    "x:",
    "https:example.com",
    "https:/example.com",
    "http://256.0.0.1",
    "http://1.2.3.999",
  ]) assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Uri", value));
  for (const value of [
    "relative/path",
    "https://example.test/%ZZ",
    "x:\"",
    "x:<",
    "x://host|name/path",
    "x:a#b#c",
  ]) assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Uri", value), RpcInstanceError);
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

test("V1 descriptor member collision folding is ASCII-only and strings require Unicode scalar values", () => {
  const descriptor: RpcContractDescriptor = {
    descriptorVersion: 1,
    schemaProfile: "sunder.rpc.values.v1",
    contractId: "example.rpc.v1.casefold",
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

test("V1 runtime JSON rejects unpaired property names and ASCII case collisions", () => {
  const descriptor = descriptorWithDefinitions({ Any: {} }, "Any");
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Any", { A: true, a: false }), /case-colliding/u);
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Any", { "\ud800": true }), /unpaired UTF-16 surrogate/u);
});

test("V1 requires the value profile and validates its exact schema forms, error details, and productive recursion", () => {
  const descriptor = descriptorWithDefinitions(valueProfileDefinitions(), "Value");
  const withDetails: RpcContractDescriptor = {
    ...descriptor,
    errors: {
      "example.details": {
        description: "Structured failure details.",
        detailsSchema: { $ref: "#/$defs/Decimal" },
      },
    },
    services: {
      example: {
        methods: {
          read: {
            ...descriptor.services.example!.methods.read!,
            errors: ["example.details"],
          },
        },
      },
    },
  };
  assert.doesNotThrow(() => validateRpcContractDescriptor(withDetails));
  const projection = rpcAbiV1Projection(withDetails) as unknown as {
    readonly schemaProfile: string;
    readonly errors: Readonly<Record<string, unknown>>;
  };
  assert.equal(projection.schemaProfile, "sunder.rpc.values.v1");
  assert.equal(
    JSON.stringify(projection.errors["example.details"]),
    JSON.stringify({ detailsSchema: { $ref: "#/$defs/Decimal" } }),
  );

  const { schemaProfile: _profile, ...missingProfile } = descriptor;
  assert.throws(
    () => validateRpcContractDescriptor(missingProfile),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "schema-profile",
  );
  assert.throws(
    () => validateRpcContractDescriptor({ ...descriptor, schemaProfile: "other" }),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "schema-profile",
  );
  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({ Value: { type: "number" } }, "Value")),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "schema-profile",
  );
  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({
      Value: { type: "string", logicalType: "uuid" },
    }, "Value")),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "schema-profile",
  );
  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({
      Value: { type: "string", format: "big-integer", maxDigits: 3, valueMinimum: "01" },
    }, "Value")),
    /canonical numeric string/u,
  );
  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({
      A: { $ref: "#/$defs/B" },
      B: { $ref: "#/$defs/A" },
    }, "A")),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "schema-reference-cycle",
  );
  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({
      RequiredNode: {
        type: "object",
        properties: { next: { $ref: "#/$defs/RequiredNode" } },
        required: ["next"],
        additionalProperties: false,
      },
    }, "RequiredNode")),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "schema-reference-cycle",
  );
  assert.doesNotThrow(() => validateRpcContractDescriptor(descriptorWithDefinitions({
    RecursiveArray: {
      type: "array",
      items: { $ref: "#/$defs/RecursiveArray" },
      minItems: 0,
      maxItems: 4,
    },
  }, "RecursiveArray")));
});

test("V1 value-profile instance validation covers portable JSON, asserted formats, exact numbers, bytes, and recursion", () => {
  const descriptor = descriptorWithDefinitions(valueProfileDefinitions(), "Value");
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Any", {
    nested: [true, 0.1, { value: -0 }],
  }));
  assert.throws(
    () => validateRpcInstance(descriptor, "#/$defs/Any", { nested: [Number.POSITIVE_INFINITY] }),
    /finite JSON number/u,
  );
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Dynamic", { any: [1, 2, 3] }));
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Dynamic", []), RpcInstanceError);
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/DynamicBounded", { first: 1, second: 2 }));
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/DynamicBounded", { first: 1, second: 2, third: 3 }), RpcInstanceError);

  const accepted: Readonly<Record<string, readonly unknown[]>> = {
    Uuid: ["00000000-0000-0000-0000-000000000000"],
    Date: ["0001-01-01", "2000-02-29", "9999-12-31"],
    DateTime: ["2030-01-02T03:04:05Z", "2030-01-02T03:04:05.123Z", "2030-01-02T03:04:05+05:30"],
    Duration: ["P1D", "PT0S", "P1DT2H3M4.5S"],
    Uri: ["https://example.test/a?b=c#d", "urn:example:test"],
    BigInteger: ["-100", "0", "999"],
    Decimal: ["-10", "0", "0.001", "999.999"],
    Float32: [0.1, Math.fround(1 / 3), 3.4028234663852886e38],
    Float64: [-Number.MAX_VALUE, 0.1, Number.MAX_VALUE],
    Bytes: ["", "/w==", "AQIDBA=="],
  };
  for (const [definition, values] of Object.entries(accepted)) {
    for (const value of values) assert.doesNotThrow(
      () => validateRpcInstance(descriptor, `#/$defs/${definition}`, value),
      `${definition}: ${String(value)}`,
    );
  }

  const rejected: Readonly<Record<string, readonly unknown[]>> = {
    Uuid: ["FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"],
    Date: ["1900-02-29", "2000-1-01"],
    DateTime: ["2030-01-02t03:04:05z", "2030-01-02T03:04:05+14:01"],
    Duration: ["P", "P01D", "PT1.0S"],
    Uri: ["relative/path", "https://example.test/a b"],
    BigInteger: ["01", "-0", "1000", "10000"],
    Decimal: ["-10.5", "1.230", "0.0001", "1000"],
    Float32: [3.5e38, Number.NaN],
    Float64: [Number.POSITIVE_INFINITY],
    Bytes: ["/x==", "AQIDBA", "AQIDBAU="],
  };
  for (const [definition, values] of Object.entries(rejected)) {
    for (const value of values) assert.throws(
      () => validateRpcInstance(descriptor, `#/$defs/${definition}`, value),
      RpcInstanceError,
      `${definition}: ${String(value)}`,
    );
  }

  let recursive: Record<string, unknown> = { value: 100 };
  for (let index = 99; index >= 0; index--) recursive = { value: index, next: recursive };
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Node", recursive));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Node", null));
});

test("V1 quantitative instance facets are optional and have no million-unit default ceiling", () => {
  const descriptor = descriptorWithDefinitions(relaxedFacetDefinitions(), "Unit");
  const longText = "x".repeat(1_000_001);
  const largeBase64 = "AAAA".repeat(333_334);
  const largeInteger = "9".repeat(1_000_001);

  assert.doesNotThrow(() => validateRpcContractDescriptor(descriptor));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Text", longText));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/LargeTextBound", longText));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Values", [true, false]));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/LargeValuesBound", [true, false]));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/StringMap", { "": true }));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Bytes", largeBase64));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/BigInteger", largeInteger));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Decimal", "1234567890.123456789"));
});

test("V1 instance validation is iterative and has no schema-depth limit", () => {
  const descriptor = descriptorWithDefinitions(relaxedFacetDefinitions(), "Unit");
  let value: unknown = [];
  for (let depth = 0; depth < 10_000; depth++) value = [value];

  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/RecursiveArray", value));
  assert.doesNotThrow(() => validateRpcInstance(descriptor, "#/$defs/Any", value));

  const cyclic: { self?: unknown } = {};
  cyclic.self = cyclic;
  assert.throws(() => validateRpcInstance(descriptor, "#/$defs/Any", cyclic), /cyclic object graph/u);
});

test("V1 explicitly supplied instance minima must not exceed maxima", () => {
  const schemas: readonly JsonSchema[] = [
    { type: "string", minLength: 2, maxLength: 1 },
    { type: "array", items: { type: "boolean" }, minItems: 2, maxItems: 1 },
    {
      type: "object",
      additionalProperties: { type: "boolean" },
      propertyNames: { type: "string" },
      minProperties: 2,
      maxProperties: 1,
    },
  ];
  for (const schema of schemas) {
    assert.throws(
      () => validateRpcContractDescriptor(descriptorWithDefinitions({ Value: schema }, "Value")),
      /must not exceed/u,
    );
  }
});

test("V1 descriptor and runtime floating-point values use format-specific IEEE-754 semantics", () => {
  const descriptor = descriptorWithDefinitions({
    Float32: {
      type: "number",
      format: "float32",
      minimum: -1.25,
      maximum: 1.25,
      enum: [0.1],
    },
    Float64: {
      type: "number",
      format: "float64",
      minimum: -1.5,
      maximum: 1.5,
    },
  }, "Float32");
  const parsed = parseRpcContractDescriptor(JSON.stringify(descriptor));
  assert.doesNotThrow(() => validateRpcInstance(parsed, "#/$defs/Float32", 0.100000001));
  assert.throws(() => validateRpcInstance(parsed, "#/$defs/Float32", 0.2), RpcInstanceError);
  assert.doesNotThrow(() => validateRpcInstance(parsed, "#/$defs/Float64", 1.25));

  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({
      Float32: { type: "number", format: "float32", enum: [0.1, 0.100000001] },
    }, "Float32")),
    /duplicate value/u,
  );
  assert.throws(
    () => validateRpcContractDescriptor(descriptorWithDefinitions({
      Float32: { type: "number", format: "float32", maximum: 3.5e38 },
    }, "Float32")),
    (error: unknown) => error instanceof RpcDescriptorError && error.code === "descriptor-number",
  );

  const midpointSource = JSON.stringify(descriptorWithDefinitions({
    Float32: { type: "number", format: "float32", const: 0 },
  }, "Float32")).replace('"const":0', '"const":1.000000059604644775390626');
  const midpoint = parseRpcContractDescriptor(midpointSource);
  const reparsed = parseRpcContractDescriptor(canonicalRpcDescriptorBytes(midpoint));
  assert.doesNotThrow(() => validateRpcInstance(midpoint, "#/$defs/Float32", 1));
  assert.doesNotThrow(() => validateRpcInstance(reparsed, "#/$defs/Float32", 1));
  assert.throws(() => validateRpcInstance(midpoint, "#/$defs/Float32", 1.0000001192092896), RpcInstanceError);
  assert.throws(() => validateRpcInstance(reparsed, "#/$defs/Float32", 1.0000001192092896), RpcInstanceError);
});

test("descriptor parsing preserves mathematical-integer token semantics", () => {
  const source = JSON.stringify(descriptorWithDefinitions({
    Value: { type: "integer", minimum: 0 },
  }, "Value"));
  for (const token of ["1.0000000000000001", "1e-9999", "9007199254740991.1"]) {
    assert.throws(
      () => parseRpcContractDescriptor(source.replace('"minimum":0', `"minimum":${token}`)),
      (error: unknown) => error instanceof RpcDescriptorError && error.code === "descriptor-number",
      token,
    );
  }
  assert.doesNotThrow(() => parseRpcContractDescriptor(source.replace('"minimum":0', '"minimum":1e0')));
});

function completeDescriptor(): RpcContractDescriptor {
  const source = JSON.parse(readFileSync(resolve(conformanceRoot, "valid/complete.rpc.json"), "utf8")) as Record<string, unknown> & {
    readonly $defs: Record<string, unknown>;
  };
  const candidate = source.schemaProfile === "sunder.rpc.values.v1"
    ? source
    : {
      ...source,
      schemaProfile: "sunder.rpc.values.v1",
      $defs: {
        ...source.$defs,
        Uuid: { type: "string", format: "uuid", description: "Canonical lowercase UUID." },
        Instant: { type: "string", format: "date-time" },
        Ratio: { type: "number", format: "float64", minimum: -1000, maximum: 1000 },
      },
    };
  const descriptor = parseRpcContractDescriptor(JSON.stringify(candidate));
  return descriptor;
}

function descriptorWithDefinitions(
  definitions: Readonly<Record<string, JsonSchema>>,
  entryDefinition: string,
): RpcContractDescriptor {
  return {
    descriptorVersion: 1,
    schemaProfile: "sunder.rpc.values.v1",
    contractId: "example.rpc.v1.regression",
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

function nestedNullableRecordSchema(wrapperCount: number): JsonSchema {
  let schema: JsonSchema = { type: "boolean" };
  for (let level = 0; level < wrapperCount; level++) {
    schema = {
      oneOf: [
        { type: "null" },
        {
          type: "object",
          properties: { next: schema },
          required: ["next"],
          additionalProperties: false,
        },
      ],
    };
  }
  return schema;
}

function valueProfileDefinitions(): Readonly<Record<string, JsonSchema>> {
  return {
    Any: {},
    Dynamic: { type: "object" },
    DynamicBounded: { type: "object", maxProperties: 2 },
    Uuid: { type: "string", format: "uuid" },
    Date: { type: "string", format: "date" },
    DateTime: { type: "string", format: "date-time" },
    Duration: { type: "string", format: "duration" },
    Uri: { type: "string", format: "uri" },
    BigInteger: {
      type: "string",
      format: "big-integer",
      maxDigits: 4,
      valueMinimum: "-100",
      valueExclusiveMaximum: "1000",
    },
    Decimal: {
      type: "string",
      format: "decimal",
      maxPrecision: 6,
      maxScale: 3,
      valueExclusiveMinimum: "-10.5",
      valueMaximum: "999.999",
    },
    Float32: { type: "number", format: "float32" },
    Float64: { type: "number", format: "float64" },
    Bytes: { type: "string", contentEncoding: "base64", maxDecodedBytes: 4 },
    Node: {
      oneOf: [
        { type: "null" },
        { $ref: "#/$defs/NodeRecord" },
      ],
    },
    NodeRecord: {
      type: "object",
      properties: {
        value: { type: "integer" },
        next: { $ref: "#/$defs/Node" },
      },
      required: ["value"],
      additionalProperties: false,
    },
    Value: {
      type: "object",
      properties: {
        any: { $ref: "#/$defs/Any" },
        dynamic: { $ref: "#/$defs/Dynamic" },
        node: { $ref: "#/$defs/Node" },
      },
      required: ["any", "dynamic", "node"],
      additionalProperties: false,
    },
  };
}

function relaxedFacetDefinitions(): Readonly<Record<string, JsonSchema>> {
  return {
    Unit: { type: "object", properties: {}, required: [], additionalProperties: false, maxProperties: 2_000_000 },
    Any: {},
    Text: { type: "string" },
    LargeTextBound: { type: "string", maxLength: 2_000_000 },
    Values: { type: "array", items: { type: "boolean" } },
    LargeValuesBound: { type: "array", items: { type: "boolean" }, maxItems: 2_000_000 },
    StringMap: {
      type: "object",
      additionalProperties: { type: "boolean" },
      propertyNames: { type: "string" },
    },
    Bytes: { type: "string", contentEncoding: "base64" },
    BigInteger: { type: "string", format: "big-integer" },
    Decimal: { type: "string", format: "decimal" },
    RecursiveArray: { type: "array", items: { $ref: "#/$defs/RecursiveArray" } },
  };
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
