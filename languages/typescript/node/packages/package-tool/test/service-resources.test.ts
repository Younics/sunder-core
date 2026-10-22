import assert from "node:assert/strict";
import { mkdtemp,mkdir,writeFile,readFile,rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { build } from "esbuild";
import ts from "typescript";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {computeRpcDescriptorSha256,computeRpcAbiSha256} from "@sunder/sdk";
import { loadProjectWithOwnedFrame } from "../src/model";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { emptyLock,fixtureSource,catalogFeed } from "./service-fixtures";
import { MODEL_PROJECTION_PATH,parseModelProjectionDocument,validateServiceReferenceSchema,readModelProjection,modelProjectionOuter,validateModelProjectionSet } from "../src/rpc-model-projection";
import { canonicalCandidateFixture } from "./model-projection-fixtures";
import { parseContractLock } from "../src/rpc-contract-lock";
import { publishContractCache } from "../src/rpc-contract-cache";
import { publishModelProjectionCache } from "../src/rpc-model-projection-cache";

const authored=`import {defineService,SunderContent} from "@sunder/sdk";
/*ts import type {ServiceReference,OptionalServiceReference,WorkerContext,ServiceContext} from "@sunder/sdk";*/
import {Self} from "#sunder/api";
/** @type {import("@sunder/sdk").WorkerContext} */
let worker/*: WorkerContext*/;
/** @param {import("@sunder/sdk").WorkerContext} context */
export function configure(context/*: WorkerContext*/){worker=context;return {};}
/** @type {{signal?:AbortSignal,targetInvoked:boolean}} */
export const pending/*:{signal?:AbortSignal;targetInvoked:boolean}*/={targetInvoked:false};
export const runner=defineService({
/** @param {string} name @returns {string} */
run(name/*:string*/)/*:string*/{return "run:"+name;},
/** @param {string} name */
async *watch(name/*:string*/){yield "run:"+name;}
},{exposure:"public",runtimeUse:{}});
/** @typedef {{kind:"content",value:import("@sunder/sdk").SunderContent}|{kind:"service",value:import("@sunder/sdk").ServiceReference<typeof runner>}} ResourceChoice */
/*ts export type ResourceChoice={kind:"content";value:SunderContent}|{kind:"service";value:ServiceReference<typeof runner>};*/
/** @typedef {{text:string, content:import("@sunder/sdk").SunderContent[], copies:Record<string,import("@sunder/sdk").SunderContent>, runner:import("@sunder/sdk").ServiceReference<typeof runner>, maybe:import("@sunder/sdk").OptionalServiceReference<typeof runner>|null, optional?:import("@sunder/sdk").SunderContent|null, variant?:ResourceChoice}} Reply */
/*ts export type Reply={text:string;content:SunderContent[];copies:Record<string,SunderContent>;runner:ServiceReference<typeof runner>;maybe:OptionalServiceReference<typeof runner>|null;optional?:SunderContent|null;variant?:ResourceChoice};*/
/** @returns {Promise<Reply>} */
async function make()/*:Promise<Reply>*/{
 const provider=await worker.api(Self).runner.Providers.resolve();
 const content=SunderContent.fromBytes(new Uint8Array([1,2,3]),{fileName:"example.bin"});
 return {text:"hello",content:[content,content],copies:{again:content},runner:provider.Reference,maybe:provider.Reference,optional:null,variant:{kind:"content",value:content}};
}
export const documents=defineService({
 get:make,
 // Return values are inferred by the TS/checkJs source checker, including the
 // resource class. No transport schema, codec or return annotation is authored.
 download(){return SunderContent.fromBytes(new Uint8Array([8,9]),{fileName:"inferred.bin"});},
 /** @param {import("@sunder/sdk").SunderContent} content */
 echoContent(content/*:SunderContent*/){return content;},
 /** @param {import("@sunder/sdk").ServiceReference<typeof runner>} reference */
 useReference(reference/*:ServiceReference<typeof runner>*/){return reference.Service.run("inferred");},
 /** @param {import("@sunder/sdk").ServiceReference<typeof runner>} reference */
 watchReference(reference/*:ServiceReference<typeof runner>*/){return reference.Service.watch("inferred-stream");},
 /** @param {import("@sunder/sdk").ServiceReference<typeof runner>} reference @param {import("@sunder/sdk").ServiceContext} context @returns {Promise<string>} */
 async waitForTarget(reference/*:ServiceReference<typeof runner>*/,context/*:ServiceContext*/)/*:Promise<string>*/{
   pending.signal=context.signal;
   await new Promise((_resolve,reject)=>{const cancelled=()=>reject(context.signal.reason);context.signal.addEventListener("abort",cancelled,{once:true});if(context.signal.aborted)cancelled();});
   pending.targetInvoked=true;return reference.Service.run("too-late");
 },
 /** @param {import("@sunder/sdk").ServiceReference<typeof runner>} reference @param {import("@sunder/sdk").ServiceContext} context */
 async *watchTarget(reference/*:ServiceReference<typeof runner>*/,context/*:ServiceContext*/){
   pending.signal=context.signal;
   await new Promise((_resolve,reject)=>{const cancelled=()=>reject(context.signal.reason);context.signal.addEventListener("abort",cancelled,{once:true});if(context.signal.aborted)cancelled();});
   pending.targetInvoked=true;yield await reference.Service.run("too-late");
 },
 /** @param {Reply} request @param {import("@sunder/sdk").ServiceContext} context @returns {Promise<Reply>} */
 async forward(request/*:Reply*/,context/*:ServiceContext*/)/*:Promise<Reply>*/{context.signal.throwIfAborted();await request.runner.Service.run("provider");return request;},
 /** @returns {AsyncIterable<Reply>} */
 async *watch()/*:AsyncIterable<Reply>*/{yield await make();yield await make();}
},{exposure:"public",runtimeUse:{},methods:{get:{timeoutSeconds:2100},watch:{timeoutSeconds:1800}}});`;

async function fixture(extension:"ts"|"js"){
  const root=await mkdtemp(join(tmpdir(),"sunder-natural-resources-"));await mkdir(join(root,"src"));
  await writeFile(join(root,`src/services.${extension}`),fixtureSource(authored,extension));
  await writeFile(join(root,"sunder.package.json"),JSON.stringify({schemaVersion:3,id:"example.resources",name:"Natural resources",version:"1.0.0",workerCapabilities:[],services:{module:`src/services.${extension}`}}));
  await writeFile(join(root,"sunder-contracts.lock.json"),emptyLock);
  await writeFile(join(root,"package.json"),JSON.stringify({type:"module",imports:{"#sunder/api":{types:"./.sunder/generated/rpc/api.d.ts",default:"./.sunder/generated/rpc/api.js"}}}));
  return {root,load:()=>loadProjectWithOwnedFrame(root,{allowDrift:true}),dispose:()=>rm(root,{recursive:true,force:true})};
}
for(const extension of ["ts","js"] as const)test(`${extension} response retirement is provider-local, fingerprinted, and restricted to natural-content streams`,async()=>{
  const c=await fixture(extension);
  const source=`import {defineService,SunderContent} from "@sunder/sdk";
export const documents=defineService({async *watch(){yield SunderContent.fromBytes(new Uint8Array([1]),{fileName:"one.bin"});yield SunderContent.fromBytes(new Uint8Array([2]),{fileName:"two.bin"});}}, {methods:{watch:{completeOnResponseContentRetirement:true}}});`;
  const put=(value:string)=>writeFile(join(c.root,`src/services.${extension}`),fixtureSource(value,extension));
  try{
    await put(source);const opted=await c.load();
    await generateRpcBindingsForLoadedProject(opted);
    const bundle=await build({stdin:{contents:'export {providers} from "./.sunder/generated/rpc/adapter.js";export {RpcError} from "@sunder/sdk";',resolveDir:c.root,loader:"ts"},bundle:true,platform:"node",format:"cjs",write:false,alias:{"@sunder/sdk":require.resolve("@sunder/sdk"),"@sunder/sdk/browser":require.resolve("@sunder/sdk/browser"),"@sunder/sdk/generated":require.resolve("@sunder/sdk/generated")}});
    const module={exports:{} as any};new Function("require","module","exports",bundle.outputFiles[0]!.text)(require,module,module.exports);
    const {providers,RpcError}=module.exports;
    let registrations=0;const proof=new WeakMap<object,AbortSignal>();
    const error=new RpcError({kind:"unavailable",code:"rpc.content.authority-revoked",message:"retired"},true);
    const controller=new AbortController();
    const context={signal:controller.signal,content:{async register(source:AsyncIterable<Uint8Array>,_options:unknown,call:{signal:AbortSignal}){
      for await(const _ of source){}if(++registrations===2){proof.set(error,call.signal);throw error;}
      return {id:"first",length:1,sha256:"a".repeat(64),mediaType:"application/octet-stream",fileName:"one.bin",expiresAtUtc:"2099-01-01T00:00:00Z",repeatability:"single-use"};
    },consumeResponseContentRetirement(error:object,signal:AbortSignal){const matched=proof.get(error)===signal;proof.delete(error);return matched;}}};
    const values=[];for await(const value of providers[0].handler.invokeServerStream(context,"documents","watch",{}))values.push(value);
    assert.equal(values.length,1);assert.equal(registrations,2);
    // A public host-authenticated flag without transport proof must still fail.
    context.content.consumeResponseContentRetirement=()=>false;registrations=0;
    await assert.rejects(async()=>{for await(const _ of providers[0].handler.invokeServerStream(context,"documents","watch",{})){}},value=>value===error);
    for(const [name,text] of opted.ownedRpc!.files)if(/(?:package-export|authoring-receipt|model-projection).*json$/u.test(name))assert.doesNotMatch(text,/completeOnResponseContentRetirement/);
    await put(source.replace("completeOnResponseContentRetirement:true","completeOnResponseContentRetirement:false"));const off=await c.load();
    assert.deepEqual(off.contracts[0]!.canonicalBytes,opted.contracts[0]!.canonicalBytes);assert.equal(off.contracts[0]!.abiSha256,opted.contracts[0]!.abiSha256);
    assert.notEqual(off.ownedRpc!.inputFingerprint,opted.ownedRpc!.inputFingerprint);
    assert.notEqual(off.ownedRpc!.files.get("adapter.ts"),opted.ownedRpc!.files.get("adapter.ts"));
    for(const invalid of [source.replace("async *watch()","async watch()").replaceAll("yield ","return "),source.replaceAll(/SunderContent\.fromBytes\(new Uint8Array\(\[\d\]\),\{fileName:"[a-z]+\.bin"\}\)/gu,'"no content"'),source.replace("Retirement:true","Retirement:123"),source.replace("Retirement:true","Retirement:Math.random()>0.5")]){
      await put(invalid);await assert.rejects(c.load());
    }
    // A second implementation can opt out while sharing the exact same contract.
    await put(source+'\nexport const other=defineService({async *watch(){yield SunderContent.fromBytes(new Uint8Array([3]),{fileName:"three.bin"});}},{implements:documents,providerId:"example.resources.other",methods:{watch:{completeOnResponseContentRetirement:false}}});');
    const multiple=await c.load();assert.match(multiple.ownedRpc!.files.get("adapter.ts")!,/example.resources.other/);
  }finally{await c.dispose();}
});
for(const extension of ["ts","js"] as const)test(`${extension} optional reference marker emits occurrence-only canonical evidence without changing nullable wire contracts`,async()=>{
  const c=await fixture(extension);
  const source=`import {defineService} from "@sunder/sdk";
/*ts import type {OptionalServiceReference,ServiceReference} from "@sunder/sdk";*/
export const runner=defineService({run(){return "ready";}},{exposure:"public",runtimeUse:{}});
/** @typedef {{executionTarget:import("@sunder/sdk").OptionalServiceReference<typeof runner>|null}} Input */
/*ts export type Input={executionTarget:OptionalServiceReference<typeof runner>|null};*/
export const documents=defineService({
/** @param {Input} request @returns {Input} */
roundtrip(request/*:Input*/)/*:Input*/{return request;}
},{exposure:"public",runtimeUse:{}});`;
  const put=(value:string)=>writeFile(join(c.root,`src/services.${extension}`),fixtureSource(value,extension));
  try{
    await put(source);const project=await c.load();const contract=project.contracts.find(value=>value.contractKey==="documents")!;
    assert.deepEqual({...contract.modelProjection.optionalServiceReferenceMembers},{Input:["executionTarget"]});
    const projection=modelProjectionOuter(contract.modelProjection);assert.deepEqual(Object.keys(projection),["models","serviceReferences","optionalServiceReferenceMembers"]);
    assert.deepEqual(readModelProjection(projection,contract.descriptor),contract.modelProjection);
    for(const invalid of [{}, {Input:[]},{Missing:["executionTarget"]},{Input:["missing"]},{Input:["executionTarget","executionTarget"]}] as Record<string,string[]>[])assert.throws(()=>readModelProjection({...projection,optionalServiceReferenceMembers:invalid},contract.descriptor),/model-projection-invalid/);
    assert.match(project.ownedRpc!.files.get("owned/documents.ts")!,/registerOptionalServiceReference/);
    assert.match(project.ownedRpc!.files.get("owned/documents.ts")!,/optionalServiceReferenceFromWire/);
    const sidecar=parseModelProjectionDocument(Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!));
    assert.deepEqual({...sidecar.contracts.find(value=>value.contractId===contract.descriptor.contractId)!.modelProjection.optionalServiceReferenceMembers},{Input:["executionTarget"]});
    await put(source.replaceAll("OptionalServiceReference","ServiceReference").replace("ServiceReference,ServiceReference","ServiceReference"));const strict=await c.load();const strictContract=strict.contracts.find(value=>value.contractKey==="documents")!;
    assert.deepEqual(strictContract.canonicalBytes,contract.canonicalBytes);assert.equal(strictContract.abiSha256,contract.abiSha256);assert.equal(strictContract.modelProjection.optionalServiceReferenceMembers,undefined);
    assert.notEqual(strict.ownedRpc!.inputFingerprint,project.ownedRpc!.inputFingerprint);
    const evidence=project.contracts.map(value=>({contractId:value.descriptor.contractId,contractVersion:value.descriptor.version,descriptorSha256:value.descriptorSha256,abiSha256:value.abiSha256,descriptor:value.descriptor,modelProjection:value.modelProjection,authorityKind:"package",authorityId:project.config.id}));
    const descriptor={...contract.descriptor,contractId:"example.resources.copy"};
    const copied={...evidence.find(value=>value.contractId===contract.descriptor.contractId)!,contractId:descriptor.contractId,descriptor,descriptorSha256:await computeRpcDescriptorSha256(descriptor),abiSha256:await computeRpcAbiSha256(descriptor)};
    validateModelProjectionSet([...evidence,copied]);
    assert.throws(()=>validateModelProjectionSet([...evidence,{...copied,modelProjection:strictContract.modelProjection}]),/Incompatible model/);
    for(const invalid of [source.replaceAll("|null",""),source.replaceAll("executionTarget:","executionTarget?:"),source.replaceAll("typeof runner>|null","typeof runner>[]|null")]){await put(invalid);await assert.rejects(c.load(),/OptionalServiceReference|service-reference|nonportable/);}
    await put(source.replace('exposure:"public",runtimeUse:{}','exposure:"public"'));await assert.rejects(c.load(),/service-reference-use-missing/);
  }finally{await c.dispose();}
});
for(const extension of ["ts","js"] as const)test(`${extension} default natural content executes the generated provider over the actual Worker protocol`,async()=>{
  const c=await fixture(extension);
  try{
    const source=`import {defineService,SunderContent} from "@sunder/sdk";
/** @typedef {{revision:number,observedAt:string,content:import("@sunder/sdk").SunderContent}} Reply */
/*ts export type Reply={revision:number;observedAt:string;content:SunderContent};*/
export const messages=defineService({
/** @param {string} observedAt @returns {Reply} */
download(observedAt/*:string*/)/*:Reply*/{return {revision:1,observedAt,content:SunderContent.fromBytes(new TextEncoder().encode("natural payload"))};},
/** @param {Reply} request @returns {Promise<Reply>} */
async echo(request/*:Reply*/)/*:Promise<Reply>*/{
 const reader=(await request.content.openRead()).getReader();
 /** @type {Uint8Array[]} */ const chunks/*:Uint8Array[]*/=[];
 try{while(true){const next=await reader.read();if(next.done)break;chunks.push(next.value);}}finally{await reader.cancel();reader.releaseLock();}
 const bytes=new Uint8Array(chunks.reduce((size,part)=>size+part.length,0));let offset=0;for(const part of chunks){bytes.set(part,offset);offset+=part.length;}
 if(new TextDecoder().decode(bytes)!=="natural payload")throw new Error("Payload changed");
 return {...request,content:SunderContent.fromBytes(bytes)};
}
},{constraints:{"download.observedAt":{format:"date-time"},"Reply.observedAt":{format:"date-time"},"Reply.revision":{format:"integer"}}});`;
    await writeFile(join(c.root,`src/services.${extension}`),fixtureSource(source,extension));
    const project=await c.load();await generateRpcBindingsForLoadedProject(project);
    const bundle=join(c.root,"natural-provider.cjs");
    await build({entryPoints:[join(c.root,".sunder/generated/rpc/adapter.ts")],outfile:bundle,bundle:true,platform:"node",format:"cjs",alias:{"@sunder/sdk":require.resolve("@sunder/sdk"),"@sunder/sdk/browser":require.resolve("@sunder/sdk/browser"),"@sunder/sdk/generated":require.resolve("@sunder/sdk/generated")}});
    // Reuse the strict framed Worker harness, including its hostile Host cases.
    // SDK test compilation is a prerequisite (the workspace test order builds it first).
    const harness=join(require.resolve("@sunder/sdk"),"../../dist-test/test/worker.test.js");
    const env:NodeJS.ProcessEnv={...process.env,SUNDER_TEST_NATURAL_PROVIDER:bundle};delete env.NODE_TEST_CONTEXT;
    const result=spawnSync(process.execPath,["--test","--test-reporter=tap","--test-name-pattern=^natural content transport",harness],{env,encoding:"utf8",timeout:90_000});
    assert.equal(result.status,0,result.stdout+result.stderr);assert.match(result.stdout,/pass 2/);
  }finally{await c.dispose();}
});
for(const extension of ["ts","js"] as const)test(`${extension} optional reference occurrences execute actual generated datatype code over framed Worker calls`,async()=>{
  const c=await fixture(extension);
  try{
    const source=`import {defineService,RpcError} from "@sunder/sdk";import {Self} from "#sunder/api";
/*ts import type {OptionalServiceReference,ServiceReference,WorkerContext,ServiceContext} from "@sunder/sdk";*/
/** @type {import("@sunder/sdk").WorkerContext} */ let worker/*:WorkerContext*/;
/** @param {import("@sunder/sdk").WorkerContext} context */
export function configure(context/*:WorkerContext*/){worker=context;return {};}
export const runner=defineService({run(){return "ready";}},{runtimeUse:{}});
/** @typedef {{executionTarget:import("@sunder/sdk").OptionalServiceReference<typeof runner>|null}} OptionalInput */
/*ts export type OptionalInput={executionTarget:OptionalServiceReference<typeof runner>|null};*/
/** @typedef {{executionTarget:import("@sunder/sdk").ServiceReference<typeof runner>|null}} RequiredInput */
/*ts export type RequiredInput={executionTarget:ServiceReference<typeof runner>|null};*/
/** @typedef {{aOptional:import("@sunder/sdk").OptionalServiceReference<typeof runner>|null,zRequired:import("@sunder/sdk").ServiceReference<typeof runner>|null}} MixedInput */
/*ts export type MixedInput={aOptional:OptionalServiceReference<typeof runner>|null;zRequired:ServiceReference<typeof runner>|null};*/
/** @param {import("@sunder/sdk").ServiceReference<typeof runner>|null} reference @param {import("@sunder/sdk").ServiceContext} context @returns {Promise<string>} */
async function call(reference/*:ServiceReference<typeof runner>|null*/,context/*:ServiceContext*/)/*:Promise<string>*/{
 if(reference===null)return "absent";
 try{return await reference.Service.run({signal:context.signal});}
 catch(error){if(error instanceof RpcError)return "failure:"+error.error.kind+":"+error.error.dispatchState+":"+error.error.retrySafety;throw error;}
}
export const messages=defineService({
/** @param {OptionalInput} request @param {import("@sunder/sdk").ServiceContext} context */
inspect(request/*:OptionalInput*/,context/*:ServiceContext*/){return call(request.executionTarget,context);},
/** @param {RequiredInput} request @param {import("@sunder/sdk").ServiceContext} context */
required(request/*:RequiredInput*/,context/*:ServiceContext*/){return call(request.executionTarget,context);},
/** @param {MixedInput} request @param {import("@sunder/sdk").ServiceContext} context */
mixed(request/*:MixedInput*/,context/*:ServiceContext*/){return call(request.aOptional,context);},
/** @returns {Promise<OptionalInput>} */
async output()/*:Promise<OptionalInput>*/{const provider=await worker.api(Self).runner.Providers.resolve();return {executionTarget:provider.Reference};},
/** @returns {Promise<RequiredInput>} */
async outputRequired()/*:Promise<RequiredInput>*/{const provider=await worker.api(Self).runner.Providers.resolve();return {executionTarget:provider.Reference};},
/** @returns {Promise<MixedInput>} */
async outputMixed()/*:Promise<MixedInput>*/{const provider=await worker.api(Self).runner.Providers.resolve();return {aOptional:provider.Reference,zRequired:provider.Reference};}
});`;
    await writeFile(join(c.root,`src/services.${extension}`),fixtureSource(source,extension));
    const config=JSON.parse(await readFile(join(c.root,"sunder.package.json"),"utf8"));config.id="example.package";await writeFile(join(c.root,"sunder.package.json"),JSON.stringify(config));
    const project=await c.load();await generateRpcBindingsForLoadedProject(project);
    const bundle=join(c.root,"optional-provider.cjs");
    const sdk=join(require.resolve("@sunder/sdk"),"../../dist-test/src/index.js"),browser=join(sdk,"../browser.js"),generated=join(sdk,"../generated.js");
    await build({stdin:{contents:`export * from "./.sunder/generated/rpc/adapter.js";export {Self} from "./.sunder/generated/rpc/api.js";export {configure} from "./src/services.${extension}";`,resolveDir:c.root,loader:"ts"},outfile:bundle,bundle:true,platform:"node",format:"cjs",external:[sdk,browser,generated],alias:{"@sunder/sdk":sdk,"@sunder/sdk/browser":browser,"@sunder/sdk/generated":generated,"#sunder/api":join(c.root,".sunder/generated/rpc/api.js")}});
    const env:NodeJS.ProcessEnv={...process.env,SUNDER_TEST_OPTIONAL_PROVIDER:bundle};delete env.NODE_TEST_CONTEXT;
    const harness=join(require.resolve("@sunder/sdk"),"../../dist-test/test/worker.test.js");
    const result=spawnSync(process.execPath,["--test","--test-reporter=tap","--test-name-pattern=^source optional references",harness],{env,encoding:"utf8",timeout:90_000});
    assert.equal(result.status,0,result.stdout+result.stderr);assert.match(result.stdout,/pass 1/);
  }finally{await c.dispose();}
});
for(const extension of ["ts","js"] as const)test(`${extension} natural resources derive exact reference projections and browser-safe Models without author RPC plumbing`,async()=>{
  const c=await fixture(extension);try{
    const project=await c.load();const documents=project.contracts.find(contract=>contract.contractKey==="documents")!;
    assert.equal(documents.naturalContent,true);assert.equal(Object.keys(documents.modelProjection.serviceReferences).length,1);
    assert.deepEqual(JSON.parse(JSON.stringify(documents.methodPolicies)),{documents:{get:{timeoutSeconds:2100},watch:{timeoutSeconds:1800}}});
    assert.ok(project.config.workerCapabilities.includes("rpc.extended-deadline.v1"));
    for(const name of ["package-export.v1.json","authoring-receipt.v1.json",MODEL_PROJECTION_PATH]){
      const entry=JSON.parse(project.ownedRpc!.files.get(name)!).contracts.find((value:any)=>value.contractId===documents.descriptor.contractId);
      assert.deepEqual(entry.methodPolicies,JSON.parse(JSON.stringify(documents.methodPolicies)));
      assert.equal(Object.keys(entry)[Object.keys(entry).indexOf("modelProjection")+1],"methodPolicies");
    }
    for(const [key,target] of Object.entries(documents.modelProjection.serviceReferences)){
      assert.deepEqual(target,{contractId:"example.resources.runner",serviceId:"runner"});validateServiceReferenceSchema(documents.descriptor.$defs[key]);
    }
    assert.equal(parseModelProjectionDocument(Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!)).contracts.length,2);
    const binding=project.ownedRpc!.files.get("owned/documents.ts")!;assert.match(binding,/registerContent/);assert.match(binding,/prepareServiceReference/);assert.doesNotMatch(binding,/Object\.entries\(value\)/);
    await generateRpcBindingsForLoadedProject(project);
    const consumer=join(c.root,`consumer.${extension}`);const consumerSource=fixtureSource(`import {Self,Services} from "#sunder/api";
/*ts import type {SelfModels} from "#sunder/api"; import type {ApiContext,ServiceReference} from "@sunder/sdk/browser";*/
/** @param {import("#sunder/api").SelfModels.Reply} value */
export async function read(value/*:SelfModels.Reply*/){const stream=await value.content[0].openRead();await value.runner.Service.run("browser");await value.content[0].dispose();return stream;}
/** @param {import("@sunder/sdk/browser").ApiContext} owner @param {AbortSignal} caller @param {AbortSignal} enumeration */
export async function consume(owner/*:ApiContext*/,caller/*:AbortSignal*/,enumeration/*:AbortSignal*/){
 const api=owner.api(Self);const readyProvider=await api.runner.Providers.resolve();
 /** @type {import("@sunder/sdk/browser").ServiceReference<typeof Services.Self.runner>} */
 const reference/*:ServiceReference<typeof Services.Self.runner>*/=readyProvider.Reference;
 await readyProvider.Service.run("direct");
 for await(const text of reference.Service.watch("typed",{signal:caller}).withCancellation(enumeration))text.toUpperCase();
 for await(const reply of api.documents.watch({signal:caller}).withCancellation(enumeration))await reply.content[0].dispose();
 const content=await api.documents.download();await content.dispose();
}`,extension);
    await writeFile(consumer,consumerSource);
    const compilerOptions:ts.CompilerOptions={target:ts.ScriptTarget.ES2023,module:ts.ModuleKind.ESNext,moduleResolution:ts.ModuleResolutionKind.Bundler,strict:true,allowJs:true,checkJs:true,noEmit:true,types:[],skipLibCheck:false,
       paths:{"@sunder/sdk/browser":[require.resolve("@sunder/sdk/browser").replace(/\.js$/u,".d.ts")],"@sunder/sdk/generated":[require.resolve("@sunder/sdk/generated").replace(/\.js$/u,".d.ts")],"@sunder/sdk/core":[require.resolve("@sunder/sdk/core").replace(/\.js$/u,".d.ts")]}};
    const program=ts.createProgram([consumer],compilerOptions);
    assert.deepEqual(ts.getPreEmitDiagnostics(program).map(value=>`${value.file?.fileName}:${value.start}: ${ts.flattenDiagnosticMessageText(value.messageText,"\n")}`),[]);
    await writeFile(consumer,consumerSource.replace('run("browser")','run(42)').replace(".openRead()",'.openRead("not-a-signal")'));
    const invalid=ts.createProgram([consumer],compilerOptions);
    assert.deepEqual(ts.getPreEmitDiagnostics(invalid).map(value=>value.code),[2345,2345],"Both TS and genuine checkJs consumers must reject wrongly typed natural resource calls.");
    const browser=await build({stdin:{contents:'export {Self,Services} from "#sunder/api";export {SunderContent,createBrowserContext} from "@sunder/sdk/browser";',resolveDir:c.root,loader:"ts"},bundle:true,platform:"browser",format:"cjs",write:false,metafile:true,
       alias:{"@sunder/sdk/browser":require.resolve("@sunder/sdk/browser"),"@sunder/sdk/generated":require.resolve("@sunder/sdk/generated")}});
    assert.ok(Object.keys(browser.metafile!.inputs).every(path=>!/(?:node:|\/worker\.|\/content-node\.)/u.test(path)));assert.doesNotMatch(browser.outputFiles[0]!.text,/node:fs|SelfModels/);
  }finally{await c.dispose();}
});

async function runtime(c:Awaited<ReturnType<typeof fixture>>,project:Awaited<ReturnType<typeof c.load>>,consumerRoot?:string){
  const bundle=await build({stdin:{contents:`export * from "./.sunder/generated/rpc/api.js";
export {createProviders} from "./.sunder/generated/rpc/adapter.js";
export {configure} from ${JSON.stringify("./"+project.config.services!.module.replace(/\.[jt]s$/u,".js"))};
export * as Authored from ${JSON.stringify("./"+project.config.services!.module.replace(/\.[jt]s$/u,".js"))};
export {createApiContext,SunderContent,RpcError} from "@sunder/sdk";
export {bindApiContext} from "@sunder/sdk/generated";
${consumerRoot?`export * as Consumer from ${JSON.stringify(join(consumerRoot,".sunder/generated/rpc/api.js"))};`:""}`,resolveDir:c.root,loader:"ts"},bundle:true,platform:"node",format:"cjs",write:false,
    alias:{"@sunder/sdk":require.resolve("@sunder/sdk"),"@sunder/sdk/browser":require.resolve("@sunder/sdk/browser"),"@sunder/sdk/generated":require.resolve("@sunder/sdk/generated"),"#sunder/api":join(c.root,".sunder/generated/rpc/api.js")}});
  const module={exports:{} as any};new Function("require","module","exports",bundle.outputFiles[0]!.text)(require,module,module.exports);const api=module.exports;
  let sequence=0;let allowed=true;let endpointFailure:unknown;let endpointQueries=0;const watchSignals:AbortSignal[]=[];const snapshots=project.config.providers.map(provider=>{const contract=project.contracts.find(contract=>contract.descriptor.contractId===provider.contractId)!;
    return {...provider,packageId:project.config.id,packageVersion:project.config.version,descriptorSha256:contract.descriptorSha256,abiSha256:contract.abiSha256,
      activationId:"11223344556677889900aabbccddeeff",activationEpoch:1,sessionGeneration:1,endpointReference:"endpoint:"+provider.providerId,state:"active",catalogRevision:1,faultCode:null};});
  const bodies=new Map<string,{bytes:Uint8Array;scope:number;target:string;direction:"request"|"response"}>();
  const scopes:Array<{id:number;controller:AbortController;closed:boolean;closeCalls:number}>=[];const wires:any[]=[];const registrations:Array<{direction:string;scope:number;target:string}>=[];
  const deadlines:Array<{requested:number;effective:number}>=[];
  let revision=1,sequenceNumber=1;const feeds:Array<ReturnType<typeof catalogFeed>>=[];
  const register=async(scope:number,target:string,direction:"request"|"response",source:AsyncIterable<Uint8Array>,options:any)=>{
    const parts:Uint8Array[]=[];for await(const part of source)parts.push(part);const bytes=new Uint8Array(parts.reduce((n,p)=>n+p.length,0));let offset=0;for(const part of parts){bytes.set(part,offset);offset+=part.length;}
    const id="resource-"+(++sequence);bodies.set(id,{bytes,scope,target,direction});registrations.push({direction,scope,target});assert.equal(options.repeatability,undefined);
    return {id,length:bytes.length,sha256:createHash("sha256").update(bytes).digest("hex"),mediaType:options.mediaType,fileName:options.fileName,expiresAtUtc:"2099-01-01T00:00:00Z",repeatability:"single-use"};
  };
  const open=async(scope:number,target:string,direction:"request"|"response",reference:any,signal:AbortSignal)=>{
    signal.throwIfAborted();const body=bodies.get(reference.id);assert.ok(body);assert.equal(body.scope,scope);assert.equal(body.target,target);assert.equal(body.direction,direction);
    let closed=false;return {stream:{async *[Symbol.asyncIterator](){signal.throwIfAborted();if(closed)throw Error("Closed read");yield body.bytes.slice();}},async discard(){closed=true;}};
  };
  let providers:any[]=[];
  const transport:any={async discover(id:string){return {revision,sequence:sequenceNumber,resetRequired:false,providers:allowed?snapshots.filter(value=>value.contractId===id).map(value=>({...value})):[]};},
    watch(_revision:number,_sequence:number,options:any){const feed=catalogFeed(options.signal);feeds.push(feed);watchSignals.push(options.signal);return feed.stream;},
    async createCallScope(options:any){
      const deadline=new Date(Date.now()+60000);if(options?.deadline)deadlines.push({requested:options.deadline.getTime()-Date.now(),effective:deadline.getTime()});
      const scope={id:scopes.length,controller:new AbortController(),closed:false,closeCalls:0};scopes.push(scope);
      const invoke=async function*(target:string,service:string,method:string,value:any,stream:boolean){
        scope.controller.signal.throwIfAborted();const snapshot=snapshots.find(value=>value.endpointReference===target)!;assert.ok(snapshot);if(!allowed)throw Error("permission-denied");
        const provider=providers.find(value=>value.providerId===snapshot.providerId)!;assert.ok(provider);
        const lifetime=new AbortController();const signal=AbortSignal.any([scope.controller.signal,lifetime.signal]);
        const context={callerPackageId:"example.caller",callerPackageVersion:"1.0.0",deadlineUtc:new Date(Date.now()+60000).toISOString(),callDepth:1,provider:snapshot,signal,
          content:{register:(body:AsyncIterable<Uint8Array>,metadata:any)=>register(scope.id,target,"response",body,metadata),open:(reference:any,call:any)=>open(scope.id,target,"request",reference,call?.signal??signal)}};
        try{
          const request=JSON.parse(JSON.stringify(value));wires.push(request);
          if(stream){for await(const result of provider.handler.invokeServerStream(context,service,method,request)){const wire=JSON.parse(JSON.stringify(result));wires.push(wire);yield wire;}}
          else{const result=await provider.handler.invokeUnary(context,service,method,request);const wire=JSON.parse(JSON.stringify(result));wires.push(wire);yield wire;}
        }catch(error){if(error instanceof api.RpcError)throw new api.RpcError((error as any).error,true);throw error;}
        finally{lifetime.abort(new Error("Invocation ended"));}
      };
      return {discover:transport.discover,...transport.getProvider?{getProvider:transport.getProvider}:{},signal:scope.controller.signal,deadline,
        content:{register:(target:string,source:AsyncIterable<Uint8Array>,metadata:any)=>register(scope.id,target,"request",source,metadata),
          open:(reference:any,call:any)=>{const body=bodies.get(reference.id)!;return open(scope.id,body.target,"response",reference,call?.signal??scope.controller.signal);}},
        async invoke(target:string,service:string,method:string,value:any,options:any){assert.ok(options.deadline.getTime()<=deadline.getTime());for await(const result of invoke(target,service,method,value,false))return result;},
        subscribe:(target:string,service:string,method:string,value:any)=>invoke(target,service,method,value,true),
        async close(){scope.closeCalls++;if(scope.closed)return;scope.closed=true;scope.controller.abort(new Error("Scope closed"));},
      };
    }};
  const owner=api.createApiContext(project.config.id,transport);api.bindApiContext(owner,api.Self);api.configure(owner);providers=api.createProviders(owner);
  const caller=api.createApiContext(project.config.id,transport);api.bindApiContext(caller,api.Self);
  const consumer=consumerRoot?api.createApiContext("example.consumer",transport):undefined;if(consumer)api.bindApiContext(consumer,api.Consumer.Self);
  return {api,owner,caller,consumer,client:caller.api(api.Self),scopes,wires,registrations,snapshots,deadlines,watchSignals,get endpointQueries(){return endpointQueries;},setEndpointFailure(value:unknown){endpointFailure=value;transport.getProvider=async(target:string)=>{endpointQueries++;if(endpointFailure)throw endpointFailure;return snapshots.find(value=>value.endpointReference===target)??null;};},setAllowed(value:boolean){allowed=value;},
    retire(contractId:string){const provider=snapshots.find(value=>value.contractId===contractId)!;provider.state="inactive";provider.catalogRevision=++revision;sequenceNumber++;for(const feed of feeds)feed.emit({revision,sequence:sequenceNumber,kind:"deactivated",provider:{...provider}});},
    dispose:async()=>{await consumer?.disposeAsync();await caller.disposeAsync();await owner.disposeAsync();}};
}
async function bytes(content:any):Promise<number[]>{const stream=await content.openRead();const reader=stream.getReader();const result:number[]=[];try{while(true){const next=await reader.read();if(next.done)return result;result.push(...next.value);}}finally{await reader.cancel();reader.releaseLock();}}
for(const extension of ["ts","js"] as const)test(`${extension} generated catalog list and borrowed proxy immediately probe fresh permission after endpoint-only unavailability`,async()=>{
  const c=await fixture(extension);let host:Awaited<ReturnType<typeof runtime>>|undefined;
  try{
    const project=await c.load();await generateRpcBindingsForLoadedProject(project);host=await runtime(c,project);host.setEndpointFailure(undefined);
    let selected:any;
    const consume=async()=>{
      const list=await host!.client.runner.Providers.list();
      selected=list.providers.find((provider:any)=>provider.snapshot.state==="active"&&!provider.retirementSignal.aborted);
      assert.ok(selected);
      try{await selected.ensureCurrent();return await selected.Service.run("memory");}
      catch(error){if(error instanceof host!.api.RpcError&&(error as any).error.kind==="unavailable")return null;throw error;}
    };
    assert.equal(await consume(),"run:memory");const original=selected,originalProxy=original.Service;
    const unavailable=new host.api.RpcError({kind:"unavailable",code:"rpc.endpoint.unavailable",message:"endpoint only",dispatchState:"not-dispatched",retrySafety:"safe"},true);
    const denied=new host.api.RpcError({kind:"permission-denied",code:"rpc.endpoint.denied",message:"fresh endpoint denial",dispatchState:"not-dispatched",retrySafety:"safe"},true);
    const delivered=host.wires.length;host.setEndpointFailure(unavailable);assert.equal(await consume(),null);assert.equal(original.retirementSignal.aborted,true);
    host.setEndpointFailure(denied);const queries=host.endpointQueries;await assert.rejects(consume(),error=>error===denied);assert.equal(host.endpointQueries,queries+1);
    for(let index=0;index<8;index++){host.setEndpointFailure(unavailable);assert.equal(await consume(),null);assert.equal(host.wires.length,delivered);assert.equal(host.watchSignals.length,1);assert.equal(host.watchSignals[0]!.aborted,false);}
    host.setEndpointFailure(undefined);assert.equal(await consume(),"run:memory");assert.notEqual(selected,original);assert.notEqual(selected.Service,originalProxy);
    assert.equal(original.retirementSignal.aborted,true);assert.throws(()=>original.Service);assert.equal(host.watchSignals.length,1);assert.equal(host.watchSignals[0]!.aborted,false);
  }finally{await host?.dispose();await c.dispose();}
});

for(const extension of ["ts","js"] as const)test(`${extension} generated adapters roundtrip content and exact provider references, including retained stream results and forwarding`,async()=>{
  const c=await fixture(extension);let host:Awaited<ReturnType<typeof runtime>>|undefined;try{
    const project=await c.load();await generateRpcBindingsForLoadedProject(project);host=await runtime(c,project);
    const reply=await host.client.documents.get();assert.equal(reply.content[0],reply.content[1]);assert.equal(reply.content[0],reply.copies.again);
    assert.ok(host.deadlines.some(value=>value.requested>2_099_000&&value.requested<=2_100_000));
    assert.equal(reply.variant.value,reply.content[0]);
    assert.deepEqual(await bytes(reply.content[0]),[1,2,3]);assert.equal(await reply.runner.Service.run("caller"),"run:caller");
    assert.equal(host.registrations.filter(value=>value.direction==="response").length,1);
    assert.equal(host.scopes.filter(value=>!value.closed).length,1);await reply.content[0].dispose();assert.ok(host.scopes.every(value=>value.closed));
    const readyProvider=await host.client.runner.Providers.resolve();const reference=readyProvider.Reference;
    assert.equal(await readyProvider.Service.run("ready"),"run:ready");assert.equal(await host.client.documents.useReference(reference),"run:inferred");
    const values:string[]=[];for await(const value of host.client.documents.watchReference(reference).withCancellation(new AbortController().signal))values.push(value);
    assert.deepEqual(values,["run:inferred-stream"]);
    const inferred=await host.client.documents.download();assert.deepEqual(await bytes(inferred),[8,9]);await inferred.dispose();
    const local=host.api.SunderContent.fromBytes(new Uint8Array([4,5,6]));
    const echoed=await host.client.documents.echoContent(local);assert.deepEqual(await bytes(echoed),[4,5,6]);await echoed.dispose();
    const forwarded=await host.client.documents.forward({text:"forward",content:[local,local],copies:{again:local},runner:reference,maybe:reference,variant:{kind:"service",value:reference}});
    assert.equal(forwarded.variant.value,forwarded.runner);
    assert.deepEqual(await bytes(forwarded.content[0]),[4,5,6]);assert.deepEqual(await bytes(local),[4,5,6]);await forwarded.content[0].dispose();await local.dispose();
    for(const wire of host.wires.filter(value=>value?.runner))assert.deepEqual(Object.keys(wire.runner),["packageId","packageVersion","providerId","contractId","contractVersion","descriptorSha256","abiSha256","activationId","activationEpoch","sessionGeneration","endpoint"]);
    const boundDocuments=(await host.client.documents.Providers.resolve()).Service;
    for(const early of [false,true])for(const cancellation of ["caller","enumeration"] as const){
      const caller=new AbortController(),enumeration=new AbortController();const items:any[]=[];
      for await(const value of (early?boundDocuments:host.client.documents).watch({signal:caller.signal}).withCancellation(enumeration.signal)){items.push(value);if(early)break;}
      assert.equal(items.length,early?1:2);const retained:Awaited<ReturnType<typeof runtime>>["scopes"]=host.scopes.filter(value=>!value.closed);assert.equal(retained.length,1);
      (cancellation==="caller"?caller:enumeration).abort(new Error(`${cancellation} cancelled after stream ${early?"exit":"completion"}`));
      // These bodies have never been opened: single-use acquisition cannot mask
      // a broken late-cancellation link by making a second read fail anyway.
      for(const item of items)await assert.rejects(bytes(item.content[0]));
      assert.equal(retained[0]!.closed,true);assert.equal(retained[0]!.closeCalls,1);
      assert.equal(await items[0].runner.Service.run("after-stream"),"run:after-stream","A service reference uses receiver DI, not the decoding content scope.");
    }
    const runner=host.snapshots.find(value=>value.contractId==="example.resources.runner")!;runner.sessionGeneration++;
    await assert.rejects(reference.Service.run("stale"),/No active provider|retired/);runner.sessionGeneration-=2;assert.throws(()=>reference.Service,/retired/);
    runner.sessionGeneration++;host.setAllowed(false);assert.throws(()=>reference.Service,/retired/);
  }finally{await host?.dispose();await c.dispose();}
});

for(const extension of ["ts","js"] as const)test(`${extension} generated request-reference participants cancel waiting domain methods and streams before target invocation`,async()=>{
  const c=await fixture(extension);let host:Awaited<ReturnType<typeof runtime>>|undefined;
  try{const project=await c.load();await generateRpcBindingsForLoadedProject(project);for(const kind of ["unary","stream"]){host=await runtime(c,project);
    const ready=await host.client.runner.Providers.resolve();const call=kind==="unary"?host.client.documents.waitForTarget(ready.Reference):(async()=>{for await(const _ of host!.client.documents.watchTarget(ready.Reference))assert.fail("Retired target yielded a value.");})();void call.catch(()=>{});
    const deadline=Date.now()+5000;while(!host.api.Authored.pending.signal){assert.ok(Date.now()<deadline,"Domain method did not receive its generated marshalling context.");await new Promise(resolve=>setTimeout(resolve,1));}
    assert.equal(host.api.Authored.pending.signal.aborted,false);host.retire("example.resources.runner");await assert.rejects(call);
    assert.equal(host.api.Authored.pending.signal.aborted,true);assert.equal(host.api.Authored.pending.targetInvoked,false);assert.equal(ready.retirementSignal.aborted,true);
    assert.equal(ready.Reference.retirementSignal,ready.retirementSignal);await host.owner.disposeAsync();assert.equal(ready.Reference.retirementSignal.aborted,true);
    await host.dispose();host=undefined;}
  }finally{await host?.dispose();await c.dispose();}
});

for(const extension of ["ts","js"] as const)test(`${extension} reference declarations reject callback factories, missing role use, and private public-sidecar targets`,async()=>{
  const c=await fixture(extension);try{
    const original=fixtureSource(authored,extension);const path=join(c.root,`src/services.${extension}`);
    await writeFile(path,original.replace('exposure:"public",runtimeUse:{}','exposure:"public"'));
    await assert.rejects(c.load(),/service-reference-use-missing/);
    await writeFile(path,original.replace('exposure:"public",runtimeUse:{}','exposure:"package",runtimeUse:{}'));
    await assert.rejects(c.load(),/Service target must resolve/);
    const callback=extension==="ts"?'const callback=(name:string)=>name;':'/** @param {string} name */ const callback=(name)=>name;';
    await writeFile(path,callback+"\n"+original.replaceAll("typeof runner","typeof callback"));
    await assert.rejects(c.load(),/service-reference-invalid/);
    const fake=`const fake=createServiceToken({contractId:"example.resources.runner",contractVersion:"1.0.0",descriptorSha256:"${"a".repeat(64)}",abiSha256:"${"b".repeat(64)}",serviceId:"runner"},()=>({run:()=>Promise.resolve("callback")}));`;
    await writeFile(path,original.replace('import {defineService,SunderContent}', 'import {defineService,SunderContent,createServiceToken}').replace('import {Self} from "#sunder/api";', 'import {Self} from "#sunder/api";\n'+fake).replaceAll("typeof runner","typeof fake"));
    await assert.rejects(c.load(),/service-reference-invalid/);
    await writeFile(path,original.replace('request.runner.Service.run("provider")','request.runner.Service.run(42)'));
    await assert.rejects(c.load(),/handler-incomplete:.*not assignable to parameter of type 'string'/s,"Authored JS is checked, not merely transpiled.");
  }finally{await c.dispose();}
});

for(const extension of ["ts","js"] as const)test(`${extension} selected public bundles project natural content and typed service tokens through offline imports`,async()=>{
  const producer=await fixture("ts"),consumer=await fixture(extension);let host:Awaited<ReturnType<typeof runtime>>|undefined;
  try{
    const project=await producer.load();await generateRpcBindingsForLoadedProject(project);
    const records=Object.fromEntries([...project.contracts].sort((a,b)=>a.descriptor.contractId<b.descriptor.contractId?-1:1).map(contract=>[contract.descriptor.contractId,{artifactSource:"owned",authorityKind:"package",authorityId:project.config.id,selectedAuthorityRelease:project.config.version,
      selectedAuthorityContentIdentity:"0".repeat(64),resolvedContractVersion:contract.descriptor.version,descriptorBytes:contract.canonicalBytes.length,descriptorSha256:contract.descriptorSha256,abiSha256:contract.abiSha256,etag:`"${contract.descriptorSha256}"`}]));
    const descriptors=new Map(project.contracts.map(contract=>[contract.descriptorSha256,contract.canonicalBytes]));
    const candidate=await canonicalCandidateFixture(project.config.id,{requestedVersionRange:"1.0.0",includePrerelease:false,selectedPackageVersion:project.config.version,selectedPackageContentIdentity:"0".repeat(64),publicContractCount:project.contracts.length,
      publicInventorySha256:"0".repeat(64),modelProjectionBytes:1,modelProjectionSha256:"0".repeat(64),contracts:records},Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!),descriptors);
    const lock=parseContractLock(Buffer.from(JSON.stringify({lockVersion:1,packageArtifacts:{[project.config.id]:candidate.artifact},packageImports:{[project.config.id]:{localName:"library",presence:"required",requiredContracts:[],consumerRoles:["runtime"]}},providerImports:{},platformImports:{}},null,2)+"\n"));
    const cache=join(consumer.root,"cache");await publishContractCache(cache,descriptors,lock.requirements);await publishModelProjectionCache(cache,new Map([[project.config.id,candidate.projection]]),lock.packageArtifacts,descriptors);
    const config=JSON.parse(await readFile(join(consumer.root,"sunder.package.json"),"utf8"));config.id="example.consumer";await writeFile(join(consumer.root,"sunder.package.json"),JSON.stringify(config));await writeFile(join(consumer.root,"sunder-contracts.lock.json"),lock.exactBytes);
    const source=`import {defineService} from "@sunder/sdk";import {Services} from "#sunder/api";
/*ts import type {ServiceReference} from "@sunder/sdk";import type {LibraryModels} from "#sunder/api";*/
export const relay=defineService({
/** @param {import("#sunder/api").LibraryModels.Reply} request @returns {import("#sunder/api").LibraryModels.Reply} */
roundtrip(request/*:LibraryModels.Reply*/)/*:LibraryModels.Reply*/{return request;},
/** @param {import("@sunder/sdk").ServiceReference<typeof Services.Library.example_Resources_Runner.runner>} reference @returns {Promise<string>} */
async run(reference/*:ServiceReference<typeof Services.Library.example_Resources_Runner.runner>*/)/*:Promise<string>*/{return await reference.Service.run({name:"relay"});}
});`;
    await writeFile(join(consumer.root,`src/services.${extension}`),fixtureSource(source,extension));
    const loaded=await loadProjectWithOwnedFrame(consumer.root,{contractCacheRoot:cache});await generateRpcBindingsForLoadedProject(loaded);
    assert.equal(Object.keys(loaded.ownedRpc!.contracts[0]!.modelProjection.serviceReferences).length,1);
    assert.deepEqual({...loaded.ownedRpc!.contracts[0]!.modelProjection.optionalServiceReferenceMembers},{Reply:["maybe"]});
    assert.deepEqual({...loaded.contracts.find(value=>value.descriptor.contractId==="example.resources.documents")!.modelProjection.optionalServiceReferenceMembers},{Reply:["maybe"]});
    assert.match(loaded.ownedRpc!.files.get("imported/example.resources.documents.ts")!,/SunderContent/);
    host=await runtime(producer,project,consumer.root);const api=host.consumer.api(host.api.Consumer.Library);
    const reply=await api.example_Resources_Documents.documents.get({});assert.deepEqual(await bytes(reply.content[0]),[1,2,3]);await reply.content[0].dispose();
    assert.equal(await reply.runner.Service.run({name:"imported"}),"run:imported");await host.consumer.disposeAsync();assert.throws(()=>reply.runner.Service,/retired/);
    assert.equal(await host.client.runner.run("owner"),"run:owner");
  }finally{await host?.dispose();await consumer.dispose();await producer.dispose();}
});

for(const extension of ["ts","js"] as const)test(`${extension} generated domain errors marshal natural details and retain their content for the receiving owner`,async()=>{
  const c=await fixture(extension);let host:Awaited<ReturnType<typeof runtime>>|undefined;try{
    const source=`import {defineService,defineServiceError,SunderContent} from "@sunder/sdk";import {Errors} from "#sunder/api";
/** @typedef {{body:import("@sunder/sdk").SunderContent}} Failure */
/*ts type Failure={body:SunderContent};*/
export function configure(){return {};}
export const documents=defineService({
/** @returns {Promise<string>} */
async fail()/*:Promise<string>*/{throw Errors.documents["example.failure"]({body:SunderContent.fromBytes(new Uint8Array([7,8,9]))});}
},{exposure:"public",runtimeUse:{},errors:{"example.failure":/** @type {import("@sunder/sdk").ServiceError<Failure>} */(defineServiceError/*<Failure>*/())},methods:{fail:{errors:["example.failure"]}}});`;
    await writeFile(join(c.root,`src/services.${extension}`),fixtureSource(source,extension));const project=await c.load();await generateRpcBindingsForLoadedProject(project);host=await runtime(c,project);
    assert.doesNotMatch(project.ownedRpc!.files.get("api.d.ts")!,/["']@sunder\/sdk["']/u,"Natural error factories must retain browser-safe declaration types.");
    let failure:unknown;try{await host.client.documents.fail();assert.fail("Expected declared domain failure");}catch(error){failure=error;}
    const details=host.api.Errors.documents["example.failure"].details(failure);assert.ok(details);assert.deepEqual(await bytes(details.body),[7,8,9]);
    assert.equal(host.scopes.filter(value=>!value.closed).length,1);await details.body.dispose();assert.ok(host.scopes.every(value=>value.closed));
  }finally{await host?.dispose();await c.dispose();}
});
