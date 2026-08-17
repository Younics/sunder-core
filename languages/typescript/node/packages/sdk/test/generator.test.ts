import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  linkSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { join, resolve } from "node:path";
import test from "node:test";
import { canonicalizeDescriptor, generateTypeScriptBindings } from "../src/generator";
import { parseRpcContractDescriptor } from "../src/descriptor";
import {
  SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
} from "../src/descriptor-v2";
import type {
  JsonValue,
  RpcContractDescriptor,
  RpcContractDescriptorV2,
  RpcContractIdentity,
  RpcContractIdentityV2,
  RpcInvocationContext,
  RpcInvoker,
  RpcProviderHandler,
  RpcProviderRegistration,
} from "../src/types";

interface GeneratedBindingsModule {
  readonly CONTRACT_ID: string;
  readonly CONTRACT_VERSION: string;
  readonly CONTRACT_DESCRIPTOR_SHA256: string;
  readonly CONTRACT_IDENTITY: RpcContractIdentity;
  readonly ExampleGeneratorRpcHandler: new (...services: readonly unknown[]) => RpcProviderHandler;
  readonly ExampleGeneratorRpcClient: new <TTarget>(
    invoker: RpcInvoker<TTarget>,
    target: TTarget,
  ) => {
    readonly FooBar: {
      DoIt(request: JsonValue): Promise<JsonValue>;
      DoIt2(request: JsonValue): AsyncIterable<JsonValue>;
    };
    readonly FooBar2: {
      Read(request: JsonValue): Promise<JsonValue>;
    };
  };
}

const descriptor: RpcContractDescriptor = {
  descriptorVersion: 1,
  contractId: "example.messages",
  version: "1.0.0",
  services: [{
    serviceId: "messages",
    methods: [
      { methodId: "send", kind: "unary", requestSchema: { $ref: "#/$defs/Request" }, responseSchema: { $ref: "#/$defs/Response" } },
      { methodId: "watch", kind: "server-stream", requestSchema: { $ref: "#/$defs/Request" }, eventSchema: { $ref: "#/$defs/Response" } },
    ],
  }],
  $defs: {
    Response: { type: "object", properties: { accepted: { type: "boolean" } }, required: ["accepted"], additionalProperties: false },
    Request: { type: "object", properties: { message: { type: "string", minLength: 1, maxLength: 128 } }, required: ["message"], additionalProperties: false },
  },
};

const compileDescriptor: RpcContractDescriptor = {
  descriptorVersion: 1,
  contractId: "example.generator",
  version: "1.0.0",
  services: [
    {
      serviceId: "foo_bar",
      methods: [
        { methodId: "read", kind: "unary", requestSchema: "#/$defs/foo-bar", responseSchema: "#/$defs/foo_bar" },
      ],
    },
    {
      serviceId: "foo-bar",
      methods: [
        { methodId: "do_it", kind: "server-stream", requestSchema: "#/$defs/foo-bar", eventSchema: "#/$defs/foo_bar" },
        { methodId: "do-it", kind: "unary", requestSchema: "#/$defs/foo-bar", responseSchema: "#/$defs/foo_bar" },
      ],
    },
  ],
  $defs: {
    foo_bar: {
      type: "object",
      properties: {
        accepted: { type: "boolean", const: true },
        status: { $ref: "#/$defs/Status" },
      },
      required: ["accepted", "status"],
      additionalProperties: false,
    },
    Status: {
      type: "string",
      minLength: 4,
      maxLength: 6,
      enum: ["queued", "done"],
    },
    "foo-bar": {
      type: "object",
      properties: {
        optional: {
          oneOf: [
            { type: "string", minLength: 0, maxLength: 32 },
            { type: "null" },
          ],
        },
        metadata: { $ref: "#/$defs/NestedMap" },
        matrix: { $ref: "#/$defs/ReadonlyArray" },
        json: { $ref: "#/$defs/JsonValue" },
        class: { $ref: "#/$defs/Fixed" },
        "wire-value": { $ref: "#/$defs/Status" },
        wire_value: { $ref: "#/$defs/Status" },
      },
      required: ["metadata", "matrix", "json", "class", "wire-value", "wire_value"],
      additionalProperties: false,
    },
    ReadonlyArray: {
      type: "array",
      items: {
        type: "array",
        items: { $ref: "#/$defs/Status" },
        minItems: 0,
        maxItems: 8,
      },
      minItems: 0,
      maxItems: 8,
    },
    NestedMap: {
      type: "object",
      properties: {},
      additionalProperties: {
        type: "object",
        properties: {},
        additionalProperties: { $ref: "#/$defs/Status" },
        propertyNames: { type: "string", minLength: 1, maxLength: 32 },
        minProperties: 0,
        maxProperties: 8,
      },
      propertyNames: { type: "string", minLength: 1, maxLength: 32 },
      minProperties: 0,
      maxProperties: 8,
    },
    JsonValue: {
      type: "string",
      minLength: 4,
      maxLength: 4,
      const: "json",
    },
    IFooBarProvider: { type: "boolean", const: false },
    FooBarClient: {
      type: "string",
      minLength: 3,
      maxLength: 3,
      const: "api",
    },
    Fixed: {
      type: "string",
      minLength: 7,
      maxLength: 7,
      const: "request",
    },
  },
};

const v2Descriptor: RpcContractDescriptorV2 = {
  descriptorVersion: 2,
  schemaProfile: "sunder.rpc.values.v1",
  contractId: "example.v2.bindings",
  version: "2.0.0",
  errors: {},
  services: {
    content: {
      methods: {
        put: {
          kind: "unary",
          idempotency: "idempotent",
          errors: [],
          requestSchema: { $ref: "#/$defs/Request" },
          responseSchema: { $ref: "#/$defs/Response" },
        },
      },
    },
  },
  $defs: {
    Uuid: { type: "string", format: "uuid" },
    Instant: { type: "string", format: "date-time" },
    Content: { type: "object", logicalType: "content-reference" },
    Text: { type: "string", minLength: 0, maxLength: 128 },
    NullableText: {
      oneOf: [
        { type: "null" },
        { $ref: "#/$defs/Text" },
      ],
    },
    Request: {
      type: "object",
      properties: {
        correlationId: { $ref: "#/$defs/Uuid" },
        observedAt: { $ref: "#/$defs/Instant" },
        content: { $ref: "#/$defs/Content" },
        optionalNote: { $ref: "#/$defs/NullableText" },
      },
      required: ["correlationId", "observedAt", "content"],
      additionalProperties: false,
    },
    Response: {
      type: "object",
      properties: { accepted: { type: "boolean" } },
      required: ["accepted"],
      additionalProperties: false,
    },
  },
};

const valueProfileDescriptor: RpcContractDescriptorV2 = {
  descriptorVersion: 2,
  schemaProfile: "sunder.rpc.values.v1",
  contractId: "example.values",
  version: "2.0.0",
  errors: {
    "example.details": { detailsSchema: { $ref: "#/$defs/Decimal" } },
  },
  services: {
    values: {
      methods: {
        roundtrip: {
          kind: "unary",
          idempotency: "idempotent",
          errors: ["example.details"],
          requestSchema: { $ref: "#/$defs/Values" },
          responseSchema: { $ref: "#/$defs/Values" },
        },
      },
    },
  },
  $defs: {
    Any: {},
    Dynamic: { type: "object" },
    BigInteger: { type: "string", format: "big-integer", maxDigits: 64 },
    Decimal: { type: "string", format: "decimal", maxPrecision: 32, maxScale: 8 },
    Float32: { type: "number", format: "float32", enum: [0.1, 3.5] },
    Float64: { type: "number", format: "float64", const: 0.1 },
    Uuid: { type: "string", format: "uuid" },
    Date: { type: "string", format: "date" },
    DateTime: { type: "string", format: "date-time" },
    Duration: { type: "string", format: "duration" },
    Uri: { type: "string", format: "uri" },
    Bytes: { type: "string", contentEncoding: "base64", maxDecodedBytes: 64 },
    Status: { type: "string", minLength: 4, maxLength: 6, enum: ["ready", "queued"] },
    Text: { type: "string", minLength: 0, maxLength: 64 },
    NullableText: { oneOf: [{ type: "null" }, { $ref: "#/$defs/Text" }] },
    Node: { oneOf: [{ type: "null" }, { $ref: "#/$defs/NodeRecord" }] },
    NodeRecord: {
      type: "object",
      properties: {
        value: { type: "integer" },
        next: { $ref: "#/$defs/Node" },
      },
      required: ["value"],
      additionalProperties: false,
    },
    Added: {
      type: "object",
      properties: {
        kind: { type: "string", minLength: 5, maxLength: 5, const: "added" },
        value: { $ref: "#/$defs/BigInteger" },
      },
      required: ["kind", "value"],
      additionalProperties: false,
    },
    Removed: {
      type: "object",
      properties: {
        kind: { type: "string", minLength: 7, maxLength: 7, const: "removed" },
        value: { $ref: "#/$defs/BigInteger" },
      },
      required: ["kind", "value"],
      additionalProperties: false,
    },
    Event: { oneOf: [{ $ref: "#/$defs/Added" }, { $ref: "#/$defs/Removed" }] },
    Values: {
      type: "object",
      properties: {
        any: { $ref: "#/$defs/Any" },
        bigInteger: { $ref: "#/$defs/BigInteger" },
        bytes: { $ref: "#/$defs/Bytes" },
        date: { $ref: "#/$defs/Date" },
        dateTime: { $ref: "#/$defs/DateTime" },
        decimal: { $ref: "#/$defs/Decimal" },
        duration: { $ref: "#/$defs/Duration" },
        dynamic: { $ref: "#/$defs/Dynamic" },
        event: { $ref: "#/$defs/Event" },
        float32: { $ref: "#/$defs/Float32" },
        float64: { $ref: "#/$defs/Float64" },
        node: { $ref: "#/$defs/Node" },
        optionalNullable: { $ref: "#/$defs/NullableText" },
        status: { $ref: "#/$defs/Status" },
        uri: { $ref: "#/$defs/Uri" },
        uuid: { $ref: "#/$defs/Uuid" },
      },
      required: [
        "any",
        "bigInteger",
        "bytes",
        "date",
        "dateTime",
        "decimal",
        "duration",
        "dynamic",
        "event",
        "float32",
        "float64",
        "node",
        "status",
        "uri",
        "uuid",
      ],
      additionalProperties: false,
    },
  },
};

const COMPILE_DESCRIPTOR_SHA256 = descriptorSha256(compileDescriptor);
const V2_DESCRIPTOR_SHA256 = descriptorSha256(v2Descriptor);
const V2_ABI_SHA256 = abiSha256(v2Descriptor);

test("TypeScript binding generation is deterministic and mirrors C# binding shapes", () => {
  const first = generateTypeScriptBindings(descriptor);
  const second = generateTypeScriptBindings(descriptor);
  assert.equal(first, second);
  assert.match(first, /export type Request = \{/u);
  assert.match(first, /export interface IMessagesProvider/u);
  assert.match(first, /export class MessagesClient/u);
  assert.match(first, /Promise<Response>/u);
  assert.match(first, /AsyncIterable<Response>/u);
});

test("generated bindings compile for worker and browser clients and execute provider dispatch", async () => {
  const first = generateTypeScriptBindings(compileDescriptor, { descriptorSha256: COMPILE_DESCRIPTOR_SHA256 });
  const second = generateTypeScriptBindings(compileDescriptor, { descriptorSha256: COMPILE_DESCRIPTOR_SHA256 });

  assert.equal(first, second);
  assert.match(first, /from "@sunder\/sdk\/core";/u);
  assert.doesNotMatch(first, /^import (?!type\b)/mu);
  assert.doesNotMatch(first, /from "node:/u);
  assert.match(first, /export const CONTRACT_ID = "example\.generator" as const;/u);
  assert.match(first, /export const CONTRACT_VERSION = "1\.0\.0" as const;/u);
  assert.match(first, new RegExp(`export const CONTRACT_DESCRIPTOR_SHA256 = "${COMPILE_DESCRIPTOR_SHA256}" as const;`, "u"));
  assert.match(first, /as const satisfies RpcContractIdentity;/u);
  assert.match(first, /export type FooBar = \{/u);
  assert.match(first, /export type FooBar2 = \{/u);
  assert.match(first, /export type Status = "queued" \| "done";/u);
  assert.match(first, /export type Fixed = "request";/u);
  assert.match(first, /export type JsonValue2 = "json";/u);
  assert.match(first, /export type ReadonlyArray2 = ReadonlyArray<ReadonlyArray<Status>>;/u);
  assert.match(first, /export type NestedMap = Readonly<Record<string, Readonly<Record<string, Status>>>>;/u);
  assert.match(first, /readonly optional\?: string \| null;/u);
  assert.match(first, /readonly "wire-value": Status;/u);
  assert.match(first, /export interface IFooBarProvider2/u);
  assert.match(first, /export class FooBarClient2<TTarget = string>/u);
  assert.match(first, /DoIt\(request: FooBar, context: RpcInvocationContext\): Promise<FooBar2>;/u);
  assert.match(first, /DoIt2\(request: FooBar, context: RpcInvocationContext\): AsyncIterable<FooBar2>;/u);
  assert.match(first, /export class ExampleGeneratorRpcHandler implements RpcProviderHandler/u);
  assert.match(first, /this\.#service1\.DoIt\(request as FooBar, context\)/u);
  assert.match(first, /this\.#service1\.DoIt2\(request as FooBar, context\)/u);
  assert.match(first, /export class ExampleGeneratorRpcClient<TTarget = string>/u);
  assert.match(first, /readonly FooBar: FooBarClient2<TTarget>;/u);
  assert.match(first, /readonly FooBar2: FooBarClient3<TTarget>;/u);
  assert.doesNotMatch(first, /RpcProviderRegistration/u);

  const generated = compileGeneratedBindings(first);
  assert.equal(generated.CONTRACT_ID, "example.generator");
  assert.equal(generated.CONTRACT_VERSION, "1.0.0");
  assert.equal(generated.CONTRACT_DESCRIPTOR_SHA256, COMPILE_DESCRIPTOR_SHA256);
  assert.deepEqual(generated.CONTRACT_IDENTITY, {
    contractId: "example.generator",
    contractVersion: "1.0.0",
    contractSha256: COMPILE_DESCRIPTOR_SHA256,
  });

  const calls: string[] = [];
  const response = { accepted: true, status: "done" } as const;
  const context = {} as RpcInvocationContext;
  const primaryProvider = {
    async DoIt(_request: unknown, receivedContext: RpcInvocationContext) {
      assert.equal(receivedContext, context);
      calls.push("do-it");
      return response;
    },
    async *DoIt2(_request: unknown, receivedContext: RpcInvocationContext) {
      assert.equal(receivedContext, context);
      calls.push("do_it");
      yield response;
    },
  };
  const secondaryProvider = {
    async Read(_request: unknown, receivedContext: RpcInvocationContext) {
      assert.equal(receivedContext, context);
      calls.push("read");
      return response;
    },
  };
  const handler = new generated.ExampleGeneratorRpcHandler(primaryProvider, secondaryProvider);
  const request = { class: "request", json: "json", matrix: [], metadata: {}, "wire-value": "done", wire_value: "queued" } as const;
  assert.deepEqual(await handler.invokeUnary(context, "foo-bar", "do-it", request), response);
  const events = [];
  for await (const event of handler.invokeServerStream(context, "foo-bar", "do_it", request)) events.push(event);
  assert.deepEqual(events, [response]);
  assert.deepEqual(await handler.invokeUnary(context, "foo_bar", "read", request), response);
  assert.deepEqual(calls, ["do-it", "do_it", "read"]);

  const callsBeforeUnknown = calls.length;
  assert.throws(
    () => handler.invokeUnary(context, "foo-bar", "missing", request),
    /Unknown unary RPC method 'foo-bar\/missing'/u,
  );
  assert.throws(
    () => handler.invokeUnary(context, "foo-bar", "do_it", request),
    /Unknown unary RPC method 'foo-bar\/do_it'/u,
  );
  assert.throws(
    () => handler.invokeServerStream(context, "foo-bar", "do-it", request),
    /Unknown server-stream RPC method 'foo-bar\/do-it'/u,
  );
  assert.equal(calls.length, callsBeforeUnknown);

  const selectedTarget = { endpoint: "selected" } as const;
  const clientCalls: Array<readonly [typeof selectedTarget, string, string]> = [];
  const invoker: RpcInvoker<typeof selectedTarget> = {
    async invoke<TRequest extends JsonValue, TResponse extends JsonValue>(target: typeof selectedTarget, serviceId: string, methodId: string, _request: TRequest) {
      clientCalls.push([target, serviceId, methodId]);
      return response as unknown as TResponse;
    },
    subscribe<TRequest extends JsonValue, TEvent extends JsonValue>(target: typeof selectedTarget, serviceId: string, methodId: string, _request: TRequest) {
      clientCalls.push([target, serviceId, methodId]);
      return (async function* events(): AsyncIterable<TEvent> {
        yield response as unknown as TEvent;
      }());
    },
  };
  const client = new generated.ExampleGeneratorRpcClient(invoker, selectedTarget);
  assert.deepEqual(await client.FooBar.DoIt(request), response);
  const clientEvents = [];
  for await (const event of client.FooBar.DoIt2(request)) clientEvents.push(event);
  assert.deepEqual(clientEvents, [response]);
  assert.deepEqual(await client.FooBar2.Read(request), response);
  assert.deepEqual(clientCalls, [
    [selectedTarget, "foo-bar", "do-it"],
    [selectedTarget, "foo-bar", "do_it"],
    [selectedTarget, "foo_bar", "read"],
  ]);
});

test("generator hash options enforce descriptor-version-specific identities", () => {
  const v1DescriptorSha256 = descriptorSha256(descriptor);
  assert.doesNotThrow(() => generateTypeScriptBindings(descriptor));
  assert.doesNotThrow(() => generateTypeScriptBindings(v2Descriptor));
  assert.doesNotThrow(() => generateTypeScriptBindings(descriptor, {
    descriptorSha256: v1DescriptorSha256,
  }));
  assert.doesNotThrow(() => generateTypeScriptBindings(v2Descriptor, {
    descriptorSha256: V2_DESCRIPTOR_SHA256,
    abiSha256: V2_ABI_SHA256,
  }));
  assert.throws(
    () => generateTypeScriptBindings(descriptor, { descriptorSha256: "A".repeat(64) }),
    /descriptorSha256 must be a lowercase 64-character hexadecimal/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(v2Descriptor, { descriptorSha256: V2_DESCRIPTOR_SHA256, abiSha256: "b".repeat(63) }),
    /abiSha256 must be a lowercase 64-character hexadecimal/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(descriptor, { abiSha256: V2_ABI_SHA256 }),
    /abiSha256 is not valid for descriptor version 1/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(v2Descriptor, { descriptorSha256: V2_DESCRIPTOR_SHA256 }),
    /requires descriptorSha256 and abiSha256 to be provided together/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(v2Descriptor, { abiSha256: V2_ABI_SHA256 }),
    /requires descriptorSha256 and abiSha256 to be provided together/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(descriptor, { descriptorSha256: mismatchedHash(v1DescriptorSha256) }),
    /descriptorSha256 does not match the canonical RPC descriptor/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(v2Descriptor, {
      descriptorSha256: mismatchedHash(V2_DESCRIPTOR_SHA256),
      abiSha256: V2_ABI_SHA256,
    }),
    /descriptorSha256 does not match the canonical RPC descriptor/u,
  );
  assert.throws(
    () => generateTypeScriptBindings(v2Descriptor, {
      descriptorSha256: V2_DESCRIPTOR_SHA256,
      abiSha256: mismatchedHash(V2_ABI_SHA256),
    }),
    /abiSha256 does not match the domain-separated canonical RPC ABI/u,
  );
});

test("V2 bindings compile and execute with canonical worker identity, logical wire types, and presence semantics", async () => {
  const source = generateTypeScriptBindings(v2Descriptor, {
    descriptorSha256: V2_DESCRIPTOR_SHA256,
    abiSha256: V2_ABI_SHA256,
  });
  assert.match(source, new RegExp(`export const CONTRACT_DESCRIPTOR_SHA256 = "${V2_DESCRIPTOR_SHA256}" as const;`, "u"));
  assert.match(source, new RegExp(`export const CONTRACT_ABI_SHA256 = "${V2_ABI_SHA256}" as const;`, "u"));
  assert.match(source, /export const CONTRACT_IDENTITY = \{/u);
  assert.match(source, /descriptorSha256: CONTRACT_DESCRIPTOR_SHA256,/u);
  assert.match(source, /abiSha256: CONTRACT_ABI_SHA256,/u);
  assert.match(source, /as const satisfies RpcContractIdentityV2;/u);
  assert.match(source, /export function createExampleV2BindingsRpcProviderRegistration\(/u);
  assert.doesNotMatch(source, /contractSha256/u);
  assert.match(source, /export type Content = RpcContentReference;/u);
  assert.match(source, /export type Uuid = string;/u);
  assert.match(source, /export type Instant = string;/u);
  assert.match(source, /export type NullableText = null \| Text;/u);
  assert.match(source, /readonly optionalNote\?: NullableText;/u);
  assert.doesNotMatch(source, /^import (?!type\b)/mu);
  assert.doesNotMatch(source, /from "node:/u);

  const generated = compileGeneratedModule(source, `
import {
  CONTRACT_ABI_SHA256,
  CONTRACT_DESCRIPTOR_SHA256,
  CONTRACT_IDENTITY,
  ExampleV2BindingsRpcClient,
  createExampleV2BindingsRpcProviderRegistration,
  type IContentProvider,
  type Request,
  type Response,
} from "./bindings";
import type {
  RpcContentReference,
  RpcContractIdentityV2,
  RpcInvocationContext,
  RpcProviderRegistration,
} from "@sunder/sdk";
import type { SunderBrowserRpc } from "@sunder/sdk/browser";

const content: RpcContentReference = {
  id: "opaque-content-id",
  length: 4,
  sha256: "${"c".repeat(64)}",
  mediaType: "text/plain",
  fileName: "note.txt",
  expiresAtUtc: "2030-01-02T03:04:05Z",
  repeatability: "single-use",
};
const common = {
  correlationId: "00000000-0000-0000-0000-000000000000",
  observedAt: "2030-01-02T03:04:05Z",
  content,
};
const absent: Request = common;
const presentNull: Request = { ...common, optionalNote: null };
const presentValue: Request = { ...common, optionalNote: "value" };
// @ts-expect-error optional-nullable preserves absence rather than accepting undefined
const presentUndefined: Request = { ...common, optionalNote: undefined };
declare const browserRpc: SunderBrowserRpc;
const client = new ExampleV2BindingsRpcClient(browserRpc, "selected-provider-handle");
const result: Promise<Response> = client.Content.Put(absent);
const provider: IContentProvider = {
  async Put(request: Request, context: RpcInvocationContext): Promise<Response> {
    void request;
    void context;
    return { accepted: true };
  },
};
const registration: RpcProviderRegistration = createExampleV2BindingsRpcProviderRegistration(
  "example.v2.bindings.default",
  provider,
);
const descriptorHash: "${V2_DESCRIPTOR_SHA256}" = CONTRACT_DESCRIPTOR_SHA256;
const abiHash: "${V2_ABI_SHA256}" = CONTRACT_ABI_SHA256;
const identity: RpcContractIdentityV2 = CONTRACT_IDENTITY;
const workerTransportDescriptorHash: string = registration.descriptorSha256;
const workerTransportAbiHash: string = registration.abiSha256;
void presentNull;
void presentValue;
void presentUndefined;
void result;
void registration;
void descriptorHash;
void abiHash;
void identity;
void workerTransportDescriptorHash;
void workerTransportAbiHash;
`) as {
    readonly CONTRACT_DESCRIPTOR_SHA256: string;
    readonly CONTRACT_ABI_SHA256: string;
    readonly CONTRACT_IDENTITY: Readonly<{
      contractId: string;
      contractVersion: string;
      descriptorSha256: string;
      abiSha256: string;
    }>;
    readonly ExampleV2BindingsRpcClient: new <TTarget>(
      invoker: RpcInvoker<TTarget>,
      target: TTarget,
    ) => { readonly Content: { Put(request: JsonValue): Promise<JsonValue> } };
    readonly ExampleV2BindingsRpcHandler: new (...services: readonly unknown[]) => RpcProviderHandler;
    createExampleV2BindingsRpcProviderRegistration(
      providerId: string,
      ...services: readonly unknown[]
    ): RpcProviderRegistration;
  };
  assert.equal(generated.CONTRACT_DESCRIPTOR_SHA256, V2_DESCRIPTOR_SHA256);
  assert.equal(generated.CONTRACT_ABI_SHA256, V2_ABI_SHA256);
  assert.deepEqual(generated.CONTRACT_IDENTITY, {
    contractId: "example.v2.bindings",
    contractVersion: "2.0.0",
    descriptorSha256: V2_DESCRIPTOR_SHA256,
    abiSha256: V2_ABI_SHA256,
  });

  const invocations: Array<readonly [string, string, string, JsonValue]> = [];
  const invoker: RpcInvoker<string> = {
    async invoke<TRequest extends JsonValue, TResponse extends JsonValue>(target: string, serviceId: string, methodId: string, request: TRequest) {
      invocations.push([target, serviceId, methodId, request]);
      return { accepted: true } as unknown as TResponse;
    },
    subscribe<TRequest extends JsonValue, TEvent extends JsonValue>(): AsyncIterable<TEvent> {
      return (async function* empty(): AsyncIterable<TEvent> {})();
    },
  };
  const request = {
    correlationId: "00000000-0000-0000-0000-000000000000",
    observedAt: "2030-01-02T03:04:05Z",
    content: {
      id: "opaque-content-id",
      length: 4,
      sha256: "c".repeat(64),
      mediaType: "text/plain",
      fileName: "note.txt",
      expiresAtUtc: "2030-01-02T03:04:05Z",
      repeatability: "single-use",
    },
  } as const;
  const providerContext = {} as RpcInvocationContext;
  const providerCalls: JsonValue[] = [];
  const registration = generated.createExampleV2BindingsRpcProviderRegistration(
    "example.v2.bindings.default",
    {
      async Put(receivedRequest: JsonValue, receivedContext: RpcInvocationContext) {
        assert.equal(receivedContext, providerContext);
        providerCalls.push(receivedRequest);
        return { accepted: true } as const;
      },
    },
  );
  const registrationWithV2Identity = registration as RpcProviderRegistration & RpcContractIdentityV2;
  assert.equal(registration.providerId, "example.v2.bindings.default");
  assert.equal(registration.contractId, "example.v2.bindings");
  assert.equal(registration.contractVersion, "2.0.0");
  assert.equal(registrationWithV2Identity.descriptorSha256, V2_DESCRIPTOR_SHA256);
  assert.equal(registrationWithV2Identity.abiSha256, V2_ABI_SHA256);
  assert.ok(registration.handler instanceof generated.ExampleV2BindingsRpcHandler);
  assert.deepEqual(
    await registration.handler.invokeUnary(providerContext, "content", "put", request),
    { accepted: true },
  );
  assert.deepEqual(providerCalls, [request]);

  const client = new generated.ExampleV2BindingsRpcClient(invoker, "selected-provider-handle");
  assert.deepEqual(await client.Content.Put(request), { accepted: true });
  assert.deepEqual(invocations, [["selected-provider-handle", "content", "put", request]]);
});

test("value-profile bindings deterministically compile and convert exact domain values", async () => {
  const first = generateTypeScriptBindings(valueProfileDescriptor);
  const second = generateTypeScriptBindings(valueProfileDescriptor);
  assert.equal(first, second);
  assert.match(first, /export type Any = JsonValue;/u);
  assert.match(first, /export type Dynamic = JsonObject;/u);
  assert.match(first, /export type BigInteger = bigint;/u);
  assert.match(first, /export type Decimal = string;/u);
  assert.match(first, /export type Float32 = number;/u);
  assert.match(first, /export type Bytes = Uint8Array;/u);
  assert.match(first, /export type Status = "ready" \| "queued";/u);
  assert.match(first, /export type Event = Added \| Removed;/u);
  assert.match(first, /Math\.fround/u);
  assert.match(first, /\$toWireBase64/u);
  assert.match(first, /\$fromWireBigInteger/u);
  assert.match(first, /readonly optionalNullable\?: NullableText;/u);
  assert.match(first, /import \{ RpcDomainError \} from "@sunder\/sdk";/u);
  assert.match(first, /export function createExampleDetailsRpcError\(details: Decimal/u);
  assert.match(first, /export function tryGetExampleDetailsRpcErrorDetails\(error: unknown\): Decimal \| undefined/u);

  const generated = compileGeneratedModule(first, `
import {
  ExampleValuesRpcClient,
  createExampleDetailsRpcError,
  tryGetExampleDetailsRpcErrorDetails,
  type Event,
  type Values,
} from "./bindings";
import type { RpcClient } from "@sunder/sdk";
import type { SunderBrowserRpc } from "@sunder/sdk/browser";

const common: Values = {
  any: { nested: [true, 1.25] },
  bigInteger: 12345678901234567890n,
  bytes: new Uint8Array([0, 1, 2, 255]),
  date: "2030-01-02",
  dateTime: "2030-01-02T03:04:05Z",
  decimal: "123.456",
  duration: "P1DT2H",
  dynamic: { nested: [1, 2, 3] },
  event: { kind: "added", value: 42n },
  float32: Math.fround(0.1),
  float64: 0.1,
  node: { value: 1, next: { value: 2 } },
  status: "ready",
  uri: "https://example.test/value",
  uuid: "00000000-0000-0000-0000-000000000000",
};
const presentNull: Values = { ...common, optionalNullable: null };
// @ts-expect-error optional-nullable preserves absence rather than accepting undefined
const presentUndefined: Values = { ...common, optionalNullable: undefined };
const removed: Event = { kind: "removed", value: 1n };
const detailError = createExampleDetailsRpcError("123.45");
const detail: string | undefined = tryGetExampleDetailsRpcErrorDetails(detailError);
declare const runtimeRpc: RpcClient;
declare const browserRpc: SunderBrowserRpc;
const runtimeClient = new ExampleValuesRpcClient(runtimeRpc, "endpoint-reference");
const browserClient = new ExampleValuesRpcClient(browserRpc, "provider-handle");
const runtimeResult: Promise<Values> = runtimeClient.Values.Roundtrip(common);
const browserResult: Promise<Values> = browserClient.Values.Roundtrip(presentNull);
void presentUndefined;
void removed;
void detail;
void runtimeResult;
void browserResult;
`) as {
    readonly ExampleValuesRpcClient: new <TTarget>(
      invoker: RpcInvoker<TTarget>,
      target: TTarget,
    ) => { readonly Values: { Roundtrip(request: unknown): Promise<unknown> } };
    readonly ExampleValuesRpcHandler: new (provider: {
      Roundtrip(request: unknown, context: RpcInvocationContext): Promise<unknown>;
    }) => RpcProviderHandler;
    createExampleDetailsRpcError(details: string, message?: string): {
      readonly error: { readonly kind: string; readonly code: string; readonly details?: JsonValue };
    };
    tryGetExampleDetailsRpcErrorDetails(error: unknown): string | undefined;
  };

  const typedError = generated.createExampleDetailsRpcError("123.45", "Rejected.");
  assert.equal(typedError.error.kind, "domain");
  assert.equal(typedError.error.code, "example.details");
  assert.equal(typedError.error.details, "123.45");
  assert.equal(generated.tryGetExampleDetailsRpcErrorDetails(typedError), "123.45");

  const domain = {
    any: { nested: [true, 1.25] },
    bigInteger: 12345678901234567890n,
    bytes: new Uint8Array([0, 1, 2, 255]),
    date: "2030-01-02",
    dateTime: "2030-01-02T03:04:05Z",
    decimal: "123.456",
    duration: "P1DT2H",
    dynamic: { nested: [1, 2, 3] },
    event: { kind: "added", value: 42n },
    float32: 0.1,
    float64: 0.1,
    node: { value: 1, next: { value: 2 } },
    status: "ready",
    uri: "https://example.test/value",
    uuid: "00000000-0000-0000-0000-000000000000",
  };
  let wireRequest: JsonValue | undefined;
  const invoker: RpcInvoker<string> = {
    async invoke<TRequest extends JsonValue, TResponse extends JsonValue>(_target: string, _serviceId: string, _methodId: string, request: TRequest) {
      wireRequest = request;
      return request as unknown as TResponse;
    },
    subscribe<TRequest extends JsonValue, TEvent extends JsonValue>(): AsyncIterable<TEvent> {
      return (async function* empty(): AsyncIterable<TEvent> {})();
    },
  };
  const client = new generated.ExampleValuesRpcClient(invoker, "endpoint");
  const roundTripped = await client.Values.Roundtrip(domain) as typeof domain;
  const wire = wireRequest as unknown as Record<string, unknown>;
  assert.equal(wire.bigInteger, "12345678901234567890");
  assert.equal(wire.bytes, "AAEC/w==");
  assert.equal(wire.float32, Math.fround(0.1));
  assert.equal((wire.event as Record<string, unknown>).value, "42");
  assert.equal(roundTripped.bigInteger, domain.bigInteger);
  assert.deepEqual([...roundTripped.bytes], [0, 1, 2, 255]);
  assert.equal(roundTripped.float32, Math.fround(0.1));
  assert.equal(roundTripped.event.value, 42n);

  let providerRequest: unknown;
  const context = {} as RpcInvocationContext;
  const handler = new generated.ExampleValuesRpcHandler({
    async Roundtrip(request: unknown, receivedContext: RpcInvocationContext) {
      assert.equal(receivedContext, context);
      providerRequest = request;
      return request;
    },
  });
  const wireResponse = await handler.invokeUnary(context, "values", "roundtrip", wireRequest!);
  assert.equal((providerRequest as typeof domain).bigInteger, domain.bigInteger);
  assert.deepEqual([...(providerRequest as typeof domain).bytes], [0, 1, 2, 255]);
  assert.deepEqual(wireResponse, wireRequest);
});

test("new generated symbols remain deterministic and collision-safe", () => {
  const collisionDescriptor: RpcContractDescriptor = {
    descriptorVersion: 1,
    contractId: "collision",
    version: "1.0.0",
    services: [
      {
        serviceId: "collision",
        methods: [{ methodId: "read", kind: "unary", requestSchema: "#/$defs/Request", responseSchema: "#/$defs/Response" }],
      },
      {
        serviceId: "foo-bar",
        methods: [{ methodId: "read", kind: "unary", requestSchema: "#/$defs/Request", responseSchema: "#/$defs/Response" }],
      },
      {
        serviceId: "foo_bar",
        methods: [{ methodId: "read", kind: "unary", requestSchema: "#/$defs/Request", responseSchema: "#/$defs/Response" }],
      },
    ],
    $defs: {
      CollisionClient: { type: "boolean", const: false },
      CollisionRpcClient: { type: "boolean", const: false },
      CollisionRpcHandler: { type: "boolean", const: false },
      ICollisionProvider: { type: "boolean", const: false },
      RpcContractIdentity: { type: "boolean", const: false },
      RpcProviderRegistration: { type: "boolean", const: false },
      Request: {
        type: "object",
        properties: { value: { type: "boolean" } },
        required: ["value"],
        additionalProperties: false,
      },
      Response: {
        type: "object",
        properties: { value: { type: "boolean" } },
        required: ["value"],
        additionalProperties: false,
      },
    },
  };
  const collisionDescriptorSha256 = descriptorSha256(collisionDescriptor);
  const first = generateTypeScriptBindings(collisionDescriptor, { descriptorSha256: collisionDescriptorSha256 });
  const second = generateTypeScriptBindings(collisionDescriptor, { descriptorSha256: collisionDescriptorSha256 });
  assert.equal(first, second);
  assert.match(first, /export type RpcContractIdentity2 = false;/u);
  assert.match(first, /export type RpcProviderRegistration = false;/u);
  assert.match(first, /export interface ICollisionProvider2/u);
  assert.match(first, /export class CollisionClient2<TTarget = string>/u);
  assert.match(first, /export class CollisionRpcHandler2 implements RpcProviderHandler/u);
  assert.match(first, /export class CollisionRpcClient2<TTarget = string>/u);
  assert.match(first, /readonly FooBar: FooBarClient<TTarget>;/u);
  assert.match(first, /readonly FooBar2: FooBarClient2<TTarget>;/u);
  assert.doesNotMatch(first, /createCollisionRpcProviderRegistration/u);
  const generated = compileGeneratedModule(first, `
import {
  CollisionRpcClient2,
  type ICollisionProvider2,
} from "./bindings";
declare const provider: ICollisionProvider2;
void CollisionRpcClient2;
void provider;
`) as Readonly<Record<string, unknown>>;
  assert.equal(typeof generated.CollisionRpcClient2, "function");
  assert.equal(typeof generated.CollisionRpcHandler2, "function");
});

test("generator CLI supplies canonical V1 and V2 hashes", () => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    for (const [name, currentDescriptor] of [["v1", descriptor], ["v2", v2Descriptor]] as const) {
      const descriptorPath = join(temporaryDirectory, `${name}.rpc.json`);
      const bindingsPath = join(temporaryDirectory, `${name}.ts`);
      writeFileSync(descriptorPath, `${JSON.stringify(currentDescriptor, null, 2)}\n`, "utf8");
      writeFileSync(bindingsPath, "prior bindings\n", "utf8");
      const result = runGeneratorCli(descriptorPath, bindingsPath, temporaryDirectory);
      const diagnostics = [result.error?.stack, result.stdout, result.stderr].filter(Boolean).join("\n");
      assert.equal(result.status, 0, diagnostics);
      const source = readFileSync(bindingsPath, "utf8");
      const expectedDescriptorHash = createHash("sha256")
        .update(canonicalRpcDescriptorBytes(currentDescriptor))
        .digest("hex");
      assert.match(source, new RegExp(`CONTRACT_DESCRIPTOR_SHA256 = "${expectedDescriptorHash}"`, "u"));
      if (currentDescriptor.descriptorVersion === 1) {
        assert.doesNotMatch(source, /createExampleMessagesRpcProviderRegistration/u);
        assert.doesNotMatch(source, /CONTRACT_ABI_SHA256/u);
      } else {
        const expectedAbiHash = createHash("sha256")
          .update(SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR, "utf8")
          .update(canonicalRpcAbiProjectionBytes(currentDescriptor))
          .digest("hex");
        assert.match(source, new RegExp(`CONTRACT_ABI_SHA256 = "${expectedAbiHash}"`, "u"));
        assert.match(source, /createExampleV2BindingsRpcProviderRegistration/u);
        assert.match(source, /descriptorSha256: CONTRACT_DESCRIPTOR_SHA256/u);
        assert.match(source, /abiSha256: CONTRACT_ABI_SHA256/u);
      }
    }
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI refuses the same input and output path without changing the descriptor", () => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const descriptorPath = join(temporaryDirectory, "contract.rpc.json");
    const source = `${JSON.stringify(descriptor, null, 2)}\n`;
    writeFileSync(descriptorPath, source, "utf8");
    const result = runGeneratorCli(descriptorPath, descriptorPath, temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /input and bindings output must be different paths/u);
    assert.equal(readFileSync(descriptorPath, "utf8"), source);
    assert.deepEqual(readdirSync(temporaryDirectory), ["contract.rpc.json"]);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI refuses a symbolic-link output without changing its target", (context) => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const descriptorPath = join(temporaryDirectory, "contract.rpc.json");
    const targetPath = join(temporaryDirectory, "prior.ts");
    const bindingsPath = join(temporaryDirectory, "bindings.ts");
    writeFileSync(descriptorPath, `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
    writeFileSync(targetPath, "prior bindings\n", "utf8");
    try {
      symlinkSync(targetPath, bindingsPath, "file");
    } catch (error) {
      if (["EACCES", "ENOSYS", "EPERM"].includes((error as NodeJS.ErrnoException).code ?? "")) {
        context.skip("Symbolic links are not available on this platform.");
        return;
      }
      throw error;
    }

    const result = runGeneratorCli(descriptorPath, bindingsPath, temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /output .* must be absent or an existing regular file/u);
    assert.equal(readFileSync(targetPath, "utf8"), "prior bindings\n");
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI refuses a non-regular output", () => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const descriptorPath = join(temporaryDirectory, "contract.rpc.json");
    const bindingsPath = join(temporaryDirectory, "bindings.ts");
    writeFileSync(descriptorPath, `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
    mkdirSync(bindingsPath);
    const result = runGeneratorCli(descriptorPath, bindingsPath, temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /output .* must be absent or an existing regular file/u);
    assert.deepEqual(readdirSync(bindingsPath), []);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI refuses input and output hard-link identity", () => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const descriptorPath = join(temporaryDirectory, "contract.rpc.json");
    const bindingsPath = join(temporaryDirectory, "bindings.ts");
    const source = `${JSON.stringify(descriptor, null, 2)}\n`;
    writeFileSync(descriptorPath, source, "utf8");
    linkSync(descriptorPath, bindingsPath);
    const result = runGeneratorCli(descriptorPath, bindingsPath, temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /input and bindings output must identify different files/u);
    assert.equal(readFileSync(descriptorPath, "utf8"), source);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI refuses a symbolic-link descriptor source", (context) => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const targetPath = join(temporaryDirectory, "contract-target.rpc.json");
    const descriptorPath = join(temporaryDirectory, "contract.rpc.json");
    const bindingsPath = join(temporaryDirectory, "bindings.ts");
    writeFileSync(targetPath, `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
    try {
      symlinkSync(targetPath, descriptorPath, "file");
    } catch (error) {
      if (["EACCES", "ENOSYS", "EPERM"].includes((error as NodeJS.ErrnoException).code ?? "")) {
        context.skip("Symbolic links are not available on this platform.");
        return;
      }
      throw error;
    }

    const result = runGeneratorCli(descriptorPath, bindingsPath, temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /input .* must be a regular file and not a symbolic link/u);
    assert.deepEqual(readdirSync(temporaryDirectory).sort(), ["contract-target.rpc.json", "contract.rpc.json"]);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI refuses symbolic-link output parents", (context) => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const descriptorPath = join(temporaryDirectory, "contract.rpc.json");
    const targetDirectory = join(temporaryDirectory, "target");
    const linkedDirectory = join(temporaryDirectory, "linked");
    writeFileSync(descriptorPath, `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
    mkdirSync(targetDirectory);
    try {
      symlinkSync(targetDirectory, linkedDirectory, "dir");
    } catch (error) {
      if (["EACCES", "ENOSYS", "EPERM"].includes((error as NodeJS.ErrnoException).code ?? "")) {
        context.skip("Symbolic links are not available on this platform.");
        return;
      }
      throw error;
    }

    const result = runGeneratorCli(descriptorPath, join(linkedDirectory, "bindings.ts"), temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /output parent .* without symbolic links/u);
    assert.deepEqual(readdirSync(targetDirectory), []);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("generator CLI preserves an existing output when descriptor planning fails", () => {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-cli-"));
  try {
    const descriptorPath = join(temporaryDirectory, "invalid.rpc.json");
    const bindingsPath = join(temporaryDirectory, "bindings.ts");
    writeFileSync(descriptorPath, '{"descriptorVersion":1}\n', "utf8");
    writeFileSync(bindingsPath, "prior bindings\n", "utf8");
    const result = runGeneratorCli(descriptorPath, bindingsPath, temporaryDirectory);
    assert.notEqual(result.status, 0);
    assert.equal(readFileSync(bindingsPath, "utf8"), "prior bindings\n");
    assert.deepEqual(readdirSync(temporaryDirectory).sort(), ["bindings.ts", "invalid.rpc.json"]);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});

test("descriptor canonicalization sorts object keys and emits finite binary64 JCS numbers", () => {
  assert.equal(canonicalizeDescriptor({ z: 1, a: { y: true, x: false } }), '{"a":{"x":false,"y":true},"z":1}');
  assert.equal(
    canonicalizeDescriptor({ zero: -0, small: 1e-7, normal: 1e-6, large: 1e21, fraction: 1.5 }),
    '{"fraction":1.5,"large":1e+21,"normal":0.000001,"small":1e-7,"zero":0}',
  );
  assert.throws(() => canonicalizeDescriptor({ value: Number.POSITIVE_INFINITY }), /finite IEEE-754 binary64/u);
});

test("descriptor parser enforces strict JSON, identity, references, and schema bounds", () => {
  const source = JSON.stringify(descriptor, null, 2);
  const parsed = parseRpcContractDescriptor(new TextEncoder().encode(source));
  assert.equal(parsed.contractId, descriptor.contractId);
  assert.equal(
    canonicalizeDescriptor(parsed as unknown as import("../src/types").JsonValue),
    canonicalizeDescriptor(descriptor as unknown as import("../src/types").JsonValue),
  );

  const duplicate = source.replace(
    '"contractId": "example.messages",',
    '"contractId": "example.messages", "contractId": "example.messages",',
  );
  assert.throws(() => parseRpcContractDescriptor(duplicate), /Duplicate JSON property/u);
  assert.throws(
    () => parseRpcContractDescriptor(source.replace("#/$defs/Request", "https://example.test/schema")),
    /existing local schema/u,
  );
  assert.throws(() => parseRpcContractDescriptor(`\uFEFF${source}`), /byte-order mark/u);
  assert.throws(
    () => parseRpcContractDescriptor(source.replace('"maxLength": 128', '"maxLength": 1.5')),
    /safe integer/u,
  );
});

function compileGeneratedBindings(source: string): GeneratedBindingsModule {
  return compileGeneratedModule(source, `
import {
  CONTRACT_DESCRIPTOR_SHA256,
  CONTRACT_ID,
  CONTRACT_IDENTITY,
  CONTRACT_VERSION,
  ExampleGeneratorRpcClient,
  ExampleGeneratorRpcHandler,
  FooBarClient2,
  type FooBar,
  type FooBar2,
  type IFooBarProvider2,
  type IFooBarProvider3,
} from "./bindings";
import type {
  RpcClient,
  RpcContractIdentity,
  RpcInvocationContext,
  RpcProviderHandler,
} from "@sunder/sdk";
import type { SunderBrowserRpc } from "@sunder/sdk/browser";

const request: FooBar = {
  class: "request",
  json: "json",
  matrix: [["queued", "done"]],
  metadata: { group: { current: "queued" } },
  "wire-value": "done",
  wire_value: "queued",
};
const nullableRequest: FooBar = { ...request, optional: null };
const response: FooBar2 = { accepted: true, status: "done" };
const provider: IFooBarProvider2 = {
  async DoIt() { return response; },
  async *DoIt2() { yield response; },
};
const secondaryProvider: IFooBarProvider3 = {
  async Read() { return response; },
};
const handler: RpcProviderHandler = new ExampleGeneratorRpcHandler(provider, secondaryProvider);
const identity: RpcContractIdentity = CONTRACT_IDENTITY;
declare const runtimeRpc: RpcClient;
declare const browserRpc: SunderBrowserRpc;
const runtimeClient = new FooBarClient2(runtimeRpc, "endpoint-reference");
const browserClient = new FooBarClient2(browserRpc, "provider-handle");
const runtimeFacade = new ExampleGeneratorRpcClient(runtimeRpc, "endpoint-reference");
const browserFacade = new ExampleGeneratorRpcClient(browserRpc, "provider-handle");
const runtimeUnary: Promise<FooBar2> = runtimeClient.DoIt(request);
const browserUnary: Promise<FooBar2> = browserClient.DoIt(nullableRequest);
const runtimeStream: AsyncIterable<FooBar2> = runtimeClient.DoIt2(request);
const browserStream: AsyncIterable<FooBar2> = browserClient.DoIt2(nullableRequest);
const facadeRuntimeUnary: Promise<FooBar2> = runtimeFacade.FooBar.DoIt(request);
const facadeBrowserUnary: Promise<FooBar2> = browserFacade.FooBar.DoIt(nullableRequest);
const facadeSecondaryUnary: Promise<FooBar2> = runtimeFacade.FooBar2.Read(request);
const context = null as unknown as RpcInvocationContext;
handler.invokeUnary(context, "foo-bar", "do-it", request);
const contractId: "example.generator" = CONTRACT_ID;
const contractVersion: "1.0.0" = CONTRACT_VERSION;
const descriptorHash: "${COMPILE_DESCRIPTOR_SHA256}" = CONTRACT_DESCRIPTOR_SHA256;
void provider;
void secondaryProvider;
void identity;
void runtimeUnary;
void browserUnary;
void runtimeStream;
void browserStream;
void facadeRuntimeUnary;
void facadeBrowserUnary;
void facadeSecondaryUnary;
void contractId;
void contractVersion;
void descriptorHash;
`) as GeneratedBindingsModule;
}

function compileGeneratedModule(source: string, usage: string): unknown {
  const packageRoot = sdkPackageRoot();
  const temporaryDirectory = mkdtempSync(join(packageRoot, ".generator-compile-"));
  try {
    writeFileSync(join(temporaryDirectory, "bindings.ts"), source, "utf8");
    writeFileSync(join(temporaryDirectory, "usage.ts"), usage, "utf8");
    const configPath = join(temporaryDirectory, "tsconfig.json");
    const outputDirectory = join(temporaryDirectory, "out");
    writeFileSync(configPath, `${JSON.stringify({
      extends: resolve(packageRoot, "../../tsconfig.base.json"),
      compilerOptions: {
        declaration: false,
        exactOptionalPropertyTypes: true,
        outDir: outputDirectory,
        rootDir: temporaryDirectory,
        skipLibCheck: false,
        sourceMap: false,
      },
      include: ["*.ts"],
    }, null, 2)}\n`, "utf8");

    const result = spawnSync(process.execPath, [require.resolve("typescript/bin/tsc"), "--project", configPath, "--pretty", "false"], {
      cwd: packageRoot,
      encoding: "utf8",
    });
    const diagnostics = [result.error?.stack, result.stdout, result.stderr].filter(Boolean).join("\n");
    assert.equal(result.status, 0, diagnostics);
    return require(join(outputDirectory, "bindings.js")) as unknown;
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
}

function descriptorSha256(value: RpcContractDescriptor): string {
  return createHash("sha256").update(canonicalRpcDescriptorBytes(value)).digest("hex");
}

function abiSha256(value: RpcContractDescriptorV2): string {
  return createHash("sha256")
    .update(SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR, "utf8")
    .update(canonicalRpcAbiProjectionBytes(value))
    .digest("hex");
}

function mismatchedHash(value: string): string {
  return `${value.startsWith("0") ? "1" : "0"}${value.slice(1)}`;
}

function runGeneratorCli(inputPath: string, outputPath: string, cwd: string) {
  return spawnSync(process.execPath, [join(sdkPackageRoot(), "dist/generator-cli.js"), inputPath, outputPath], {
    cwd,
    encoding: "utf8",
  });
}

function sdkPackageRoot(): string {
  return resolve(__dirname, "../..");
}
