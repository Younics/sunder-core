import { createHash, randomUUID } from "node:crypto";
import { AsyncLocalStorage } from "node:async_hooks";
import { once } from "node:events";
import { lstatSync } from "node:fs";
import { mkdir, open, realpath, stat, unlink, type FileHandle } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import type { Readable } from "node:stream";
import {
  FrameDecoder,
  ProtocolError,
  TransportResourceError,
  encodeFrame,
  encodedJsonByteLength,
  hasCanonicalNonNegativeIntegerToken,
} from "./framing";
import { computeWorkerResourceProfileHash } from "./resource-profile";
import { createApiContext, getApiBindingContext } from "./api";
import { isResourceInvocationCancellation } from "./resources";
import { contentReferenceError } from "./descriptor-v1";
import { createPackageStorageKey, type PackageStorageKeyMigration, type PackageStorageKeyMigrationAction } from "./storage";
import { formatUtcTimestamp, parseUtcTimestamp } from "./timestamp";
import { SEMANTIC_VERSION_MAXIMUM_LENGTH, isPackageId, isSemanticVersion } from "./validation";
import {
  SUNDER_WORKER_LIMITS,
  SUNDER_WORKER_PROTOCOL,
  SUNDER_WORKER_PROTOCOL_VERSION,
  RpcError,
  type HostEnvelope,
  type JsonValue,
  type PackageSettingsField,
  type PackageLogAttribute,
  type PackageLogLevel,
  type PackageSettingsSchema,
  type ProviderWireIdentity,
  type RpcCallOptions,
  type RpcCallScope,
  type RpcCallerContentClient,
  type RpcCatalogEvent,
  type RpcCatalogSnapshot,
  type RpcClient,
  type RpcContentClient,
  type RpcContentFile,
  type RpcContentReference,
  type RpcContentRegistrationOptions,
  type RpcContentSource,
  type RpcErrorValue,
  type RpcInvocationContext,
  type RpcProviderRegistration,
  type RpcProviderSnapshot,
  type WorkerCallOptions,
  type WorkerAuthHandler,
  type WorkerCallbackCancellationContext,
  type WorkerCallbackHandler,
  type WorkerConfigure,
  type WorkerContext,
  type WorkerEnvelope,
  type WorkerEventLogEntry,
  type WorkerFiles,
  type WorkerLimits,
  type WorkerLocalPath,
  type WorkerLogDetails,
  type WorkerLogger,
  type WorkerLogging,
  type WorkerMigrationPreparationContext,
  type WorkerOptions,
  type WorkerResourceProfile,
  type WorkerSecrets,
  type WorkerSettings,
  type WorkerSettingsSchemaWire,
  type WorkerState,
  type WorkerStateReader,
  type WorkerStorageMigrationPlanWire,
  type WorkerStorageMigrationRuleWire,
} from "./worker-types";

const LIMITS: WorkerLimits = { ...SUNDER_WORKER_LIMITS };
const SAFE_ERROR_CODE = /^[A-Za-z0-9._-]+$/u;
const SAFE_ID = /^[A-Za-z0-9_-]+$/u;
const SHA256 = /^[0-9a-f]{64}$/u;
const SHA256_ANY_CASE = /^[0-9a-fA-F]{64}$/u;
const STORAGE_KEY = /^[A-Za-z0-9._-]{1,256}$/u;
const RPC_EXTENDED_DEADLINE_ENVIRONMENT = "SUNDER_RPC_EXTENDED_DEADLINE_V1";
const OPERATIONAL_OUTCOMES_ENVIRONMENT = "SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1";
const MAX_OUTBOUND_OBSERVATIONS = 32;
const MAX_INBOUND_OBSERVATIONS = 64;
// Public RpcError metadata is descriptive, not proof of transport provenance.
const operationalErrors = new WeakSet<RpcError>();
const WINDOWS_RESERVED = new Set([
  "CON", "PRN", "AUX", "NUL",
  "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
]);
const LOG_LEVELS: Readonly<Record<PackageLogLevel, number>> = Object.freeze({
  trace: 0,
  debug: 1,
  information: 2,
  warning: 3,
  error: 4,
  critical: 5,
});

export async function runWorker(configure: WorkerConfigure): Promise<void> {
  if (typeof configure !== "function") throw new TypeError("Worker configuration must be a function.");
  const runtime = new WorkerRuntime(configure);
  await runtime.run();
}

export function safeStderr(message: string, error?: unknown): void {
  const detail = error instanceof Error
    ? `${error.name}: ${error.message}`
    : error === undefined
      ? ""
      : String(error);
  let output = detail.length === 0 ? message : `${message}: ${detail}`;
  output = output
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/gu, " ")
    .replace(/[\r\n]+/gu, " ")
    .trim();
  if (output.length > LIMITS.maxDiagnosticCharacters) {
    output = `${truncate(output, LIMITS.maxDiagnosticCharacters - 12)} [truncated]`;
  }
  process.stderr.write(`${output}\n`);
}

class WorkerRuntime {
  readonly #configure: WorkerConfigure;
  readonly #environment: WorkerEnvironment;
  readonly #decoder = new FrameDecoder(LIMITS);
  readonly #writer = new ProtocolWriter();
  readonly #hostInvocations = new Map<string, HostInvocation>();
  readonly #currentInvocationId = new AsyncLocalStorage<string>();
  readonly #hostContributions = new Map<string, HostContribution>();
  readonly #clientCalls = new Map<string, ClientCall>();
  readonly #scopes = new Map<string, ScopeState>();
  readonly #payloadHandles = new Map<string, PayloadHandle>();
  readonly #rememberedHostIds = new RememberedIds();
  readonly #rememberedPayloadIds = new RememberedIds();
  readonly #work = new AbortController();
  readonly #lifetime = new AbortController();
  readonly #client: RpcClient;
  readonly #context: WorkerContext;
  #options: NormalizedWorkerOptions | undefined;
  #providers: ReadonlyMap<string, NormalizedProviderRegistration> = new Map();
  #phase: WorkerPhase = "waiting-hello";
  #messageTail = Promise.resolve();
  #candidateStartOperation = Promise.resolve();
  #generationCommitOperation = Promise.resolve();
  #migrationPreparationOperation = Promise.resolve();
  #activationOperation = Promise.resolve();
  #shutdownOperation = Promise.resolve();
  #generationCommitAcknowledgementPending = false;
  #generationFaultReported = false;
  #clientId = 0;
  #sessionGeneration: number | undefined;
  #loggingClosed = false;
  #openScopes = 0;
  #openContentHandles = 0;
  #openPayloadHandles = 0;
  #resolveRun: (() => void) | undefined;
  #rejectRun: ((error: unknown) => void) | undefined;

  public constructor(configure: WorkerConfigure) {
    this.#configure = configure;
    this.#environment = captureEnvironment();
    this.#client = this.#createClient();
    this.#context = this.#createContext();
  }

  public async run(): Promise<void> {
    const completion = new Promise<void>((resolvePromise, reject) => {
      this.#resolveRun = resolvePromise;
      this.#rejectRun = reject;
    });
    const onData = (chunk: Buffer): void => {
      try {
        for (const value of this.#decoder.push(chunk)) {
          this.#messageTail = this.#messageTail
            .then(() => this.#handleEnvelope(value))
            .catch((error: unknown) => this.#fail(error));
        }
      } catch (error) {
        this.#fail(error);
      }
    };
    const onEnd = (): void => {
      try {
        this.#decoder.end();
        if (this.#phase !== "stopped") {
          this.#fail(new ProtocolError("Host protocol input closed before shutdown completed."));
        }
      } catch (error) {
        this.#fail(error);
      }
    };
    process.stdin.on("data", onData);
    process.stdin.once("end", onEnd);
    process.stdin.resume();
    try {
      await completion;
    } finally {
      process.stdin.off("data", onData);
      process.stdin.off("end", onEnd);
      process.stdin.pause();
    }
  }

  async #handleEnvelope(value: JsonValue): Promise<void> {
    const root = objectValue(value, "Host envelope");
    const type = stringValue(root, "type", 64);
    if (this.#phase === "waiting-hello") {
      if (type !== "host.hello") throw new ProtocolError(`Host sent '${type}' before host.hello.`);
      await this.#handleHello(root);
      return;
    }
    switch (type) {
      case "host.start-candidate":
        this.#beginStartCandidate(root);
        break;
      case "host.commit-generation":
        this.#beginCommitGeneration(root);
        break;
      case "host.prepare-storage-migrations":
        this.#beginPrepareStorageMigrations(root);
        break;
      case "host.activate":
        this.#beginActivate(root);
        break;
      case "host.invoke":
        this.#beginHostInvocation(root);
        break;
      case "host.cancel":
        this.#cancelHostInvocation(root);
        break;
      case "host.callback-start":
      case "host.callback-complete":
      case "host.callback-cancel":
      case "host.auth-status":
      case "host.auth-connect":
      case "host.auth-complete":
      case "host.auth-disconnect":
        this.#beginHostContribution(type, root);
        break;
      case "host.contribution-cancel":
        this.#cancelHostContribution(root);
        break;
      case "host.result":
      case "host.event":
      case "host.complete":
      case "host.error":
        this.#handleClientResponse(type, root);
        break;
      case "host.shutdown":
        this.#beginShutdown(root);
        break;
      default:
        throw new ProtocolError(`Unknown Host envelope type '${type}'.`);
    }
  }

  async #handleHello(root: Readonly<Record<string, JsonValue>>): Promise<void> {
    only(
      root,
      "type",
      "protocol",
      "protocolVersion",
      "challenge",
      "packageId",
      "packageVersion",
      "activationId",
      "sessionId",
      "providers",
      "resourceProfile",
      "resourceProfileHash",
    );
    const protocol = stringValue(root, "protocol", 64);
    const version = integerValue(root, "protocolVersion");
    const challenge = stringValue(root, "challenge", 128);
    const packageId = stringValue(root, "packageId", 256);
    const packageVersion = stringValue(root, "packageVersion", SEMANTIC_VERSION_MAXIMUM_LENGTH);
    const activationId = canonicalActivationId(root, "activationId");
    const sessionId = idValue(root, "sessionId", 256);
    if (protocol !== SUNDER_WORKER_PROTOCOL || version !== SUNDER_WORKER_PROTOCOL_VERSION) {
      throw new ProtocolError("Host requested an unsupported worker protocol version.");
    }
    if (packageId !== this.#environment.packageId
      || packageVersion !== this.#environment.packageVersion
      || activationId !== this.#environment.activationId
      || sessionId !== this.#environment.sessionId
      || protocol !== this.#environment.protocol) {
      throw new ProtocolError("host.hello does not match the sanitized process activation environment.");
    }

    const profileHash = stringValue(root, "resourceProfileHash", 64);
    const profile = resourceProfile(root.resourceProfile, profileHash);
    applyResourceProfile(profile);
    this.#decoder.configure(LIMITS);

    const configured = this.#configure(this.#context);
    if (configured === null || typeof configured !== "object" || configured instanceof Promise) {
      throw new TypeError("Worker configuration must synchronously return WorkerOptions.");
    }
    const normalized = normalizeWorkerOptions(configured);
    const providers = new Map<string, NormalizedProviderRegistration>();
    for (const provider of normalized.providers) providers.set(provider.providerId, provider);
    this.#options = normalized;
    this.#providers = providers;

    const declared = providerArray(root.providers);
    const advertised = normalized.providers.map(providerWire).sort(compareProviders);
    if (JSON.stringify([...declared].sort(compareProviders)) !== JSON.stringify(advertised)) {
      throw new ProtocolError("Registered providers do not exactly match host-declared providers.");
    }
    const contributions = Object.freeze({
      settingsSchema: normalized.settingsSchema,
      storageMigrations: storageMigrationPlans(normalized.stateMigrations, normalized.secretMigrations),
      callbackHandlers: Object.freeze(normalized.callbackHandlers.map((handler) => handler.callbackHandlerId)),
      authHandler: normalized.authHandler !== null,
      rpcProviders: Object.freeze(advertised),
      candidateLifecycle: normalized.onCandidateStarted !== undefined,
      generationLifecycle: normalized.onGenerationCommitted !== undefined,
      migrationPreparationLifecycle: true,
      ...(this.#environment.operationalOutcomesV1 ? { operationalOutcomesV1: true } : {}),
    });
    await this.#writer.write({
      type: "worker.ready",
      protocol: SUNDER_WORKER_PROTOCOL,
      protocolVersion: SUNDER_WORKER_PROTOCOL_VERSION,
      challenge,
      packageId,
      packageVersion,
      activationId,
      sessionId,
      resourceProfileHash: profileHash,
      contributions,
    });
    if (this.#phase !== "waiting-hello") throw new ProtocolError("Worker state changed while completing host.hello.");
    this.#phase = "ready";
  }

  #beginStartCandidate(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type");
    if (this.#phase !== "ready") throw new ProtocolError("host.start-candidate is duplicate or out of order.");
    this.#phase = "candidate-starting";
    this.#candidateStartOperation = this.#completeCandidateStart();
  }

  async #completeCandidateStart(): Promise<void> {
    try {
      await this.#options!.onCandidateStarted?.(this.#context, this.#work.signal);
      if (this.#isStopping()) return;
      if (this.#phase !== "candidate-starting") {
        throw new ProtocolError("Worker state changed while starting the candidate.");
      }
      this.#phase = "candidate-started";
      await this.#writer.write({ type: "worker.candidate-started" });
    } catch (error) {
      if (this.#isExpectedStop(error)) return;
      this.#fail(new ProtocolError(`The worker candidate-start callback failed: ${errorMessage(error)}`));
    }
  }

  #beginCommitGeneration(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "activationId", "sessionGeneration");
    const activationId = canonicalActivationId(root, "activationId");
    const sessionGeneration = integerValue(root, "sessionGeneration");
    if (activationId !== this.#environment.activationId) {
      throw new ProtocolError("host.commit-generation does not identify this worker activation.");
    }
    if (this.#phase === "committing" && sessionGeneration === this.#sessionGeneration) return;
    if (this.#phase === "committed" && sessionGeneration === this.#sessionGeneration) {
      if (this.#generationCommitAcknowledgementPending) return;
      this.#generationCommitAcknowledgementPending = true;
      this.#generationCommitOperation = this.#writeGenerationCommitted(activationId, sessionGeneration)
        .finally(() => { this.#generationCommitAcknowledgementPending = false; });
      return;
    }
    if (this.#phase !== "candidate-started" || this.#sessionGeneration !== undefined) {
      throw new ProtocolError("host.commit-generation is duplicate, conflicting, or out of order.");
    }
    this.#sessionGeneration = sessionGeneration;
    this.#phase = "committing";
    this.#generationCommitOperation = this.#completeGenerationCommit(activationId, sessionGeneration);
  }

  async #completeGenerationCommit(activationId: string, sessionGeneration: number): Promise<void> {
    try {
      const generation = Object.freeze({ activationId, sessionGeneration });
      await this.#options!.onGenerationCommitted?.(generation, this.#context, this.#work.signal);
      if (this.#isStopping()) return;
      if (this.#phase !== "committing" || this.#sessionGeneration !== sessionGeneration) {
        throw new ProtocolError("Worker state changed while committing the Runtime generation.");
      }
      this.#phase = "committed";
      this.#generationCommitAcknowledgementPending = true;
      await this.#writeGenerationCommitted(activationId, sessionGeneration);
      this.#generationCommitAcknowledgementPending = false;
    } catch (error) {
      this.#generationCommitAcknowledgementPending = false;
      if (this.#isExpectedStop(error)) return;
      this.#fail(new ProtocolError(`The worker generation-commit callback failed: ${errorMessage(error)}`));
    }
  }

  #writeGenerationCommitted(activationId: string, sessionGeneration: number): Promise<void> {
    return this.#writer.write({
      type: "worker.generation-committed",
      activationId,
      sessionGeneration,
    });
  }

  #beginPrepareStorageMigrations(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "activationId", "sessionGeneration");
    const activationId = canonicalActivationId(root, "activationId");
    const sessionGeneration = integerValue(root, "sessionGeneration");
    if (activationId !== this.#environment.activationId
      || this.#phase !== "committed"
      || sessionGeneration !== this.#sessionGeneration) {
      throw new ProtocolError("host.prepare-storage-migrations is duplicate, conflicting, or out of order.");
    }
    this.#phase = "preparing-storage-migrations";
    this.#migrationPreparationOperation = this.#completeStorageMigrationPreparation(
      activationId,
      sessionGeneration,
    );
  }

  async #completeStorageMigrationPreparation(activationId: string, sessionGeneration: number): Promise<void> {
    try {
      const preparation: WorkerMigrationPreparationContext = Object.freeze({
        activationId,
        sessionGeneration,
        state: this.#createMigrationStateReader(),
      });
      await this.#options!.onBeforeStorageMigrations?.(preparation, this.#work.signal);
      if (this.#isStopping()) return;
      if (this.#phase !== "preparing-storage-migrations" || this.#sessionGeneration !== sessionGeneration) {
        throw new ProtocolError("Worker state changed while preparing storage migrations.");
      }
      this.#phase = "storage-migrations-prepared";
      await this.#writer.write({
        type: "worker.storage-migrations-prepared",
        activationId,
        sessionGeneration,
      });
    } catch (error) {
      if (this.#isExpectedStop(error)) return;
      this.#fail(new ProtocolError(`The worker storage-migration preparation callback failed: ${errorMessage(error)}`));
    }
  }

  #beginActivate(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "sessionGeneration", "workspaceRootPath");
    const sessionGeneration = integerValue(root, "sessionGeneration");
    const workspaceRootPath = stringValue(root, "workspaceRootPath", 4096);
    if (this.#phase !== "storage-migrations-prepared" || sessionGeneration !== this.#sessionGeneration) {
      throw new ProtocolError("host.activate is duplicate or out of order.");
    }
    this.#environment.workspace.bind(workspaceRootPath, [
      this.#environment.content.rootPath,
      this.#environment.temporary.rootPath,
    ]);
    this.#phase = "activating";
    this.#activationOperation = this.#completeActivation(sessionGeneration);
  }

  async #completeActivation(sessionGeneration: number): Promise<void> {
    try {
      await this.#applyStorageMigrations("state", this.#options!.stateMigrations, this.#work.signal);
      await this.#applyStorageMigrations("secrets", this.#options!.secretMigrations, this.#work.signal);
      await this.#options!.onActivated?.(this.#context, this.#work.signal);
      if (this.#isStopping()) return;
      if (this.#phase !== "activating" || this.#sessionGeneration !== sessionGeneration) {
        throw new ProtocolError("Worker state changed while activating the Runtime generation.");
      }
      this.#phase = "active";
      await this.#writer.write({ type: "worker.activated", sessionGeneration });
    } catch (error) {
      if (this.#isExpectedStop(error)) return;
      this.#fail(new ProtocolError(`The worker activation callback failed: ${errorMessage(error)}`));
    }
  }

  async #applyStorageMigrations(
    store: "state" | "secrets",
    migrations: readonly PackageStorageKeyMigration[],
    signal: AbortSignal,
  ): Promise<void> {
    if (migrations.length === 0) return;
    const dynamicCount = migrations.filter((migration) => migration.kind === "dynamic-cleanup").length;
    const physicalKeys = dynamicCount === 0 ? [] : await this.#listPhysicalMigrationKeys(store, signal);
    if (physicalKeys.length * dynamicCount > 4096) {
      throw new RangeError("The worker storage migration exceeds the bounded dynamic-action limit.");
    }
    const rules = migrations.map((migration) => migrationApplyRule(migration, physicalKeys));
    const result = await this.#unaryClientCall<JsonValue>({
      type: "worker.migration-apply",
      store,
      rules,
    }, { signal });
    if (result !== null) throw new ProtocolError("Host worker storage-migration result must be null.");
  }

  #listPhysicalMigrationKeys(store: "state" | "secrets", signal: AbortSignal): Promise<readonly string[]> {
    return this.#validatedUnaryClientCall({
      type: "worker.migration-keys",
      store,
    }, { signal }, migrationPhysicalKeys);
  }

  #requireFreshHostId(id: string): void {
    if (this.#hostInvocations.has(id) || this.#hostContributions.has(id) || this.#rememberedHostIds.has(id)) {
      throw new ProtocolError(`Protocol peer reused identifier '${id}'.`);
    }
  }

  #beginHostInvocation(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "id", "kind", "providerId", "serviceId", "methodId", "request", "context");
    if (this.#phase !== "active") throw new ProtocolError("Host invocation arrived before activation.");
    const id = idValue(root, "id");
    this.#requireFreshHostId(id);
    const kind = stringValue(root, "kind", 32);
    if (kind !== "unary" && kind !== "server-stream") throw new ProtocolError("Host invocation kind is invalid.");
    if (this.#inboundLimitReached(kind === "server-stream")) {
      throw new ProtocolError("Host exceeded the worker outstanding invocation limit.");
    }
    const providerId = stringValue(root, "providerId", 256);
    const provider = this.#providers.get(providerId);
    if (provider === undefined) throw new ProtocolError(`Host invoked undeclared provider '${providerId}'.`);
    const serviceId = stringValue(root, "serviceId", 128);
    const methodId = stringValue(root, "methodId", 128);
    const contextValue = objectValue(root.context, "Host invocation context");
    only(contextValue, "callerPackageId", "callerPackageVersion", "deadlineUtc", "callDepth", "provider");
    const deadlineUtc = stringValue(contextValue, "deadlineUtc", 64);
    if (!Number.isFinite(parseUtcTimestamp(deadlineUtc))) throw new ProtocolError("Host invocation deadline is invalid.");
    const callDepth = integerValue(contextValue, "callDepth");
    if (callDepth < 1 || callDepth > 0x7fffffff) throw new ProtocolError("Host invocation callDepth is invalid.");
    const snapshot = providerSnapshot(contextValue.provider);
    if (snapshot.packageId !== this.#environment.packageId
      || snapshot.packageVersion !== this.#environment.packageVersion
      || snapshot.activationId !== this.#environment.activationId
      || this.#sessionGeneration === undefined
      || snapshot.sessionGeneration < this.#sessionGeneration
      || snapshot.state !== "active"
      || snapshot.faultCode !== null
      || snapshot.providerId !== provider.providerId
      || snapshot.contractId !== provider.contractId
      || snapshot.contractVersion !== provider.contractVersion
      || snapshot.descriptorSha256 !== provider.descriptorSha256
      || snapshot.abiSha256 !== provider.abiSha256) {
      throw new ProtocolError("Host invocation context does not identify the exact registered provider activation.");
    }

    const controller = new AbortController();
    const invocation: HostInvocation = {
      id,
      deadline:parseUtcTimestamp(deadlineUtc),
      kind,
      provider,
      serviceId,
      methodId,
      controller,
      hostCancelled: false,
      active: true,
      contentFiles: new Set(),
    };
    this.#hostInvocations.set(id, invocation);
    const context: RpcInvocationContext = Object.freeze({
      callerPackageId: stringValue(contextValue, "callerPackageId", 256),
      callerPackageVersion: stringValue(contextValue, "callerPackageVersion", SEMANTIC_VERSION_MAXIMUM_LENGTH),
      deadlineUtc,
      callDepth,
      provider: snapshot,
      content: this.#createInvocationContentClient(invocation),
      signal: controller.signal,
    });
    const request = requiredValue(root, "request");
    const operation = this.#currentInvocationId.run(id, () => kind === "unary"
      ? this.#invokeUnary(invocation, context, request)
      : this.#invokeStream(invocation, context, request));
    invocation.operation = operation.finally(() => {
      this.#hostInvocations.delete(id);
      // Active lifetime must not consume the bounded terminal/cancel window.
      this.#rememberedHostIds.add(id);
      invocation.active = false;
      for (const file of invocation.contentFiles) file.revoke();
      invocation.contentFiles.clear();
    });
    void invocation.operation.catch((error: unknown) => this.#fail(error));
  }

  async #invokeUnary(invocation: HostInvocation, context: RpcInvocationContext, request: JsonValue): Promise<void> {
    try {
      const result = await invocation.provider.handler.invokeUnary(
        context,
        invocation.serviceId,
        invocation.methodId,
        request,
      );
      assertJsonValue(result);
      if (!this.#suppressInvocationResponse(invocation)) {
        await this.#writer.write({ type: "worker.result", id: invocation.id, value: result });
      }
    } catch (error) {
      await this.#writeProviderError(invocation, error);
    }
  }

  async #invokeStream(invocation: HostInvocation, context: RpcInvocationContext, request: JsonValue): Promise<void> {
    try {
      const stream = invocation.provider.handler.invokeServerStream(
        context,
        invocation.serviceId,
        invocation.methodId,
        request,
      );
      if (stream === null || typeof stream !== "object" || !(Symbol.asyncIterator in stream)) {
        throw new TypeError("Provider returned a non-AsyncIterable stream.");
      }
      for await (const event of stream) {
        if (context.signal.aborted) throw abortError(context.signal);
        assertJsonValue(event);
        if (this.#suppressInvocationResponse(invocation)) return;
        await this.#writer.write({ type: "worker.event", id: invocation.id, value: event });
      }
      if (!this.#suppressInvocationResponse(invocation)) {
        await this.#writer.write({ type: "worker.complete", id: invocation.id });
      }
    } catch (error) {
      await this.#writeProviderError(invocation, error);
    }
  }

  async #writeProviderError(invocation: HostInvocation, error: unknown): Promise<void> {
    if (this.#suppressInvocationResponse(invocation)) return;
    if (error instanceof TransportResourceError) {
      await this.#writer.write({
        type: "worker.error",
        id: invocation.id,
        error: {
          kind: "resource-exhausted",
          code: "rpc.worker.response-limit",
          message: "The RPC response exceeds the negotiated worker transport limits.",
        },
      });
      return;
    }
    if (invocation.hostCancelled || isResourceInvocationCancellation(error,invocation.controller.signal)) {
      await this.#writer.write({
        type: "worker.error",
        id: invocation.id,
        error: { kind: "cancelled", code: "rpc.call.cancelled", message: "The RPC call was cancelled." },
      });
      return;
    }

    if (this.#environment.operationalOutcomesV1 && error instanceof RpcError && operationalErrors.has(error)
      && (error.error.kind === "resource-exhausted" || error.error.kind === "unavailable" || error.error.kind === "deadline-exceeded")) {
      // Never forward a nested call's NotDispatched/Safe stamp to the enclosing
      // provider call. The Host owns its code, message and dispatch/retry metadata.
      await this.#writer.write({ type: "worker.error", id: invocation.id, error: {
        kind: "operational", code: error.error.kind, message: "The provider could not complete the RPC operation.",
      } });
      return;
    }

    if (isDeclaredDomainError(invocation.provider, invocation.serviceId, invocation.methodId, error)) {
      const message = sanitizeText(error.error.message, 512) || "The provider rejected the request.";
      await this.#writer.write({
        type: "worker.error",
        id: invocation.id,
        error: error.error.details === undefined
          ? { kind: "domain", code: error.error.code, message }
          : { kind: "domain", code: error.error.code, message, details: error.error.details },
      });
      return;
    }

    const exceptionType = providerExceptionType(error);
    try {
      const result = await this.#unaryClientCall<JsonValue>({
        type: "worker.provider-fault-diagnostic",
        invocationId: invocation.id,
        providerId: invocation.provider.providerId,
        serviceId: invocation.serviceId,
        methodId: invocation.methodId,
        exceptionType,
        exceptionFingerprint: sha256(Buffer.from(exceptionType, "utf8")),
      }, { signal: invocation.controller.signal }, {
        lane: "logging",
        awaitTerminalAfterAbort: true,
      });
      if (result !== null) throw new ProtocolError("Host returned a value for worker.provider-fault-diagnostic.");
    } catch (diagnosticError) {
      if (diagnosticError instanceof ProtocolError) throw diagnosticError;
      // Diagnostic persistence is failure-isolated; the generic provider fault remains terminal.
    }
    safeStderr("Process provider handler failed", exceptionType);
    await this.#writer.write({
      type: "worker.error",
      id: invocation.id,
      error: {
        kind: "provider-fault",
        code: "rpc.provider.handler-fault",
        message: "The process provider handler failed.",
      },
    });
  }

  #suppressInvocationResponse(invocation: HostInvocation): boolean {
    return this.#isStopping() && !invocation.hostCancelled;
  }

  #cancelHostInvocation(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "id");
    const id = idValue(root, "id");
    const invocation = this.#hostInvocations.get(id);
    if (invocation === undefined) {
      if (this.#hostContributions.has(id)) throw new ProtocolError(`Host used invocation cancellation for contribution '${id}'.`);
      if (this.#rememberedHostIds.has(id)) return;
      throw new ProtocolError(`Host sent unsolicited cancellation '${id}'.`);
    }
    if (invocation.hostCancelled) throw new ProtocolError(`Host sent duplicate cancellation '${id}'.`);
    invocation.hostCancelled = true;
    invocation.controller.abort(abortError());
  }

  #beginHostContribution(type: string, root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "id", "activationId", "sessionGeneration", "value");
    const id = idValue(root, "id");
    const activationId = canonicalActivationId(root, "activationId");
    const sessionGeneration = integerValue(root, "sessionGeneration");
    if (this.#phase !== "active"
      || activationId !== this.#environment.activationId
      || sessionGeneration !== this.#sessionGeneration) {
      throw new ProtocolError("Host contribution call does not identify this exact active generation.");
    }
    this.#requireFreshHostId(id);
    if (this.#inboundLimitReached(false)) {
      throw new ProtocolError("Host exceeded the worker inbound call limit.");
    }
    const controller = new AbortController();
    const stop = (): void => controller.abort(abortError());
    this.#work.signal.addEventListener("abort", stop, { once: true });
    const contribution: HostContribution = {
      id,
      type,
      sessionGeneration,
      value: requiredValue(root, "value"),
      controller,
      hostCancelled: false,
      active: true,
    };
    this.#hostContributions.set(id, contribution);
    contribution.operation = this.#executeHostContribution(contribution).finally(() => {
      this.#work.signal.removeEventListener("abort", stop);
      this.#hostContributions.delete(id);
      this.#rememberedHostIds.add(id);
      contribution.active = false;
    });
    void contribution.operation.catch((error: unknown) => this.#fail(error));
  }

  async #executeHostContribution(contribution: HostContribution): Promise<void> {
    try {
      const [type, value] = await this.#dispatchHostContribution(contribution);
      await this.#writer.write({
        type,
        id: contribution.id,
        activationId: this.#environment.activationId,
        sessionGeneration: contribution.sessionGeneration,
        value,
      });
    } catch (error) {
      if (error instanceof ProtocolError) throw error;
      const cancelled = contribution.controller.signal.aborted || isAbortError(error);
      if (!cancelled) safeStderr("Worker contribution handler failed", error);
      await this.#writer.write({
        type: "worker.contribution-error",
        id: contribution.id,
        activationId: this.#environment.activationId,
        sessionGeneration: contribution.sessionGeneration,
        error: {
          code: cancelled ? "worker.contribution-cancelled" : "worker.contribution-fault",
          message: cancelled
            ? "The worker contribution call was cancelled."
            : "The worker contribution handler failed.",
        },
      });
    }
  }

  async #dispatchHostContribution(contribution: HostContribution): Promise<readonly [string, JsonValue]> {
    const signal = contribution.controller.signal;
    signal.throwIfAborted();
    switch (contribution.type) {
      case "host.callback-start":
        return ["worker.callback-started", await this.#startCallback(contribution.value, signal)];
      case "host.callback-complete":
        return ["worker.callback-completed", await this.#completeCallback(contribution.value, signal)];
      case "host.callback-cancel":
        return ["worker.callback-cancelled", await this.#cancelCallback(contribution.value, signal)];
      case "host.auth-status":
        return ["worker.auth-status", await this.#authStatus(contribution.value, signal)];
      case "host.auth-connect":
        return ["worker.auth-connected", await this.#startAuth(contribution.value, signal)];
      case "host.auth-complete":
        return ["worker.auth-completed", await this.#completeAuth(contribution.value, signal)];
      case "host.auth-disconnect":
        return ["worker.auth-disconnected", await this.#disconnectAuth(contribution.value, signal)];
      default:
        throw new ProtocolError(`Host sent unsupported contribution call '${contribution.type}'.`);
    }
  }

  async #startCallback(value: JsonValue, signal: AbortSignal): Promise<JsonValue> {
    const root = objectValue(value, "Host callback-start value");
    only(root, "handlerId", "callbackSessionId", "callbackUri", "purpose", "parameters");
    const handler = this.#callbackHandler(stringValue(root, "handlerId", 128));
    const context = Object.freeze({
      callbackSessionId: idValue(root, "callbackSessionId"),
      callbackUri: absoluteUri(root, "callbackUri"),
      purpose: optionalString(root, "purpose", 128),
      parameters: stringMap(root, "parameters", false) as Readonly<Record<string, string>>,
    });
    const result = await handler.startCallback(context, signal);
    signal.throwIfAborted();
    if (result === null) return null;
    return callbackStartResult(result, this.#environment.packageId, context.callbackSessionId);
  }

  async #completeCallback(value: JsonValue, signal: AbortSignal): Promise<JsonValue> {
    const root = objectValue(value, "Host callback-complete value");
    only(root, "handlerId", "callbackSessionId", "queryValues");
    const handler = this.#callbackHandler(stringValue(root, "handlerId", 128));
    const context = Object.freeze({
      callbackSessionId: idValue(root, "callbackSessionId"),
      queryValues: stringMap(root, "queryValues", true),
    });
    const result = await handler.completeCallback(context, signal);
    signal.throwIfAborted();
    return callbackCompletionResult(result, this.#environment.packageId, context.callbackSessionId);
  }

  async #cancelCallback(value: JsonValue, signal: AbortSignal): Promise<null> {
    const root = objectValue(value, "Host callback-cancel value");
    only(root, "handlerId", "callbackSessionId", "reason");
    const handlerId = stringValue(root, "handlerId", 128);
    const reason = stringValue(root, "reason", 32);
    if (reason !== "expired" && reason !== "package-unloaded" && reason !== "host-shutdown" && reason !== "caller-requested") {
      throw new ProtocolError("Host callback cancellation reason is unsupported.");
    }
    const context: WorkerCallbackCancellationContext = Object.freeze({
      callbackSessionId: idValue(root, "callbackSessionId"),
      reason,
    });
    if (handlerId === "auth") await this.#authHandler().cancelCallback(context, signal);
    else await this.#callbackHandler(handlerId).cancelCallback(context, signal);
    signal.throwIfAborted();
    return null;
  }

  async #authStatus(value: JsonValue, signal: AbortSignal): Promise<JsonValue> {
    const root = objectValue(value, "Host auth-status value");
    only(root);
    const result = await this.#authHandler().getStatus(signal);
    signal.throwIfAborted();
    return authStatusResult(result, this.#environment.packageId);
  }

  async #startAuth(value: JsonValue, signal: AbortSignal): Promise<JsonValue> {
    const root = objectValue(value, "Host auth-connect value");
    only(root, "authSessionId", "callbackUri");
    const context = Object.freeze({
      authSessionId: idValue(root, "authSessionId"),
      callbackUri: absoluteUri(root, "callbackUri"),
    });
    const result = await this.#authHandler().startAuthorization(context, signal);
    signal.throwIfAborted();
    if (result === null) return null;
    return callbackStartResult(result, this.#environment.packageId, context.authSessionId);
  }

  async #completeAuth(value: JsonValue, signal: AbortSignal): Promise<JsonValue> {
    const root = objectValue(value, "Host auth-complete value");
    only(root, "authSessionId", "queryValues");
    const context = Object.freeze({
      authSessionId: idValue(root, "authSessionId"),
      queryValues: stringMap(root, "queryValues", true),
    });
    const result = await this.#authHandler().completeAuthorization(context, signal);
    signal.throwIfAborted();
    return authStatusResult(result, this.#environment.packageId);
  }

  async #disconnectAuth(value: JsonValue, signal: AbortSignal): Promise<JsonValue> {
    const root = objectValue(value, "Host auth-disconnect value");
    only(root);
    const result = await this.#authHandler().disconnect(signal);
    signal.throwIfAborted();
    return authStatusResult(result, this.#environment.packageId);
  }

  #callbackHandler(handlerId: string): NormalizedCallbackHandler {
    const handler = this.#options!.callbackHandlers.find((candidate) => candidate.callbackHandlerId === handlerId);
    if (handler === undefined) throw new ProtocolError(`Host requested undeclared callback handler '${handlerId}'.`);
    return handler;
  }

  #authHandler(): NormalizedAuthHandler {
    const handler = this.#options!.authHandler;
    if (handler === null) throw new ProtocolError("Host requested an undeclared auth handler.");
    return handler;
  }

  #cancelHostContribution(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "id", "activationId", "sessionGeneration");
    const id = idValue(root, "id");
    const contribution = this.#hostContributions.get(id);
    if (contribution === undefined) {
      if (this.#hostInvocations.has(id)) throw new ProtocolError(`Host used contribution cancellation for invocation '${id}'.`);
      if (this.#rememberedHostIds.has(id)) return;
      throw new ProtocolError(`Host sent unsolicited contribution cancellation '${id}'.`);
    }
    if (contribution.hostCancelled
      || canonicalActivationId(root, "activationId") !== this.#environment.activationId
      || integerValue(root, "sessionGeneration") !== contribution.sessionGeneration) {
      throw new ProtocolError("Host sent a duplicate or mismatched contribution cancellation.");
    }
    contribution.hostCancelled = true;
    contribution.controller.abort(abortError());
  }

  #handleClientResponse(type: string, root: Readonly<Record<string, JsonValue>>): void {
    if (type === "host.result" || type === "host.event") only(root, "type", "id", "value");
    else if (type === "host.complete") only(root, "type", "id");
    else only(root, "type", "id", "error");
    const id = idValue(root, "id");
    const call = this.#clientCalls.get(id);
    if (call === undefined || call.terminal) {
      throw new ProtocolError(`Host sent unsolicited, duplicate, or post-terminal response '${id}'.`);
    }
    if (type === "host.event") {
      if (call.kind !== "stream") throw new ProtocolError("Host sent an event for a unary worker call.");
      if (!call.cancelled) {
        const value = requiredValue(root, "value");
        const bytes = encodedJsonByteLength(value, LIMITS.maxRpcValueBytes);
        const rejection = call.queue.push(value, bytes);
        if (rejection !== null) {
          call.cancelled = true;
          call.cleanup();
          const error = infrastructureError(
            "resource-exhausted",
            "rpc.worker.stream-queue-limit",
            streamQueueLimitMessage(call.streamIdentity, rejection),
            "possibly-dispatched",
            "unsafe",
          );
          call.reject(error);
          call.queue.close(error);
          void this.#writer.write({ type: "worker.cancel", id: call.id }).catch((writeError: unknown) => this.#fail(writeError));
        }
      }
      return;
    }
    if (type === "host.result" && call.kind !== "unary") {
      throw new ProtocolError("Host sent a unary result for a stream worker call.");
    }
    if (type === "host.complete" && call.kind !== "stream") {
      throw new ProtocolError("Host completed a unary worker call without a result.");
    }

    call.terminal = true;
    this.#clientCalls.delete(id);
    call.cleanup();
    const finish = async (): Promise<void> => {
      const releaseTerminalResource = type !== "host.result"
        || call.cancelled && !call.awaitTerminalAfterAbort;
      try {
        if (type === "host.result") {
          const value = requiredValue(root, "value");
          if (call.cancelled && !call.awaitTerminalAfterAbort) {
            try {
              await call.abandonedResult?.(value);
            } finally {
              call.reject(abortError());
            }
          } else {
            call.resolve(value);
          }
        } else if (type === "host.complete") {
          if (!call.cancelled) call.queue.close();
        } else {
          const error = rpcError(root.error);
          if (!call.cancelled) call.hostError?.(error);
          if (call.cancelled && error.error.kind === "cancelled") call.reject(abortError());
          else call.reject(error);
          call.queue.close(call.cancelled && error.error.kind === "cancelled" ? abortError() : error);
        }
      } finally {
        if (releaseTerminalResource) call.releaseTerminalResource?.();
        call.resolveTerminal();
      }
    };
    void finish().catch((error: unknown) => this.#fail(error));
  }

  #beginShutdown(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "shutdownId", "reason");
    if (!canShutdown(this.#phase)) throw new ProtocolError("Host shutdown is duplicate or out of order.");
    const shutdownId = idValue(root, "shutdownId");
    const reason = stringValue(root, "reason", 128);
    this.#phase = "stopping";
    this.#work.abort(abortError());
    for (const invocation of this.#hostInvocations.values()) invocation.controller.abort(abortError());
    const contributions = [...this.#hostContributions.values()];
    for (const contribution of contributions) contribution.controller.abort(abortError());
    const ordinaryCalls = [...this.#clientCalls.values()].filter((call) => call.lane === "ordinary" && !call.terminal);
    for (const call of ordinaryCalls) this.#cancelClientCall(call);
    const scopeCleanup = [...this.#scopes.values()].map((scope) => this.#closeScope(scope));
    this.#shutdownOperation = this.#completeShutdown(shutdownId, reason, contributions, ordinaryCalls, scopeCleanup);
  }

  async #completeShutdown(
    shutdownId: string,
    reason: string,
    contributions: readonly HostContribution[],
    ordinaryCalls: readonly ClientCall[],
    scopeCleanup: readonly Promise<void>[],
  ): Promise<void> {
    try {
      await Promise.all([
        ...[...this.#hostInvocations.values()].map((invocation) => invocation.operation ?? Promise.resolve()),
        ...contributions.map((contribution) => contribution.operation ?? Promise.resolve()),
        this.#candidateStartOperation,
        this.#generationCommitOperation,
        this.#migrationPreparationOperation,
        this.#activationOperation,
      ]);
      await Promise.all([...ordinaryCalls.map((call) => call.terminalPromise), ...scopeCleanup]);
      await getApiBindingContext(this.#context).disposeAsync();
      await this.#releaseAllPayloadsForShutdown();
      await this.#options!.onShutdown?.(
        Object.freeze({ reason }),
        this.#context,
        this.#lifetime.signal,
      );
      this.#loggingClosed = true;
      const logging = [...this.#clientCalls.values()]
        .filter((call) => call.lane === "logging" && !call.terminal)
        .map((call) => call.terminalPromise);
      await Promise.all(logging);
      await this.#writer.write({ type: "worker.shutdown-ack", shutdownId });
      await this.#writer.flush();
      this.#phase = "stopped";
      this.#lifetime.abort(abortError());
      this.#resolveRun?.();
    } catch (error) {
      this.#fail(new ProtocolError(`The worker shutdown sequence failed: ${errorMessage(error)}`));
    }
  }

  #createClient(): RpcClient {
    const runtime=this;
    return Object.freeze({
      get deadline(){const id=runtime.#currentInvocationId.getStore();const invocation=id?runtime.#hostInvocations.get(id):undefined;return invocation?new Date(invocation.deadline):undefined;},
      createCallScope: (options?: RpcCallOptions) => this.#createCallScope(options),
      getProvider: (endpointReference: string, options?: RpcCallOptions) => this.#getProvider(undefined, endpointReference, options),
      reportInvariantViolation: (endpointReference: string, error: unknown, options?: RpcCallOptions) => (
        this.#reportInvariantViolation(undefined, endpointReference, error, options)
      ),
      discover: (contractId: string, options?: RpcCallOptions) => this.#discover(undefined, contractId, options),
      watch: (afterRevision: number, afterSequence: number, options?: RpcCallOptions) => (
        this.#watch(undefined, afterRevision, afterSequence, options)
      ),
      invoke: <TRequest extends JsonValue, TResponse extends JsonValue>(
        endpointReference: string,
        serviceId: string,
        methodId: string,
        request: TRequest,
        options?: RpcCallOptions,
      ) => this.#invoke<TResponse>(undefined, endpointReference, serviceId, methodId, request, options),
      subscribe: <TRequest extends JsonValue, TEvent extends JsonValue>(
        endpointReference: string,
        serviceId: string,
        methodId: string,
        request: TRequest,
        options?: RpcCallOptions,
      ) => this.#subscribe<TEvent>(undefined, endpointReference, serviceId, methodId, request, options),
    });
  }

  async #createCallScope(options?: RpcCallOptions): Promise<RpcCallScope> {
    options?.signal?.throwIfAborted();
    if (this.#openScopes >= LIMITS.maxCallScopes) {
      throw infrastructureError("resource-exhausted", "rpc.worker.scope-limit", "The worker RPC call-scope limit was reached.");
    }
    this.#openScopes += 1;
    const capacity = new ResourceReservation(() => {
      if (this.#openScopes > 0) this.#openScopes -= 1;
    });
    try {
      const value = await this.#unaryClientCall<JsonValue>({
        type: "worker.scope-open",
        deadlineUtc: deadline(options),
        ...this.#lineageMetadata(),
      }, options, {
        abandonedResult: async (result) => {
          const identity = scopeIdentity(result);
          await this.#closeAbandonedScope(identity.scopeId);
        },
        releaseTerminalResource: () => capacity.release(),
      });
      const identity = this.#parseHostValue(value, scopeIdentity);
      if (this.#scopes.has(identity.scopeId)) throw new ProtocolError(`Host reused RPC call-scope id '${identity.scopeId}'.`);
      const state: ScopeState = {
        id: identity.scopeId,
        deadline: identity.deadline,
        controller: new AbortController(),
        contentFiles: new Set(),
        capacity,
        closing: false,
      };
      this.#scopes.set(state.id, state);
      state.cancelDeadline = scheduleDeadline(state.deadline.getTime(), () => {
        state.controller.abort(deadlineExceededError());
        void this.#closeScope(state).catch((error: unknown) => this.#fail(error));
      });
      return this.#scopeApi(state);
    } catch (error) {
      capacity.release();
      throw error;
    }
  }

  #scopeApi(state: ScopeState): RpcCallScope {
    const linkedOptions = (options?: RpcCallOptions): RpcCallOptions => ({
      ...options,
      signal: combineSignals(state.controller.signal, options?.signal),
    });
    const content: RpcCallerContentClient = Object.freeze({
      register: (endpointReference: string, source: RpcContentSource, options: RpcContentRegistrationOptions, call?:RpcCallOptions) => this.#registerContentSource(
        { kind: "scope", scope: state, endpointReference }, source, options, combineSignals(state.controller.signal,call?.signal)),
      registerFile: (endpointReference: string, filePath: string, options: RpcContentRegistrationOptions, call?:RpcCallOptions) => this.#registerContentFile(
        { kind: "scope", scope: state, endpointReference }, filePath, options, combineSignals(state.controller.signal,call?.signal)),
      open: (reference: RpcContentReference, options?:RpcCallOptions) => this.#openContent(
        { kind: "scope", scope: state }, reference, combineSignals(state.controller.signal,options?.signal)),
    });
    return Object.freeze({
      deadline: new Date(state.deadline),
      signal: state.controller.signal,
      content,
      getProvider: (endpointReference: string, options?: RpcCallOptions) => (
        this.#scopeOperation(state, () => this.#getProvider(state.id, endpointReference, linkedOptions(options)))
      ),
      reportInvariantViolation: (endpointReference: string, error: unknown, options?: RpcCallOptions) => (
        this.#scopeOperation(state, () => this.#reportInvariantViolation(state.id, endpointReference, error, linkedOptions(options)))
      ),
      discover: (contractId: string, options?: RpcCallOptions) => (
        this.#scopeOperation(state, () => this.#discover(state.id, contractId, linkedOptions(options)))
      ),
      watch: (afterRevision: number, afterSequence: number, options?: RpcCallOptions) => (
        this.#scopeStream(state, () => this.#watch(state.id, afterRevision, afterSequence, linkedOptions(options)))
      ),
      invoke: <TRequest extends JsonValue, TResponse extends JsonValue>(
        endpointReference: string,
        serviceId: string,
        methodId: string,
        request: TRequest,
        options?: RpcCallOptions,
      ) => this.#scopeOperation(state, () => this.#invoke<TResponse>(
        state.id, endpointReference, serviceId, methodId, request, linkedOptions(options))),
      subscribe: <TRequest extends JsonValue, TEvent extends JsonValue>(
        endpointReference: string,
        serviceId: string,
        methodId: string,
        request: TRequest,
        options?: RpcCallOptions,
      ) => this.#scopeStream(state, () => this.#subscribe<TEvent>(
        state.id, endpointReference, serviceId, methodId, request, linkedOptions(options))),
      close: () => this.#closeScope(state),
    });
  }

  async #scopeOperation<T>(state: ScopeState, operation: () => Promise<T>): Promise<T> {
    this.#ensureScopeActive(state);
    try {
      return await operation();
    } catch (error) {
      if (state.controller.signal.aborted && Date.now() >= state.deadline.getTime()) throw deadlineExceededError();
      throw error;
    }
  }

  #scopeStream<T>(state: ScopeState, operation: () => AsyncIterable<T>): AsyncIterable<T> {
    const runtime = this;
    return {
      async *[Symbol.asyncIterator](): AsyncIterator<T> {
        runtime.#ensureScopeActive(state);
        try {
          for await (const value of operation()) yield value;
        } catch (error) {
          if (state.controller.signal.aborted && Date.now() >= state.deadline.getTime()) throw deadlineExceededError();
          throw error;
        }
      },
    };
  }

  async #closeScope(state: ScopeState): Promise<void> {
    if (state.closePromise !== undefined) return await state.closePromise;
    state.closing = true;
    state.cancelDeadline?.();
    state.controller.abort(abortError());
    for (const file of state.contentFiles) file.revoke();
    state.contentFiles.clear();
    state.closePromise = (async () => {
      try {
        if (this.#scopes.get(state.id) === state && this.#canSendControl()) {
          const value = await this.#unaryClientCall<JsonValue>({
            type: "worker.scope-close",
            scopeId: state.id,
          }, undefined, { lane: "control" });
          if (value !== null) throw new ProtocolError("Host call-scope close result must be null.");
        }
      } finally {
        if (this.#scopes.get(state.id) === state) this.#scopes.delete(state.id);
        state.capacity.release();
      }
    })();
    return await state.closePromise;
  }

  async #closeAbandonedScope(scopeId: string): Promise<void> {
    if (this.#scopes.has(scopeId)) throw new ProtocolError(`Host reused RPC call-scope id '${scopeId}'.`);
    if (!this.#canSendControl()) return;
    const value = await this.#unaryClientCall<JsonValue>({
      type: "worker.scope-close",
      scopeId,
    }, undefined, { lane: "control" });
    if (value !== null) throw new ProtocolError("Host call-scope close result must be null.");
  }

  #ensureScopeActive(state: ScopeState): void {
    if (state.closing || this.#scopes.get(state.id) !== state) throw new Error("RPC call scope is closed.");
    if (Date.now() >= state.deadline.getTime()) {
      state.controller.abort(deadlineExceededError());
      void this.#closeScope(state).catch((error: unknown) => this.#fail(error));
      throw deadlineExceededError();
    }
  }

  #getProvider(scopeId: string | undefined, endpointReference: string, options?: RpcCallOptions): Promise<RpcProviderSnapshot | null> {
    const envelope: Record<string, unknown> = {
      type: "worker.get-provider",
      ...(scopeId === undefined ? {} : { scopeId }),
      endpointReference: boundedString(endpointReference, "RPC endpointReference", 256),
    };
    return this.#validatedUnaryClientCall(envelope, options, (value) => value === null ? null : providerSnapshot(value));
  }

  #reportInvariantViolation(scopeId: string | undefined, endpointReference: string, error: unknown, options?: RpcCallOptions): Promise<boolean> {
    const source = error instanceof Error ? error.message : String(error ?? "");
    const exceptionMessage = sanitizeText(source, 512) || "The provider violated an orchestrator invariant.";
    return this.#validatedUnaryClientCall({
      type: "worker.report-invariant-violation",
      ...(scopeId === undefined ? {} : { scopeId }),
      endpointReference: boundedString(endpointReference, "RPC endpointReference", 256),
      exceptionMessage,
    }, options, (value) => {
      if (typeof value !== "boolean") throw new ProtocolError("Host invariant-report result must be a Boolean.");
      return value;
    });
  }

  #discover(scopeId: string | undefined, contractId: string, options?: RpcCallOptions): Promise<RpcCatalogSnapshot> {
    return this.#validatedUnaryClientCall({
      type: "worker.discover",
      ...(scopeId === undefined ? {} : { scopeId }),
      contractId: boundedString(contractId, "RPC contractId", 256),
    }, options, catalogSnapshot);
  }

  #watch(scopeId: string | undefined, afterRevision: number, afterSequence: number, options?: RpcCallOptions): AsyncIterable<RpcCatalogEvent> {
    safeNonNegativeInteger(afterRevision, "RPC afterRevision");
    safeNonNegativeInteger(afterSequence, "RPC afterSequence");
    return this.#validatedStreamClientCall({
      type: "worker.watch",
      ...(scopeId === undefined ? {} : { scopeId }),
      afterRevision,
      afterSequence,
    }, options, catalogEvent, { operation: "watch" });
  }

  #invoke<TResponse extends JsonValue>(
    scopeId: string | undefined,
    endpointReference: string,
    serviceId: string,
    methodId: string,
    request: JsonValue,
    options?: RpcCallOptions,
  ): Promise<TResponse> {
    assertJsonValue(request);
    return this.#unaryClientCall<TResponse>({
      type: "worker.invoke",
      ...(scopeId === undefined ? {} : { scopeId }),
      endpointReference: boundedString(endpointReference, "RPC endpointReference", 256),
      serviceId: boundedString(serviceId, "RPC serviceId", 128),
      methodId: boundedString(methodId, "RPC methodId", 128),
      request,
      deadlineUtc: deadline(options),
      ...this.#lineageMetadata(),
    }, options);
  }

  #subscribe<TEvent extends JsonValue>(
    scopeId: string | undefined,
    endpointReference: string,
    serviceId: string,
    methodId: string,
    request: JsonValue,
    options?: RpcCallOptions,
  ): AsyncIterable<TEvent> {
    assertJsonValue(request);
    const validatedServiceId = boundedString(serviceId, "RPC serviceId", 128);
    const validatedMethodId = boundedString(methodId, "RPC methodId", 128);
    return this.#streamClientCall<TEvent>({
      type: "worker.subscribe",
      ...(scopeId === undefined ? {} : { scopeId }),
      endpointReference: boundedString(endpointReference, "RPC endpointReference", 256),
      serviceId: validatedServiceId,
      methodId: validatedMethodId,
      request,
      deadlineUtc: deadline(options),
      ...this.#lineageMetadata(),
    }, options, {
      operation: "subscribe",
      serviceId: sanitizeDiagnosticIdentity(validatedServiceId),
      methodId: sanitizeDiagnosticIdentity(validatedMethodId),
    });
  }

  #validatedUnaryClientCall<T>(
    envelope: Readonly<Record<string, unknown>>,
    options: RpcCallOptions | undefined,
    parser: (value: JsonValue) => T,
    behavior: ClientCallBehavior = {},
  ): Promise<T> {
    return this.#unaryClientCall<JsonValue>(envelope, options, behavior)
      .then((value) => this.#parseHostValue(value, parser));
  }

  #validatedStreamClientCall<T>(
    envelope: Readonly<Record<string, unknown>>,
    options: RpcCallOptions | undefined,
    parser: (value: JsonValue) => T,
    streamIdentity: OutboundStreamIdentity,
  ): AsyncIterable<T> {
    const source = this.#streamClientCall<JsonValue>(envelope, options, streamIdentity);
    const runtime = this;
    return {
      async *[Symbol.asyncIterator](): AsyncIterator<T> {
        for await (const value of source) yield runtime.#parseHostValue(value, parser);
      },
    };
  }

  #parseHostValue<T>(value: JsonValue, parser: (value: JsonValue) => T): T {
    try {
      return parser(value);
    } catch (error) {
      const protocol = error instanceof ProtocolError
        ? error
        : new ProtocolError(`Host result is malformed: ${errorMessage(error)}`);
      this.#fail(protocol);
      throw protocol;
    }
  }

  #lineageMetadata(): Readonly<{ parentInvocationId?: string | null }> {
    return this.#environment.rpcExtendedDeadlineV1
      ? { parentInvocationId: this.#currentInvocationId.getStore() ?? null }
      : {};
  }

  #createInvocationContentClient(invocation: HostInvocation): RpcContentClient {
    const retirements = new WeakMap<object, AbortSignal>();
    const authenticate = (signal: AbortSignal | undefined) => (error: RpcError): void => {
      const value = error.error;
      const codes = { unavailable: "rpc.content.authority-revoked", "deadline-exceeded": "rpc.call.deadline-exceeded", "stale-endpoint": "rpc.endpoint.stale", cancelled: "rpc.call.cancelled" };
      if (signal && !signal.aborted && !invocation.controller.signal.aborted
        && value.dispatchState === "not-dispatched" && value.retrySafety === "safe"
        && Object.hasOwn(codes, value.kind) && codes[value.kind as keyof typeof codes] === value.code) retirements.set(error, signal);
    };
    return Object.freeze({
      consumeResponseContentRetirement: (error: unknown, signal: AbortSignal): boolean => {
        if (!error || typeof error !== "object") return false;
        const matched = retirements.get(error) === signal;
        retirements.delete(error);
        return matched && !signal.aborted && !invocation.controller.signal.aborted;
      },
      register: (source: RpcContentSource, options: RpcContentRegistrationOptions, call?:RpcCallOptions) => this.#registerContentSource(
        { kind: "invocation", invocation }, source, options, combineSignals(invocation.controller.signal,call?.signal), authenticate(call?.signal)),
      registerFile: (filePath: string, options: RpcContentRegistrationOptions, call?:RpcCallOptions) => this.#registerContentFile(
        { kind: "invocation", invocation }, filePath, options, combineSignals(invocation.controller.signal,call?.signal)),
      open: (reference: RpcContentReference, options?:RpcCallOptions) => this.#openContent(
        { kind: "invocation", invocation }, reference, combineSignals(invocation.controller.signal,options?.signal)),
    });
  }

  async #registerContentSource(
    authority: ContentRegistrationAuthority,
    source: RpcContentSource,
    options: RpcContentRegistrationOptions,
    signal: AbortSignal,
    hostError?: (error: RpcError) => void,
  ): Promise<RpcContentReference> {
    if (source === null || typeof source !== "object" || !(Symbol.asyncIterator in source)) {
      throw new TypeError("RPC content source must be an AsyncIterable<Uint8Array>.");
    }
    const normalized = contentRegistrationOptions(options);
    const directory = this.#environment.temporary.resolve("sunder-sdk-worker/rpc-content");
    await mkdir(directory, { recursive: true, mode: 0o700 });
    const filePath = resolve(directory, `${authority.kind === "scope" ? "caller" : "provider"}-${randomUUID().replaceAll("-", "")}.content`);
    let file: FileHandle | undefined;
    let length = 0;
    try {
      signal.throwIfAborted();
      file = await open(filePath, "wx", 0o600);
      for await (const item of source) {
        signal.throwIfAborted();
        if (!(item instanceof Uint8Array)) throw new TypeError("RPC content source yielded a non-Uint8Array value.");
        length += item.byteLength;
        if (!Number.isSafeInteger(length) || length > LIMITS.maxContentBytes) {
          throw new RangeError(`RPC content exceeds the ${LIMITS.maxContentBytes} byte worker limit.`);
        }
        await writeAll(file, item);
      }
      await file.sync();
      await file.close();
      file = undefined;
      if (normalized.length !== null && normalized.length !== length) {
        throw new RangeError("RPC content length does not match the declared length.");
      }
      return await this.#registerContentFile(authority, filePath, { ...options, length }, signal, hostError);
    } finally {
      await file?.close().catch(() => undefined);
      await unlink(filePath).catch(() => undefined);
    }
  }

  async #registerContentFile(
    authority: ContentRegistrationAuthority,
    filePath: string,
    options: RpcContentRegistrationOptions,
    signal: AbortSignal,
    hostError?: (error: RpcError) => void,
  ): Promise<RpcContentReference> {
    this.#ensureContentAuthority(authority);
    signal.throwIfAborted();
    const normalizedPath = await this.#resolveWorkerOwnedFile(filePath);
    const information = await stat(normalizedPath);
    if (!information.isFile() || information.size > LIMITS.maxContentBytes) {
      throw new RangeError(`RPC content must be a regular file no larger than ${LIMITS.maxContentBytes} bytes.`);
    }
    const optionsWire = contentRegistrationOptions(options);
    if (optionsWire.length !== null && optionsWire.length !== information.size) {
      throw new RangeError("RPC content file length does not match the declared length.");
    }
    const authorityFields = authority.kind === "scope"
      ? { scopeId: authority.scope.id, endpointReference: boundedString(authority.endpointReference, "RPC endpointReference", 256) }
      : { invocationId: authority.invocation.id };
    const value = await this.#unaryClientCall<JsonValue>({
      type: "worker.content-register",
      ...authorityFields,
      filePath: normalizedPath,
      options: optionsWire,
    }, { signal }, { hostError });
    return this.#parseHostValue(value, contentReference);
  }

  async #openContent(
    authority: ContentOpenAuthority,
    referenceValue: RpcContentReference,
    signal: AbortSignal,
  ): Promise<RpcContentFile> {
    this.#ensureContentAuthority(authority);
    const reference = contentReference(referenceValue, false);
    if (reference.length > LIMITS.maxContentBytes) throw new RangeError("RPC content reference exceeds the worker byte limit.");
    if (this.#openContentHandles >= LIMITS.maxContentHandles) {
      throw infrastructureError("resource-exhausted", "rpc.worker.content-handle-limit", "The worker RPC content-handle limit was reached.");
    }
    this.#openContentHandles += 1;
    let retained = false;
    const reservation = new ResourceReservation(() => {
      if (this.#openContentHandles > 0) this.#openContentHandles -= 1;
    });
    const authorityFields: Readonly<Record<string, string>> = authority.kind === "scope"
      ? { scopeId: authority.scope.id }
      : { invocationId: authority.invocation.id };
    try {
      const value = await this.#unaryClientCall<JsonValue>({
        type: "worker.content-open",
        ...authorityFields,
        reference,
      }, { signal }, {
        abandonedResult: async (result) => {
          const handle = contentFileHandle(result);
          await this.#validateOpenedContent(handle.filePath, reference, undefined);
          if (this.#isContentAuthorityActive(authority) && this.#canSendControl()) {
            await this.#releaseContent(authorityFields, handle.handleId);
          }
        },
        releaseTerminalResource: () => reservation.release(),
      });
      const handle = this.#parseHostValue(value, contentFileHandle);
      const opened = await this.#validateOpenedContent(handle.filePath, reference, signal);
      if (opened === undefined) throw new ProtocolError("Host RPC content stream was not opened.");
      if (!this.#isContentAuthorityActive(authority)) {
        opened.destroy();
        if (this.#canSendControl()) await this.#releaseContent(authorityFields, handle.handleId);
        throw abortError(signal);
      }
      retained = true;
      const owner = authority.kind === "scope" ? authority.scope.contentFiles : authority.invocation.contentFiles;
      const state = new OpenContentFile(
        opened,
        handle.filePath,
        async () => {
          owner.delete(state);
          try {
            if (this.#isContentAuthorityActive(authority) && this.#canSendControl()) {
              await this.#releaseContent(authorityFields, handle.handleId);
            }
          } finally {
            reservation.release();
          }
        },
        (error) => {
          if (this.#isContentAuthorityActive(authority)) this.#fail(error);
        },
      );
      owner.add(state);
      return state.api;
    } catch (error) {
      if (!retained) reservation.release();
      throw error;
    }
  }

  async #releaseContent(authorityFields: Readonly<Record<string, string>>, handleId: string): Promise<void> {
    const value = await this.#unaryClientCall<JsonValue>({
      type: "worker.content-release",
      ...authorityFields,
      handleId,
    }, undefined, { lane: "control", awaitTerminalAfterAbort: true });
    if (value !== null) throw new ProtocolError("Host content release result must be null.");
  }

  async #validateOpenedContent(filePath: string, reference: RpcContentReference, signal: AbortSignal | undefined): Promise<Readable | undefined> {
    const path = await this.#resolveHostTemporaryFile(filePath, "RPC content");
    signal?.throwIfAborted();
    const file = await open(path, "r");
    try {
      const information = await file.stat();
      if (!information.isFile() || information.size !== reference.length) {
        throw new ProtocolError("Host RPC content file length does not match its reference.");
      }
      const actual = await hashOpenFile(file, reference.length, signal);
      if (actual !== reference.sha256.toLowerCase()) {
        throw new ProtocolError("Host RPC content file hash does not match its reference.");
      }
      if (signal === undefined) {
        await file.close();
        return undefined;
      }
      signal.throwIfAborted();
      return file.createReadStream({ autoClose: true, start: 0 });
    } catch (error) {
      await file.close().catch(() => undefined);
      throw error;
    }
  }

  #ensureContentAuthority(authority: ContentRegistrationAuthority | ContentOpenAuthority): void {
    if (!this.#isContentAuthorityActive(authority)) {
      throw infrastructureError("unavailable", "rpc.content.authority-revoked", "The RPC content authority has ended.");
    }
  }

  #isContentAuthorityActive(authority: ContentRegistrationAuthority | ContentOpenAuthority): boolean {
    return authority.kind === "scope"
      ? !authority.scope.closing && this.#scopes.get(authority.scope.id) === authority.scope && !authority.scope.controller.signal.aborted
      : authority.invocation.active && this.#hostInvocations.get(authority.invocation.id) === authority.invocation && !authority.invocation.controller.signal.aborted;
  }

  async #resolveWorkerOwnedFile(value: string): Promise<string> {
    const path = boundedString(value, "RPC content filePath", 4096);
    const fullPath = resolve(this.#environment.content.rootPath, path);
    const root = [
      this.#environment.content.rootPath,
      this.#environment.temporary.rootPath,
    ].find((candidate) => isWithin(candidate, fullPath));
    if (root === undefined) {
      throw new RangeError("RPC content files must be inside package content or temporary storage.");
    }
    const actualRoot = await realpath(root);
    const actualPath = await realpath(fullPath);
    if (!isWithin(actualRoot, actualPath)) throw new RangeError("RPC content file traverses a symbolic link outside its authorized root.");
    const information = await stat(actualPath);
    if (!information.isFile()) throw new RangeError("RPC content path must identify a regular file.");
    return actualPath;
  }

  #createContext(): WorkerContext {
    const settings = this.#createSettings();
    const state = this.#createState();
    const files = this.#createFiles();
    const secrets = this.#createSecrets();
    const logging = this.#createLogging();
    const storage = Object.freeze({ state, files, workspace: this.#environment.workspace });
    const services = createApiContext(this.#environment.packageId, this.#client, this.#work.signal);
    return Object.freeze({
      api: services.api,
      packageId: this.#environment.packageId,
      packageVersion: this.#environment.packageVersion,
      contentRootPath: this.#environment.content.rootPath,
      temporaryRootPath: this.#environment.temporary.rootPath,
      content: this.#environment.content,
      temporary: this.#environment.temporary,
      workspace: this.#environment.workspace,
      settings,
      state,
      files,
      storage,
      secrets,
      logging,
      rpc: this.#client,
      reportGenerationFault: (code: string, message: string, options?: WorkerCallOptions) => (
        this.#reportGenerationFault(code, message, options)
      ),
    });
  }

  async #reportGenerationFault(code: string, message: string, options?: WorkerCallOptions): Promise<void> {
    const normalizedCode = requiredText(code, "Generation-fault code", 128);
    if (!SAFE_ERROR_CODE.test(normalizedCode)) throw new TypeError("Generation-fault code must be an ASCII error token.");
    const normalizedMessage = sanitizeText(requiredText(message, "Generation-fault message", 512), 512);
    if (normalizedMessage.length === 0) throw new TypeError("Generation-fault message must contain safe text.");
    options?.signal?.throwIfAborted();
    if (this.#phase !== "active" || this.#sessionGeneration === undefined) {
      throw new Error("A worker may report a generation fault only for its active committed generation.");
    }
    if (this.#generationFaultReported) throw new Error("This worker already reported a fault for its active generation.");
    this.#generationFaultReported = true;
    await this.#writer.write({
      type: "worker.generation-fault",
      activationId: this.#environment.activationId,
      sessionGeneration: this.#sessionGeneration,
      code: normalizedCode,
      message: normalizedMessage,
    });
  }

  #createSettings(): WorkerSettings {
    return Object.freeze({
      get: (key: string, options?: WorkerCallOptions) => this.#getDataString("worker.settings-get", validateStorageKey(key), "effective", options, false),
      getStored: (key: string, options?: WorkerCallOptions) => this.#getDataString("worker.settings-get", validateStorageKey(key), "stored", options, false),
      set: (key: string, value: string, options?: WorkerCallOptions) => this.#setDataString("worker.settings-set", validateStorageKey(key), validateStorageValue(value), options, false),
      delete: (key: string, options?: WorkerCallOptions) => this.#deleteData("worker.settings-delete", validateStorageKey(key), options),
    });
  }

  #createState(): WorkerState {
    return Object.freeze({
      get: (key: string, options?: WorkerCallOptions) => this.#getDataString("worker.state-get", validateStorageKey(key), "value", options, false),
      set: (key: string, value: string, options?: WorkerCallOptions) => this.#setDataString("worker.state-set", validateStorageKey(key), validateStorageValue(value), options, false),
      has: (key: string, options?: WorkerCallOptions) => this.#validatedUnaryClientCall({
        type: "worker.state-get",
        key: validateStorageKey(key),
        mode: "contains",
      }, options, (value) => {
        if (typeof value !== "boolean") throw new ProtocolError("Host package-state contains result must be a Boolean.");
        return value;
      }),
      delete: (key: string, options?: WorkerCallOptions) => this.#deleteData("worker.state-delete", validateStorageKey(key), options),
      list: (prefix?: string | null, options?: WorkerCallOptions) => this.#listStateKeys(prefix ?? null, options),
    });
  }

  #createMigrationStateReader(): WorkerStateReader {
    return Object.freeze({
      get: (key: string, options?: WorkerCallOptions) => this.#getDataString(
        "worker.state-get",
        validateStorageKey(key),
        "value",
        options,
        false,
        true,
      ),
      has: (key: string, options?: WorkerCallOptions) => this.#validatedUnaryClientCall({
        type: "worker.state-get",
        key: validateStorageKey(key),
        mode: "contains",
      }, options, (value) => {
        if (typeof value !== "boolean") throw new ProtocolError("Host package-state contains result must be a Boolean.");
        return value;
      }, { migrationPreparationRead: true }),
      list: (prefix?: string | null, options?: WorkerCallOptions) => this.#listStateKeys(
        prefix ?? null,
        options,
        true,
      ),
    });
  }

  #createFiles(): WorkerFiles {
    return Object.freeze({
      read: (relativePath: string, options?: WorkerCallOptions) => this.#readPackageFile(validateRelativePath(relativePath), options),
      write: (relativePath: string, contents: Uint8Array, options?: WorkerCallOptions) => this.#writePackageFile(validateRelativePath(relativePath), contents, options),
      delete: (relativePath: string, options?: WorkerCallOptions) => this.#deletePackageFile(validateRelativePath(relativePath), options),
    });
  }

  #createSecrets(): WorkerSecrets {
    return Object.freeze({
      get: (key: string, options?: WorkerCallOptions) => this.#getDataString("worker.secrets-get", validateStorageKey(key), undefined, options, true),
      set: (key: string, value: string, options?: WorkerCallOptions) => this.#setDataString("worker.secrets-set", validateStorageKey(key), validateStorageValue(value), options, true),
      delete: (key: string, options?: WorkerCallOptions) => this.#deleteData("worker.secrets-delete", validateStorageKey(key), options),
    });
  }

  #createLogging(): WorkerLogging {
    const events = Object.freeze({
      write: async (entry: WorkerEventLogEntry, options?: WorkerCallOptions): Promise<void> => {
        const value = await this.#writeLog(normalizeEventLog(entry), options);
        if (value !== null) throw new ProtocolError("Host worker event-log result must be null.");
      },
    });
    return Object.freeze({
      events,
      createLogger: (category: string): WorkerLogger => {
        const normalizedCategory = requiredText(category, "Logger category", 256);
        const log = (level: PackageLogLevel, message: string, details?: WorkerLogDetails): void => {
          try {
            const operation = this.#writeLog(normalizeConventionalLog(normalizedCategory, level, message, details));
            void operation.catch(() => undefined);
          } catch {
            // Conventional logging is detached and failure-isolated.
          }
        };
        return Object.freeze({
          log,
          trace: (message: string, details?: WorkerLogDetails) => log("trace", message, details),
          debug: (message: string, details?: WorkerLogDetails) => log("debug", message, details),
          information: (message: string, details?: WorkerLogDetails) => log("information", message, details),
          warning: (message: string, details?: WorkerLogDetails) => log("warning", message, details),
          error: (message: string, details?: WorkerLogDetails) => log("error", message, details),
          critical: (message: string, details?: WorkerLogDetails) => log("critical", message, details),
        });
      },
    });
  }

  #writeLog(entry: NormalizedLogEntry, options?: WorkerCallOptions): Promise<JsonValue> {
    return this.#unaryClientCall<JsonValue>({
      type: "worker.logging-write",
      ...entry,
    }, options, { lane: "logging", awaitTerminalAfterAbort: true });
  }

  async #getDataString(
    type: string,
    key: string,
    mode: string | undefined,
    options: WorkerCallOptions | undefined,
    sensitive: boolean,
    migrationPreparationRead = false,
  ): Promise<string | null> {
    const reservation = this.#reservePayloadHandle();
    let retained = false;
    try {
      const value = await this.#unaryClientCall<JsonValue>({
        type,
        key,
        ...(mode === undefined ? {} : { mode }),
      }, options, {
        abandonedResult: (result) => this.#abandonDataResult(result, LIMITS.maxPayloadBytes),
        releaseTerminalResource: () => { if (!retained) reservation.release(); },
        migrationPreparationRead,
      });
      const result = dataValue(value, LIMITS.maxPayloadBytes);
      if (result.kind === "inline") return result.value;
      const handle = await this.#claimPayload(result, reservation);
      retained = true;
      let bytes: Buffer | undefined;
      try {
        bytes = await this.#readPayload(handle, options?.signal);
        const text = decodeStrictUtf8(bytes, "Host package-data payload");
        return validateStorageValue(text);
      } finally {
        if (sensitive && bytes !== undefined) bytes.fill(0);
        await this.#releasePayload(handle);
      }
    } finally {
      if (!retained) reservation.release();
    }
  }

  async #setDataString(
    type: string,
    key: string,
    value: string,
    options: WorkerCallOptions | undefined,
    sensitive: boolean,
  ): Promise<void> {
    const bytes = Buffer.from(value, "utf8");
    try {
      if (bytes.byteLength <= LIMITS.inlineDataBytes) {
        const result = await this.#unaryClientCall<JsonValue>({
          type,
          key,
          value: { kind: "inline", value },
        }, options, { awaitTerminalAfterAbort: true });
        if (result !== null) throw new ProtocolError("Host package-data mutation result must be null.");
        return;
      }
      await this.#withUploadPayload(bytes, LIMITS.maxPayloadBytes, options, async (handle) => {
        let submitted = false;
        try {
          const result = await this.#unaryClientCall<JsonValue>({
            type,
            key,
            value: { kind: "payload", handleId: handle.id, length: bytes.byteLength, sha256: sha256(bytes) },
          }, options, {
            awaitTerminalAfterAbort: true,
            started: () => {
              this.#consumePayload(handle);
              submitted = true;
            },
          });
          if (result !== null) throw new ProtocolError("Host package-data mutation result must be null.");
        } finally {
          if (!submitted) await this.#releasePayload(handle);
        }
      });
    } finally {
      if (sensitive) bytes.fill(0);
    }
  }

  async #deleteData(type: string, key: string, options?: WorkerCallOptions): Promise<void> {
    const result = await this.#unaryClientCall<JsonValue>({ type, key }, options, { awaitTerminalAfterAbort: true });
    if (result !== null) throw new ProtocolError("Host package-data delete result must be null.");
  }

  async #listStateKeys(
    prefix: string | null,
    options?: WorkerCallOptions,
    migrationPreparationRead = false,
  ): Promise<readonly string[]> {
    if (prefix !== null && prefix !== "") validateStorageKey(prefix);
    const reservation = this.#reservePayloadHandle();
    let retained = false;
    try {
      const value = await this.#unaryClientCall<JsonValue>({
        type: "worker.state-list",
        prefix,
      }, options, {
        abandonedResult: (result) => this.#abandonKeysResult(result),
        releaseTerminalResource: () => { if (!retained) reservation.release(); },
        migrationPreparationRead,
      });
      const result = dataKeys(value);
      if (result.kind === "inline") return validateReturnedKeys(result.keys, value);
      const handle = await this.#claimPayload(result, reservation);
      retained = true;
      try {
        const bytes = await this.#readPayload(handle, options?.signal);
        const text = decodeStrictUtf8(bytes, "Host package-state key payload");
        let parsed: unknown;
        try {
          parsed = JSON.parse(text) as unknown;
        } catch {
          throw new ProtocolError("Host package-state key payload is not strict JSON.");
        }
        if (!Array.isArray(parsed) || parsed.some((item) => typeof item !== "string")) {
          throw new ProtocolError("Host package-state key payload must be a string array.");
        }
        return validateReturnedKeys(parsed as string[]);
      } finally {
        await this.#releasePayload(handle);
      }
    } finally {
      if (!retained) reservation.release();
    }
  }

  async #readPackageFile(relativePath: string, options?: WorkerCallOptions): Promise<Buffer | null> {
    const reservation = this.#reservePayloadHandle();
    let retained = false;
    try {
      const value = await this.#unaryClientCall<JsonValue>({
        type: "worker.files-read",
        relativePath,
      }, options, {
        abandonedResult: (result) => this.#abandonFileResult(result),
        releaseTerminalResource: () => { if (!retained) reservation.release(); },
      });
      if (value === null) return null;
      const descriptor = dataPayload(value, LIMITS.maxFilePayloadBytes);
      const handle = await this.#claimPayload(descriptor, reservation);
      retained = true;
      try {
        return await this.#readPayload(handle, options?.signal);
      } finally {
        await this.#releasePayload(handle);
      }
    } finally {
      if (!retained) reservation.release();
    }
  }

  async #writePackageFile(relativePath: string, contents: Uint8Array, options?: WorkerCallOptions): Promise<void> {
    if (!(contents instanceof Uint8Array)) throw new TypeError("Package file contents must be a Uint8Array.");
    if (contents.byteLength > LIMITS.maxFilePayloadBytes) {
      throw new RangeError(`Package files cannot exceed ${LIMITS.maxFilePayloadBytes} bytes.`);
    }
    const bytes = Buffer.from(contents);
    await this.#withUploadPayload(bytes, LIMITS.maxFilePayloadBytes, options, async (handle) => {
      let submitted = false;
      try {
        const value = await this.#unaryClientCall<JsonValue>({
          type: "worker.files-write",
          relativePath,
          contents: { kind: "payload", handleId: handle.id, length: bytes.byteLength, sha256: sha256(bytes) },
        }, options, {
          awaitTerminalAfterAbort: true,
          started: () => {
            this.#consumePayload(handle);
            submitted = true;
          },
        });
        if (value !== null) throw new ProtocolError("Host package-file write result must be null.");
      } finally {
        if (!submitted) await this.#releasePayload(handle);
      }
    });
  }

  async #deletePackageFile(relativePath: string, options?: WorkerCallOptions): Promise<void> {
    const value = await this.#unaryClientCall<JsonValue>({
      type: "worker.files-delete",
      relativePath,
    }, options, { awaitTerminalAfterAbort: true });
    if (value !== null) throw new ProtocolError("Host package-file delete result must be null.");
  }

  async #withUploadPayload(
    bytes: Buffer,
    maximumBytes: number,
    options: WorkerCallOptions | undefined,
    use: (handle: PayloadHandle) => Promise<void>,
  ): Promise<void> {
    if (bytes.byteLength > maximumBytes) throw new RangeError("Worker transport payload exceeds its byte limit.");
    const reservation = this.#reservePayloadHandle();
    let retained = false;
    try {
      const result = await this.#unaryClientCall<JsonValue>({
        type: "worker.payload-allocate",
        length: bytes.byteLength,
      }, options, {
        abandonedResult: async (value) => {
          const allocation = payloadAllocation(value, maximumBytes);
          if (allocation.length !== bytes.byteLength) throw new ProtocolError("Host payload allocation length does not match the request.");
          if (this.#rememberedPayloadIds.has(allocation.handleId) || this.#payloadHandles.has(allocation.handleId)) {
            throw new ProtocolError(`Host reused transport payload handle '${allocation.handleId}'.`);
          }
          await this.#resolveHostPayloadPath(allocation.filePath);
          this.#rememberedPayloadIds.add(allocation.handleId);
          await this.#releaseUntrackedPayload(allocation.handleId);
        },
        releaseTerminalResource: () => { if (!retained) reservation.release(); },
      });
      const allocation = this.#parseHostValue(result, (value) => payloadAllocation(value, maximumBytes));
      if (allocation.length !== bytes.byteLength) throw new ProtocolError("Host payload allocation length does not match the request.");
      const descriptor: DataPayload = { ...allocation, sha256: null };
      const handle = await this.#claimPayload(descriptor, reservation);
      retained = true;
      try {
        await this.#writePayload(handle, bytes, options?.signal);
        await use(handle);
      } finally {
        if (this.#payloadHandles.get(handle.id) === handle) await this.#releasePayload(handle);
      }
    } finally {
      if (!retained) reservation.release();
    }
  }

  #reservePayloadHandle(): ResourceReservation {
    if (this.#openPayloadHandles >= LIMITS.maxPayloadHandles) {
      throw infrastructureError("resource-exhausted", "worker.payload.handle-limit", "The worker transport payload-handle limit was reached.");
    }
    this.#openPayloadHandles += 1;
    return new ResourceReservation(() => {
      if (this.#openPayloadHandles > 0) this.#openPayloadHandles -= 1;
    });
  }

  async #claimPayload(descriptor: DataPayload, reservation: ResourceReservation): Promise<PayloadHandle> {
    if (this.#rememberedPayloadIds.has(descriptor.handleId) || this.#payloadHandles.has(descriptor.handleId)) {
      throw new ProtocolError(`Host reused transport payload handle '${descriptor.handleId}'.`);
    }
    const filePath = await this.#resolveHostPayloadPath(descriptor.filePath);
    const handle: PayloadHandle = {
      id: descriptor.handleId,
      filePath,
      length: descriptor.length,
      sha256: descriptor.sha256,
      reservation,
    };
    this.#payloadHandles.set(handle.id, handle);
    return handle;
  }

  #consumePayload(handle: PayloadHandle): void {
    if (this.#payloadHandles.get(handle.id) !== handle) throw new ProtocolError("Worker upload payload is no longer active.");
    this.#payloadHandles.delete(handle.id);
    this.#rememberedPayloadIds.add(handle.id);
    handle.reservation.release();
  }

  async #readPayload(handle: PayloadHandle, signal?: AbortSignal): Promise<Buffer> {
    signal?.throwIfAborted();
    const file = await open(handle.filePath, "r");
    try {
      const information = await file.stat();
      if (!information.isFile() || information.size !== handle.length) {
        throw new ProtocolError("Host transport payload file length does not match its descriptor.");
      }
      const bytes = Buffer.alloc(handle.length);
      let offset = 0;
      while (offset < bytes.byteLength) {
        signal?.throwIfAborted();
        const { bytesRead } = await file.read(bytes, offset, bytes.byteLength - offset, offset);
        if (bytesRead === 0) throw new ProtocolError("Host transport payload ended before its declared length.");
        offset += bytesRead;
      }
      if (handle.sha256 !== null && sha256(bytes) !== handle.sha256.toLowerCase()) {
        throw new ProtocolError("Host transport payload file hash does not match its descriptor.");
      }
      return bytes;
    } finally {
      await file.close().catch(() => undefined);
    }
  }

  async #writePayload(handle: PayloadHandle, bytes: Buffer, signal?: AbortSignal): Promise<void> {
    signal?.throwIfAborted();
    if (bytes.byteLength !== handle.length) throw new ProtocolError("Worker upload bytes do not match the allocated payload length.");
    const file = await open(handle.filePath, "r+");
    try {
      await file.truncate(0);
      await writeAll(file, bytes, signal);
      await file.sync();
    } finally {
      await file.close().catch(() => undefined);
    }
  }

  async #releasePayload(handle: PayloadHandle): Promise<void> {
    if (this.#payloadHandles.get(handle.id) !== handle) {
      handle.reservation.release();
      return;
    }
    this.#payloadHandles.delete(handle.id);
    this.#rememberedPayloadIds.add(handle.id);
    try {
      if (this.#canSendControl()) await this.#releaseUntrackedPayload(handle.id);
    } finally {
      handle.reservation.release();
    }
  }

  async #releaseUntrackedPayload(handleId: string): Promise<void> {
    const value = await this.#unaryClientCall<JsonValue>({
      type: "worker.payload-release",
      handleId,
    }, undefined, { lane: "control", awaitTerminalAfterAbort: true });
    if (value !== null) throw new ProtocolError("Host payload release result must be null.");
  }

  async #abandonDataResult(value: JsonValue, maximumBytes: number): Promise<void> {
    const result = dataValue(value, maximumBytes);
    if (result.kind === "payload") await this.#abandonPayload(result);
  }

  async #abandonKeysResult(value: JsonValue): Promise<void> {
    const result = dataKeys(value);
    if (result.kind === "payload") await this.#abandonPayload(result);
  }

  async #abandonFileResult(value: JsonValue): Promise<void> {
    if (value !== null) await this.#abandonPayload(dataPayload(value, LIMITS.maxFilePayloadBytes));
  }

  async #abandonPayload(descriptor: DataPayload): Promise<void> {
    if (this.#rememberedPayloadIds.has(descriptor.handleId) || this.#payloadHandles.has(descriptor.handleId)) {
      throw new ProtocolError(`Host reused transport payload handle '${descriptor.handleId}'.`);
    }
    await this.#resolveHostPayloadPath(descriptor.filePath);
    this.#rememberedPayloadIds.add(descriptor.handleId);
    if (this.#canSendControl()) await this.#releaseUntrackedPayload(descriptor.handleId);
  }

  async #releaseAllPayloadsForShutdown(): Promise<void> {
    const handles = [...this.#payloadHandles.values()];
    this.#payloadHandles.clear();
    await Promise.all(handles.map(async (handle) => {
      this.#rememberedPayloadIds.add(handle.id);
      try {
        if (this.#canSendControl()) await this.#releaseUntrackedPayload(handle.id);
      } finally {
        handle.reservation.release();
      }
    }));
  }

  async #resolveHostPayloadPath(value: string): Promise<string> {
    const path = boundedString(value, "Host payload filePath", 4096);
    const root = this.#environment.temporary.resolve("payload");
    const fullPath = resolve(root, path);
    if (!isWithin(root, fullPath)) throw new ProtocolError("Host transport payload file is outside the worker temporary directory.");
    const actualRoot = await realpath(root);
    const actualPath = await realpath(fullPath);
    if (!isWithin(actualRoot, actualPath)) throw new ProtocolError("Host transport payload file traverses outside the worker temporary directory.");
    const information = lstatSync(fullPath);
    if (information.isSymbolicLink() || !information.isFile()) throw new ProtocolError("Host transport payload path must identify a regular file.");
    return actualPath;
  }

  async #resolveHostTemporaryFile(value: string, label: string): Promise<string> {
    const path = boundedString(value, `${label} filePath`, 4096);
    const fullPath = resolve(path);
    if (!isWithin(this.#environment.temporary.rootPath, fullPath)) {
      throw new ProtocolError(`${label} file escaped the worker temporary directory.`);
    }
    const actualRoot = await realpath(this.#environment.temporary.rootPath);
    const actualPath = await realpath(fullPath);
    if (!isWithin(actualRoot, actualPath)) throw new ProtocolError(`${label} file traverses outside the worker temporary directory.`);
    const link = lstatSync(fullPath);
    if (link.isSymbolicLink() || !link.isFile()) throw new ProtocolError(`${label} path must identify a regular file.`);
    return actualPath;
  }

  #unaryClientCall<T>(
    envelope: Readonly<Record<string, unknown>>,
    options?: WorkerCallOptions,
    behavior: ClientCallBehavior = {},
  ): Promise<T> {
    const call = this.#newClientCall("unary", options, behavior);
    const request = { ...envelope, id: call.id } as unknown as WorkerEnvelope;
    this.#startClientCall(call, request, options, behavior.started);
    return call.promise as Promise<T>;
  }

  #streamClientCall<T>(
    envelope: Readonly<Record<string, unknown>>,
    options: WorkerCallOptions | undefined,
    streamIdentity: OutboundStreamIdentity,
  ): AsyncIterable<T> {
    const call = this.#newClientCall("stream", options, {}, streamIdentity);
    const request = { ...envelope, id: call.id } as unknown as WorkerEnvelope;
    this.#startClientCall(call, request, options);
    return call.queue.iterate<T>(() => this.#cancelClientCall(call));
  }

  #newClientCall(
    kind: "unary" | "stream",
    options: WorkerCallOptions | undefined,
    behavior: ClientCallBehavior,
    streamIdentity?: OutboundStreamIdentity,
  ): ClientCall {
    const lane = behavior.lane ?? (this.#environment.operationalOutcomesV1 && kind === "stream" ? "observation" : "ordinary");
    if (!this.#laneAvailable(lane, behavior.migrationPreparationRead ?? false)) {
      throw infrastructureError("unavailable", "rpc.worker.not-active", "The process worker activation is not active.");
    }
    const laneCount = [...this.#clientCalls.values()].filter((call) => call.lane === lane && !call.terminal).length;
    const laneLimit = lane === "control"
      ? LIMITS.maxControlCalls
      : lane === "logging"
        ? LIMITS.maxLoggingCalls
        : lane === "observation" ? MAX_OUTBOUND_OBSERVATIONS : LIMITS.maxOutboundCalls;
    if (laneCount >= laneLimit) {
      throw infrastructureError(
        "resource-exhausted",
        lane === "control" ? "rpc.worker.control-call-limit" : lane === "logging" ? "rpc.worker.logging-call-limit" : lane === "observation" ? "rpc.worker.observation-limit" : "rpc.worker.call-limit",
        lane === "control" ? "The worker RPC control-call limit was reached." : lane === "logging" ? "The worker logging-call limit was reached." : lane === "observation" ? "The worker RPC observation limit was reached." : "The worker RPC call limit was reached.",
      );
    }
    options?.signal?.throwIfAborted();
    if (this.#clientId >= Number.MAX_SAFE_INTEGER) {
      throw infrastructureError("resource-exhausted", "rpc.worker.message-id-limit", "The worker exhausted its outbound message identifier space.");
    }
    const id = `w${++this.#clientId}`;
    let resolvePromise: (value: JsonValue) => void = () => undefined;
    let rejectPromise: (error: unknown) => void = () => undefined;
    let resolveTerminal: () => void = () => undefined;
    const promise = new Promise<JsonValue>((resolveValue, reject) => {
      resolvePromise = resolveValue;
      rejectPromise = reject;
    });
    const terminalPromise = new Promise<void>((resolveValue) => { resolveTerminal = resolveValue; });
    if (kind === "stream") void promise.catch(() => undefined);
    const call: ClientCall = {
      id,
      kind,
      lane,
      streamIdentity,
      queue: new AsyncQueue(LIMITS.maxStreamQueueMessages, LIMITS.maxStreamQueueBytes),
      promise,
      resolve: resolvePromise,
      reject: rejectPromise,
      cancelled: false,
      terminal: false,
      awaitTerminalAfterAbort: behavior.awaitTerminalAfterAbort ?? false,
      abandonedResult: behavior.abandonedResult,
      hostError: behavior.hostError,
      releaseTerminalResource: behavior.releaseTerminalResource,
      terminalPromise,
      resolveTerminal,
      cleanup: () => undefined,
    };
    return call;
  }

  #startClientCall(
    call: ClientCall,
    request: WorkerEnvelope,
    options: WorkerCallOptions | undefined,
    started?: () => void,
  ): void {
    let write: Promise<void>;
    try {
      write = this.#writer.write(request);
    } catch (error) {
      if (error instanceof TransportResourceError) {
        throw infrastructureError(
          "resource-exhausted",
          error.message.includes("queue") ? "rpc.worker.write-queue-limit" : "rpc.worker.request-limit",
          error.message.includes("queue")
            ? "The worker protocol write queue is full."
            : "The worker RPC request exceeds the negotiated transport limits.",
        );
      }
      throw error;
    }
    const onAbort = (): void => this.#cancelClientCall(call);
    options?.signal?.addEventListener("abort", onAbort, { once: true });
    call.cleanup = () => options?.signal?.removeEventListener("abort", onAbort);
    this.#clientCalls.set(call.id, call);
    started?.();
    void write.catch((error: unknown) => {
      if (this.#clientCalls.get(call.id) === call) {
        this.#clientCalls.delete(call.id);
        call.terminal = true;
        call.cleanup();
        call.reject(error);
        call.queue.close(error);
        call.releaseTerminalResource?.();
        call.resolveTerminal();
      }
      this.#fail(error);
    });
  }

  #cancelClientCall(call: ClientCall): void {
    if (call.terminal || call.cancelled) return;
    call.cancelled = true;
    call.cleanup();
    if (!call.awaitTerminalAfterAbort && call.releaseTerminalResource === undefined) {
      const error = abortError();
      call.reject(error);
      call.queue.close(error);
    }
    void this.#writer.write({ type: "worker.cancel", id: call.id }).catch((error: unknown) => this.#fail(error));
  }

  #laneAvailable(lane: ClientCallLane, migrationPreparationRead: boolean): boolean {
    if (migrationPreparationRead) {
      return lane === "ordinary" && this.#phase === "preparing-storage-migrations";
    }
    if (lane === "control") return this.#canSendControl();
    if (lane === "logging") {
      return !this.#loggingClosed && this.#phase !== "waiting-hello" && this.#phase !== "stopped" && this.#phase !== "faulted";
    }
    return this.#phase === "activating" || this.#phase === "active";
  }

  #inboundLimitReached(observation: boolean): boolean {
    if (!this.#environment.operationalOutcomesV1) return this.#hostInvocations.size + this.#hostContributions.size >= LIMITS.maxInboundCalls;
    const count = [...this.#hostInvocations.values()].filter(call => (call.kind === "server-stream") === observation).length;
    return observation ? count >= MAX_INBOUND_OBSERVATIONS : count + this.#hostContributions.size >= LIMITS.maxInboundCalls;
  }

  #canSendControl(): boolean {
    return this.#phase === "preparing-storage-migrations"
      || this.#phase === "activating"
      || this.#phase === "active"
      || this.#phase === "stopping";
  }

  #isStopping(): boolean {
    return this.#phase === "stopping" || this.#phase === "stopped" || this.#phase === "faulted";
  }

  #isExpectedStop(error: unknown): boolean {
    return this.#isStopping() && (this.#work.signal.aborted || isAbortError(error));
  }

  #fail(error: unknown): void {
    if (this.#phase === "stopped" || this.#phase === "faulted") return;
    const failure = error instanceof Error ? error : new ProtocolError(String(error));
    this.#phase = "faulted";
    this.#work.abort(failure);
    this.#lifetime.abort(failure);
    for (const invocation of this.#hostInvocations.values()) invocation.controller.abort(failure);
    for (const contribution of this.#hostContributions.values()) contribution.controller.abort(failure);
    for (const scope of this.#scopes.values()) {
      scope.closing = true;
      scope.cancelDeadline?.();
      scope.controller.abort(failure);
      for (const file of scope.contentFiles) file.revoke();
    }
    for (const call of this.#clientCalls.values()) {
      call.terminal = true;
      call.cleanup();
      call.reject(failure);
      call.queue.close(failure);
      call.releaseTerminalResource?.();
      call.resolveTerminal();
    }
    this.#clientCalls.clear();
    safeStderr(`${SUNDER_WORKER_PROTOCOL} protocol failure`, failure);
    process.exitCode = 70;
    this.#rejectRun?.(failure);
  }
}

class ProtocolWriter {
  #queued = 0;
  #queuedBytes = 0;
  #tail = Promise.resolve();

  public write(envelope: WorkerEnvelope): Promise<void> {
    const frame = encodeFrame(envelope, LIMITS);
    if (this.#queued >= LIMITS.maxWriteQueue || frame.byteLength > LIMITS.maxWriteQueueBytes - this.#queuedBytes) {
      throw new TransportResourceError("Worker protocol write queue exceeded its count or byte bound.");
    }
    this.#queued += 1;
    this.#queuedBytes += frame.byteLength;
    const write = this.#tail.then(async () => {
      this.#queued -= 1;
      this.#queuedBytes -= frame.byteLength;
      if (!process.stdout.write(frame)) await once(process.stdout, "drain");
    });
    this.#tail = write.catch(() => undefined);
    return write;
  }

  public flush(): Promise<void> {
    return this.#tail;
  }
}

type AsyncQueueLimit = "count" | "bytes" | "count+bytes";

interface AsyncQueueRejection {
  readonly limit: AsyncQueueLimit;
  readonly queuedMessages: number;
  readonly queuedBytes: number;
  readonly maximumMessages: number;
  readonly maximumBytes: number;
  readonly incomingBytes: number;
}

class AsyncQueue {
  readonly #values: Array<Readonly<{ value: JsonValue; bytes: number }>> = [];
  readonly #waiters: Array<{ resolve: (value: IteratorResult<JsonValue>) => void; reject: (error: unknown) => void }> = [];
  readonly #maximumMessages: number;
  readonly #maximumBytes: number;
  #queuedBytes = 0;
  #closed = false;
  #error: unknown;

  public constructor(maximumMessages: number, maximumBytes: number) {
    this.#maximumMessages = maximumMessages;
    this.#maximumBytes = maximumBytes;
  }

  public push(value: JsonValue, bytes: number): AsyncQueueRejection | null {
    if (this.#closed) throw new ProtocolError("Received a stream event after terminal completion.");
    const waiter = this.#waiters.shift();
    if (waiter !== undefined) {
      waiter.resolve({ done: false, value });
      return null;
    }
    const countExceeded = this.#values.length >= this.#maximumMessages;
    const bytesExceeded = bytes > this.#maximumBytes - this.#queuedBytes;
    if (countExceeded || bytesExceeded) {
      return Object.freeze({
        limit: countExceeded && bytesExceeded ? "count+bytes" : countExceeded ? "count" : "bytes",
        queuedMessages: this.#values.length,
        queuedBytes: this.#queuedBytes,
        maximumMessages: this.#maximumMessages,
        maximumBytes: this.#maximumBytes,
        incomingBytes: bytes,
      });
    }
    this.#values.push({ value, bytes });
    this.#queuedBytes += bytes;
    return null;
  }

  public close(error?: unknown): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#error = error;
    if (error !== undefined) {
      this.#values.length = 0;
      this.#queuedBytes = 0;
    }
    for (const waiter of this.#waiters.splice(0)) {
      if (error === undefined) waiter.resolve({ done: true, value: undefined });
      else waiter.reject(error);
    }
  }

  public iterate<T>(cancel: () => void): AsyncIterable<T> {
    const queue = this;
    return {
      async *[Symbol.asyncIterator](): AsyncIterator<T> {
        try {
          while (true) {
            const result = await queue.#next();
            if (result.done) return;
            yield result.value as T;
          }
        } finally {
          cancel();
        }
      },
    };
  }

  #next(): Promise<IteratorResult<JsonValue>> {
    const item = this.#values.shift();
    if (item !== undefined) {
      this.#queuedBytes -= item.bytes;
      return Promise.resolve({ done: false, value: item.value });
    }
    if (this.#closed) {
      return this.#error === undefined
        ? Promise.resolve({ done: true, value: undefined })
        : Promise.reject(this.#error);
    }
    return new Promise((resolvePromise, reject) => this.#waiters.push({ resolve: resolvePromise, reject }));
  }
}

class RememberedIds {
  readonly #ids = new Set<string>();
  readonly #order: string[] = [];

  public add(id: string): void {
    if (this.#ids.has(id)) throw new ProtocolError(`Protocol peer reused identifier '${id}'.`);
    this.#ids.add(id);
    this.#order.push(id);
    while (this.#order.length > LIMITS.maxRememberedIds) {
      const removed = this.#order.shift();
      if (removed !== undefined) this.#ids.delete(removed);
    }
  }

  public has(id: string): boolean {
    return this.#ids.has(id);
  }
}

class ResourceReservation {
  #release: (() => void) | undefined;

  public constructor(release: () => void) {
    this.#release = release;
  }

  public release(): void {
    const release = this.#release;
    this.#release = undefined;
    release?.();
  }
}

class OpenContentFile {
  readonly api: RpcContentFile;
  readonly #stream: Readable;
  readonly #release: () => Promise<void>;
  #releasePromise: Promise<void> | undefined;
  #revoked = false;

  public constructor(
    stream: Readable,
    filePath: string,
    release: () => Promise<void>,
    reportFailure: (error: unknown) => void,
  ) {
    this.#stream = stream;
    this.#release = release;
    stream.on("error", () => undefined);
    stream.once("close", () => {
      void this.#releaseOnce().catch(reportFailure);
    });
    this.api = Object.freeze({
      filePath,
      stream,
      discard: () => this.#discard(),
    });
  }

  public revoke(): void {
    if (this.#revoked) return;
    this.#revoked = true;
    this.#stream.destroy(abortError());
  }

  async #discard(): Promise<void> {
    if (!this.#stream.destroyed) this.#stream.destroy();
    await this.#releaseOnce();
  }

  #releaseOnce(): Promise<void> {
    this.#releasePromise ??= this.#release();
    return this.#releasePromise;
  }
}

function captureEnvironment(): WorkerEnvironment {
  const protocol = requiredEnvironment("SUNDER_WORKER_PROTOCOL", 64);
  if (protocol !== SUNDER_WORKER_PROTOCOL) throw new Error(`SUNDER_WORKER_PROTOCOL must select ${SUNDER_WORKER_PROTOCOL}.`);
  const packageId = requiredEnvironment("SUNDER_PACKAGE_ID", 256);
  if (!isPackageId(packageId)) throw new Error("SUNDER_PACKAGE_ID is not a canonical package identifier.");
  const packageVersion = requiredEnvironment("SUNDER_PACKAGE_VERSION", SEMANTIC_VERSION_MAXIMUM_LENGTH);
  if (!isSemanticVersion(packageVersion)) throw new Error("SUNDER_PACKAGE_VERSION is not strict semantic versioning.");
  const activationId = requiredEnvironment("SUNDER_ACTIVATION_ID", 64);
  if (!/^[0-9a-f]{32}$/u.test(activationId)) throw new Error("SUNDER_ACTIVATION_ID is not a canonical activation identifier.");
  const sessionId = requiredEnvironment("SUNDER_SESSION_ID", 256);
  if (!SAFE_ID.test(sessionId)) throw new Error("SUNDER_SESSION_ID contains unsupported characters.");
  const content = localPath(requiredPathEnvironment("SUNDER_PACKAGE_CONTENT_ROOT"));
  const temporary = localPath(requiredPathEnvironment("SUNDER_WORKER_TEMP_ROOT"));
  const workspace = bindableLocalPath();
  const rpcExtendedDeadlineV1 = optionalEnvironmentFlag(RPC_EXTENDED_DEADLINE_ENVIRONMENT);
  const operationalOutcomesV1 = optionalEnvironmentFlag(OPERATIONAL_OUTCOMES_ENVIRONMENT);
  const identities = [content.rootPath, temporary.rootPath]
    .map((path) => process.platform === "win32" ? path.toLowerCase() : path);
  if (new Set(identities).size !== identities.length) {
    throw new Error("Worker V1 content and temporary roots must be distinct.");
  }
  return Object.freeze({
    protocol,
    packageId,
    packageVersion,
    activationId,
    sessionId,
    content,
    temporary,
    workspace,
    rpcExtendedDeadlineV1,
    operationalOutcomesV1,
  });
}

function optionalEnvironmentFlag(name: string): boolean {
  const value = process.env[name];
  if (value === undefined) return false;
  if (value === "1") return true;
  throw new Error(`${name} must be absent or exactly '1'.`);
}

function requiredEnvironment(name: string, maximum: number): string {
  const value = process.env[name];
  if (value === undefined || value.length === 0 || value.length > maximum) {
    throw new Error(`${name} must be a non-empty bounded value.`);
  }
  return value;
}

function requiredPathEnvironment(name: string): string {
  const value = requiredEnvironment(name, 4096);
  if (value.includes("\0") || !isAbsolute(value)) throw new Error(`${name} must be an existing absolute directory.`);
  const path = resolve(value);
  try {
    if (!lstatSync(path).isDirectory()) throw new Error();
  } catch {
    throw new Error(`${name} must be an existing absolute directory.`);
  }
  return path;
}

function localPath(rootPath: string): WorkerLocalPath {
  return Object.freeze({
    rootPath,
    resolve: (relativePath: string): string => {
      const portable = validateRelativePath(relativePath);
      const result = resolve(rootPath, ...portable.split("/"));
      if (!isWithin(rootPath, result) || result === rootPath) throw new RangeError("Package local path escaped its root.");
      ensureNoObservedLinks(rootPath, portable);
      return result;
    },
  });
}

function bindableLocalPath(): BindableWorkerLocalPath {
  let boundRoot: string | undefined;
  const requireRoot = (): string => {
    if (boundRoot === undefined) {
      throw new Error("The persistent workspace is unavailable until the Host activates this worker generation.");
    }
    return boundRoot;
  };
  return Object.freeze({
    get rootPath(): string {
      return requireRoot();
    },
    resolve: (relativePath: string): string => {
      const rootPath = requireRoot();
      const portable = validateRelativePath(relativePath);
      const result = resolve(rootPath, ...portable.split("/"));
      if (!isWithin(rootPath, result) || result === rootPath) throw new RangeError("Package local path escaped its root.");
      ensureNoObservedLinks(rootPath, portable);
      return result;
    },
    bind: (rootPath: string, excludedRoots: readonly string[]): void => {
      if (boundRoot !== undefined) throw new ProtocolError("The persistent workspace is already bound.");
      if (rootPath.includes("\0") || !isAbsolute(rootPath)) {
        throw new ProtocolError("host.activate workspaceRootPath must identify an existing absolute directory.");
      }
      const canonicalPath = resolve(rootPath);
      try {
        const information = lstatSync(canonicalPath);
        if (!information.isDirectory() || information.isSymbolicLink()) throw new Error();
      } catch {
        throw new ProtocolError("host.activate workspaceRootPath must identify an existing non-link directory.");
      }
      const identity = process.platform === "win32" ? canonicalPath.toLowerCase() : canonicalPath;
      if (excludedRoots.some((path) => (process.platform === "win32" ? path.toLowerCase() : path) === identity)) {
        throw new ProtocolError("Worker V1 content, temporary, and persistent workspace roots must be distinct.");
      }
      boundRoot = canonicalPath;
    },
  });
}

function ensureNoObservedLinks(rootPath: string, relativePath: string): void {
  let current = rootPath;
  for (const segment of ["", ...relativePath.split("/")]) {
    if (segment !== "") current = resolve(current, segment);
    try {
      if (lstatSync(current).isSymbolicLink()) throw new RangeError("Package local paths must not traverse symbolic links.");
    } catch (error) {
      if (isNodeError(error) && error.code === "ENOENT") continue;
      throw error;
    }
  }
}

function normalizeWorkerOptions(options: WorkerOptions): NormalizedWorkerOptions {
  if (!Array.isArray(options.providers)) throw new TypeError("Worker providers must be an array.");
  const providers: NormalizedProviderRegistration[] = [];
  const ids = new Set<string>();
  for (const provider of options.providers) {
    const normalized = normalizeProvider(provider);
    if (!ids.add(normalized.providerId)) throw new TypeError(`Provider '${normalized.providerId}' is registered more than once.`);
    providers.push(normalized);
  }
  const callbackHandlers = options.callbackHandlers === undefined
    ? []
    : normalizeCallbackHandlers(options.callbackHandlers);
  const authHandler = options.authHandler === undefined || options.authHandler === null
    ? null
    : normalizeAuthHandler(options.authHandler);
  const callbackNames = [
    "onCandidateStarted",
    "onGenerationCommitted",
    "onBeforeStorageMigrations",
    "onActivated",
    "onShutdown",
  ] as const;
  for (const name of callbackNames) {
    if (options[name] !== undefined && typeof options[name] !== "function") throw new TypeError(`Worker ${name} must be a function.`);
  }
  return Object.freeze({
    providers: Object.freeze(providers),
    settingsSchema: options.settingsSchema === undefined || options.settingsSchema === null
      ? null
      : normalizeSettingsSchema(options.settingsSchema),
    stateMigrations: normalizeStorageMigrations(options.stateMigrations, "stateMigrations"),
    secretMigrations: normalizeStorageMigrations(options.secretMigrations, "secretMigrations"),
    callbackHandlers: Object.freeze(callbackHandlers),
    authHandler,
    onCandidateStarted: options.onCandidateStarted,
    onGenerationCommitted: options.onGenerationCommitted,
    onBeforeStorageMigrations: options.onBeforeStorageMigrations,
    onActivated: options.onActivated,
    onShutdown: options.onShutdown,
  });
}

function normalizeStorageMigrations(
  value: readonly PackageStorageKeyMigration[] | undefined,
  label: string,
): readonly PackageStorageKeyMigration[] {
  if (value === undefined) return Object.freeze([]);
  if (!Array.isArray(value) || value.length > 32) {
    throw new TypeError(`Worker ${label} must be an array containing at most 32 rules.`);
  }
  return Object.freeze(value.map((migration, index) => normalizeStorageMigration(migration, `${label}[${index}]`)));
}

function normalizeStorageMigration(value: PackageStorageKeyMigration, label: string): PackageStorageKeyMigration {
  const migration = plainObject(value, `Worker ${label}`);
  const kind = migration.kind;
  if (kind === "exact") {
    optionOnly(migration, label, "kind", "legacyKey", "destinationKey");
    const legacyKey = migrationLegacyPart(migration.legacyKey, `${label}.legacyKey`, false);
    const destinationKey = validateStorageKey(migration.destinationKey);
    if (legacyKey === destinationKey) throw new TypeError(`Worker ${label} must change the physical key.`);
    return Object.freeze({ kind, legacyKey, destinationKey });
  }
  if (kind === "opaque-id" || kind === "opaque-id-json" || kind === "opaque-id-json-case-insensitive") {
    const hasIdentity = kind !== "opaque-id";
    optionOnly(
      migration,
      label,
      "kind",
      "legacyPrefix",
      "legacySuffix",
      "destinationPrefix",
      "destinationVersion",
      ...(hasIdentity ? ["identityProperty"] : []),
    );
    const legacyPrefix = migrationLegacyPart(migration.legacyPrefix, `${label}.legacyPrefix`, false);
    const legacySuffix = migrationLegacyPart(migration.legacySuffix, `${label}.legacySuffix`, true);
    const destinationPrefix = validateStorageKey(migration.destinationPrefix);
    const destinationVersion = migration.destinationVersion;
    if (typeof destinationVersion !== "number"
      || !Number.isSafeInteger(destinationVersion)
      || destinationVersion < 1
      || destinationVersion > 0x7fffffff) {
      throw new RangeError(`Worker ${label}.destinationVersion must be a positive signed 32-bit integer.`);
    }
    createPackageStorageKey(destinationPrefix, destinationVersion, "validation");
    if (!hasIdentity) {
      return Object.freeze({ kind, legacyPrefix, legacySuffix, destinationPrefix, destinationVersion });
    }
    const identityProperty = migrationLegacyPart(migration.identityProperty, `${label}.identityProperty`, false);
    return Object.freeze({
      kind,
      legacyPrefix,
      legacySuffix,
      destinationPrefix,
      destinationVersion,
      identityProperty,
    });
  }
  if (kind === "dynamic-cleanup") {
    optionOnly(migration, label, "kind", "resolve");
    if (typeof migration.resolve !== "function") throw new TypeError(`Worker ${label}.resolve must be a function.`);
    const resolveMigration = migration.resolve;
    return Object.freeze({
      kind,
      resolve: (legacyKey: string) => resolveMigration.call(value, legacyKey),
    });
  }
  throw new TypeError(`Worker ${label}.kind is unsupported.`);
}

function storageMigrationPlans(
  stateMigrations: readonly PackageStorageKeyMigration[],
  secretMigrations: readonly PackageStorageKeyMigration[],
): readonly WorkerStorageMigrationPlanWire[] {
  const plans: WorkerStorageMigrationPlanWire[] = [];
  if (stateMigrations.length > 0) {
    plans.push(Object.freeze({ store: "state", rules: Object.freeze(stateMigrations.map(migrationDeclaration)) }));
  }
  if (secretMigrations.length > 0) {
    plans.push(Object.freeze({ store: "secrets", rules: Object.freeze(secretMigrations.map(migrationDeclaration)) }));
  }
  return Object.freeze(plans);
}

function migrationDeclaration(migration: PackageStorageKeyMigration): WorkerStorageMigrationRuleWire {
  switch (migration.kind) {
    case "exact":
      return Object.freeze({ kind: migration.kind, legacyKey: migration.legacyKey, destinationKey: migration.destinationKey });
    case "opaque-id":
      return Object.freeze({
        kind: migration.kind,
        legacyPrefix: migration.legacyPrefix,
        legacySuffix: migration.legacySuffix,
        destinationPrefix: migration.destinationPrefix,
        destinationVersion: migration.destinationVersion,
      });
    case "opaque-id-json":
    case "opaque-id-json-case-insensitive":
      return Object.freeze({
        kind: migration.kind,
        legacyPrefix: migration.legacyPrefix,
        legacySuffix: migration.legacySuffix,
        destinationPrefix: migration.destinationPrefix,
        destinationVersion: migration.destinationVersion,
        identityProperty: migration.identityProperty,
      });
    case "dynamic-cleanup":
      return Object.freeze({ kind: migration.kind });
  }
}

function migrationApplyRule(
  migration: PackageStorageKeyMigration,
  physicalKeys: readonly string[],
): JsonValue {
  if (migration.kind !== "dynamic-cleanup") return migrationDeclaration(migration) as JsonValue;
  return {
    kind: migration.kind,
    actions: physicalKeys.map((key) => migrationAction(key, migration.resolve(key))),
  };
}

function migrationAction(key: string, value: PackageStorageKeyMigrationAction): JsonValue {
  const action = plainObject(value, `Dynamic storage migration action for '${key}'`);
  if (action.kind === "no-match" || action.kind === "delete") {
    optionOnly(action, `dynamic action for '${key}'`, "kind");
    return { key, kind: action.kind, destinationKey: null, precedence: 0 };
  }
  if (action.kind === "rewrite") {
    optionOnly(action, `dynamic action for '${key}'`, "kind", "destinationKey", "precedence");
    const destinationKey = validateStorageKey(action.destinationKey);
    const precedence = action.precedence;
    if (typeof precedence !== "number"
      || !Number.isSafeInteger(precedence)
      || precedence < 0
      || precedence > 0x7fffffff) {
      throw new RangeError("Dynamic storage migration precedence must be a non-negative signed 32-bit integer.");
    }
    return { key, kind: action.kind, destinationKey, precedence };
  }
  throw new TypeError(`Dynamic storage migration resolver returned an unsupported action for '${key}'.`);
}

function migrationPhysicalKeys(value: JsonValue): readonly string[] {
  const root = objectValue(value, "Host migration key snapshot");
  only(root, "keys");
  if (!Array.isArray(root.keys) || root.keys.length > 2048) {
    throw new ProtocolError("Host migration key snapshot must contain at most 2048 keys.");
  }
  let previous: string | undefined;
  for (const key of root.keys) {
    if (typeof key !== "string" || key.length === 0 || key.length > 4096 || !isWellFormed(key)
      || previous !== undefined && ordinal(previous, key) >= 0) {
      throw new ProtocolError("Host migration key snapshot must be bounded, unique, and ordinally sorted.");
    }
    previous = key;
  }
  return Object.freeze([...root.keys] as string[]);
}

function migrationLegacyPart(value: unknown, label: string, allowEmpty: boolean): string {
  const text = validateStorageValue(value);
  if (!allowEmpty && text.length === 0) throw new TypeError(`Worker ${label} must not be empty.`);
  return text;
}

function optionOnly(root: Readonly<Record<string, unknown>>, label: string, ...allowed: readonly string[]): void {
  const names = new Set(allowed);
  for (const name of Object.keys(root)) {
    if (!names.has(name)) throw new TypeError(`Worker ${label} contains unsupported property '${name}'.`);
  }
  if (Object.keys(root).length !== allowed.length || allowed.some((name) => !Object.hasOwn(root, name))) {
    throw new TypeError(`Worker ${label} must contain exactly ${allowed.join(", ")}.`);
  }
}

function normalizeProvider(provider: RpcProviderRegistration): NormalizedProviderRegistration {
  if (provider === null || typeof provider !== "object") throw new TypeError("Worker provider registration must be an object.");
  validateProviderIdentity(provider);
  if (provider.handler === null || typeof provider.handler !== "object"
    || typeof provider.handler.invokeUnary !== "function"
    || typeof provider.handler.invokeServerStream !== "function") {
    throw new TypeError("Worker provider handler is invalid.");
  }
  const declaredErrors: Record<string, Readonly<Record<string, readonly string[]>>> = Object.create(null) as Record<string, Readonly<Record<string, readonly string[]>>>;
  if (provider.declaredErrors === null || typeof provider.declaredErrors !== "object" || Array.isArray(provider.declaredErrors)) {
    throw new TypeError("Worker provider declaredErrors must be an object.");
  }
  for (const [serviceId, methodsValue] of Object.entries(provider.declaredErrors)) {
    boundedString(serviceId, "Declared-error service id", 128);
    if (methodsValue === null || typeof methodsValue !== "object" || Array.isArray(methodsValue)) {
      throw new TypeError("Worker provider declared-error service must be an object.");
    }
    const methods: Record<string, readonly string[]> = Object.create(null) as Record<string, readonly string[]>;
    for (const [methodId, errorsValue] of Object.entries(methodsValue)) {
      boundedString(methodId, "Declared-error method id", 128);
      if (!Array.isArray(errorsValue)) throw new TypeError("Worker provider declared errors must be arrays.");
      const errors = errorsValue.map((code) => {
        if (typeof code !== "string" || code.length === 0 || code.length > 128 || !SAFE_ERROR_CODE.test(code)) {
          throw new TypeError("Worker provider declared error code is invalid.");
        }
        return code;
      });
      if (new Set(errors).size !== errors.length) throw new TypeError("Worker provider declared errors must be unique.");
      methods[methodId] = Object.freeze(errors);
    }
    declaredErrors[serviceId] = Object.freeze(methods);
  }
  const sourceHandler = provider.handler;
  const invokeUnary = sourceHandler.invokeUnary;
  const invokeServerStream = sourceHandler.invokeServerStream;
  const handler = Object.freeze({
    invokeUnary: (...args: Parameters<typeof invokeUnary>) => invokeUnary.call(sourceHandler, ...args),
    invokeServerStream: (...args: Parameters<typeof invokeServerStream>) => invokeServerStream.call(sourceHandler, ...args),
  });
  return Object.freeze({
    providerId: provider.providerId,
    contractId: provider.contractId,
    contractVersion: provider.contractVersion,
    descriptorSha256: provider.descriptorSha256,
    abiSha256: provider.abiSha256,
    declaredErrors: Object.freeze(declaredErrors),
    handler,
  });
}

function normalizeCallbackHandlers(handlers: readonly WorkerCallbackHandler[]): NormalizedCallbackHandler[] {
  if (!Array.isArray(handlers) || handlers.length > 32) {
    throw new TypeError("Worker callbackHandlers must be an array containing at most 32 handlers.");
  }
  const ids = new Set<string>();
  const output = handlers.map((handler) => {
    if (handler === null || typeof handler !== "object") throw new TypeError("Worker callback handler must be an object.");
    const callbackHandlerId = requiredText(handler.callbackHandlerId, "Callback handler id", 128);
    if (!SAFE_ERROR_CODE.test(callbackHandlerId)) throw new TypeError("Callback handler id must be an ASCII token.");
    if (callbackHandlerId.toLowerCase() === "auth") {
      throw new TypeError("The reserved 'auth' callback handler id must be registered through authHandler.");
    }
    const folded = callbackHandlerId.toLowerCase();
    if (!ids.add(folded)) throw new TypeError(`Worker callback handler '${callbackHandlerId}' is registered more than once.`);
    if (typeof handler.startCallback !== "function"
      || typeof handler.completeCallback !== "function"
      || typeof handler.cancelCallback !== "function") {
      throw new TypeError(`Worker callback handler '${callbackHandlerId}' is invalid.`);
    }
    const startCallback = handler.startCallback;
    const completeCallback = handler.completeCallback;
    const cancelCallback = handler.cancelCallback;
    return Object.freeze({
      callbackHandlerId,
      startCallback: (...args: Parameters<typeof startCallback>) => startCallback.call(handler, ...args),
      completeCallback: (...args: Parameters<typeof completeCallback>) => completeCallback.call(handler, ...args),
      cancelCallback: (...args: Parameters<typeof cancelCallback>) => cancelCallback.call(handler, ...args),
    });
  });
  output.sort((left, right) => ordinal(left.callbackHandlerId, right.callbackHandlerId));
  return output;
}

function normalizeAuthHandler(handler: WorkerAuthHandler): NormalizedAuthHandler {
  if (handler === null || typeof handler !== "object"
    || typeof handler.getStatus !== "function"
    || typeof handler.startAuthorization !== "function"
    || typeof handler.completeAuthorization !== "function"
    || typeof handler.disconnect !== "function"
    || typeof handler.cancelCallback !== "function") {
    throw new TypeError("Worker authHandler is invalid.");
  }
  const getStatus = handler.getStatus;
  const startAuthorization = handler.startAuthorization;
  const completeAuthorization = handler.completeAuthorization;
  const disconnect = handler.disconnect;
  const cancelCallback = handler.cancelCallback;
  return Object.freeze({
    getStatus: (...args: Parameters<typeof getStatus>) => getStatus.call(handler, ...args),
    startAuthorization: (...args: Parameters<typeof startAuthorization>) => startAuthorization.call(handler, ...args),
    completeAuthorization: (...args: Parameters<typeof completeAuthorization>) => completeAuthorization.call(handler, ...args),
    disconnect: (...args: Parameters<typeof disconnect>) => disconnect.call(handler, ...args),
    cancelCallback: (...args: Parameters<typeof cancelCallback>) => cancelCallback.call(handler, ...args),
  });
}

function normalizeSettingsSchema(schema: PackageSettingsSchema): WorkerSettingsSchemaWire {
  if (schema === null || typeof schema !== "object" || !Array.isArray(schema.sections) || schema.sections.length === 0) {
    throw new TypeError("A worker settings schema must contain at least one section.");
  }
  const sections = [] as Array<WorkerSettingsSchemaWire["sections"][number]>;
  const sectionIds = new Set<string>();
  const fieldKeys = new Set<string>();
  for (const section of schema.sections) {
    if (section === null || typeof section !== "object" || !Array.isArray(section.fields) || section.fields.length === 0) {
      throw new TypeError("A worker settings section must contain at least one field.");
    }
    const sectionId = validateStorageKey(requiredText(section.sectionId, "Settings section id"));
    if (!sectionIds.add(sectionId)) throw new TypeError(`Settings section '${sectionId}' is declared more than once.`);
    const fields = section.fields.map((field: PackageSettingsField) => {
      if (field === null || typeof field !== "object") throw new TypeError("Worker settings field must be an object.");
      const key = validateStorageKey(requiredText(field.key, "Settings field key"));
      if (!fieldKeys.add(key)) throw new TypeError(`Settings field '${key}' is declared more than once.`);
      if (field.kind !== "text" && field.kind !== "secret" && field.kind !== "boolean" && field.kind !== "select") {
        throw new TypeError(`Settings field '${key}' has an invalid kind.`);
      }
      const options = [...(field.options ?? [])].map((option) => {
        if (option === null || typeof option !== "object") throw new TypeError(`Settings field '${key}' has an invalid option.`);
        return Object.freeze({
          value: validateStorageValue(requiredText(option.value, `Settings field '${key}' option value`)),
          label: requiredText(option.label, `Settings field '${key}' option label`),
        });
      });
      if (new Set(options.map((option) => option.value)).size !== options.length) {
        throw new TypeError(`Settings field '${key}' has duplicate option values.`);
      }
      const defaultValue = nullableText(field.defaultValue, `Settings field '${key}' default`);
      if (defaultValue !== null) validateStorageValue(defaultValue);
      if (field.kind === "secret" && defaultValue !== null) throw new TypeError(`Secret setting '${key}' must not declare a default.`);
      if (field.kind === "boolean" && defaultValue !== null && !/^(?:true|false)$/iu.test(defaultValue)) {
        throw new TypeError(`Boolean setting '${key}' has an invalid default.`);
      }
      if (field.kind === "select") {
        if (options.length === 0) throw new TypeError(`Select setting '${key}' must declare options.`);
        if (defaultValue !== null && !options.some((option) => option.value === defaultValue)) {
          throw new TypeError(`Select setting '${key}' default is not a declared option.`);
        }
      } else if (options.length !== 0) {
        throw new TypeError(`Non-select setting '${key}' must not declare options.`);
      }
      return Object.freeze({
        key,
        label: requiredText(field.label, `Settings field '${key}' label`),
        kind: field.kind,
        description: nullableText(field.description, `Settings field '${key}' description`),
        isRequired: field.isRequired ?? false,
        placeholder: nullableText(field.placeholder, `Settings field '${key}' placeholder`),
        defaultValue,
        options: Object.freeze(options),
      });
    });
    if (fields.some((field: WorkerSettingsSchemaWire["sections"][number]["fields"][number]) => typeof field.isRequired !== "boolean")) {
      throw new TypeError("Settings isRequired must be Boolean.");
    }
    sections.push(Object.freeze({
      sectionId,
      title: requiredText(section.title, `Settings section '${sectionId}' title`),
      description: nullableText(section.description, `Settings section '${sectionId}' description`),
      fields: Object.freeze(fields),
    }));
  }
  return Object.freeze({
    summary: nullableText(schema.summary, "Settings summary"),
    sections: Object.freeze(sections),
  });
}

function normalizeEventLog(entry: WorkerEventLogEntry): NormalizedLogEntry {
  if (entry === null || typeof entry !== "object") throw new TypeError("Worker event log entry must be an object.");
  return Object.freeze({
    channel: "event",
    level: logLevel(entry.level),
    category: null,
    eventId: 0,
    eventName: requiredText(entry.eventName, "Worker event name", 256),
    message: boundedLogMessage(entry.message),
    attributes: normalizeLogAttributes(entry.attributes),
    exceptions: normalizeLogExceptions(entry.error),
  });
}

function normalizeConventionalLog(
  category: string,
  level: PackageLogLevel,
  message: string,
  details?: WorkerLogDetails,
): NormalizedLogEntry {
  const eventId = details?.eventId ?? 0;
  if (!Number.isSafeInteger(eventId) || eventId < -0x80000000 || eventId > 0x7fffffff) {
    throw new RangeError("Worker logger eventId must be a signed 32-bit integer.");
  }
  return Object.freeze({
    channel: "logger",
    level: logLevel(level),
    category,
    eventId,
    eventName: details?.eventName === undefined || details.eventName === null ? null : truncate(String(details.eventName), 256),
    message: boundedLogMessage(message),
    attributes: normalizeLogAttributes(details?.attributes),
    exceptions: normalizeLogExceptions(details?.error),
  });
}

function normalizeLogAttributes(attributes: Readonly<Record<string, unknown>> | undefined): Readonly<Record<string, PackageLogAttribute>> {
  if (attributes === undefined) return Object.freeze({});
  if (attributes === null || typeof attributes !== "object" || Array.isArray(attributes)) {
    throw new TypeError("Worker log attributes must be an object.");
  }
  const output: Record<string, PackageLogAttribute> = Object.create(null) as Record<string, PackageLogAttribute>;
  const names = new Set<string>();
  for (const [key, raw] of Object.entries(attributes)) {
    if (key === "{OriginalFormat}") continue;
    const normalizedKey = requiredText(key, "Worker log attribute key", 128);
    const folded = normalizedKey.toLowerCase();
    if (names.has(folded)) continue;
    if (names.size >= 64) throw new RangeError("Worker logs support at most 64 attributes.");
    names.add(folded);
    output[normalizedKey] = normalizeLogAttribute(raw);
  }
  return Object.freeze(output);
}

function normalizeLogAttribute(value: unknown): PackageLogAttribute {
  if (value === null || typeof value === "boolean") return value;
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new TypeError("Worker log numeric attributes must be finite.");
    return value;
  }
  if (value instanceof Date) {
    if (!Number.isFinite(value.getTime())) throw new TypeError("Worker log Date attributes must be valid.");
    return value.toISOString();
  }
  if (typeof value === "string") return truncate(value, 4096);
  return truncate(String(value), 4096);
}

function normalizeLogExceptions(error: unknown): readonly NormalizedLogException[] {
  if (error === undefined || error === null) return Object.freeze([]);
  const output: NormalizedLogException[] = [];
  let current: unknown = error;
  while (current !== undefined && current !== null && output.length < 5) {
    if (current instanceof Error) {
      output.push(Object.freeze({
        type: truncate(current.constructor.name || current.name || "Error", 512),
        message: truncate(current.message, 4096),
        stackTrace: current.stack === undefined ? null : truncate(current.stack, 32768),
      }));
      current = current.cause;
    } else {
      output.push(Object.freeze({ type: typeof current, message: truncate(String(current), 4096), stackTrace: null }));
      break;
    }
  }
  return Object.freeze(output);
}

function logLevel(level: PackageLogLevel): number {
  const value = LOG_LEVELS[level];
  if (value === undefined) throw new RangeError("Worker log level is invalid.");
  return value;
}

function boundedLogMessage(value: unknown): string {
  if (typeof value !== "string") throw new TypeError("Worker log message must be a string.");
  if (value.length > 16384) throw new RangeError("Worker log message exceeds 16384 characters.");
  return value;
}

function objectValue(value: JsonValue | undefined, label: string): Readonly<Record<string, JsonValue>> {
  if (value === null || value === undefined || Array.isArray(value) || typeof value !== "object") {
    throw new ProtocolError(`${label} must be an object.`);
  }
  return value as Readonly<Record<string, JsonValue>>;
}

function only(root: Readonly<Record<string, JsonValue>>, ...allowed: readonly string[]): void {
  const names = new Set(allowed);
  for (const name of Object.keys(root)) {
    if (!names.has(name)) throw new ProtocolError(`Envelope contains unsupported property '${name}'.`);
  }
  if (Object.keys(root).length !== allowed.length || allowed.some((name) => !Object.hasOwn(root, name))) {
    throw new ProtocolError(`Envelope must contain exactly ${allowed.join(", ")}.`);
  }
}

function stringValue(root: Readonly<Record<string, JsonValue>>, name: string, maximum: number): string {
  const value = root[name];
  if (typeof value !== "string" || value.length === 0 || value.length > maximum) {
    throw new ProtocolError(`Envelope property '${name}' must be a non-empty bounded string.`);
  }
  return value;
}

function requiredValue(root: Readonly<Record<string, JsonValue>>, name: string): JsonValue {
  if (!Object.hasOwn(root, name)) throw new ProtocolError(`Envelope is missing required property '${name}'.`);
  return root[name]!;
}

function idValue(root: Readonly<Record<string, JsonValue>>, name: string, maximum = 128): string {
  const value = stringValue(root, name, maximum);
  if (!SAFE_ID.test(value)) throw new ProtocolError(`Envelope property '${name}' is not a valid id.`);
  return value;
}

function canonicalActivationId(root: Readonly<Record<string, JsonValue>>, name: string): string {
  const value = stringValue(root, name, 64);
  if (!/^[0-9a-f]{32}$/u.test(value)) throw new ProtocolError(`Envelope property '${name}' is not a canonical activation id.`);
  return value;
}

function integerValue(root: Readonly<Record<string, JsonValue>>, name: string): number {
  const value = nonNegativeIntegerValue(root, name);
  if (!hasCanonicalNonNegativeIntegerToken(root, name)) {
    throw new ProtocolError(`Envelope property '${name}' must be a non-negative safe integer.`);
  }
  return value;
}

function nonNegativeIntegerValue(root: Readonly<Record<string, JsonValue>>, name: string): number {
  const value = root[name];
  if (typeof value !== "number"
    || !Number.isSafeInteger(value)
    || value < 0) {
    throw new ProtocolError(`Envelope property '${name}' must be a non-negative safe integer.`);
  }
  return value;
}

function optionalString(root: Readonly<Record<string, JsonValue>>, name: string, maximum: number): string | null {
  const value = requiredValue(root, name);
  if (value === null) return null;
  if (typeof value !== "string" || value.length > maximum || !isWellFormed(value)) {
    throw new ProtocolError(`Envelope property '${name}' must be a bounded string or null.`);
  }
  return value;
}

function absoluteUri(root: Readonly<Record<string, JsonValue>>, name: string): string {
  const value = stringValue(root, name, 4096);
  try {
    new URL(value);
  } catch {
    throw new ProtocolError(`Envelope property '${name}' must be an absolute URI.`);
  }
  return value;
}

function stringMap(
  root: Readonly<Record<string, JsonValue>>,
  name: string,
  allowNullValues: boolean,
): Readonly<Record<string, string | null>> {
  const value = objectValue(requiredValue(root, name), `Host contribution property '${name}'`);
  const entries = Object.entries(value);
  if (entries.length > 16) throw new ProtocolError(`Host contribution property '${name}' has too many entries.`);
  const result: Record<string, string | null> = Object.create(null) as Record<string, string | null>;
  let totalLength = 0;
  for (const [key, item] of entries) {
    if (key.trim().length === 0 || key.length > 64
      || typeof item !== "string" && (!allowNullValues || item !== null)
      || typeof item === "string" && (item.length > 2048 || !isWellFormed(item))) {
      throw new ProtocolError(`Host contribution property '${name}' contains an invalid entry.`);
    }
    totalLength += key.length + (item?.length ?? 0);
    if (totalLength > 8192) throw new ProtocolError(`Host contribution property '${name}' exceeds its total size bound.`);
    result[key] = item as string | null;
  }
  return Object.freeze(result);
}

function callbackStartResult(value: unknown, packageId: string, sessionId: string): JsonValue {
  const result = plainObject(value, "Worker callback start result");
  const flow = result.flow;
  if (flow !== "browser") throw new TypeError("Worker callback start flow must be 'browser'.");
  const launchUrl = absoluteResultUri(result.launchUrl, "Worker callback launchUrl");
  return {
    packageId,
    callbackSessionId: sessionId,
    flow,
    launchUrl,
    message: boundedResultText(result.message),
  };
}

function callbackCompletionResult(value: unknown, packageId: string, sessionId: string): JsonValue {
  const result = plainObject(value, "Worker callback completion result");
  const state = result.state;
  if (state !== "completed" && state !== "failed" && state !== "cancelled") {
    throw new TypeError("Worker callback completion state is invalid.");
  }
  return {
    packageId,
    callbackSessionId: sessionId,
    state,
    message: boundedResultText(result.message),
  };
}

function authStatusResult(value: unknown, packageId: string): JsonValue {
  const result = plainObject(value, "Worker auth status");
  const status = result.status;
  if (status !== "unavailable" && status !== "not-connected" && status !== "connected" && status !== "failed") {
    throw new TypeError("Worker auth status kind is invalid.");
  }
  if (typeof result.canAuthorize !== "boolean" || typeof result.canDisconnect !== "boolean") {
    throw new TypeError("Worker auth status actions must be Boolean.");
  }
  return {
    packageId,
    status,
    message: boundedResultText(result.message),
    canAuthorize: result.canAuthorize,
    canDisconnect: result.canDisconnect,
  };
}

function plainObject(value: unknown, label: string): Readonly<Record<string, unknown>> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError(`${label} must be an object.`);
  return value as Readonly<Record<string, unknown>>;
}

function absoluteResultUri(value: unknown, label: string): string {
  const text = requiredText(value, label, 4096);
  try {
    new URL(text);
  } catch {
    throw new TypeError(`${label} must be an absolute URI.`);
  }
  return text;
}

function boundedResultText(value: unknown): string {
  if (value === undefined || value === null || value === "") return "";
  if (typeof value !== "string" || !isWellFormed(value)) throw new TypeError("Worker contribution message must be valid text.");
  return sanitizeText(value, 2048);
}

function boundedString(value: unknown, label: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximum) {
    throw new TypeError(`${label} must be a non-empty bounded string.`);
  }
  return value;
}

function requiredText(value: unknown, label: string, maximum = Number.MAX_SAFE_INTEGER): string {
  if (typeof value !== "string" || value.trim().length === 0 || value.length > maximum || !isWellFormed(value)) {
    throw new TypeError(`${label} must be non-empty, bounded, valid Unicode text.`);
  }
  return value;
}

function nullableText(value: unknown, label: string): string | null {
  if (value === undefined || value === null) return null;
  if (typeof value !== "string" || !isWellFormed(value)) throw new TypeError(`${label} must be valid Unicode text or null.`);
  return value;
}

function contentRegistrationOptions(options: RpcContentRegistrationOptions): Readonly<Record<string, JsonValue>> {
  if (options === null || typeof options !== "object") throw new TypeError("RPC content registration options must be an object.");
  const length = options.length ?? null;
  if (length !== null && (!Number.isSafeInteger(length) || length < 0 || length > LIMITS.maxContentBytes)) {
    throw new RangeError("RPC content length is outside the worker content limit.");
  }
  if (options.expiresAt !== undefined && !Number.isFinite(options.expiresAt.getTime())) {
    throw new RangeError("RPC content expiry must be a valid Date.");
  }
  const repeatability = options.repeatability ?? "single-use";
  if (repeatability !== "single-use" && repeatability !== "repeatable") throw new RangeError("RPC content repeatability is invalid.");
  const maximumUses = options.maximumUses ?? 1;
  if (!Number.isSafeInteger(maximumUses) || maximumUses < 1 || maximumUses > LIMITS.maxContentUses
    || (repeatability === "single-use" && maximumUses !== 1)) {
    throw new RangeError("RPC content maximumUses is invalid.");
  }
  return Object.freeze({
    mediaType: requiredText(options.mediaType, "RPC content mediaType", 128),
    fileName: requiredText(options.fileName, "RPC content fileName", 255),
    length,
    expiresAtUtc: options.expiresAt === undefined ? null : formatUtcTimestamp(options.expiresAt),
    repeatability,
    maximumUses,
  });
}

function contentReference(value: unknown, requireWireToken = true): RpcContentReference {
  const root = objectValue(value as JsonValue, "RPC content reference");
  only(root, "id", "length", "sha256", "mediaType", "fileName", "expiresAtUtc", "repeatability");
  const hash = stringValue(root, "sha256", 64);
  if (!SHA256_ANY_CASE.test(hash)) throw new ProtocolError("RPC content SHA-256 is invalid.");
  const expiry = stringValue(root, "expiresAtUtc", 64);
  // Worker metadata uses fixed seven-digit UTC; RPC logical content uses exact
  // canonical UTC. Convert only the Host registration result, never a reference
  // supplied for opening. No Date reformatting (which would lose sub-ms digits).
  const expiresAtUtc = requireWireToken && Number.isFinite(parseUtcTimestamp(expiry))
    ? expiry.replace(/\.([0-9]{7})Z$/u, (_match, fraction: string) => {
      const significant = fraction.replace(/0+$/u, "");
      return significant ? `.${significant}Z` : "Z";
    }) : expiry;
  const repeatability = stringValue(root, "repeatability", 32);
  if (repeatability !== "single-use" && repeatability !== "repeatable") throw new ProtocolError("RPC content repeatability is invalid.");
  const reference = {
    id: idValue(root, "id"),
    length: requireWireToken
      ? integerValue(root, "length")
      : nonNegativeIntegerValue(root, "length"),
    sha256: hash,
    mediaType: stringValue(root, "mediaType", 128),
    fileName: stringValue(root, "fileName", 255),
    expiresAtUtc,
    repeatability,
  };
  // Validate the logical profile separately from the stricter Worker transport
  // bounds above. Canonical references retain every digit and their exact text.
  const error = contentReferenceError(reference, "RPC content reference");
  if (error) throw new ProtocolError(error);
  return Object.freeze(reference) as RpcContentReference;
}

function contentFileHandle(value: JsonValue): Readonly<{ handleId: string; filePath: string }> {
  const root = objectValue(value, "RPC content file handle");
  only(root, "handleId", "filePath");
  return Object.freeze({ handleId: idValue(root, "handleId"), filePath: stringValue(root, "filePath", 4096) });
}

function scopeIdentity(value: JsonValue): Readonly<{ scopeId: string; deadline: Date }> {
  const root = objectValue(value, "RPC call-scope identity");
  only(root, "scopeId", "deadlineUtc");
  const deadlineUtc = stringValue(root, "deadlineUtc", 64);
  const milliseconds = parseUtcTimestamp(deadlineUtc);
  if (!Number.isFinite(milliseconds)) throw new ProtocolError("Host RPC call-scope deadline is invalid.");
  return Object.freeze({ scopeId: idValue(root, "scopeId"), deadline: new Date(milliseconds) });
}

function providerArray(value: JsonValue | undefined): readonly ProviderWireIdentity[] {
  if (!Array.isArray(value)) throw new ProtocolError("Envelope providers must be an array.");
  return value.map((item) => {
    const provider = objectValue(item, "Provider identity");
    only(provider, "providerId", "contractId", "contractVersion", "descriptorSha256", "abiSha256");
    return Object.freeze({
      providerId: stringValue(provider, "providerId", 256),
      contractId: stringValue(provider, "contractId", 256),
      contractVersion: stringValue(provider, "contractVersion", SEMANTIC_VERSION_MAXIMUM_LENGTH),
      descriptorSha256: sha256Value(provider, "descriptorSha256"),
      abiSha256: sha256Value(provider, "abiSha256"),
    });
  });
}

function providerSnapshot(value: JsonValue | undefined): RpcProviderSnapshot {
  const provider = objectValue(value, "Provider snapshot");
  only(provider, "packageId", "packageVersion", "providerId", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "activationId", "activationEpoch", "sessionGeneration", "endpointReference", "catalogRevision", "state", "faultCode");
  const state = stringValue(provider, "state", 32);
  if (state !== "active" && state !== "inactive" && state !== "faulted") throw new ProtocolError("Provider state is invalid.");
  const faultCode = provider.faultCode;
  if (faultCode !== null && (typeof faultCode !== "string" || faultCode.length === 0 || faultCode.length > 128 || !SAFE_ERROR_CODE.test(faultCode))) {
    throw new ProtocolError("Provider faultCode is invalid.");
  }
  return Object.freeze({
    packageId: stringValue(provider, "packageId", 256),
    packageVersion: stringValue(provider, "packageVersion", SEMANTIC_VERSION_MAXIMUM_LENGTH),
    providerId: stringValue(provider, "providerId", 256),
    contractId: stringValue(provider, "contractId", 256),
    contractVersion: stringValue(provider, "contractVersion", SEMANTIC_VERSION_MAXIMUM_LENGTH),
    descriptorSha256: sha256Value(provider, "descriptorSha256"),
    abiSha256: sha256Value(provider, "abiSha256"),
    activationId: canonicalActivationId(provider, "activationId"),
    activationEpoch: integerValue(provider, "activationEpoch"),
    sessionGeneration: integerValue(provider, "sessionGeneration"),
    endpointReference: stringValue(provider, "endpointReference", 256),
    catalogRevision: integerValue(provider, "catalogRevision"),
    state,
    faultCode,
  });
}

function catalogSnapshot(value: JsonValue): RpcCatalogSnapshot {
  const snapshot = objectValue(value, "RPC catalog snapshot");
  only(snapshot, "revision", "sequence", "providers", "resetRequired");
  if (!Array.isArray(snapshot.providers)) throw new ProtocolError("RPC catalog providers must be an array.");
  return Object.freeze({
    revision: integerValue(snapshot, "revision"),
    sequence: integerValue(snapshot, "sequence"),
    providers: Object.freeze(snapshot.providers.map((provider) => providerSnapshot(provider))),
    resetRequired: booleanValue(snapshot, "resetRequired"),
  });
}

function catalogEvent(value: JsonValue): RpcCatalogEvent {
  const event = objectValue(value, "RPC catalog event");
  only(event, "revision", "sequence", "kind", "provider");
  const kind = stringValue(event, "kind", 32);
  if (kind !== "added" && kind !== "removed" && kind !== "activated" && kind !== "deactivated" && kind !== "faulted" && kind !== "reset-required") {
    throw new ProtocolError("RPC catalog event kind is invalid.");
  }
  const provider = requiredValue(event, "provider");
  return Object.freeze({
    revision: integerValue(event, "revision"),
    sequence: integerValue(event, "sequence"),
    kind,
    provider: provider === null ? null : providerSnapshot(provider),
  });
}

function booleanValue(root: Readonly<Record<string, JsonValue>>, name: string): boolean {
  const value = root[name];
  if (typeof value !== "boolean") throw new ProtocolError(`Envelope property '${name}' must be Boolean.`);
  return value;
}

function rpcError(value: JsonValue | undefined): RpcError {
  const error = objectValue(value, "RPC error");
  const hasDetails = Object.hasOwn(error, "details");
  only(
    error,
    "kind",
    "code",
    "message",
    "dispatchState",
    "retrySafety",
    ...(hasDetails ? ["details"] : []),
  );
  const kind = stringValue(error, "kind", 64) as RpcErrorValue["kind"];
  const allowed = new Set<RpcErrorValue["kind"]>([
    "domain", "permission-denied", "not-found", "stale-endpoint", "validation", "deadline-exceeded",
    "cancelled", "resource-exhausted", "unavailable", "provider-faulted", "protocol",
  ]);
  if (!allowed.has(kind)) throw new ProtocolError(`Host returned unknown RPC error kind '${kind}'.`);
  const code = stringValue(error, "code", 128);
  if (!SAFE_ERROR_CODE.test(code)) throw new ProtocolError("Host returned an unsafe RPC error code.");
  const dispatchState = stringValue(error, "dispatchState", 32);
  if (dispatchState !== "not-dispatched" && dispatchState !== "possibly-dispatched") throw new ProtocolError("Host returned an invalid RPC dispatch state.");
  const retrySafety = stringValue(error, "retrySafety", 16);
  if (retrySafety !== "safe" && retrySafety !== "unsafe") throw new ProtocolError("Host returned an invalid RPC retry safety.");
  const details = hasDetails ? requiredValue(error, "details") : undefined;
  if (details !== undefined && kind !== "domain") {
    throw new ProtocolError("Host returned details on a non-domain RPC error.");
  }
  const rpcErrorValue = {
    kind,
    code,
    message: stringValue(error, "message", 512),
    dispatchState: dispatchState as RpcErrorValue["dispatchState"],
    retrySafety: retrySafety as RpcErrorValue["retrySafety"],
  };
  const result = new RpcError(details === undefined ? rpcErrorValue : { ...rpcErrorValue, details }, true);
  operationalErrors.add(result);
  return result;
}

function validateProviderIdentity(provider: RpcProviderRegistration): void {
  if (!isPackageId(provider.providerId)) throw new TypeError("Provider providerId is invalid.");
  if (!isPackageId(provider.contractId)) throw new TypeError("Provider contractId is invalid.");
  if (!isSemanticVersion(provider.contractVersion)) throw new TypeError("Provider contractVersion is invalid.");
  if (!SHA256.test(provider.descriptorSha256)) throw new TypeError("Provider descriptorSha256 is invalid.");
  if (!SHA256.test(provider.abiSha256)) throw new TypeError("Provider abiSha256 is invalid.");
}

function providerWire(provider: NormalizedProviderRegistration): ProviderWireIdentity {
  return Object.freeze({
    providerId: provider.providerId,
    contractId: provider.contractId,
    contractVersion: provider.contractVersion,
    descriptorSha256: provider.descriptorSha256,
    abiSha256: provider.abiSha256,
  });
}

function sha256Value(root: Readonly<Record<string, JsonValue>>, name: string): string {
  const value = stringValue(root, name, 64);
  if (!SHA256.test(value)) throw new ProtocolError(`Envelope property '${name}' is not a lowercase SHA-256 value.`);
  return value;
}

function compareProviders(left: ProviderWireIdentity, right: ProviderWireIdentity): number {
  return ordinal(left.providerId, right.providerId);
}

function deadline(options?: RpcCallOptions): string | null {
  if (options?.deadline === undefined) return null;
  if (!Number.isFinite(options.deadline.getTime())) throw new RangeError("RPC deadline must be a valid Date.");
  return formatUtcTimestamp(options.deadline);
}

function scheduleDeadline(deadlineMilliseconds: number, callback: () => void): () => void {
  const maximumDelay = 0x7fffffff;
  let timer: NodeJS.Timeout | undefined;
  let cancelled = false;
  const schedule = (): void => {
    if (cancelled) return;
    const remaining = deadlineMilliseconds - Date.now();
    if (remaining <= 0) {
      timer = setTimeout(callback, 0);
      return;
    }
    timer = setTimeout(schedule, Math.min(remaining, maximumDelay));
  };
  schedule();
  return () => {
    cancelled = true;
    if (timer !== undefined) clearTimeout(timer);
  };
}

function combineSignals(required: AbortSignal, optional?: AbortSignal): AbortSignal {
  return optional === undefined ? required : AbortSignal.any([required, optional]);
}

function abortError(signal?: AbortSignal): Error {
  if (signal?.reason instanceof Error) return signal.reason;
  const error = new Error("The operation was aborted.");
  error.name = "AbortError";
  return error;
}

function deadlineExceededError(): RpcError {
  return infrastructureError("deadline-exceeded", "rpc.call.deadline-exceeded", "The RPC call deadline elapsed.");
}

function infrastructureError(
  kind: RpcErrorValue["kind"],
  code: string,
  message: string,
  dispatchState: RpcErrorValue["dispatchState"] = "not-dispatched",
  retrySafety: RpcErrorValue["retrySafety"] = "safe",
): RpcError {
  const result = new RpcError({ kind, code, message, dispatchState, retrySafety });
  operationalErrors.add(result);
  return result;
}

function streamQueueLimitMessage(
  identity: OutboundStreamIdentity | undefined,
  rejection: AsyncQueueRejection,
): string {
  const stream: OutboundStreamIdentity = identity ?? { operation: "stream" };
  let message = `The worker RPC stream queue exceeded its count or byte bound. Local diagnostic: limit=${rejection.limit}; queuedMessages=${rejection.queuedMessages}/${rejection.maximumMessages}; queuedBytes=${rejection.queuedBytes}/${rejection.maximumBytes}; incomingBytes=${rejection.incomingBytes}; operation=${stream.operation}`;
  if (stream.serviceId !== undefined) message += `; service=${stream.serviceId}`;
  if (stream.methodId !== undefined) message += `; method=${stream.methodId}`;
  return `${message}.`.slice(0, 512);
}

function sanitizeDiagnosticIdentity(value: string): string {
  return [...value]
    .slice(0, 64)
    .map((character) => /^[A-Za-z0-9._-]$/u.test(character) ? character : "_")
    .join("");
}

function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}

function isDeclaredDomainError(
  provider: NormalizedProviderRegistration,
  serviceId: string,
  methodId: string,
  error: unknown,
): error is RpcError {
  return error instanceof RpcError
    && !error.isHostAuthenticated
    && error.error.kind === "domain"
    && error.error.code.length <= 128
    && SAFE_ERROR_CODE.test(error.error.code)
    && provider.declaredErrors[serviceId]?.[methodId]?.includes(error.error.code) === true;
}

function providerExceptionType(error: unknown): string {
  const source = error instanceof Error
    ? error.constructor.name || error.name
    : typeof error;
  const sanitized = [...source].map((character) => /[A-Za-z0-9._+`]/u.test(character) ? character : "_").join("");
  return truncate(sanitized || "Error", 256);
}

function sanitizeText(value: string, maximum: number): string {
  return truncate(value.replace(/[\u0000-\u001f\u007f]/gu, " ").trim(), maximum);
}

function truncate(value: string, maximum: number): string {
  if (value.length <= maximum) return value;
  let length = maximum;
  const previous = value.charCodeAt(length - 1);
  const next = value.charCodeAt(length);
  if (previous >= 0xd800 && previous <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) length -= 1;
  return value.slice(0, length);
}

function validateStorageKey(value: unknown): string {
  if (typeof value !== "string" || !STORAGE_KEY.test(value)) {
    throw new TypeError("Package storage keys must be portable ASCII tokens of at most 256 characters.");
  }
  return value;
}

function validateStorageValue(value: unknown): string {
  if (typeof value !== "string" || !isWellFormed(value) || Buffer.byteLength(value, "utf8") > LIMITS.maxPayloadBytes) {
    throw new TypeError(`Package storage values must be valid Unicode no larger than ${LIMITS.maxPayloadBytes} UTF-8 bytes.`);
  }
  return value;
}

function validateRelativePath(value: unknown): string {
  if (typeof value !== "string" || value.length === 0 || value.length > 1024 || value.includes("\\")
    || /[^\u0020-\u007e]/u.test(value)) {
    throw new TypeError("Package file paths must be portable visible-ASCII relative paths.");
  }
  for (const segment of value.split("/")) {
    const base = segment.split(".", 1)[0]?.toUpperCase() ?? "";
    if (segment.length === 0 || segment.length > 255 || segment === "." || segment === ".."
      || segment.endsWith(" ") || segment.endsWith(".") || /[<>:"|?*]/u.test(segment)
      || WINDOWS_RESERVED.has(base)) {
      throw new TypeError("Package file path contains an invalid portable segment.");
    }
  }
  return value;
}

function validateReturnedKeys(keys: readonly string[], raw?: JsonValue): readonly string[] {
  if (raw !== undefined && Buffer.byteLength(JSON.stringify(keys), "utf8") > LIMITS.inlineDataBytes) {
    throw new ProtocolError("Host inline package-state keys exceed the inline transport limit.");
  }
  let previous: string | undefined;
  for (const key of keys) {
    if (!STORAGE_KEY.test(key) || previous !== undefined && ordinal(previous, key) >= 0) {
      throw new ProtocolError("Host package-state keys are invalid, duplicated, or not ordinally sorted.");
    }
    previous = key;
  }
  return Object.freeze([...keys]);
}

function dataValue(value: JsonValue, maximumBytes: number): DataValue {
  const root = objectValue(value, "Host package-data result");
  const kind = stringValue(root, "kind", 32);
  if (kind === "inline") {
    only(root, "kind", "value");
    const item = requiredValue(root, "value");
    if (item !== null && typeof item !== "string") throw new ProtocolError("Host inline package-data value must be a string or null.");
    if (typeof item === "string" && (Buffer.byteLength(item, "utf8") > LIMITS.inlineDataBytes || !isWellFormed(item))) {
      throw new ProtocolError("Host inline package-data value exceeds the inline limit or is invalid Unicode.");
    }
    return { kind: "inline", value: item };
  }
  if (kind !== "payload") throw new ProtocolError("Host package-data result kind is invalid.");
  return dataPayload(root, maximumBytes);
}

function dataKeys(value: JsonValue): DataKeys {
  const root = objectValue(value, "Host package-state list result");
  const kind = stringValue(root, "kind", 32);
  if (kind === "inline") {
    only(root, "kind", "keys");
    if (!Array.isArray(root.keys) || root.keys.some((item) => typeof item !== "string")) {
      throw new ProtocolError("Host inline package-state keys must be a string array.");
    }
    return { kind: "inline", keys: root.keys as string[] };
  }
  if (kind !== "payload") throw new ProtocolError("Host package-state list result kind is invalid.");
  return dataPayload(root, LIMITS.maxPayloadBytes);
}

function dataPayload(value: JsonValue | Readonly<Record<string, JsonValue>>, maximumBytes: number): DataPayload {
  const root = objectValue(value as JsonValue, "Host package-data payload");
  only(root, "kind", "handleId", "filePath", "length", "sha256");
  if (stringValue(root, "kind", 32) !== "payload") throw new ProtocolError("Host package-data payload kind is invalid.");
  const length = integerValue(root, "length");
  if (length > maximumBytes) throw new ProtocolError("Host package-data payload exceeds its byte limit.");
  const hash = stringValue(root, "sha256", 64);
  if (!SHA256_ANY_CASE.test(hash)) throw new ProtocolError("Host package-data payload hash is invalid.");
  return {
    kind: "payload",
    handleId: idValue(root, "handleId"),
    filePath: stringValue(root, "filePath", 4096),
    length,
    sha256: hash,
  };
}

function payloadAllocation(value: JsonValue, maximumBytes: number): DataPayload {
  const root = objectValue(value, "Host payload allocation");
  only(root, "handleId", "filePath", "length");
  const length = integerValue(root, "length");
  if (length > maximumBytes) throw new ProtocolError("Host payload allocation exceeds its byte limit.");
  return {
    kind: "payload",
    handleId: idValue(root, "handleId"),
    filePath: stringValue(root, "filePath", 4096),
    length,
    sha256: null,
  };
}

function decodeStrictUtf8(bytes: Buffer, label: string): string {
  try {
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    throw new ProtocolError(`${label} is not strict UTF-8.`);
  }
}

async function writeAll(file: FileHandle, bytes: Uint8Array, signal?: AbortSignal): Promise<void> {
  let offset = 0;
  while (offset < bytes.byteLength) {
    signal?.throwIfAborted();
    const { bytesWritten } = await file.write(bytes, offset, bytes.byteLength - offset, null);
    if (bytesWritten === 0) throw new Error("Filesystem write made no progress.");
    offset += bytesWritten;
  }
}

async function hashOpenFile(file: FileHandle, length: number, signal?: AbortSignal): Promise<string> {
  const hash = createHash("sha256");
  const buffer = Buffer.allocUnsafe(128 * 1024);
  let offset = 0;
  while (offset < length) {
    signal?.throwIfAborted();
    const count = Math.min(buffer.byteLength, length - offset);
    const { bytesRead } = await file.read(buffer, 0, count, offset);
    if (bytesRead === 0) throw new ProtocolError("Host content file ended before its declared length.");
    hash.update(buffer.subarray(0, bytesRead));
    offset += bytesRead;
  }
  return hash.digest("hex");
}

function sha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

function resourceProfile(value: JsonValue | undefined, expectedHash: string): WorkerResourceProfile {
  const root = objectValue(value, "Host resourceProfile");
  only(
    root,
    "version",
    "rpcValueBytes",
    "valueDepth",
    "valueNodes",
    "objectMembers",
    "inlineDecodedBytes",
    "contentReferenceBytes",
  );
  if (!SHA256.test(expectedHash)) throw new ProtocolError("Host resourceProfileHash is not canonical SHA-256.");
  const profile = {
    version: integerValue(root, "version"),
    rpcValueBytes: integerValue(root, "rpcValueBytes"),
    valueDepth: integerValue(root, "valueDepth"),
    valueNodes: integerValue(root, "valueNodes"),
    objectMembers: integerValue(root, "objectMembers"),
    inlineDecodedBytes: integerValue(root, "inlineDecodedBytes"),
    contentReferenceBytes: integerValue(root, "contentReferenceBytes"),
  };
  if (profile.version !== 1
    || profile.rpcValueBytes <= 0 || profile.rpcValueBytes > 32 * 1024 * 1024
    || profile.valueDepth <= 0 || profile.valueDepth > 64
    || profile.valueNodes <= 0 || profile.valueNodes > 4_194_304
    || profile.objectMembers <= 0 || profile.objectMembers > 65_536
    || profile.inlineDecodedBytes <= 0 || profile.inlineDecodedBytes > 4 * 1024 * 1024
    || profile.contentReferenceBytes <= 0 || profile.contentReferenceBytes > 256 * 1024 * 1024) {
    throw new ProtocolError("Host resourceProfile exceeds Worker V1 protocol ceilings.");
  }
  const normalized: WorkerResourceProfile = Object.freeze({ ...profile, version: 1 });
  const actualHash = computeWorkerResourceProfileHash(normalized);
  if (actualHash !== expectedHash) {
    throw new ProtocolError("host.hello resourceProfileHash does not match its closed resourceProfile.");
  }
  return normalized;
}

function applyResourceProfile(profile: WorkerResourceProfile): void {
  LIMITS.maxRpcValueBytes = profile.rpcValueBytes;
  LIMITS.maxValueDepth = profile.valueDepth;
  LIMITS.maxValueNodes = profile.valueNodes;
  LIMITS.maxObjectMembers = profile.objectMembers;
  LIMITS.maxFrameBytes = profile.rpcValueBytes + LIMITS.maxEnvelopeReserveBytes;
  LIMITS.maxMessageDepth = profile.valueDepth + 8;
  LIMITS.maxContentBytes = profile.contentReferenceBytes;
  LIMITS.maxWriteQueueBytes = profile.rpcValueBytes * 8;
  LIMITS.maxStreamQueueBytes = profile.rpcValueBytes * 2;
}

function safeNonNegativeInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer.`);
}

function assertJsonValue(value: unknown): asserts value is JsonValue {
  const seen = new Set<object>();
  let nodes = 0;
  const visit = (item: unknown, depth: number): void => {
    if (depth > LIMITS.maxValueDepth) throw new TypeError("JSON value exceeds the negotiated RPC value depth limit.");
    if (++nodes > LIMITS.maxValueNodes) throw new TypeError("JSON value exceeds the negotiated RPC value node-count limit.");
    if (item === null || typeof item === "boolean") return;
    if (typeof item === "string") {
      if (!isWellFormed(item)) throw new TypeError("JSON value contains invalid Unicode.");
      return;
    }
    if (typeof item === "number") {
      if (!Number.isFinite(item)) throw new TypeError("JSON value contains a non-finite number.");
      return;
    }
    if (typeof item !== "object") throw new TypeError("Value is not JSON-compatible.");
    if (seen.has(item)) throw new TypeError("JSON value contains a cycle.");
    seen.add(item);
    if (Array.isArray(item)) {
      for (const child of item) visit(child, depth + 1);
    } else {
      const prototype = Object.getPrototypeOf(item) as object | null;
      if (prototype !== Object.prototype && prototype !== null) throw new TypeError("JSON object has a non-plain prototype.");
      const children = Object.values(item);
      if (children.length > LIMITS.maxObjectMembers) {
        throw new TypeError("JSON object exceeds the negotiated RPC object-member limit.");
      }
      for (const child of children) visit(child, depth + 1);
    }
    seen.delete(item);
  };
  visit(value, 1);
}

function isWellFormed(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (next < 0xdc00 || next > 0xdfff) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}

function isWithin(root: string, path: string): boolean {
  const traversal = relative(resolve(root), resolve(path));
  if (traversal === "") return true;
  const comparison = process.platform === "win32" ? traversal.toLowerCase() : traversal;
  return !isAbsolute(traversal) && comparison !== ".." && !comparison.startsWith(`..${sep}`);
}

function canShutdown(phase: WorkerPhase): boolean {
  return phase === "ready" || phase === "candidate-starting" || phase === "candidate-started"
    || phase === "committing" || phase === "committed" || phase === "preparing-storage-migrations"
    || phase === "storage-migrations-prepared" || phase === "activating" || phase === "active";
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? `${error.name}: ${error.message}` : String(error);
}

function isNodeError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && "code" in error;
}

type WorkerPhase =
  | "waiting-hello"
  | "ready"
  | "candidate-starting"
  | "candidate-started"
  | "committing"
  | "committed"
  | "preparing-storage-migrations"
  | "storage-migrations-prepared"
  | "activating"
  | "active"
  | "stopping"
  | "stopped"
  | "faulted";

interface WorkerEnvironment {
  readonly protocol: string;
  readonly packageId: string;
  readonly packageVersion: string;
  readonly activationId: string;
  readonly sessionId: string;
  readonly content: WorkerLocalPath;
  readonly temporary: WorkerLocalPath;
  readonly workspace: BindableWorkerLocalPath;
  readonly rpcExtendedDeadlineV1: boolean;
  readonly operationalOutcomesV1: boolean;
}

interface BindableWorkerLocalPath extends WorkerLocalPath {
  bind(rootPath: string, excludedRoots: readonly string[]): void;
}

interface NormalizedWorkerOptions {
  readonly providers: readonly NormalizedProviderRegistration[];
  readonly settingsSchema: WorkerSettingsSchemaWire | null;
  readonly stateMigrations: readonly PackageStorageKeyMigration[];
  readonly secretMigrations: readonly PackageStorageKeyMigration[];
  readonly callbackHandlers: readonly NormalizedCallbackHandler[];
  readonly authHandler: NormalizedAuthHandler | null;
  readonly onCandidateStarted?: WorkerOptions["onCandidateStarted"];
  readonly onGenerationCommitted?: WorkerOptions["onGenerationCommitted"];
  readonly onBeforeStorageMigrations?: WorkerOptions["onBeforeStorageMigrations"];
  readonly onActivated?: WorkerOptions["onActivated"];
  readonly onShutdown?: WorkerOptions["onShutdown"];
}

interface NormalizedProviderRegistration extends RpcProviderRegistration {}
interface NormalizedCallbackHandler extends WorkerCallbackHandler {}
interface NormalizedAuthHandler extends WorkerAuthHandler {}

interface HostInvocation {
  readonly id: string;
  readonly deadline:number;
  readonly kind: "unary" | "server-stream";
  readonly provider: NormalizedProviderRegistration;
  readonly serviceId: string;
  readonly methodId: string;
  readonly controller: AbortController;
  readonly contentFiles: Set<OpenContentFile>;
  hostCancelled: boolean;
  active: boolean;
  operation?: Promise<void>;
}

interface HostContribution {
  readonly id: string;
  readonly type: string;
  readonly sessionGeneration: number;
  readonly value: JsonValue;
  readonly controller: AbortController;
  hostCancelled: boolean;
  active: boolean;
  operation?: Promise<void>;
}

type ClientCallLane = "ordinary" | "observation" | "control" | "logging";

interface ClientCall {
  readonly hostError?: (error: RpcError) => void;
  readonly id: string;
  readonly kind: "unary" | "stream";
  readonly lane: ClientCallLane;
  readonly streamIdentity?: OutboundStreamIdentity;
  readonly queue: AsyncQueue;
  readonly promise: Promise<JsonValue>;
  readonly resolve: (value: JsonValue) => void;
  readonly reject: (error: unknown) => void;
  readonly awaitTerminalAfterAbort: boolean;
  readonly abandonedResult?: (value: JsonValue) => Promise<void> | void;
  readonly releaseTerminalResource?: () => void;
  readonly terminalPromise: Promise<void>;
  readonly resolveTerminal: () => void;
  cancelled: boolean;
  terminal: boolean;
  cleanup: () => void;
}

interface OutboundStreamIdentity {
  readonly operation: "stream" | "watch" | "subscribe";
  readonly serviceId?: string;
  readonly methodId?: string;
}

interface ClientCallBehavior {
  readonly hostError?: (error: RpcError) => void;
  readonly lane?: ClientCallLane;
  readonly migrationPreparationRead?: boolean;
  readonly awaitTerminalAfterAbort?: boolean;
  readonly abandonedResult?: (value: JsonValue) => Promise<void> | void;
  readonly releaseTerminalResource?: () => void;
  readonly started?: () => void;
}

interface ScopeState {
  readonly id: string;
  readonly deadline: Date;
  readonly controller: AbortController;
  readonly contentFiles: Set<OpenContentFile>;
  readonly capacity: ResourceReservation;
  closing: boolean;
  cancelDeadline?: () => void;
  closePromise?: Promise<void>;
}

type ContentRegistrationAuthority =
  | Readonly<{ kind: "invocation"; invocation: HostInvocation }>
  | Readonly<{ kind: "scope"; scope: ScopeState; endpointReference: string }>;

type ContentOpenAuthority =
  | Readonly<{ kind: "invocation"; invocation: HostInvocation }>
  | Readonly<{ kind: "scope"; scope: ScopeState }>;

interface PayloadHandle {
  readonly id: string;
  readonly filePath: string;
  readonly length: number;
  readonly sha256: string | null;
  readonly reservation: ResourceReservation;
}

interface DataPayload {
  readonly kind: "payload";
  readonly handleId: string;
  readonly filePath: string;
  readonly length: number;
  readonly sha256: string | null;
}

type DataValue = Readonly<{ kind: "inline"; value: string | null }> | DataPayload;
type DataKeys = Readonly<{ kind: "inline"; keys: readonly string[] }> | DataPayload;

interface NormalizedLogException {
  readonly type: string;
  readonly message: string;
  readonly stackTrace: string | null;
}

interface NormalizedLogEntry {
  readonly channel: "event" | "logger";
  readonly level: number;
  readonly category: string | null;
  readonly eventId: number;
  readonly eventName: string | null;
  readonly message: string;
  readonly attributes: Readonly<Record<string, PackageLogAttribute>>;
  readonly exceptions: readonly NormalizedLogException[];
}
