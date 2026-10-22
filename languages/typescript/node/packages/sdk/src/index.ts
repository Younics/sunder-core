// Every root export is intentional. Generated implementation support has its own entry point.
export { SUNDER_WORKER_PROTOCOL, SUNDER_WORKER_PROTOCOL_VERSION, SUNDER_WORKER_LIMITS, RpcError, RpcDomainError } from "./types";
export type {
  JsonObject, JsonPrimitive, JsonValue, RpcCallOptions, RpcInvoker, WorkerLimits, WorkerResourceProfile,
  RpcMethodKind, RpcMethodIdempotency, RpcSchemaReference, RpcErrorDescriptorV1, RpcMethodDescriptorV1Base,
  RpcMethodDescriptorV1, RpcServiceDescriptorV1, RpcContractDescriptorV1, RpcMethodDescriptor,
  RpcServiceDescriptor, RpcContractDescriptor, JsonSchema, RpcErrorKind, RpcErrorValue, RpcProviderState,
  RpcProviderSnapshot, RpcCatalogSnapshot, RpcCatalogEventKind, RpcCatalogEvent, RpcContentRepeatability,
  RpcContentReference, RpcContentRegistrationOptions, RpcContentSource, RpcContentFile, RpcContentClient,
  RpcCallerContentClient, RpcCallScope, RpcClient, RpcProviderIdentity, RpcContractIdentity,
  RpcInvocationContext, RpcProviderHandler, RpcDeclaredErrors, RpcProviderRegistration,
  PackageSettingsFieldKind, PackageSettingsOption, PackageSettingsField, PackageSettingsSection, PackageSettingsSchema,
} from "./types";
export type {
  WorkerCallOptions, WorkerSettings, WorkerStateReader, WorkerState, WorkerFiles, WorkerSecrets,
  PackageLogLevel, PackageLogAttribute, WorkerLogDetails, WorkerLogger, WorkerEventLogEntry,
  WorkerEventLogger, WorkerLogging, WorkerCallbackCompletionState, WorkerCallbackCancellationReason,
  WorkerCallbackStartContext, WorkerCallbackStartResult, WorkerCallbackCompletionContext,
  WorkerCallbackCompletionResult, WorkerCallbackCancellationContext, WorkerCallbackHandler,
  WorkerAuthStatusKind, WorkerAuthStatus, WorkerAuthSessionStartContext, WorkerAuthSessionStartResult,
  WorkerAuthSessionCompletionContext, WorkerAuthHandler, WorkerLocalPath, WorkerStorage, WorkerContext,
  WorkerOptions, WorkerConfigure, WorkerShutdownContext, WorkerGenerationContext, WorkerMigrationPreparationContext,
} from "./worker-types";
export { SUNDER_WORKER_STANDARD_RESOURCE_PROFILE, computeWorkerResourceProfileHash } from "./resource-profile";
export {
  FrameDecoder,
  ProtocolError,
  TransportResourceError,
  encodeFrame,
  encodedJsonByteLength,
} from "./framing";
export type { FrameLimits } from "./framing";
export { runWorker, safeStderr } from "./worker";
export { defineService, defineServiceError } from "./services";
export type { ServiceContext, ServiceMethods, ServiceUse, ServiceRuntimeUse, ServiceConstraints, ServiceOptions, ServiceError, ServiceContract, ServiceDefinition } from "./services";
export { createApiContext } from "./api";
export type { ApiToken, ApiContext, ApiTransport, ApiProvider, ServiceStream } from "./api";
export {SunderContent} from "./content";
export type {SunderContentOptions,SunderContentStreamOptions} from "./content";
export {contentFromFile} from "./content-node";
export { ServiceReference, ServiceProvider } from "./service-reference";
export type { ServiceApi, ServiceToken, OptionalServiceReference, ServiceCatalogSnapshot, ServiceCatalog } from "./service-reference";
export { ServiceDomainError } from "./resources";
export { PackageStorageKeyMigrationAction, PackageStorageKeyMigration, createPackageStorageKey } from "./storage";
export { formatUtcTimestamp, parseUtcTimestamp } from "./timestamp";
export { PACKAGE_ID_MAXIMUM_LENGTH, SEMANTIC_VERSION_MAXIMUM_LENGTH, PACKAGE_VERSION_RANGE_MAXIMUM_LENGTH,
  isPackageId, isSemanticVersion, isPackageVersionRange, isVersionInRange, isArchiveRelativePath } from "./validation";
export {
  RpcDescriptorError,
  SUNDER_RPC_VALUE_SCHEMA_PROFILE,
  parseRpcContractDescriptor,
  validateRpcContractDescriptor,
  canonicalizeDescriptor,
} from "./descriptor";
export type { RpcDescriptorErrorCode } from "./descriptor";
export {
  RpcInstanceError,
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  rpcAbiV1Projection,
  validateRpcInstance,
} from "./descriptor-v1";
