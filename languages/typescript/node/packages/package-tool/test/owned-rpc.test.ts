import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, stat, readdir, open, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, dirname, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import ts from "typescript";
import { build as esbuild } from "esbuild";
import { currentRid, loadProject } from "../src/model";
import { generateRpcBindings, generateRpcBindingsForLoadedProject, rpcBindingsOutputRoot } from "../src/rpc-bindings";
import { bundleProduction, prepareProject } from "../src/package";
import { withOutputLocks } from "../src/output";
import { contractsCommand } from "../src/rpc-contracts";
import { numericSource, echoSource } from "./owned-service-sources";
import { emptyLock, fixtureSource, generatedProviders, invocation, writeSourceFile as writeFile } from "./service-fixtures";
import { exerciseOwnedWorker } from "./owned-rpc-worker";
import { exercisePackedOwned } from "./packed-owned";
import { workerResolutionOptions } from "../src/rpc-runtime-inputs";
import { MODEL_PROJECTION_PATH } from "../src/rpc-model-projection";

const exec = promisify(execFile); const cli = resolve(__dirname, "../../dist/cli.js");
async function project(source = numericSource, extension = "ts", id = "example.owned") {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-owned-")); await mkdir(resolve(root, "src"));
  await writeFile(resolve(root, `src/services.${extension}`), source);
  await writeFile(resolve(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({schemaVersion:3,id,name:"Owned",version:"7.6.5",workerCapabilities:[],services:{module:`src/services.${extension}`}}));
  return root;
}
const run = (root: string, ...args: string[]) => exec(process.execPath,[cli,...args,"--project",root],{maxBuffer:4*1024*1024});
async function numeric(root: string, value: unknown = {choice:2,ratio:0.5,zero:0}) {
  const provider=(await generatedProviders(root))[0]!;
  return provider.handler.invokeUnary(invocation(),"echo","run",value as never);
}
for(const extension of ["ts","js"]) test(`${extension} source identities, schema annotations, literals and runtime codecs preserve exact semantics`,async()=>{
  const root=await project(numericSource,extension);
  try {
    await generateRpcBindings(root); const ordinary=(await loadProject(root)).contracts[0]!;
    assert.deepEqual(await numeric(root),{choice:2,ratio:0.5,zero:0});
    await writeFile(resolve(root,`src/services.${extension}`),numericSource.replace('exposure:"package"','exposure:"package",includeSchemaDialect:true'));
    const annotated=(await loadProject(root)).contracts[0]!;
    assert.notEqual(annotated.descriptorSha256,ordinary.descriptorSha256); assert.equal(annotated.abiSha256,ordinary.abiSha256);
    await writeFile(resolve(root,`src/services.${extension}`),numericSource.replace('exposure:"package"','exposure:"package",contractId:"legacy.messages"'));
    assert.equal((await loadProject(root)).contracts[0]!.descriptor.contractId,"legacy.messages");
    const receipt=await readFile(resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json"));
    for(const invalid of ['Legacy.Messages','legacy..messages','legacy/messages']) {
      await writeFile(resolve(root,`src/services.${extension}`),numericSource.replace('exposure:"package"',`exposure:"package",contractId:${JSON.stringify(invalid)}`));
      await assert.rejects(generateRpcBindings(root),/rpc-authoring/);
    }
    await writeFile(resolve(root,`src/services.${extension}`),numericSource.replace('return { ...request };','return { ...request, ratio: 0.25 };'));
    await assert.rejects(generateRpcBindings(root),/handler-incomplete/);
    assert.deepEqual(await readFile(resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json")),receipt);
  } finally {await rm(root,{recursive:true,force:true});}
});
for(const extension of ["ts","js"]) test(`${extension} inferred raw JSON object is explicit, not an untyped fallback`,async()=>{
  const source=`import {defineService} from "@sunder/sdk";
export const echo=defineService({
/** @param {import("@sunder/sdk").JsonObject} request @returns {import("@sunder/sdk").JsonObject} */
run(request/*:import("@sunder/sdk").JsonObject*/)/*:import("@sunder/sdk").JsonObject*/{return request;}});`;
  const root=await project(source,extension);
  try {
    await generateRpcBindings(root); const descriptor=(await loadProject(root)).contracts[0]!.descriptor;
    assert.equal(descriptor.$defs.EchoRunResult!.type,"object");
    const value={request:{arbitrary:[1,null,{nested:false}],text:"漢字"}};
    assert.deepEqual(await numeric(root,value),value.request);
    for(const invalid of [[],"text",42,null]) await assert.rejects(async()=>numeric(root,{request:invalid}));
    await writeFile(resolve(root,`src/services.${extension}`),source.replace("return request;","return [];"));
    await assert.rejects(generateRpcBindings(root),/handler-incomplete/);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("metadata is closed, non-executing, symbol-resolved, and mutation/escape failures preserve output",async()=>{
  const root=await project(numericSource.replace('{defineService}','{defineService as service}').replace('=defineService(', '=service('));
  try {
    await generateRpcBindings(root); const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json"); const prior=await readFile(path);
    const variants=[
      ...['(()=>{throw new Error("DO NOT RUN");})()','process.env.EXPOSURE','true?"package":"public"','import("./side-effect.js")'].map(value=>numericSource.replace('exposure:"package"',`exposure:${value}`)),
      numericSource.replace('exposure:"package"','["exposure"]:"package"'),
      numericSource.replace('exposure:"package"','get exposure(){return "package";}'),
      numericSource.replace('exposure:"package"','...{exposure:"package"}'),
      numericSource.replace('export const echo=', 'const facets={maxLength:5}; facets.maxLength=10; export const echo=').replace('exposure:"package"','exposure:"package",constraints:{"Payload.choice":facets}'),
      numericSource.replace('export const echo=', 'const policy={required:false}; Object.assign(policy,{required:true}); export const echo=').replace('exposure:"package"','exposure:"package",runtimeUse:policy'),
      numericSource.replace('export const echo=', 'const policy={required:false}; const box={policy}; Reflect.set(box.policy,"required",true); export const echo=').replace('exposure:"package"','exposure:"package",runtimeUse:policy'),
      numericSource.replace('defineService({','defineService({...{'),
      numericSource.replace('type Payload=', 'type JsonValue=').replaceAll('Payload','JsonValue'),
    ];
    for(const source of variants){await writeFile(resolve(root,"src/services.ts"),source); await assert.rejects(generateRpcBindings(root)); assert.deepEqual(await readFile(path),prior);}
    await writeFile(resolve(root,"src/side-effect.js"),'throw new Error("MUST NOT EXECUTE IMPORT"); export const value=1;');
    await writeFile(resolve(root,"src/services.ts"),`throw new Error("MUST NOT EXECUTE SOURCE"); import {value} from "./side-effect.js"; void value;\n${numericSource}`);
    await generateRpcBindings(root);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("runtime declarations freeze method inventory and semantic options without freezing implementation state",async()=>{
  const source=numericSource.replace('export const echo=', 'const state={calls:0}; export const echo=').replace('return { ...request };','state.calls++; return { ...request };');
  const root=await project(source);
  try {
    await generateRpcBindings(root); assert.deepEqual(await numeric(root),{choice:2,ratio:0.5,zero:0});
    for(const mutation of ['Object.assign(echo.options,{providerId:"example.other"});','Object.assign(echo.methods,{extra(){return {};}});','Object.assign(echo.methods,{run(){throw new Error("replacement");}});']) {
      await writeFile(resolve(root,"src/services.ts"),source+mutation);
      // Execute the already-generated adapter against modified runtime source;
      // real builds also fence these physical inputs before publication.
      await assert.rejects(generatedProviders(root),/read only|not extensible|metadata|frozen|Cannot assign/);
    }
  }finally{await rm(root,{recursive:true,force:true});}
});
for(const extension of ["ts","js"]) test(`${extension} cold generated helper aliases and source edits use current overlays only`,async()=>{
  const text=`import {identity} from "./helper.js";\n${echoSource.replace('context.signal.throwIfAborted(); if(typeof','context.signal.throwIfAborted(); if(identity()!=="example.owned.echo")throw new Error("stale alias"); if(typeof')}`;
  const root=await project(text,extension);
  try {
    await writeFile(resolve(root,"package.json"),JSON.stringify({type:"module",imports:{"#generated":{module:"./wrong.js",node:"./.sunder/generated/rpc/owned/echo.js",default:"./wrong.js"}}}));
    await writeFile(resolve(root,`src/helper.${extension}`),'import {CONTRACT_ID} from "#generated"; export function identity(){return CONTRACT_ID;}');
    await run(root,"build"); await exerciseOwnedWorker(root,false,"7.6.5");
    const before=(await loadProject(root)).contracts[0]!.descriptorSha256;
    await writeFile(resolve(root,`src/services.${extension}`),text.replace('"Payload.scores[]":','"Payload.count":{maxDigits:40},"Payload.scores[]":'));
    await run(root,"build"); assert.notEqual((await loadProject(root)).contracts[0]!.descriptorSha256,before);
    await exerciseOwnedWorker(root,false,"7.6.5");
    await writeFile(resolve(root,".sunder/generated/rpc/stale.ts"),'export const stale=true;');
    await writeFile(resolve(root,`src/helper.${extension}`),'import {stale} from "../.sunder/generated/rpc/stale.js"; export function identity(){return stale;}');
    await assert.rejects(generateRpcBindings(root),/current generated|Cannot find module/);
  }finally{await rm(root,{recursive:true,force:true});}
});
for(const extension of ["ts","d.ts"]) test(`compiler bounds ${extension} sparse source before unbounded read`,async()=>{
  const root=await project(`import {value} from "./huge.js"; void value;\n${numericSource}`); const path=resolve(root,`src/huge.${extension}`); const original=ts.sys.readFile; let unsafe=0;
  try{
    const file=await open(path,"w"); await file.truncate(100*1024*1024);await file.close();
    ts.sys.readFile=(name,encoding)=>{if(resolve(name)===path){unsafe++;throw new Error("unbounded");}return original(name,encoding);};
    await assert.rejects(loadProject(root),/source-limit-exceeded/);assert.equal(unsafe,0);
  }finally{ts.sys.readFile=original;await rm(root,{recursive:true,force:true});}
});
test("normalized source budget is exactly 4 MiB and dependency declarations are bounded before reads",async()=>{
  const text=fixtureSource(`import {a} from "./a.js"; import {b} from "./b.js"; void [a,b];\n${numericSource}`,"ts"); const root=await project(text);
  try{
    const a='export const a=1;\n/*'+"x".repeat(1024*1024)+'*/\n'; const prefix='export const b=2;\n/*';const suffix='*/\n';
    const b=prefix+"y".repeat(4_194_304-Buffer.byteLength(text+a+prefix+suffix))+suffix;
    await writeFile(resolve(root,"src/a.ts"),a.replaceAll("\n","\r\n"));await writeFile(resolve(root,"src/b.ts"),b.replaceAll("\n","\r\n"));await generateRpcBindings(root);
    const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json");const previous=await readFile(path);
    await writeFile(resolve(root,"src/b.ts"),b+" ");await assert.rejects(generateRpcBindings(root),/source-limit-exceeded/);assert.deepEqual(await readFile(path),previous);
    await mkdir(resolve(root,"node_modules/large"),{recursive:true});await writeFile(resolve(root,"node_modules/large/package.json"),'{"name":"large","version":"1.0.0","types":"index.d.ts"}');
    const file=await open(resolve(root,"node_modules/large/index.d.ts"),"w");await file.truncate(100*1024*1024);await file.close();
    await writeFile(resolve(root,"src/services.ts"),`import {value} from "large"; void value;\n${numericSource}`);await assert.rejects(loadProject(root),/source-limit-exceeded/);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("transitive source symlink files and ancestors fail before publication",async()=>{
  const root=await project(`import {value} from "./linked.js";void value;\n${numericSource}`); const outside=await mkdtemp(resolve(tmpdir(),"sunder-source-link-"));
  try{
    await writeFile(resolve(outside,"helper.ts"),'export const value=1;');await symlink(resolve(outside,"helper.ts"),resolve(root,"src/linked.ts"),"file");await assert.rejects(loadProject(root),/symbolic links|non-link file/);
    await rm(resolve(root,"src/linked.ts"));await symlink(outside,resolve(root,"src/linked"),process.platform==="win32"?"junction":"dir");
    await writeFile(resolve(root,"src/services.ts"),`import {value} from "./linked/helper.js";void value;\n${numericSource}`);await assert.rejects(loadProject(root),/symbolic links|non-link file/);
  }finally{await rm(root,{recursive:true,force:true});await rm(outside,{recursive:true,force:true});}
});
for(const extension of ["ts","js"]) test(`${extension} Node import conditions select the checked implementation rather than module/default`,async()=>{
  const root=await project(`import {value} from "#impl";\n${numericSource.replace('return { ...request };','return { ...request, choice:value() };')}`,extension);
  try{
    await writeFile(resolve(root,"package.json"),JSON.stringify({type:"module",imports:{"#impl":{module:"./src/wrong.js",node:"./src/node.js",default:"./src/fallback.js"}}}));
    await writeFile(resolve(root,"src/wrong.js"),'export function value(){return "wrong module";}');await writeFile(resolve(root,"src/node.js"),'export function value(){return "wrong node";}');await writeFile(resolve(root,"src/fallback.js"),'/** @returns {1|2} */ export function value(){return 2;}');
    await assert.rejects(loadProject(root),/handler-incomplete/);
    await writeFile(resolve(root,"src/node.js"),'/** @returns {1|2} */ export function value(){return 2;}');await generateRpcBindings(root);assert.deepEqual(await numeric(root),{choice:2,ratio:0.5,zero:0});
  }finally{await rm(root,{recursive:true,force:true});}
});
test("declaration-shadowed runtime bytes and newly preferred resolution candidates invalidate loaded plans",async()=>{
  const root=await project(`import {value} from "./helper.js";\n${numericSource.replace('return { ...request };','return { ...request, choice:value() };')}`);
  try{
    await writeFile(resolve(root,"src/helper.d.ts"),'export declare function value():1|2;');await writeFile(resolve(root,"src/helper.js"),'/** @returns {1|2} */ export function value(){return 1;}');
    await generateRpcBindings(root);const loaded=await prepareProject(root);const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json");const before=await readFile(path);
    await writeFile(resolve(root,"src/helper.js"),'/** @returns {1|2} */ export function value(){return 2;}');
    await assert.rejects(generateRpcBindingsForLoadedProject(loaded),/input changed/);await assert.rejects(bundleProduction(loaded,currentRid(),resolve(root,"worker.cjs")),/input changed/);assert.deepEqual(await readFile(path),before);
    await writeFile(resolve(root,"src/helper.js"),'/** @returns {1|2} */ export function value(){return 1;}');
    const snapshot=await loadProject(root);await writeFile(resolve(root,"src/helper.ts"),'export function value():1|2{return 2;}');await assert.rejects(generateRpcBindingsForLoadedProject(snapshot),/changed/);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("runtime-only package resolution and package metadata changes cannot replace previous output",async()=>{
  const root=await project(`import {value} from "shadow";\n${numericSource.replace('return { ...request };','return { ...request, choice:value() };')}`);
  try{
    const dep=resolve(root,"node_modules/shadow");await mkdir(dep,{recursive:true});const config='{"name":"shadow","version":"1.0.0","types":"index.d.ts","main":"index.js"}';
    await writeFile(resolve(dep,"package.json"),config);await writeFile(resolve(dep,"index.d.ts"),'export declare function value():1|2;');await writeFile(resolve(dep,"index.js"),'const data=require("./choice");exports.value=()=>data.choice;');await writeFile(resolve(dep,"choice.json"),'{"choice":1}');
    await generateRpcBindings(root);const loaded=await loadProject(root);const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json");const before=await readFile(path);
    await writeFile(resolve(dep,"choice.js"),'exports.choice=2;');await assert.rejects(generateRpcBindingsForLoadedProject(loaded),/runtime resolution input set changed/);await rm(resolve(dep,"choice.js"));
    await writeFile(resolve(dep,"package.json"),config.replace("1.0.0","1.0.1"));await assert.rejects(generateRpcBindingsForLoadedProject(loaded),/input changed|configuration changed/);assert.deepEqual(await readFile(path),before);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("runtime replay executes captured helper bytes even with validation deliberately isolated",async()=>{
  const input=`import {value} from "./helper.js";\n${echoSource.replace('return request;','return {...request,flag:value()};')}`;
  const root=await project(input);
  try{
    await writeFile(resolve(root,"src/helper.d.ts"),'export declare function value():boolean;');
    await writeFile(resolve(root,"src/helper.js"),'export function value(){return false;}');
    await run(root,"build");const loaded=await loadProject(root);
    await writeFile(resolve(root,"src/helper.js"),'export function value(){return true;}');
    await assert.rejects(loaded.ownedRpc!.revalidate(),/input changed/);
    const capture=await esbuild({absWorkingDir:root,entryPoints:[resolve(root,loaded.config.entry)],bundle:true,platform:"node",format:"cjs",target:"node24",...workerResolutionOptions,tsconfigRaw:{},write:false,plugins:[loaded.ownedRpc!.runtime.plugin(async()=>{})]});
    const module=resolve(root,"captured.cjs");await writeFile(module,capture.outputFiles[0]!.contents);
    await exerciseOwnedWorker(root,false,"7.6.5",undefined,module);
  }finally{await rm(root,{recursive:true,force:true});}
});
for(const extension of ["ts","js"]) test(`${extension} hoisted literal tuple type information stays narrow without becoming runtime schema`,async()=>{
  const tuples='/** @type {readonly [2,1]} */\nconst choices/*:readonly [2,1]*/=[2,1];\n/** @type {readonly ["ready","queued"]} */\nconst statuses/*:readonly ["ready","queued"]*/=["ready","queued"];\n';
  const source=numericSource.replace('/** @typedef',tuples+'/** @typedef').replaceAll('choice:1|2','choice:typeof choices[number]').replaceAll('zero:0}', 'zero:0,status:typeof statuses[number]}');
  const root=await project(source,extension);
  try{await generateRpcBindings(root);const value={choice:2,ratio:0.5,zero:0,status:"ready"};assert.deepEqual(await numeric(root,value),value);
    const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json");const before=await readFile(path);
    for(const invalid of [source.replace('return { ...request };','return {...request,status:"unknown"};'),source+'\nchoices.push(3);']){
      await writeFile(resolve(root,`src/services.${extension}`),invalid);await assert.rejects(generateRpcBindings(root),/handler-incomplete/);assert.deepEqual(await readFile(path),before);
    }
  }finally{await rm(root,{recursive:true,force:true});}
});
test("native package exports into owned generated output are overlays on cold and warm builds",async()=>{
 const source=`import {identity} from "owned-export/generated"; void identity;\n${numericSource}`;const root=await project(source);
 try{await writeFile(resolve(root,"package.json"),JSON.stringify({name:"owned-export",type:"module",exports:{"./generated":"./.sunder/generated/rpc/owned/echo.js"}}));
   await writeFile(resolve(root,"src/services.ts"),source.replace('{identity}','{CONTRACT_ID as identity}'));
   await generateRpcBindings(root);const first=(await loadProject(root)).contracts[0]!.descriptorSha256;
   await writeFile(resolve(root,"src/services.ts"),source.replace('{identity}','{CONTRACT_ID as identity}').replace('exposure:"package"','exposure:"package",includeSchemaDialect:true'));
   await generateRpcBindings(root);assert.notEqual((await loadProject(root)).contracts[0]!.descriptorSha256,first);
 }finally{await rm(root,{recursive:true,force:true});}
});
for(const extension of ["ts","js"]) test(`${extension} generated Worker executes unary/stream/cancellation/domain/fault/resource boundaries`,async()=>{
  const root=await project(echoSource,extension);
  try{
    await run(root,"build");await exerciseOwnedWorker(root,false,"7.6.5");await run(root,"dev");await exerciseOwnedWorker(root,true,"7.6.5");
    const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json");const before=await readFile(path);const time=(await stat(path)).mtimeMs;await run(root,"generate");assert.equal((await stat(path)).mtimeMs,time);
    const browser=await esbuild({entryPoints:[resolve(root,".sunder/generated/rpc/api.js")],bundle:true,platform:"browser",write:false,alias:{"@sunder/sdk/browser":require.resolve("@sunder/sdk/browser")}});assert.doesNotMatch(browser.outputFiles[0]!.text,/node:fs|runWorker/);
    await writeFile(resolve(root,`src/services.${extension}`),echoSource.replace('return request;','return {...request,count:"wrong"};'));await assert.rejects(generateRpcBindings(root),/handler-incomplete/);assert.deepEqual(await readFile(path),before);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("configuration, source and input membership are fenced under output leases and immediately before publication",async()=>{
  const root=await project();
  try{
    await generateRpcBindings(root);const loaded=await loadProject(root);const path=resolve(root,".sunder/generated/rpc/authoring-receipt.v1.json");const before=await readFile(path);let pending:Promise<void>|undefined;
    await withOutputLocks([rpcBindingsOutputRoot(root)],async()=>{pending=generateRpcBindingsForLoadedProject(loaded);await writeFile(resolve(root,"sunder.package.json"),(await readFile(resolve(root,"sunder.package.json"),"utf8")).replace("7.6.5","7.6.6"));});await assert.rejects(pending!,/changed/);assert.deepEqual(await readFile(path),before);
    await writeFile(resolve(root,"sunder.package.json"),(await readFile(resolve(root,"sunder.package.json"),"utf8")).replace("7.6.6","7.6.5"));
    const plan=await loadProject(root);const validate=plan.ownedRpc!.revalidate;let calls=0;
    const changed={...plan,ownedRpc:{...plan.ownedRpc!,revalidate:async()=>{if(++calls===2)await writeFile(resolve(root,"src/services.ts"),numericSource+"\n// changed\n");await validate();}}};
    await writeFile(resolve(root,".sunder/generated/rpc/api.d.ts"),'// stale\n');await assert.rejects(generateRpcBindingsForLoadedProject(changed),/changed/);assert.deepEqual(await readFile(path),before);
    assert.equal((await readdir(resolve(root,".sunder/generated"))).some(name=>/stage-|backup-/u.test(name)),false);
  }finally{await rm(root,{recursive:true,force:true});}
});
test("closed configuration rejects obsolete authoring, malformed evidence and fallback while non-service packages need no lock",async()=>{
  const root=await project();
  try{
    for(const lock of ["{}\n",emptyLock.replace('"platformImports": {}','"platformImports": null')]){await writeFile(resolve(root,"sunder-contracts.lock.json"),lock);await assert.rejects(generateRpcBindings(root),/rpc-authoring/);}
    await writeFile(resolve(root,"sunder-contracts.lock.json"),emptyLock);await writeFile(resolve(root,"sunder-contracts.owned.json"),'{"schemaVersion":1}\n');await assert.rejects(generateRpcBindings(root),/baseline/);await rm(resolve(root,"sunder-contracts.owned.json"));
    const path=resolve(root,"sunder.package.json");const config=JSON.parse(await readFile(path,"utf8"));
    for(const name of ["rpc","contracts","providers","usesContracts","rpcBindings","platformImports"]){await writeFile(path,JSON.stringify({...config,[name]:[]}));await assert.rejects(loadProject(root),/unknown property/);}
    await writeFile(path,JSON.stringify({...config,schemaVersion:2}));await assert.rejects(loadProject(root),/retired/);
    delete config.services;config.entry="src/worker.ts";await writeFile(path,JSON.stringify(config));await writeFile(resolve(root,"src/worker.ts"),'export {};');await assert.rejects(loadProject(root),/fallback/);
    await rm(resolve(root,"sunder-contracts.lock.json"));await generateRpcBindings(root);
    assert.deepEqual(await readdir(rpcBindingsOutputRoot(root)),[dirname(MODEL_PROJECTION_PATH)]);
    assert.deepEqual(await readdir(resolve(rpcBindingsOutputRoot(root),dirname(MODEL_PROJECTION_PATH))),[basename(MODEL_PROJECTION_PATH)]);
    assert.deepEqual(JSON.parse(await readFile(resolve(rpcBindingsOutputRoot(root),MODEL_PROJECTION_PATH),"utf8")),{schemaVersion:1,packageId:config.id,packageVersion:config.version,contracts:[]});
    for(const name of ["sunder-contracts.lock.json","sunder-contracts.owned.json"])await assert.rejects(stat(resolve(root,name)),{code:"ENOENT"});
  }finally{await rm(root,{recursive:true,force:true});}
});
test("bounded source/semantic graphs reject byte, node, reference, service and provider overflow",async()=>{
  const root=await project();
  try{
    const variants=[numericSource+`/*${"x".repeat(4_194_304)}*/`,
      `import {defineService} from "@sunder/sdk";\n${Array.from({length:66},(_,i)=>`const a${i}=${i?`a${i-1}`:'{maxLength:5}'};`).join("\n")}\nexport const echo=defineService({run(value:string){return value;}},{constraints:{"run.value":a65}});`,
      numericSource+Array.from({length:24_000},(_,i)=>`const v${i}=${i};`).join("\n"),
      `import {defineService} from "@sunder/sdk"; ${Array.from({length:129},(_,i)=>`export const s${i}=defineService({run(){return true;}});`).join("\n")}`,
      `import {defineService} from "@sunder/sdk"; export const contract=defineService<{run():boolean}>(null,{declarationOnly:true}); ${Array.from({length:129},(_,i)=>`export const p${i}=defineService({run(){return true;}},{implements:contract,providerId:"example.p${i}"});`).join("\n")}`];
    for(const source of variants){await writeFile(resolve(root,"src/services.ts"),source);await assert.rejects(generateRpcBindings(root),/source-limit-exceeded/);}
  }finally{await rm(root,{recursive:true,force:true});}
});
// The seven retired DSL source tuples and C#-only naming controls are historical
// oracles owned by sdk/test/authoring-parity.test.ts, not positive producer inputs.
// Current source parity and actual codecs are exercised above and by the frozen
// runtime-backed public fixture in service-authoring.test.ts.
test("packed production-only SDK/tool closure, public review, imports and producer fences", {skip:process.env.SUNDER_TEST_PACKED_CLOSURE!=="1",timeout:1_800_000},()=>exercisePackedOwned());
