import assert from "node:assert/strict";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash } from "node:crypto";
import { once } from "node:events";
import { mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { mkdir, readFile, realpath, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { FrameDecoder, encodeFrame } from "../src/framing";
import {
  SUNDER_WORKER_STANDARD_RESOURCE_PROFILE,
  computeWorkerResourceProfileHash,
} from "../src/resource-profile";
import { formatUtcTimestamp } from "../src/timestamp";
import type { JsonValue, WorkerResourceProfile } from "../src/types";
import { workerV3ConformanceCorpus } from "./worker-v3-conformance-corpus";

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
  descriptorSha256: "a".repeat(64),
  abiSha256: "b".repeat(64),
};
const minimumResourceProfile = {
  version: 1,
  rpcValueBytes: 1024 * 1024,
  valueDepth: 16,
  valueNodes: 16_384,
  objectMembers: 256,
  inlineDecodedBytes: 16 * 1024,
  contentReferenceBytes: 1024 * 1024,
} as const;

test("Node worker executes the shared Worker V3 conformance corpus", async () => {
  const corpus = workerV3ConformanceCorpus;
  assert.equal(corpus.formatVersion, 3);
  assert.equal(corpus.protocol.identity, "sunder.worker.v3");
  assert.equal(corpus.protocol.version, 3);
  assert.deepEqual(Object.keys(corpus.categories), [
    "shutdown-order",
    "framing",
    "resource-profile",
    "transport-bounds",
    "json",
    "safe-integers",
    "data",
    "logging",
    "migration",
    "callback",
    "auth",
    "content",
    "scope",
    "cancel",
  ]);
  assert.deepEqual(corpus.categories["shutdown-order"]?.requirements, [
    "cancel-and-drain-worker-originated-calls",
    "close-scopes-and-release-content-handles",
    "invoke-on-shutdown",
    "close-and-drain-logging-lane",
    "write-and-flush-shutdown-ack",
  ]);
  for (const category of Object.values(corpus.categories)) assert.ok(category.requirements.length > 0);
  const worker = startWorker("conformance");
  try {
    const contributions = await handshakeAndStartCandidate(worker, corpus.lifecycle.challenge);
    assert.deepEqual(contributions.storageMigrations, corpus.storageMigrations.plans);
    send(worker, {
      type: "host.commit-generation",
      activationId: packageIdentity.activationId,
      sessionGeneration: corpus.lifecycle.sessionGeneration,
    });
    assert.deepEqual(
      await worker.messages.next(),
      generationCommitted(corpus.lifecycle.sessionGeneration),
    );
    await prepareStorageMigrations(worker, corpus.lifecycle.sessionGeneration);
    send(worker, {
      type: "host.activate",
      sessionGeneration: corpus.lifecycle.sessionGeneration,
    });

    const migrationKeys = await worker.messages.next();
    assert.deepEqual({ type: migrationKeys.type, store: migrationKeys.store }, {
      type: "worker.migration-keys",
      store: "state",
    });
    send(worker, {
      type: "host.result",
      id: migrationKeys.id!,
      value: { keys: corpus.storageMigrations.dynamicKeys },
    });
    const stateMigration = await worker.messages.next();
    const statePlan = corpus.storageMigrations.plans.find((plan) => plan.store === "state");
    assert.deepEqual({
      type: stateMigration.type,
      store: stateMigration.store,
      rules: stateMigration.rules,
    }, {
      type: "worker.migration-apply",
      store: "state",
      rules: statePlan?.rules.map((rule) => rule.kind === "dynamic-cleanup"
        ? { kind: rule.kind, actions: corpus.storageMigrations.dynamicActions }
        : rule),
    });
    send(worker, { type: "host.result", id: stateMigration.id!, value: null });
    const secretMigration = await worker.messages.next();
    const secretPlan = corpus.storageMigrations.plans.find((plan) => plan.store === "secrets");
    assert.deepEqual({
      type: secretMigration.type,
      store: secretMigration.store,
      rules: secretMigration.rules,
    }, {
      type: "worker.migration-apply",
      store: "secrets",
      rules: secretPlan?.rules,
    });
    send(worker, { type: "host.result", id: secretMigration.id!, value: null });

    const stateSet = await worker.messages.next();
    assert.deepEqual({ type: stateSet.type, key: stateSet.key, value: stateSet.value }, {
      type: "worker.state-set",
      key: corpus.packageData.key,
      value: { kind: "inline", value: corpus.packageData.value },
    });
    send(worker, { type: "host.result", id: stateSet.id!, value: null });
    const stateGet = await worker.messages.next();
    assert.deepEqual({ type: stateGet.type, key: stateGet.key, mode: stateGet.mode }, {
      type: "worker.state-get",
      key: corpus.packageData.key,
      mode: "value",
    });
    send(worker, {
      type: "host.result",
      id: stateGet.id!,
      value: { kind: "inline", value: corpus.packageData.value },
    });
    const stateDelete = await worker.messages.next();
    assert.deepEqual({ type: stateDelete.type, key: stateDelete.key }, {
      type: "worker.state-delete",
      key: corpus.packageData.key,
    });
    send(worker, { type: "host.result", id: stateDelete.id!, value: null });
    assert.deepEqual(await worker.messages.next(), {
      type: "worker.activated",
      sessionGeneration: corpus.lifecycle.sessionGeneration,
    });

    send(worker, corpusInvocation(corpus.rpc.unary.id, "unary", corpus.rpc.unary.methodId, corpus.rpc.unary.request));
    const unary = await worker.messages.next();
    assert.equal(unary.type, "worker.result");
    assert.deepEqual(unary.value, corpus.rpc.unary.result);

    send(worker, corpusInvocation(corpus.rpc.stream.id, "server-stream", corpus.rpc.stream.methodId, corpus.rpc.stream.request));
    for (const expected of corpus.rpc.stream.events) {
      const event = await worker.messages.next();
      assert.equal(event.type, "worker.event");
      assert.deepEqual(event.value, expected);
    }
    assert.deepEqual(await worker.messages.next(), {
      type: "worker.complete",
      id: corpus.rpc.stream.id,
    });

    send(worker, corpusInvocation(
      corpus.rpc.domainFault.id,
      "unary",
      corpus.rpc.domainFault.methodId,
      corpus.rpc.domainFault.request,
    ));
    const domainFault = await worker.messages.next();
    assert.equal(domainFault.type, "worker.error");
    assert.deepEqual(domainFault.error, corpus.rpc.domainFault.error);

    send(worker, corpusInvocation(
      corpus.rpc.undeclaredDomainFault.id,
      "unary",
      corpus.rpc.undeclaredDomainFault.methodId,
      corpus.rpc.undeclaredDomainFault.request,
    ));
    const diagnostic = await worker.messages.next();
    assert.equal(corpus.rpc.undeclaredDomainFault.diagnosticRequired, true);
    assert.equal(diagnostic.type, "worker.provider-fault-diagnostic");
    assert.equal(diagnostic.invocationId, corpus.rpc.undeclaredDomainFault.id);
    assert.equal(diagnostic.exceptionType, "RpcDomainError");
    assert.equal(
      diagnostic.exceptionFingerprint,
      sha256(Buffer.from("RpcDomainError", "utf8")),
    );
    send(worker, { type: "host.result", id: diagnostic.id!, value: null });
    const undeclaredDomainFault = await worker.messages.next();
    assert.equal(undeclaredDomainFault.type, "worker.error");
    assert.deepEqual(undeclaredDomainFault.error, corpus.rpc.undeclaredDomainFault.error);

    send(worker, {
      type: "host.shutdown",
      shutdownId: corpus.lifecycle.shutdownId,
      reason: corpus.lifecycle.shutdownReason,
    });
    assert.deepEqual(await worker.messages.next(), {
      type: "worker.shutdown-ack",
      shutdownId: corpus.lifecycle.shutdownId,
    });
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

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

test("activation callback can await discovery and invocation before acknowledgement", async () => {
  const worker = startWorker("activation-await-rpc");
  try {
    await prepareActivation(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });

    const discover = await worker.messages.next();
    assert.equal(discover.type, "worker.discover");
    send(worker, {
      type: "host.result",
      id: discover.id!,
      value: { revision: 1, sequence: 1, providers: [], resetRequired: false },
    });
    const invoke = await worker.messages.next();
    assert.equal(invoke.type, "worker.invoke");
    send(worker, { type: "host.result", id: invoke.id!, value: { accepted: true } });

    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    await waitForStderr(worker, "ACTIVATION_RPC_OK");
    send(worker, { type: "host.shutdown", shutdownId: "activation-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("provider nested invocation carries its Host invocation lineage", async () => {
  const worker = startWorker("nested-lineage", true);
  try {
    await handshake(worker);
    send(worker, invocation("parent-lineage", "unary", "echo"));

    const nested = await worker.messages.next();
    assert.equal(nested.type, "worker.invoke");
    assert.equal(nested.parentInvocationId, "parent-lineage");
    send(worker, { type: "host.result", id: nested.id!, value: { accepted: true } });

    const result = await worker.messages.next();
    assert.equal(result.type, "worker.result");
    send(worker, { type: "host.shutdown", shutdownId: "nested-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("opted-in nested scope and subscription carry Host invocation lineage", async () => {
  const worker = startWorker("nested-lineage-scope-subscribe", true);
  try {
    await handshake(worker);
    send(worker, invocation("scope-parent-lineage", "unary", "echo"));

    const open = await worker.messages.next();
    assert.equal(open.type, "worker.scope-open");
    assert.equal(open.parentInvocationId, "scope-parent-lineage");
    send(worker, {
      type: "host.result",
      id: open.id!,
      value: { scopeId: "nested-scope", deadlineUtc: formatUtcTimestamp(new Date(Date.now() + 30_000)) },
    });

    const subscribe = await worker.messages.next();
    assert.equal(subscribe.type, "worker.subscribe");
    assert.equal(subscribe.scopeId, "nested-scope");
    assert.equal(subscribe.parentInvocationId, "scope-parent-lineage");
    send(worker, { type: "host.complete", id: subscribe.id! });

    const close = await worker.messages.next();
    assert.equal(close.type, "worker.scope-close");
    send(worker, { type: "host.result", id: close.id!, value: null });
    assert.equal((await worker.messages.next()).type, "worker.result");

    send(worker, { type: "host.shutdown", shutdownId: "nested-scope-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("storage-migration preparation is exact-generation and read-only before activation", async () => {
  const worker = startWorker("migration-preparation");
  try {
    await handshakeAndStartCandidate(worker);
    send(worker, {
      type: "host.commit-generation",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
    });
    assert.deepEqual(await worker.messages.next(), generationCommitted());
    send(worker, {
      type: "host.prepare-storage-migrations",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
    });

    const get = await worker.messages.next();
    assert.deepEqual({ type: get.type, key: get.key, mode: get.mode }, {
      type: "worker.state-get",
      key: "legacy.value",
      mode: "value",
    });
    send(worker, { type: "host.result", id: get.id!, value: { kind: "inline", value: "stored" } });
    const has = await worker.messages.next();
    assert.deepEqual({ type: has.type, key: has.key, mode: has.mode }, {
      type: "worker.state-get",
      key: "legacy.value",
      mode: "contains",
    });
    send(worker, { type: "host.result", id: has.id!, value: true });
    const list = await worker.messages.next();
    assert.deepEqual({ type: list.type, prefix: list.prefix }, {
      type: "worker.state-list",
      prefix: "legacy",
    });
    send(worker, {
      type: "host.result",
      id: list.id!,
      value: { kind: "inline", keys: ["legacy.value"] },
    });

    assert.deepEqual(await worker.messages.next(), {
      type: "worker.storage-migrations-prepared",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
    });
    await waitForStderr(worker, "MIGRATION_PREPARATION_OK");
    send(worker, { type: "host.activate", sessionGeneration: 1 });
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    send(worker, { type: "host.shutdown", shutdownId: "migration-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("exact generation commit retries are idempotent during and after acknowledgement", async () => {
  const worker = startWorker("commit-retry");
  try {
    await handshakeAndStartCandidate(worker);
    const commit = {
      type: "host.commit-generation",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
    } as const;
    send(worker, commit);
    await waitForStderr(worker, "COMMIT_STARTED");
    send(worker, commit);
    assert.deepEqual(await worker.messages.next(), generationCommitted());
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 50));
    assert.equal(worker.messages.count, 0);

    send(worker, commit);
    assert.deepEqual(await worker.messages.next(), generationCommitted());
    await prepareStorageMigrations(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    send(worker, { type: "host.shutdown", shutdownId: "commit-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
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

test("standard Worker resource profile matches the normative policy and canonical hash", () => {
  assert.deepEqual(SUNDER_WORKER_STANDARD_RESOURCE_PROFILE, {
    version: 1,
    rpcValueBytes: 8 * 1024 * 1024,
    valueDepth: 64,
    valueNodes: 524_288,
    objectMembers: 4_096,
    inlineDecodedBytes: 512 * 1024,
    contentReferenceBytes: 64 * 1024 * 1024,
  });
  assert.equal(Object.isFrozen(SUNDER_WORKER_STANDARD_RESOURCE_PROFILE), true);
  assert.equal(
    computeWorkerResourceProfileHash({
      contentReferenceBytes: SUNDER_WORKER_STANDARD_RESOURCE_PROFILE.contentReferenceBytes,
      inlineDecodedBytes: SUNDER_WORKER_STANDARD_RESOURCE_PROFILE.inlineDecodedBytes,
      objectMembers: SUNDER_WORKER_STANDARD_RESOURCE_PROFILE.objectMembers,
      valueNodes: SUNDER_WORKER_STANDARD_RESOURCE_PROFILE.valueNodes,
      valueDepth: SUNDER_WORKER_STANDARD_RESOURCE_PROFILE.valueDepth,
      rpcValueBytes: SUNDER_WORKER_STANDARD_RESOURCE_PROFILE.rpcValueBytes,
      version: 1,
    }),
    "15bde75ca257659d5742c38f678a445bc4bdfb7e21eff9b81fedc3195e0a3ec9",
  );
});

test("worker requires the exact closed effective resource profile hash", async () => {
  const worker = startWorker();
  try {
    sendHello(worker, "challenge", minimumResourceProfile, "0".repeat(64));
    assert.equal(await exitCode(worker.child), 70);
    assert.match(worker.stderr(), /resourceProfileHash/u);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("oversized outbound requests are not dispatched or retained", async () => {
  const worker = startWorker("outbound-oversize");
  try {
    await handshake(worker, minimumResourceProfile);
    await waitForStderr(worker, "OUTBOUND_OVERSIZE_REJECTED");
    assert.equal(worker.messages.count, 0);
    assert.equal(worker.child.exitCode, null);
    send(worker, { type: "host.shutdown", shutdownId: "oversize-request-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("oversized provider results return ResourceExhausted without faulting the worker", async () => {
  const worker = startWorker();
  try {
    await handshake(worker, minimumResourceProfile);
    send(worker, invocation("oversized-result", "unary", "oversized"));
    const exhausted = await worker.messages.next();
    assert.equal(exhausted.type, "worker.error");
    assert.deepEqual(exhausted.error, {
      kind: "resource-exhausted",
      code: "rpc.worker.response-limit",
      message: "The RPC response exceeds the negotiated worker transport limits.",
    });
    assert.equal(worker.child.exitCode, null);

    send(worker, invocation("valid-after-oversize", "unary", "echo"));
    assert.equal((await worker.messages.next()).type, "worker.result");
    assert.equal(worker.child.exitCode, null);
    send(worker, { type: "host.shutdown", shutdownId: "oversize-result-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker binds inbound provider snapshots to the exact active registration", async (context) => {
  const mismatches: ReadonlyArray<readonly [string, Readonly<Record<string, JsonValue>>]> = [
    ["package id", { packageId: "other.package" }],
    ["package version", { packageVersion: "2.0.0" }],
    ["activation id", { activationId: "c".repeat(32) }],
    ["generation floor", { sessionGeneration: 0 }],
    ["provider id", { providerId: "other.provider" }],
    ["contract id", { contractId: "other.rpc" }],
    ["contract version", { contractVersion: "2.0.0" }],
    ["descriptor hash", { descriptorSha256: "c".repeat(64) }],
    ["ABI hash", { abiSha256: "d".repeat(64) }],
    ["active state", { state: "inactive" }],
    ["fault state", { faultCode: "rpc.provider.faulted" }],
  ];
  for (const [label, mismatch] of mismatches) {
    await context.test(label, async () => {
      const worker = startWorker();
      try {
        await handshake(worker);
        const call = object(invocation(`mismatch-${label.replaceAll(" ", "-")}`, "unary", "echo"));
        const invocationContext = object(call.context);
        const snapshot = object(invocationContext.provider);
        send(worker, {
          ...call,
          context: { ...invocationContext, provider: { ...snapshot, ...mismatch } },
        });
        assert.equal(await exitCode(worker.child), 70);
      } finally {
        worker.child.kill("SIGKILL");
      }
    });
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

test("typed RpcClient provider and catalog results fail closed before exposure", async (context) => {
  const scenarios: ReadonlyArray<{
    readonly name: string;
    readonly mode: string;
    readonly requestType: string;
    readonly responseType: "host.result" | "host.event";
    readonly value: JsonValue;
  }> = [
    { name: "getProvider", mode: "outbound-get-provider", requestType: "worker.get-provider", responseType: "host.result", value: {} },
    { name: "discover", mode: "outbound-unary", requestType: "worker.discover", responseType: "host.result", value: null },
    { name: "watch", mode: "outbound-stream", requestType: "worker.watch", responseType: "host.event", value: {} },
  ];
  for (const scenario of scenarios) {
    await context.test(scenario.name, async () => {
      const worker = startWorker(scenario.mode);
      try {
        await handshake(worker);
        const request = await worker.messages.next();
        assert.equal(request.type, scenario.requestType);
        send(worker, { type: scenario.responseType, id: request.id!, value: scenario.value });
        assert.equal(await exitCode(worker.child), 70);
      } finally {
        worker.child.kill("SIGKILL");
      }
    });
  }
});

test("worker preserves domain error details and rejects forged infrastructure details", async () => {
  const providerWorker = startWorker();
  try {
    await handshake(providerWorker);
    send(providerWorker, invocation("domain-details-1", "unary", "domain-details"));
    const response = await providerWorker.messages.next();
    assert.equal(response.type, "worker.error");
    assert.deepEqual(response.error, {
      kind: "domain",
      code: "messages.rejected",
      message: "Rejected.",
      details: { reason: "duplicate" },
    });
    send(providerWorker, { type: "host.shutdown", shutdownId: "provider-stop", reason: "test" });
    assert.equal((await providerWorker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    providerWorker.child.kill("SIGKILL");
  }

  const callerWorker = startWorker("outbound-domain-details");
  try {
    await handshake(callerWorker);
    const request = await callerWorker.messages.next();
    assert.equal(request.type, "worker.invoke");
    send(callerWorker, {
      type: "host.error",
      id: request.id!,
      error: {
        kind: "domain",
        code: "messages.rejected",
        message: "Rejected.",
        dispatchState: "possibly-dispatched",
        retrySafety: "unsafe",
        details: { reason: "duplicate" },
      },
    });
    await waitForStderr(callerWorker, "OUTBOUND_DOMAIN_DETAILS_OK");
    send(callerWorker, { type: "host.shutdown", shutdownId: "caller-stop", reason: "test" });
    assert.equal((await callerWorker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    callerWorker.child.kill("SIGKILL");
  }

  const forgedWorker = startWorker("outbound-domain-details");
  try {
    await handshake(forgedWorker);
    const request = await forgedWorker.messages.next();
    send(forgedWorker, {
      type: "host.error",
      id: request.id!,
      error: {
        kind: "unavailable",
        code: "rpc.host.forged",
        message: "Forged.",
        dispatchState: "possibly-dispatched",
        retrySafety: "unsafe",
        details: { reason: "forged" },
      },
    });
    assert.equal(await exitCode(forgedWorker.child), 70);
  } finally {
    forgedWorker.child.kill("SIGKILL");
  }
});

test("worker emits seven-digit outbound deadlines and awaits shutdown lifecycle", async () => {
  const deadline = startWorker("outbound-deadline");
  try {
    await handshake(deadline);
    const request = await deadline.messages.next();
    assert.equal(request.type, "worker.invoke");
    assert.equal(request.deadlineUtc, "2030-01-02T03:04:05.6780000Z");
    assert.equal(Object.hasOwn(request, "parentInvocationId"), false);
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

test("shutdown cancels and drains ordinary calls before acknowledgement", async () => {
  const worker = startWorker("outbound-stream");
  try {
    await handshake(worker);
    const watch = await worker.messages.next();
    assert.equal(watch.type, "worker.watch");
    send(worker, { type: "host.shutdown", shutdownId: "drain-stop", reason: "test" });
    const cancel = await worker.messages.next().catch((error: unknown) => {
      throw new Error(`worker did not cancel the ordinary call: ${worker.stderr()}`, { cause: error });
    });
    assert.deepEqual(cancel, { type: "worker.cancel", id: watch.id });
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 50));
    assert.equal(worker.messages.count, 0, "shutdown acknowledged before the Host terminated the ordinary call");
    send(worker, { type: "host.complete", id: watch.id! });
    const acknowledgement = await worker.messages.next().catch((error: unknown) => {
      throw new Error(`worker did not acknowledge drained shutdown: ${worker.stderr()}`, { cause: error });
    });
    assert.deepEqual(acknowledgement, { type: "worker.shutdown-ack", shutdownId: "drain-stop" });
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("shutdown aborts a staged candidate callback without acknowledging the candidate", async () => {
  const worker = startWorker("candidate-abort");
  try {
    sendHello(worker);
    assert.equal((await worker.messages.next()).type, "worker.ready");
    send(worker, { type: "host.start-candidate" });
    await waitForStderr(worker, "CANDIDATE_STARTED");
    send(worker, { type: "host.shutdown", shutdownId: "candidate-stop", reason: "test" });
    assert.deepEqual(await worker.messages.next(), { type: "worker.shutdown-ack", shutdownId: "candidate-stop" });
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker exposes immutable context roots and emits normalized settings schema", async () => {
  const contextWorker = startWorker("context");
  try {
    await handshake(contextWorker);
    await waitForStderr(contextWorker, `CONTEXT_OK:${join(contextWorker.workspaceRoot, "db/items.sqlite")}`);
    send(contextWorker, { type: "host.shutdown", shutdownId: "context-stop", reason: "test" });
    assert.equal((await contextWorker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    contextWorker.child.kill("SIGKILL");
  }

  const schemaWorker = startWorker("settings-schema");
  try {
    sendHello(schemaWorker);
    const ready = await schemaWorker.messages.next();
    assert.deepEqual(object(ready.contributions).settingsSchema, {
      summary: "Worker settings.",
      sections: [{
        sectionId: "general",
        title: "General",
        description: null,
        fields: [{
          key: "enabled",
          label: "Enabled",
          kind: "boolean",
          description: null,
          isRequired: false,
          placeholder: null,
          defaultValue: "true",
          options: [],
        }, {
          key: "mode",
          label: "Mode",
          kind: "select",
          description: null,
          isRequired: false,
          placeholder: null,
          defaultValue: null,
          options: [{ value: "fast", label: "Fast" }],
        }],
      }],
    });
    send(schemaWorker, { type: "host.shutdown", shutdownId: "schema-stop", reason: "test" });
    assert.equal((await schemaWorker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    schemaWorker.child.kill("SIGKILL");
  }
});

test("caller-owned scope carries authority through RPC calls and closes idempotently", async () => {
  const worker = startWorker("scope-roundtrip");
  try {
    await prepareActivation(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });
    const opened = await worker.messages.next();
    assert.equal(opened.type, "worker.scope-open");
    assert.equal(opened.deadlineUtc, "2030-01-02T03:04:05.6780000Z");
    assert.equal(Object.hasOwn(opened, "parentInvocationId"), false);
    send(worker, {
      type: "host.result",
      id: opened.id!,
      value: { scopeId: "scope1", deadlineUtc: "2030-01-02T03:04:00.0000000Z" },
    });

    const getProvider = await worker.messages.next();
    assert.equal(getProvider.type, "worker.get-provider");
    assert.equal(getProvider.scopeId, "scope1");
    send(worker, { type: "host.result", id: getProvider.id!, value: null });

    const invariant = await worker.messages.next();
    assert.equal(invariant.type, "worker.report-invariant-violation");
    assert.equal(invariant.scopeId, "scope1");
    assert.equal(invariant.exceptionMessage, "line one line two");
    send(worker, { type: "host.result", id: invariant.id!, value: true });

    const discover = await worker.messages.next();
    assert.equal(discover.type, "worker.discover");
    assert.equal(discover.scopeId, "scope1");
    send(worker, {
      type: "host.result",
      id: discover.id!,
      value: { revision: 1, sequence: 1, providers: [], resetRequired: false },
    });

    const invoke = await worker.messages.next();
    assert.equal(invoke.type, "worker.invoke");
    assert.equal(invoke.scopeId, "scope1");
    assert.equal(Object.hasOwn(invoke, "parentInvocationId"), false);
    send(worker, { type: "host.result", id: invoke.id!, value: null });

    const closed = await worker.messages.next();
    assert.deepEqual({ ...closed, id: undefined }, { type: "worker.scope-close", scopeId: "scope1", id: undefined });
    send(worker, { type: "host.result", id: closed.id!, value: null });
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    await waitForStderr(worker, "SCOPE_RPC_OK");
    send(worker, { type: "host.shutdown", shutdownId: "scope-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("caller-owned content is staged, verified, streamed, released, and scoped", async () => {
  const worker = startWorker("scope-content");
  const openedPath = join(worker.temporaryRoot, "rpc-content", "scope-opened.txt");
  await mkdir(join(worker.temporaryRoot, "rpc-content"), { recursive: true });
  await writeFile(openedPath, "host content", "utf8");
  try {
    await prepareActivation(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });
    const scope = await worker.messages.next();
    assert.equal(scope.type, "worker.scope-open");
    send(worker, {
      type: "host.result",
      id: scope.id!,
      value: { scopeId: "content-scope", deadlineUtc: formatUtcTimestamp(new Date(Date.now() + 30_000)) },
    });

    const register = await worker.messages.next();
    assert.equal(register.type, "worker.content-register");
    assert.equal(register.scopeId, "content-scope");
    assert.equal(register.endpointReference, "rpc1_endpoint");
    assert.equal((await readFile(String(register.filePath), "utf8")), "caller source");
    const reference = contentReference("scope-content", "host content");
    send(worker, { type: "host.result", id: register.id!, value: reference });

    const open = await worker.messages.next();
    assert.equal(open.type, "worker.content-open");
    assert.equal(open.scopeId, "content-scope");
    assert.deepEqual(open.reference, reference);
    send(worker, { type: "host.result", id: open.id!, value: { handleId: "scope-handle", filePath: openedPath } });

    const release = await worker.messages.next();
    assert.equal(release.type, "worker.content-release");
    assert.equal(release.scopeId, "content-scope");
    assert.equal(release.handleId, "scope-handle");
    send(worker, { type: "host.result", id: release.id!, value: null });
    const close = await worker.messages.next();
    assert.equal(close.type, "worker.scope-close");
    send(worker, { type: "host.result", id: close.id!, value: null });
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    await waitForStderr(worker, "SCOPE_CONTENT_OK");
    send(worker, { type: "host.shutdown", shutdownId: "scope-content-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("package data APIs preserve inline and verified payload transport", async () => {
  const worker = startWorker("package-data");
  const payloadRoot = join(worker.temporaryRoot, "payload");
  await mkdir(payloadRoot, { recursive: true });
  try {
    await prepareActivation(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });

    const effective = await worker.messages.next();
    assert.deepEqual({ type: effective.type, key: effective.key, mode: effective.mode }, {
      type: "worker.settings-get", key: "setting.one", mode: "effective",
    });
    send(worker, { type: "host.result", id: effective.id!, value: { kind: "inline", value: "effective" } });

    const stored = await worker.messages.next();
    assert.equal(stored.type, "worker.settings-get");
    const storedBytes = Buffer.from("x".repeat(70_000), "utf8");
    const storedPath = join(payloadRoot, "stored-setting");
    await writeFile(storedPath, storedBytes);
    send(worker, { type: "host.result", id: stored.id!, value: payload("stored-setting", storedPath, storedBytes) });
    const storedRelease = await worker.messages.next();
    assert.equal(storedRelease.type, "worker.payload-release");
    send(worker, { type: "host.result", id: storedRelease.id!, value: null });

    const settingAllocation = await worker.messages.next();
    assert.equal(settingAllocation.type, "worker.payload-allocate");
    const settingUploadPath = join(payloadRoot, "setting-upload");
    await writeFile(settingUploadPath, Buffer.alloc(Number(settingAllocation.length)));
    send(worker, {
      type: "host.result",
      id: settingAllocation.id!,
      value: { handleId: "setting-upload", filePath: settingUploadPath, length: Number(settingAllocation.length) },
    });
    const settingSet = await worker.messages.next();
    assert.equal(settingSet.type, "worker.settings-set");
    assert.equal(object(settingSet.value).sha256, sha256(await readFile(settingUploadPath)));
    send(worker, { type: "host.result", id: settingSet.id!, value: null });

    const settingDelete = await worker.messages.next();
    assert.equal(settingDelete.type, "worker.settings-delete");
    send(worker, { type: "host.result", id: settingDelete.id!, value: null });
    const stateHas = await worker.messages.next();
    assert.deepEqual({ type: stateHas.type, mode: stateHas.mode }, { type: "worker.state-get", mode: "contains" });
    send(worker, { type: "host.result", id: stateHas.id!, value: true });
    const stateList = await worker.messages.next();
    assert.equal(stateList.type, "worker.state-list");
    send(worker, { type: "host.result", id: stateList.id!, value: { kind: "inline", keys: ["state.a", "state.b"] } });

    const fileRead = await worker.messages.next();
    assert.equal(fileRead.type, "worker.files-read");
    const fileBytes = Buffer.from("file input", "utf8");
    const fileReadPath = join(payloadRoot, "file-read");
    await writeFile(fileReadPath, fileBytes);
    send(worker, { type: "host.result", id: fileRead.id!, value: payload("file-read", fileReadPath, fileBytes) });
    const fileRelease = await worker.messages.next();
    assert.equal(fileRelease.type, "worker.payload-release");
    send(worker, { type: "host.result", id: fileRelease.id!, value: null });

    const fileAllocation = await worker.messages.next();
    assert.equal(fileAllocation.type, "worker.payload-allocate");
    const fileUploadPath = join(payloadRoot, "file-upload");
    await writeFile(fileUploadPath, Buffer.alloc(Number(fileAllocation.length)));
    send(worker, {
      type: "host.result",
      id: fileAllocation.id!,
      value: { handleId: "file-upload", filePath: fileUploadPath, length: Number(fileAllocation.length) },
    });
    const fileWrite = await worker.messages.next();
    assert.equal(fileWrite.type, "worker.files-write");
    assert.equal((await readFile(fileUploadPath, "utf8")), "file output");
    assert.equal(object(fileWrite.contents).sha256, sha256(await readFile(fileUploadPath)));
    send(worker, { type: "host.result", id: fileWrite.id!, value: null });

    for (const expected of ["worker.files-delete", "worker.secrets-get", "worker.secrets-set", "worker.secrets-delete"]) {
      const request = await worker.messages.next();
      assert.equal(request.type, expected);
      const value = expected === "worker.secrets-get" ? { kind: "inline", value: "secret" } : null;
      send(worker, { type: "host.result", id: request.id!, value });
    }
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    await waitForStderr(worker, "PACKAGE_DATA_OK");
    send(worker, { type: "host.shutdown", shutdownId: "data-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker sends structured logger and event records over the isolated logging lane", async () => {
  const worker = startWorker("logging");
  try {
    await prepareActivation(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });
    const first = await worker.messages.next();
    const second = await worker.messages.next();
    const entries = new Map([first, second].map((entry) => [entry.channel, entry]));
    const logger = entries.get("logger");
    const event = entries.get("event");
    assert.equal(logger?.type, "worker.logging-write");
    assert.equal(logger?.category, "Example.Worker");
    assert.equal(logger?.eventId, 7);
    assert.deepEqual(logger?.attributes, { configured: true, count: 2 });
    assert.equal(event?.type, "worker.logging-write");
    assert.equal(event?.eventName, "worker.event");
    assert.equal(event?.category, null);
    assert.equal(Array.isArray(event?.exceptions), true);
    send(worker, { type: "host.result", id: first.id!, value: null });
    send(worker, { type: "host.result", id: second.id!, value: null });
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
    await waitForStderr(worker, "LOGGING_OK");
    send(worker, { type: "host.shutdown", shutdownId: "logging-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker advertises and dispatches callback and auth contributions for the exact generation", async () => {
  const worker = startWorker("contributions");
  try {
    sendHello(worker);
    const ready = await worker.messages.next();
    const contributions = object(ready.contributions);
    assert.deepEqual(contributions.storageMigrations, []);
    assert.deepEqual(contributions.callbackHandlers, ["example.callback"]);
    assert.equal(contributions.authHandler, true);
    assert.equal(contributions.migrationPreparationLifecycle, true);
    send(worker, { type: "host.start-candidate" });
    assert.equal((await worker.messages.next()).type, "worker.candidate-started");
    send(worker, {
      type: "host.commit-generation",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
    });
    assert.deepEqual(await worker.messages.next(), generationCommitted());
    await prepareStorageMigrations(worker);
    send(worker, { type: "host.activate", sessionGeneration: 1 });
    assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });

    send(worker, contribution("host.callback-start", "callback-start", {
      handlerId: "example.callback",
      callbackSessionId: "callback-session",
      callbackUri: "https://runtime.test/callback",
      purpose: "connect",
      parameters: { input: "value" },
    }));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.callback-started", "callback-start", {
      packageId: packageIdentity.packageId,
      callbackSessionId: "callback-session",
      flow: "browser",
      launchUrl: "https://example.test/callback",
      message: "continue",
    }));

    send(worker, contribution("host.callback-complete", "callback-complete", {
      handlerId: "example.callback",
      callbackSessionId: "callback-session",
      queryValues: { code: "ok", optional: null },
    }));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.callback-completed", "callback-complete", {
      packageId: packageIdentity.packageId,
      callbackSessionId: "callback-session",
      state: "completed",
      message: "complete",
    }));

    send(worker, contribution("host.callback-cancel", "callback-cancel", {
      handlerId: "example.callback",
      callbackSessionId: "callback-session",
      reason: "caller-requested",
    }));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.callback-cancelled", "callback-cancel", null));
    await waitForStderr(worker, "CALLBACK_CANCELLED:callback-session:caller-requested");

    send(worker, contribution("host.auth-status", "auth-status", {}));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.auth-status", "auth-status", {
      packageId: packageIdentity.packageId,
      status: "connected",
      message: "ready",
      canAuthorize: false,
      canDisconnect: true,
    }));

    send(worker, contribution("host.auth-connect", "auth-connect", {
      authSessionId: "auth-session",
      callbackUri: "https://runtime.test/auth",
    }));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.auth-connected", "auth-connect", {
      packageId: packageIdentity.packageId,
      callbackSessionId: "auth-session",
      flow: "browser",
      launchUrl: "https://example.test/auth",
      message: "authorize",
    }));

    send(worker, contribution("host.auth-complete", "auth-complete", {
      authSessionId: "auth-session",
      queryValues: { code: "auth-code" },
    }));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.auth-completed", "auth-complete", {
      packageId: packageIdentity.packageId,
      status: "connected",
      message: "authorized",
      canAuthorize: false,
      canDisconnect: true,
    }));

    send(worker, contribution("host.auth-disconnect", "auth-disconnect", {}));
    assert.deepEqual(await worker.messages.next(), contributionResult("worker.auth-disconnected", "auth-disconnect", {
      packageId: packageIdentity.packageId,
      status: "not-connected",
      message: "disconnected",
      canAuthorize: true,
      canDisconnect: false,
    }));

    send(worker, { type: "host.shutdown", shutdownId: "contributions-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(worker.child), 0);
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker composition snapshots mutable handler callbacks before advertisement", async () => {
  const worker = startWorker("composition-snapshot");
  try {
    await handshake(worker);
    send(worker, contribution("host.callback-start", "snapshot-callback", {
      handlerId: "example.callback",
      callbackSessionId: "snapshot-session",
      callbackUri: "https://runtime.test/callback",
      purpose: "snapshot",
      parameters: { input: "value" },
    }));
    assert.equal((await worker.messages.next()).type, "worker.callback-started");
    send(worker, { type: "host.shutdown", shutdownId: "snapshot-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("Host cancellation terminates and drains an active contribution", async () => {
  const worker = startWorker("contribution-cancel");
  try {
    await handshake(worker);
    send(worker, contribution("host.callback-start", "callback-wait", {
      handlerId: "example.callback",
      callbackSessionId: "callback-wait-session",
      callbackUri: "https://runtime.test/callback",
      purpose: "wait",
      parameters: { input: "value" },
    }));
    await waitForStderr(worker, "CONTRIBUTION_WAITING");
    send(worker, {
      type: "host.contribution-cancel",
      id: "callback-wait",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
    });
    assert.deepEqual(await worker.messages.next(), {
      type: "worker.contribution-error",
      id: "callback-wait",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
      error: {
        code: "worker.contribution-cancelled",
        message: "The worker contribution call was cancelled.",
      },
    });
    send(worker, { type: "host.shutdown", shutdownId: "contribution-cancel-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("shutdown cancels an active contribution and waits for its terminal response", async () => {
  const worker = startWorker("contribution-cancel");
  try {
    await handshake(worker);
    send(worker, contribution("host.callback-start", "shutdown-contribution", {
      handlerId: "example.callback",
      callbackSessionId: "shutdown-session",
      callbackUri: "https://runtime.test/callback",
      purpose: "wait",
      parameters: { input: "value" },
    }));
    await waitForStderr(worker, "CONTRIBUTION_WAITING");
    send(worker, { type: "host.shutdown", shutdownId: "contribution-drain-stop", reason: "test" });
    const terminal = await worker.messages.next();
    assert.equal(terminal.type, "worker.contribution-error");
    assert.equal(object(terminal.error).code, "worker.contribution-cancelled");
    assert.deepEqual(await worker.messages.next(), {
      type: "worker.shutdown-ack",
      shutdownId: "contribution-drain-stop",
    });
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("worker reports one bounded fault for its exact active generation", async () => {
  const worker = startWorker("generation-fault");
  try {
    await handshake(worker);
    assert.deepEqual(await worker.messages.next(), {
      type: "worker.generation-fault",
      activationId: packageIdentity.activationId,
      sessionGeneration: 1,
      code: "example.generation-fault",
      message: "generation failed without secrets",
    });
    await waitForStderr(worker, "GENERATION_FAULT_OK");
    send(worker, { type: "host.shutdown", shutdownId: "generation-fault-stop", reason: "test" });
    assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
  } finally {
    worker.child.kill("SIGKILL");
  }
});

test("only declared domain errors cross the provider boundary", async (context) => {
  const declared = startWorker();
  try {
    await handshake(declared);
    send(declared, invocation("declared", "unary", "domain"));
    assert.deepEqual(await declared.messages.next(), {
      type: "worker.error",
      id: "declared",
      error: workerV3ConformanceCorpus.rpc.domainFault.error,
    });
    send(declared, { type: "host.shutdown", shutdownId: "declared-stop", reason: "test" });
    assert.equal((await declared.messages.next()).type, "worker.shutdown-ack");
  } finally {
    declared.child.kill("SIGKILL");
  }

  for (const [methodId, exceptionType] of [
    ["undeclared-domain", "RpcDomainError"],
    ["infrastructure", "RpcError"],
    ["fault", "TypeError"],
  ] as const) {
    await context.test(methodId, async () => {
      const worker = startWorker();
      try {
        await handshake(worker);
        send(worker, invocation(`fault-${methodId}`, "unary", methodId));
        const diagnostic = await worker.messages.next();
        assert.equal(diagnostic.type, "worker.provider-fault-diagnostic");
        assert.equal(diagnostic.invocationId, `fault-${methodId}`);
        assert.equal(diagnostic.exceptionType, exceptionType);
        assert.equal(diagnostic.exceptionFingerprint, sha256(Buffer.from(exceptionType, "utf8")));
        send(worker, { type: "host.result", id: diagnostic.id!, value: null });
        assert.deepEqual(await worker.messages.next(), {
          type: "worker.error",
          id: `fault-${methodId}`,
          error: {
            kind: "provider-fault",
            code: "rpc.provider.handler-fault",
            message: "The process provider handler failed.",
          },
        });
        send(worker, { type: "host.shutdown", shutdownId: `stop-${methodId}`, reason: "test" });
        assert.equal((await worker.messages.next()).type, "worker.shutdown-ack");
      } finally {
        worker.child.kill("SIGKILL");
      }
    });
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
         dispatchState: "possibly-dispatched",
         retrySafety: "unsafe",
       },
    });
    await waitForStderr(outbound, "OUTBOUND_HOST_DEADLINE_OK");
    send(outbound, { type: "host.shutdown", shutdownId: "deadline-outbound-stop", reason: "test" });
    assert.equal((await outbound.messages.next()).type, "worker.shutdown-ack");
  } finally {
    outbound.child.kill("SIGKILL");
  }
});

test("worker rejects missing required values and isolates bounded stream queue exhaustion", async () => {
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
    await handshake(overflow, minimumResourceProfile);
    const request = await overflow.messages.next();
    assert.equal(request.type, "worker.watch");
    if (typeof request.id !== "string") throw new Error("worker.watch id is missing");
    await waitForStderr(overflow, "OUTBOUND_STARTED");
    const escaped = "\"".repeat(350_000);
    for (let sequence = 1; sequence <= 3; sequence++) {
      send(overflow, { type: "host.event", id: request.id, value: { sequence, escaped } });
    }
    assert.deepEqual(await overflow.messages.next(), { type: "worker.cancel", id: request.id });
    assert.equal(overflow.child.exitCode, null);
    send(overflow, invocation("release-overflow", "unary", "release-overflow"));
    assert.deepEqual(await overflow.messages.next(), {
      type: "worker.result",
      id: "release-overflow",
      value: { accepted: true },
    });
    send(overflow, {
      type: "host.error",
      id: request.id,
      error: {
        kind: "cancelled",
        code: "rpc.call.cancelled",
        message: "The RPC call was cancelled.",
        dispatchState: "possibly-dispatched",
        retrySafety: "unsafe",
      },
    });
    await waitForStderr(overflow, "STREAM_QUEUE_EXHAUSTED");
    send(overflow, { type: "host.shutdown", shutdownId: "overflow-stop", reason: "test" });
    assert.equal((await overflow.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(overflow.child), 0);
  } finally {
    overflow.child.kill("SIGKILL");
  }

  const countOverflow = startWorker("outbound-subscribe-count-overflow");
  try {
    await handshake(countOverflow, minimumResourceProfile);
    const request = await countOverflow.messages.next();
    assert.equal(request.type, "worker.subscribe");
    assert.equal(Object.hasOwn(request, "parentInvocationId"), false);
    assert.equal(request.endpointReference, "rpc1_private-endpoint");
    assert.equal(request.serviceId, "messages\r\nprivate/path");
    assert.equal(request.methodId, "watch\tprivate");
    if (typeof request.id !== "string") throw new Error("worker.subscribe id is missing");
    await waitForStderr(countOverflow, "OUTBOUND_STARTED");
    for (let sequence = 1; sequence <= 33; sequence++) {
      send(countOverflow, { type: "host.event", id: request.id, value: { sequence } });
    }
    assert.deepEqual(await countOverflow.messages.next(), { type: "worker.cancel", id: request.id });
    assert.equal(countOverflow.child.exitCode, null);
    send(countOverflow, invocation("release-count-overflow", "unary", "release-overflow"));
    assert.deepEqual(await countOverflow.messages.next(), {
      type: "worker.result",
      id: "release-count-overflow",
      value: { accepted: true },
    });
    send(countOverflow, {
      type: "host.error",
      id: request.id,
      error: {
        kind: "cancelled",
        code: "rpc.call.cancelled",
        message: "The RPC call was cancelled.",
        dispatchState: "possibly-dispatched",
        retrySafety: "unsafe",
      },
    });
    await waitForStderr(countOverflow, "STREAM_QUEUE_EXHAUSTED");
    send(countOverflow, { type: "host.shutdown", shutdownId: "count-overflow-stop", reason: "test" });
    assert.equal((await countOverflow.messages.next()).type, "worker.shutdown-ack");
    assert.equal(await exitCode(countOverflow.child), 0);
  } finally {
    countOverflow.child.kill("SIGKILL");
  }
});

test("worker applies separate Host-matched inbound and outbound concurrency limits", async () => {
  const inbound = startWorker("inbound-limit");
  try {
    await handshake(inbound);
    for (let index = 1; index <= 65; index++) send(inbound, invocation(`inbound${index}`, "unary", "wait"));
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

test("provider content client binds register, verified stream open, and discard to its Host invocation", async () => {
  const worker = startWorker();
  const providerPath = join(worker.contentRoot, "provider.txt");
  const callerPath = join(worker.temporaryRoot, "rpc-content", "caller.txt");
  await mkdir(join(worker.temporaryRoot, "rpc-content"), { recursive: true });
  await writeFile(providerPath, "provider payload", "utf8");
  await writeFile(callerPath, "caller payload", "utf8");
  const callerReference = contentReference("caller-content", "caller payload");
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
    assert.equal(register.filePath, await realpath(providerPath));
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
    assert.equal(discard.type, "worker.content-release");
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
  }
});

function startWorker(mode?: string, rpcExtendedDeadlineV1 = false): WorkerProcess {
  const fixture = resolve(__dirname, "worker-fixture.js");
  const root = mkdtempSync(join(tmpdir(), "sunder-worker-test-"));
  const contentRoot = join(root, "content");
  const temporaryRoot = join(root, "temporary");
  const workspaceRoot = join(root, "workspace");
  mkdirSync(contentRoot, { recursive: true });
  mkdirSync(temporaryRoot, { recursive: true });
  mkdirSync(workspaceRoot, { recursive: true });
  const child = spawn(process.execPath, [fixture], {
    stdio: ["pipe", "pipe", "pipe"],
    env: {
      SUNDER_WORKER_PROTOCOL: "sunder.worker.v3",
      SUNDER_PACKAGE_ID: packageIdentity.packageId,
      SUNDER_PACKAGE_VERSION: packageIdentity.packageVersion,
      SUNDER_ACTIVATION_ID: packageIdentity.activationId,
      SUNDER_SESSION_ID: packageIdentity.sessionId,
      SUNDER_PACKAGE_CONTENT_ROOT: contentRoot,
      SUNDER_WORKER_TEMP_ROOT: temporaryRoot,
      SUNDER_PACKAGE_WORKSPACE_ROOT: workspaceRoot,
      SUNDER_TEST_MODE: mode,
      ...(rpcExtendedDeadlineV1 ? { SUNDER_RPC_EXTENDED_DEADLINE_V1: "1" } : {}),
    },
  });
  const messages = new MessageQueue();
  child.stdout.on("data", (chunk: Buffer) => messages.pushChunk(chunk));
  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });
  child.once("exit", () => rmSync(root, { recursive: true, force: true }));
  return { child, messages, stderr: () => stderr, contentRoot, temporaryRoot, workspaceRoot };
}

async function handshake(
  worker: WorkerProcess,
  profile: WorkerResourceProfile = SUNDER_WORKER_STANDARD_RESOURCE_PROFILE,
): Promise<void> {
  await prepareActivation(worker, "challenge", 1, profile);
  send(worker, { type: "host.activate", sessionGeneration: 1 });
  assert.deepEqual(await worker.messages.next(), { type: "worker.activated", sessionGeneration: 1 });
}

async function prepareActivation(
  worker: WorkerProcess,
  challenge = "challenge",
  sessionGeneration = 1,
  profile: WorkerResourceProfile = SUNDER_WORKER_STANDARD_RESOURCE_PROFILE,
): Promise<void> {
  await handshakeAndStartCandidate(worker, challenge, profile);
  send(worker, {
    type: "host.commit-generation",
    activationId: packageIdentity.activationId,
    sessionGeneration,
  });
  assert.deepEqual(await worker.messages.next(), generationCommitted(sessionGeneration));
  await prepareStorageMigrations(worker, sessionGeneration);
}

async function prepareStorageMigrations(worker: WorkerProcess, sessionGeneration = 1): Promise<void> {
  send(worker, {
    type: "host.prepare-storage-migrations",
    activationId: packageIdentity.activationId,
    sessionGeneration,
  });
  assert.deepEqual(await worker.messages.next(), {
    type: "worker.storage-migrations-prepared",
    activationId: packageIdentity.activationId,
    sessionGeneration,
  });
}

async function handshakeAndStartCandidate(
  worker: WorkerProcess,
  challenge = "challenge",
  profile: WorkerResourceProfile = SUNDER_WORKER_STANDARD_RESOURCE_PROFILE,
): Promise<Readonly<Record<string, JsonValue>>> {
  sendHello(worker, challenge, profile);
  const ready = await worker.messages.next();
  assert.equal(ready.type, "worker.ready");
  assert.equal(ready.challenge, challenge);
  assert.equal(ready.resourceProfileHash, computeWorkerResourceProfileHash(profile));
  const contributions = object(ready.contributions);
  assert.deepEqual(contributions.rpcProviders, [provider]);
  assert.equal(contributions.migrationPreparationLifecycle, true);
  send(worker, { type: "host.start-candidate" });
  assert.deepEqual(await worker.messages.next(), { type: "worker.candidate-started" });
  return contributions;
}

function sendHello(
  worker: WorkerProcess,
  challenge = "challenge",
  profile: WorkerResourceProfile = SUNDER_WORKER_STANDARD_RESOURCE_PROFILE,
  profileHash = computeWorkerResourceProfileHash(profile),
): void {
  send(worker, {
    type: "host.hello",
    protocol: "sunder.worker.v3",
    protocolVersion: 3,
    challenge,
    ...packageIdentity,
    providers: [provider],
    resourceProfile: profile,
    resourceProfileHash: profileHash,
  });
}

function generationCommitted(sessionGeneration = 1): Readonly<Record<string, JsonValue>> {
  return {
    type: "worker.generation-committed",
    activationId: packageIdentity.activationId,
    sessionGeneration,
  };
}

function contribution(type: string, id: string, value: JsonValue): JsonValue {
  return {
    type,
    id,
    activationId: packageIdentity.activationId,
    sessionGeneration: 1,
    value,
  };
}

function contributionResult(type: string, id: string, value: JsonValue): Readonly<Record<string, JsonValue>> {
  return {
    type,
    id,
    activationId: packageIdentity.activationId,
    sessionGeneration: 1,
    value,
  };
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

function corpusInvocation(
  id: string,
  kind: "unary" | "server-stream",
  methodId: string,
  request: JsonValue,
): JsonValue {
  return {
    ...object(invocation(id, kind, methodId)),
    request,
  };
}

function contentReference(id: string, valueOrLength: string | number): JsonValue {
  const length = typeof valueOrLength === "string" ? Buffer.byteLength(valueOrLength) : valueOrLength;
  const hash = typeof valueOrLength === "string"
    ? createHash("sha256").update(valueOrLength).digest("hex")
    : "a".repeat(64);
  return {
    id,
    length,
    sha256: hash,
    mediaType: "text/plain",
    fileName: `${id}.txt`,
    expiresAtUtc: formatUtcTimestamp(new Date(Date.now() + 30_000)),
    repeatability: "single-use",
  };
}

function payload(handleId: string, filePath: string, bytes: Buffer): JsonValue {
  return {
    kind: "payload",
    handleId,
    filePath,
    length: bytes.byteLength,
    sha256: sha256(bytes),
  };
}

function sha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
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
  readonly contentRoot: string;
  readonly temporaryRoot: string;
  readonly workspaceRoot: string;
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
    if (value !== undefined) return Promise.resolve(value);
    return new Promise((resolvePromise, reject) => {
      const receive = (item: Readonly<Record<string, JsonValue>>): void => {
        clearTimeout(timer);
        resolvePromise(item);
      };
      const timer = setTimeout(() => {
        const index = this.#waiters.indexOf(receive);
        if (index >= 0) this.#waiters.splice(index, 1);
        reject(new Error("Timed out waiting for a worker protocol frame."));
      }, 5_000);
      this.#waiters.push(receive);
    });
  }
}
