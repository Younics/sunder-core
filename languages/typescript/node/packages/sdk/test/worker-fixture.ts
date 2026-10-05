import { Readable } from "node:stream";
import { PackageStorageKeyMigration, PackageStorageKeyMigrationAction, RpcDomainError, RpcError, runWorker, safeStderr, type JsonValue, type PackageStorageKeyMigration as StorageMigration, type RpcClient, type RpcContentReference, type RpcInvocationContext, type WorkerAuthHandler, type WorkerCallbackHandler, type WorkerContext, type WorkerMigrationPreparationContext, type WorkerOptions } from "../src/index";
import { workerV1ConformanceCorpus, type StorageMigrationRule } from "./worker-v1-conformance-corpus";

const identity = {
  providerId: "example.provider",
  contractId: "example.rpc",
  contractVersion: "1.0.0",
  descriptorSha256: "a".repeat(64),
  abiSha256: "b".repeat(64),
} as const;
const mode = process.env.SUNDER_TEST_MODE;
let releaseOverflowConsumption: (() => void) | undefined;

function assertWorkspaceUnavailable(context: WorkerContext): void {
  try {
    void context.workspace.rootPath;
  } catch {
    return;
  }
  throw new Error("The persistent workspace was available before worker activation.");
}

async function* stream(context: RpcInvocationContext, request: JsonValue): AsyncIterable<JsonValue> {
  context.signal.throwIfAborted();
  yield { sequence: 1, request };
  await Promise.resolve();
  context.signal.throwIfAborted();
  yield { sequence: 2, request };
}

async function outbound(client: RpcClient): Promise<void> {
  if (mode === "outbound-get-provider") {
    await client.getProvider("rpc1_endpoint");
    safeStderr("OUTBOUND_PROVIDER_OK");
  } else if (mode === "outbound-unary") {
    await client.discover("example.rpc");
    safeStderr("OUTBOUND_OK");
  } else if (mode === "outbound-stream") {
    for await (const _ of client.watch(0, 0)) {
      // The mock Host controls stream termination.
    }
  } else if (mode === "outbound-stream-overflow" || mode === "outbound-subscribe-count-overflow") {
    const countOverflow = mode === "outbound-subscribe-count-overflow";
    const endpointReference = "rpc1_private-endpoint";
    const events = countOverflow
      ? client.subscribe(
        endpointReference,
        "messages\r\nprivate/path",
        "watch\tprivate",
        { token: "request-secret" },
      )
      : client.watch(0, 0);
    const released = new Promise<void>((resolve) => {
      releaseOverflowConsumption = resolve;
    });
    safeStderr("OUTBOUND_STARTED");
    await released;
    try {
      for await (const _ of events) {
        // The queue must already have rejected the burst before consumption starts.
      }
      throw new Error("Stream queue exhaustion unexpectedly completed normally.");
    } catch (error) {
      if (!(error instanceof RpcError)
        || error.error.kind !== "resource-exhausted"
        || error.error.code !== "rpc.worker.stream-queue-limit"
        || !error.error.message.includes(countOverflow ? "limit=count" : "limit=bytes")
        || !error.error.message.includes(countOverflow ? "operation=subscribe" : "operation=watch")
        || countOverflow && (
          !error.error.message.includes("service=messages__private_path")
          || !error.error.message.includes("method=watch_private")
          || error.error.message.includes(endpointReference)
          || error.error.message.includes("request-secret")
          || error.error.message.includes("\r")
          || error.error.message.includes("\n")
        )) throw error;
      safeStderr("STREAM_QUEUE_EXHAUSTED");
    }
  } else if (mode === "outbound-deadline") {
    await client.invoke("rpc1_endpoint", "messages", "echo", null, {
      deadline: new Date("2030-01-02T03:04:05.678Z"),
    });
    safeStderr("OUTBOUND_DEADLINE_OK");
  } else if (mode === "outbound-expired-deadline") {
    try {
      await client.invoke("rpc1_endpoint", "messages", "echo", null, {
        deadline: new Date("2020-01-02T03:04:05.678Z"),
      });
      throw new Error("Expired Host-authoritative deadline unexpectedly succeeded.");
    } catch (error) {
      if (!(error instanceof RpcError)
        || error.error.kind !== "deadline-exceeded"
        || error.error.dispatchState !== "possibly-dispatched"
        || error.error.retrySafety !== "unsafe") throw error;
      safeStderr("OUTBOUND_HOST_DEADLINE_OK");
    }
  } else if (mode === "outbound-limit") {
    for (let index = 0; index <= 32; index++) client.watch(0, index);
  } else if (mode === "outbound-oversize") {
    try {
      await client.invoke("rpc1_endpoint", "messages", "echo", { text: "\"".repeat(600_000) });
      throw new Error("Oversized outbound RPC unexpectedly dispatched.");
    } catch (error) {
      if (!(error instanceof RpcError)
        || error.error.kind !== "resource-exhausted"
        || error.error.dispatchState !== "not-dispatched") throw error;
      safeStderr("OUTBOUND_OVERSIZE_REJECTED");
    }
  } else if (mode === "outbound-domain-details") {
    try {
      await client.invoke("rpc1_endpoint", "messages", "echo", null);
      throw new Error("Domain error details were not returned.");
    } catch (error) {
      if (!(error instanceof RpcError)
        || !error.isHostAuthenticated
        || error.error.kind !== "domain"
        || error.error.code !== "messages.rejected"
        || error.error.details === null
        || Array.isArray(error.error.details)
        || typeof error.error.details !== "object"
        || (error.error.details as Readonly<Record<string, JsonValue>>).reason !== "duplicate"
        || !Object.isFrozen(error.error.details)) throw error;
      safeStderr("OUTBOUND_DOMAIN_DETAILS_OK");
    }
  }
}

async function activationOutbound(client: RpcClient): Promise<void> {
  const catalog = await client.discover("example.rpc");
  if (catalog.revision !== 1) throw new Error("Activation discovery returned the wrong revision.");
  const result = await client.invoke("rpc1_endpoint", "messages", "echo", null);
  if (result === null
    || Array.isArray(result)
    || typeof result !== "object"
    || (result as Readonly<Record<string, JsonValue>>).accepted !== true) {
    throw new Error("Activation invocation returned an invalid result.");
  }
  safeStderr("ACTIVATION_RPC_OK");
}

async function scopeOutbound(client: RpcClient): Promise<void> {
  const scope = await client.createCallScope({ deadline: new Date("2030-01-02T03:04:05.678Z") });
  if (scope.deadline.toISOString() !== "2030-01-02T03:04:00.000Z") throw new Error("Host scope deadline was not retained.");
  await scope.getProvider("rpc1_endpoint");
  if (!await scope.reportInvariantViolation("rpc1_endpoint", new Error("line one\nline two"))) {
    throw new Error("Host rejected the invariant report.");
  }
  await scope.discover("example.rpc");
  await scope.invoke("rpc1_endpoint", "messages", "echo", null);
  await scope.close();
  await scope.close();
  safeStderr("SCOPE_RPC_OK");
}

async function scopeContent(context: WorkerContext): Promise<void> {
  const scope = await context.rpc.createCallScope();
  const registered = await scope.content.register(
    "rpc1_endpoint",
    Readable.from([Buffer.from("caller source", "utf8")]),
    { mediaType: "text/plain", fileName: "caller.txt", length: 13 },
  );
  const opened = await scope.content.open(registered);
  let value = "";
  for await (const chunk of opened.stream) value += Buffer.from(chunk).toString("utf8");
  await opened.discard();
  await scope.close();
  if (value !== "host content") throw new Error("Scoped content was not verified and streamed.");
  safeStderr("SCOPE_CONTENT_OK");
}

async function packageData(context: WorkerContext): Promise<void> {
  if (await context.settings.get("setting.one") !== "effective") throw new Error("Effective setting changed.");
  if ((await context.settings.getStored("setting.large"))?.length !== 70_000) throw new Error("Stored payload setting changed.");
  await context.settings.set("setting.large", "s".repeat(70_000));
  await context.settings.delete("setting.one");
  if (!await context.state.has("state.one")) throw new Error("State contains changed.");
  if ((await context.state.list()).join(",") !== "state.a,state.b") throw new Error("State key list changed.");
  const file = await context.files.read("cache/input.bin");
  if (file?.toString("utf8") !== "file input") throw new Error("Package file read changed.");
  await context.files.write("cache/output.bin", Buffer.from("file output", "utf8"));
  await context.files.delete("cache/input.bin");
  if (await context.secrets.get("provider.token") !== "secret") throw new Error("Secret get changed.");
  await context.secrets.set("provider.token", "updated");
  await context.secrets.delete("provider.token");
  safeStderr("PACKAGE_DATA_OK");
}

async function conformancePackageData(context: WorkerContext): Promise<void> {
  const { key, value } = workerV1ConformanceCorpus.packageData;
  await context.state.set(key, value);
  if (await context.state.get(key) !== value) throw new Error("Worker V1 conformance state value changed.");
  await context.state.delete(key);
}

function conformanceMigrations(store: "state" | "secrets"): readonly StorageMigration[] {
  const plan = workerV1ConformanceCorpus.storageMigrations.plans.find((candidate) => candidate.store === store);
  return plan?.rules.map(conformanceMigration) ?? [];
}

function conformanceMigration(rule: StorageMigrationRule): StorageMigration {
  switch (rule.kind) {
    case "exact":
      return PackageStorageKeyMigration.exact(rule.legacyKey, rule.destinationKey);
    case "opaque-id":
      return PackageStorageKeyMigration.opaqueId(
        rule.legacyPrefix,
        rule.legacySuffix,
        rule.destinationPrefix,
        rule.destinationVersion,
      );
    case "opaque-id-json":
      return PackageStorageKeyMigration.opaqueIdWithJsonIdentity(
        rule.legacyPrefix,
        rule.legacySuffix,
        rule.destinationPrefix,
        rule.destinationVersion,
        rule.identityProperty,
      );
    case "opaque-id-json-case-insensitive":
      return PackageStorageKeyMigration.opaqueIdWithCaseInsensitiveJsonIdentity(
        rule.legacyPrefix,
        rule.legacySuffix,
        rule.destinationPrefix,
        rule.destinationVersion,
        rule.identityProperty,
      );
    case "dynamic-cleanup":
      return PackageStorageKeyMigration.dynamicCleanup((key) => {
        const action = workerV1ConformanceCorpus.storageMigrations.dynamicActions.find((candidate) => candidate.key === key);
        if (action === undefined || action.kind === "no-match") return PackageStorageKeyMigrationAction.noMatch;
        if (action.kind === "delete") return PackageStorageKeyMigrationAction.delete;
        return PackageStorageKeyMigrationAction.rewrite(action.destinationKey!, action.precedence);
      });
  }
}

async function logging(context: WorkerContext): Promise<void> {
  context.logging.createLogger("Example.Worker").information("detached", {
    eventId: 7,
    eventName: "detached.event",
    attributes: { configured: true, count: 2 },
  });
  await context.logging.events.write({
    level: "warning",
    eventName: "worker.event",
    message: "awaited",
    attributes: { item: "value" },
    error: new Error("event failure", { cause: new TypeError("inner") }),
  });
  safeStderr("LOGGING_OK");
}

const callbackHandler: WorkerCallbackHandler = {
  callbackHandlerId: "example.callback",
  async startCallback(context, signal) {
    signal.throwIfAborted();
    if (context.purpose === "wait") {
      safeStderr("CONTRIBUTION_WAITING");
      await new Promise<void>((_resolvePromise, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      });
    }
    if (context.parameters.input !== "value") throw new Error("Callback parameters changed.");
    return { flow: "browser", launchUrl: "https://example.test/callback", message: "continue" };
  },
  completeCallback(context, signal) {
    signal.throwIfAborted();
    if (context.queryValues.code !== "ok" || context.queryValues.optional !== null) {
      throw new Error("Callback query values changed.");
    }
    return { state: "completed", message: "complete" };
  },
  cancelCallback(context, signal) {
    signal.throwIfAborted();
    safeStderr(`CALLBACK_CANCELLED:${context.callbackSessionId}:${context.reason}`);
  },
};

const authHandler: WorkerAuthHandler = {
  getStatus(signal) {
    signal.throwIfAborted();
    return { status: "connected", message: "ready", canAuthorize: false, canDisconnect: true };
  },
  startAuthorization(context, signal) {
    signal.throwIfAborted();
    if (!context.callbackUri.startsWith("https://")) throw new Error("Auth callback URI changed.");
    return { flow: "browser", launchUrl: "https://example.test/auth", message: "authorize" };
  },
  completeAuthorization(context, signal) {
    signal.throwIfAborted();
    if (context.queryValues.code !== "auth-code") throw new Error("Auth query values changed.");
    return { status: "connected", message: "authorized", canAuthorize: false, canDisconnect: true };
  },
  disconnect(signal) {
    signal.throwIfAborted();
    return { status: "not-connected", message: "disconnected", canAuthorize: true, canDisconnect: false };
  },
  cancelCallback(context, signal) {
    signal.throwIfAborted();
    safeStderr(`AUTH_CANCELLED:${context.callbackSessionId}:${context.reason}`);
  },
};

void runWorker((workerContext) => {
  if (mode === "context") assertWorkspaceUnavailable(workerContext);
  const options: WorkerOptions = {
  providers: [{
    ...identity,
    declaredErrors: {
      messages: {
        content: [],
        domain: workerV1ConformanceCorpus.rpc.declaredErrors.domain ?? [],
        "domain-details": ["messages.rejected"],
        echo: [],
        fault: [],
        infrastructure: [],
        oversized: [],
        "release-overflow": [],
        "undeclared-domain": workerV1ConformanceCorpus.rpc.declaredErrors["undeclared-domain"] ?? [],
        wait: [],
        watch: [],
      },
    },
    handler: {
      async invokeUnary(context, _serviceId, methodId, request): Promise<JsonValue> {
        if (mode === "nested-lineage" && methodId === "echo") {
          return await workerContext.rpc.invoke(
            "rpc1_endpoint",
            "messages",
            "echo",
            request,
          );
        }
        if (mode === "nested-lineage-scope-subscribe" && methodId === "echo") {
          const scope = await workerContext.rpc.createCallScope();
          for await (const _ of scope.subscribe(
            "rpc1_endpoint",
            "messages",
            "watch",
            request,
          )) {
            // The test Host completes without events.
          }
          await scope.close();
          return { accepted: true };
        }
        if (methodId === "release-overflow") {
          releaseOverflowConsumption?.();
          releaseOverflowConsumption = undefined;
          return { accepted: true };
        }
        if (methodId === "wait") {
          await new Promise<void>((resolve, reject) => {
            context.signal.addEventListener("abort", () => reject(context.signal.reason), { once: true });
          });
        }
        if (methodId === "domain") {
          throw new RpcDomainError(
            workerV1ConformanceCorpus.rpc.domainFault.error.code,
            workerV1ConformanceCorpus.rpc.domainFault.error.message,
            workerV1ConformanceCorpus.rpc.domainFault.error.details,
          );
        }
        if (methodId === "domain-details") {
          throw new RpcDomainError("messages.rejected", "Rejected.", { reason: "duplicate" });
        }
        if (methodId === "undeclared-domain") {
          throw new RpcDomainError(
            workerV1ConformanceCorpus.rpc.undeclaredDomainFault.thrownError.code,
            workerV1ConformanceCorpus.rpc.undeclaredDomainFault.thrownError.message,
          );
        }
        if (methodId === "infrastructure") {
          throw new RpcError({ kind: "validation", code: "rpc.validation", message: "must be hidden" });
        }
        if (methodId === "fault") throw new TypeError("must be hidden");
        if (methodId === "oversized") return { text: "\"".repeat(600_000) };
        if (methodId === "content") {
          if (request === null || Array.isArray(request) || typeof request !== "object") {
            throw new TypeError("Content test request must be an object.");
          }
          const requestObject = request as Readonly<Record<string, JsonValue>>;
          const registerPath = requestObject.registerPath;
          const reference = requestObject.reference;
          if (typeof registerPath !== "string" || reference === null || Array.isArray(reference) || typeof reference !== "object") {
            throw new TypeError("Content test request is invalid.");
          }
          const registered = await context.content.registerFile(registerPath, {
            mediaType: "text/plain",
            fileName: "provider.txt",
            length: 16,
            expiresAt: new Date("2030-01-02T03:04:05.678Z"),
          });
          const opened = await context.content.open(reference as unknown as RpcContentReference);
          let openedValue = "";
          for await (const chunk of opened.stream) openedValue += Buffer.from(chunk).toString("utf8");
          await opened.discard();
          return { registered: { ...registered }, openedValue };
        }
        return { accepted: true, request };
      },
      invokeServerStream: (context, _serviceId, _methodId, request) => stream(context, request),
    },
  }],
  ...(mode === "conformance" ? {
    stateMigrations: conformanceMigrations("state"),
    secretMigrations: conformanceMigrations("secrets"),
  } : {}),
  async onActivated(context, signal) {
    signal.throwIfAborted();
    if (mode === "activation-await-rpc") {
      await activationOutbound(context.rpc);
      return;
    }
    if (mode === "scope-roundtrip") {
      await scopeOutbound(context.rpc);
      return;
    }
    if (mode === "scope-content") {
      await scopeContent(context);
      return;
    }
    if (mode === "package-data") {
      await packageData(context);
      return;
    }
    if (mode === "conformance") {
      await conformancePackageData(context);
      return;
    }
    if (mode === "logging") {
      await logging(context);
      return;
    }
    if (mode === "context") {
      if (context !== workerContext
        || context.packageId !== "example.package"
        || context.packageVersion !== "1.0.0"
        || context.storage.state !== context.state
        || context.storage.files !== context.files
        || context.storage.workspace !== context.workspace
        || !Object.isFrozen(context)
        || !Object.isFrozen(context.storage)) {
        throw new Error("Worker context identity or immutable composition changed.");
      }
      safeStderr(`CONTEXT_OK:${context.workspace.resolve("db/items.sqlite")}`);
      return;
    }
    if (mode === "generation-fault") {
      setImmediate(() => {
        void (async () => {
          await context.reportGenerationFault("example.generation-fault", "generation failed\nwithout secrets");
          try {
            await context.reportGenerationFault("example.second", "must fail");
            throw new Error("Duplicate generation fault unexpectedly succeeded.");
          } catch {
            safeStderr("GENERATION_FAULT_OK");
          }
        })().catch((error: unknown) => {
          safeStderr("GENERATION_FAULT_FAILED", error);
          process.exit(70);
        });
      });
      return;
    }
    setImmediate(() => {
      void outbound(context.rpc).catch((error: unknown) => {
        if (error instanceof Error && error.name === "AbortError") {
          safeStderr("OUTBOUND_CANCELLED");
          return;
        }
        safeStderr("OUTBOUND_FAILED", error);
        process.exit(70);
      });
    });
  },
  ...(mode === "commit-retry" ? {
    async onGenerationCommitted(_generation: unknown, _context: unknown, signal: AbortSignal) {
      signal.throwIfAborted();
      safeStderr("COMMIT_STARTED");
      await new Promise((resolve) => setTimeout(resolve, 100));
    },
  } : {}),
  ...(mode === "candidate-abort" ? {
    async onCandidateStarted(_context: WorkerContext, signal: AbortSignal) {
      safeStderr("CANDIDATE_STARTED");
      await new Promise<void>((_resolvePromise, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      });
    },
  } : {}),
  ...(mode === "migration-preparation" ? {
    async onBeforeStorageMigrations(preparation: WorkerMigrationPreparationContext, signal: AbortSignal) {
      signal.throwIfAborted();
      if (preparation.activationId !== "a".repeat(32)
        || preparation.sessionGeneration !== 1
        || !Object.isFrozen(preparation)
        || !Object.isFrozen(preparation.state)) {
        throw new Error("Storage-migration preparation context is invalid.");
      }
      let mutationRejected = false;
      try {
        await workerContext.state.set("legacy.value", "forbidden", { signal });
      } catch (error) {
        mutationRejected = error instanceof RpcError && error.error.code === "rpc.worker.not-active";
      }
      if (!mutationRejected) throw new Error("The normal mutable state surface was admitted during migration preparation.");
      const value = await preparation.state.get("legacy.value", { signal });
      const present = await preparation.state.has("legacy.value", { signal });
      const keys = await preparation.state.list("legacy", { signal });
      if (value !== "stored" || !present || keys.length !== 1 || keys[0] !== "legacy.value") {
        throw new Error("Storage-migration preparation state snapshot was invalid.");
      }
      safeStderr("MIGRATION_PREPARATION_OK");
    },
  } : {}),
  settingsSchema: mode === "settings-schema" ? {
    summary: "Worker settings.",
    sections: [{
      sectionId: "general",
      title: "General",
      fields: [{
        key: "enabled",
        label: "Enabled",
        kind: "boolean",
        defaultValue: "true",
      }, {
        key: "mode",
        label: "Mode",
        kind: "select",
        options: [{ value: "fast", label: "Fast" }],
      }],
    }],
  } : null,
  callbackHandlers: mode === "contributions" || mode === "contribution-cancel" || mode === "composition-snapshot"
    ? [callbackHandler]
    : [],
  authHandler: mode === "contributions" ? authHandler : null,
  onShutdown: async ({ reason }, context, signal) => {
    signal.throwIfAborted();
    if (process.env.SUNDER_TEST_MODE !== "shutdown-lifecycle") return;
    if (context !== workerContext) throw new Error("Worker lifecycle context changed.");
    await new Promise((resolve) => setTimeout(resolve, 25));
    safeStderr(`SHUTDOWN_DONE:${reason}`);
  },
  };
  if (mode === "composition-snapshot") {
    queueMicrotask(() => {
      (callbackHandler as unknown as { startCallback: WorkerCallbackHandler["startCallback"] }).startCallback = () => {
        throw new Error("Mutable callback handler escaped the composition snapshot.");
      };
    });
  }
  return options;
}).catch(() => {
  process.exitCode = 70;
});
