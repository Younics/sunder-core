import type { ApiBindingContext } from "./api";
import type { JsonValue, RpcCallOptions } from "./core";
import type { RpcContentReference, RpcContentRegistrationOptions, RpcInvocationContext } from "./types";
import { SunderContent, contentChunks, createContentResource, isSunderContent } from "./content";
import { exactResourceInteger } from "./resource-integers";
import { contentReferenceError } from "./descriptor-v1";
import { ServiceReference, parseServiceReferenceLocator, referenceState, resolveServiceLocator, resolveOptionalServiceLocator, serviceTargetKey, serviceTokenDefinition } from "./service-reference";
import {isSafeReferenceAbsence,staleServiceBinding,type ServiceBinding} from "./service-bindings";

/** Portable view of existing content operations; file paths stay in the Node adapter. */
export interface ResourceContentAccess {
  consumeResponseContentRetirement?(error:unknown,registrationSignal:AbortSignal):boolean;
  register(target:string,source:AsyncIterable<Uint8Array>,options:RpcContentRegistrationOptions,call?:RpcCallOptions):Promise<RpcContentReference>;
  open(reference:RpcContentReference,options?:RpcCallOptions):Promise<{readonly stream:AsyncIterable<Uint8Array>;discard():Promise<void>}>;
}
export type ResourceSendVisitor<T>=(context:RpcMarshallingContext,value:T,depth?:number)=>Promise<void>;
export type ResourceReceiveVisitor=(context:RpcMarshallingContext,value:JsonValue,depth?:number)=>Promise<void>;
/** Generated domain-error factories keep natural details until the invocation visitor runs. */
export class ServiceDomainError<T=unknown> extends Error{
  constructor(readonly code:string,message:string,readonly details:T){super(message);this.name="ServiceDomainError";}
  toJSON():never{throw new TypeError("Natural domain errors require generated service marshalling.");}
}
const errorDetails=new WeakMap<object,Map<string,unknown>>();
const invocationCancellations=new WeakMap<object,AbortSignal>();
/** @internal Only SDK resource cancellation for this exact invocation uses the cancellation response. */
export function isResourceInvocationCancellation(error:unknown,signal:AbortSignal):boolean{return !!error&&typeof error==="object"&&invocationCancellations.get(error)===signal;}
/** @internal Prepared by the receiving operation; never decoded through a reflection fallback. */
export function setServiceErrorDetails(error:object,code:string,details:unknown):void{let values=errorDetails.get(error);if(!values)errorDetails.set(error,values=new Map());values.set(code,details);}
export function getServiceErrorDetails<T>(error:unknown,code:string):T|undefined{return error&&typeof error==="object"?errorDetails.get(error)?.get(code) as T|undefined:undefined;}

/** One operation plus explicit received-content leases. No ambient/reflected resource graph. */
export class RpcMarshallingContext {
  readonly #controller=new AbortController();readonly #contents=new Set<SunderContent>();
  readonly #registeredContent=new Map<SunderContent,Promise<RpcContentReference>>();
  readonly #wireContent=new Map<SunderContent,RpcContentReference>();
  readonly #receivedContent=new Map<string,{wire:string;content:SunderContent}>();
  readonly #registeredReferences=new Map<object,{key:string;wire:JsonValue|null;locatorKey:string;strict:boolean;bindings:readonly ServiceBinding[]}>();
  readonly #preparedReferences=new Map<string,ServiceReference<unknown>|null>();
  readonly #strictLocators=new Set<string>();
  readonly #participants=new Map<ServiceBinding,()=>void>();
  #responseRetirement:unknown;
  #leases=1;#operation=true;#busy=false;#closing=false;#physicalClose:Promise<void>|undefined;#closed:Promise<void>|undefined;
  readonly #parents:readonly AbortSignal[];readonly #abort:()=>void;
  readonly #access:ResourceContentAccess|undefined;readonly #target:string;readonly #close:()=>Promise<void>;readonly #onClosed:()=>void;readonly #borrowed:boolean;
  private constructor(readonly receiver:ApiBindingContext|undefined,access:ResourceContentAccess|undefined,target:string,
    close:()=>Promise<void>,onClosed:()=>void,borrowed:boolean,parents:readonly AbortSignal[]){
    this.#access=access;this.#target=target;this.#close=close;this.#onClosed=onClosed;this.#borrowed=borrowed;
    this.#parents=parents;this.#abort=()=>{const aborted=parents.find(signal=>signal.aborted);void this.revoke(aborted?.reason).catch(()=>{});};
    for(const parent of parents)parent.addEventListener("abort",this.#abort,{once:true});if(parents.some(signal=>signal.aborted))this.#abort();
  }
  get signal():AbortSignal{return this.#controller.signal;}
  /** @internal Preserve domain/broker failures unless this invocation's resources retired. */
  invocationFailure(error:unknown):unknown{
    if(!this.#borrowed||!this.signal.aborted)return error;
    const cancelled=new Error("The invocation's service resource lifetime was cancelled.",{cause:error});
    invocationCancellations.set(cancelled,this.#parents[0]!);return cancelled;
  }
  /** @internal Generated request visitors and exact-target calls register participants. */
  addParticipant(binding:ServiceBinding):void{
    this.signal.throwIfAborted();binding.assertCurrent();if(this.#participants.has(binding))return;
    if(this.#participants.size>=4096)throw new Error("The operation service-participant limit was reached.");
    this.#participants.set(binding,binding.participate(()=>this.revoke(binding.signal.reason)));
  }
  /** @internal Pass the marshalling signal into domain stream enumeration too. */
  async *iterate<T>(source:AsyncIterable<T>):AsyncIterable<T>{
    const iterator=(source as {[Symbol.asyncIterator](signal?:AbortSignal):AsyncIterator<T>})[Symbol.asyncIterator](this.signal);
    try{while(true){this.signal.throwIfAborted();const next=await iterator.next();this.signal.throwIfAborted();if(next.done)return;yield next.value;}}
    finally{await iterator.return?.();}
  }
  /** @internal ApiBindingContext owns the existing call scope and its durable cancellation link. */
  static forCall(receiver:ApiBindingContext,content:ResourceContentAccess|undefined,target:string,signal:AbortSignal,close:()=>Promise<void>,onClosed:()=>void,scopeSignal?:AbortSignal):RpcMarshallingContext{
    return new RpcMarshallingContext(receiver,content,target,close,onClosed,false,scopeSignal?[signal,scopeSignal]:[signal]);
  }
  static forInvocation(invocation:RpcInvocationContext,receiver?:ApiBindingContext):RpcMarshallingContext{
    const content=invocation.content;
    return new RpcMarshallingContext(receiver,content?{
      register:(_target,source,options,call)=>content.register(source,options,call),
      open:(reference,options)=>content.open(reference,options),
      consumeResponseContentRetirement:(error,signal)=>content.consumeResponseContentRetirement?.(error,signal)===true,
    }:undefined,"",async()=>{},()=>{},true,receiver?[invocation.signal,receiver.signal]:[invocation.signal]);
  }
  async serialize<T>(value:T,register:ResourceSendVisitor<T>,convert:(value:T,context:RpcMarshallingContext)=>JsonValue):Promise<JsonValue>{
    this.#begin();try{await register(this,value);this.signal.throwIfAborted();return convert(value,this);}finally{this.#clear();}
  }
  /** @internal Only generated opted-in response serialization may complete a stream. */
  async serializeResponse<T>(value:T,register:ResourceSendVisitor<T>,convert:(value:T,context:RpcMarshallingContext)=>JsonValue,completeOnRetirement:boolean):Promise<JsonValue|undefined>{
    this.#responseRetirement=undefined;
    try{return await this.serialize(value,register,convert);}
    catch(error){if(completeOnRetirement&&this.#borrowed&&!this.signal.aborted&&error!==undefined&&error===this.#responseRetirement)return undefined;throw error;}
    finally{this.#responseRetirement=undefined;}
  }
  async deserialize<T>(value:JsonValue,prepare:ResourceReceiveVisitor,convert:(value:JsonValue,context:RpcMarshallingContext)=>T):Promise<T>{
    this.#begin();try{await prepare(this,value);this.signal.throwIfAborted();return convert(value,this);}
    catch(error){await Promise.allSettled([...this.#receivedContent.values()].map(item=>item.content.dispose()));throw error;}
    finally{this.#clear();}
  }
  #begin():void{this.signal.throwIfAborted();if(!this.#operation||this.#busy)throw new Error("Resource values require one active, non-overlapping marshalling operation.");this.#busy=true;}
  #clear():void{this.#registeredContent.clear();this.#wireContent.clear();this.#receivedContent.clear();this.#registeredReferences.clear();this.#preparedReferences.clear();this.#strictLocators.clear();this.#busy=false;}
  #active():void{this.signal.throwIfAborted();if(!this.#operation||!this.#busy)throw new Error("Resource conversion requires a generated per-value visitor.");}
  async registerContent(content:SunderContent|null|undefined):Promise<void>{
    this.#active();if(content==null)return;if(!isSunderContent(content))throw new TypeError("Natural service content must be a SunderContent resource.");
    let registered=this.#registeredContent.get(content);
    if(!registered){
      registered=(async()=>{
        if(!this.#access)throw new Error("The current RPC transport does not expose its existing content scope operations.");
        const read=await content.openRead(this.signal);
        // Unique to this attempt, and not exposed to the source's open/read callback.
        const registrationSignal=AbortSignal.any([this.signal]);
        let wire:RpcContentReference;
        try{wire=await this.#access.register(this.#target,contentChunks(read,this.signal),{mediaType:content.mediaType,fileName:content.fileName,
          ...(content.length===null?{}:{length:content.length})},{signal:registrationSignal});}
        catch(error){if(this.#access.consumeResponseContentRetirement?.(error,registrationSignal))this.#responseRetirement=error;throw error;}
        this.signal.throwIfAborted();validateContentReference(wire);return Object.freeze({...wire});
      })();this.#registeredContent.set(content,registered);
    }
    this.#wireContent.set(content,await registered);
  }
  contentToWire(content:SunderContent):JsonValue{
    this.#active();const wire=this.#wireContent.get(content);if(!wire)throw new TypeError("Content was not registered by the generated send visitor.");return wire;
  }
  contentFromWire(value:JsonValue):SunderContent{
    this.#active();validateContentReference(value);const wire=Object.freeze({id:value.id,length:value.length,sha256:value.sha256,mediaType:value.mediaType,fileName:value.fileName,expiresAtUtc:value.expiresAtUtc,repeatability:value.repeatability}) as RpcContentReference;
    const text=JSON.stringify(wire);const previous=this.#receivedContent.get(wire.id);
    if(previous){if(previous.wire!==text)throw new TypeError("Aliased content references disagree on metadata.");return previous.content;}
    if(!this.#access)throw new Error("The current RPC transport does not expose its existing content scope operations.");
    this.#leases++;let used=false;
    const content=createContentResource({mediaType:wire.mediaType,fileName:wire.fileName,length:wire.length,sha256:wire.sha256,expiresAtUtc:wire.expiresAtUtc,isRepeatable:wire.repeatability==="repeatable"},async signal=>{
      this.signal.throwIfAborted();if(used&&wire.repeatability==="single-use")throw new Error("Received content is single-use.");used=true;
      const opened=await this.#access!.open(wire,{signal:AbortSignal.any([signal,this.signal])});
      return {stream:opened.stream,close:()=>opened.discard()};
    },async()=>{this.#contents.delete(content);this.#leases--;if(!this.#operation&&this.#leases===0)this.#startClose();await this.#physicalClose;});
    this.#contents.add(content);this.#receivedContent.set(wire.id,{wire:text,content});return content;
  }
  async registerServiceReference(value:unknown,contractId:string,serviceId:string):Promise<void>{
    this.#active();if(value==null)return;const state=referenceState(value);state.context.signal.throwIfAborted();state.binding.assertCurrent();
    const token=this.#receiver().serviceToken(contractId,serviceId);const target=serviceTokenDefinition(token).target;
    if(serviceTargetKey(state.target)!==serviceTargetKey(target))throw new TypeError("Service reference has a different projected service target.");
    const locatorKey=JSON.stringify(state.locator);this.#strictLocators.add(locatorKey);
    const previous=this.#registeredReferences.get(value as object);if(previous){if(previous.key!==serviceTargetKey(target))throw new TypeError("Service reference target changed within a value.");if(previous.strict)return;}
    const provider=await resolveServiceLocator(this.#receiver(),state.locator,target,{signal:this.signal});this.signal.throwIfAborted();
    const binding=this.#receiver().serviceBindings().track(provider);state.binding.assertCurrent();binding.assertCurrent();
    if(!this.#borrowed){this.addParticipant(state.binding);this.addParticipant(binding);}
    this.#registeredReferences.set(value as object,{key:serviceTargetKey(target),wire:state.locator as unknown as JsonValue,locatorKey,strict:true,bindings:[state.binding,binding]});
  }
  async registerOptionalServiceReference(value:unknown,contractId:string,serviceId:string):Promise<void>{
    this.#active();if(value===null)return;const state=referenceState(value);state.context.signal.throwIfAborted();
    const receiver=this.#receiver(),target=serviceTokenDefinition(receiver.serviceToken(contractId,serviceId)).target;
    if(serviceTargetKey(state.target)!==serviceTargetKey(target))throw new TypeError("Service reference has a different projected service target.");
    const locatorKey=JSON.stringify(state.locator);
    if(this.#strictLocators.has(locatorKey)){await this.registerServiceReference(value,contractId,serviceId);return;}
    // Do not use an old unavailable cache entry to hide a fresh authorization failure.
    const provider=await resolveOptionalServiceLocator(receiver,state.locator,target,{signal:this.signal});this.signal.throwIfAborted();state.context.signal.throwIfAborted();
    const bindings=[state.binding,...provider?[receiver.serviceBindings().track(provider)]:[]];
    let absent=provider===null;
    for(const binding of bindings)if(binding.signal.aborted){if(!isSafeReferenceAbsence(binding.signal.reason))throw binding.signal.reason;absent=true;}
    this.#registeredReferences.set(value as object,{key:serviceTargetKey(target),wire:absent?null:state.locator as unknown as JsonValue,locatorKey,strict:false,bindings});
  }
  serviceReferenceToWire(value:unknown,contractId:string,serviceId:string):JsonValue{
    this.#active();const registered=value&&typeof value==="object"?this.#registeredReferences.get(value):undefined;
    if(!registered||registered.key!==contractId+"/"+serviceId)throw new TypeError("Service reference was not registered by the generated send visitor.");
    for(const binding of registered.bindings)binding.assertCurrent();if(registered.wire===null)throw staleServiceBinding();return registered.wire;
  }
  optionalServiceReferenceToWire(value:unknown,contractId:string,serviceId:string):JsonValue{
    this.#active();if(value===null)return null;const registered=this.#registeredReferences.get(value as object);
    if(!registered||registered.key!==contractId+"/"+serviceId)throw new TypeError("Optional reference needs its generated occurrence visitor.");
    if(this.#strictLocators.has(registered.locatorKey))return this.serviceReferenceToWire(value,contractId,serviceId);
    for(const binding of registered.bindings)if(binding.signal.aborted){if(isSafeReferenceAbsence(binding.signal.reason))return null;throw binding.signal.reason;}
    return registered.wire;
  }
  async prepareServiceReference(value:JsonValue,contractId:string,serviceId:string):Promise<void>{
    this.#active();if(value===null)return;const locator=parseServiceReferenceLocator(value);const key=contractId+"/"+serviceId+"\0"+JSON.stringify(locator);
    this.#strictLocators.add(JSON.stringify(locator));const receiver=this.#receiver();const token=receiver.serviceToken(contractId,serviceId);
    const provider=await resolveServiceLocator(receiver,locator,serviceTokenDefinition(token).target,{signal:this.signal});this.signal.throwIfAborted();
    const reference=this.#preparedReferences.get(key)??ServiceReference.bind(receiver,token,provider);referenceState(reference).binding.assertCurrent();if(this.#borrowed)this.addParticipant(referenceState(reference).binding);
    this.#preparedReferences.set(key,reference);
  }
  async prepareOptionalServiceReference(value:JsonValue,contractId:string,serviceId:string):Promise<void>{
    this.#active();if(value===null)return;const locator=parseServiceReferenceLocator(value),locatorKey=JSON.stringify(locator),key=contractId+"/"+serviceId+"\0"+locatorKey;
    if(this.#strictLocators.has(locatorKey)){await this.prepareServiceReference(value,contractId,serviceId);return;}
    const receiver=this.#receiver(),token=receiver.serviceToken(contractId,serviceId);
    const provider=await resolveOptionalServiceLocator(receiver,locator,serviceTokenDefinition(token).target,{signal:this.signal});this.signal.throwIfAborted();
    const previous=this.#preparedReferences.get(key);const reference=previous===null?null:previous??(provider?ServiceReference.bind(receiver,token,provider):null);
    if(reference&&referenceState(reference).binding.signal.aborted){const reason=referenceState(reference).binding.signal.reason;if(!isSafeReferenceAbsence(reason))throw reason;this.#preparedReferences.set(key,null);}
    else this.#preparedReferences.set(key,provider?reference:null);
  }
  serviceReferenceFromWire<T>(value:JsonValue,contractId:string,serviceId:string):ServiceReference<T>{
    this.#active();const locator=parseServiceReferenceLocator(value);const result=this.#preparedReferences.get(contractId+"/"+serviceId+"\0"+JSON.stringify(locator));
    if(!result)throw new TypeError("Service reference requires the generated receiver preparation visitor.");referenceState(result).binding.assertCurrent();return result as ServiceReference<T>;
  }
  optionalServiceReferenceFromWire<T>(value:JsonValue,contractId:string,serviceId:string):ServiceReference<T>|null{
    this.#active();if(value===null)return null;const locator=parseServiceReferenceLocator(value),locatorKey=JSON.stringify(locator),key=contractId+"/"+serviceId+"\0"+locatorKey;
    if(this.#strictLocators.has(locatorKey))return this.serviceReferenceFromWire<T>(value,contractId,serviceId);
    if(!this.#preparedReferences.has(key))throw new TypeError("Optional reference requires its generated receiver preparation visitor.");
    const reference=this.#preparedReferences.get(key);if(!reference)return null;const binding=referenceState(reference).binding;
    if(binding.signal.aborted){if(isSafeReferenceAbsence(binding.signal.reason))return null;throw binding.signal.reason;}return reference as ServiceReference<T>;
  }
  #receiver():ApiBindingContext{if(!this.receiver)throw new Error("Typed service references require the receiver's generated API lifetime.");return this.receiver;}
  dispose():Promise<void>{
    if(this.#operation){this.#operation=false;this.#leases--;}
    if(this.#borrowed)return this.revoke(new Error("The provider invocation has completed."));
    if(this.#leases===0)this.#startClose();return this.#closed??Promise.resolve();
  }
  revoke(reason:unknown=new Error("The service resource lifetime has retired.")):Promise<void>{
    if(this.#closing)return this.#closed??Promise.resolve();this.#closing=true;
    const contents=[...this.#contents];this.#startPhysicalClose();
    this.#closed=Promise.resolve().then(async()=>{
      const results=await Promise.allSettled([this.#physicalClose!,...contents.map(content=>content.dispose())]);
      this.#finish();const failed=results.find(result=>result.status==="rejected");if(failed?.status==="rejected")throw failed.reason;
    });void this.#closed.catch(()=>{});this.#controller.abort(reason);return this.#closed;
  }
  #startClose():void{
    if(this.#closing)return;this.#closing=true;this.#startPhysicalClose();
    this.#closed=this.#physicalClose!.finally(()=>this.#finish());void this.#closed.catch(()=>{});this.#controller.abort(new Error("The service operation has completed."));
  }
  #startPhysicalClose():void{this.#physicalClose??=Promise.resolve().then(this.#close);void this.#physicalClose.catch(()=>{});}
  #finish():void{for(const parent of this.#parents)parent.removeEventListener("abort",this.#abort);for(const release of this.#participants.values())release();this.#participants.clear();this.#onClosed();}
}
function validateContentReference(value:unknown):asserts value is RpcContentReference{
  const error=contentReferenceError(value,"content");if(error||!value||typeof value!=="object"||!exactResourceInteger(value,"length",(value as RpcContentReference).length))throw new TypeError(error??"Content length is not an exact safe integer.");
}
