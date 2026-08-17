import { TextDecoder } from "node:util";
import type { JsonValue } from "./types";

export interface FrameLimits {
  readonly maxFrameBytes: number;
  readonly maxHeaderBytes: number;
  readonly maxMessageDepth: number;
  readonly maxRpcValueBytes: number;
  readonly maxValueDepth: number;
  readonly maxEnvelopeReserveBytes: number;
}

const DEFAULT_LIMITS: FrameLimits = Object.freeze({
  maxFrameBytes: 8 * 1024 * 1024 + 64 * 1024,
  maxHeaderBytes: 8 * 1024,
  maxMessageDepth: 72,
  maxRpcValueBytes: 8 * 1024 * 1024,
  maxValueDepth: 64,
  maxEnvelopeReserveBytes: 64 * 1024,
});

const strictUtf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });

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
  validateEnvelopeResourceBounds(text, value, body.length, limits);
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
      validateJson(text, this.#limits.maxMessageDepth);
      let value: unknown;
      try {
        value = JSON.parse(text) as unknown;
      } catch {
        throw new ProtocolError("Protocol body is not valid JSON.");
      }
      rejectNonFiniteNumbers(value);
      validateEnvelopeResourceBounds(text, value as JsonValue, body.length, this.#limits);
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
  value: JsonValue | Readonly<Record<string, unknown>>,
  bodyBytes: number,
  limits: FrameLimits,
): void {
  const ranges = validateJson(text, limits.maxMessageDepth);
  const root = value !== null && typeof value === "object" && !Array.isArray(value)
    ? value as Readonly<Record<string, unknown>>
    : undefined;
  const error = root?.error !== null && typeof root?.error === "object" && !Array.isArray(root.error)
    ? root.error as Readonly<Record<string, unknown>>
    : undefined;
  const property = root !== undefined && Object.hasOwn(root, "request")
    ? "request"
    : root !== undefined && Object.hasOwn(root, "value")
      ? "value"
      : error !== undefined && Object.hasOwn(error, "details")
        ? "error.details"
        : undefined;
  const range = property === undefined ? undefined : ranges.get(property);
  const valueText = range === undefined ? undefined : text.slice(range.start, range.end);
  const valueBytes = valueText === undefined ? 0 : Buffer.byteLength(valueText, "utf8");
  if (valueBytes > limits.maxRpcValueBytes) {
    throw new TransportResourceError("RPC value exceeds the negotiated byte limit.");
  }
  if (bodyBytes - valueBytes > limits.maxEnvelopeReserveBytes) {
    throw new TransportResourceError("Protocol envelope metadata exceeds its reserved byte limit.");
  }
  if (valueText !== undefined) validateJson(valueText, limits.maxValueDepth);
}

function rejectNonFiniteNumbers(value: unknown): void {
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new ProtocolError("Protocol JSON contains a non-finite number.");
    return;
  }
  if (value === null || typeof value !== "object") return;
  for (const child of Array.isArray(value) ? value : Object.values(value)) rejectNonFiniteNumbers(child);
}

interface JsonRange {
  readonly start: number;
  readonly end: number;
}

function validateJson(text: string, maximumDepth: number): ReadonlyMap<string, JsonRange> {
  let offset = 0;
  const topLevelProperties = new Map<string, JsonRange>();
  const whitespace = (): void => {
    while (offset < text.length && /[\u0009\u000a\u000d\u0020]/u.test(text[offset] ?? "")) offset += 1;
  };
  const fail = (message: string): never => {
    throw new ProtocolError(message);
  };
  const stringToken = (): string => {
    if (text[offset] !== '"') fail("Expected a JSON string.");
    const start = offset;
    offset += 1;
    while (offset < text.length) {
      const value = text[offset];
      if (value === '"') {
        offset += 1;
        try {
          return JSON.parse(text.slice(start, offset)) as string;
        } catch {
          return fail("JSON contains an invalid string escape.");
        }
      }
      if (value === "\\") {
        offset += 2;
      } else {
        if (value !== undefined && value.charCodeAt(0) < 0x20) fail("JSON string contains an unescaped control character.");
        offset += 1;
      }
    }
    return fail("JSON string is unterminated.");
  };
  const value = (depth: number, path: string): void => {
    if (depth > maximumDepth) fail("JSON exceeds the configured depth limit.");
    whitespace();
    const current = text[offset];
    if (current === "{") {
      offset += 1;
      whitespace();
      const exact = new Set<string>();
      const folded = new Set<string>();
      if (text[offset] === "}") {
        offset += 1;
        return;
      }
      while (true) {
        const key = stringToken();
        const lower = asciiCaseFold(key);
        if (exact.has(key) || folded.has(lower)) fail(`JSON contains duplicate or case-colliding property '${key}'.`);
        exact.add(key);
        folded.add(lower);
        whitespace();
        if (text[offset] !== ":") fail("JSON object property is missing ':'.");
        offset += 1;
        whitespace();
        const valueStart = offset;
        const childPath = path.length === 0 ? key : `${path}.${key}`;
        value(depth + 1, childPath);
        if (depth === 1 || path === "error" && key === "details") {
          topLevelProperties.set(childPath, { start: valueStart, end: offset });
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
      offset += 1;
      whitespace();
      if (text[offset] === "]") {
        offset += 1;
        return;
      }
      while (true) {
        value(depth + 1, path);
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
      stringToken();
      return;
    }
    const remainder = text.slice(offset);
    const literal = /^(?:true|false|null)/u.exec(remainder)?.[0];
    if (literal !== undefined) {
      offset += literal.length;
      return;
    }
    const number = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/u.exec(remainder)?.[0];
    if (number !== undefined) {
      offset += number.length;
      return;
    }
    fail("JSON contains an invalid value.");
  };
  whitespace();
  value(1, "");
  whitespace();
  if (offset !== text.length) fail("JSON contains trailing data.");
  return topLevelProperties;
}

function asciiCaseFold(value: string): string {
  return value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 0x20));
}
