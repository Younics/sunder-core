import type { JsonValue, RpcCallOptions, RpcInvoker } from "./core";

export type { JsonObject, JsonPrimitive, JsonValue, RpcCallOptions, RpcInvoker } from "./core";

export const SUNDER_WORKER_PROTOCOL = "sunder.worker.v1" as const;
export const SUNDER_WORKER_PROTOCOL_VERSION = 1 as const;

export interface WorkerLimits {
  maxFrameBytes: number;
  maxHeaderBytes: number;
  maxMessageDepth: number;
  maxRpcValueBytes: number;
  maxValueDepth: number;
  maxValueNodes: number;
  maxObjectMembers: number;
  maxEnvelopeReserveBytes: number;
  maxInboundCalls: number;
  maxOutboundCalls: number;
  maxLoggingCalls: number;
  maxCallScopes: number;
  maxContentHandles: number;
  maxPayloadHandles: number;
  maxControlCalls: number;
  maxStreamQueueMessages: number;
  maxWriteQueue: number;
  maxStreamQueueBytes: number;
  maxWriteQueueBytes: number;
  maxRememberedIds: number;
  maxDiagnosticCharacters: number;
  maxContentBytes: number;
  maxPayloadBytes: number;
  maxFilePayloadBytes: number;
  maxContentUses: number;
  inlineDataBytes: number;
}

export const SUNDER_WORKER_LIMITS: Readonly<WorkerLimits> = Object.freeze({
  // Bootstrap ceilings, replaced by host.hello's effective resource profile.
  maxFrameBytes: 64 * 1024 * 1024 + 64 * 1024,
  maxHeaderBytes: 8 * 1024,
  maxMessageDepth: 72,
  maxRpcValueBytes: 64 * 1024 * 1024,
  maxValueDepth: 64,
  maxValueNodes: 8_388_608,
  maxObjectMembers: 131_072,
  maxEnvelopeReserveBytes: 64 * 1024,
  maxInboundCalls: 64,
  maxOutboundCalls: 32,
  maxLoggingCalls: 32,
  maxCallScopes: 64,
  maxContentHandles: 128,
  maxPayloadHandles: 16,
  maxControlCalls: 320,
  maxStreamQueueMessages: 32,
  maxWriteQueue: 128,
  maxStreamQueueBytes: 128 * 1024 * 1024,
  maxWriteQueueBytes: 512 * 1024 * 1024,
  maxRememberedIds: 2048,
  maxDiagnosticCharacters: 4096,
  maxContentBytes: 512 * 1024 * 1024,
  maxPayloadBytes: 1024 * 1024,
  maxFilePayloadBytes: 16 * 1024 * 1024,
  maxContentUses: 32,
  inlineDataBytes: 64 * 1024,
});

export interface WorkerResourceProfile extends Readonly<Record<string, JsonValue>> {
  readonly version: 1;
  readonly rpcValueBytes: number;
  readonly valueDepth: number;
  readonly valueNodes: number;
  readonly objectMembers: number;
  readonly inlineDecodedBytes: number;
  readonly contentReferenceBytes: number;
}
export type RpcMethodKind = "unary" | "server-stream";
export type RpcMethodIdempotency = "none" | "idempotent";

export interface RpcSchemaReference {
  readonly $ref: string;
}

export interface RpcErrorDescriptorV1 {
  readonly description?: string;
  readonly detailsSchema?: RpcSchemaReference;
}

export interface RpcMethodDescriptorV1Base {
  readonly idempotency: RpcMethodIdempotency;
  readonly errors: readonly string[];
  readonly requestSchema: RpcSchemaReference;
}

export type RpcMethodDescriptorV1 =
  | (RpcMethodDescriptorV1Base & {
    readonly kind: "unary";
    readonly responseSchema: RpcSchemaReference;
    readonly eventSchema?: never;
  })
  | (RpcMethodDescriptorV1Base & {
    readonly kind: "server-stream";
    readonly eventSchema: RpcSchemaReference;
    readonly responseSchema?: never;
  });

export interface RpcServiceDescriptorV1 {
  readonly methods: Readonly<Record<string, RpcMethodDescriptorV1>>;
}

export interface RpcContractDescriptorV1 {
  readonly $schema?: "https://json-schema.org/draft/2020-12/schema";
  readonly descriptorVersion: 1;
  readonly schemaProfile: "sunder.rpc.values.v1";
  readonly contractId: string;
  readonly version: string;
  readonly errors: Readonly<Record<string, RpcErrorDescriptorV1>>;
  readonly services: Readonly<Record<string, RpcServiceDescriptorV1>>;
  readonly $defs: Readonly<Record<string, JsonSchema>>;
}

export type RpcMethodDescriptor = RpcMethodDescriptorV1;
export type RpcServiceDescriptor = RpcServiceDescriptorV1;
export type RpcContractDescriptor = RpcContractDescriptorV1;
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
  readonly dispatchState: "not-dispatched" | "possibly-dispatched";
  readonly retrySafety: "safe" | "unsafe";
  readonly details?: JsonValue;
}

export class RpcError extends Error {
  public readonly error: RpcErrorValue;
  public readonly isHostAuthenticated: boolean;

  public constructor(
    error: Omit<RpcErrorValue, "dispatchState" | "retrySafety"> & Partial<Pick<RpcErrorValue, "dispatchState" | "retrySafety">>,
    isHostAuthenticated = false,
  ) {
    super(error.message);
    this.name = "RpcError";
    const value = {
      kind: error.kind,
      code: error.code,
      message: error.message,
      dispatchState: error.dispatchState ?? "not-dispatched",
      retrySafety: error.retrySafety ?? "safe",
    };
    if (error.details !== undefined) {
      if (error.kind !== "domain") throw new TypeError("Only domain RPC errors may carry details.");
      this.error = Object.freeze({ ...value, details: cloneRpcErrorDetails(error.details) });
    } else {
      this.error = Object.freeze(value);
    }
    this.isHostAuthenticated = isHostAuthenticated;
  }
}

export class RpcDomainError extends RpcError {
  public constructor(code: string, message: string, details?: JsonValue) {
    super(details === undefined
      ? { kind: "domain", code, message }
      : { kind: "domain", code, message, details });
    this.name = "RpcDomainError";
  }
}

function cloneRpcErrorDetails(value: unknown, active = new WeakSet<object>()): JsonValue {
  if (value === null || typeof value === "string" || typeof value === "boolean") return value;
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new TypeError("RPC error details must contain only finite JSON numbers.");
    return value;
  }
  if (typeof value !== "object") throw new TypeError("RPC error details must be a JSON value.");
  if (active.has(value)) throw new TypeError("RPC error details must not contain cycles.");
  active.add(value);
  try {
    if (Array.isArray(value)) {
      return Object.freeze(value.map((item) => cloneRpcErrorDetails(item, active)));
    }
    const prototype = Object.getPrototypeOf(value) as unknown;
    if (prototype !== Object.prototype && prototype !== null) {
      throw new TypeError("RPC error details must contain only JSON objects.");
    }
    const output: Record<string, JsonValue> = Object.create(null) as Record<string, JsonValue>;
    for (const [name, item] of Object.entries(value)) {
      output[name] = cloneRpcErrorDetails(item, active);
    }
    copyResourceIntegerTokens(value,output);
    return Object.freeze(output);
  } finally {
    active.delete(value);
  }
}

export type RpcProviderState = "active" | "inactive" | "faulted";

export interface RpcProviderSnapshot {
  readonly packageId: string;
  readonly packageVersion: string;
  readonly providerId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
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

export type RpcContentSource = AsyncIterable<Uint8Array>;

export interface RpcContentFile {
  readonly filePath: string;
  readonly stream: AsyncIterable<Uint8Array>;
  discard(): Promise<void>;
}

export interface RpcContentClient {
  /** @internal Consumes transport-private provenance for one exact registration attempt. */
  consumeResponseContentRetirement?(error: unknown, registrationSignal: AbortSignal): boolean;
  register(source: RpcContentSource, options: RpcContentRegistrationOptions, call?:RpcCallOptions): Promise<RpcContentReference>;
  registerFile(filePath: string, options: RpcContentRegistrationOptions, call?:RpcCallOptions): Promise<RpcContentReference>;
  open(reference: RpcContentReference, options?:RpcCallOptions): Promise<RpcContentFile>;
}

export interface RpcCallerContentClient {
  register(endpointReference: string, source: RpcContentSource, options: RpcContentRegistrationOptions, call?:RpcCallOptions): Promise<RpcContentReference>;
  registerFile(endpointReference: string, filePath: string, options: RpcContentRegistrationOptions, call?:RpcCallOptions): Promise<RpcContentReference>;
  open(reference: RpcContentReference, options?:RpcCallOptions): Promise<RpcContentFile>;
}

export interface RpcCallScope extends RpcInvoker<string> {
  readonly deadline: Date;
  readonly signal: AbortSignal;
  readonly content: RpcCallerContentClient;
  getProvider(endpointReference: string, options?: RpcCallOptions): Promise<RpcProviderSnapshot | null>;
  reportInvariantViolation(endpointReference: string, error: unknown, options?: RpcCallOptions): Promise<boolean>;
  discover(contractId: string, options?: RpcCallOptions): Promise<RpcCatalogSnapshot>;
  watch(afterRevision: number, afterSequence: number, options?: RpcCallOptions): AsyncIterable<RpcCatalogEvent>;
  close(): Promise<void>;
}

export interface RpcClient extends RpcInvoker<string> {
  /** Effective Host deadline of the current parent invocation, when present. */
  readonly deadline?:Date;
  createCallScope(options?: RpcCallOptions): Promise<RpcCallScope>;
  getProvider(endpointReference: string, options?: RpcCallOptions): Promise<RpcProviderSnapshot | null>;
  reportInvariantViolation(endpointReference: string, error: unknown, options?: RpcCallOptions): Promise<boolean>;
  discover(contractId: string, options?: RpcCallOptions): Promise<RpcCatalogSnapshot>;
  watch(afterRevision: number, afterSequence: number, options?: RpcCallOptions): AsyncIterable<RpcCatalogEvent>;
}

export interface RpcProviderIdentity {
  readonly providerId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
}

export interface RpcContractIdentity {
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
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

export type RpcDeclaredErrors = Readonly<Record<string, Readonly<Record<string, readonly string[]>>>>;

export interface RpcProviderRegistration extends RpcProviderIdentity {
  readonly declaredErrors: RpcDeclaredErrors;
  readonly handler: RpcProviderHandler;
}

export type PackageSettingsFieldKind = "text" | "secret" | "boolean" | "select";

export interface PackageSettingsOption {
  readonly value: string;
  readonly label: string;
}

export interface PackageSettingsField {
  readonly key: string;
  readonly label: string;
  readonly kind: PackageSettingsFieldKind;
  readonly description?: string | null;
  readonly isRequired?: boolean;
  readonly placeholder?: string | null;
  readonly defaultValue?: string | null;
  readonly options?: readonly PackageSettingsOption[];
}

export interface PackageSettingsSection {
  readonly sectionId: string;
  readonly title: string;
  readonly description?: string | null;
  readonly fields: readonly PackageSettingsField[];
}

export interface PackageSettingsSchema {
  readonly summary?: string | null;
  readonly sections: readonly PackageSettingsSection[];
}
import { copyResourceIntegerTokens } from "./resource-integers";
