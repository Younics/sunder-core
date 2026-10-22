import type { RpcCallOptions } from "./core";
import type { ApiBindingContext, ApiIdentity, ApiProvider, ServiceStream } from "./api";
import type { ServiceContract, ServiceDefinition, ServiceContext } from "./services";
import { isPackageId, isSemanticVersion, isVersionInRange } from "./validation";
import { exactResourceInteger, recordResourceIntegerToken } from "./resource-integers";
import { RpcError, type RpcContractIdentity } from "./types";
import { serviceFailureKind, type ServiceBinding } from "./service-bindings";

type WithoutControl<A extends readonly unknown[]> = A extends [...infer P,infer L] ? L extends AbortSignal|ServiceContext ? P : A : A;
export type ServiceApi<T> = T extends ServiceToken<infer M> ? M : T extends ServiceDefinition<infer M> | ServiceContract<infer M>
  ? {readonly [K in keyof M]:M[K] extends (...args:infer A)=>infer R ? (...args:[...WithoutControl<A>,$options?:RpcCallOptions])=>R extends AsyncIterable<infer E>?ServiceStream<E>:Promise<Awaited<R>> : never} : T;
declare const serviceType:unique symbol;
export interface ServiceToken<T> {readonly [serviceType]:T;readonly contractId:string;readonly serviceId:string}
export interface ServiceTarget extends RpcContractIdentity {readonly serviceId:string}
export interface ServiceReferenceLocator {
  readonly packageId:string;readonly packageVersion:string;readonly providerId:string;readonly contractId:string;readonly contractVersion:string;
  readonly descriptorSha256:string;readonly abiSha256:string;readonly activationId:string;readonly activationEpoch:number;readonly sessionGeneration:number;readonly endpoint:string;
}
interface TokenDefinition {readonly target:ServiceTarget;readonly create:(context:ApiBindingContext)=>unknown}
const tokens=new WeakMap<object,TokenDefinition>();
interface ReferenceState {readonly locator:ServiceReferenceLocator;readonly target:ServiceTarget;readonly context:ApiBindingContext;readonly service:unknown;readonly binding:ServiceBinding}
const references=new WeakMap<object,ReferenceState>();
const providerStates=new WeakMap<object,{snapshot:Readonly<ApiProvider>;reference:()=>ServiceReference<unknown>}>();
const constructorKey=Object.freeze({});
const visibleProviders=new WeakMap<object,{context:ApiBindingContext;key:string}>();
const fields=["packageId","packageVersion","providerId","contractId","contractVersion","descriptorSha256","abiSha256","activationId","activationEpoch","sessionGeneration","endpoint"] as const;

/** A reference to an existing provider; obtain it from a generated Providers catalog. */
class ServiceReferenceValue<TService,TIdentity> {
  private constructor(key:object,state:ReferenceState){if(key!==constructorKey)throw new TypeError("Obtain service references from a generated provider catalog.");references.set(this,state);Object.freeze(this);}
  get Service():TService{const state=referenceState(this);state.context.signal.throwIfAborted();state.binding.assertCurrent();return state.service as TService;}
  get retirementSignal():AbortSignal{return referenceState(this).binding.signal;}
  ensureCurrent(signal?:AbortSignal):Promise<void>{return referenceState(this).binding.ensureCurrent(signal);}
  tryReportInvariantViolation(error:unknown,signal?:AbortSignal):Promise<boolean>{return referenceState(this).binding.tryReportInvariantViolation(error,signal);}
  toJSON():never{throw new TypeError("ServiceReference requires generated service marshalling.");}
  /** @internal The receiver's selected service factory is used, never a sender object. */
  static bind<T>(context:ApiBindingContext,token:ServiceToken<ServiceApi<T>>,provider:ApiProvider):ServiceReference<T>{
    return ServiceProvider.from<T>(context,token,provider).Reference;
  }
  /** @internal Only the validated catalog factory constructs an entry. */
  static create<T>(key:object,state:ReferenceState):ServiceReference<T>{return new ServiceReferenceValue<ServiceApi<T>,T>(key,state);}
}
/** Normalize authored declarations and generated service tokens to the same API.
 * The unused identity argument is retained for the compiler, not assignability. */
export type ServiceReference<T> = ServiceReferenceValue<ServiceApi<T>,T>;
declare const optionalServiceReferenceBrand:unique symbol;
/** @internal Generated property types retain source occurrence policy without a runtime wrapper. */
export type OptionalServiceReferenceMember<T> = T & {readonly [optionalServiceReferenceBrand]?:true};
/** Opt in only on a direct, required nullable record property. Default references remain strict. */
export type OptionalServiceReference<T> = OptionalServiceReferenceMember<ServiceReference<T>>;
export const ServiceReference=ServiceReferenceValue;
export class ServiceProvider<T> {
  private constructor(key:object,snapshot:Readonly<ApiProvider>,reference:()=>ServiceReference<T>){if(key!==constructorKey)throw new TypeError("Obtain providers from a generated catalog.");providerStates.set(this,{snapshot,reference:reference as ()=>ServiceReference<unknown>});Object.freeze(this);}
  get snapshot():Readonly<ApiProvider>{return providerState(this).snapshot;}
  get Reference():ServiceReference<T>{return providerState(this).reference() as ServiceReference<T>;}
  get Service():ServiceApi<T>{return this.Reference.Service;}
  get retirementSignal():AbortSignal{return this.Reference.retirementSignal;}
  ensureCurrent(signal?:AbortSignal):Promise<void>{return this.Reference.ensureCurrent(signal);}
  tryReportInvariantViolation(error:unknown,signal?:AbortSignal):Promise<boolean>{return this.Reference.tryReportInvariantViolation(error,signal);}
  toJSON():never{throw new TypeError("Serialize a provider Reference through generated service marshalling.");}
  /** @internal Only a fetched, validated catalog snapshot reaches this factory. */
  static from<T>(context:ApiBindingContext,token:ServiceToken<ServiceApi<T>>,snapshot:ApiProvider):ServiceProvider<T>{
    requireVisibleProvider(context,token,snapshot);const captured=snapshot;const locator=locatorFromProvider(captured);assertServiceTarget(locator,serviceTokenDefinition(token).target);
    const binding=context.serviceBindings().track(captured);
    return binding.entry(token,()=>{const definition=serviceTokenDefinition(token);
      const reference=ServiceReference.create<T>(constructorKey,{context,locator,target:definition.target,binding,service:definition.create(context.bindProvider(locator,binding.signal,binding))});
      return new ServiceProvider<T>(constructorKey,captured,()=>reference);});
  }
}
export interface ServiceCatalogSnapshot<T>{readonly revision:number;readonly sequence:number;readonly resetRequired:boolean;readonly providers:readonly ServiceProvider<T>[]}
export interface ServiceCatalog<T>{
  list(options?:RpcCallOptions):Promise<ServiceCatalogSnapshot<T>>;
  get(provider:ServiceProvider<T>,options?:RpcCallOptions):Promise<ServiceProvider<T>|null>;
  resolve(options?:RpcCallOptions):Promise<ServiceProvider<T>>;
  watch(afterRevision:number,afterSequence:number,options?:RpcCallOptions):AsyncIterable<{readonly revision:number;readonly sequence:number;readonly kind:string;readonly provider:ServiceProvider<T>|null}>;
}
/** @internal Codegen helper: defining a token neither registers a provider nor grants access. */
export function createServiceToken<T,const Target extends ServiceTarget>(target:Target,create:(context:ApiBindingContext)=>T):ServiceToken<T>&Pick<Target,"contractId"|"serviceId">{
  if(!isPackageId(target.contractId)||!isSemanticVersion(target.contractVersion)||!hash(target.descriptorSha256)||!hash(target.abiSha256)||!/^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$/u.test(target.serviceId)||target.serviceId.length>128)throw new TypeError("Invalid generated service target.");
  const token=Object.freeze({contractId:target.contractId,serviceId:target.serviceId});tokens.set(token,{target:Object.freeze({...target}),create});return token as ServiceToken<T>&Pick<Target,"contractId"|"serviceId">;
}
/** @internal */
export function serviceTokenDefinition(token:ServiceToken<unknown>):TokenDefinition{return tokens.get(token)??(()=>{throw new TypeError("Unknown generated service token.");})();}
/** @internal */
export function serviceTargetKey(target:Pick<ServiceTarget,"contractId"|"serviceId">):string{return target.contractId+"/"+target.serviceId;}
/** @internal */
export function referenceState(value:unknown):ReferenceState{
  if(!value||typeof value!=="object"||!references.has(value))throw new TypeError("A service reference must come from an existing generated provider binding.");return references.get(value)!;
}
function providerState(value:object){const state=providerStates.get(value);if(!state)throw new TypeError("Provider must come from a generated catalog.");return state;}
/** @internal */
export function createServiceCatalog<T>(context:ApiBindingContext,token:ServiceToken<ServiceApi<T>>,identity:ApiIdentity):ServiceCatalog<T>{
  const target=serviceTokenDefinition(token).target;
  const capture=():ApiBindingContext=>{const owner=context.capture();if(owner.serviceToken(target.contractId,target.serviceId)!==token)throw new TypeError("Service catalog token is not selected by this receiver's API lifetime.");return owner;};
  const list=async(options?:RpcCallOptions):Promise<ServiceCatalogSnapshot<T>>=>{
    const owner=capture();const value=await owner.serviceBindings().discover(target.contractId,options);owner.signal.throwIfAborted();
    return Object.freeze({revision:value.revision??0,sequence:value.sequence??0,resetRequired:value.resetRequired,
      providers:Object.freeze(value.providers.filter(provider=>matchesTarget(provider,target)).map(provider=>ServiceProvider.from(owner,token,validatedProvider(owner,target,provider))))});
  };
  return Object.freeze({list,async get(provider:ServiceProvider<T>,options?:RpcCallOptions){const snapshot=providerState(provider).snapshot;const locator=locatorFromProvider(snapshot);
     try{await provider.ensureCurrent(options?.signal);const owner=capture();const current=await resolveServiceLocator(owner,locator,target,options);return ServiceProvider.from(owner,token,current);}catch(error){if(serviceFailureKind(error)==="stale-endpoint")return null;throw error;}},
    async resolve(options?:RpcCallOptions){const snapshot=await list(options);if(snapshot.resetRequired)throw staleReference();
      const matches=snapshot.providers.filter(provider=>provider.snapshot.state==="active"&&provider.snapshot.packageId===identity.packageId
        &&(identity.packageRange===undefined||isVersionInRange(provider.snapshot.packageVersion,identity.packageRange)&&(identity.includePrerelease||!provider.snapshot.packageVersion.split("+",1)[0]!.includes("-"))));
       if(matches.length!==1)throw new RpcError({kind:"not-found",code:matches.length?"rpc.provider-ambiguous":"rpc.provider-not-found",message:matches.length?"Service provider selection is ambiguous.":"No active provider matches the selected service."});return matches[0]!;},
    watch(afterRevision:number,afterSequence:number,options?:RpcCallOptions){
      const captured=options?{...options,...(options.deadline?{deadline:new Date(options.deadline.getTime())}:{})}:undefined;
       return {async *[Symbol.asyncIterator](){const owner=capture();await owner.serviceBindings().discover(target.contractId,captured);const transport=await owner.transport(captured);if(!transport.watch)throw new Error("The RPC transport does not expose its existing provider catalog watch operation.");
        const linked={...captured,signal:captured?.signal?AbortSignal.any([owner.signal,captured.signal]):owner.signal};
        for await(const event of transport.watch(afterRevision,afterSequence,linked)){linked.signal.throwIfAborted();
          if(event.kind==="reset-required")yield Object.freeze({...event,provider:null});
          else if(event.provider&&matchesTarget(event.provider,target))yield Object.freeze({...event,provider:ServiceProvider.from<T>(owner,token,validatedProvider(owner,target,event.provider))});}
      }};
    },
  });
}
/** @internal Both sender and receiver resolve against their own visibility APIs. */
export async function resolveServiceLocator(context:ApiBindingContext,locator:ServiceReferenceLocator,target:ServiceTarget,options?:RpcCallOptions):Promise<ApiProvider>{
   assertServiceTarget(locator,target);const binding=await context.serviceBindings().resolve(locator,options?.signal);
   return validatedProvider(context,target,binding.snapshot);
}
/** @internal Target/schema validation is deliberately outside optional availability handling. */
export async function resolveOptionalServiceLocator(context:ApiBindingContext,locator:ServiceReferenceLocator,target:ServiceTarget,options?:RpcCallOptions):Promise<ApiProvider|null>{
  assertServiceTarget(locator,target);const binding=await context.serviceBindings().resolveOptional(locator,options?.signal);
  return binding?validatedProvider(context,target,binding.snapshot):null;
}
function validatedProvider(context:ApiBindingContext,target:ServiceTarget,provider:ApiProvider):ApiProvider{
  const copy=Object.freeze({...provider});assertServiceTarget(locatorFromProvider(copy),target);
  if(!["active","inactive","faulted"].includes(copy.state))throw new TypeError("Invalid provider state.");
  visibleProviders.set(copy,{context,key:serviceTargetKey(target)});return copy;
}
function requireVisibleProvider(context:ApiBindingContext,token:ServiceToken<unknown>,provider:ApiProvider):void{
  const proof=visibleProviders.get(provider);if(!proof||proof.context!==context||proof.key!==serviceTargetKey(serviceTokenDefinition(token).target))throw new TypeError("Provider must be an existing validated binding from this receiver's visibility API.");
}
export function parseServiceReferenceLocator(input:unknown):ServiceReferenceLocator{
  const value=typeof input==="string"?parseLocatorJson(input):input;
  if(!value||typeof value!=="object"||Array.isArray(value))throw new TypeError("A service reference requires a closed eleven-field locator.");
  const properties=Object.getOwnPropertyDescriptors(value);
  if(Reflect.ownKeys(properties).length!==fields.length||fields.some(key=>!properties[key]||!("value" in properties[key]!)))throw new TypeError("A service reference has missing, extra or non-data fields.");
  const item=value as Record<string,unknown>;
  for(const key of ["packageId","providerId","contractId"] as const)if(typeof item[key]!=="string"||!isPackageId(item[key]))throw new TypeError("Invalid locator identity.");
  for(const key of ["packageVersion","contractVersion"] as const)if(typeof item[key]!=="string"||!isSemanticVersion(item[key]))throw new TypeError("Invalid locator version.");
  if(!hash(item.descriptorSha256)||!hash(item.abiSha256)||typeof item.activationId!=="string"||!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(item.activationId)||/^0{8}(?:-0{4}){3}-0{12}$/u.test(item.activationId)
    ||!exactResourceInteger(value,"activationEpoch",item.activationEpoch)||!exactResourceInteger(value,"sessionGeneration",item.sessionGeneration)
    ||typeof item.endpoint!=="string"||!item.endpoint.trim()||item.endpoint.length>256)throw new TypeError("Invalid locator hash, activation, generation or endpoint.");
  return Object.freeze(Object.fromEntries(fields.map(key=>[key,item[key]]))) as unknown as ServiceReferenceLocator;
}
/** Worker V1 snapshots use N-format GUIDs; ordinary V1 values use canonical UUIDs. */
export function locatorFromProvider(provider:ApiProvider):ServiceReferenceLocator{
  const id=provider.activationId;const activationId=typeof id==="string"&&/^[0-9a-f]{32}$/u.test(id)?`${id.slice(0,8)}-${id.slice(8,12)}-${id.slice(12,16)}-${id.slice(16,20)}-${id.slice(20)}`:id;
  return parseServiceReferenceLocator({packageId:provider.packageId,packageVersion:provider.packageVersion,providerId:provider.providerId,contractId:provider.contractId,contractVersion:provider.contractVersion,
    descriptorSha256:provider.descriptorSha256,abiSha256:provider.abiSha256,activationId,activationEpoch:provider.activationEpoch,sessionGeneration:provider.sessionGeneration,endpoint:provider.endpointReference});
}
export function sameServiceLocator(a:ServiceReferenceLocator,b:ServiceReferenceLocator):boolean{return fields.every(key=>a[key]===b[key]);}
export function assertServiceTarget(locator:ServiceReferenceLocator,target:ServiceTarget):void{if(!matchesTarget(locator,target))throw staleReference();}
function matchesTarget(value:RpcContractIdentity,target:ServiceTarget):boolean{return value.contractId===target.contractId&&value.contractVersion===target.contractVersion&&value.descriptorSha256===target.descriptorSha256&&value.abiSha256===target.abiSha256;}
function hash(value:unknown):value is string{return typeof value==="string"&&/^[0-9a-f]{64}$/u.test(value);}
function staleReference():RpcError{return new RpcError({kind:"stale-endpoint",code:"rpc.stale-endpoint",message:"The exact referenced service activation is not visible or current."});}
function parseLocatorJson(text:string):unknown{
  if(text.length>16_384)throw new TypeError("Locator exceeds its byte bound.");
  // Shallow closed wire object. Preserve exact numeric tokens before Number can round them.
  let offset=0;const result:Record<string,unknown>=Object.create(null) as Record<string,unknown>;const names=new Set<string>();
  const ws=()=>{while(/[ \t\r\n]/u.test(text[offset]??"x"))offset++;};
  const str=():string=>{const start=offset;if(text[offset++]!=='"')throw new TypeError("Expected locator string.");while(offset<text.length){const ch=text[offset++];if(ch==='\\')offset++;else if(ch==='"')return JSON.parse(text.slice(start,offset)) as string;}throw new TypeError("Unterminated locator string.");};
  ws();if(text[offset++]!=="{")throw new TypeError("Expected locator object.");ws();
  while(text[offset]!=="}"){const key=str();if(names.has(key.toLowerCase()))throw new TypeError("Duplicate locator member.");names.add(key.toLowerCase());ws();if(text[offset++]!==":")throw new TypeError("Expected locator colon.");ws();
    if(text[offset]==='"')result[key]=str();else{const match=/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/u.exec(text.slice(offset));if(!match)throw new TypeError("Invalid locator value.");offset+=match[0].length;result[key]=Number(match[0]);recordResourceIntegerToken(result,key,match[0]);}
    ws();if(text[offset]==="}")break;if(text[offset++]!==",")throw new TypeError("Invalid locator separator.");ws();if(text[offset]==="}")throw new TypeError("Trailing locator comma.");}
  offset++;ws();if(offset!==text.length)throw new TypeError("Trailing locator input.");return result;
}
