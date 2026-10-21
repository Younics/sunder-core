import assert from "node:assert/strict";
import test from "node:test";
import {bindApiContext,createApiContext,createApiToken,getApiBindingContext,type ApiBindingContext} from "../src/api";
import {createServiceCatalog,createServiceToken} from "../src/service-reference";
import {RpcError} from "../src/types";
import type {RpcInvocationContext} from "../src/types";
import {RpcMarshallingContext} from "../src/resources";
import {locatorFromProvider} from "../src/service-reference";
import {bindingFixture,provider,until} from "./service-binding-fixture";

function owner(f:ReturnType<typeof bindingFixture>){
  const identity={packageId:provider.packageId,contractId:provider.contractId,contractVersion:provider.contractVersion,descriptorSha256:provider.descriptorSha256,abiSha256:provider.abiSha256};
  const token=createServiceToken({...identity,serviceId:"service"},(context:ApiBindingContext)=>({run:()=>context.unary(identity,(rpc,target,options)=>rpc.invoke(target,"service","run",{},options))}));
  const api=createApiContext("example.owner",f.transport);const self=createApiToken("example.owner",()=>({}),"generation",[token]);bindApiContext(api,self);
  return {api,token,context:getApiBindingContext(api),catalog:createServiceCatalog<typeof token>(getApiBindingContext(api),token,identity)};
}
test("one dormant receiver observer uses an atomic cursor, resnapshots startup and reuses exact typed entries",async()=>{
  const f=bindingFixture(),o=owner(f);assert.equal(f.discovers,0);assert.equal(f.watches.length,0);
  try{const first=await o.catalog.resolve(),second=await o.catalog.resolve();assert.equal(first,second);assert.equal(first.Reference,second.Reference);assert.equal(first.Service,second.Service);
    assert.equal(f.watches.length,1);assert.deepEqual([f.watches[0]!.revision,f.watches[0]!.sequence],[8,19]);assert.ok(f.discovers>=3);await first.ensureCurrent();
  }finally{await o.api.disposeAsync();}
  const race=bindingFixture();race.onWatch(()=>race.setProviders([]));const changed=owner(race);
  try{await assert.rejects(changed.catalog.resolve(),/No active/);assert.equal(race.watches.length,1);}finally{await changed.api.disposeAsync();}
});
test("withdrawal and either generation change permanently retire old references and already acquired proxies",async t=>{
  for(const change of ["removed","deactivated","faulted","generation-up","generation-down"])await t.test(change,async()=>{
    const f=bindingFixture(),o=owner(f);try{const entry=await o.catalog.resolve(),ref=entry.Reference,proxy=ref.Service;
      ref.retirementSignal.addEventListener("abort",()=>{throw new Error("business callback");});
      ref.retirementSignal.onabort=()=>{throw new Error("business onabort callback");};
      f.emit(change.startsWith("generation")?"activated":change,{...provider,sessionGeneration:change==="generation-up"?2:change==="generation-down"?0:1});
      await until(()=>ref.retirementSignal.aborted);assert.equal(entry.Reference,ref);assert.throws(()=>ref.Service);await assert.rejects(proxy.run());assert.equal(f.invokes,0);
      await assert.rejects(ref.ensureCurrent());await o.api.disposeAsync();assert.equal(entry.Reference,ref);assert.equal(entry.retirementSignal.aborted,true);
    }finally{await o.api.disposeAsync();}
  });
});
test("EOF, reset and malformed observations retire bindings before recovery even for identical recovered locator bytes",async t=>{
  for(const mode of ["eof","reset","malformed"])await t.test(mode,async()=>{
    const f=bindingFixture(),o=owner(f);try{const old=await o.catalog.resolve();
      if(mode==="eof")f.watches[0]!.end();else if(mode==="reset")f.emit("reset-required",null);else f.watches[0]!.emit({revision:8,sequence:19,kind:"added",provider});
      await until(()=>old.retirementSignal.aborted);assert.equal(f.watches.length,1);await until(()=>f.watches.length===2);await new Promise(resolve=>setTimeout(resolve,5));
      const fresh=await o.catalog.resolve();assert.notEqual(fresh,old);assert.equal(fresh.retirementSignal.aborted,false);assert.throws(()=>old.Service);
    }finally{await o.api.disposeAsync();}
  });
});
test("EnsureCurrent and invariant reports retain structured broker failures and report only an exact live activation",async()=>{
  for(const kind of ["permission-denied","unavailable"] as const){const f=bindingFixture(),o=owner(f);try{const other={...provider,contractId:"example.other",endpointReference:"other",providerId:"example.other"};f.setProviders([{...provider},other]);
    const entry=await o.catalog.resolve();await o.context.serviceBindings().discover(other.contractId);const sibling=o.context.serviceBindings().track(other);
    const error=new RpcError({kind,code:"rpc.test",message:"denied",dispatchState:"possibly-dispatched",retrySafety:"unsafe"},true);
    f.setGetFailure(error);await assert.rejects(entry.ensureCurrent(),value=>value===error);assert.equal(entry.retirementSignal.aborted,true);assert.equal(sibling.signal.aborted,false,"An exact-target query failure must not revoke an unrelated outer-call binding; watch disconnect/reset owns global retirement.");
  }finally{await o.api.disposeAsync();}}
  const f=bindingFixture(),o=owner(f);try{const first=await o.catalog.resolve();f.setProviders([{...provider,sessionGeneration:2}]);assert.equal(await first.tryReportInvariantViolation(new Error("stale")),false);assert.equal(f.reports,0);
    f.setProviders([{...provider}]);const fresh=await o.catalog.resolve();const error=new RpcError({kind:"permission-denied",code:"rpc.report.denied",message:"report denied",dispatchState:"possibly-dispatched",retrySafety:"unsafe"},true);
    f.setReportFailure(error);await assert.rejects(fresh.tryReportInvariantViolation(new Error("bad")),value=>value===error);
  }finally{await o.api.disposeAsync();}
  const good=bindingFixture(),bound=owner(good);try{const entry=await bound.catalog.resolve();const report=good.transport.reportInvariantViolation!;
    good.transport.reportInvariantViolation=async(target,error,options)=>{const result=await report(target,error,options);good.emit("faulted");await until(()=>entry.retirementSignal.aborted);assert.equal(options!.signal!.aborted,false,"Retirement must not cancel the report acknowledgement.");return result;};
    assert.equal(await entry.tryReportInvariantViolation(new Error("bad")),true);assert.equal(entry.retirementSignal.aborted,true);assert.equal(good.reports,1);}finally{await bound.api.disposeAsync();}
});
test("cached observer unavailability cannot hide a fresh permission denial in strict or optional lookup",async()=>{
  const f=bindingFixture(),o=owner(f);
  try{
    const reference=(await o.catalog.resolve()).Reference;f.watches[0]!.end();await until(()=>reference.retirementSignal.aborted);
    const denied=new RpcError({kind:"permission-denied",code:"rpc.lookup.denied",message:"fresh denial"});
    f.transport.discover=async()=>{throw denied;};f.transport.getProvider=async()=>{throw denied;};
    await assert.rejects(o.catalog.resolve(),error=>error===denied);
    await assert.rejects(o.context.serviceBindings().resolveOptional(locatorFromProvider(provider)),error=>error===denied);
    const marshalling=RpcMarshallingContext.forCall(o.context,undefined,"target",o.api.signal,async()=>{},()=>{});
    try{await assert.rejects(marshalling.serialize(reference,(ctx,value)=>ctx.registerOptionalServiceReference(value,provider.contractId,"service"),()=>null),error=>error===denied);}
    finally{await marshalling.dispose();}
  }finally{await o.api.disposeAsync();}
});
test("genuine discovery failures retain global retirement, including EnsureCurrent's discovery fallback",async()=>{
  for(const throughBinding of [false,true]){
    const f=bindingFixture(),o=owner(f);const other={...provider,contractId:"example.other",endpointReference:"other",providerId:"example.other"};f.setProviders([{...provider},other]);
    try{
      const entry=await o.catalog.resolve();await o.context.serviceBindings().discover(other.contractId);const sibling=o.context.serviceBindings().track(other);
      const unavailable=new RpcError({kind:"unavailable",code:"rpc.catalog.unavailable",message:"discovery continuity lost"});f.setDiscoverFailure(unavailable);
      if(throughBinding)delete f.transport.getProvider;
      await assert.rejects(throughBinding?entry.ensureCurrent():o.catalog.list(),error=>error===unavailable);
      assert.equal(entry.retirementSignal.aborted,true);assert.equal(sibling.signal.aborted,true,"Discovery failures are not endpoint-local failures.");
      f.setDiscoverFailure(undefined);await until(()=>f.watches.length===2);
      const fresh=await o.catalog.resolve();assert.notEqual(fresh,entry);await fresh.ensureCurrent();assert.equal(entry.retirementSignal.aborted,true);
    }finally{await o.api.disposeAsync();}
  }
});
test("finite resource pressure preserves exact references and the independent observer",async t=>{
  for(const operation of ["discover","ensure","resolve"] as const)await t.test(operation,async()=>{
    const f=bindingFixture(),o=owner(f),other={...provider,contractId:"example.other",endpointReference:"other",providerId:"example.other"};
    f.setProviders([{...provider},other]);
    try{
      const entry=await o.catalog.resolve();await o.context.serviceBindings().discover(other.contractId);
      const sibling=o.context.serviceBindings().track(other);
      const pressure=new RpcError({kind:"resource-exhausted",code:"rpc.worker.call-limit",message:"busy"});
      f.setDiscoverFailure(pressure);f.setGetFailure(pressure);
      await assert.rejects(operation==="discover"?o.catalog.list():operation==="ensure"?entry.ensureCurrent():o.context.serviceBindings().resolve(locatorFromProvider(provider)),error=>error===pressure);
      assert.equal(entry.retirementSignal.aborted,false);assert.equal(sibling.signal.aborted,false);assert.equal(f.watches.length,1);
      f.setDiscoverFailure(undefined);f.setGetFailure(undefined);
      await entry.ensureCurrent();await sibling.ensureCurrent();assert.equal(await o.catalog.resolve(),entry);assert.equal(f.watches.length,1);
    }finally{await o.api.disposeAsync();}
  });
});
test("endpoint Unavailable retires only exact bindings, while PermissionDenied retains contract scope",async()=>{
  for(const kind of ["unavailable","permission-denied"] as const){
    const f=bindingFixture(),o=owner(f);const same={...provider,providerId:"example.second",endpointReference:"second"},other={...provider,contractId:"example.other",providerId:"example.other",endpointReference:"other"};f.setProviders([{...provider},same,other]);
    try{
      const entries=await o.catalog.list();const entry=entries.providers.find(value=>value.snapshot.providerId===provider.providerId)!;
      const peer=entries.providers.find(value=>value.snapshot.providerId===same.providerId)!;await o.context.serviceBindings().discover(other.contractId);const sibling=o.context.serviceBindings().track(other);
      const error=new RpcError({kind,code:"rpc.endpoint.failure",message:kind});f.setGetFailure(error);
      const results=await Promise.allSettled(Array.from({length:16},()=>entry.ensureCurrent()));
      for(const result of results){assert.equal(result.status,"rejected");if(result.status==="rejected")assert.equal(result.reason,error);}
      assert.equal(entry.retirementSignal.aborted,true);assert.equal(peer.retirementSignal.aborted,kind==="permission-denied");assert.equal(sibling.signal.aborted,false);assert.equal(f.watches.length,1);
      f.setGetFailure(undefined);const fresh=(await o.catalog.list()).providers.find(value=>value.snapshot.providerId===provider.providerId)!;assert.notEqual(fresh,entry);await fresh.ensureCurrent();assert.equal(entry.retirementSignal.aborted,true);assert.equal(f.watches.length,1);
    }finally{await o.api.disposeAsync();}
  }
});
test("DI disposal drains a pending lookup and watch while the issued retirement signal remains inspectable",async()=>{
  const f=bindingFixture(),o=owner(f);const entry=await o.catalog.resolve();let release!:(value:null)=>void,started=false;
  f.transport.getProvider=async()=>{started=true;return new Promise<null>(resolve=>{release=resolve;});};
  let reentrant:Promise<void>|undefined;entry.retirementSignal.addEventListener("abort",()=>{reentrant=o.context.serviceBindings().disposeAsync();});
  const query=entry.ensureCurrent();void query.catch(()=>{});await until(()=>started);let done=false;const disposal=o.api.disposeAsync().then(()=>{done=true;});
  await Promise.resolve();assert.equal(done,false);assert.equal(entry.retirementSignal.aborted,true);release(null);await disposal;await reentrant;await assert.rejects(query);assert.equal(entry.Reference.retirementSignal,entry.retirementSignal);
});
test("retirement removes old cache entries through more than 4096 sequential activations",async()=>{
  const f=bindingFixture(),o=owner(f);try{for(let index=0;index<4100;index++){f.setProviders([{...provider,activationEpoch:index}]);const entry=await o.catalog.resolve();await entry.ensureCurrent();if(index%256===0)await new Promise(resolve=>setImmediate(resolve));}assert.equal(f.watches.length,1);}finally{await o.api.disposeAsync();}
});
test("request references cancel operations before target invocation and remain participants through retained content",async()=>{
  const f=bindingFixture(),o=owner(f);let closed=0;
  try{const reference=(await o.catalog.resolve()).Reference;
    const resources=RpcMarshallingContext.forCall(o.context,{register:async()=>{throw Error("unused");},open:async()=>({stream:{async *[Symbol.asyncIterator](){yield new Uint8Array([1]);}},async discard(){}})},"target",o.api.signal,async()=>{closed++;},()=>{});
    await resources.serialize(reference,(ctx,value)=>ctx.registerServiceReference(value,provider.contractId,"service"),(value,ctx)=>ctx.serviceReferenceToWire(value,provider.contractId,"service"));
    const wire={id:"held",length:1,sha256:"a".repeat(64),mediaType:"application/octet-stream",fileName:"",expiresAtUtc:"2099-01-01T00:00:00Z",repeatability:"single-use"};
    const content=await resources.deserialize(wire,async()=>{},(value,ctx)=>ctx.contentFromWire(value));await resources.dispose();assert.equal(closed,0);
    f.emit("removed");await until(()=>resources.signal.aborted);await resources.revoke();assert.equal(f.invokes,0);assert.equal(closed,1);await assert.rejects(content.openRead());
  }finally{await o.api.disposeAsync();}
});
test("received request participants supply the provider-domain signal while output references borrow only receiver lifetime",async()=>{
  const f=bindingFixture(),o=owner(f);try{
    const original=new AbortController();const resources=RpcMarshallingContext.forInvocation({signal:original.signal} as RpcInvocationContext,o.context);
    const wire=locatorFromProvider(provider) as unknown as import("../src/core").JsonValue;
    const reference=await resources.deserialize(wire,(ctx,value)=>ctx.prepareServiceReference(value,provider.contractId,"service"),(value,ctx)=>ctx.serviceReferenceFromWire(value,provider.contractId,"service"));
    f.emit("removed");await until(()=>resources.signal.aborted);assert.equal(original.signal.aborted,false);assert.equal(reference.retirementSignal.aborted,true);assert.equal(f.invokes,0);await resources.dispose();
    f.setProviders([{...provider}]);let closed=0;const output=RpcMarshallingContext.forCall(o.context,undefined,"target",o.api.signal,async()=>{closed++;},()=>{});
    const returned=await output.deserialize(wire,(ctx,value)=>ctx.prepareServiceReference(value,provider.contractId,"service"),(value,ctx)=>ctx.serviceReferenceFromWire(value,provider.contractId,"service"));
    await output.dispose();assert.equal(closed,1);assert.equal(returned.retirementSignal.aborted,false);await returned.ensureCurrent();
  }finally{await o.api.disposeAsync();}
});
test("exact call targets automatically remain participants after unary completion while returned content is retained",async()=>{
  const f=bindingFixture(),o=owner(f);const createScope=f.transport.createCallScope!;let closed=0;
  f.transport.createCallScope=async options=>({...await createScope(options),content:{register:async()=>{throw Error("unused");},open:async()=>({stream:{async *[Symbol.asyncIterator](){yield new Uint8Array([1]);}},async discard(){}})},async close(){closed++;}});
  try{const token=createApiToken("example.owner",context=>({get:()=>context.unary({...provider,resourceContent:true},async(_rpc,_target,_options,resources)=>resources.deserialize({id:"held",length:1,sha256:"a".repeat(64),mediaType:"application/octet-stream",fileName:"",expiresAtUtc:"2099-01-01T00:00:00Z",repeatability:"single-use"},async()=>{},(value,ctx)=>ctx.contentFromWire(value)))}),"generation",[o.token]);
    const content=await o.api.api(token).get();assert.equal(closed,0);f.emit("removed");await until(()=>closed===1);await assert.rejects(content.openRead());await content.dispose();assert.equal(closed,1);
  }finally{await o.api.disposeAsync();}
});
test("the receiver enforces the contract and per-provider typed-entry bounds",async()=>{
  const f=bindingFixture(),o=owner(f);try{await o.catalog.resolve();const lifetime=o.context.serviceBindings();const binding=lifetime.track(provider);
    for(let index=1;index<128;index++)binding.entry({},()=>({index}));assert.throws(()=>binding.entry({},()=>({})),/limit/);
    for(let index=1;index<128;index++)await lifetime.discover(`example.contract${index}`);
    await assert.rejects(lifetime.discover("example.overflow"),/limit/);
  }finally{await o.api.disposeAsync();}
});
test("generated method timeout requests inherit the parent minimum and dispatch with the Host-clamped deadline",async t=>{
  t.mock.timers.enable({apis:["Date"],now:1_900_000_000_000});
  const f=bindingFixture();Object.defineProperty(f.transport,"deadline",{value:new Date(Date.now()+45000)});let requested=0,dispatched=0;
  f.transport.createCallScope=async options=>{requested=options!.deadline!.getTime();return {...f.transport,deadline:new Date(Date.now()+15000),async close(){},async invoke(_target,_service,_method,_value,options){dispatched=options!.deadline!.getTime();return "ok" as never;}};};
  const o=owner(f);try{const policy=createApiToken("example.owner",context=>({run:()=>context.unary({...provider,timeoutSeconds:2100},(rpc,target,options)=>rpc.invoke(target,"service","run",{},options))}),"generation",[o.token]);
    assert.equal(await o.api.api(policy).run(),"ok");assert.equal(requested,Date.now()+45000);assert.equal(dispatched,Date.now()+15000);
  }finally{await o.api.disposeAsync();}
});
