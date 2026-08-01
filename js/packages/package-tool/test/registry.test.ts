import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import {
  canonicalizeDescriptor,
  type JsonValue,
  type RpcContractDescriptor,
} from "@sunder/sdk";
import { fetchRegistryContract, fetchRegistryContractBindings } from "../src/registry";

const descriptor: RpcContractDescriptor = {
  descriptorVersion: 1,
  contractId: "example.messages",
  version: "1.2.3",
  services: [{
    serviceId: "messages",
    methods: [{
      methodId: "send",
      kind: "unary",
      requestSchema: { $ref: "#/$defs/Request" },
      responseSchema: { $ref: "#/$defs/Response" },
    }],
  }],
  $defs: {
    Request: {
      type: "object",
      properties: { message: { type: "string", minLength: 1, maxLength: 128 } },
      required: ["message"],
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

test("Registry contract fetch verifies metadata, SHA, parser, canonical bytes, and feeds generation", async () => {
  const canonicalBytes = new TextEncoder().encode(canonicalizeDescriptor(descriptor as unknown as JsonValue));
  const sha256 = createHash("sha256").update(canonicalBytes).digest("hex");
  const calls: string[] = [];
  const fetchImplementation = sequenceFetch(calls, metadata(sha256, canonicalBytes.byteLength), canonicalBytes, sha256);

  const result = await fetchRegistryContractBindings({
    registryOrigin: "https://registry.example",
    contractId: "example.messages",
    version: "1.2.3",
    fetchImplementation,
  });

  assert.equal(result.metadata.sha256, sha256);
  assert.equal(result.descriptor.contractId, "example.messages");
  assert.deepEqual(result.canonicalBytes, canonicalBytes);
  assert.match(result.bindings, /export class MessagesClient/u);
  assert.deepEqual(calls, [
    "https://registry.example/api/v1/contracts/example.messages/versions/1.2.3",
    "https://registry.example/api/v1/contracts/example.messages/versions/1.2.3/descriptor",
  ]);
});

test("Registry contract fetch rejects hash mismatch and noncanonical descriptor content", async () => {
  const canonicalBytes = new TextEncoder().encode(canonicalizeDescriptor(descriptor as unknown as JsonValue));
  const sha256 = createHash("sha256").update(canonicalBytes).digest("hex");
  const tampered = canonicalBytes.slice();
  tampered[tampered.byteLength - 1] = 0x20;
  await assert.rejects(
    fetchRegistryContract({
      registryOrigin: "https://registry.example",
      contractId: "example.messages",
      version: "1.2.3",
      fetchImplementation: sequenceFetch([], metadata(sha256, tampered.byteLength), tampered),
    }),
    /SHA-256/u,
  );

  const formattedBytes = new TextEncoder().encode(JSON.stringify(descriptor, null, 2));
  const formattedSha = createHash("sha256").update(formattedBytes).digest("hex");
  await assert.rejects(
    fetchRegistryContract({
      registryOrigin: "https://registry.example",
      contractId: "example.messages",
      version: "1.2.3",
      fetchImplementation: sequenceFetch([], metadata(formattedSha, formattedBytes.byteLength), formattedBytes, formattedSha),
    }),
    /not canonical/u,
  );
});

function metadata(sha256: string, size: number): object {
  return {
    contractId: "example.messages",
    version: "1.2.3",
    sha256,
    size,
    descriptorDownloadUrl: "/api/v1/contracts/example.messages/versions/1.2.3/descriptor",
    firstPublisherPackageId: "example.publisher",
    firstPublisherPackageVersion: "2.0.0",
    publishedAtUtc: "2026-07-31T10:00:00Z",
  };
}

function sequenceFetch(
  calls: string[],
  metadataValue: object,
  descriptorBytes: Uint8Array,
  entityTag?: string,
): typeof fetch {
  let call = 0;
  return (async (input: string | URL | Request) => {
    calls.push(input instanceof Request ? input.url : input.toString());
    if (call++ === 0) {
      return new Response(JSON.stringify(metadataValue), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }
    return new Response(descriptorBytes, {
      status: 200,
      headers: {
        "content-type": "application/schema+json",
        "content-length": descriptorBytes.byteLength.toString(),
        ...(entityTag === undefined ? {} : { etag: `"${entityTag}"` }),
      },
    });
  }) as typeof fetch;
}
