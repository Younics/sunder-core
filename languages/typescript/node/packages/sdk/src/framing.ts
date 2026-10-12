import { TextDecoder } from "node:util";
import type { JsonValue } from "./types";
import { recordResourceIntegerToken } from "./resource-integers";

export interface FrameLimits {
  readonly maxFrameBytes: number;
  readonly maxHeaderBytes: number;
  readonly maxMessageDepth: number;
  readonly maxRpcValueBytes: number;
  readonly maxValueDepth: number;
  readonly maxValueNodes: number;
  readonly maxObjectMembers: number;
  readonly maxEnvelopeReserveBytes: number;
}

const DEFAULT_LIMITS: FrameLimits = Object.freeze({
  maxFrameBytes: 8 * 1024 * 1024 + 64 * 1024,
  maxHeaderBytes: 8 * 1024,
  maxMessageDepth: 72,
  maxRpcValueBytes: 8 * 1024 * 1024,
  maxValueDepth: 64,
  maxValueNodes: 524_288,
  maxObjectMembers: 4_096,
  maxEnvelopeReserveBytes: 64 * 1024,
});

const strictUtf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
const trackedIntegerNames = Object.freeze([
  "activationEpoch",
  "callDepth",
  "catalogRevision",
  "contentReferenceBytes",
  "inlineDecodedBytes",
  "length",
  "objectMembers",
  "protocolVersion",
  "revision",
  "rpcValueBytes",
  "sequence",
  "sessionGeneration",
  "value",
  "valueDepth",
  "valueNodes",
  "version",
]);
const trackedIntegerNameIndexes = new Map(trackedIntegerNames.map((name, index) => [name, index]));
const decodedIntegerTokens = new Map<string, WeakMap<object, number>>(
  trackedIntegerNames.map((name) => [name, new WeakMap<object, number>()]),
);

export class ProtocolError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "ProtocolError";
  }
}

export class TransportResourceError extends ProtocolError {
  public constructor(message: string) {
    super(message);
    this.name = "TransportResourceError";
  }
}

export function hasCanonicalNonNegativeIntegerToken(
  root: object,
  name: string,
): boolean {
  const tokens = decodedIntegerTokens.get(name);
  if (tokens === undefined || !tokens.has(root)) return false;
  const property = Object.getOwnPropertyDescriptor(root, name);
  return property !== undefined
    && "value" in property
    && property.enumerable === true
    && property.configurable === true
    && property.writable === true
    && Object.is(property.value, tokens.get(root));
}

export function encodeFrame(value: JsonValue | Readonly<Record<string, unknown>>, limits: FrameLimits = DEFAULT_LIMITS): Buffer {
  let text: string;
  try {
    text = JSON.stringify(value);
  } catch (error) {
    throw new ProtocolError(`Protocol envelope is not JSON-compatible: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (text === undefined) throw new ProtocolError("Protocol envelope is not JSON-compatible.");
  const body = Buffer.from(text, "utf8");
  if (body.length === 0 || body.length > limits.maxFrameBytes) {
    throw new TransportResourceError("Protocol frame exceeds the configured body limit.");
  }
  validateEnvelopeResourceBounds(text, body.length, limits);
  const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, "ascii");
  if (header.length > limits.maxHeaderBytes) {
    throw new ProtocolError("Protocol frame header exceeds the configured limit.");
  }
  return Buffer.concat([header, body]);
}

export function encodedJsonByteLength(value: JsonValue, maximumBytes: number): number {
  const text = JSON.stringify(value);
  const bytes = Buffer.byteLength(text, "utf8");
  if (bytes > maximumBytes) throw new TransportResourceError("RPC value exceeds the negotiated byte limit.");
  return bytes;
}

export class FrameDecoder {
  #limits: FrameLimits;
  #header: Buffer;
  #headerLength = 0;
  #contentLength: number | null = null;
  #bodySegments: Buffer[] = [];
  #bodyBytes = 0;

  public constructor(limits: Partial<FrameLimits> = {}) {
    this.#limits = normalizeLimits(limits);
    this.#header = Buffer.allocUnsafe(this.#limits.maxHeaderBytes);
  }

  public configure(limits: Partial<FrameLimits>): void {
    if (this.#headerLength !== 0 || this.#contentLength !== null || this.#bodyBytes !== 0) {
      throw new ProtocolError("Frame limits cannot change while a frame is buffered.");
    }
    this.#limits = normalizeLimits(limits);
    this.#header = Buffer.allocUnsafe(this.#limits.maxHeaderBytes);
  }

  public push(chunk: Uint8Array): JsonValue[] {
    if (chunk.byteLength === 0) return [];
    const incoming = Buffer.from(chunk);
    const output: JsonValue[] = [];
    let offset = 0;
    while (offset < incoming.length) {
      if (this.#contentLength === null) {
        if (this.#headerLength === this.#limits.maxHeaderBytes) {
          throw new ProtocolError("Protocol frame header exceeds the configured limit.");
        }
        this.#header[this.#headerLength++] = incoming[offset++]!;
        if (this.#headerLength < 4
          || this.#header[this.#headerLength - 4] !== 0x0d
          || this.#header[this.#headerLength - 3] !== 0x0a
          || this.#header[this.#headerLength - 2] !== 0x0d
          || this.#header[this.#headerLength - 1] !== 0x0a) {
          continue;
        }
        const header = this.#header.subarray(0, this.#headerLength - 4).toString("ascii");
        const match = /^Content-Length: (0|[1-9][0-9]*)$/.exec(header);
        if (match === null) {
          throw new ProtocolError("Protocol frame requires exactly one canonical Content-Length header.");
        }
        const lengthText = match[1];
        if (lengthText === undefined) throw new ProtocolError("Protocol Content-Length is missing.");
        const length = Number(lengthText);
        if (!Number.isSafeInteger(length) || length <= 0 || length > this.#limits.maxFrameBytes) {
          throw new ProtocolError("Protocol Content-Length is outside the configured bounds.");
        }
        this.#contentLength = length;
        this.#headerLength = 0;
      }
      const remaining = this.#contentLength - this.#bodyBytes;
      const take = Math.min(remaining, incoming.length - offset);
      if (take > 0) {
        this.#bodySegments.push(incoming.subarray(offset, offset + take));
        this.#bodyBytes += take;
        offset += take;
      }
      if (this.#bodyBytes < this.#contentLength) continue;
      const body = this.#bodySegments.length === 1
        ? this.#bodySegments[0]!
        : Buffer.concat(this.#bodySegments, this.#bodyBytes);
      this.#contentLength = null;
      this.#bodySegments = [];
      this.#bodyBytes = 0;
      if (body.length >= 3 && body[0] === 0xef && body[1] === 0xbb && body[2] === 0xbf) {
        throw new ProtocolError("Protocol JSON must not contain a UTF-8 byte-order mark.");
      }
      let text: string;
      try {
        text = strictUtf8.decode(body);
      } catch {
        throw new ProtocolError("Protocol body is not strict UTF-8.");
      }
      const validation = validateJson(text, this.#limits.maxMessageDepth, true);
      validateEnvelopeResourceBounds(text, body.length, this.#limits, validation);
      let value: unknown;
      try {
        value = JSON.parse(text) as unknown;
      } catch {
        throw new ProtocolError("Protocol body is not valid JSON.");
      }
      attachDecodedIntegerTokens(value, validation.integerTokens);
      output.push(value as JsonValue);
    }
    return output;
  }

  public end(): void {
    if (this.#headerLength !== 0 || this.#contentLength !== null || this.#bodyBytes !== 0) {
      throw new ProtocolError("Protocol input ended in a partial frame.");
    }
  }
}

function normalizeLimits(limits: Partial<FrameLimits>): FrameLimits {
  const normalized = Object.freeze({ ...DEFAULT_LIMITS, ...limits });
  if (Object.values(normalized).some((value) => !Number.isSafeInteger(value) || value <= 0)) {
    throw new RangeError("Frame limits must be positive safe integers.");
  }
  return normalized;
}

function validateEnvelopeResourceBounds(
  text: string,
  bodyBytes: number,
  limits: FrameLimits,
  validatedJson?: JsonValidation,
): void {
  const validation = validatedJson ?? validateJson(text, limits.maxMessageDepth);
  const property = validation.ranges.has("request")
    ? "request"
    : validation.ranges.has("value")
      ? "value"
      : validation.ranges.has("error.details")
        ? "error.details"
        : undefined;
  const range = property === undefined ? undefined : validation.ranges.get(property);
  const valueText = range === undefined ? undefined : text.slice(range.start, range.end);
  const valueBytes = valueText === undefined ? 0 : Buffer.byteLength(valueText, "utf8");
  if (valueBytes > limits.maxRpcValueBytes) {
    throw new TransportResourceError("RPC value exceeds the negotiated byte limit.");
  }
  if (bodyBytes - valueBytes > limits.maxEnvelopeReserveBytes) {
    throw new TransportResourceError("Protocol envelope metadata exceeds its reserved byte limit.");
  }
  if (range !== undefined && range.maximumDepth > limits.maxValueDepth) {
    throw new TransportResourceError("RPC value exceeds the negotiated depth limit.");
  }
  if (range !== undefined && range.nodes > limits.maxValueNodes) {
    throw new TransportResourceError("RPC value exceeds the negotiated node-count limit.");
  }
  if (range !== undefined && range.maximumObjectMembers > limits.maxObjectMembers) {
    throw new TransportResourceError("RPC value exceeds the negotiated object-member limit.");
  }
}

interface JsonRange {
  readonly start: number;
  readonly end: number;
  readonly nodes: number;
  readonly maximumDepth: number;
  readonly maximumObjectMembers: number;
}

interface JsonValidation {
  readonly ranges: ReadonlyMap<string, JsonRange>;
  readonly integerTokens: DecodedIntegerTokens;
}

interface DecodedIntegerTokens {
  readonly containerParents: readonly number[];
  readonly containerKeys: readonly (string | number)[];
  readonly holderOrdinals: readonly number[];
  readonly nameIndexes: readonly number[];
  readonly values: readonly number[];
  readonly resourceHolders:readonly number[];readonly resourceNames:readonly string[];readonly resourceTokens:readonly string[];
}

function validateJson(text: string, maximumDepth: number, collectIntegerTokens = false): JsonValidation {
  let offset = 0;
  const topLevelProperties = new Map<string, JsonRange>();
  const containerParents: number[] = [];
  const containerKeys: (string | number)[] = [];
  const integerTokenHolderOrdinals: number[] = [];
  const integerTokenNameIndexes: number[] = [];
  const integerTokenValues: number[] = [];
  const resourceHolders:number[]=[];const resourceNames:string[]=[];const resourceTokens:string[]=[];
  let activeStats: { rootDepth: number; nodes: number; maximumDepth: number; maximumObjectMembers: number } | undefined;
  let lastNumberToken: string | undefined;
  let lastNumberValue: number | undefined;
  const numberPattern = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/uy;
  const whitespace = (): void => {
    while (offset < text.length && /[\u0009\u000a\u000d\u0020]/u.test(text[offset] ?? "")) offset += 1;
  };
  const fail = (message: string): never => {
    throw new ProtocolError(message);
  };
  const stringToken = (decode: boolean): string => {
    if (text[offset] !== '"') fail("Expected a JSON string.");
    const start = offset;
    let requiresScalarValidation = false;
    offset += 1;
    while (offset < text.length) {
      const value = text[offset];
      if (value === '"') {
        offset += 1;
        if (!decode && !requiresScalarValidation) return "";
        try {
          const parsed = JSON.parse(text.slice(start, offset)) as string;
          if (!hasUnicodeScalars(parsed)) return fail("JSON strings must contain only Unicode scalar values.");
          return parsed;
        } catch {
          return fail("JSON contains an invalid string escape.");
        }
      }
      if (value === "\\") {
        if (text[offset + 1] === "u") requiresScalarValidation = true;
        offset += 2;
      } else {
        if (value !== undefined && value.charCodeAt(0) < 0x20) fail("JSON string contains an unescaped control character.");
        if (value !== undefined && value.charCodeAt(0) >= 0xd800 && value.charCodeAt(0) <= 0xdfff) {
          requiresScalarValidation = true;
        }
        offset += 1;
      }
    }
    return fail("JSON string is unterminated.");
  };
  const value = (
    depth: number,
    context: "root" | "error" | undefined,
    parentContainerOrdinal = -1,
    containerKey: string | number = 0,
  ): void => {
    if (depth > maximumDepth) fail("JSON exceeds the configured depth limit.");
    whitespace();
    lastNumberToken = undefined;
    lastNumberValue = undefined;
    if (activeStats !== undefined) {
      activeStats.nodes += 1;
      activeStats.maximumDepth = Math.max(activeStats.maximumDepth, depth - activeStats.rootDepth + 1);
    }
    const current = text[offset];
    if (current === "{") {
      const currentContainerOrdinal = collectIntegerTokens ? containerParents.length : -1;
      if (collectIntegerTokens) {
        containerParents.push(parentContainerOrdinal);
        containerKeys.push(containerKey);
      }
      offset += 1;
      whitespace();
      let firstFolded: string | undefined;
      let folded: Set<string> | undefined;
      let memberCount = 0;
      if (text[offset] === "}") {
        offset += 1;
        return;
      }
      while (true) {
        const key = stringToken(true);
        const lower = asciiCaseFold(key);
        if (firstFolded === undefined) {
          firstFolded = lower;
        } else {
          folded ??= new Set([firstFolded]);
          if (folded.has(lower)) fail(`JSON contains duplicate or case-colliding property '${key}'.`);
          folded.add(lower);
        }
        memberCount += 1;
        if (activeStats !== undefined) {
          activeStats.maximumObjectMembers = Math.max(activeStats.maximumObjectMembers, memberCount);
        }
        whitespace();
        if (text[offset] !== ":") fail("JSON object property is missing ':'.");
        offset += 1;
        whitespace();
        const valueStart = offset;
        const rangeName = context === "root" && (key === "request" || key === "value")
          ? key
          : context === "error" && key === "details"
            ? "error.details"
            : undefined;
        const previousStats = activeStats;
        if (rangeName !== undefined) {
          activeStats = { rootDepth: depth + 1, nodes: 0, maximumDepth: 0, maximumObjectMembers: 0 };
        }
        value(
          depth + 1,
          context === "root" && key === "error" ? "error" : undefined,
          currentContainerOrdinal,
          key,
        );
        const numberToken = lastNumberToken;
        const numberValue = lastNumberValue;
        lastNumberToken = undefined;
        lastNumberValue = undefined;
        if(collectIntegerTokens&&numberToken!==undefined&&["activationEpoch","sessionGeneration","length"].includes(key)){
          resourceHolders.push(currentContainerOrdinal);resourceNames.push(key);resourceTokens.push(numberToken);
        }
        if (numberToken !== undefined
          && numberValue !== undefined
          && Number.isSafeInteger(numberValue)
          && numberValue >= 0
          && /^(?:0|[1-9][0-9]*)$/u.test(numberToken)) {
          const nameIndex = trackedIntegerNameIndexes.get(key);
          if (collectIntegerTokens && nameIndex !== undefined) {
            integerTokenHolderOrdinals.push(currentContainerOrdinal);
            integerTokenNameIndexes.push(nameIndex);
            integerTokenValues.push(numberValue);
          }
        }
        if (rangeName !== undefined) {
          const stats = activeStats!;
          topLevelProperties.set(rangeName, {
            start: valueStart,
            end: offset,
            nodes: stats.nodes,
            maximumDepth: stats.maximumDepth,
            maximumObjectMembers: stats.maximumObjectMembers,
          });
          activeStats = previousStats;
        }
        whitespace();
        if (text[offset] === "}") {
          offset += 1;
          return;
        }
        if (text[offset] !== ",") fail("JSON object is missing ','.");
        offset += 1;
        whitespace();
      }
    }
    if (current === "[") {
      const currentContainerOrdinal = collectIntegerTokens ? containerParents.length : -1;
      if (collectIntegerTokens) {
        containerParents.push(parentContainerOrdinal);
        containerKeys.push(containerKey);
      }
      offset += 1;
      whitespace();
      if (text[offset] === "]") {
        offset += 1;
        return;
      }
      let index = 0;
      while (true) {
        value(depth + 1, undefined, currentContainerOrdinal, index);
        index += 1;
        lastNumberToken = undefined;
        lastNumberValue = undefined;
        whitespace();
        if (text[offset] === "]") {
          offset += 1;
          return;
        }
        if (text[offset] !== ",") fail("JSON array is missing ','.");
        offset += 1;
      }
    }
    if (current === '"') {
      stringToken(false);
      return;
    }
    const literal = text.startsWith("true", offset)
      ? "true"
      : text.startsWith("false", offset)
        ? "false"
        : text.startsWith("null", offset)
          ? "null"
          : undefined;
    if (literal !== undefined) {
      offset += literal.length;
      return;
    }
    numberPattern.lastIndex = offset;
    const number = numberPattern.exec(text)?.[0];
    if (number !== undefined) {
      offset += number.length;
      const parsed = Number(number);
      if (!Number.isFinite(parsed)) fail("Protocol JSON contains a non-finite number.");
      lastNumberToken = number;
      lastNumberValue = parsed;
      return;
    }
    fail("JSON contains an invalid value.");
  };
  whitespace();
  value(1, "root");
  whitespace();
  if (offset !== text.length) fail("JSON contains trailing data.");
  return {
    ranges: topLevelProperties,
    integerTokens: {
      containerParents,
      containerKeys,
      holderOrdinals: integerTokenHolderOrdinals,
      nameIndexes: integerTokenNameIndexes,
      values: integerTokenValues,
      resourceHolders,resourceNames,resourceTokens,
    },
  };
}

function attachDecodedIntegerTokens(value: unknown, entries: DecodedIntegerTokens): void {
  if (entries.holderOrdinals.length === 0 && entries.resourceHolders.length===0) return;
  if (value === null || typeof value !== "object" || entries.containerParents[0] !== -1) {
    throw new ProtocolError("Protocol integer-token provenance did not match the decoded JSON structure.");
  }
  let maximumOrdinal = 0;
  for (const ordinal of entries.holderOrdinals) maximumOrdinal = Math.max(maximumOrdinal, ordinal);
  for (const ordinal of entries.resourceHolders) maximumOrdinal=Math.max(maximumOrdinal,ordinal);
  const containers: object[] = [value];
  for (let ordinal = 1; ordinal <= maximumOrdinal; ordinal++) {
    const parent = containers[entries.containerParents[ordinal]!] as object | undefined;
    const key = entries.containerKeys[ordinal]!;
    const child = parent === undefined ? undefined : Object.getOwnPropertyDescriptor(parent, key)?.value;
    if (child === null || typeof child !== "object") {
      throw new ProtocolError("Protocol integer-token provenance did not match the decoded JSON structure.");
    }
    containers.push(child);
  }
  for (let index = 0; index < entries.holderOrdinals.length; index++) {
    const holder = containers[entries.holderOrdinals[index]!]!;
    const name = trackedIntegerNames[entries.nameIndexes[index]!]!;
    decodedIntegerTokens.get(name)!.set(holder, entries.values[index]!);
  }
  for(let index=0;index<entries.resourceHolders.length;index++)recordResourceIntegerToken(containers[entries.resourceHolders[index]!]!,entries.resourceNames[index]!,entries.resourceTokens[index]!);
}

function asciiCaseFold(value: string): string {
  return value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 0x20));
}

function hasUnicodeScalars(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const current = value.charCodeAt(index);
    if (current < 0xd800 || current > 0xdfff) continue;
    if (current > 0xdbff || index + 1 >= value.length) return false;
    const next = value.charCodeAt(++index);
    if (next < 0xdc00 || next > 0xdfff) return false;
  }
  return true;
}
