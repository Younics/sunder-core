export type JsonPrimitive = null | boolean | number | string;
export type JsonValue = JsonPrimitive | readonly JsonValue[] | { readonly [key: string]: JsonValue };

export const SUNDER_WORKER_PROTOCOL = "sunder.worker.v1" as const;
export const SUNDER_WORKER_PROTOCOL_VERSION = 1 as const;

export type RpcMethodKind = "unary" | "server-stream";

export interface RpcMethodDescriptor {
  readonly methodId: string;
  readonly kind: RpcMethodKind;
  readonly requestSchema: string | { readonly $ref: string };
  readonly responseSchema?: string | { readonly $ref: string };
  readonly eventSchema?: string | { readonly $ref: string };
}

export interface RpcServiceDescriptor {
  readonly serviceId: string;
  readonly methods: readonly RpcMethodDescriptor[] | Readonly<Record<string, RpcMethodDescriptor>>;
}

export interface RpcContractDescriptor {
  readonly $schema?: "https://json-schema.org/draft/2020-12/schema";
  readonly descriptorVersion: 1;
  readonly contractId: string;
  readonly version: string;
  readonly services: readonly RpcServiceDescriptor[] | Readonly<Record<string, RpcServiceDescriptor>>;
  readonly $defs: Readonly<Record<string, JsonSchema>>;
}

export type JsonSchema = Readonly<Record<string, JsonValue>>;

export type RpcErrorKind =
  | "domain"
  | "permission-denied"
  | "not-found"
  | "stale-endpoint"
  | "validation"
  | "deadline-exceeded"
  | "cancelled"
  | "resource-exhausted"
  | "unavailable"
  | "provider-faulted"
  | "protocol";

export interface RpcErrorValue {
  readonly kind: RpcErrorKind;
  readonly code: string;
  readonly message: string;
}

export class RpcError extends Error {
  public readonly error: RpcErrorValue;

  public constructor(error: RpcErrorValue) {
    super(error.message);
    this.name = "RpcError";
    this.error = Object.freeze({ ...error });
  }
}

export type RpcProviderState = "active" | "inactive" | "faulted";

export interface RpcProviderSnapshot {
  readonly packageId: string;
  readonly packageVersion: string;
  readonly providerId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly contractSha256: string;
  readonly activationId: string;
  readonly activationEpoch: number;
  readonly sessionGeneration: number;
  readonly endpointReference: string;
  readonly catalogRevision: number;
  readonly state: RpcProviderState;
  readonly faultCode: string | null;
}

export interface RpcCatalogSnapshot {
  readonly revision: number;
  readonly sequence: number;
  readonly providers: readonly RpcProviderSnapshot[];
  readonly resetRequired: boolean;
}

export type RpcCatalogEventKind =
  | "added"
  | "removed"
  | "activated"
  | "deactivated"
  | "faulted"
  | "reset-required";

export interface RpcCatalogEvent {
  readonly revision: number;
  readonly sequence: number;
  readonly kind: RpcCatalogEventKind;
  readonly provider: RpcProviderSnapshot | null;
}

export type RpcContentRepeatability = "single-use" | "repeatable";

export interface RpcContentReference extends Readonly<Record<string, JsonValue>> {
  readonly id: string;
  readonly length: number;
  readonly sha256: string;
  readonly mediaType: string;
  readonly fileName: string;
  readonly expiresAtUtc: string;
  readonly repeatability: RpcContentRepeatability;
}

export interface RpcContentRegistrationOptions {
  readonly mediaType: string;
  readonly fileName: string;
  readonly length?: number;
  readonly expiresAt?: Date;
  readonly repeatability?: RpcContentRepeatability;
  readonly maximumUses?: number;
}

export interface RpcContentFile {
  readonly filePath: string;
  discard(): Promise<void>;
}

export interface RpcContentClient {
  registerFile(filePath: string, options: RpcContentRegistrationOptions): Promise<RpcContentReference>;
  openFile(reference: RpcContentReference): Promise<RpcContentFile>;
}

export interface RpcCallOptions {
  readonly signal?: AbortSignal;
  readonly deadline?: Date;
}

export interface RpcClient {
  getProvider(endpointReference: string, options?: RpcCallOptions): Promise<RpcProviderSnapshot | null>;
  discover(contractId: string, options?: RpcCallOptions): Promise<RpcCatalogSnapshot>;
  watch(afterRevision: number, afterSequence: number, options?: RpcCallOptions): AsyncIterable<RpcCatalogEvent>;
  invoke<TRequest extends JsonValue, TResponse extends JsonValue>(
    endpointReference: string,
    serviceId: string,
    methodId: string,
    request: TRequest,
    options?: RpcCallOptions,
  ): Promise<TResponse>;
  subscribe<TRequest extends JsonValue, TEvent extends JsonValue>(
    endpointReference: string,
    serviceId: string,
    methodId: string,
    request: TRequest,
    options?: RpcCallOptions,
  ): AsyncIterable<TEvent>;
}

export interface RpcProviderIdentity {
  readonly providerId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly contractSha256: string;
}

export interface RpcContractIdentity {
  readonly contractId: string;
  readonly contractVersion: string;
  readonly contractSha256: string;
}

export interface RpcInvocationContext {
  readonly callerPackageId: string;
  readonly callerPackageVersion: string;
  readonly deadlineUtc: string;
  readonly callDepth: number;
  readonly provider: RpcProviderSnapshot;
  readonly content: RpcContentClient;
  readonly signal: AbortSignal;
}

export interface RpcProviderHandler {
  invokeUnary(
    context: RpcInvocationContext,
    serviceId: string,
    methodId: string,
    request: JsonValue,
  ): Promise<JsonValue> | JsonValue;
  invokeServerStream(
    context: RpcInvocationContext,
    serviceId: string,
    methodId: string,
    request: JsonValue,
  ): AsyncIterable<JsonValue>;
}

export interface RpcProviderRegistration extends RpcProviderIdentity {
  readonly handler: RpcProviderHandler;
}

export interface WorkerOptions {
  readonly providers: readonly RpcProviderRegistration[];
  readonly onActivated?: (client: RpcClient) => Promise<void> | void;
  readonly onShutdown?: (context: WorkerShutdownContext) => Promise<void> | void;
  readonly limits?: Partial<WorkerLimits>;
}

export interface WorkerShutdownContext {
  readonly reason: string;
}

export interface WorkerLimits {
  readonly maxFrameBytes: number;
  readonly maxHeaderBytes: number;
  readonly maxMessageDepth: number;
  readonly maxInboundCalls: number;
  readonly maxOutboundCalls: number;
  readonly maxStreamQueueMessages: number;
  readonly maxWriteQueue: number;
  readonly maxRememberedIds: number;
}

export interface ProviderWireIdentity extends RpcProviderIdentity {}

export type HostEnvelope =
  | Readonly<{ type: "host.hello"; protocol: typeof SUNDER_WORKER_PROTOCOL; protocolVersion: 1; challenge: string; packageId: string; packageVersion: string; activationId: string; sessionId: string; providers: readonly ProviderWireIdentity[] }>
  | Readonly<{ type: "host.activate"; sessionGeneration: number }>
  | Readonly<{ type: "host.invoke"; id: string; kind: RpcMethodKind; providerId: string; serviceId: string; methodId: string; request: JsonValue; context: Omit<RpcInvocationContext, "signal" | "content"> }>
  | Readonly<{ type: "host.cancel"; id: string }>
  | Readonly<{ type: "host.result"; id: string; value: JsonValue }>
  | Readonly<{ type: "host.event"; id: string; value: JsonValue }>
  | Readonly<{ type: "host.complete"; id: string }>
  | Readonly<{ type: "host.error"; id: string; error: RpcErrorValue }>
  | Readonly<{ type: "host.shutdown"; shutdownId: string; reason: string }>;

export type WorkerEnvelope =
  | Readonly<{ type: "worker.ready"; protocol: typeof SUNDER_WORKER_PROTOCOL; protocolVersion: 1; challenge: string; packageId: string; packageVersion: string; activationId: string; sessionId: string; providers: readonly ProviderWireIdentity[] }>
  | Readonly<{ type: "worker.activated"; sessionGeneration: number }>
  | Readonly<{ type: "worker.result"; id: string; value: JsonValue }>
  | Readonly<{ type: "worker.event"; id: string; value: JsonValue }>
  | Readonly<{ type: "worker.complete"; id: string }>
  | Readonly<{ type: "worker.error"; id: string; error: RpcErrorValue | Readonly<{ kind: "provider-fault"; code: string; message: string }> }>
  | Readonly<{ type: "worker.get-provider"; id: string; endpointReference: string }>
  | Readonly<{ type: "worker.discover"; id: string; contractId: string }>
  | Readonly<{ type: "worker.watch"; id: string; afterRevision: number; afterSequence: number }>
  | Readonly<{ type: "worker.invoke"; id: string; endpointReference: string; serviceId: string; methodId: string; request: JsonValue; deadlineUtc: string | null }>
  | Readonly<{ type: "worker.subscribe"; id: string; endpointReference: string; serviceId: string; methodId: string; request: JsonValue; deadlineUtc: string | null }>
  | Readonly<{ type: "worker.content-register"; id: string; invocationId: string; filePath: string; options: Readonly<{ mediaType: string; fileName: string; length: number | null; expiresAtUtc: string | null; repeatability: RpcContentRepeatability; maximumUses: number }> }>
  | Readonly<{ type: "worker.content-open"; id: string; invocationId: string; reference: RpcContentReference }>
  | Readonly<{ type: "worker.content-discard"; id: string; invocationId: string; handleId: string }>
  | Readonly<{ type: "worker.cancel"; id: string }>
  | Readonly<{ type: "worker.shutdown-ack"; shutdownId: string }>;
