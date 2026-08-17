import type { Readable } from "node:stream";
import type { JsonValue, RpcCallOptions, RpcInvoker } from "./core";
import type { PackageStorageKeyMigration } from "./storage";

export type { JsonObject, JsonPrimitive, JsonValue, RpcCallOptions, RpcInvoker } from "./core";

export const SUNDER_WORKER_PROTOCOL = "sunder.worker.v3" as const;
export const SUNDER_WORKER_PROTOCOL_VERSION = 3 as const;

export interface WorkerLimits {
  maxFrameBytes: number;
  maxHeaderBytes: number;
  maxMessageDepth: number;
  maxRpcValueBytes: number;
  maxValueDepth: number;
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
  maxFrameBytes: 32 * 1024 * 1024 + 64 * 1024,
  maxHeaderBytes: 8 * 1024,
  maxMessageDepth: 72,
  maxRpcValueBytes: 32 * 1024 * 1024,
  maxValueDepth: 64,
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
  maxStreamQueueBytes: 64 * 1024 * 1024,
  maxWriteQueueBytes: 256 * 1024 * 1024,
  maxRememberedIds: 2048,
  maxDiagnosticCharacters: 4096,
  maxContentBytes: 256 * 1024 * 1024,
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

export interface RpcMethodDescriptorV1 {
  readonly methodId: string;
  readonly kind: RpcMethodKind;
  readonly requestSchema: string | RpcSchemaReference;
  readonly responseSchema?: string | RpcSchemaReference;
  readonly eventSchema?: string | RpcSchemaReference;
}

export interface RpcServiceDescriptorV1 {
  readonly serviceId: string;
  readonly methods: readonly RpcMethodDescriptorV1[] | Readonly<Record<string, RpcMethodDescriptorV1>>;
}

export interface RpcContractDescriptorV1 {
  readonly $schema?: "https://json-schema.org/draft/2020-12/schema";
  readonly descriptorVersion: 1;
  readonly contractId: string;
  readonly version: string;
  readonly services: readonly RpcServiceDescriptorV1[] | Readonly<Record<string, RpcServiceDescriptorV1>>;
  readonly $defs: Readonly<Record<string, JsonSchema>>;
}

export interface RpcErrorDescriptorV2 {
  readonly description?: string;
  readonly detailsSchema?: RpcSchemaReference;
}

export interface RpcMethodDescriptorV2Base {
  readonly idempotency: RpcMethodIdempotency;
  readonly errors: readonly string[];
  readonly requestSchema: RpcSchemaReference;
}

export type RpcMethodDescriptorV2 =
  | (RpcMethodDescriptorV2Base & {
    readonly kind: "unary";
    readonly responseSchema: RpcSchemaReference;
    readonly eventSchema?: never;
  })
  | (RpcMethodDescriptorV2Base & {
    readonly kind: "server-stream";
    readonly eventSchema: RpcSchemaReference;
    readonly responseSchema?: never;
  });

export interface RpcServiceDescriptorV2 {
  readonly methods: Readonly<Record<string, RpcMethodDescriptorV2>>;
}

export interface RpcContractDescriptorV2 {
  readonly $schema?: "https://json-schema.org/draft/2020-12/schema";
  readonly descriptorVersion: 2;
  readonly schemaProfile: "sunder.rpc.values.v1";
  readonly contractId: string;
  readonly version: string;
  readonly errors: Readonly<Record<string, RpcErrorDescriptorV2>>;
  readonly services: Readonly<Record<string, RpcServiceDescriptorV2>>;
  readonly $defs: Readonly<Record<string, JsonSchema>>;
}

// Keep the unversioned nested aliases on their shipped V1 shapes. V2 map values
// intentionally omit repeated IDs and are available through the explicit V2 types.
export type RpcMethodDescriptor = RpcMethodDescriptorV1;
export type RpcServiceDescriptor = RpcServiceDescriptorV1;
export type RpcContractDescriptor = RpcContractDescriptorV1 | RpcContractDescriptorV2;
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
  readonly stream: Readable;
  discard(): Promise<void>;
}

export interface RpcContentClient {
  register(source: RpcContentSource, options: RpcContentRegistrationOptions): Promise<RpcContentReference>;
  registerFile(filePath: string, options: RpcContentRegistrationOptions): Promise<RpcContentReference>;
  open(reference: RpcContentReference): Promise<RpcContentFile>;
}

export interface RpcCallerContentClient {
  register(endpointReference: string, source: RpcContentSource, options: RpcContentRegistrationOptions): Promise<RpcContentReference>;
  registerFile(endpointReference: string, filePath: string, options: RpcContentRegistrationOptions): Promise<RpcContentReference>;
  open(reference: RpcContentReference): Promise<RpcContentFile>;
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
  readonly contractSha256: string;
}

export interface RpcContractIdentityV2 {
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

export interface WorkerCallOptions {
  readonly signal?: AbortSignal;
}

export interface WorkerSettings {
  get(key: string, options?: WorkerCallOptions): Promise<string | null>;
  getStored(key: string, options?: WorkerCallOptions): Promise<string | null>;
  set(key: string, value: string, options?: WorkerCallOptions): Promise<void>;
  delete(key: string, options?: WorkerCallOptions): Promise<void>;
}

export interface WorkerStateReader {
  get(key: string, options?: WorkerCallOptions): Promise<string | null>;
  has(key: string, options?: WorkerCallOptions): Promise<boolean>;
  list(prefix?: string | null, options?: WorkerCallOptions): Promise<readonly string[]>;
}

export interface WorkerState extends WorkerStateReader {
  set(key: string, value: string, options?: WorkerCallOptions): Promise<void>;
  delete(key: string, options?: WorkerCallOptions): Promise<void>;
}

export interface WorkerFiles {
  read(relativePath: string, options?: WorkerCallOptions): Promise<Buffer | null>;
  write(relativePath: string, contents: Uint8Array, options?: WorkerCallOptions): Promise<void>;
  delete(relativePath: string, options?: WorkerCallOptions): Promise<void>;
}

export interface WorkerSecrets {
  get(key: string, options?: WorkerCallOptions): Promise<string | null>;
  set(key: string, value: string, options?: WorkerCallOptions): Promise<void>;
  delete(key: string, options?: WorkerCallOptions): Promise<void>;
}

export type PackageLogLevel = "trace" | "debug" | "information" | "warning" | "error" | "critical";
export type PackageLogAttribute = null | boolean | number | string | Date;

export interface WorkerLogDetails {
  readonly eventId?: number;
  readonly eventName?: string | null;
  readonly attributes?: Readonly<Record<string, unknown>>;
  readonly error?: unknown;
}

export interface WorkerLogger {
  log(level: PackageLogLevel, message: string, details?: WorkerLogDetails): void;
  trace(message: string, details?: WorkerLogDetails): void;
  debug(message: string, details?: WorkerLogDetails): void;
  information(message: string, details?: WorkerLogDetails): void;
  warning(message: string, details?: WorkerLogDetails): void;
  error(message: string, details?: WorkerLogDetails): void;
  critical(message: string, details?: WorkerLogDetails): void;
}

export interface WorkerEventLogEntry {
  readonly level: PackageLogLevel;
  readonly eventName: string;
  readonly message: string;
  readonly attributes?: Readonly<Record<string, unknown>>;
  readonly error?: unknown;
}

export interface WorkerEventLogger {
  write(entry: WorkerEventLogEntry, options?: WorkerCallOptions): Promise<void>;
}

export interface WorkerLogging {
  readonly events: WorkerEventLogger;
  createLogger(category: string): WorkerLogger;
}

export type WorkerCallbackCompletionState = "completed" | "failed" | "cancelled";
export type WorkerCallbackCancellationReason = "expired" | "package-unloaded" | "host-shutdown" | "caller-requested";

export interface WorkerCallbackStartContext {
  readonly callbackSessionId: string;
  readonly callbackUri: string;
  readonly purpose: string | null;
  readonly parameters: Readonly<Record<string, string>>;
}

export interface WorkerCallbackStartResult {
  readonly flow: "browser";
  readonly launchUrl: string;
  readonly message?: string | null;
}

export interface WorkerCallbackCompletionContext {
  readonly callbackSessionId: string;
  readonly queryValues: Readonly<Record<string, string | null>>;
}

export interface WorkerCallbackCompletionResult {
  readonly state: WorkerCallbackCompletionState;
  readonly message?: string | null;
}

export interface WorkerCallbackCancellationContext {
  readonly callbackSessionId: string;
  readonly reason: WorkerCallbackCancellationReason;
}

export interface WorkerCallbackHandler {
  readonly callbackHandlerId: string;
  startCallback(
    context: WorkerCallbackStartContext,
    signal: AbortSignal,
  ): Promise<WorkerCallbackStartResult | null> | WorkerCallbackStartResult | null;
  completeCallback(
    context: WorkerCallbackCompletionContext,
    signal: AbortSignal,
  ): Promise<WorkerCallbackCompletionResult> | WorkerCallbackCompletionResult;
  cancelCallback(context: WorkerCallbackCancellationContext, signal: AbortSignal): Promise<void> | void;
}

export type WorkerAuthStatusKind = "unavailable" | "not-connected" | "connected" | "failed";

export interface WorkerAuthStatus {
  readonly status: WorkerAuthStatusKind;
  readonly message?: string | null;
  readonly canAuthorize: boolean;
  readonly canDisconnect: boolean;
}

export interface WorkerAuthSessionStartContext {
  readonly authSessionId: string;
  readonly callbackUri: string;
}

export interface WorkerAuthSessionStartResult {
  readonly flow: "browser";
  readonly launchUrl: string;
  readonly message?: string | null;
}

export interface WorkerAuthSessionCompletionContext {
  readonly authSessionId: string;
  readonly queryValues: Readonly<Record<string, string | null>>;
}

export interface WorkerAuthHandler {
  getStatus(signal: AbortSignal): Promise<WorkerAuthStatus> | WorkerAuthStatus;
  startAuthorization(
    context: WorkerAuthSessionStartContext,
    signal: AbortSignal,
  ): Promise<WorkerAuthSessionStartResult | null> | WorkerAuthSessionStartResult | null;
  completeAuthorization(
    context: WorkerAuthSessionCompletionContext,
    signal: AbortSignal,
  ): Promise<WorkerAuthStatus> | WorkerAuthStatus;
  disconnect(signal: AbortSignal): Promise<WorkerAuthStatus> | WorkerAuthStatus;
  cancelCallback(context: WorkerCallbackCancellationContext, signal: AbortSignal): Promise<void> | void;
}

export interface WorkerLocalPath {
  readonly rootPath: string;
  resolve(relativePath: string): string;
}

export interface WorkerStorage {
  readonly state: WorkerState;
  readonly files: WorkerFiles;
  readonly workspace: WorkerLocalPath;
}

export interface WorkerContext {
  readonly packageId: string;
  readonly packageVersion: string;
  readonly contentRootPath: string;
  readonly temporaryRootPath: string;
  readonly content: WorkerLocalPath;
  readonly temporary: WorkerLocalPath;
  readonly workspace: WorkerLocalPath;
  readonly settings: WorkerSettings;
  readonly state: WorkerState;
  readonly files: WorkerFiles;
  readonly storage: WorkerStorage;
  readonly secrets: WorkerSecrets;
  readonly logging: WorkerLogging;
  readonly rpc: RpcClient;
  reportGenerationFault(code: string, message: string, options?: WorkerCallOptions): Promise<void>;
}

export interface WorkerOptions {
  readonly providers: readonly RpcProviderRegistration[];
  readonly settingsSchema?: PackageSettingsSchema | null;
  readonly stateMigrations?: readonly PackageStorageKeyMigration[];
  readonly secretMigrations?: readonly PackageStorageKeyMigration[];
  readonly callbackHandlers?: readonly WorkerCallbackHandler[];
  readonly authHandler?: WorkerAuthHandler | null;
  readonly onCandidateStarted?: (context: WorkerContext, signal: AbortSignal) => Promise<void> | void;
  readonly onGenerationCommitted?: (generation: WorkerGenerationContext, context: WorkerContext, signal: AbortSignal) => Promise<void> | void;
  readonly onBeforeStorageMigrations?: (preparation: WorkerMigrationPreparationContext, signal: AbortSignal) => Promise<void> | void;
  readonly onActivated?: (context: WorkerContext, signal: AbortSignal) => Promise<void> | void;
  readonly onShutdown?: (shutdown: WorkerShutdownContext, context: WorkerContext, signal: AbortSignal) => Promise<void> | void;
}

export type WorkerConfigure = (context: WorkerContext) => WorkerOptions;

export interface WorkerShutdownContext {
  readonly reason: string;
}

export interface WorkerGenerationContext {
  readonly activationId: string;
  readonly sessionGeneration: number;
}

export interface WorkerMigrationPreparationContext extends WorkerGenerationContext {
  readonly state: WorkerStateReader;
}

export interface ProviderWireIdentity extends RpcProviderIdentity {}

export interface WorkerSettingsSchemaWire {
  readonly summary: string | null;
  readonly sections: readonly Readonly<{
    sectionId: string;
    title: string;
    description: string | null;
    fields: readonly Readonly<{
      key: string;
      label: string;
      kind: PackageSettingsFieldKind;
      description: string | null;
      isRequired: boolean;
      placeholder: string | null;
      defaultValue: string | null;
      options: readonly Readonly<{ value: string; label: string }>[];
    }>[];
  }>[];
}

export interface WorkerContributions {
  readonly settingsSchema: WorkerSettingsSchemaWire | null;
  readonly storageMigrations: readonly WorkerStorageMigrationPlanWire[];
  readonly callbackHandlers: readonly string[];
  readonly authHandler: boolean;
  readonly rpcProviders: readonly ProviderWireIdentity[];
  readonly candidateLifecycle: boolean;
  readonly generationLifecycle: boolean;
  readonly migrationPreparationLifecycle: boolean;
}

export type WorkerStorageMigrationRuleWire =
  | Readonly<{ kind: "exact"; legacyKey: string; destinationKey: string }>
  | Readonly<{ kind: "opaque-id"; legacyPrefix: string; legacySuffix: string; destinationPrefix: string; destinationVersion: number }>
  | Readonly<{ kind: "opaque-id-json" | "opaque-id-json-case-insensitive"; legacyPrefix: string; legacySuffix: string; destinationPrefix: string; destinationVersion: number; identityProperty: string }>
  | Readonly<{ kind: "dynamic-cleanup" }>;

export interface WorkerStorageMigrationPlanWire {
  readonly store: "state" | "secrets";
  readonly rules: readonly WorkerStorageMigrationRuleWire[];
}

export type HostEnvelope =
  | Readonly<{ type: "host.hello"; protocol: typeof SUNDER_WORKER_PROTOCOL; protocolVersion: 3; challenge: string; packageId: string; packageVersion: string; activationId: string; sessionId: string; providers: readonly ProviderWireIdentity[]; resourceProfile: WorkerResourceProfile; resourceProfileHash: string }>
  | Readonly<{ type: "host.start-candidate" }>
  | Readonly<{ type: "host.commit-generation"; activationId: string; sessionGeneration: number }>
  | Readonly<{ type: "host.prepare-storage-migrations"; activationId: string; sessionGeneration: number }>
  | Readonly<{ type: "host.activate"; sessionGeneration: number }>
  | Readonly<{ type: "host.invoke"; id: string; kind: RpcMethodKind; providerId: string; serviceId: string; methodId: string; request: JsonValue; context: Omit<RpcInvocationContext, "signal" | "content"> }>
  | Readonly<{ type: "host.cancel"; id: string }>
  | Readonly<{ type: "host.result"; id: string; value: JsonValue }>
  | Readonly<{ type: "host.event"; id: string; value: JsonValue }>
  | Readonly<{ type: "host.complete"; id: string }>
  | Readonly<{ type: "host.error"; id: string; error: RpcErrorValue }>
  | Readonly<{ type: "host.callback-start" | "host.callback-complete" | "host.callback-cancel" | "host.auth-status" | "host.auth-connect" | "host.auth-complete" | "host.auth-disconnect"; id: string; activationId: string; sessionGeneration: number; value: JsonValue }>
  | Readonly<{ type: "host.contribution-cancel"; id: string; activationId: string; sessionGeneration: number }>
  | Readonly<{ type: "host.shutdown"; shutdownId: string; reason: string }>;

export type WorkerEnvelope = Readonly<Record<string, unknown>> & { readonly type: string };
