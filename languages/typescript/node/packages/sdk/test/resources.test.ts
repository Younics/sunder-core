import assert from "node:assert/strict";
import test from "node:test";
import { createApiContext, createApiToken, bindApiContext, getApiBindingContext, type ApiBindingContext, type ApiProvider, type ApiTransport } from "../src/api";
import { SunderContent } from "../src/content";
import { RpcMarshallingContext, type ResourceContentAccess } from "../src/resources";
import { ServiceReference, createServiceCatalog, createServiceToken, locatorFromProvider, parseServiceReferenceLocator, referenceState, type ServiceToken } from "../src/service-reference";
import {RpcError} from "../src/types";
import {staleServiceBinding} from "../src/service-bindings";
import { FrameDecoder } from "../src/framing";
import { sha256Hex } from "../src/sha256";
import type { JsonValue, RpcCallOptions } from "../src/core";
import type { RpcContentReference, RpcInvocationContext } from "../src/types";
import { contentFromFile } from "../src/content-node";
import { mkdtemp,readFile,writeFile,rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createBrowserContext } from "../src/browser";
import {liveWatch} from "./service-binding-fixture";

const identity={packageId:"example.provider",contractId:"example.service",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64)};
const snapshot:ApiProvider={...identity,packageVersion:"1.2.3",providerId:"example.provider.main",activationId:"11223344556677889900aabbccddeeff",activationEpoch:3,sessionGeneration:5,endpointReference:"opaque-endpoint",state:"active",catalogRevision:1};
const empty=async()=>{};
const deferred=<T>()=>{let resolve!:(value:T)=>void;const promise=new Promise<T>(r=>resolve=r);return {promise,resolve};};
async function read(content:SunderContent,signal?:AbortSignal):Promise<Uint8Array>{
  const stream=await content.openRead(signal);const reader=stream.getReader();const chunks:Uint8Array[]=[];
  try{while(true){const next=await reader.read();if(next.done)break;chunks.push(next.value);}}finally{await reader.cancel();reader.releaseLock();}
  const result=new Uint8Array(chunks.reduce((sum,chunk)=>sum+chunk.length,0));let offset=0;for(const chunk of chunks){result.set(chunk,offset);offset+=chunk.length;}return result;
}
function fixture(){
  const bytes=new Uint8Array([1,2,3]);let next=0;let current:ApiProvider[]=[{...snapshot}];
  const bodies=new Map<string,Uint8Array>();const registrations:Array<{target:string;wire:RpcContentReference}>=[];
  const scopes:Array<{closed:number;closeCalls:number;discarded:number;discardCalls:number;controller:AbortController;closedEvent:ReturnType<typeof deferred<void>>;deadline:Date}>=[];
  const wire=(data=bytes):RpcContentReference=>{const id="content-"+(++next);bodies.set(id,data.slice());return {id,length:data.length,sha256:sha256Hex(data),mediaType:"application/octet-stream",fileName:"data.bin",expiresAtUtc:"2099-01-01T00:00:00Z",repeatability:"single-use"};};
  let reply:JsonValue={body:wire()} as unknown as JsonValue;let deadline=Date.now()+60_000;let blockReads=false;
  const transport:ApiTransport={async discover(contractId){return {revision:1,sequence:1,resetRequired:false,providers:current.filter(value=>value.contractId===contractId)};},
    watch:liveWatch,
    async invoke(){return reply as never;},async *subscribe(){yield reply as never;yield {body:wire()} as never;},
    async createCallScope(){
      const state={closed:0,closeCalls:0,discarded:0,discardCalls:0,controller:new AbortController(),closedEvent:deferred<void>(),deadline:new Date(deadline)};scopes.push(state);
      const content:ResourceContentAccess={async register(target,source,options){const chunks:Uint8Array[]=[];for await(const chunk of source)chunks.push(chunk);
        const data=new Uint8Array(chunks.reduce((n,x)=>n+x.length,0));let offset=0;for(const chunk of chunks){data.set(chunk,offset);offset+=chunk.length;}
        const value={...wire(data),mediaType:options.mediaType,fileName:options.fileName};registrations.push({target,wire:value});assert.equal(options.repeatability,undefined);return value;},
        async open(reference,options){state.controller.signal.throwIfAborted();options?.signal?.throwIfAborted();let discarded=false;const released=deferred<void>();
          return {stream:{async *[Symbol.asyncIterator](){state.controller.signal.throwIfAborted();options?.signal?.throwIfAborted();yield bodies.get(reference.id)!.slice();if(blockReads)await released.promise;options?.signal?.throwIfAborted();}},async discard(){state.discardCalls++;if(!discarded){discarded=true;state.discarded++;released.resolve();}}};}};
      return {discover:transport.discover,invoke:transport.invoke,subscribe:transport.subscribe,content,signal:state.controller.signal,deadline:state.deadline,
        async close(){state.closeCalls++;if(state.closed)return;state.closed++;state.controller.abort(new Error("Host scope closed"));state.closedEvent.resolve();}};
    }};
  return {transport,scopes,registrations,wire,bodies,bytes,setProviders(value:ApiProvider[]){current=value;},setReply(value:JsonValue){reply=value;},setDeadline(value:number){deadline=value;},blockReads(){blockReads=true;}};
}
function contentApi(f:ReturnType<typeof fixture>){
  const token=createApiToken("example.owner",context=>({
    call:(input?:SunderContent,options?:RpcCallOptions)=>context.unary({...identity,resourceContent:true},async(transport,target,linked,resources)=>{
      const value=input?{one:input,two:input}:{};
      const request=await resources.serialize(value,async(ctx,item)=>{if(item.one){await ctx.registerContent(item.one);await ctx.registerContent(item.two);}},(item,ctx):JsonValue=>item.one?{one:ctx.contentToWire(item.one),two:ctx.contentToWire(item.two!)}:{});
      const reply=await transport.invoke(target,"service","call",request,linked);
      return resources.deserialize(reply,empty,(value,ctx)=>{const body=(value as {body:JsonValue}).body;return {one:ctx.contentFromWire(body),two:ctx.contentFromWire(body)};});
    },options),
    stream:(options?:RpcCallOptions)=>context.stream({...identity,resourceContent:true},(transport,target,linked,resources)=>({async *[Symbol.asyncIterator](){
      for await(const value of transport.subscribe(target,"service","stream",{},linked))yield await resources.deserialize(value,empty,(item,ctx)=>ctx.contentFromWire((item as {body:JsonValue}).body));
    }}),options),
    invalid:()=>context.unary({...identity,resourceContent:true},async(transport,target,linked,resources)=>{
      const wire=await transport.invoke(target,"service","bad",{},linked);return resources.deserialize(wire,empty,(value,ctx)=>{ctx.contentFromWire((value as {body:JsonValue}).body);throw new Error("late decode failure");});
    }),
  }));
  const owner=createApiContext("example.owner",f.transport);return {owner,api:owner.api(token),token};
}

test("portable local content copies bytes, is repeatable, and owns streams including never-opened inputs",async()=>{
  const bytes=new Uint8Array([1,2,3]);const content=SunderContent.fromBytes(bytes,{fileName:"hello.bin"});bytes.fill(9);
  const unnamed=SunderContent.fromBytes(bytes);assert.equal(unnamed.fileName,"content.bin");await unnamed.dispose();
  assert.equal(content.sha256,sha256Hex(new Uint8Array([1,2,3])));assert.equal(content.isRepeatable,true);
  const first=await read(content);first.fill(8);assert.deepEqual(await read(content),new Uint8Array([1,2,3]));
  assert.throws(()=>JSON.stringify(content),/generated service marshalling/);await content.dispose();await assert.rejects(read(content),/disposed/);
  let closed=0;const source=new ReadableStream<Uint8Array>({cancel(){closed++;}});const streamContent=SunderContent.fromStream(source);assert.equal(streamContent.fileName,"content.bin");await streamContent.dispose();assert.equal(closed,1);
  const borrowed=new ReadableStream<Uint8Array>({cancel(){closed++;}});await SunderContent.fromStream(borrowed,{leaveOpen:true}).dispose();assert.equal(closed,1);await borrowed.cancel();assert.equal(closed,2);
  let disposed=0;const iterable={async *[Symbol.asyncIterator](){yield new Uint8Array([1]);},async [Symbol.asyncDispose](){disposed++;}};
  await SunderContent.fromStream(iterable).dispose();assert.equal(disposed,1);
});
test("received logical content preserves exact expiry metadata and rejects Worker-only spellings",async()=>{
  const f=fixture();const {owner,api}=contentApi(f);
  try{
    for(const expiresAtUtc of ["2099-01-01T00:00:00Z","2099-01-01T00:00:00.123456789Z","2099-01-01T00:00:00.123456788Z"]){
      const reference={...f.wire(),fileName:"",expiresAtUtc};f.setReply({body:reference});
      const result=await api.call();assert.equal(result.one.expiresAtUtc,expiresAtUtc);assert.equal(result.one.fileName,"");assert.equal(result.one.sha256,reference.sha256);
      await result.one.dispose();
    }
    for(const expiresAtUtc of ["2099-01-01T00:00:00.0000000Z","2099-01-01T00:00:00.1234000Z","2099-01-01T00:00:00+00:00","2099-01-01T00:00:00.1234567890Z"]){
      f.setReply({body:{...f.wire(),expiresAtUtc}});await assert.rejects(api.call(),/canonical UTC/);
    }
  }finally{await owner.dispose();}
});

test("Node file content is lazy, repeatable and never deletes the source file",async()=>{
  const root=await mkdtemp(join(tmpdir(),"sunder-content-file-"));try{
    const path=join(root,"source.bin");const content=contentFromFile(path);await writeFile(path,new Uint8Array([4,5,6]));
    assert.equal(content.fileName,"source.bin");assert.equal(content.isRepeatable,true);assert.deepEqual(await read(content),new Uint8Array([4,5,6]));assert.deepEqual(await read(content),new Uint8Array([4,5,6]));
    await content.dispose();assert.deepEqual(await readFile(path),Buffer.from([4,5,6]));
  }finally{await rm(root,{recursive:true,force:true});}
});
test("unary content registration coalesces aliases and retains only outstanding scope leases",async()=>{
  const f=fixture();const {api,owner}=contentApi(f);const local=SunderContent.fromBytes(f.bytes);
  const result=await api.call(local);assert.equal(f.registrations.length,1);assert.equal(f.registrations[0]!.target,snapshot.endpointReference);
  assert.equal(result.one,result.two);assert.equal(f.scopes[0]!.closed,0);assert.deepEqual(await read(result.one),f.bytes);assert.equal(f.scopes[0]!.discarded,1);
  await result.one.dispose();assert.equal(f.scopes[0]!.closed,1);assert.deepEqual(await read(local),f.bytes,"Sending does not dispose author content.");
  const second=await api.call();assert.equal(f.scopes[1]!.closed,0);await owner.disposeAsync();assert.equal(f.scopes[1]!.closed,1);await assert.rejects(read(second.one));await local.dispose();
});
for(const early of [false,true])test(`stream ${early?"early exit":"completion"} retains both original cancellation links through its last content lease`,async t=>{
  for(const termination of ["caller","enumeration","enumeration-only","last-content","owner","both-and-owner"] as const)await t.test(termination,async()=>{
    const f=fixture();const {api,owner}=contentApi(f);const caller=new AbortController(),enumeration=new AbortController();const items:SunderContent[]=[];
    try{
      const stream=api.stream(termination==="enumeration-only"?undefined:{signal:caller.signal});
      // Exercise both iterator-token and ordinary for-await fluent acquisition.
      const iteration=termination==="enumeration"?{[Symbol.asyncIterator]:()=>stream[Symbol.asyncIterator](enumeration.signal)}:stream.withCancellation(enumeration.signal);
      assert.equal(f.scopes.length,0);
      for await(const item of iteration){items.push(item);if(early)break;}
      const scope=f.scopes[0]!;assert.equal(scope.closed,0);
      f.blockReads();const reader=(await items[0]!.openRead()).getReader();assert.deepEqual((await reader.read()).value,f.bytes);
      const pending=reader.read();void pending.catch(()=>{});
      let shutdown:Promise<void>|undefined;
      if(termination==="caller"||termination==="both-and-owner")caller.abort(new Error("original caller retired"));
      if(termination==="enumeration"||termination==="enumeration-only"||termination==="both-and-owner")enumeration.abort(new Error("original enumerator retired"));
      if(termination==="owner"||termination==="both-and-owner")shutdown=owner.disposeAsync();
      if(termination==="last-content")for(const [index,item] of items.entries()){
        await item.dispose();assert.equal(scope.closed,index===items.length-1?1:0);
      }
      await scope.closedEvent.promise;await shutdown;await assert.rejects(pending);reader.releaseLock();
      for(const item of items)await assert.rejects(item.openRead());
      await owner.disposeAsync();assert.equal(scope.closeCalls,1);assert.equal(scope.discardCalls,1);
    }finally{await owner.disposeAsync();}
  });
});
test("Host-clamped deadlines and DI retirement revoke retained responses after unary completion",async t=>{
  t.mock.timers.enable({apis:["Date","setTimeout"],now:1_800_000_000_000});
  const f=fixture();f.setDeadline(Date.now()+100);const {api,owner}=contentApi(f);const result=await api.call({} as never).catch(()=>undefined);
  // Invalid unregistered resources do not dispatch or retain a scope.
  assert.equal(result,undefined);assert.equal(f.scopes[0]!.closed,1);
  f.setDeadline(Date.now()+50);const response=await api.call();t.mock.timers.tick(51);await f.scopes[1]!.closedEvent.promise;await assert.rejects(read(response.one));await owner.disposeAsync();
});
test("failed decode rolls back already-created content resources",async()=>{
  const f=fixture();const {api,owner}=contentApi(f);await assert.rejects(api.invalid(),/late decode failure/);assert.equal(f.scopes[0]!.closed,1);await owner.disposeAsync();
});
test("browser owners use portable existing content scopes and revoke retained values at document retirement",async()=>{
  const f=fixture();f.setProviders([{...snapshot,providerHandle:"browser-handle"}]);const prepared=contentApi(f);await prepared.owner.disposeAsync();
  const host=new EventTarget();Object.defineProperty(host,"sunder",{value:Object.freeze({version:1,protocol:"sunder.bridge.v1",signal:new AbortController().signal,rpc:f.transport}),writable:false,configurable:false});
  const owner=createBrowserContext(prepared.token,host);const local=SunderContent.fromBytes(f.bytes);const result=await owner.api(prepared.token).call(local);
  assert.equal(f.registrations[0]!.target,"browser-handle");assert.deepEqual(await read(result.one),f.bytes);assert.equal(f.scopes[0]!.closed,0);
  host.dispatchEvent(new Event("pagehide"));await f.scopes[0]!.closedEvent.promise;await assert.rejects(read(result.one));await owner.disposeAsync();await local.dispose();
});
test("invocation resources are borrowed, forwarding registers new authority, and completed handlers cannot extend reads",async()=>{
  const f=fixture();const scope=await f.transport.createCallScope!();const wire=f.wire();let registrations=0;
  const invocation={signal:new AbortController().signal,content:{register:async(source:AsyncIterable<Uint8Array>,options:any,call?:RpcCallOptions)=>{registrations++;return scope.content!.register("provider-response",source,options,call);},open:scope.content!.open}} as unknown as RpcInvocationContext;
  const incoming=RpcMarshallingContext.forInvocation(invocation);const content=await incoming.deserialize(wire,empty,(value,ctx)=>ctx.contentFromWire(value));
  const sent=await incoming.serialize({content,alias:content},async(ctx,value)=>{await ctx.registerContent(value.content);await ctx.registerContent(value.alias);},(value,ctx)=>({content:ctx.contentToWire(value.content),alias:ctx.contentToWire(value.alias)}));
  assert.equal(registrations,1);assert.notEqual((sent as any).content.id,wire.id);assert.deepEqual((sent as any).content,(sent as any).alias);
  await incoming.dispose();await assert.rejects(read(content));assert.equal(f.scopes[0]!.closed,0,"Borrowed invocation context does not close Host authority.");await scope.close();
});
test("disposal drains an in-flight open and discards its late result",async()=>{
  const f=fixture();const owner=createApiContext("example.owner",f.transport);const binding=getApiBindingContext(owner);
  const started=deferred<void>(),opened=deferred<{stream:AsyncIterable<Uint8Array>;discard():Promise<void>}>();let discarded=0,closed=0;
  const ctx=RpcMarshallingContext.forCall(binding,{register:async()=>{throw Error();},open:async()=>{started.resolve();return opened.promise;}},"endpoint",owner.signal,async()=>{closed++;},()=>{});
  const content=await ctx.deserialize(f.wire(),empty,(value,context)=>context.contentFromWire(value));await ctx.dispose();
  const readPromise=content.openRead();void readPromise.catch(()=>{});await started.promise;let disposed=false;const disposal=content.dispose().then(()=>{disposed=true;});await Promise.resolve();assert.equal(disposed,false);
  opened.resolve({stream:{async *[Symbol.asyncIterator](){yield f.bytes;}},async discard(){discarded++;}});
  await disposal;await assert.rejects(readPromise);assert.equal(discarded,1);assert.equal(closed,1);await owner.disposeAsync();
});

function referenceOwner(f:ReturnType<typeof fixture>,side:string){
  const target={...identity,serviceId:"service"};
  const service=createServiceToken(target,(context:ApiBindingContext)=>({side,call:(options?:RpcCallOptions)=>context.unary(identity,(rpc,endpoint,linked)=>rpc.invoke(endpoint,"service","call",{},linked),options)}));
  const token=createApiToken("example."+side,()=>({}),"generation",[service]);
  const owner=createApiContext("example."+side,f.transport);bindApiContext(owner,token);
  return {owner,binding:getApiBindingContext(owner),service,catalog:createServiceCatalog<typeof service>(getApiBindingContext(owner),service,identity)};
}
test("optional reference lookup is narrow, fresh, explicit-null and never hides caller cancellation",async()=>{
  const f=fixture(),source=referenceOwner(f,"optional");const ref=(await source.catalog.resolve()).Reference;
  const context=RpcMarshallingContext.forCall(source.binding,undefined,"endpoint",source.owner.signal,empty,()=>{});
  const send=()=>context.serialize(ref,(ctx,value)=>ctx.registerOptionalServiceReference(value,identity.contractId,"service"),(value,ctx)=>ctx.optionalServiceReferenceToWire(value,identity.contractId,"service"));
  const wire=locatorFromProvider(snapshot) as unknown as JsonValue;
  const receive=()=>context.deserialize(wire,(ctx,value)=>ctx.prepareOptionalServiceReference(value,identity.contractId,"service"),(value,ctx)=>ctx.optionalServiceReferenceFromWire(value,identity.contractId,"service"));
  try{
    f.transport.getProvider=async()=>null;assert.equal(await send(),null);assert.equal(await receive(),null);assert.equal(context.signal.aborted,false);
    for(const kind of ["unavailable","stale-endpoint","permission-denied","validation","protocol","deadline-exceeded","provider-faulted","cancelled"] as const){
      for(const unsafe of [false,true]){const error=new RpcError({kind,code:"rpc.optional.test",message:kind,dispatchState:unsafe?"possibly-dispatched":"not-dispatched",retrySafety:unsafe?"unsafe":"safe"});
        f.transport.getProvider=async()=>{throw error;};
        if(!unsafe&&(kind==="unavailable"||kind==="stale-endpoint")){assert.equal(await send(),null);assert.equal(await receive(),null);}
        else {await assert.rejects(send(),value=>value===error);await assert.rejects(receive(),value=>value===error);}
      }
    }
    f.transport.getProvider=async()=>snapshot;assert.deepEqual(await send(),wire);
    const caller=new AbortController();const cancelled=new Error("actual caller cancellation");caller.abort(cancelled);
    const cancelledContext=RpcMarshallingContext.forInvocation({signal:caller.signal} as RpcInvocationContext,source.binding);
    await assert.rejects(cancelledContext.deserialize(wire,(ctx,value)=>ctx.prepareOptionalServiceReference(value,identity.contractId,"service"),()=>null),error=>error===cancelled);
    await cancelledContext.dispose();
  }finally{await context.dispose();await source.owner.disposeAsync();}
});
test("optional read/write races become null; mixed strict occurrences of the same locator win in either order",async()=>{
  for(const strict of [false,true])for(const strictFirst of [false,true]){
    const f=fixture(),source=referenceOwner(f,"source"),other=referenceOwner(f,"other");
    const ref=(await source.catalog.resolve()).Reference,alias=(await other.catalog.resolve()).Reference;assert.notEqual(ref,alias);
    const context=RpcMarshallingContext.forCall(source.binding,undefined,"endpoint",source.owner.signal,empty,()=>{});
    try{
      const operation=context.serialize({ref,alias},async(ctx,value)=>{
        if(strict&&strictFirst)await ctx.registerServiceReference(value.alias,identity.contractId,"service");
        await ctx.registerOptionalServiceReference(value.ref,identity.contractId,"service");
        if(strict&&!strictFirst)await ctx.registerServiceReference(value.alias,identity.contractId,"service");
        await referenceState(value.ref).binding.retire(staleServiceBinding());
      },(value,ctx)=>({optional:ctx.optionalServiceReferenceToWire(value.ref,identity.contractId,"service")}));
      if(strict)await assert.rejects(operation);else assert.deepEqual(await operation,{optional:null});
      const incoming=RpcMarshallingContext.forInvocation({signal:new AbortController().signal} as RpcInvocationContext,other.binding);
      try{
        const wire=locatorFromProvider(snapshot) as unknown as JsonValue;
        const decoded=incoming.deserialize(wire,async(ctx,value)=>{
          if(strict&&strictFirst)await ctx.prepareServiceReference(JSON.parse(JSON.stringify(value)) as JsonValue,identity.contractId,"service");
          await ctx.prepareOptionalServiceReference(value,identity.contractId,"service");
          if(strict&&!strictFirst)await ctx.prepareServiceReference(JSON.parse(JSON.stringify(value)) as JsonValue,identity.contractId,"service");
          await referenceState(alias).binding.retire(staleServiceBinding());
        },(value,ctx)=>ctx.optionalServiceReferenceFromWire(value,identity.contractId,"service"));
        if(strict)await assert.rejects(decoded);else assert.equal(await decoded,null);
        assert.equal(incoming.signal.aborted,strict);
      }finally{await incoming.dispose();}
    }finally{await context.dispose();await source.owner.disposeAsync();await other.owner.disposeAsync();}
  }
});
test("optional decoded reference retirement cancels only its nested call and preserves outer fallback lifetime",async()=>{
  const f=fixture(),receiver=referenceOwner(f,"receiver");const context=RpcMarshallingContext.forInvocation({signal:new AbortController().signal} as RpcInvocationContext,receiver.binding);
  const started=deferred<void>();let callSignal:AbortSignal|undefined;
  f.transport.invoke=async(_target,_service,_method,_request,options)=>{callSignal=options!.signal;started.resolve();return await new Promise((_,reject)=>{callSignal!.addEventListener("abort",()=>reject(new RpcError({kind:"cancelled",code:"rpc.call.cancelled",message:"transport cancelled"})),{once:true});});};
  try{
    const reference=await context.deserialize(locatorFromProvider(snapshot) as unknown as JsonValue,(ctx,value)=>ctx.prepareOptionalServiceReference(value,identity.contractId,"service"),(value,ctx)=>ctx.optionalServiceReferenceFromWire<typeof receiver.service>(value,identity.contractId,"service"));assert.ok(reference);
    const call=reference.Service.call();void call.catch(()=>{});await started.promise;await referenceState(reference).binding.retire(staleServiceBinding());
    await assert.rejects(call,error=>error instanceof RpcError&&error.error.kind==="stale-endpoint"&&error.error.dispatchState==="possibly-dispatched"&&error.error.retrySafety==="unsafe");
    assert.equal(callSignal!.aborted,true);assert.equal(context.signal.aborted,false);assert.equal(receiver.owner.signal.aborted,false);
    assert.equal(await context.serialize("fallback",empty,value=>value),"fallback");assert.throws(()=>reference.Service,/retired/);
  }finally{await context.dispose();await receiver.owner.disposeAsync();}
});
test("optional reference lookup uses receiver-proven browser handles rather than portable endpoints",async()=>{
  const f=fixture();const visible={...snapshot,providerHandle:"receiver-only-handle"};f.setProviders([visible]);let lookups=0;
  f.transport.getProvider=async target=>{assert.equal(target,visible.providerHandle);lookups++;return visible;};
  const receiver=referenceOwner(f,"browser");const resources=RpcMarshallingContext.forInvocation({signal:new AbortController().signal} as RpcInvocationContext,receiver.binding);
  try{
    const ref=await resources.deserialize(locatorFromProvider(snapshot) as unknown as JsonValue,(ctx,value)=>ctx.prepareOptionalServiceReference(value,identity.contractId,"service"),(value,ctx)=>ctx.optionalServiceReferenceFromWire<typeof receiver.service>(value,identity.contractId,"service"));
    assert.ok(ref);await ref.Service.call();assert.ok(lookups>=2);assert.equal(resources.signal.aborted,false);
  }finally{await resources.dispose();await receiver.owner.disposeAsync();}
});
test("typed references roundtrip the complete locator, use the receiver proxy and outlive only the decoding operation",async()=>{
  const f=fixture();const sender=referenceOwner(f,"sender"),receiver=referenceOwner(f,"receiver");
  const provider=await sender.catalog.resolve();const reference=provider.Reference;assert.equal(reference.Service.side,"sender");assert.equal(provider.Reference,reference);
  assert.throws(()=>JSON.stringify(reference),/generated service marshalling/);assert.throws(()=>JSON.stringify(provider),/generated service marshalling/);
  const outbound=RpcMarshallingContext.forCall(sender.binding,undefined,"endpoint",sender.owner.signal,empty,()=>{});
  const wire=await outbound.serialize(reference,(ctx,value)=>ctx.registerServiceReference(value,identity.contractId,"service"),(value,ctx)=>ctx.serviceReferenceToWire(value,identity.contractId,"service"));await outbound.dispose();
  assert.deepEqual(Object.keys(wire as object),["packageId","packageVersion","providerId","contractId","contractVersion","descriptorSha256","abiSha256","activationId","activationEpoch","sessionGeneration","endpoint"]);
  assert.equal((wire as any).activationId,"11223344-5566-7788-9900-aabbccddeeff");assert.equal("Service" in (wire as object),false);
  let closed=0;const incoming=RpcMarshallingContext.forCall(receiver.binding,undefined,"endpoint",receiver.owner.signal,async()=>{closed++;},()=>{});
  const received=await incoming.deserialize(wire,(ctx,value)=>ctx.prepareServiceReference(value,identity.contractId,"service"),(value,ctx)=>ctx.serviceReferenceFromWire<typeof receiver.service>(value,identity.contractId,"service"));await incoming.dispose();
  assert.equal(closed,1);assert.equal(received.Service.side,"receiver");await received.Service.call();assert.equal(f.scopes[0]!.closed,1);
  await receiver.owner.disposeAsync();assert.throws(()=>received.Service,/retired/);await sender.owner.disposeAsync();
});
test("typed catalogs retain exact get/watch identities and never select replacement activations",async()=>{
  const f=fixture();const observing=f.transport.watch!;f.transport.watch=function(revision,sequence,options){if(revision!==3)return observing(revision,sequence,options);return (async function*(){assert.equal(revision,3);assert.equal(sequence,4);
    yield {revision:4,sequence:5,kind:"activated",provider:{...snapshot}};
    yield {revision:5,sequence:6,kind:"faulted",provider:{...snapshot,state:"faulted"}};
    yield {revision:6,sequence:7,kind:"reset-required",provider:null};})();};
  const owner=referenceOwner(f,"catalog");const provider=await owner.catalog.resolve();assert.ok(await owner.catalog.get(provider));
  f.setProviders([{...snapshot,sessionGeneration:6}]);assert.equal(await owner.catalog.get(provider),null);
  const events:Array<{provider:{readonly Reference:unknown}|null}>=[];for await(const event of owner.catalog.watch(3,4))events.push(event);
  assert.equal(events.length,3);assert.ok(events[0]!.provider!.Reference);assert.equal((events[1]!.provider!.Reference as ServiceReference<unknown>).retirementSignal.aborted,true);assert.equal(events[2]!.provider,null);
  await owner.owner.disposeAsync();
});
test("references reject every changed locator field, both generation directions, invisible/inactive providers, unprepared values and callbacks",async()=>{
  const f=fixture();const source=referenceOwner(f,"source");const ref=(await source.catalog.resolve()).Reference;const locator=locatorFromProvider(snapshot);
  assert.throws(()=>new (ServiceReference as any)({locator,context:source.binding,service:{call(){}}}),/generated provider catalog/);
  assert.throws(()=>ServiceReference.bind(source.binding,source.service,snapshot),/validated binding/);
  const mutations:Partial<ApiProvider>[]=[{packageId:"example.other"},{packageVersion:"1.2.4"},{providerId:"example.other"},{contractId:"example.other"},{contractVersion:"2.0.0"},
    {descriptorSha256:"c".repeat(64)},{abiSha256:"c".repeat(64)},{activationId:"21223344556677889900aabbccddeeff"},{activationEpoch:4},{sessionGeneration:4},{sessionGeneration:6},{endpointReference:"replacement"},{state:"inactive"}];
  for(const change of [...mutations,null]){
    f.setProviders(change?[{...snapshot,...change}]:[]);
    const ctx=RpcMarshallingContext.forCall(source.binding,undefined,"endpoint",source.owner.signal,empty,()=>{});
    await assert.rejects(ctx.deserialize(locator as unknown as JsonValue,(context,value)=>context.prepareServiceReference(value,identity.contractId,"service"),(value,context)=>context.serviceReferenceFromWire(value,identity.contractId,"service")),/referenced service|current|visible/);
    await assert.rejects(ctx.serialize(ref,(context,value)=>context.registerServiceReference(value,identity.contractId,"service"),(value,context)=>context.serviceReferenceToWire(value,identity.contractId,"service")),/referenced service|current|visible/);await ctx.dispose();
  }
  f.setProviders([{...snapshot}]);const ctx=RpcMarshallingContext.forCall(source.binding,undefined,"endpoint",source.owner.signal,empty,()=>{});
  await assert.rejects(ctx.deserialize(locator as unknown as JsonValue,empty,(value,context)=>context.serviceReferenceFromWire(value,identity.contractId,"service")),/preparation/);
  await assert.rejects(ctx.serialize({Service(){return "callback";}},(context,value)=>context.registerServiceReference(value,identity.contractId,"service"),()=>null),/existing generated provider/);
  await ctx.dispose();await source.owner.disposeAsync();
});
test("locator parsing rejects malformed/rounded JSON while accepting mathematical safe integer spellings",()=>{
  const locator=locatorFromProvider(snapshot);const text=JSON.stringify(locator);
  assert.equal(parseServiceReferenceLocator(text.replace('"sessionGeneration":5','"sessionGeneration":5.0e0')).sessionGeneration,5);
  for(const value of ["5.00000000000000001","9007199254740991.1","-1","null","true","9007199254740992"])
    assert.throws(()=>parseServiceReferenceLocator(text.replace('"sessionGeneration":5',`"sessionGeneration":${value}`)));
  for(const key of Object.keys(locator)){const value={...locator} as any;delete value[key];assert.throws(()=>parseServiceReferenceLocator(value));}
  assert.throws(()=>parseServiceReferenceLocator({...locator,Service:{}}));assert.throws(()=>parseServiceReferenceLocator(text.replace('"endpoint":','"Endpoint":"duplicate","endpoint":')));
  const body=Buffer.from('{"type":"host.result","id":"1","value":'+text.replace('"sessionGeneration":5','"sessionGeneration":5.00000000000000001')+'}');
  const frames=new FrameDecoder().push(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`),body]));
  assert.throws(()=>parseServiceReferenceLocator((frames[0] as any).value));
});
