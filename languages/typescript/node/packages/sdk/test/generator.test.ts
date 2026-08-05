import assert from "node:assert/strict";
import test from "node:test";
import { canonicalizeDescriptor, generateTypeScriptBindings } from "../src/generator";
import { parseRpcContractDescriptor } from "../src/descriptor";
import type { RpcContractDescriptor } from "../src/types";

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

test("TypeScript binding generation is deterministic and mirrors C# binding shapes", () => {
  const first = generateTypeScriptBindings(descriptor);
  const second = generateTypeScriptBindings(descriptor);
  assert.equal(first, second);
  assert.match(first, /export interface Request/u);
  assert.match(first, /export interface IMessagesProvider/u);
  assert.match(first, /export class MessagesClient/u);
  assert.match(first, /Promise<Response>/u);
  assert.match(first, /AsyncIterable<Response>/u);
});

test("descriptor canonicalization sorts object keys and rejects non-safe descriptor numbers", () => {
  assert.equal(canonicalizeDescriptor({ z: 1, a: { y: true, x: false } }), '{"a":{"x":false,"y":true},"z":1}');
  assert.throws(() => canonicalizeDescriptor({ value: 1.5 }), /safe integers/u);
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
  assert.throws(() => parseRpcContractDescriptor('{"value":1.5}'), /safe integer/u);
});
