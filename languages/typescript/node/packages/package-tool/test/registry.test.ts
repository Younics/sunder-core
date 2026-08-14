import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import {
  SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  type RpcContractDescriptorV1,
  type RpcContractDescriptorV2,
} from "@sunder/sdk";
import { fetchRegistryContract, fetchRegistryContractBindings } from "../src/registry";

const CONTRACT_ID = "example.messages";
const CONTRACT_VERSION = "1.2.3";
const DESCRIPTOR_PATH = `/api/v1/contracts/${CONTRACT_ID}/versions/${CONTRACT_VERSION}/descriptor`;
const METADATA_URL = `https://registry.example${DESCRIPTOR_PATH.slice(0, -"/descriptor".length)}`;
const DESCRIPTOR_URL = `https://registry.example${DESCRIPTOR_PATH}`;

const requestSchema = {
  type: "object",
  properties: { message: { type: "string", minLength: 1, maxLength: 128 } },
  required: ["message"],
  additionalProperties: false,
} as const;

const descriptor: RpcContractDescriptorV2 = {
  descriptorVersion: 2,
  contractId: CONTRACT_ID,
  version: CONTRACT_VERSION,
  errors: {},
  services: {
    messages: {
      methods: {
        send: {
          kind: "unary",
          idempotency: "none",
          errors: [],
          requestSchema: { $ref: "#/$defs/Request" },
          responseSchema: { $ref: "#/$defs/Response" },
        },
      },
    },
  },
  $defs: {
    Request: requestSchema,
    Response: {
      type: "object",
      properties: { accepted: { type: "boolean" } },
      required: ["accepted"],
      additionalProperties: false,
    },
  },
};

const canonicalBytes = canonicalRpcDescriptorBytes(descriptor);
const descriptorSha256 = sha256(canonicalBytes);
const abiSha256 = rpcAbiSha256(descriptor);

test("Registry contract fetch verifies V2 metadata and passes both identities into binding generation", async () => {
  const calls: string[] = [];
  const fetchImplementation = sequenceFetch(
    calls,
    metadataFor(descriptor),
    canonicalBytes,
    { entityTag: descriptorSha256 },
  );

  const result = await fetchRegistryContractBindings(fetchRequest(fetchImplementation));

  assert.equal(result.metadata.descriptorSha256, descriptorSha256);
  assert.equal(result.metadata.abiSha256, abiSha256);
  assert.equal(result.metadata.exposure, "public");
  assert.equal(result.metadata.firstPublisherPackageId, "example.publisher");
  assert.equal(result.metadata.firstPublisherPackageVersion, "2.0.0");
  assert.equal(result.metadata.publishedAtUtc, "2026-07-31T10:00:00Z");
  assert.equal(result.descriptor.descriptorVersion, 2);
  assert.equal(result.descriptor.contractId, CONTRACT_ID);
  assert.deepEqual(result.canonicalBytes, canonicalBytes);
  assert.match(result.bindings, /export class MessagesClient/u);
  assert.match(result.bindings, new RegExp(`CONTRACT_DESCRIPTOR_SHA256 = "${descriptorSha256}"`, "u"));
  assert.match(result.bindings, new RegExp(`CONTRACT_ABI_SHA256 = "${abiSha256}"`, "u"));
  assert.deepEqual(calls, [METADATA_URL, DESCRIPTOR_URL]);
});

test("annotation-only descriptor changes alter descriptor identity but preserve ABI identity", async () => {
  const annotated: RpcContractDescriptorV2 = {
    ...descriptor,
    $defs: {
      ...descriptor.$defs,
      Request: {
        ...requestSchema,
        title: "Send message request",
        description: "Documentation that does not alter the wire contract.",
      },
    },
  };
  const annotatedBytes = canonicalRpcDescriptorBytes(annotated);
  const annotatedDescriptorSha256 = sha256(annotatedBytes);
  const annotatedAbiSha256 = rpcAbiSha256(annotated);

  assert.notEqual(annotatedDescriptorSha256, descriptorSha256);
  assert.equal(annotatedAbiSha256, abiSha256);

  const result = await fetchRegistryContract(fetchRequest(sequenceFetch(
    [],
    metadataFor(annotated, annotatedBytes),
    annotatedBytes,
    { entityTag: annotatedDescriptorSha256 },
  )));
  assert.equal(result.metadata.descriptorSha256, annotatedDescriptorSha256);
  assert.equal(result.metadata.abiSha256, abiSha256);
});

test("Registry contract fetch rejects descriptor and ABI hash mismatches independently", async () => {
  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor, canonicalBytes, { descriptorSha256: mismatchedHash(descriptorSha256) }),
      canonicalBytes,
    ))),
    /descriptor SHA-256 does not match metadata/u,
  );

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor, canonicalBytes, { abiSha256: mismatchedHash(abiSha256) }),
      canonicalBytes,
    ))),
    /ABI SHA-256 does not match metadata/u,
  );
});

test("Registry contract fetch rejects package and private contract exposure", async () => {
  for (const exposure of ["package", "private"]) {
    const calls: string[] = [];
    await assert.rejects(
      fetchRegistryContract(fetchRequest(sequenceFetch(
        calls,
        metadataFor(descriptor, canonicalBytes, { exposure }),
        canonicalBytes,
      ))),
      /exposure must be public/u,
    );
    assert.deepEqual(calls, [METADATA_URL]);
  }
});

test("Registry contract fetch rejects V1 metadata instead of interpreting sha256", async () => {
  const legacyMetadata = {
    contractId: CONTRACT_ID,
    version: CONTRACT_VERSION,
    sha256: descriptorSha256,
    size: canonicalBytes.byteLength,
    descriptorDownloadUrl: DESCRIPTOR_URL,
    firstPublisherPackageId: "example.publisher",
    firstPublisherPackageVersion: "2.0.0",
    publishedAtUtc: "2026-07-31T10:00:00Z",
  };
  const calls: string[] = [];

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(calls, legacyMetadata, canonicalBytes))),
    /metadata descriptor SHA-256 is invalid/u,
  );
  assert.deepEqual(calls, [METADATA_URL]);
});

test("Registry contract fetch rejects a V1 descriptor even under V2-shaped metadata", async () => {
  const legacyDescriptor: RpcContractDescriptorV1 = {
    descriptorVersion: 1,
    contractId: CONTRACT_ID,
    version: CONTRACT_VERSION,
    services: [{
      serviceId: "messages",
      methods: [{
        methodId: "send",
        kind: "unary",
        requestSchema: { $ref: "#/$defs/Request" },
        responseSchema: { $ref: "#/$defs/Response" },
      }],
    }],
    $defs: descriptor.$defs,
  };
  const legacyBytes = canonicalRpcDescriptorBytes(legacyDescriptor);

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor, legacyBytes),
      legacyBytes,
      { entityTag: sha256(legacyBytes) },
    ))),
    /must use descriptorVersion 2/u,
  );
});

test("Registry contract metadata and descriptor require the exact requested ID and version", async () => {
  for (const overrides of [{ contractId: "other.messages" }, { version: "1.2.4" }]) {
    const calls: string[] = [];
    await assert.rejects(
      fetchRegistryContract(fetchRequest(sequenceFetch(
        calls,
        metadataFor(descriptor, canonicalBytes, overrides),
        canonicalBytes,
      ))),
      /metadata identity does not match the request/u,
    );
    assert.deepEqual(calls, [METADATA_URL]);
  }

  const mismatchedDescriptors: RpcContractDescriptorV2[] = [
    { ...descriptor, contractId: "other.messages" },
    { ...descriptor, version: "1.2.4" },
  ];
  for (const mismatchedDescriptor of mismatchedDescriptors) {
    const bytes = canonicalRpcDescriptorBytes(mismatchedDescriptor);
    await assert.rejects(
      fetchRegistryContract(fetchRequest(sequenceFetch(
        [],
        metadataFor(mismatchedDescriptor, bytes),
        bytes,
        { entityTag: sha256(bytes) },
      ))),
      /descriptor identity does not match metadata/u,
    );
  }
});

test("Registry contract fetch rejects noncanonical descriptor content after transport verification", async () => {
  const formattedBytes = new TextEncoder().encode(JSON.stringify(descriptor, null, 2));
  const formattedSha256 = sha256(formattedBytes);

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor, formattedBytes),
      formattedBytes,
      { entityTag: formattedSha256 },
    ))),
    /not canonical/u,
  );
});

test("Registry contract fetch accepts only the exact same-origin descriptor download URL", async () => {
  const untrustedUrls = [
    `https://other.example${DESCRIPTOR_PATH}`,
    `https://registry.example${DESCRIPTOR_PATH}/extra`,
    `${DESCRIPTOR_URL}?download=true`,
    `${DESCRIPTOR_URL}#descriptor`,
    `https://user:password@registry.example${DESCRIPTOR_PATH}`,
  ];

  for (const descriptorDownloadUrl of untrustedUrls) {
    const calls: string[] = [];
    await assert.rejects(
      fetchRegistryContract(fetchRequest(sequenceFetch(
        calls,
        metadataFor(descriptor, canonicalBytes, { descriptorDownloadUrl }),
        canonicalBytes,
      ))),
      /untrusted descriptor download URL/u,
    );
    assert.deepEqual(calls, [METADATA_URL]);
  }
});

test("Registry contract fetch rejects Registry origins that are not bare HTTP origins", async () => {
  const invalidOrigins = [
    "ftp://registry.example/",
    "https://registry.example/path",
    "https://registry.example/?query=true",
    "https://registry.example/#fragment",
    "https://user@registry.example/",
  ];

  for (const registryOrigin of invalidOrigins) {
    await assert.rejects(
      fetchRegistryContract({
        registryOrigin,
        contractId: CONTRACT_ID,
        version: CONTRACT_VERSION,
        fetchImplementation: unexpectedFetch,
      }),
      /registryOrigin must be an absolute HTTP\(S\) origin/u,
    );
  }
});

test("Registry contract fetch enforces descriptor content type, length, size, and ETag", async () => {
  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor),
      canonicalBytes,
      { descriptorHeaders: { "content-type": "application/json" } },
    ))),
    /unexpected content type/u,
  );

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor),
      canonicalBytes,
      { descriptorHeaders: { "content-length": String(canonicalBytes.byteLength + 1) } },
    ))),
    /Content-Length does not match metadata/u,
  );

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor, canonicalBytes, { size: canonicalBytes.byteLength + 1 }),
      canonicalBytes,
      { includeContentLength: false },
    ))),
    /descriptor size does not match metadata/u,
  );

  await assert.rejects(
    fetchRegistryContract(fetchRequest(sequenceFetch(
      [],
      metadataFor(descriptor),
      canonicalBytes,
      { entityTag: mismatchedHash(descriptorSha256) },
    ))),
    /ETag does not match metadata/u,
  );

  const result = await fetchRegistryContract(fetchRequest(sequenceFetch(
    [],
    metadataFor(descriptor),
    canonicalBytes,
    { includeContentLength: false },
  )));
  assert.deepEqual(result.canonicalBytes, canonicalBytes);
});

function metadataFor(
  value: RpcContractDescriptorV2,
  bytes: Uint8Array = canonicalRpcDescriptorBytes(value),
  overrides: Readonly<Record<string, unknown>> = {},
): Record<string, unknown> {
  return {
    contractId: CONTRACT_ID,
    version: CONTRACT_VERSION,
    descriptorSha256: sha256(bytes),
    abiSha256: rpcAbiSha256(value),
    exposure: "public",
    size: bytes.byteLength,
    descriptorDownloadUrl: DESCRIPTOR_URL,
    firstPublisherPackageId: "example.publisher",
    firstPublisherPackageVersion: "2.0.0",
    publishedAtUtc: "2026-07-31T10:00:00Z",
    ...overrides,
  };
}

function fetchRequest(fetchImplementation: typeof fetch) {
  return {
    registryOrigin: "https://registry.example",
    contractId: CONTRACT_ID,
    version: CONTRACT_VERSION,
    fetchImplementation,
  } as const;
}

interface SequenceFetchOptions {
  readonly descriptorHeaders?: Readonly<Record<string, string>>;
  readonly descriptorStatus?: number;
  readonly entityTag?: string;
  readonly includeContentLength?: boolean;
}

function sequenceFetch(
  calls: string[],
  metadataValue: object,
  descriptorBytes: Uint8Array,
  options: SequenceFetchOptions = {},
): typeof fetch {
  let call = 0;
  return (async (input: string | URL | Request, init?: RequestInit) => {
    const currentCall = call++;
    calls.push(input instanceof Request ? input.url : input.toString());
    assert.equal(init?.redirect, "error");
    assert.equal(
      new Headers(init?.headers).get("accept"),
      currentCall === 0 ? "application/json" : "application/schema+json",
    );
    if (currentCall === 0) {
      return new Response(JSON.stringify(metadataValue), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }

    const headers = new Headers(options.descriptorHeaders);
    if (!headers.has("content-type")) headers.set("content-type", "application/schema+json");
    if (options.includeContentLength !== false && !headers.has("content-length")) {
      headers.set("content-length", descriptorBytes.byteLength.toString());
    }
    if (options.entityTag !== undefined && !headers.has("etag")) headers.set("etag", `"${options.entityTag}"`);
    return new Response(descriptorBytes, {
      status: options.descriptorStatus ?? 200,
      headers,
    });
  }) as typeof fetch;
}

const unexpectedFetch = (async () => {
  throw new Error("fetch must not be called");
}) as typeof fetch;

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
}

function rpcAbiSha256(value: RpcContractDescriptorV2): string {
  return createHash("sha256")
    .update(SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR, "utf8")
    .update(canonicalRpcAbiProjectionBytes(value))
    .digest("hex");
}

function mismatchedHash(value: string): string {
  return `${value.startsWith("0") ? "1" : "0"}${value.slice(1)}`;
}
