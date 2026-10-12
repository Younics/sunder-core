import type { ApiProvider, ApiTransport } from "./api";
import type { RpcCallOptions } from "./core";
import { waitForResource } from "./content";
import { locatorFromProvider, sameServiceLocator, type ServiceReferenceLocator } from "./service-reference";
import { RpcError } from "./types";
import { isPackageId } from "./validation";

type Snapshot = Awaited<ReturnType<ApiTransport["discover"]>>;
type Change = { readonly revision: number; readonly sequence: number; readonly kind: string; readonly provider: ApiProvider | null };
type Cursor = { readonly revision: number; readonly sequence: number };
type Contract = { sequence: number; identities:Set<string>; snapshot?: Snapshot; error?: unknown; optionalOnly?:boolean };
const maximumBindings = 4096;
const deferred = () => {
  let resolve!: () => void; let reject!: (error: unknown) => void;
  const promise = new Promise<void>((yes, no) => { resolve = yes; reject = no; });
  void promise.catch(() => {}); return { promise, resolve, reject };
};
const failure = (kind: "unavailable" | "protocol" | "resource-exhausted" | "stale-endpoint" | "provider-faulted", code: string, message: string) => new RpcError({ kind, code, message });
const unavailable = () => failure("unavailable", "rpc.catalog.disconnected", "The receiver's service catalog is unavailable.");
const invalid = () => failure("protocol", "rpc.binding.invalid-catalog", "The broker returned invalid service catalog metadata.");
const capacity = () => failure("resource-exhausted", "rpc.binding.capacity", "The receiving service binding limit was reached.");
export const staleServiceBinding = () => failure("stale-endpoint", "rpc.stale-endpoint", "The exact referenced service activation has retired.");
export function serviceFailureKind(error: unknown): unknown {
  return error && typeof error === "object" && "error" in error && error.error && typeof error.error === "object" && "kind" in error.error ? error.error.kind : undefined;
}
/** @internal Only the exact-reference lookup stage may interpret this as absence. */
export function isSafeReferenceAbsence(error:unknown):boolean{
  if(!error||typeof error!=="object"||!("error" in error)||!error.error||typeof error.error!=="object")return false;
  const value=error.error as {kind?:unknown;dispatchState?:unknown;retrySafety?:unknown};
  return (value.kind==="unavailable"||value.kind==="stale-endpoint")&&value.dispatchState==="not-dispatched"&&value.retrySafety==="safe";
}
const observedFailure = (error: unknown) => serviceFailureKind(error) === undefined ? unavailable() : error;
const keyOf = (provider: ApiProvider) => JSON.stringify(locatorFromProvider(provider));

/** @internal One permanent activation binding. Typed proxies borrow their receiver. */
export class ServiceBinding {
  readonly #controller = new AbortController();
  readonly #listeners = new Set<() => void | Promise<void>>();
  readonly #entries = new Map<object, unknown>();
  constructor(readonly owner: ServiceBindingLifetime, readonly snapshot: Readonly<ApiProvider>, readonly epoch: number, readonly sequence: number) {
    // Business cancellation callbacks cannot kill the shared observer. Preserve a
    // real AbortSignal (including its platform brand), isolating listener failures.
    const signal = this.#controller.signal;
    const add = signal.addEventListener.bind(signal), remove = signal.removeEventListener.bind(signal);
    const wrappers = new WeakMap<object, EventListener>();
    let onabort:EventListener|null=null;
    Object.defineProperties(signal, {
      onabort:{get:()=>onabort,set:(listener:EventListener|null)=>{if(onabort)signal.removeEventListener("abort",onabort);onabort=typeof listener==="function"?listener:null;if(onabort)signal.addEventListener("abort",onabort);}},
      addEventListener: { value: (type: string, listener: EventListenerOrEventListenerObject | null, options?: AddEventListenerOptions | boolean) => {
        if (!listener) return;
        let wrapped = wrappers.get(listener);
        if (!wrapped) { wrapped = event => { try {
          const result: unknown = typeof listener === "function" ? listener.call(signal, event) : listener.handleEvent(event);
          if (result && typeof (result as PromiseLike<unknown>).then === "function") void Promise.resolve(result).catch(() => {});
        } catch {} }; wrappers.set(listener, wrapped); }
        add(type, wrapped, options);
      } },
      removeEventListener: { value: (type: string, listener: EventListenerOrEventListenerObject | null, options?: EventListenerOptions | boolean) => {
        if (listener) remove(type, wrappers.get(listener) ?? listener, options);
      } },
    });
  }
  get signal(): AbortSignal { return this.#controller.signal; }
  get locator(): ServiceReferenceLocator { return locatorFromProvider(this.snapshot); }
  assertCurrent(): void { this.signal.throwIfAborted(); }
  entry<T>(token: object, create: () => T): T {
    if (this.#entries.has(token)) return this.#entries.get(token) as T;
    if (this.#entries.size >= 128) throw capacity();
    const value = create(); if (!this.signal.aborted) this.#entries.set(token, value); return value;
  }
  participate(retire: () => void | Promise<void>): () => void {
    this.assertCurrent(); if (this.#listeners.size >= maximumBindings) throw capacity();
    this.#listeners.add(retire); return () => this.#listeners.delete(retire);
  }
  retire(reason: unknown): Promise<void> {
    if (this.signal.aborted) return Promise.resolve();
    this.#entries.clear(); this.#controller.abort(reason);
    const cleanup = [...this.#listeners].map(listener => { try { return Promise.resolve(listener()); } catch { return Promise.resolve(); } });
    this.#listeners.clear(); return Promise.allSettled(cleanup).then(() => {});
  }
  ensureCurrent(signal?: AbortSignal): Promise<void> { return this.owner.ensureCurrent(this, signal); }
  tryReportInvariantViolation(error: unknown, signal?: AbortSignal): Promise<boolean> { return this.owner.tryReport(this, error, signal); }
}

/** Codegen owner: one lazy, bounded catalog observer per receiver API lifetime. */
export class ServiceBindingLifetime {
  readonly #stopping = new AbortController();
  readonly #contracts = new Map<string, Contract>();
  readonly #bindings = new Map<string, ServiceBinding>();
  readonly #retirements = new Set<Promise<void>>();
  readonly #queries = new Set<Promise<unknown>>();
  #ready = deferred(); #observer?: Promise<void>; #observation?: AbortController; #disposed?: Promise<void>;
  #connected = false; #epoch = 0; #availabilityError?: unknown;
  readonly #parentAbort: () => void;
  constructor(readonly transport: (signal: AbortSignal) => Promise<ApiTransport>, readonly parent: AbortSignal) {
    this.#parentAbort = () => { void this.disposeAsync().catch(() => {}); };
    parent.addEventListener("abort", this.#parentAbort, { once: true });
    if (parent.aborted) this.#parentAbort();
  }
  #check(epoch = this.#epoch): void {
    if(this.#disposed)throw this.#stopping.signal.reason??failure("unavailable","rpc.binding.lifetime-disposed","The receiving service lifetime has retired.");
    this.#stopping.signal.throwIfAborted();
    if (epoch !== this.#epoch) throw this.#availabilityError ?? unavailable();
  }
  #track<T>(work: () => Promise<T>): Promise<T> {
    const promise = work(); this.#queries.add(promise);
    void promise.finally(() => this.#queries.delete(promise)).catch(() => {}); return promise;
  }
  async discover(contractId: string, options?: RpcCallOptions, optionalLookup=false): Promise<Snapshot> {
    this.#check(); options?.signal?.throwIfAborted();
    if (!isPackageId(contractId)) throw new TypeError("Invalid service contract identity.");
    if (!this.#contracts.has(contractId)) {
      if (this.#contracts.size >= 128) throw capacity();
      this.#contracts.set(contractId, { sequence: -1,identities:new Set(),optionalOnly:optionalLookup });
    }
    if(!optionalLookup)this.#contracts.get(contractId)!.optionalOnly=false;
    this.#observer ??= this.#observe();
    const signal = AbortSignal.any([this.#stopping.signal, ...(options?.signal ? [options.signal] : [])]);
    if (!this.#connected && this.#availabilityError !== undefined){
      const cached=this.#availabilityError;
      if(isSafeReferenceAbsence(cached))await this.#track(async()=>{const rpc=await this.transport(signal);await rpc.discover(contractId,{...options,signal});signal.throwIfAborted();});
      throw cached;
    }
    await waitForResource(this.#ready.promise, signal); this.#check();
    const epoch = this.#epoch;
    return this.#track(async () => {
      try {
        const rpc = await this.transport(signal); const snapshot = await rpc.discover(contractId, { ...options, signal });
        signal.throwIfAborted(); this.#check(epoch); this.#applySnapshot(contractId, snapshot); return this.#contracts.get(contractId)!.snapshot!;
      } catch (error) { if (!signal.aborted&&!(optionalLookup&&isSafeReferenceAbsence(error))) this.#queryFailed(error, contractId, epoch); throw error; }
    });
  }
  track(snapshot: ApiProvider): ServiceBinding {
    this.#check(); validateProvider(snapshot);
    if (!this.#connected) throw this.#availabilityError ?? unavailable();
    const contract = this.#contracts.get(snapshot.contractId); if (!contract) throw invalid();
    const key = keyOf(snapshot), previous = this.#bindings.get(key);
    if (previous) { if(snapshot.state!=="active")this.#retire(previous,staleServiceBinding()); return previous; }
    const binding = new ServiceBinding(this, Object.freeze({ ...snapshot }), this.#epoch, contract.sequence);
    if (snapshot.state !== "active" || !contract.identities.has(key)) {
      void binding.retire(contract.error ?? staleServiceBinding()); return binding;
    }
    if (this.#bindings.size + this.#retirements.size >= maximumBindings) throw capacity();
    this.#bindings.set(key, binding); return binding;
  }
  async resolve(locator: ServiceReferenceLocator, signal?: AbortSignal): Promise<ServiceBinding> {
    const snapshot = await this.discover(locator.contractId, { signal });
    const current = snapshot.providers.filter(value => value.state === "active" && sameServiceLocator(locator, locatorFromProvider(value)));
    if (current.length !== 1) throw staleServiceBinding();
    const binding = this.track(current[0]!); await binding.ensureCurrent(signal); return binding;
  }
  /** A fresh single-target lookup cannot poison unrelated outer-call participants. */
  async resolveOptional(locator:ServiceReferenceLocator,caller?:AbortSignal):Promise<ServiceBinding|null>{
    this.#check();caller?.throwIfAborted();const signal=AbortSignal.any([this.#stopping.signal,...caller?[caller]:[]]);
    return this.#track(async()=>{
      try{
        const rpc=await this.transport(signal);signal.throwIfAborted();
        // Always ask afresh, even when the observer has a cached unavailable error.
        // A current permission/validation failure must not be hidden by that cache.
        const fresh=await rpc.discover(locator.contractId,{signal});
        if(!fresh||!Array.isArray(fresh.providers)||fresh.providers.length>maximumBindings||!safe(fresh.revision)||!safe(fresh.sequence)||typeof fresh.resetRequired!=="boolean"||fresh.resetRequired)throw invalid();
        const endpoints=new Set<string>();for(const provider of fresh.providers){validateProvider(provider);if(provider.contractId!==locator.contractId||provider.catalogRevision!>fresh.revision||endpoints.has(provider.endpointReference!))throw invalid();endpoints.add(provider.endpointReference!);}
        let current:ApiProvider|null|undefined=fresh.providers.find(provider=>sameServiceLocator(locator,locatorFromProvider(provider)));
        // Browser handles are receiver-local authority. Never invent one from the
        // sender's portable endpoint; use only this receiver's fresh catalog proof.
        if(current&&rpc.getProvider)current=await rpc.getProvider(current.providerHandle??locator.endpoint,{signal});
        signal.throwIfAborted();if(!current)return null;validateProvider(current);
        if(current.state==="faulted")throw failure("provider-faulted","rpc.provider-faulted","The exact reference target is faulted.");
        if(current.state!=="active"||!sameServiceLocator(locator,locatorFromProvider(current)))return null;
        const snapshot=await this.discover(locator.contractId,{signal},true);signal.throwIfAborted();
        const visible=snapshot.providers.find(provider=>sameServiceLocator(locator,locatorFromProvider(provider)));
        if(!visible||visible.state==="inactive")return null;
        if(visible.state==="faulted")throw failure("provider-faulted","rpc.provider-faulted","The exact reference target is faulted.");
        const binding=this.track(visible);binding.assertCurrent();return binding;
      }catch(error){signal.throwIfAborted();if(isSafeReferenceAbsence(error))return null;throw error;}
    });
  }
  async ensureCurrent(binding: ServiceBinding, caller?: AbortSignal): Promise<void> {
    caller?.throwIfAborted(); binding.assertCurrent(); this.#check();
    const epoch = this.#epoch, signal = AbortSignal.any([this.#stopping.signal, binding.signal, ...(caller ? [caller] : [])]);
    return this.#track(async () => {
      let endpointProbe=false;
      try {
        const rpc = await this.transport(signal);
        let current:ApiProvider|null|undefined;
        if(rpc.getProvider){endpointProbe=true;current=await rpc.getProvider(binding.snapshot.providerHandle ?? binding.locator.endpoint, { signal });}
        else {const snapshot=await rpc.discover(binding.locator.contractId,{signal});this.#check(epoch);this.#applySnapshot(binding.locator.contractId,snapshot);current=snapshot.providers.find(value=>value.endpointReference===binding.locator.endpoint);}
        caller?.throwIfAborted(); binding.assertCurrent(); this.#check(epoch);
        if (current) validateProvider(current);
        if (!current || current.state !== "active" || !sameServiceLocator(binding.locator, locatorFromProvider(current))) this.#retireCurrent(binding, staleServiceBinding());
        binding.assertCurrent();
      } catch (error) {
        if (!caller?.aborted && !this.#stopping.signal.aborted && !binding.signal.aborted){
          // Only endpoint Unavailable is target-local. Transport acquisition and
          // fallback discovery still own catalog continuity; permission denial
          // retains its contract-wide authority semantics.
          if(endpointProbe&&serviceFailureKind(error)==="unavailable"&&epoch===this.#epoch)this.#retireCurrent(binding,error);
          else this.#queryFailed(error,binding.snapshot.contractId,epoch);
        }
        throw error;
      }
    });
  }
  async tryReport(binding: ServiceBinding, error: unknown, caller?: AbortSignal): Promise<boolean> {
    try { await this.ensureCurrent(binding, caller); } catch (failure) { if (serviceFailureKind(failure) === "stale-endpoint") return false; throw failure; }
    const epoch = this.#epoch, signal = AbortSignal.any([this.#stopping.signal, ...(caller ? [caller] : [])]);
    return this.#track(async () => {
      try {
        binding.assertCurrent(); const rpc = await this.transport(signal);
        if (!rpc.reportInvariantViolation) throw unavailable();
        // The report may itself retire the provider; its acknowledgement is not linked to that retirement.
        const reported = await rpc.reportInvariantViolation(binding.snapshot.providerHandle ?? binding.locator.endpoint, error, { signal });
        if (typeof reported !== "boolean") throw invalid();
        if (reported) this.#retireCurrent(binding, failure("provider-faulted", "rpc.provider-faulted", "The exact service activation was reported for an invariant violation."));
        return reported;
      } catch (failure) { if (!signal.aborted) this.#queryFailed(failure, binding.snapshot.contractId, epoch); throw failure; }
    });
  }
  #retireCurrent(binding:ServiceBinding,reason:unknown):void{
    const contract=this.#contracts.get(binding.snapshot.contractId),key=keyOf(binding.snapshot);
    if(contract?.identities.delete(key)&&contract.snapshot)contract.snapshot=Object.freeze({...contract.snapshot,providers:Object.freeze(contract.snapshot.providers.filter(provider=>keyOf(provider)!==key))});
    this.#retire(binding,reason);
  }
  #retire(binding: ServiceBinding, reason: unknown): void {
    const key = keyOf(binding.snapshot); if (this.#bindings.get(key) === binding) this.#bindings.delete(key);
    if (binding.signal.aborted) return;
    const cleanup = binding.retire(reason); this.#retirements.add(cleanup);
    void cleanup.finally(() => this.#retirements.delete(cleanup));
  }
  #queryFailed(error: unknown, contractId: string, epoch: number): void {
    if (epoch !== this.#epoch || this.#stopping.signal.aborted) return;
    if (serviceFailureKind(error) === "permission-denied") {
      const contract = this.#contracts.get(contractId)!; contract.snapshot = undefined; contract.identities.clear();contract.error = error;
      for (const binding of this.#bindings.values()) if (binding.snapshot.contractId === contractId) this.#retire(binding, error);
    } else this.#invalidate(observedFailure(error), epoch);
  }
  #invalidate(error: unknown, epoch: number): void {
    if (epoch !== this.#epoch) return;
    this.#epoch++; this.#connected = false; this.#availabilityError = error; this.#ready.reject(error);
    for (const binding of this.#bindings.values()) this.#retire(binding, error);
    for (const contract of this.#contracts.values()) { contract.snapshot = undefined;contract.identities.clear(); contract.sequence = -1; contract.error = undefined; }
    this.#observation?.abort(error);
  }
  #applySnapshot(id: string, snapshot: Snapshot): void {
    if (!snapshot || !safe(snapshot.revision) || !safe(snapshot.sequence) || typeof snapshot.resetRequired !== "boolean" || !Array.isArray(snapshot.providers) || snapshot.providers.length > maximumBindings) throw invalid();
    if (snapshot.resetRequired) throw unavailable();
    const endpoints = new Set<string>();
    for (const provider of snapshot.providers) {
      validateProvider(provider);
      if (provider.contractId !== id || provider.catalogRevision! > snapshot.revision! || endpoints.has(provider.endpointReference!)) throw invalid();
      endpoints.add(provider.endpointReference!);
    }
    const contract = this.#contracts.get(id)!; if (snapshot.sequence! < contract.sequence) return;
    if ([...this.#contracts].reduce((sum, [key, value]) => sum + (key === id ? 0 : value.snapshot?.providers.length ?? 0), snapshot.providers.length) > maximumBindings) throw capacity();
    contract.snapshot = Object.freeze({ ...snapshot, providers: Object.freeze(snapshot.providers.map(provider => Object.freeze({ ...provider }))) });
    contract.identities=new Set(snapshot.providers.filter(provider=>provider.state==="active").map(keyOf));
    contract.sequence = snapshot.sequence!; contract.error = undefined;
    for (const [key,binding] of this.#bindings) if (binding.snapshot.contractId === id && !contract.identities.has(key)) this.#retire(binding, staleServiceBinding());
  }
  #applyEvent(event: Change, cursor: Cursor): Cursor {
    if (!event || !safe(event.revision) || !safe(event.sequence) || !["added", "removed", "activated", "deactivated", "faulted", "reset-required"].includes(event.kind)) throw invalid();
    if (event.kind === "reset-required") throw unavailable();
    if (event.revision < cursor.revision || event.sequence <= cursor.sequence || !event.provider) throw invalid();
    const provider = event.provider; validateProvider(provider); if (provider.catalogRevision! > event.revision) throw invalid();
    const withdraw = ["removed", "deactivated", "faulted"].includes(event.kind) || provider.state !== "active";
    for (const binding of this.#bindings.values()) {
      const before = binding.snapshot;
      if (event.sequence > binding.sequence && (before.endpointReference === provider.endpointReference || before.packageId === provider.packageId && before.providerId === provider.providerId && before.contractId === provider.contractId)
        && (withdraw || keyOf(before) !== keyOf(provider))) this.#retire(binding, event.kind === "faulted" ? failure("provider-faulted", "rpc.provider-faulted", "The exact service activation faulted.") : staleServiceBinding());
    }
    const contract = this.#contracts.get(provider.contractId);
    if (contract && event.sequence > contract.sequence && contract.snapshot) {
      const providers = contract.snapshot.providers.filter(before => before.endpointReference !== provider.endpointReference && (withdraw || before.packageId !== provider.packageId || before.providerId !== provider.providerId));
      this.#applySnapshot(provider.contractId, { revision: event.revision, sequence: event.sequence, resetRequired: false, providers: withdraw ? providers : [...providers, provider] });
    }
    return { revision: event.revision, sequence: event.sequence };
  }
  async #refresh(epoch: number, signal: AbortSignal): Promise<Cursor> {
    let cursor: Cursor | undefined, denied: unknown;
    const rpc = await this.transport(signal);
    for (const id of this.#contracts.keys()) {
      try {
        const snapshot = await rpc.discover(id, { signal }); signal.throwIfAborted(); this.#check(epoch); this.#applySnapshot(id, snapshot);
        if (!cursor || snapshot.sequence! < cursor.sequence) cursor = { revision: snapshot.revision!, sequence: snapshot.sequence! };
      } catch (error) {
        if(this.#contracts.get(id)?.optionalOnly&&isSafeReferenceAbsence(error)){
          const contract=this.#contracts.get(id)!;contract.error=error;contract.snapshot=undefined;contract.identities.clear();
          for(const binding of this.#bindings.values())if(binding.snapshot.contractId===id)this.#retire(binding,error);
          continue;
        }
        if (serviceFailureKind(error) !== "permission-denied") throw error; denied = error; this.#queryFailed(error, id, epoch);
      }
    }
    if (!cursor) throw denied ?? unavailable(); return cursor;
  }
  async #observe(): Promise<void> {
    while (!this.#stopping.signal.aborted) {
      const epoch = this.#epoch, observation = new AbortController(); this.#observation = observation; this.#ready = deferred();
      const signal = AbortSignal.any([observation.signal, this.#stopping.signal]);
      let iterator: AsyncIterator<Change> | undefined, pending: Promise<IteratorResult<Change>> | undefined;
      try {
        let cursor = await this.#refresh(epoch, signal); const rpc = await this.transport(signal);
        if (!rpc.watch) throw unavailable(); iterator = rpc.watch(cursor.revision, cursor.sequence, { signal })[Symbol.asyncIterator]();
        let completed = false;
        const next = () => { completed = false; pending = iterator!.next(); void pending.then(() => { completed = true; }, () => { completed = true; }); };
        next(); await this.#refresh(epoch, signal); let drained = 0;
        while (completed) {
          if (++drained > maximumBindings) throw capacity(); const result = await pending!;
          if (result.done) throw unavailable(); cursor = this.#applyEvent(result.value, cursor); next(); await Promise.resolve();
        }
        this.#check(epoch); signal.throwIfAborted(); this.#connected = true; this.#availabilityError = undefined; this.#ready.resolve();
        while (true) { const result = await pending!; this.#check(epoch); if (result.done) throw unavailable(); cursor = this.#applyEvent(result.value, cursor); next(); }
      } catch (error) { this.#invalidate(observedFailure(error), epoch); }
      finally {
        observation.abort(); if (pending) await pending.catch(() => {});
        try { await iterator?.return?.(); } catch {}
        if (this.#observation === observation) this.#observation = undefined;
      }
      if (!this.#stopping.signal.aborted) await delay(250, this.#stopping.signal);
    }
  }
  disposeAsync(): Promise<void> {
    if (this.#disposed) return this.#disposed;
    const completion=deferred();this.#disposed=completion.promise;
    const reason = failure("unavailable", "rpc.binding.lifetime-disposed", "The receiving service lifetime has retired.");
    this.#invalidate(reason, this.#epoch); this.#stopping.abort(reason); this.parent.removeEventListener("abort", this.#parentAbort);
    void Promise.resolve().then(async () => {
      await Promise.allSettled([this.#observer, ...this.#queries]); await Promise.allSettled([...this.#retirements]); this.#contracts.clear();
    }).then(completion.resolve,completion.reject); return this.#disposed;
  }
}
function safe(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0; }
function validateProvider(provider: ApiProvider): void {
  try { locatorFromProvider(provider); } catch { throw invalid(); }
  if (!["active", "inactive", "faulted"].includes(provider.state) || !safe(provider.catalogRevision)) throw invalid();
}
function delay(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise(resolve => { const done = () => { clearTimeout(timer); signal.removeEventListener("abort", done); resolve(); }; const timer = setTimeout(done, milliseconds); signal.addEventListener("abort", done, { once: true }); if (signal.aborted) done(); });
}
