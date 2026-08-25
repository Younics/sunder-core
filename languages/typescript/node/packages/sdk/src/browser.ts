import type { JsonPrimitive, JsonValue, RpcCallOptions, RpcInvoker } from "./core";
import { formatUtcTimestamp } from "./timestamp";

export { formatUtcTimestamp } from "./timestamp";
export { RpcDomainError, RpcError } from "./types";
export {
  RpcDescriptorError,
  canonicalizeDescriptor,
  parseRpcContractDescriptor,
  validateRpcContractDescriptor,
} from "./descriptor";
export type { RpcDescriptorErrorCode } from "./descriptor";
export {
  RpcInstanceError,
  SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  rpcAbiV2Projection,
  validateRpcInstance,
} from "./descriptor-v2";
export type {
  JsonSchema,
  RpcContractDescriptor,
  RpcContractDescriptorV1,
  RpcContractDescriptorV2,
  RpcErrorDescriptorV2,
  RpcMethodDescriptorV1,
  RpcMethodDescriptorV2,
  RpcMethodDescriptorV2Base,
  RpcMethodIdempotency,
  RpcSchemaReference,
  RpcServiceDescriptorV1,
  RpcServiceDescriptorV2,
} from "./types";

export type BrowserJsonPrimitive = JsonPrimitive;
export type BrowserJsonValue = JsonValue;

export type BrowserRpcProviderState = "active" | "inactive" | "faulted";
export type BrowserRpcCatalogEventKind = "added" | "removed" | "activated" | "deactivated" | "faulted" | "reset-required";
export type BrowserRpcErrorKind =
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

export interface BrowserRpcError {
  readonly kind: BrowserRpcErrorKind;
  readonly code: string;
  readonly message: string;
  readonly dispatchState: "not-dispatched" | "possibly-dispatched";
  readonly retrySafety: "safe" | "unsafe";
  readonly details?: BrowserJsonValue;
}

export interface BrowserRpcFailure extends Error {
  readonly name: "SunderRpcError";
  readonly error: BrowserRpcError;
}

export interface BrowserRpcProvider {
  readonly packageId: string;
  readonly packageVersion: string;
  readonly providerId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
  readonly providerHandle: string;
  readonly catalogRevision: number;
  readonly state: BrowserRpcProviderState;
  readonly faultCode: string | null;
}

export interface BrowserRpcCatalogSnapshot {
  readonly revision: number;
  readonly sequence: number;
  readonly providers: readonly BrowserRpcProvider[];
  readonly resetRequired: boolean;
}

export interface BrowserRpcCatalogEvent {
  readonly revision: number;
  readonly sequence: number;
  readonly kind: BrowserRpcCatalogEventKind;
  readonly provider: BrowserRpcProvider | null;
}

export type BrowserCallOptions = RpcCallOptions;

export interface SunderBrowserRpc extends RpcInvoker<string> {
  discover(contractId: string, options?: BrowserCallOptions): Promise<BrowserRpcCatalogSnapshot>;
  watch(afterRevision: number, afterSequence: number, options?: BrowserCallOptions): AsyncIterable<BrowserRpcCatalogEvent>;
}

export interface SunderBrowserNavigation {
  getState(options?: BrowserCallOptions): Promise<{ readonly route: string }>;
  navigate(route: string, options?: BrowserCallOptions): Promise<{ readonly accepted: boolean }>;
}

export interface SunderBrowserResourcePolicy {
  readonly rpcValueBytes: number;
  readonly valueDepth: number;
  readonly valueNodes: number;
  readonly objectMembers: number;
  readonly inlineDecodedBytes: number;
  readonly contentReferenceBytes: number;
}

export interface SunderBrowserResourceProfile {
  readonly reportedByRuntime: boolean;
  readonly runtimeInstanceId: string | null;
  readonly effectiveProfileHash: string | null;
  readonly effectivePolicy: SunderBrowserResourcePolicy;
}

export interface SunderBrowserApi {
  readonly version: 2;
  readonly resourceProfile: SunderBrowserResourceProfile;
  readonly rpc: SunderBrowserRpc;
  openExternal(url: string, options?: BrowserCallOptions): Promise<{ readonly opened: boolean }>;
  getNavigationState(options?: BrowserCallOptions): Promise<{ readonly route: string }>;
  navigate(route: string, options?: BrowserCallOptions): Promise<{ readonly accepted: boolean }>;
}

let adaptedSource: SunderBrowserApi | undefined;
let adaptedApi: SunderBrowserApi | undefined;

export function getSunder(): SunderBrowserApi {
  const candidate = (globalThis as typeof globalThis & { readonly sunder?: SunderBrowserApi }).sunder;
  if (candidate?.version !== 2) {
    throw new Error("The Sunder browser bridge is not available in this document.");
  }
  if (candidate === adaptedSource && adaptedApi !== undefined) return adaptedApi;
  const rpc: SunderBrowserRpc = Object.freeze({
    discover: (contractId: string, options?: BrowserCallOptions) => candidate.rpc.discover(contractId, outboundOptions(options)),
    watch: (afterRevision: number, afterSequence: number, options?: BrowserCallOptions) => candidate.rpc.watch(afterRevision, afterSequence, outboundOptions(options)),
    invoke<TRequest extends BrowserJsonValue, TResponse extends BrowserJsonValue>(
      providerHandle: string,
      serviceId: string,
      methodId: string,
      request: TRequest,
      options?: BrowserCallOptions,
    ): Promise<TResponse> {
      return candidate.rpc.invoke<TRequest, TResponse>(providerHandle, serviceId, methodId, request, outboundOptions(options));
    },
    subscribe<TRequest extends BrowserJsonValue, TEvent extends BrowserJsonValue>(
      providerHandle: string,
      serviceId: string,
      methodId: string,
      request: TRequest,
      options?: BrowserCallOptions,
    ): AsyncIterable<TEvent> {
      return candidate.rpc.subscribe<TRequest, TEvent>(providerHandle, serviceId, methodId, request, outboundOptions(options));
    },
  });
  adaptedSource = candidate;
  adaptedApi = Object.freeze({
    version: 2,
    resourceProfile: candidate.resourceProfile,
    rpc,
    openExternal: (url: string, options?: BrowserCallOptions) => candidate.openExternal(url, outboundOptions(options)),
    getNavigationState: (options?: BrowserCallOptions) => candidate.getNavigationState(outboundOptions(options)),
    navigate: (route: string, options?: BrowserCallOptions) => candidate.navigate(route, outboundOptions(options)),
  });
  return adaptedApi;
}

export function isSunderRpcFailure(value: unknown): value is BrowserRpcFailure {
  if (!(value instanceof Error) || value.name !== "SunderRpcError") return false;
  const error = (value as { readonly error?: unknown }).error;
  const kind = (error as { readonly kind?: unknown })?.kind;
  const hasDetails = error !== null
    && typeof error === "object"
    && Object.prototype.hasOwnProperty.call(error, "details");
  return error !== null
    && typeof error === "object"
    && typeof kind === "string"
    && typeof (error as { readonly code?: unknown }).code === "string"
    && typeof (error as { readonly message?: unknown }).message === "string"
    && ((error as { readonly dispatchState?: unknown }).dispatchState === "not-dispatched"
      || (error as { readonly dispatchState?: unknown }).dispatchState === "possibly-dispatched")
    && ((error as { readonly retrySafety?: unknown }).retrySafety === "safe"
      || (error as { readonly retrySafety?: unknown }).retrySafety === "unsafe")
    && (!hasDetails || kind === "domain" && isBrowserJsonValue((error as { readonly details?: unknown }).details));
}

function isBrowserJsonValue(value: unknown, active = new WeakSet<object>()): value is BrowserJsonValue {
  if (value === null || typeof value === "string" || typeof value === "boolean") return true;
  if (typeof value === "number") return Number.isFinite(value);
  if (typeof value !== "object" || active.has(value)) return false;
  active.add(value);
  try {
    return Array.isArray(value)
      ? value.every((item) => isBrowserJsonValue(item, active))
      : Object.values(value).every((item) => isBrowserJsonValue(item, active));
  } finally {
    active.delete(value);
  }
}

function outboundOptions(options?: BrowserCallOptions): BrowserCallOptions | undefined {
  if (options?.deadline === undefined) return options;
  const formatted = formatUtcTimestamp(options.deadline);
  const deadline = new Date(options.deadline.getTime());
  Object.defineProperty(deadline, "toISOString", { value: () => formatted });
  return Object.freeze({ ...options, deadline });
}
