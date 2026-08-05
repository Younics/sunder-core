import { once } from "node:events";
import { FrameDecoder, ProtocolError, encodeFrame } from "./framing";
import { formatUtcTimestamp, parseUtcTimestamp } from "./timestamp";
import { isPackageId, isSemanticVersion } from "./validation";
import {
  SUNDER_WORKER_PROTOCOL,
  SUNDER_WORKER_PROTOCOL_VERSION,
  RpcError,
  type HostEnvelope,
  type JsonValue,
  type ProviderWireIdentity,
  type RpcCallOptions,
  type RpcCatalogEvent,
  type RpcCatalogSnapshot,
  type RpcClient,
  type RpcContentClient,
  type RpcContentFile,
  type RpcContentReference,
  type RpcContentRegistrationOptions,
  type RpcErrorValue,
  type RpcInvocationContext,
  type RpcProviderRegistration,
  type RpcProviderSnapshot,
  type WorkerEnvelope,
  type WorkerLimits,
  type WorkerOptions,
} from "./types";

const DEFAULT_LIMITS: WorkerLimits = Object.freeze({
  maxFrameBytes: 1024 * 1024,
  maxHeaderBytes: 8 * 1024,
  maxMessageDepth: 64,
  maxInboundCalls: 64,
  maxOutboundCalls: 32,
  maxStreamQueueMessages: 32,
  maxWriteQueue: 128,
  maxRememberedIds: 2048,
});

export async function runWorker(options: WorkerOptions): Promise<void> {
  const runtime = new WorkerRuntime(options);
  await runtime.run();
}

export function safeStderr(message: string, error?: unknown, maximumCharacters = 4096): void {
  const detail = error instanceof Error ? `${error.name}: ${error.message}` : error === undefined ? "" : String(error);
  let output = detail.length === 0 ? message : `${message}: ${detail}`;
  output = output.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/gu, " ").replace(/[\r\n]+/gu, " ").trim();
  if (output.length > maximumCharacters) output = `${output.slice(0, maximumCharacters)} [truncated]`;
  process.stderr.write(`${output}\n`);
}

class WorkerRuntime {
  readonly #options: WorkerOptions;
  readonly #limits: WorkerLimits;
  readonly #decoder: FrameDecoder;
  readonly #writer: ProtocolWriter;
  readonly #providers: ReadonlyMap<string, RpcProviderRegistration>;
  readonly #hostInvocations = new Map<string, HostInvocation>();
  readonly #clientCalls = new Map<string, ClientCall>();
  readonly #rememberedHostIds = new Set<string>();
  readonly #rememberedHostIdOrder: string[] = [];
  readonly #client: RpcClient;
  #phase: "waiting-hello" | "ready" | "active" | "stopping" | "stopped" = "waiting-hello";
  #messageTail = Promise.resolve();
  #activationOperation = Promise.resolve();
  #clientId = 0;
  #resolveRun: (() => void) | undefined;
  #rejectRun: ((error: unknown) => void) | undefined;

  public constructor(options: WorkerOptions) {
    this.#options = options;
    this.#limits = Object.freeze({ ...DEFAULT_LIMITS, ...options.limits });
    if (Object.values(this.#limits).some((value) => !Number.isSafeInteger(value) || value <= 0)) {
      throw new RangeError("Worker limits must be positive safe integers.");
    }
    this.#decoder = new FrameDecoder(this.#limits);
    this.#writer = new ProtocolWriter(this.#limits);
    const providers = new Map<string, RpcProviderRegistration>();
    for (const provider of options.providers) {
      validateProviderIdentity(provider);
      if (providers.has(provider.providerId)) throw new Error(`Provider '${provider.providerId}' is registered more than once.`);
      providers.set(provider.providerId, provider);
    }
    this.#providers = providers;
    this.#client = this.#createClient();
  }

  public async run(): Promise<void> {
    const completion = new Promise<void>((resolve, reject) => {
      this.#resolveRun = resolve;
      this.#rejectRun = reject;
    });
    const onData = (chunk: Buffer): void => {
      try {
        for (const value of this.#decoder.push(chunk)) {
          this.#messageTail = this.#messageTail.then(() => this.#handleEnvelope(value)).catch((error: unknown) => {
            this.#fail(error);
          });
        }
      } catch (error) {
        this.#fail(error);
      }
    };
    const onEnd = (): void => {
      try {
        this.#decoder.end();
        if (this.#phase !== "stopped") this.#fail(new ProtocolError("Host protocol input closed before shutdown completed."));
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
      case "host.activate":
        await this.#handleActivate(root);
        break;
      case "host.invoke":
        this.#beginHostInvocation(root);
        break;
      case "host.cancel":
        this.#cancelHostInvocation(root);
        break;
      case "host.result":
      case "host.event":
      case "host.complete":
      case "host.error":
        this.#handleClientResponse(type, root);
        break;
      case "host.shutdown":
        await this.#handleShutdown(root);
        break;
      default:
        throw new ProtocolError(`Unknown Host envelope type '${type}'.`);
    }
  }

  async #handleHello(root: Readonly<Record<string, JsonValue>>): Promise<void> {
    only(root, "type", "protocol", "protocolVersion", "challenge", "packageId", "packageVersion", "activationId", "sessionId", "providers");
    const protocol = stringValue(root, "protocol", 64);
    const version = integerValue(root, "protocolVersion");
    const challenge = stringValue(root, "challenge", 128);
    const packageId = stringValue(root, "packageId", 256);
    const packageVersion = stringValue(root, "packageVersion", 128);
    const activationId = idValue(root, "activationId");
    const sessionId = idValue(root, "sessionId", 256);
    if (protocol !== SUNDER_WORKER_PROTOCOL || version !== SUNDER_WORKER_PROTOCOL_VERSION) {
      throw new ProtocolError("Host requested an unsupported worker protocol version.");
    }
    const expectedEnvironment: Readonly<Record<string, string | undefined>> = {
      packageId: process.env.SUNDER_PACKAGE_ID,
      packageVersion: process.env.SUNDER_PACKAGE_VERSION,
      activationId: process.env.SUNDER_ACTIVATION_ID,
      sessionId: process.env.SUNDER_SESSION_ID,
      protocol: process.env.SUNDER_WORKER_PROTOCOL,
    };
    if (expectedEnvironment.packageId !== packageId
      || expectedEnvironment.packageVersion !== packageVersion
      || expectedEnvironment.activationId !== activationId
      || expectedEnvironment.sessionId !== sessionId
      || expectedEnvironment.protocol !== protocol) {
      throw new ProtocolError("host.hello does not match the sanitized process activation environment.");
    }
    const declared = providerArray(root.providers);
    const advertised = [...this.#providers.values()].map(providerWire).sort(compareProviders);
    if (JSON.stringify([...declared].sort(compareProviders)) !== JSON.stringify(advertised)) {
      throw new ProtocolError("Registered providers do not exactly match host-declared providers.");
    }
    await this.#writer.write({
      type: "worker.ready",
      protocol: SUNDER_WORKER_PROTOCOL,
      protocolVersion: SUNDER_WORKER_PROTOCOL_VERSION,
      challenge,
      packageId,
      packageVersion,
      activationId,
      sessionId,
      providers: advertised,
    } satisfies WorkerEnvelope);
    this.#phase = "ready";
  }

  async #handleActivate(root: Readonly<Record<string, JsonValue>>): Promise<void> {
    only(root, "type", "sessionGeneration");
    if (this.#phase !== "ready") throw new ProtocolError("host.activate is duplicate or out of order.");
    const sessionGeneration = integerValue(root, "sessionGeneration");
    this.#phase = "active";
    await this.#writer.write({ type: "worker.activated", sessionGeneration } satisfies WorkerEnvelope);
    this.#activationOperation = Promise.resolve().then(() => this.#options.onActivated?.(this.#client));
    void this.#activationOperation.catch((error: unknown) => this.#fail(error));
  }

  #beginHostInvocation(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "id", "kind", "providerId", "serviceId", "methodId", "request", "context");
    if (this.#phase !== "active") throw new ProtocolError("Host invocation arrived before activation.");
    const id = idValue(root, "id");
    this.#rememberHostId(id);
    if (this.#hostInvocations.size >= this.#limits.maxInboundCalls) {
      throw new ProtocolError("Host exceeded the worker outstanding invocation limit.");
    }
    const kind = stringValue(root, "kind", 32);
    if (kind !== "unary" && kind !== "server-stream") throw new ProtocolError("Host invocation kind is invalid.");
    const providerId = stringValue(root, "providerId", 256);
    const provider = this.#providers.get(providerId);
    if (provider === undefined) throw new ProtocolError(`Host invoked undeclared provider '${providerId}'.`);
    const serviceId = stringValue(root, "serviceId", 128);
    const methodId = stringValue(root, "methodId", 128);
    const contextValue = objectValue(root.context, "Host invocation context");
    only(contextValue, "callerPackageId", "callerPackageVersion", "deadlineUtc", "callDepth", "provider");
    const deadlineUtc = stringValue(contextValue, "deadlineUtc", 64);
    const deadline = parseUtcTimestamp(deadlineUtc);
    if (!Number.isFinite(deadline)) throw new ProtocolError("Host invocation deadline is invalid.");
    // The Host owns deadline cancellation so only it classifies authenticated deadline failures.
    const controller = new AbortController();
    const invocation: HostInvocation = { id, controller, cancelled: false };
    this.#hostInvocations.set(id, invocation);
    const context: RpcInvocationContext = Object.freeze({
      callerPackageId: stringValue(contextValue, "callerPackageId", 256),
      callerPackageVersion: stringValue(contextValue, "callerPackageVersion", 128),
      deadlineUtc,
      callDepth: integerValue(contextValue, "callDepth"),
      provider: providerSnapshot(contextValue.provider),
      content: this.#createContentClient(id, controller.signal),
      signal: controller.signal,
    });
    const request = requiredValue(root, "request");
    const operation = kind === "unary"
      ? this.#invokeUnary(invocation, provider, context, serviceId, methodId, request)
      : this.#invokeStream(invocation, provider, context, serviceId, methodId, request);
    invocation.operation = operation.finally(() => this.#hostInvocations.delete(id));
    void invocation.operation.catch((error: unknown) => this.#fail(error));
  }

  async #invokeUnary(
    invocation: HostInvocation,
    provider: RpcProviderRegistration,
    context: RpcInvocationContext,
    serviceId: string,
    methodId: string,
    request: JsonValue,
  ): Promise<void> {
    try {
      const result = await provider.handler.invokeUnary(context, serviceId, methodId, request);
      assertJsonValue(result);
      await this.#writer.write({ type: "worker.result", id: invocation.id, value: result } satisfies WorkerEnvelope);
    } catch (error) {
      await this.#writeProviderError(invocation, error);
    }
  }

  async #invokeStream(
    invocation: HostInvocation,
    provider: RpcProviderRegistration,
    context: RpcInvocationContext,
    serviceId: string,
    methodId: string,
    request: JsonValue,
  ): Promise<void> {
    try {
      const stream = provider.handler.invokeServerStream(context, serviceId, methodId, request);
      if (stream === null || typeof stream !== "object" || !(Symbol.asyncIterator in stream)) {
        throw new TypeError("Provider returned a non-AsyncIterable stream.");
      }
      for await (const event of stream) {
        if (context.signal.aborted) throw abortError();
        assertJsonValue(event);
        await this.#writer.write({ type: "worker.event", id: invocation.id, value: event } satisfies WorkerEnvelope);
      }
      await this.#writer.write({ type: "worker.complete", id: invocation.id } satisfies WorkerEnvelope);
    } catch (error) {
      await this.#writeProviderError(invocation, error);
    }
  }

  async #writeProviderError(invocation: HostInvocation, error: unknown): Promise<void> {
    const wire = error instanceof RpcError
      ? error.error
      : invocation.controller.signal.aborted
        ? { kind: "cancelled", code: "rpc.call.cancelled", message: "The RPC call was cancelled." } as const
        : { kind: "provider-fault", code: "rpc.provider.handler-fault", message: "The process provider handler failed." } as const;
    await this.#writer.write({ type: "worker.error", id: invocation.id, error: wire } satisfies WorkerEnvelope);
    if (!(error instanceof RpcError) && !invocation.controller.signal.aborted) safeStderr("Process provider handler failed", error);
  }

  #cancelHostInvocation(root: Readonly<Record<string, JsonValue>>): void {
    only(root, "type", "id");
    const id = idValue(root, "id");
    const invocation = this.#hostInvocations.get(id);
    if (invocation === undefined) {
      // A terminal response and Host cancellation can cross on independent streams.
      if (this.#rememberedHostIds.has(id)) return;
      throw new ProtocolError(`Host sent unsolicited cancellation '${id}'.`);
    }
    if (invocation.cancelled) throw new ProtocolError(`Host sent duplicate cancellation '${id}'.`);
    invocation.cancelled = true;
    invocation.controller.abort(abortError());
  }

  #handleClientResponse(type: string, root: Readonly<Record<string, JsonValue>>): void {
    if (type === "host.result" || type === "host.event") only(root, "type", "id", "value");
    else if (type === "host.complete") only(root, "type", "id");
    else only(root, "type", "id", "error");
    const id = idValue(root, "id");
    const call = this.#clientCalls.get(id);
    if (call === undefined || call.terminal) throw new ProtocolError(`Host sent unsolicited, duplicate, or post-terminal response '${id}'.`);
    if (type === "host.event") {
      if (call.kind !== "stream") throw new ProtocolError("Host sent an event for a unary worker call.");
      if (!call.cancelled) call.queue.push(requiredValue(root, "value"));
      return;
    }
    call.terminal = true;
    this.#clientCalls.delete(id);
    call.cleanup();
    if (type === "host.result") {
      if (call.kind !== "unary") throw new ProtocolError("Host sent a unary result for a stream worker call.");
      if (!call.cancelled) call.resolve(requiredValue(root, "value"));
    } else if (type === "host.complete") {
      if (call.kind !== "stream") throw new ProtocolError("Host completed a unary worker call without a result.");
      if (!call.cancelled) call.queue.close();
    } else {
      const error = rpcError(root.error);
      if (!call.cancelled) {
        call.reject(error);
        call.queue.close(error);
      }
    }
  }

  async #handleShutdown(root: Readonly<Record<string, JsonValue>>): Promise<void> {
    only(root, "type", "shutdownId", "reason");
    if (this.#phase === "stopping" || this.#phase === "stopped") throw new ProtocolError("Host shutdown is duplicate.");
    const shutdownId = idValue(root, "shutdownId");
    const reason = stringValue(root, "reason", 128);
    this.#phase = "stopping";
    for (const invocation of this.#hostInvocations.values()) invocation.controller.abort(abortError());
    for (const call of this.#clientCalls.values()) this.#cancelClientCall(call);
    await Promise.allSettled([...this.#hostInvocations.values()].map((invocation) => invocation.operation ?? Promise.resolve()));
    await this.#activationOperation;
    await this.#options.onShutdown?.(Object.freeze({ reason }));
    await this.#writer.write({ type: "worker.shutdown-ack", shutdownId } satisfies WorkerEnvelope);
    await this.#writer.flush();
    this.#phase = "stopped";
    this.#resolveRun?.();
  }

  #createClient(): RpcClient {
    return Object.freeze({
      getProvider: (endpointReference: string, options?: RpcCallOptions) => this.#unaryClientCall<RpcProviderSnapshot | null>(
        { type: "worker.get-provider", endpointReference }, options),
      discover: (contractId: string, options?: RpcCallOptions) => this.#unaryClientCall<RpcCatalogSnapshot>(
        { type: "worker.discover", contractId }, options),
      watch: (afterRevision: number, afterSequence: number, options?: RpcCallOptions) => this.#streamClientCall<RpcCatalogEvent>(
        { type: "worker.watch", afterRevision, afterSequence }, options),
      invoke: <TRequest extends JsonValue, TResponse extends JsonValue>(
        endpointReference: string,
        serviceId: string,
        methodId: string,
        request: TRequest,
        options?: RpcCallOptions,
      ) => this.#unaryClientCall<TResponse>({
        type: "worker.invoke",
        endpointReference,
        serviceId,
        methodId,
        request,
        deadlineUtc: deadline(options),
      }, options),
      subscribe: <TRequest extends JsonValue, TEvent extends JsonValue>(
        endpointReference: string,
        serviceId: string,
        methodId: string,
        request: TRequest,
        options?: RpcCallOptions,
      ) => this.#streamClientCall<TEvent>({
        type: "worker.subscribe",
        endpointReference,
        serviceId,
        methodId,
        request,
        deadlineUtc: deadline(options),
      }, options),
    });
  }

  #createContentClient(invocationId: string, signal: AbortSignal): RpcContentClient {
    return Object.freeze({
      registerFile: async (
        filePath: string,
        options: RpcContentRegistrationOptions,
      ): Promise<RpcContentReference> => {
        const value = await this.#unaryClientCall<JsonValue>({
          type: "worker.content-register",
          invocationId,
          filePath: boundedString(filePath, "RPC content filePath", 4096),
          options: contentRegistrationOptions(options),
        }, { signal });
        return contentReference(value);
      },
      openFile: async (reference: RpcContentReference): Promise<RpcContentFile> => {
        const value = await this.#unaryClientCall<JsonValue>({
          type: "worker.content-open",
          invocationId,
          reference: contentReference(reference),
        }, { signal });
        const handle = contentFileHandle(value);
        let discard: Promise<void> | undefined;
        return Object.freeze({
          filePath: handle.filePath,
          discard: (): Promise<void> => {
            discard ??= this.#unaryClientCall<JsonValue>({
              type: "worker.content-discard",
              invocationId,
              handleId: handle.handleId,
            }, { signal }).then(() => undefined);
            return discard;
          },
        });
      },
    });
  }

  #unaryClientCall<T>(envelope: Readonly<Record<string, unknown>>, options?: RpcCallOptions): Promise<T> {
    const call = this.#newClientCall("unary", options, Object.hasOwn(envelope, "deadlineUtc"));
    const request = { ...envelope, id: call.id } as WorkerEnvelope;
    void this.#writer.write(request).catch((error: unknown) => this.#fail(error));
    return call.promise as Promise<T>;
  }

  #streamClientCall<T>(envelope: Readonly<Record<string, unknown>>, options?: RpcCallOptions): AsyncIterable<T> {
    const call = this.#newClientCall("stream", options, Object.hasOwn(envelope, "deadlineUtc"));
    const request = { ...envelope, id: call.id } as WorkerEnvelope;
    void this.#writer.write(request).catch((error: unknown) => this.#fail(error));
    return call.queue.iterate<T>(() => this.#cancelClientCall(call));
  }

  #newClientCall(kind: "unary" | "stream", options: RpcCallOptions | undefined, hostOwnsDeadline: boolean): ClientCall {
    if (this.#phase !== "active") throw new RpcError({ kind: "unavailable", code: "rpc.worker.not-active", message: "The process worker activation is not active." });
    if (this.#clientCalls.size >= this.#limits.maxOutboundCalls) {
      throw new RpcError({ kind: "resource-exhausted", code: "rpc.worker.call-limit", message: "The worker RPC call limit was reached." });
    }
    if (options?.signal?.aborted === true) throw abortError();
    const deadlineMilliseconds = options?.deadline?.getTime();
    if (deadlineMilliseconds !== undefined && !Number.isFinite(deadlineMilliseconds)) {
      throw new RangeError("RPC deadline must be a valid Date.");
    }
    const id = `w${++this.#clientId}`;
    let resolvePromise: (value: JsonValue) => void = () => undefined;
    let rejectPromise: (error: unknown) => void = () => undefined;
    const promise = new Promise<JsonValue>((resolve, reject) => {
      resolvePromise = resolve;
      rejectPromise = reject;
    });
    if (kind === "stream") void promise.catch(() => undefined);
    const call: ClientCall = {
      id,
      kind,
      queue: new AsyncQueue(this.#limits.maxStreamQueueMessages),
      promise,
      resolve: resolvePromise,
      reject: rejectPromise,
      cancelled: false,
      terminal: false,
      cleanup: () => undefined,
    };
    const onAbort = (): void => this.#cancelClientCall(call);
    options?.signal?.addEventListener("abort", onAbort, { once: true });
    const cancelDeadline = deadlineMilliseconds === undefined || hostOwnsDeadline
      ? () => undefined
      : scheduleDeadline(deadlineMilliseconds, onAbort);
    call.cleanup = () => {
      options?.signal?.removeEventListener("abort", onAbort);
      cancelDeadline();
    };
    this.#clientCalls.set(id, call);
    return call;
  }

  #cancelClientCall(call: ClientCall): void {
    if (call.terminal || call.cancelled) return;
    call.cancelled = true;
    call.cleanup();
    const error = abortError();
    call.reject(error);
    call.queue.close(error);
    void this.#writer.write({ type: "worker.cancel", id: call.id } satisfies WorkerEnvelope).catch((writeError: unknown) => this.#fail(writeError));
  }

  #rememberHostId(id: string): void {
    if (this.#rememberedHostIds.has(id)) throw new ProtocolError(`Host reused duplicate invocation id '${id}'.`);
    this.#rememberedHostIds.add(id);
    this.#rememberedHostIdOrder.push(id);
    while (this.#rememberedHostIdOrder.length > this.#limits.maxRememberedIds) {
      const removed = this.#rememberedHostIdOrder.shift();
      if (removed !== undefined) this.#rememberedHostIds.delete(removed);
    }
  }

  #fail(error: unknown): void {
    if (this.#phase === "stopped") return;
    this.#phase = "stopped";
    for (const invocation of this.#hostInvocations.values()) {
      invocation.controller.abort(error);
    }
    for (const call of this.#clientCalls.values()) {
      call.reject(error);
      call.queue.close(error);
      call.cleanup();
    }
    safeStderr("sunder.worker.v1 protocol failure", error);
    process.exitCode = 70;
    this.#rejectRun?.(error);
  }
}

class ProtocolWriter {
  readonly #limits: WorkerLimits;
  #queued = 0;
  #tail = Promise.resolve();

  public constructor(limits: WorkerLimits) {
    this.#limits = limits;
  }

  public write(envelope: WorkerEnvelope): Promise<void> {
    if (this.#queued >= this.#limits.maxWriteQueue) throw new ProtocolError("Worker protocol write queue exceeded its bound.");
    const frame = encodeFrame(envelope as unknown as Readonly<Record<string, unknown>>, this.#limits);
    this.#queued += 1;
    const write = this.#tail.then(async () => {
      if (!process.stdout.write(frame)) await once(process.stdout, "drain");
    }).finally(() => {
      this.#queued -= 1;
    });
    this.#tail = write.catch(() => undefined);
    return write;
  }

  public flush(): Promise<void> {
    return this.#tail;
  }
}

class AsyncQueue {
  readonly #values: JsonValue[] = [];
  readonly #waiters: Array<{ resolve: (value: IteratorResult<JsonValue>) => void; reject: (error: unknown) => void }> = [];
  readonly #capacity: number;
  #closed = false;
  #error: unknown;

  public constructor(capacity: number) {
    this.#capacity = capacity;
  }

  public push(value: JsonValue): void {
    if (this.#closed) throw new ProtocolError("Received a stream event after terminal completion.");
    const waiter = this.#waiters.shift();
    if (waiter !== undefined) {
      waiter.resolve({ done: false, value });
      return;
    }
    if (this.#values.length >= this.#capacity) throw new ProtocolError("Worker stream event queue exceeded its bound.");
    this.#values.push(value);
  }

  public close(error?: unknown): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#error = error;
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
    const value = this.#values.shift();
    if (value !== undefined) return Promise.resolve({ done: false, value });
    if (this.#closed) {
      return this.#error === undefined
        ? Promise.resolve({ done: true, value: undefined })
        : Promise.reject(this.#error);
    }
    return new Promise((resolve, reject) => this.#waiters.push({ resolve, reject }));
  }
}

interface HostInvocation {
  readonly id: string;
  readonly controller: AbortController;
  cancelled: boolean;
  operation?: Promise<void>;
}

interface ClientCall {
  readonly id: string;
  readonly kind: "unary" | "stream";
  readonly queue: AsyncQueue;
  readonly promise: Promise<JsonValue>;
  readonly resolve: (value: JsonValue) => void;
  readonly reject: (error: unknown) => void;
  cancelled: boolean;
  terminal: boolean;
  cleanup: () => void;
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
  if (!/^[A-Za-z0-9_-]+$/u.test(value)) throw new ProtocolError(`Envelope property '${name}' is not a valid id.`);
  return value;
}

function integerValue(root: Readonly<Record<string, JsonValue>>, name: string): number {
  const value = root[name];
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) {
    throw new ProtocolError(`Envelope property '${name}' must be a non-negative safe integer.`);
  }
  return value;
}

function boundedString(value: unknown, label: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximum) {
    throw new TypeError(`${label} must be a non-empty bounded string.`);
  }
  return value;
}

function contentRegistrationOptions(options: RpcContentRegistrationOptions): Readonly<Record<string, JsonValue>> {
  if (options === null || typeof options !== "object") {
    throw new TypeError("RPC content registration options must be an object.");
  }
  const length = options.length ?? null;
  if (length !== null && (!Number.isSafeInteger(length) || length < 0)) {
    throw new RangeError("RPC content length must be a non-negative safe integer.");
  }
  if (options.expiresAt !== undefined && !Number.isFinite(options.expiresAt.getTime())) {
    throw new RangeError("RPC content expiry must be a valid Date.");
  }
  const expiresAtUtc = options.expiresAt === undefined ? null : formatUtcTimestamp(options.expiresAt);
  const repeatability = options.repeatability ?? "single-use";
  if (repeatability !== "single-use" && repeatability !== "repeatable") {
    throw new RangeError("RPC content repeatability is invalid.");
  }
  const maximumUses = options.maximumUses ?? 1;
  if (!Number.isSafeInteger(maximumUses) || maximumUses < 1
    || (repeatability === "single-use" && maximumUses !== 1)) {
    throw new RangeError("RPC content maximumUses is invalid.");
  }
  return Object.freeze({
    mediaType: boundedString(options.mediaType, "RPC content mediaType", 128),
    fileName: boundedString(options.fileName, "RPC content fileName", 255),
    length,
    expiresAtUtc,
    repeatability,
    maximumUses,
  });
}

function contentReference(value: unknown): RpcContentReference {
  const root = objectValue(value as JsonValue, "RPC content reference");
  only(root, "id", "length", "sha256", "mediaType", "fileName", "expiresAtUtc", "repeatability");
  const sha256 = stringValue(root, "sha256", 64);
  if (!/^[0-9a-fA-F]{64}$/u.test(sha256)) throw new ProtocolError("RPC content SHA-256 is invalid.");
  const expiresAtUtc = stringValue(root, "expiresAtUtc", 64);
  if (!Number.isFinite(parseUtcTimestamp(expiresAtUtc))) {
    throw new ProtocolError("RPC content expiry is invalid.");
  }
  const repeatability = stringValue(root, "repeatability", 32);
  if (repeatability !== "single-use" && repeatability !== "repeatable") {
    throw new ProtocolError("RPC content repeatability is invalid.");
  }
  return Object.freeze({
    id: idValue(root, "id"),
    length: integerValue(root, "length"),
    sha256,
    mediaType: stringValue(root, "mediaType", 128),
    fileName: stringValue(root, "fileName", 255),
    expiresAtUtc,
    repeatability,
  });
}

function contentFileHandle(value: JsonValue): Readonly<{ handleId: string; filePath: string }> {
  const root = objectValue(value, "RPC content file handle");
  only(root, "handleId", "filePath");
  return Object.freeze({
    handleId: idValue(root, "handleId"),
    filePath: stringValue(root, "filePath", 4096),
  });
}

function providerArray(value: JsonValue | undefined): readonly ProviderWireIdentity[] {
  if (!Array.isArray(value)) throw new ProtocolError("Envelope providers must be an array.");
  return value.map((item) => {
    const provider = objectValue(item, "Provider identity");
    only(provider, "providerId", "contractId", "contractVersion", "contractSha256");
    return Object.freeze({
      providerId: stringValue(provider, "providerId", 256),
      contractId: stringValue(provider, "contractId", 256),
      contractVersion: stringValue(provider, "contractVersion", 128),
      contractSha256: stringValue(provider, "contractSha256", 64),
    });
  });
}

function providerSnapshot(value: JsonValue | undefined): RpcProviderSnapshot {
  const provider = objectValue(value, "Provider snapshot");
  only(provider, "packageId", "packageVersion", "providerId", "contractId", "contractVersion", "contractSha256", "activationId", "activationEpoch", "sessionGeneration", "endpointReference", "catalogRevision", "state", "faultCode");
  const state = stringValue(provider, "state", 32);
  if (state !== "active" && state !== "inactive" && state !== "faulted") throw new ProtocolError("Provider state is invalid.");
  const faultCode = provider.faultCode;
  if (faultCode !== null && typeof faultCode !== "string") throw new ProtocolError("Provider faultCode is invalid.");
  return Object.freeze({
    packageId: stringValue(provider, "packageId", 256),
    packageVersion: stringValue(provider, "packageVersion", 128),
    providerId: stringValue(provider, "providerId", 256),
    contractId: stringValue(provider, "contractId", 256),
    contractVersion: stringValue(provider, "contractVersion", 128),
    contractSha256: stringValue(provider, "contractSha256", 64),
    activationId: idValue(provider, "activationId"),
    activationEpoch: integerValue(provider, "activationEpoch"),
    sessionGeneration: integerValue(provider, "sessionGeneration"),
    endpointReference: stringValue(provider, "endpointReference", 256),
    catalogRevision: integerValue(provider, "catalogRevision"),
    state,
    faultCode,
  });
}

function rpcError(value: JsonValue | undefined): RpcError {
  const error = objectValue(value, "RPC error");
  only(error, "kind", "code", "message");
  const kind = stringValue(error, "kind", 64) as RpcErrorValue["kind"];
  const allowed = new Set<RpcErrorValue["kind"]>([
    "domain", "permission-denied", "not-found", "stale-endpoint", "validation", "deadline-exceeded",
    "cancelled", "resource-exhausted", "unavailable", "provider-faulted", "protocol",
  ]);
  if (!allowed.has(kind)) throw new ProtocolError(`Host returned unknown RPC error kind '${kind}'.`);
  return new RpcError({ kind, code: stringValue(error, "code", 128), message: stringValue(error, "message", 512) });
}

function validateProviderIdentity(provider: RpcProviderRegistration): void {
  if (!isPackageId(provider.providerId)) throw new Error("Provider providerId is invalid.");
  if (!isPackageId(provider.contractId)) throw new Error("Provider contractId is invalid.");
  if (!isSemanticVersion(provider.contractVersion)) throw new Error("Provider contractVersion is invalid.");
  if (!/^[0-9a-f]{64}$/u.test(provider.contractSha256)) throw new Error("Provider contractSha256 is invalid.");
}

function providerWire(provider: RpcProviderRegistration): ProviderWireIdentity {
  return Object.freeze({
    providerId: provider.providerId,
    contractId: provider.contractId,
    contractVersion: provider.contractVersion,
    contractSha256: provider.contractSha256,
  });
}

function compareProviders(left: ProviderWireIdentity, right: ProviderWireIdentity): number {
  return left.providerId.localeCompare(right.providerId, "en-US");
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

function abortError(): Error {
  const error = new Error("The operation was aborted.");
  error.name = "AbortError";
  return error;
}

function assertJsonValue(value: unknown): asserts value is JsonValue {
  const seen = new Set<object>();
  const visit = (item: unknown, depth: number): void => {
    if (depth > 64) throw new TypeError("Provider JSON output exceeds the depth limit.");
    if (item === null || typeof item === "string" || typeof item === "boolean") return;
    if (typeof item === "number") {
      if (!Number.isFinite(item)) throw new TypeError("Provider JSON output contains a non-finite number.");
      return;
    }
    if (typeof item !== "object") throw new TypeError("Provider output is not a JSON value.");
    if (seen.has(item)) throw new TypeError("Provider JSON output contains a cycle.");
    seen.add(item);
    if (Array.isArray(item)) {
      for (const child of item) visit(child, depth + 1);
    } else {
      const prototype = Object.getPrototypeOf(item) as object | null;
      if (prototype !== Object.prototype && prototype !== null) throw new TypeError("Provider JSON object has a non-plain prototype.");
      for (const child of Object.values(item)) visit(child, depth + 1);
    }
    seen.delete(item);
  };
  visit(value, 1);
}
