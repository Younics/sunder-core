// Node-only composition stays outside the browser declaration closure. Portable
// service/descriptor types remain in types.ts and are defined exactly once.
import type { ApiToken } from "./api";
import type { PackageStorageKeyMigration } from "./storage";
import type { JsonValue, RpcClient, RpcProviderRegistration, RpcProviderIdentity, RpcInvocationContext, RpcMethodKind, RpcErrorValue, PackageSettingsSchema, PackageSettingsFieldKind, WorkerResourceProfile, SUNDER_WORKER_PROTOCOL } from "./types";
export * from "./types";

export interface WorkerCallOptions { readonly signal?: AbortSignal }
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
export interface WorkerEventLogger { write(entry: WorkerEventLogEntry, options?: WorkerCallOptions): Promise<void> }
export interface WorkerLogging { readonly events: WorkerEventLogger; createLogger(category: string): WorkerLogger }
export type WorkerCallbackCompletionState = "completed" | "failed" | "cancelled";
export type WorkerCallbackCancellationReason = "expired" | "package-unloaded" | "host-shutdown" | "caller-requested";
export interface WorkerCallbackStartContext {
  readonly callbackSessionId: string;
  readonly callbackUri: string;
  readonly purpose: string | null;
  readonly parameters: Readonly<Record<string, string>>;
}
export interface WorkerCallbackStartResult { readonly flow: "browser"; readonly launchUrl: string; readonly message?: string | null }
export interface WorkerCallbackCompletionContext { readonly callbackSessionId: string; readonly queryValues: Readonly<Record<string, string | null>> }
export interface WorkerCallbackCompletionResult { readonly state: WorkerCallbackCompletionState; readonly message?: string | null }
export interface WorkerCallbackCancellationContext { readonly callbackSessionId: string; readonly reason: WorkerCallbackCancellationReason }
export interface WorkerCallbackHandler {
  readonly callbackHandlerId: string;
  startCallback(context: WorkerCallbackStartContext, signal: AbortSignal): Promise<WorkerCallbackStartResult | null> | WorkerCallbackStartResult | null;
  completeCallback(context: WorkerCallbackCompletionContext, signal: AbortSignal): Promise<WorkerCallbackCompletionResult> | WorkerCallbackCompletionResult;
  cancelCallback(context: WorkerCallbackCancellationContext, signal: AbortSignal): Promise<void> | void;
}
export type WorkerAuthStatusKind = "unavailable" | "not-connected" | "connected" | "failed";
export interface WorkerAuthStatus { readonly status: WorkerAuthStatusKind; readonly message?: string | null; readonly canAuthorize: boolean; readonly canDisconnect: boolean }
export interface WorkerAuthSessionStartContext { readonly authSessionId: string; readonly callbackUri: string }
export interface WorkerAuthSessionStartResult { readonly flow: "browser"; readonly launchUrl: string; readonly message?: string | null }
export interface WorkerAuthSessionCompletionContext { readonly authSessionId: string; readonly queryValues: Readonly<Record<string, string | null>> }
export interface WorkerAuthHandler {
  getStatus(signal: AbortSignal): Promise<WorkerAuthStatus> | WorkerAuthStatus;
  startAuthorization(context: WorkerAuthSessionStartContext, signal: AbortSignal): Promise<WorkerAuthSessionStartResult | null> | WorkerAuthSessionStartResult | null;
  completeAuthorization(context: WorkerAuthSessionCompletionContext, signal: AbortSignal): Promise<WorkerAuthStatus> | WorkerAuthStatus;
  disconnect(signal: AbortSignal): Promise<WorkerAuthStatus> | WorkerAuthStatus;
  cancelCallback(context: WorkerCallbackCancellationContext, signal: AbortSignal): Promise<void> | void;
}
export interface WorkerLocalPath { readonly rootPath: string; resolve(relativePath: string): string }
export interface WorkerStorage { readonly state: WorkerState; readonly files: WorkerFiles; readonly workspace: WorkerLocalPath }
export interface WorkerContext {
  api<T>(token: ApiToken<T>): T;
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
export interface WorkerShutdownContext { readonly reason: string }
export interface WorkerGenerationContext { readonly activationId: string; readonly sessionGeneration: number }
export interface WorkerMigrationPreparationContext extends WorkerGenerationContext { readonly state: WorkerStateReader }
export interface ProviderWireIdentity extends RpcProviderIdentity {}
export interface WorkerSettingsSchemaWire {
  readonly summary: string | null;
  readonly sections: readonly Readonly<{
    sectionId: string; title: string; description: string | null;
    fields: readonly Readonly<{
      key: string; label: string; kind: PackageSettingsFieldKind; description: string | null; isRequired: boolean;
      placeholder: string | null; defaultValue: string | null; options: readonly Readonly<{ value: string; label: string }>[];
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
export interface WorkerStorageMigrationPlanWire { readonly store: "state" | "secrets"; readonly rules: readonly WorkerStorageMigrationRuleWire[] }
export type HostEnvelope =
  | Readonly<{ type: "host.hello"; protocol: typeof SUNDER_WORKER_PROTOCOL; protocolVersion: 1; challenge: string; packageId: string; packageVersion: string; activationId: string; sessionId: string; providers: readonly ProviderWireIdentity[]; resourceProfile: WorkerResourceProfile; resourceProfileHash: string }>
  | Readonly<{ type: "host.start-candidate" }>
  | Readonly<{ type: "host.commit-generation"; activationId: string; sessionGeneration: number }>
  | Readonly<{ type: "host.prepare-storage-migrations"; activationId: string; sessionGeneration: number }>
  | Readonly<{ type: "host.activate"; sessionGeneration: number; workspaceRootPath: string }>
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
