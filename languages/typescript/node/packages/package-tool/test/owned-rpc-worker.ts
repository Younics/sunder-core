import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { FrameDecoder, encodeFrame, formatUtcTimestamp, type JsonValue } from "@sunder/sdk";
import { currentRid } from "../src/model";

export async function exerciseOwnedWorker(root: string, dev = false, contractVersion = "1.0.0", scaffold?: { readonly packageName: string; readonly app: boolean }, runtimeModule?: string,
  consumer?: (invokeUnary: (method: string, request: JsonValue) => Promise<JsonValue>) => Promise<void>): Promise<void> {
  const target = resolve(root, "dist", ...(dev ? ["sunder-dev"] : ["targets", currentRid()]));
  const manifest = JSON.parse(await readFile(resolve(target, "manifest/sunder-package.json"), "utf8"));
  const providers = manifest.provides.map(({ role: _role, audience: _audience, ...identity }: Record<string, JsonValue>) => identity);
  assert.equal(manifest.contractBundles[0].version, contractVersion);
  assert.equal(manifest.version, scaffold ? "1.0.0" : "7.6.5");
  if (!dev || !scaffold?.app) assert.deepEqual(manifest.usesContracts, []);
  else assert.equal(manifest.usesContracts[0].role, "app");
  const descriptor = await readFile(resolve(target, "payload/shared", manifest.contractBundles[0].descriptorPath));
  assert.equal(createHash("sha256").update(descriptor).digest("hex"), providers[0].descriptorSha256);
  const identity = { packageId: manifest.id, packageVersion: manifest.version, activationId: "a".repeat(32), sessionId: "b".repeat(32) };
  const temporary = resolve(root, "worker-temp");
  const workspace = resolve(root, "workspace");
  await mkdir(temporary, { recursive: true }); await mkdir(workspace, { recursive: true });
  const child = spawn(process.execPath, [runtimeModule ?? resolve(target, "payload/runtime", dev ? "shared" : currentRid(), "bin/worker.cjs")], {
    env: { SUNDER_WORKER_PROTOCOL: "sunder.worker.v1", SUNDER_PACKAGE_ID: manifest.id, SUNDER_PACKAGE_VERSION: manifest.version,
      SUNDER_ACTIVATION_ID: identity.activationId, SUNDER_SESSION_ID: identity.sessionId, SUNDER_PACKAGE_CONTENT_ROOT: target, SUNDER_WORKER_TEMP_ROOT: temporary },
    stdio: "pipe",
  });
  const decoder = new FrameDecoder();
  const queue: JsonValue[] = [];
  let pending: ((value: JsonValue) => void) | undefined;
  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => { stderr += chunk.toString("utf8"); });
  child.stdout.on("data", (chunk: Buffer) => {
    for (const message of decoder.push(chunk)) {
      if (pending) { const resolveMessage = pending; pending = undefined; resolveMessage(message); }
      else queue.push(message);
    }
  });
  const next = async (): Promise<Record<string, JsonValue>> => {
    if (queue.length) return queue.shift() as Record<string, JsonValue>;
    return await new Promise((done, reject) => {
      const timer = setTimeout(() => { pending = undefined; reject(new Error(`Worker did not respond: ${stderr.slice(0, 1000)}`)); }, 5000);
      pending = (value) => { clearTimeout(timer); done(value as Record<string, JsonValue>); };
    });
  };
  const send = (message: JsonValue): void => { child.stdin.write(encodeFrame(message)); };
  const profile = { version: 1, rpcValueBytes: 1_048_576, valueDepth: 16, valueNodes: 16_384, objectMembers: 256, inlineDecodedBytes: 16_384, contentReferenceBytes: 1_048_576 };
  const invoke = (id: string, methodId: string, request: JsonValue, kind = "unary"): void => send({ type: "host.invoke", id, kind, providerId: providers[0].providerId,
    serviceId: scaffold ? "messages" : "echo", methodId, request, context: { callerPackageId: manifest.id, callerPackageVersion: manifest.version, deadlineUtc: formatUtcTimestamp(new Date(Date.now() + 30_000)), callDepth: 1,
      provider: { ...identity, sessionId: undefined, ...providers[0], activationEpoch: 1, sessionGeneration: 1, endpointReference: "rpc1_owned", catalogRevision: 1, state: "active", faultCode: null } } } as JsonValue);
  try {
    send({ type: "host.hello", protocol: "sunder.worker.v1", protocolVersion: 1, challenge: "owned", ...identity, providers,
      resourceProfile: profile, resourceProfileHash: createHash("sha256").update(JSON.stringify(profile)).digest("hex") });
    const ready = await next();
    assert.equal(ready.type, "worker.ready", stderr);
    assert.deepEqual((ready.contributions as Record<string, JsonValue>).rpcProviders, providers);
    send({ type: "host.start-candidate" }); assert.equal((await next()).type, "worker.candidate-started");
    send({ type: "host.commit-generation", activationId: identity.activationId, sessionGeneration: 1 }); assert.equal((await next()).type, "worker.generation-committed");
    send({ type: "host.prepare-storage-migrations", activationId: identity.activationId, sessionGeneration: 1 }); assert.equal((await next()).type, "worker.storage-migrations-prepared");
    send({ type: "host.activate", sessionGeneration: 1, workspaceRootPath: workspace }); assert.equal((await next()).type, "worker.activated");
    if (scaffold) {
      invoke("send", "send", { message: "packed scaffold" });
      assert.deepEqual(await next(), { type: "worker.result", id: "send", value: `Hello from ${scaffold.packageName}: packed scaffold` });
      invoke("stream", "watch", { message: "packed scaffold" }, "server-stream");
      assert.deepEqual(await next(), { type: "worker.event", id: "stream", value: "Watching packed scaffold" });
      assert.deepEqual(await next(), { type: "worker.complete", id: "stream" });
      const invalidRequests: JsonValue[] = [{ message: 42 }, { message: "" }, { message: "valid", extra: true }, {}];
      for (const [index, request] of invalidRequests.entries()) {
        invoke(`invalid-${index}`, "send", request);
        let rejected = await next();
        if (rejected.type === "worker.provider-fault-diagnostic") {
          send({ type: "host.result", id: rejected.id!, value: null });
          rejected = await next();
        }
        assert.equal(rejected.type, "worker.error");
      }
    } else {
    const value = { count: "9007199254740993000", bytes: "AAEC/w==", scores: { z: 0, a: 42 }, flag: false, nullable: null,
      note: null, chunks: ["", "AA=="], choice: { kind: "count", number: "-12345678901234567890" } };
    for (const method of ["round-trip", "async-trip"]) {
      invoke(method, method, value); const result = await next(); assert.equal(result.type, "worker.result"); assert.deepEqual(result.value, value);
    }
    const omitted = { count: "0", bytes: "", scores: {}, flag: false, nullable: null, choice: { kind: "text", text: "" } };
    invoke("omitted", "round-trip", omitted);
    assert.deepEqual(await next(), { type: "worker.result", id: "omitted", value: omitted });
    invoke("stream", "watch", value, "server-stream");
    assert.deepEqual(await next(), { type: "worker.event", id: "stream", value });
    assert.deepEqual(await next(), { type: "worker.complete", id: "stream" });
    invoke("cancel", "wait", value, "server-stream");
    assert.equal((await next()).type, "worker.event");
    send({ type: "host.cancel", id: "cancel" });
    const cancelled = await next(); assert.equal(cancelled.type, "worker.error"); assert.equal((cancelled.error as Record<string, JsonValue>).kind, "cancelled");
    invoke("domain", "fail", value);
    const error = await next(); assert.equal(error.type, "worker.error"); assert.equal((error.error as Record<string, JsonValue>).code, "rejected");
    invoke("fault", "fault", value);
    const diagnostic = await next(); assert.equal(diagnostic.type, "worker.provider-fault-diagnostic");
    send({ type: "host.result", id: diagnostic.id!, value: null });
    const fault = await next(); assert.equal(fault.type, "worker.error"); assert.doesNotMatch(JSON.stringify(fault.error), /Private implementation failure/);
    invoke("profile", "oversized", value);
    const exhausted = await next(); assert.equal(exhausted.type, "worker.error"); assert.equal((exhausted.error as Record<string, JsonValue>).kind, "resource-exhausted");
    }
    if (consumer) {
      let call = 0;
      await consumer(async (method, request) => {
        const id = `consumer-${++call}`; invoke(id, method, request);
        const result = await next(); assert.equal(result.type, "worker.result", JSON.stringify(result)); assert.equal(result.id, id);
        return result.value!;
      });
    }
    send({ type: "host.shutdown", shutdownId: "done", reason: "test" }); assert.equal((await next()).type, "worker.shutdown-ack");
  } finally { child.kill("SIGKILL"); }
}
