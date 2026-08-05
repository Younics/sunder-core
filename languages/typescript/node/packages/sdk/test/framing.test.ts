import assert from "node:assert/strict";
import test from "node:test";
import { FrameDecoder, ProtocolError, encodeFrame } from "../src/framing";

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
