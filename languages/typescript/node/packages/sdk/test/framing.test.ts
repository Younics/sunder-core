import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import { FrameDecoder, ProtocolError, TransportResourceError, encodeFrame } from "../src/framing";

test("Content-Length framing round trips across adversarial chunk boundaries", () => {
  const frame = encodeFrame({ type: "worker.result", id: "a", value: { text: "snowman \u2603" } });
  const decoder = new FrameDecoder();
  const values: unknown[] = [];
  for (const byte of frame) values.push(...decoder.push(Buffer.of(byte)));
  decoder.end();
  assert.deepEqual(values, [{ type: "worker.result", id: "a", value: { text: "snowman \u2603" } }]);
});

test("framing rejects malformed headers, oversized bodies, invalid UTF-8, and duplicate properties", () => {
  assert.throws(
    () => new FrameDecoder().push(Buffer.from("content-length: 2\r\n\r\n{}")),
    ProtocolError,
  );
  assert.throws(
    () => new FrameDecoder({ maxFrameBytes: 2 }).push(Buffer.from("Content-Length: 3\r\n\r\n{}x")),
    ProtocolError,
  );
  assert.throws(
    () => new FrameDecoder().push(Buffer.concat([
      Buffer.from("Content-Length: 2\r\n\r\n"),
      Buffer.from([0xc3, 0x28]),
    ])),
    ProtocolError,
  );
  const duplicate = Buffer.from('{"id":"a","ID":"b"}', "utf8");
  assert.throws(
    () => new FrameDecoder().push(Buffer.concat([
      Buffer.from(`Content-Length: ${duplicate.length}\r\n\r\n`),
      duplicate,
    ])),
    /duplicate or case-colliding/u,
  );
});

test("framing rejects excessive JSON depth and partial terminal frames", () => {
  const frame = encodeFrame([[[[null]]]]);
  assert.throws(() => new FrameDecoder({ maxMessageDepth: 3 }).push(frame), ProtocolError);
  const decoder = new FrameDecoder();
  decoder.push(Buffer.from("Content-Length: 10\r\n\r\n{}"));
  assert.throws(() => decoder.end(), /partial frame/u);

  const nonFinite = Buffer.from('{"value":1e400}', "utf8");
  assert.throws(
    () => new FrameDecoder().push(Buffer.concat([
      Buffer.from(`Content-Length: ${nonFinite.length}\r\n\r\n`),
      nonFinite,
    ])),
    /non-finite number/u,
  );
});

test("framing uses ASCII-only collision folding for nested RPC values", () => {
  const value = {
    type: "host.result",
    id: "unicode1",
    value: { k: true, "\u212a": false, "\u00e9": 1, "\u00c9": 2 },
  };

  assert.deepEqual(new FrameDecoder().push(encodeFrame(value)), [value]);
  const asciiCollision = Buffer.from('{"type":"host.result","value":{"a":1,"A":2}}', "utf8");
  assert.throws(
    () => new FrameDecoder().push(Buffer.concat([
      Buffer.from(`Content-Length: ${asciiCollision.length}\r\n\r\n`),
      asciiCollision,
    ])),
    /duplicate or case-colliding/u,
  );
});

test("framing admits the exact value plus metadata envelope and rejects one byte or depth beyond it", () => {
  const value = { text: "snowman \u2603 \" \\ \u00e9" };
  const envelope = { type: "worker.result", id: "escape", metadata: "m".repeat(128), value };
  const text = JSON.stringify(envelope);
  const valueBytes = Buffer.byteLength(JSON.stringify(value), "utf8");
  const bodyBytes = Buffer.byteLength(text, "utf8");
  const reserveBytes = bodyBytes - valueBytes;
  const limits = {
    maxFrameBytes: bodyBytes,
    maxHeaderBytes: 8 * 1024,
    maxMessageDepth: 11,
    maxRpcValueBytes: valueBytes,
    maxValueDepth: 3,
    maxEnvelopeReserveBytes: reserveBytes,
  } as const;

  const exact = encodeFrame(envelope, limits);
  assert.equal(exact.subarray(exact.indexOf("\r\n\r\n", 0, "ascii") + 4).toString("utf8"), text);
  assert.throws(() => encodeFrame({ ...envelope, metadata: `${envelope.metadata}m` }, limits), TransportResourceError);

  const depthLimits = {
    ...limits,
    maxFrameBytes: 1024 + 256,
    maxRpcValueBytes: 1024,
    maxEnvelopeReserveBytes: 256,
  };
  encodeFrame({ type: "worker.result", value: [[null]] }, depthLimits);
  assert.throws(() => encodeFrame({ type: "worker.result", value: [[[null]]] }, depthLimits), ProtocolError);

  const errorEnvelope = {
    type: "worker.error",
    id: "escape",
    metadata: "m".repeat(128),
    error: { kind: "domain", code: "example.rejected", message: "Rejected.", details: value },
  };
  const errorText = JSON.stringify(errorEnvelope);
  const errorReserveBytes = Buffer.byteLength(errorText, "utf8") - valueBytes;
  const errorLimits = {
    ...limits,
    maxFrameBytes: Buffer.byteLength(errorText, "utf8"),
    maxEnvelopeReserveBytes: errorReserveBytes,
  };
  encodeFrame(errorEnvelope, errorLimits);
  assert.throws(
    () => encodeFrame({ ...errorEnvelope, metadata: `${errorEnvelope.metadata}m` }, errorLimits),
    TransportResourceError,
  );
  encodeFrame({ type: "host.error", error: { ...errorEnvelope.error, details: [[null]] } }, depthLimits);
  assert.throws(
    () => encodeFrame({ type: "host.error", error: { ...errorEnvelope.error, details: [[[null]]] } }, depthLimits),
    ProtocolError,
  );
});

test("framing executes the shared cross-language Worker transport vectors", async () => {
  await verifySharedTransportVectors(false);
});

test("shared Worker transport oracle rejects a seeded expectation mutation", async () => {
  await assert.rejects(() => verifySharedTransportVectors(true), /fragmented-unicode-frame/u);
});

interface TransportVector {
  readonly name: string;
  readonly valid: boolean;
  readonly header: string;
  readonly bodyUtf8?: string;
  readonly bodyBase64?: string;
  readonly maxFrameBytes?: number;
  readonly maxMessageDepth?: number;
}

async function verifySharedTransportVectors(mutateFirstExpectation: boolean): Promise<void> {
  const path = resolve(__dirname, "../../../../../../../specs/worker/v3/conformance/transport-vectors.json");
  const corpus = JSON.parse(await readFile(path, "utf8")) as {
    readonly formatVersion: number;
    readonly vectors: readonly TransportVector[];
  };
  assert.equal(corpus.formatVersion, 1);
  assert.ok(corpus.vectors.length >= 10, "The shared Worker transport corpus lost boundary vectors.");
  for (const [index, vector] of corpus.vectors.entries()) {
    const body = vector.bodyBase64 === undefined
      ? Buffer.from(vector.bodyUtf8 ?? "", "utf8")
      : Buffer.from(vector.bodyBase64, "base64");
    const header = Buffer.from(vector.header.replace("{{length}}", String(body.length)), "ascii");
    const wire = Buffer.concat([header, body]);
    const decoder = new FrameDecoder({
      ...(vector.maxFrameBytes === undefined ? {} : { maxFrameBytes: vector.maxFrameBytes }),
      ...(vector.maxMessageDepth === undefined ? {} : { maxMessageDepth: vector.maxMessageDepth }),
    });
    const expected = mutateFirstExpectation && index === 0 ? !vector.valid : vector.valid;
    let valid = true;
    try {
      const values: unknown[] = [];
      for (const byte of wire) values.push(...decoder.push(Buffer.of(byte)));
      decoder.end();
      valid = values.length === 1;
    } catch {
      valid = false;
    }
    assert.equal(valid, expected, vector.name);
  }
}
