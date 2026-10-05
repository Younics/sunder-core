import { createHash } from "node:crypto";
import {
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  isSemanticVersion,
  parseRpcContractDescriptor,
  type RpcContractDescriptor,
} from "@sunder/sdk";

const MAXIMUM_BYTES = 64 * 1024 * 1024;
const MAXIMUM_DESCRIPTOR_BYTES = 4 * 1024 * 1024;

export interface PlatformContractPins {
  readonly releaseVersion: string;
  readonly contentIdentity: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
}

interface CatalogContract {
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorBytes: number;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
  readonly canonicalDescriptor: string;
  readonly descriptor: RpcContractDescriptor;
}

export interface VerifiedPlatformContractCatalog {
  readonly releaseVersion: string;
  readonly contentIdentity: string;
  readonly contracts: readonly CatalogContract[];
  getCanonicalUtf8(): Uint8Array;
  verifyPinned(pins: PlatformContractPins): RpcContractDescriptor;
}

// The selected Registry establishes publication authority. This verifies the
// canonical catalog, complete descriptor inventory, and immutable content pins.
export async function verifyPlatformContractCatalog(canonicalCatalog: Uint8Array): Promise<VerifiedPlatformContractCatalog> {
  const { bytes, releaseVersion, contracts } = readCatalog(canonicalCatalog);
  const contentIdentity = sha256(bytes);
  deepFreeze(contracts);
  return Object.freeze({
    releaseVersion, contentIdentity, contracts,
    getCanonicalUtf8: () => Uint8Array.from(bytes),
    verifyPinned(pins: PlatformContractPins): RpcContractDescriptor {
      if (pins.releaseVersion !== releaseVersion || pins.contentIdentity !== contentIdentity)
        throw new Error("Platform catalog does not match the exact release and content pins.");
      const contract = contracts.find(contract => contract.contractId === pins.contractId);
      if (contract === undefined || contract.contractVersion !== pins.contractVersion
        || contract.descriptorSha256 !== pins.descriptorSha256 || contract.abiSha256 !== pins.abiSha256)
        throw new Error("Platform catalog does not contain the exact pinned descriptor identity and descriptor/ABI hashes.");
      return contract.descriptor;
    },
  });
}

export function validatePlatformCatalogCacheOwnership(canonicalCatalog: Uint8Array, expectedContentIdentity: string): void {
  if (sha256(readCatalog(canonicalCatalog).bytes) !== expectedContentIdentity)
    throw new Error("Cached platform catalog differs from its content address.");
}

function readCatalog(canonicalCatalog: Uint8Array) {
  const { value, bytes } = parseCanonical(canonicalCatalog);
  const root = object(value, ["schemaVersion", "authorityKind", "authorityId", "releaseVersion", "contractCount", "contracts"]);
  integer(root.schemaVersion, 1, 1);
  exact(root.authorityKind, "platform");
  exact(root.authorityId, "sunder.core");
  const releaseVersion = text(root.releaseVersion, 256);
  if (!isSemanticVersion(releaseVersion) || releaseVersion.split("+", 1)[0]!.includes("-"))
    throw new Error("Platform catalog release must be a stable strict SemVer version.");
  const count = integer(root.contractCount, 1, 128);
  const records = array(root.contracts, 1);
  if (records.length !== count) throw new Error("Platform catalog count does not match its complete inventory.");
  const contracts: CatalogContract[] = [];
  let previousId = "";
  for (const value of records) {
    const record = object(value, ["contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "canonicalDescriptor"]);
    const contractId = text(record.contractId, 128);
    if (contractId <= previousId) throw new Error("Platform catalog contract IDs must be unique and ordinally sorted.");
    previousId = contractId;
    const contractVersion = text(record.contractVersion, 256);
    const descriptorBytes = integer(record.descriptorBytes, 2, MAXIMUM_DESCRIPTOR_BYTES);
    const descriptorSha256 = digest(record.descriptorSha256);
    const abiSha256 = digest(record.abiSha256);
    const canonicalDescriptor = text(record.canonicalDescriptor, MAXIMUM_DESCRIPTOR_BYTES);
    if (Buffer.byteLength(canonicalDescriptor, "utf8") !== descriptorBytes)
      throw new Error("Platform descriptor UTF-8 byte count does not match.");
    const descriptor = parseRpcContractDescriptor(canonicalDescriptor);
    const canonicalBytes = canonicalRpcDescriptorBytes(descriptor);
    if (!Buffer.from(canonicalBytes).equals(Buffer.from(canonicalDescriptor, "utf8"))
      || descriptor.contractId !== contractId || descriptor.version !== contractVersion
      || sha256(canonicalBytes) !== descriptorSha256
      || createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8")
        .update(canonicalRpcAbiProjectionBytes(descriptor)).digest("hex") !== abiSha256)
      throw new Error("Platform descriptor canonical bytes, identity, or descriptor/ABI hashes do not match.");
    contracts.push({ contractId, contractVersion, descriptorBytes, descriptorSha256, abiSha256, canonicalDescriptor, descriptor });
  }
  if (contracts.filter(contract => contract.contractId === "sunder.stack.contributor").length !== 1)
    throw new Error("A platform catalog requires exactly one sunder.stack.contributor contract.");
  return { bytes, releaseVersion, contracts };
}

function parseCanonical(input: Uint8Array): { value: unknown; bytes: Buffer } {
  if (input.byteLength === 0 || input.byteLength > MAXIMUM_BYTES) throw new Error("Platform JSON exceeds its 64 MiB byte bound or is empty.");
  const bytes = Buffer.from(input);
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) throw new Error("Platform JSON must not have a BOM.");
  // Bound allocation before JSON.parse; exact reserialization rejects duplicates
  // and alternate encodings after grammar and Unicode validation.
  let depth = 0;
  let values = 0;
  let inString = false;
  let escaped = false;
  let inPrimitive = false;
  for (const byte of bytes) {
    if (inString) {
      if (escaped) escaped = false;
      else if (byte === 0x5c) escaped = true;
      else if (byte === 0x22) inString = false;
      continue;
    }
    switch (byte) {
      case 0x22:
        inString = true; inPrimitive = false; values++; break;
      case 0x5b: case 0x7b:
        if (++depth > 16) throw new Error("Platform JSON preflight exceeds depth 16.");
        inPrimitive = false; values++; break;
      case 0x5d: case 0x7d:
        depth--; inPrimitive = false; break;
      case 0x2c: case 0x3a: case 0x09: case 0x0a: case 0x0d: case 0x20:
        inPrimitive = false; break;
      default:
        if (!inPrimitive) values++;
        inPrimitive = true; break;
    }
    if (values > 262_144) throw new Error("Platform JSON preflight exceeds the 262144 value/property-name budget.");
  }
  const value: unknown = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  validate(value, 0);
  if (!bytes.equals(Buffer.from(JSON.stringify(value), "utf8"))) throw new Error("Platform JSON must be exact canonical compact UTF-8.");
  return { value, bytes };

  function validate(value: unknown, depth: number): void {
    if (typeof value === "string") {
      if (/[\ud800-\udfff]/u.test(value)) throw new Error("Platform JSON strings must contain Unicode scalars.");
    } else if (typeof value === "number") {
      if (!Number.isSafeInteger(value) || Object.is(value, -0)) throw new Error("Platform JSON numbers must be safe canonical integers.");
    } else if (typeof value === "object" && value !== null) {
      if (depth >= 16) throw new Error("Platform JSON exceeds depth 16.");
      const count = Array.isArray(value) ? value.length : Object.keys(value).length;
      if (count > 128) throw new Error("Platform JSON containers exceed the count bound of 128.");
      for (const [key, child] of Object.entries(value)) { validate(key, depth + 1); validate(child, depth + 1); }
    } else if (typeof value !== "boolean") throw new Error("Platform JSON does not permit null or undefined.");
  }
}

function object(value: unknown, order: readonly string[]): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)
    || JSON.stringify(Object.keys(value)) !== JSON.stringify(order))
    throw new Error("Platform JSON members must match the exact closed schema property order.");
  return value as Record<string, unknown>;
}
function array(value: unknown, minimum: number): unknown[] {
  if (!Array.isArray(value) || value.length < minimum || value.length > 128) throw new Error(`Platform array must contain ${minimum}..128 entries.`);
  return value;
}
function integer(value: unknown, minimum: number, maximum: number): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || Object.is(value, -0) || value < minimum || value > maximum)
    throw new Error(`Platform integer must be in ${minimum}..${maximum}.`);
  return value;
}
function text(value: unknown, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximum) throw new Error("Platform string has an invalid type or length.");
  return value;
}
function exact(value: unknown, expected: string): void {
  if (value !== expected) throw new Error(`Platform value must be '${expected}'.`);
}
function digest(value: unknown): string {
  const hash = text(value, 64);
  if (!/^[0-9a-f]{64}$/u.test(hash)) throw new Error("Platform digest must be lowercase SHA-256 hexadecimal.");
  return hash;
}
function sha256(bytes: Uint8Array): string { return createHash("sha256").update(bytes).digest("hex"); }
function deepFreeze<T>(value: T): T {
  if (value !== null && typeof value === "object") {
    for (const child of Object.values(value)) deepFreeze(child);
    Object.freeze(value);
  }
  return value;
}
