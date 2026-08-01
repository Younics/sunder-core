import { TextDecoder } from "node:util";
import type { JsonValue } from "./types";

export interface FrameLimits {
  readonly maxFrameBytes: number;
  readonly maxHeaderBytes: number;
  readonly maxMessageDepth: number;
}

const DEFAULT_LIMITS: FrameLimits = Object.freeze({
  maxFrameBytes: 1024 * 1024,
  maxHeaderBytes: 8 * 1024,
  maxMessageDepth: 64,
});

const strictUtf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });

export class ProtocolError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "ProtocolError";
  }
}

export function encodeFrame(value: JsonValue | Readonly<Record<string, unknown>>, limits: FrameLimits = DEFAULT_LIMITS): Buffer {
  const body = Buffer.from(JSON.stringify(value), "utf8");
  if (body.length === 0 || body.length > limits.maxFrameBytes) {
    throw new ProtocolError("Protocol frame exceeds the configured body limit.");
  }
  const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, "ascii");
  if (header.length > limits.maxHeaderBytes) {
    throw new ProtocolError("Protocol frame header exceeds the configured limit.");
  }
  return Buffer.concat([header, body]);
}

export class FrameDecoder {
  readonly #limits: FrameLimits;
  #buffer = Buffer.alloc(0);
  #contentLength: number | null = null;

  public constructor(limits: Partial<FrameLimits> = {}) {
    this.#limits = Object.freeze({ ...DEFAULT_LIMITS, ...limits });
    if (Object.values(this.#limits).some((value) => !Number.isSafeInteger(value) || value <= 0)) {
      throw new RangeError("Frame limits must be positive safe integers.");
    }
  }

  public push(chunk: Uint8Array): JsonValue[] {
    if (chunk.byteLength === 0) return [];
    const incoming = Buffer.from(chunk);
    if (this.#buffer.length + incoming.length > this.#limits.maxHeaderBytes + this.#limits.maxFrameBytes) {
      throw new ProtocolError("Buffered protocol data exceeds the configured bounds.");
    }
    this.#buffer = this.#buffer.length === 0 ? incoming : Buffer.concat([this.#buffer, incoming]);
    const output: JsonValue[] = [];
    while (true) {
      if (this.#contentLength === null) {
        const boundary = this.#buffer.indexOf("\r\n\r\n", 0, "ascii");
        if (boundary < 0) {
          if (this.#buffer.length >= this.#limits.maxHeaderBytes) {
            throw new ProtocolError("Protocol frame header exceeds the configured limit.");
          }
          break;
        }
        if (boundary + 4 > this.#limits.maxHeaderBytes) {
          throw new ProtocolError("Protocol frame header exceeds the configured limit.");
        }
        const header = this.#buffer.subarray(0, boundary).toString("ascii");
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
        this.#buffer = this.#buffer.subarray(boundary + 4);
      }
      if (this.#buffer.length < this.#contentLength) break;
      const body = this.#buffer.subarray(0, this.#contentLength);
      this.#buffer = this.#buffer.subarray(this.#contentLength);
      this.#contentLength = null;
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
      output.push(value as JsonValue);
    }
    return output;
  }

  public end(): void {
    if (this.#buffer.length !== 0 || this.#contentLength !== null) {
      throw new ProtocolError("Protocol input ended in a partial frame.");
    }
  }
}

function rejectNonFiniteNumbers(value: unknown): void {
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new ProtocolError("Protocol JSON contains a non-finite number.");
    return;
  }
  if (value === null || typeof value !== "object") return;
  for (const child of Array.isArray(value) ? value : Object.values(value)) rejectNonFiniteNumbers(child);
}

function validateJson(text: string, maximumDepth: number): void {
  let offset = 0;
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
  const value = (depth: number): void => {
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
        const lower = key.toLocaleLowerCase("en-US");
        if (exact.has(key) || folded.has(lower)) fail(`JSON contains duplicate or case-colliding property '${key}'.`);
        exact.add(key);
        folded.add(lower);
        whitespace();
        if (text[offset] !== ":") fail("JSON object property is missing ':'.");
        offset += 1;
        value(depth + 1);
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
        value(depth + 1);
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
  value(1);
  whitespace();
  if (offset !== text.length) fail("JSON contains trailing data.");
}
