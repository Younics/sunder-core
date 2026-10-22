import type { JsonPrimitive, JsonValue, RpcCallOptions, RpcInvoker } from "./core";
import { formatUtcTimestamp } from "./timestamp";
import { apiTokenOwner, bindApiContext, createApiContext, type ApiContext, type ApiToken } from "./api";
export { createApiContext } from "./api";
export type { ApiToken, ApiContext, ApiTransport, ServiceStream } from "./api";
export type { ServiceContext, ServiceContract } from "./services";
export {SunderContent} from "./content";
export type {SunderContentOptions,SunderContentStreamOptions} from "./content";
export { ServiceReference, ServiceProvider } from "./service-reference";
export type { ServiceApi, ServiceToken, OptionalServiceReference, ServiceCatalogSnapshot, ServiceCatalog } from "./service-reference";
export { ServiceDomainError } from "./resources";
export type { RpcCallOptions, JsonValue, RpcInvoker } from "./core";

export { formatUtcTimestamp } from "./timestamp";
export { RpcDomainError, RpcError } from "./types";
export { isVersionInRange } from "./validation";
export {
  RpcDescriptorError,
  canonicalizeDescriptor,
  parseRpcContractDescriptor,
  validateRpcContractDescriptor,
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
export type {
  RpcContentReference, RpcContractIdentity, RpcInvocationContext, RpcProviderHandler, RpcProviderRegistration, RpcClient, RpcProviderSnapshot, RpcCallScope,
  JsonSchema,
  RpcContractDescriptor,
  RpcContractDescriptorV1,
  RpcErrorDescriptorV1,
  RpcMethodDescriptorV1,
  RpcMethodDescriptorV1Base,
  RpcMethodIdempotency,
  RpcSchemaReference,
  RpcServiceDescriptorV1,
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
  /** Full existing activation locator, supplied by resource-capable bridge adapters. */
  readonly activationId?:string;readonly activationEpoch?:number;readonly sessionGeneration?:number;readonly endpointReference?:string;
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
  getProvider?(providerHandle:string,options?:BrowserCallOptions):Promise<BrowserRpcProvider|null>;
  reportInvariantViolation?(providerHandle:string,error:unknown,options?:BrowserCallOptions):Promise<boolean>;
  discover(contractId: string, options?: BrowserCallOptions): Promise<BrowserRpcCatalogSnapshot>;
  watch(afterRevision: number, afterSequence: number, options?: BrowserCallOptions): AsyncIterable<BrowserRpcCatalogEvent>;
  createCallScope?(options?:BrowserCallOptions):Promise<import("./api").ApiTransport & {close():Promise<void>}>;
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
  readonly version: 1;
  readonly protocol: "sunder.bridge.v1";
  readonly signal: AbortSignal;
  readonly resourceProfile: SunderBrowserResourceProfile;
  readonly rpc: SunderBrowserRpc;
  openExternal(url: string, options?: BrowserCallOptions): Promise<{ readonly opened: boolean }>;
  getNavigationState(options?: BrowserCallOptions): Promise<{ readonly route: string }>;
  navigate(route: string, options?: BrowserCallOptions): Promise<{ readonly accepted: boolean }>;
}

export function getSunder(): SunderBrowserApi {
  const candidate = (globalThis as typeof globalThis & { readonly sunder?: SunderBrowserApi }).sunder;
  if (candidate?.version !== 1 || candidate.protocol !== "sunder.bridge.v1" || !candidate.signal || candidate.signal.aborted) {
    throw new Error("The Sunder browser bridge is not available in this document.");
  }
  return adaptBrowserApi(candidate);
}
function adaptBrowserApi(candidate: SunderBrowserApi): SunderBrowserApi {
  const rpc: SunderBrowserRpc = Object.freeze({
    ...(candidate.rpc.getProvider?{getProvider:(target:string,options?:BrowserCallOptions)=>candidate.rpc.getProvider!(target,outboundOptions(options))}:{}),
    ...(candidate.rpc.reportInvariantViolation?{reportInvariantViolation:(target:string,error:unknown,options?:BrowserCallOptions)=>candidate.rpc.reportInvariantViolation!(target,error,outboundOptions(options))}:{}),
    ...(candidate.rpc.createCallScope?{createCallScope:async(options?:BrowserCallOptions)=>adaptRpcScope(await candidate.rpc.createCallScope!(outboundOptions(options)))}:{}),
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
  return Object.freeze({
    version: 1,
    protocol: candidate.protocol,
    signal: candidate.signal,
    resourceProfile: candidate.resourceProfile,
    rpc,
    openExternal: (url: string, options?: BrowserCallOptions) => candidate.openExternal(url, outboundOptions(options)),
    getNavigationState: (options?: BrowserCallOptions) => candidate.getNavigationState(outboundOptions(options)),
    navigate: (route: string, options?: BrowserCallOptions) => candidate.navigate(route, outboundOptions(options)),
  });
}
function adaptRpcScope(scope:import("./api").ApiTransport&{close():Promise<void>}):import("./api").ApiTransport&{close():Promise<void>}{
  return Object.freeze({
    ...(scope.signal?{signal:scope.signal}:{}),...(scope.deadline?{deadline:scope.deadline}:{}),
    ...(scope.content?{content:{register:(target:string,source:AsyncIterable<Uint8Array>,options:import("./types").RpcContentRegistrationOptions,call?:RpcCallOptions)=>scope.content!.register(target,source,options,outboundOptions(call)),
      open:(reference:import("./types").RpcContentReference,options?:RpcCallOptions)=>scope.content!.open(reference,outboundOptions(options))}}:{}),
    discover:(id:string,options?:RpcCallOptions)=>scope.discover(id,outboundOptions(options)),
    invoke:<TRequest extends JsonValue,TResponse extends JsonValue>(target:string,serviceId:string,methodId:string,request:TRequest,options?:RpcCallOptions)=>scope.invoke<TRequest,TResponse>(target,serviceId,methodId,request,outboundOptions(options)),
    subscribe:<TRequest extends JsonValue,TEvent extends JsonValue>(target:string,serviceId:string,methodId:string,request:TRequest,options?:RpcCallOptions)=>scope.subscribe<TRequest,TEvent>(target,serviceId,methodId,request,outboundOptions(options)),
    close:()=>scope.close(),
  });
}

/** Structural document host avoids requiring a Window global in Node consumers. */
export interface SunderBrowserDocument {
  addEventListener(type: string, listener: (event?: unknown) => void, options?: { readonly once?: boolean }): void;
  removeEventListener(type: string, listener: (event?: unknown) => void): void;
}
function currentDocument(): SunderBrowserDocument {
  const host = (globalThis as typeof globalThis & { readonly window?: SunderBrowserDocument }).window;
  if (!host) throw new Error("A Sunder browser context requires a document owner.");
  return host;
}
/** Document/root-owned service access. Uses the existing immutable bridge ready event. */
export function createBrowserContext(token: ApiToken<unknown>, host: SunderBrowserDocument = currentDocument()): ApiContext {
  const packageId = apiTokenOwner(token);
  const lifetime = new AbortController(); let bridge: SunderBrowserApi | undefined;
  const retired = (): void => { host.removeEventListener("pagehide", retired); bridge?.signal.removeEventListener("abort", retired); lifetime.abort(new Error("The Sunder document has retired.")); };
  // Readiness is an event subscription, not discovery. Link retirement even if
  // this root never calls a service or iterates a stream.
  void waitForSunder(host, lifetime.signal).then(value => {
    if (lifetime.signal.aborted) return;
    bridge = value;
    value.signal.addEventListener("abort", retired, { once: true });
    if (value.signal.aborted) retired();
  }, retired);
  const context = createApiContext(packageId, async signal => {
    if (!bridge) bridge = await waitForSunder(host, signal);
    bridge.signal.throwIfAborted();
    bridge.signal.addEventListener("abort", retired, { once: true });
    return adaptBrowserApi(bridge).rpc;
  }, lifetime.signal);
  bindApiContext(context, token);
  host.addEventListener("pagehide", retired, { once: true });
  return Object.freeze({ api: context.api, signal: context.signal, fork: context.fork,
    async disposeAsync(){host.removeEventListener("pagehide",retired);bridge?.signal.removeEventListener("abort",retired);retired();await context.disposeAsync();},
    dispose() { host.removeEventListener("pagehide", retired); bridge?.signal.removeEventListener("abort", retired); retired(); context.dispose(); } });
}
export function waitForSunder(host: SunderBrowserDocument = currentDocument(), signal?: AbortSignal): Promise<SunderBrowserApi> {
  return new Promise((resolve, reject) => {
    const finish = (value?: SunderBrowserApi, error?: unknown): void => {
      host.removeEventListener("sunder:ready", ready); host.removeEventListener("pagehide", aborted); signal?.removeEventListener("abort", aborted);
      if (value) resolve(value); else reject(error ?? new Error("The Sunder document retired before bridge readiness."));
    };
    const aborted = (): void => finish(undefined, signal?.reason);
    const inspect = (candidate?: unknown, fromEvent = false): void => {
      try {
      const descriptor = Object.getOwnPropertyDescriptor(host, "sunder");
      if (!descriptor) return;
      const value = descriptor.value as SunderBrowserApi | undefined;
      if (fromEvent && candidate !== value) return;
      if (descriptor.writable || descriptor.configurable || !value || value.version !== 1 || value.protocol !== "sunder.bridge.v1" || !Object.isFrozen(value)
        || !value.signal || typeof value.signal.aborted !== "boolean" || typeof value.signal.addEventListener !== "function"
        || typeof value.signal.removeEventListener !== "function" || typeof value.signal.throwIfAborted !== "function"
        || typeof value.rpc?.discover !== "function" || typeof value.rpc.invoke !== "function" || typeof value.rpc.subscribe !== "function")
        return finish(undefined, new Error("The document has an incompatible or mutable Sunder bridge."));
      if (value.signal.aborted) return finish(undefined, new Error("The Sunder bridge has retired."));
      finish(value);
      } catch (error) { finish(undefined, error); }
    };
    const ready = (event?: unknown): void => inspect((event as {detail?: unknown} | undefined)?.detail, true);
    if (signal?.aborted) { aborted(); return; }
    host.addEventListener("sunder:ready", ready); host.addEventListener("pagehide", aborted, { once: true }); signal?.addEventListener("abort", aborted, { once: true });
    inspect();
  });
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
