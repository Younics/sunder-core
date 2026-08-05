import assert from "node:assert/strict";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { FrameDecoder, encodeFrame } from "../src/framing";
import { formatUtcTimestamp } from "../src/timestamp";
import type { JsonValue } from "../src/types";

const packageIdentity = {
  packageId: "example.package",
  packageVersion: "1.0.0",
  activationId: "a".repeat(32),
  sessionId: "b".repeat(32),
};
const provider = {
  providerId: "example.provider",
  contractId: "example.rpc",
  contractVersion: "1.0.0",
  contractSha256: "a".repeat(64),
};

test("worker performs handshake, unary, stream, cancellation, and graceful shutdown", async () => {
  const worker = startWorker();
  try {
    await handshake(worker);
    send(worker, invocation("u1", "unary", "echo"));
    assert.equal((await worker.messages.next()).type, "worker.result");
    send(worker, { type: "host.cancel", id: "u1" });

    send(worker, invocation("s1", "server-stream", "watch"));
    assert.equal((await worker.messages.next()).type, "worker.event");
    assert.equal((await worker.messages.next()).type, "worker.event");
    assert.equal((await worker.messages.next()).type, "worker.complete");

    send(worker, invocation("c1", "unary", "wait"));
    send(worker, { type: "host.cancel", id: "c1" });
    const cancelled = await worker.messages.next();
    assert.equal(cancelled.type, "worker.error");
    assert.equal(object(cancelled.error).kind, "cancelled");

    send(worker, { type: "host.shutdown", shutdownId: "shutdown1", reason: "test" });
    assert.deepEqual(await worker.messages.next(), { type: "worker.shutdown-ack", shutdownId: "shutdown1" });
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker rejects unknown envelopes and duplicate invocation ids", async () => {
  const unknown = startWorker();
  try {
    await handshake(unknown);
    send(unknown, { type: "host.unknown" });
    assert.equal(await exitCode(unknown.child), 70);
  } finally {
    unknown.child.kill("SIGKILL");
  }

  const duplicate = startWorker();
  try {
    await handshake(duplicate);
    send(duplicate, invocation("same", "unary", "wait"));
    send(duplicate, invocation("same", "unary", "echo"));
    assert.equal(await exitCode(duplicate.child), 70);
  } finally {
    duplicate.child.kill("SIGKILL");
  }
});

test("RpcClient mock Host round trip and unsolicited or post-terminal responses fail closed", async () => {
  const unary = startWorker("outbound-unary");
  try {
    await handshake(unary);
    const request = await unary.messages.next();
    assert.equal(request.type, "worker.discover");
    if (typeof request.id !== "string") throw new Error("worker.discover id is missing");
    const requestId = request.id;
    send(unary, { type: "host.result", id: requestId, value: { revision: 1, sequence: 1, providers: [], resetRequired: false } });
    await waitForStderr(unary, "OUTBOUND_OK");
    send(unary, { type: "host.result", id: requestId, value: null });
    assert.equal(await exitCode(unary.child), 70);
  } finally {
    unary.child.kill("SIGKILL");
  }

  const stream = startWorker("outbound-stream");
  try {
    await handshake(stream);
    const request = await stream.messages.next();
    assert.equal(request.type, "worker.watch");
    if (typeof request.id !== "string") throw new Error("worker.watch id is missing");
    const requestId = request.id;
    send(stream, { type: "host.complete", id: requestId });
    send(stream, { type: "host.event", id: requestId, value: { revision: 1, sequence: 1, kind: "reset-required", provider: null } });
    assert.equal(await exitCode(stream.child), 70);
  } finally {
    stream.child.kill("SIGKILL");
  }
});

test("worker emits seven-digit outbound deadlines and awaits shutdown lifecycle", async () => {
  const deadline = startWorker("outbound-deadline");
  try {
    await handshake(deadline);
    const request = await deadline.messages.next();
    assert.equal(request.type, "worker.invoke");
    assert.equal(request.deadlineUtc, "2030-01-02T03:04:05.6780000Z");
    send(deadline, { type: "host.result", id: request.id!, value: null });
    await waitForStderr(deadline, "OUTBOUND_DEADLINE_OK");
    send(deadline, { type: "host.shutdown", shutdownId: "deadline-stop", reason: "test" });
    assert.equal((await deadline.messages.next()).type, "worker.shutdown-ack");
  } finally {
    deadline.child.kill("SIGKILL");
  }

  const lifecycle = startWorker("shutdown-lifecycle");
  try {
    await handshake(lifecycle);
    send(lifecycle, { type: "host.shutdown", shutdownId: "lifecycle-stop", reason: "release" });
    assert.equal((await lifecycle.messages.next()).type, "worker.shutdown-ack");
    assert.match(lifecycle.stderr(), /SHUTDOWN_DONE:release/u);
    assert.equal(await exitCode(lifecycle.child), 0);
  } finally {
    lifecycle.child.kill("SIGKILL");
  }
});

test("Host is sole deadline authority for inbound and invocation client calls", async () => {
  const inbound = startWorker();
  try {
    await handshake(inbound);
    const expired = object(invocation("expired-inbound", "unary", "wait"));
    const context = object(expired.context);
    send(inbound, {
      ...expired,
      context: { ...context, deadlineUtc: "2020-01-02T03:04:05.6780000Z" },
    });
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 50));
    assert.equal(inbound.messages.count, 0, "worker must wait for authenticated Host cancellation");
    send(inbound, { type: "host.cancel", id: "expired-inbound" });
    const cancelled = await inbound.messages.next();
    assert.equal(cancelled.type, "worker.error");
    assert.equal(object(cancelled.error).kind, "cancelled");
    send(inbound, { type: "host.shutdown", shutdownId: "deadline-inbound-stop", reason: "test" });
    assert.equal((await inbound.messages.next()).type, "worker.shutdown-ack");
  } finally {
    inbound.child.kill("SIGKILL");
  }

  const outbound = startWorker("outbound-expired-deadline");
  try {
    await handshake(outbound);
    const request = await outbound.messages.next();
    assert.equal(request.type, "worker.invoke");
    assert.equal(request.deadlineUtc, "2020-01-02T03:04:05.6780000Z");
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 50));
    assert.equal(outbound.messages.count, 0, "worker must not race the Host with worker.cancel");
    send(outbound, {
      type: "host.error",
      id: request.id!,
      error: {
        kind: "deadline-exceeded",
        code: "rpc.call.deadline-exceeded",
        message: "The RPC call deadline elapsed.",
      },
    });
    await waitForStderr(outbound, "OUTBOUND_HOST_DEADLINE_OK");
    send(outbound, { type: "host.shutdown", shutdownId: "deadline-outbound-stop", reason: "test" });
    assert.equal((await outbound.messages.next()).type, "worker.shutdown-ack");
  } finally {
    outbound.child.kill("SIGKILL");
  }
});

test("worker rejects missing required values and bounded stream queue overflow", async () => {
  const missing = startWorker();
  try {
    await handshake(missing);
    const { request: _request, ...missingRequest } = object(invocation("missing", "unary", "echo"));
    send(missing, missingRequest);
    assert.equal(await exitCode(missing.child), 70);
  } finally {
    missing.child.kill("SIGKILL");
  }

  const overflow = startWorker("outbound-stream-overflow");
  try {
    await handshake(overflow);
    const request = await overflow.messages.next();
    assert.equal(request.type, "worker.watch");
    if (typeof request.id !== "string") throw new Error("worker.watch id is missing");
    await waitForStderr(overflow, "OUTBOUND_STARTED");
    send(overflow, { type: "host.event", id: request.id, value: { sequence: 1 } });
    send(overflow, { type: "host.event", id: request.id, value: { sequence: 2 } });
    send(overflow, { type: "host.event", id: request.id, value: { sequence: 3 } });
    assert.equal(await exitCode(overflow.child), 70);
  } finally {
    overflow.child.kill("SIGKILL");
  }
});

test("worker applies separate Host-matched inbound and outbound concurrency limits", async () => {
  const inbound = startWorker("inbound-limit");
  try {
    await handshake(inbound);
    send(inbound, invocation("inbound1", "unary", "wait"));
    send(inbound, invocation("inbound2", "unary", "wait"));
    assert.equal(await exitCode(inbound.child), 70);
  } finally {
    inbound.child.kill("SIGKILL");
  }

  const outbound = startWorker("outbound-limit");
  try {
    await handshake(outbound);
    assert.equal((await outbound.messages.next()).type, "worker.watch");
    assert.equal(await exitCode(outbound.child), 70);
  } finally {
    outbound.child.kill("SIGKILL");
  }
});

test("provider content client binds register, open, and discard to its Host invocation", async () => {
  const root = await mkdtemp(join(tmpdir(), "sunder-worker-content-"));
  const providerPath = join(root, "provider.txt");
  const callerPath = join(root, "caller.txt");
  await writeFile(providerPath, "provider payload", "utf8");
  await writeFile(callerPath, "caller payload", "utf8");
  const worker = startWorker();
  const callerReference = contentReference("caller-content", 14);
  const providerReference = contentReference("provider-content", 16);
  try {
    await handshake(worker);
    const request = object(invocation("content1", "unary", "content"));
    send(worker, {
      ...request,
      request: { registerPath: providerPath, reference: callerReference },
    });

    const register = await worker.messages.next();
    assert.equal(register.type, "worker.content-register");
    assert.equal(register.invocationId, "content1");
    assert.equal(register.filePath, providerPath);
    assert.equal(object(register.options).expiresAtUtc, "2030-01-02T03:04:05.6780000Z");
    send(worker, { type: "host.result", id: register.id!, value: providerReference });

    const open = await worker.messages.next();
    assert.equal(open.type, "worker.content-open");
    assert.equal(open.invocationId, "content1");
    assert.deepEqual(open.reference, callerReference);
    send(worker, {
      type: "host.result",
      id: open.id!,
      value: { handleId: "opened-content", filePath: callerPath },
    });

    const discard = await worker.messages.next();
    assert.equal(discard.type, "worker.content-discard");
    assert.equal(discard.invocationId, "content1");
    assert.equal(discard.handleId, "opened-content");
    send(worker, { type: "host.result", id: discard.id!, value: null });

    const result = await worker.messages.next();
    assert.equal(result.type, "worker.result");
    assert.deepEqual(object(result.value).registered, providerReference);
    assert.equal(object(result.value).openedValue, "caller payload");

    send(worker, { type: "host.shutdown", shutdownId: "shutdown-content", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
    await rm(root, { recursive: true, force: true });
  }
});

function startWorker(mode?: string): WorkerProcess {
  const fixture = resolve(__dirname, "worker-fixture.js");
  const child = spawn(process.execPath, [fixture], {
    stdio: ["pipe", "pipe", "pipe"],
    env: {
      SUNDER_WORKER_PROTOCOL: "sunder.worker.v1",
      SUNDER_PACKAGE_ID: packageIdentity.packageId,
      SUNDER_PACKAGE_VERSION: packageIdentity.packageVersion,
      SUNDER_ACTIVATION_ID: packageIdentity.activationId,
      SUNDER_SESSION_ID: packageIdentity.sessionId,
      SUNDER_TEST_MODE: mode,
    },
  });
  const messages = new MessageQueue();
  child.stdout.on("data", (chunk: Buffer) => messages.pushChunk(chunk));
  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });
  return { child, messages, stderr: () => stderr };
}

async function handshake(worker: WorkerProcess): Promise<void> {
  send(worker, {
    type: "host.hello",
    protocol: "sunder.worker.v1",
    protocolVersion: 1,
    challenge: "challenge",
    ...packageIdentity,
    providers: [provider],
  });
  const ready = await worker.messages.next();
  assert.equal(ready.type, "worker.ready");
  assert.equal(ready.challenge, "challenge");
  send(worker, { type: "host.activate", sessionGeneration: 1 });
  assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
}

function invocation(id: string, kind: "unary" | "server-stream", methodId: string): JsonValue {
  return {
    type: "host.invoke",
    id,
    kind,
    providerId: provider.providerId,
    serviceId: "messages",
    methodId,
    request: { message: "hello" },
    context: {
      callerPackageId: "caller.package",
      callerPackageVersion: "1.0.0",
      deadlineUtc: formatUtcTimestamp(new Date(Date.now() + 30_000)),
      callDepth: 1,
      provider: {
        packageId: packageIdentity.packageId,
        packageVersion: packageIdentity.packageVersion,
        ...provider,
        activationId: packageIdentity.activationId,
        activationEpoch: 1,
        sessionGeneration: 1,
        endpointReference: "rpc1_endpoint",
        catalogRevision: 1,
        state: "active",
        faultCode: null,
      },
    },
  };
}

function contentReference(id: string, length: number): JsonValue {
  return {
    id,
    length,
    sha256: "a".repeat(64),
    mediaType: "text/plain",
    fileName: `${id}.txt`,
    expiresAtUtc: formatUtcTimestamp(new Date(Date.now() + 30_000)),
    repeatability: "single-use",
  };
}

function send(worker: WorkerProcess, message: JsonValue): void {
  worker.child.stdin.write(encodeFrame(message));
}

async function exitCode(child: ChildProcessWithoutNullStreams): Promise<number | null> {
  if (child.exitCode !== null) return child.exitCode;
  const [code] = await once(child, "exit") as [number | null, NodeJS.Signals | null];
  return code;
}

async function waitForStderr(worker: WorkerProcess, value: string): Promise<void> {
  const deadline = Date.now() + 5_000;
  while (!worker.stderr().includes(value)) {
    if (Date.now() >= deadline) throw new Error(`stderr did not contain '${value}': ${worker.stderr()}`);
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

function object(value: JsonValue | undefined): Readonly<Record<string, JsonValue>> {
  assert.ok(value !== null && value !== undefined && typeof value === "object" && !Array.isArray(value));
  return value as Readonly<Record<string, JsonValue>>;
}

interface WorkerProcess {
  readonly child: ChildProcessWithoutNullStreams;
  readonly messages: MessageQueue;
  readonly stderr: () => string;
}

class MessageQueue {
  readonly #decoder = new FrameDecoder();
  readonly #values: Array<Readonly<Record<string, JsonValue>>> = [];
  readonly #waiters: Array<(value: Readonly<Record<string, JsonValue>>) => void> = [];

  public get count(): number {
    return this.#values.length;
  }

  public pushChunk(chunk: Buffer): void {
    for (const value of this.#decoder.push(chunk)) {
      const root = object(value);
      const waiter = this.#waiters.shift();
      if (waiter === undefined) this.#values.push(root);
      else waiter(root);
    }
  }

  public next(): Promise<Readonly<Record<string, JsonValue>>> {
    const value = this.#values.shift();
    return value === undefined ? new Promise((resolve) => this.#waiters.push(resolve)) : Promise.resolve(value);
  }
}
