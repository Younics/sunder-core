import type { JsonValue, RpcCallOptions, RpcInvoker } from "./core";
import { isVersionInRange } from "./validation";
import { RpcMarshallingContext, type ResourceContentAccess } from "./resources";
import { locatorFromProvider, sameServiceLocator, serviceTargetKey, serviceTokenDefinition, type ServiceReferenceLocator, type ServiceToken } from "./service-reference";
import { ServiceBindingLifetime,staleServiceBinding,type ServiceBinding } from "./service-bindings";
import {RpcError} from "./types";

export interface ApiProvider {
  readonly packageId: string; readonly packageVersion: string; readonly state: string;
  readonly contractId: string; readonly contractVersion: string; readonly descriptorSha256: string; readonly abiSha256: string;
  readonly endpointReference?: string; readonly providerHandle?: string;
  readonly providerId?: string;readonly activationId?: string;readonly activationEpoch?: number;readonly sessionGeneration?: number;
  readonly catalogRevision?: number;
}
export interface ApiTransport extends RpcInvoker<string> {
  discover(contractId: string, options?: RpcCallOptions): Promise<{ readonly resetRequired: boolean; readonly providers: readonly ApiProvider[];readonly revision?:number;readonly sequence?:number }>;
  createCallScope?(options?: RpcCallOptions): Promise<ApiTransport & { close(): Promise<void> }>;
  watch?(afterRevision:number,afterSequence:number,options?:RpcCallOptions):AsyncIterable<{readonly revision:number;readonly sequence:number;readonly kind:string;readonly provider:ApiProvider|null}>;
  readonly content?:ResourceContentAccess;readonly signal?:AbortSignal;readonly deadline?:Date;
  getProvider?(target:string,options?:RpcCallOptions):Promise<ApiProvider|null>;
  reportInvariantViolation?(target:string,error:unknown,options?:RpcCallOptions):Promise<boolean>;
}
export interface ApiIdentity {
  readonly packageId: string; readonly contractId: string; readonly contractVersion: string;
  readonly descriptorSha256: string; readonly abiSha256: string;
  readonly packageRange?: string; readonly includePrerelease?: boolean;
  /** Content authority outlives individual values; an existing caller-owned scope is required. */
  readonly callerOwnedContent?: boolean;
  readonly resourceContent?:boolean;
  /** @internal Exact-bound clients never follow a replacement activation. */
  readonly provider?:ServiceReferenceLocator;
  readonly retirementSignal?:AbortSignal;
  readonly boundBinding?:ServiceBinding;
  readonly timeoutSeconds?:number;
}
declare const apiType: unique symbol;
export interface ApiToken<T> { readonly [apiType]: T }
export interface ApiContext {
  api<T>(token: ApiToken<T>): T;
  readonly signal: AbortSignal;
  dispose(): void;
  disposeAsync(): Promise<void>;
  fork(): ApiContext;
}
/** A lazy service stream with an independently supplied enumeration signal. */
export interface ServiceStream<T> extends AsyncIterable<T> {
  [Symbol.asyncIterator](enumerationSignal?: AbortSignal): AsyncIterator<T>;
  withCancellation(enumerationSignal: AbortSignal): ServiceStream<T>;
}
/** Generated factories use this interface; ordinary callers only use api(token). */
export interface ApiBindingContext extends ApiContext {
  unary<T>(identity: ApiIdentity, call: (transport: ApiTransport, target: string, options: RpcCallOptions, resources:RpcMarshallingContext) => Promise<T>, options?: RpcCallOptions): Promise<T>;
  stream<T>(identity: ApiIdentity, call: (transport: ApiTransport, target: string, options: RpcCallOptions, resources:RpcMarshallingContext) => AsyncIterable<T>, options?: RpcCallOptions, enumerationSignal?: AbortSignal): ServiceStream<T>;
  transport(options?:RpcCallOptions):Promise<ApiTransport>;
  capture():ApiBindingContext;
  bindProvider(locator:ServiceReferenceLocator,retirementSignal?:AbortSignal,binding?:ServiceBinding):ApiBindingContext;
  serviceBindings():ServiceBindingLifetime;
  serviceToken(contractId:string,serviceId:string):ServiceToken<unknown>;
}
const tokens = new WeakMap<object, { readonly owner: string; readonly generation: string; readonly create: (context: ApiBindingContext) => unknown;readonly services:readonly ServiceToken<unknown>[] }>();
const bindings = new WeakMap<ApiContext, ApiBindingContext>();
const apiBindings=new WeakMap<ApiContext["api"],{context:ApiBindingContext;services:Map<string,ServiceToken<unknown>>}>();
const owners = new WeakMap<ApiContext["api"], { owner: string; generation?: string }>();
export function createApiToken<T>(owner: string, create: (context: ApiBindingContext) => T, generation = "",services:readonly ServiceToken<unknown>[]=[]): ApiToken<T> {
  const token = Object.freeze({}); tokens.set(token, { owner, create, generation,services:Object.freeze([...services]) }); return token as ApiToken<T>;
}
/** Generated bootstrap pins the current binding set before any authored configuration. */
export function bindApiContext(context: Pick<ApiContext, "api">, token: ApiToken<unknown>): void {
  const owner = owners.get(context.api); const definition = tokens.get(token);
  if (!owner || !definition || owner.owner !== definition.owner || owner.generation !== undefined && owner.generation !== definition.generation)
    throw new TypeError("API token belongs to a stale or different package generation.");
  owner.generation = definition.generation;
  const binding=apiBindings.get(context.api)!;
  for(const token of definition.services){const key=serviceTargetKey(serviceTokenDefinition(token).target);const previous=binding.services.get(key);
    if(previous&&previous!==token)throw new TypeError("Conflicting generated service bindings in one API lifetime.");binding.services.set(key,token);}
}
/** @internal Generated provider adapters use the Worker's existing DI/API owner. */
export function getApiBindingContext(context:Pick<ApiContext,"api">):ApiBindingContext{
  const binding=apiBindings.get(context.api);if(!binding)throw new TypeError("Unknown receiver API lifetime.");return binding.context;
}
export function apiTokenOwner(token: ApiToken<unknown>): string {
  const value = tokens.get(token); if (!value) throw new TypeError("Unknown generated API token; regenerate the package API."); return value.owner;
}
/** A stable UI facade delegates each operation to the current effect-owned context. */
export function createDeferredApi<T>(token: ApiToken<T>, current: () => ApiContext): T {
  const definition = tokens.get(token); if (!definition) throw new TypeError("Unknown generated API token.");
  const active = (): ApiBindingContext => {
    const context = current(); context.api(token); const binding = bindings.get(context);
    if (!binding) throw new TypeError("Unknown service owner."); return binding;
  };
  return definition.create({
    api: <U>(value: ApiToken<U>) => active().api(value), get signal() { return active().signal; },
    dispose: () => { throw new Error("The UI adapter owns this context."); }, fork: () => active().fork(),
    disposeAsync:async()=>{throw new Error("The UI adapter owns this context.");},
    unary: async (identity, call, options) => active().unary(identity, call, options),
    stream: (identity, call, options, enumerationSignal) => active().stream(identity, call, options, enumerationSignal),
    transport:options=>active().transport(options),capture:()=>active(),bindProvider:(locator,signal,binding)=>active().bindProvider(locator,signal,binding),serviceBindings:()=>active().serviceBindings(),serviceToken:(contractId,serviceId)=>active().serviceToken(contractId,serviceId),
  }) as T;
}

/** One owner per Worker activation, document or UI root. Acquisition performs no I/O. */
export function createApiContext(packageId: string, transport: ApiTransport | ((signal: AbortSignal) => Promise<ApiTransport>), parentSignal?: AbortSignal): ApiContext {
  const lifetime = new AbortController(); const instances = new WeakMap<object, unknown>();
  const services=new Map<string,ServiceToken<unknown>>();const resources=new Set<RpcMarshallingContext>();const opening=new Set<Promise<unknown>>();
  let serviceBindings:ServiceBindingLifetime|undefined;
  const ownership: { owner: string; generation?: string } = { owner: packageId };
  const retire = (): void => lifetime.abort(parentSignal?.reason);
  if (parentSignal?.aborted) retire(); else parentSignal?.addEventListener("abort", retire, { once: true });
  const openCore = async (identity: ApiIdentity, options?: RpcCallOptions, enumerationSignal?: AbortSignal) => {
    options = snapshotOptions(options);
    if(identity.timeoutSeconds!==undefined){if(!Number.isInteger(identity.timeoutSeconds)||identity.timeoutSeconds<1||identity.timeoutSeconds>2100)throw new TypeError("Invalid generated service timeout.");
      options={...options,deadline:new Date(Math.min(options?.deadline?.getTime()??Infinity,Date.now()+identity.timeoutSeconds*1000))};}
    assertCancellationSignal(enumerationSignal);
    const parents = [options?.signal, enumerationSignal, identity.retirementSignal, lifetime.signal];
    const controller = new AbortController();
    const abort = (): void => controller.abort(parents.find(signal => signal?.aborted)?.reason);
    for (const source of parents) if (source?.aborted) abort(); else source?.addEventListener("abort", abort, { once: true });
    let timer: ReturnType<typeof setTimeout> | undefined;
    let scope: (ApiTransport & { close(): Promise<void> }) | undefined;
    const linked: {signal:AbortSignal;deadline?:Date} = { ...options, signal: controller.signal };
    const cleanup = async (): Promise<void> => {
      if (timer !== undefined) clearTimeout(timer);
      // Keep both original links until the final resource lease, not iterator exit.
      for (const source of parents) source?.removeEventListener("abort", abort);
      await scope?.close();
    };
    try {
      controller.signal.throwIfAborted();
      if(options?.deadline&&options.deadline.getTime()<=Date.now())throw new Error("Service deadline has expired.");
      const base = await context.transport(linked);
      controller.signal.throwIfAborted();
      if(base.deadline)linked.deadline=new Date(Math.min(linked.deadline?.getTime()??Infinity,base.deadline.getTime()));
      if (identity.callerOwnedContent && (!("content" in base) || !("close" in base) || typeof base.close !== "function"))
        throw new Error("This service carries content references. Use an explicit caller-owned content scope with createApiContext or Catalogs; its owner closes the scope after consuming the content.");
      if (base.createCallScope) scope = await base.createCallScope(linked);
      controller.signal.throwIfAborted();
      const selected = scope ?? base;
      const deadline=Math.min(linked.deadline?.getTime()??Infinity,selected.deadline?.getTime()??Infinity);
      if(Number.isFinite(deadline))linked.deadline=new Date(deadline);
      const schedule=():void=>{const remaining=deadline-Date.now();if(remaining<=0){controller.abort(new Error("Service deadline has expired."));return;}
        if(Number.isFinite(remaining))timer=setTimeout(schedule,Math.min(remaining,2_147_483_647));};
      if(Number.isNaN(deadline))throw new TypeError("Host scope deadline is invalid.");schedule();controller.signal.throwIfAborted();
      if(identity.resourceContent&&(!selected.content||!selected.signal||!selected.deadline||!("close" in selected)||typeof selected.close!=="function"))
        throw new Error("Natural content requires the transport's existing content scope, retirement signal and Host-clamped deadline.");
      const snapshot = identity.boundBinding?{resetRequired:false,providers:[identity.boundBinding.snapshot]}:await context.serviceBindings().discover(identity.contractId, linked);
      controller.signal.throwIfAborted();
      if (snapshot.resetRequired) throw new RpcError({ kind: "unavailable", code: "rpc.catalog.reset", message: "Service catalog requires a fresh snapshot." });
      const matches = snapshot.providers.filter(provider => provider.state === "active" && provider.packageId === (identity.provider?.packageId??identity.packageId)
        && provider.contractId === identity.contractId && provider.contractVersion === identity.contractVersion
        && provider.descriptorSha256 === identity.descriptorSha256 && provider.abiSha256 === identity.abiSha256
        && (identity.provider ? sameServiceLocator(identity.provider,locatorFromProvider(provider)) : identity.packageRange === undefined || isVersionInRange(provider.packageVersion, identity.packageRange)
          && (identity.includePrerelease || !provider.packageVersion.split("+", 1)[0]!.includes("-"))));
      if (matches.length !== 1) throw new RpcError({ kind: "not-found",
        code: matches.length ? "rpc.provider-ambiguous" : "rpc.provider-not-found",
        message: matches.length ? "Service provider selection is ambiguous." : "No active provider matches the exact selected service." });
      const target = matches[0]!.providerHandle ?? matches[0]!.endpointReference;
      if (!target) throw new Error("Service provider has no authenticated endpoint.");
      const binding=identity.boundBinding??context.serviceBindings().track(matches[0]!);await binding.ensureCurrent(controller.signal);
      binding.assertCurrent();selected.signal?.throwIfAborted();controller.signal.throwIfAborted();
      const marshalling=RpcMarshallingContext.forCall(context,selected.content,target,controller.signal,cleanup,()=>resources.delete(marshalling),selected.signal);
      marshalling.addParticipant(binding);
      let dispatched=false;
      const preDispatch=async()=>{
        marshalling.signal.throwIfAborted();await binding.ensureCurrent(marshalling.signal);
        if(selected.getProvider){const current=await selected.getProvider(target,{...linked,signal:marshalling.signal});
          marshalling.signal.throwIfAborted();if(!current||!sameServiceLocator(binding.locator,locatorFromProvider(current))||current.state!=="active")throw staleServiceBinding();}
        selected.signal?.throwIfAborted();marshalling.signal.throwIfAborted();dispatched=true;
      };
      const guarded:ApiTransport={...selected,discover:(...args)=>selected.discover(...args),
        async invoke<TRequest extends JsonValue,TResponse extends JsonValue>(endpoint:string,service:string,method:string,request:TRequest,call?:RpcCallOptions):Promise<TResponse>{await preDispatch();return selected.invoke<TRequest,TResponse>(endpoint,service,method,request,call);},
        subscribe<TRequest extends JsonValue,TEvent extends JsonValue>(endpoint:string,service:string,method:string,request:TRequest,call?:RpcCallOptions):AsyncIterable<TEvent>{return {async *[Symbol.asyncIterator](){await preDispatch();yield*selected.subscribe<TRequest,TEvent>(endpoint,service,method,request,call);}};}};
      const failure=(error:unknown):unknown=>{
        // Real caller/owner cancellation wins. Dependency cancellation is distinct,
        // and may not manufacture safe retry metadata after dispatch.
        for(const signal of [options?.signal,enumerationSignal,lifetime.signal])if(signal?.aborted)return signal.reason;
        if(marshalling.signal.aborted&&marshalling.signal.reason instanceof RpcError){const reason=marshalling.signal.reason as RpcError;
          if(error instanceof RpcError&&(error.error.retrySafety==="unsafe"||error.error.dispatchState==="possibly-dispatched")&&error.error.kind!=="cancelled")return error;
          return dispatched?new RpcError({...reason.error,dispatchState:"possibly-dispatched",retrySafety:"unsafe"}):reason;}
        return error;
      };
      resources.add(marshalling);return { transport: guarded, target, options: {...linked,signal:marshalling.signal}, resources:marshalling, cleanup:()=>marshalling.dispose(),failure };
    } catch (error) { await cleanup().catch(() => {}); throw error; }
  };
  const open=(identity:ApiIdentity,options?:RpcCallOptions,enumerationSignal?:AbortSignal)=>{const promise=openCore(identity,options,enumerationSignal);opening.add(promise);void promise.finally(()=>opening.delete(promise)).catch(()=>{});return promise;};
  const context: ApiBindingContext = Object.freeze({
    signal: lifetime.signal,
    api<T>(token: ApiToken<T>): T {
      lifetime.signal.throwIfAborted(); const definition = tokens.get(token);
      if (!definition || definition.owner !== packageId) throw new TypeError("API token belongs to a different package context; regenerate its bindings.");
      bindApiContext(context, token);
      if (!instances.has(token)) instances.set(token, definition.create(context));
      return instances.get(token) as T;
    },
    async unary<T>(identity: ApiIdentity, call: (transport: ApiTransport, target: string, options: RpcCallOptions, resources:RpcMarshallingContext) => Promise<T>, options?: RpcCallOptions): Promise<T> {
      const operation = await open(identity, options);
      let failed = false;
      try { operation.options.signal!.throwIfAborted();const value = await call(operation.transport, operation.target, operation.options,operation.resources); operation.options.signal!.throwIfAborted(); return value; }
      catch (error) { failed = true; throw operation.failure(error); }
      finally { await finishCleanup(operation.cleanup, failed); }
    },
    stream<T>(identity: ApiIdentity, call: (transport: ApiTransport, target: string, options: RpcCallOptions, resources:RpcMarshallingContext) => AsyncIterable<T>, options?: RpcCallOptions, enumerationSignal?: AbortSignal): ServiceStream<T> {
      const captured = snapshotOptions(options);
      const stream = (enumerationSignal?: AbortSignal): ServiceStream<T> => {
        assertCancellationSignal(enumerationSignal);
        return Object.freeze({
          withCancellation: (signal: AbortSignal) => stream(signal),
          async *[Symbol.asyncIterator](signal = enumerationSignal) {
            const operation = await open(identity, captured, signal); let failed = false;
            try { operation.options.signal!.throwIfAborted();for await (const value of call(operation.transport, operation.target, operation.options,operation.resources)) { operation.options.signal!.throwIfAborted(); yield value; } }
            catch (error) { failed = true; throw operation.failure(error); }
            finally { await finishCleanup(operation.cleanup, failed); }
          },
        });
      };
      return stream(enumerationSignal);
    },
    dispose() { parentSignal?.removeEventListener("abort", retire); lifetime.abort(new Error("The service owner has retired.")); },
    async disposeAsync(){const retained=[...resources];context.dispose();await Promise.allSettled([...opening]);await Promise.all([...retained.map(resource=>resource.revoke(lifetime.signal.reason)),serviceBindings?.disposeAsync()]);},
    async transport(options?:RpcCallOptions){lifetime.signal.throwIfAborted();options?.signal?.throwIfAborted();const signal=options?.signal?AbortSignal.any([lifetime.signal,options.signal]):lifetime.signal;
      const result=typeof transport==="function"?await transport(signal):transport;signal.throwIfAborted();return result;},
    capture(){lifetime.signal.throwIfAborted();return context;},
    bindProvider(locator:ServiceReferenceLocator,retirementSignal?:AbortSignal,boundBinding?:ServiceBinding){return Object.freeze({...context,unary:<T>(identity:ApiIdentity,call:Parameters<ApiBindingContext["unary"]>[1],options?:RpcCallOptions)=>context.unary({...identity,provider:locator,retirementSignal,boundBinding},call,options) as Promise<T>,
      stream:<T>(identity:ApiIdentity,call:Parameters<ApiBindingContext["stream"]>[1],options?:RpcCallOptions,enumerationSignal?:AbortSignal)=>context.stream({...identity,provider:locator,retirementSignal,boundBinding},call,options,enumerationSignal) as ServiceStream<T>});},
    serviceBindings(){return serviceBindings??=new ServiceBindingLifetime(signal=>context.transport({signal}),lifetime.signal);},
    serviceToken(contractId:string,serviceId:string){lifetime.signal.throwIfAborted();const token=services.get(contractId+"/"+serviceId);if(!token)throw new Error("Service reference target is not in this receiver's selected descriptor bundle.");return token;},
    fork() { lifetime.signal.throwIfAborted(); const child = createApiContext(packageId, transport, lifetime.signal); owners.get(child.api)!.generation = ownership.generation; return child; },
  });
  bindings.set(context, context);
  owners.set(context.api, ownership);
  apiBindings.set(context.api,{context,services});
  return context;
}
function snapshotOptions(options?: RpcCallOptions): RpcCallOptions | undefined {
  if (options === undefined) return undefined;
  if (!options || typeof options !== "object" || Array.isArray(options)) throw new TypeError("Service call options must be a record.");
  const signal = options.signal;
  assertCancellationSignal(signal);
  const deadline = options.deadline;
  const timestamp = deadline === undefined ? undefined : Date.prototype.getTime.call(deadline);
  if (timestamp !== undefined && !Number.isFinite(timestamp)) throw new TypeError("Service deadline must be a valid Date.");
  return Object.freeze({ ...(signal === undefined ? {} : { signal }), ...(timestamp === undefined ? {} : { deadline: new Date(timestamp) }) });
}
function assertCancellationSignal(signal: AbortSignal | undefined): void {
  if (signal !== undefined && (!signal || typeof signal.aborted !== "boolean" || typeof signal.addEventListener !== "function" || typeof signal.removeEventListener !== "function" || typeof signal.throwIfAborted !== "function"))
    throw new TypeError("Service cancellation requires an AbortSignal.");
}
async function finishCleanup(cleanup: () => Promise<void>, failed: boolean): Promise<void> {
  try { await cleanup(); }
  catch (error) {
    // A control-call failure must never masquerade as a safely retryable service
    // failure after possible dispatch, or replace the original domain failure.
    if (!failed) throw new Error("Service scope cleanup failed after possible dispatch; the operation must not be retried automatically.", { cause: error });
  }
}
