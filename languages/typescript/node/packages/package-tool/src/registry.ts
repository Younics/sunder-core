import { createHash } from "node:crypto";
import {
  canonicalizeDescriptor,
  generateTypeScriptBindings,
  isPackageId,
  isSemanticVersion,
  parseRpcContractDescriptor,
  type JsonValue,
  type RpcContractDescriptor,
  type TypeScriptGeneratorOptions,
} from "@sunder/sdk";

const MAXIMUM_DESCRIPTOR_BYTES = 4 * 1024 * 1024;
const MAXIMUM_METADATA_BYTES = 64 * 1024;

export interface RegistryContractFetchRequest {
  readonly registryOrigin: string | URL;
  readonly contractId: string;
  readonly version: string;
  readonly signal?: AbortSignal;
  readonly fetchImplementation?: typeof fetch;
}

export interface RegistryContractDescriptorMetadata {
  readonly contractId: string;
  readonly version: string;
  readonly sha256: string;
  readonly size: number;
  readonly descriptorDownloadUrl: string;
  readonly firstPublisherPackageId: string;
  readonly firstPublisherPackageVersion: string;
  readonly publishedAtUtc: string;
}

export interface FetchedRegistryContract {
  readonly metadata: RegistryContractDescriptorMetadata;
  readonly descriptor: RpcContractDescriptor;
  readonly canonicalBytes: Uint8Array;
}

export interface GeneratedRegistryContractBindings extends FetchedRegistryContract {
  readonly bindings: string;
}

export async function fetchRegistryContract(
  request: RegistryContractFetchRequest,
): Promise<FetchedRegistryContract> {
  const origin = parseOrigin(request.registryOrigin);
  if (!isPackageId(request.contractId)) throw new TypeError("contractId must be a canonical lowercase dot-separated ASCII identifier.");
  if (!isSemanticVersion(request.version)) throw new TypeError("version must be a canonical strict SemVer 2.0 version.");
  const fetchImplementation = request.fetchImplementation ?? fetch;
  const descriptorPath = `/api/v1/contracts/${encodeURIComponent(request.contractId)}/versions/${encodeURIComponent(request.version)}/descriptor`;
  const metadataUrl = new URL(descriptorPath.slice(0, -"/descriptor".length), origin);
  const metadataResponse = await fetchImplementation(metadataUrl, {
    headers: { accept: "application/json" },
    redirect: "error",
    ...(request.signal === undefined ? {} : { signal: request.signal }),
  });
  if (!metadataResponse.ok) throw new Error(`Registry contract metadata request failed with HTTP ${metadataResponse.status}.`);
  const metadataBytes = await readBounded(metadataResponse, MAXIMUM_METADATA_BYTES);
  let metadataValue: unknown;
  try {
    metadataValue = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(metadataBytes));
  } catch (error) {
    throw new TypeError(`Registry contract metadata is invalid UTF-8 JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
  const metadata = parseMetadata(metadataValue, request.contractId, request.version);

  const downloadUrl = new URL(metadata.descriptorDownloadUrl, origin);
  if (downloadUrl.origin !== origin.origin
      || downloadUrl.pathname !== descriptorPath
      || downloadUrl.search !== ""
      || downloadUrl.hash !== ""
      || downloadUrl.username !== ""
      || downloadUrl.password !== "") {
    throw new TypeError("Registry contract metadata returned an untrusted descriptor download URL.");
  }
  const descriptorResponse = await fetchImplementation(downloadUrl, {
    headers: { accept: "application/schema+json" },
    redirect: "error",
    ...(request.signal === undefined ? {} : { signal: request.signal }),
  });
  if (!descriptorResponse.ok) throw new Error(`Registry contract descriptor request failed with HTTP ${descriptorResponse.status}.`);
  const contentType = descriptorResponse.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (contentType !== "application/schema+json") throw new TypeError("Registry contract descriptor has an unexpected content type.");
  const declaredLength = descriptorResponse.headers.get("content-length");
  if (declaredLength !== null && (!/^[0-9]+$/u.test(declaredLength) || Number(declaredLength) !== metadata.size)) {
    throw new TypeError("Registry contract descriptor Content-Length does not match metadata.");
  }
  const canonicalBytes = await readBounded(descriptorResponse, metadata.size);
  if (canonicalBytes.byteLength !== metadata.size) throw new TypeError("Registry contract descriptor size does not match metadata.");
  const actualSha256 = createHash("sha256").update(canonicalBytes).digest("hex");
  if (actualSha256 !== metadata.sha256) throw new TypeError("Registry contract descriptor SHA-256 does not match metadata.");
  const entityTag = descriptorResponse.headers.get("etag");
  if (entityTag !== null && entityTag !== `"${metadata.sha256}"`) throw new TypeError("Registry contract descriptor ETag does not match metadata.");

  const descriptor = parseRpcContractDescriptor(canonicalBytes);
  if (descriptor.contractId !== metadata.contractId || descriptor.version !== metadata.version) {
    throw new TypeError("Registry contract descriptor identity does not match metadata.");
  }
  const reparsedCanonical = new TextEncoder().encode(canonicalizeDescriptor(descriptor as unknown as JsonValue));
  if (!bytesEqual(canonicalBytes, reparsedCanonical)) throw new TypeError("Registry contract descriptor response is not canonical.");
  return { metadata, descriptor, canonicalBytes };
}

export async function fetchRegistryContractBindings(
  request: RegistryContractFetchRequest,
  generatorOptions: TypeScriptGeneratorOptions = {},
): Promise<GeneratedRegistryContractBindings> {
  const fetched = await fetchRegistryContract(request);
  return {
    ...fetched,
    bindings: generateTypeScriptBindings(fetched.descriptor, generatorOptions),
  };
}

async function readBounded(response: Response, maximumBytes: number): Promise<Uint8Array> {
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 1 || maximumBytes > MAXIMUM_DESCRIPTOR_BYTES) {
    throw new TypeError("Registry response byte limit is invalid.");
  }
  const contentLength = response.headers.get("content-length");
  if (contentLength !== null && (!/^[0-9]+$/u.test(contentLength) || Number(contentLength) > maximumBytes)) {
    throw new TypeError("Registry response exceeds its byte limit.");
  }
  if (response.body === null) throw new TypeError("Registry response body is missing.");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const result = await reader.read();
      if (result.done) break;
      length += result.value.byteLength;
      if (length > maximumBytes) throw new TypeError("Registry response exceeds its byte limit.");
      chunks.push(result.value);
    }
  } catch (error) {
    await reader.cancel(error).catch(() => undefined);
    throw error;
  }
  const output = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    output.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return output;
}

function parseMetadata(
  value: unknown,
  expectedContractId: string,
  expectedVersion: string,
): RegistryContractDescriptorMetadata {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Registry contract metadata must be an object.");
  const metadata = value as Partial<RegistryContractDescriptorMetadata>;
  if (metadata.contractId !== expectedContractId || metadata.version !== expectedVersion) {
    throw new TypeError("Registry contract metadata identity does not match the request.");
  }
  if (typeof metadata.sha256 !== "string" || !/^[0-9a-f]{64}$/u.test(metadata.sha256)) {
    throw new TypeError("Registry contract metadata SHA-256 is invalid.");
  }
  if (!Number.isSafeInteger(metadata.size) || metadata.size === undefined || metadata.size < 1 || metadata.size > MAXIMUM_DESCRIPTOR_BYTES) {
    throw new TypeError("Registry contract metadata size is invalid.");
  }
  if (typeof metadata.descriptorDownloadUrl !== "string" || metadata.descriptorDownloadUrl.length === 0) {
    throw new TypeError("Registry contract metadata descriptorDownloadUrl is invalid.");
  }
  if (typeof metadata.firstPublisherPackageId !== "string" || !isPackageId(metadata.firstPublisherPackageId)
      || typeof metadata.firstPublisherPackageVersion !== "string" || !isSemanticVersion(metadata.firstPublisherPackageVersion)
      || typeof metadata.publishedAtUtc !== "string" || !Number.isFinite(Date.parse(metadata.publishedAtUtc))) {
    throw new TypeError("Registry contract metadata publication attribution is invalid.");
  }
  return metadata as RegistryContractDescriptorMetadata;
}

function parseOrigin(value: string | URL): URL {
  const origin = new URL(value);
  if ((origin.protocol !== "https:" && origin.protocol !== "http:")
      || origin.pathname !== "/"
      || origin.search !== ""
      || origin.hash !== ""
      || origin.username !== ""
      || origin.password !== "") {
    throw new TypeError("registryOrigin must be an absolute HTTP(S) origin without path, query, fragment, or credentials.");
  }
  return origin;
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.byteLength !== right.byteLength) return false;
  for (let index = 0; index < left.byteLength; index++) if (left[index] !== right[index]) return false;
  return true;
}
