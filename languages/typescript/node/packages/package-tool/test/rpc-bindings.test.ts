import assert from "node:assert/strict";
import { spawn, execFile, type ChildProcessWithoutNullStreams } from "node:child_process";
import { mkdtemp, mkdir, open, readFile, readdir, rm, stat, symlink, utimes, writeFile, type FileHandle } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { generateRpcBindings } from "../src/rpc-bindings";
import { currentRid, loadProject } from "../src/model";
import { markerPath, withOutputLocks } from "../src/output";
import { buildDevPackage, buildWebTarget, bundleProduction, prepareProject } from "../src/package";
import { readProjectDescriptorFile } from "../src/descriptor-files";
import { assertPendingAfterTurn, startOutputLockContender, waitForProcessOutputLockAttempt } from "./observable-barriers";
import { emptyLock } from "./service-fixtures";

const cli=resolve(__dirname,"../../dist/cli.js");const exec=promisify(execFile);
const generated=(root:string)=>resolve(root,".sunder/generated/rpc");
async function project(names=["first","second"]){const root=await mkdtemp(resolve(tmpdir(),"sunder-bindings-"));await mkdir(resolve(root,"src"));await writeFile(resolve(root,"sunder-contracts.lock.json"),emptyLock);await writeFile(resolve(root,"sunder.package.json"),JSON.stringify({schemaVersion:3,id:"test.bindings",name:"Bindings",version:"1.0.0",workerCapabilities:[],services:{module:"src/services.ts"}}));await configure(root,names);return root;}
async function configure(root:string,names:string[]){await writeFile(resolve(root,"src/services.ts"),`import {defineService} from "@sunder/sdk";\n${names.map(name=>`export const ${name}=defineService({invoke(value:string):string{return value;}},{contractId:"example.rpc.${name}"});`).join("\n")}`);}
async function config(root:string,change:(value:Record<string,unknown>)=>void){const path=resolve(root,"sunder.package.json");const value=JSON.parse(await readFile(path,"utf8"));change(value);await writeFile(path,JSON.stringify(value));}
async function exists(path:string){return stat(path).then(()=>true,()=>false);}
async function snapshot(path:string):Promise<unknown>{const rows=[];for(const name of(await readdir(path)).sort()){const file=resolve(path,name);const info=await stat(file);rows.push([name,info.isDirectory()?await snapshot(file):(await readFile(file)).toString("base64")]);}return rows;}
async function wait(condition:()=>Promise<boolean>,diagnostics:()=>string){const end=Date.now()+60_000;while(!await condition()){if(Date.now()>end)throw Error(diagnostics());await new Promise(done=>setTimeout(done,25));}}
async function close(child:ChildProcessWithoutNullStreams){if(child.exitCode!==null)return child.exitCode;return new Promise<number|null>(done=>child.once("exit",done));}
async function web(root:string){await mkdir(resolve(root,"app"));await mkdir(resolve(root,"node_modules/@sunder"),{recursive:true});await symlink(resolve(dirname(require.resolve("@sunder/sdk")),".."),resolve(root,"node_modules/@sunder/sdk"),process.platform==="win32"?"junction":"dir");await config(root,value=>{value.app={root:"app",views:[{viewId:"test.bindings.main",displayName:"Bindings",route:"/",defaultPlacement:"middle",showInHotbar:true}]};});await writeFile(resolve(root,"app/index.html"),'<!doctype html><script type="module" src="./main.ts"></script>');await writeFile(resolve(root,"app/main.ts"),'import {CONTRACT_VERSION} from "../.sunder/generated/rpc/owned/first.js";document.body.textContent=CONTRACT_VERSION;');}
test("service configuration rejects authored output paths, traversal and closed-shape extras",async()=>{
 const root=await project();try{
  const original=await readFile(resolve(root,"sunder.package.json"));
  for(const module of ["/absolute.ts","../outside.ts",".sunder/generated/rpc/worker.ts","node_modules/dependency.js","src/types.d.ts"]){await writeFile(resolve(root,"sunder.package.json"),original);await config(root,value=>{value.services={module};});await assert.rejects(loadProject(root));}
  await writeFile(resolve(root,"sunder.package.json"),original);await config(root,value=>{value.services={module:"src/services.ts",export:"router"};});await assert.rejects(loadProject(root),/unknown property/);
  await writeFile(resolve(root,"sunder.package.json"),original);await config(root,value=>{value.rpcBindings=[{path:"contracts/v1.rpc.json",outputFile:"../escape.ts"}];});await assert.rejects(loadProject(root),/unknown property 'rpcBindings'/);
 }finally{await rm(root,{recursive:true,force:true});}
});
test("outputDirectory cannot overlap owned generated output on portable filesystems",async()=>{
 const root=await project();try{for(const outputDirectory of [".sunder",".sunder/generated",".sunder/generated/rpc",".sunder/generated/rpc/nested",".SUNDER/Generated/RPC"]){await config(root,value=>{value.outputDirectory=outputDirectory;});await assert.rejects(loadProject(root),/must not equal, contain/);}await config(root,value=>{value.outputDirectory=".sunder/generated/rpc-adjacent";});await loadProject(root);}finally{await rm(root,{recursive:true,force:true});}
});
test("generated V1 bindings preserve unchanged bytes/mtimes and replace the complete source-derived set",async()=>{
 const root=await project();try{
  await generateRpcBindings(root);const paths=[generated(root),resolve(generated(root),"owned/first.ts"),resolve(generated(root),"owned/second.ts"),markerPath(generated(root))];
  const text=await readFile(paths[1]!,"utf8");assert.match(text,/CONTRACT_DESCRIPTOR_SHA256/);assert.match(text,/CONTRACT_ABI_SHA256/);assert.match(text,/createExampleRpcFirstRpcProviderRegistration/);
  const fixed=new Date("2020-01-02T03:04:05Z");for(const path of paths)await utimes(path,fixed,fixed);const before=await Promise.all(paths.map(path=>stat(path).then(value=>value.mtimeMs)));
  await generateRpcBindings(root);assert.deepEqual(await Promise.all(paths.map(path=>stat(path).then(value=>value.mtimeMs))),before);
  await configure(root,["current"]);await generateRpcBindings(root);assert.equal(await exists(paths[1]!),false);assert.equal(await exists(resolve(generated(root),"owned/current.ts")),true);
  await configure(root,[]);await generateRpcBindings(root);assert.equal(await exists(resolve(generated(root),"owned")),false);assert.equal(await exists(resolve(generated(root),"worker.js")),true);assert.equal(await exists(resolve(generated(root),"api.d.ts")),true);
 }finally{await rm(root,{recursive:true,force:true});}
});
test("planning failures, unmarked outputs, non-file inputs and symlink sources preserve prior owned/user data",async()=>{
 const root=await project();try{
  await generateRpcBindings(root);const before=await snapshot(generated(root));const source=await readFile(resolve(root,"src/services.ts"));
  await writeFile(resolve(root,"src/services.ts"),'export const broken = ;');await assert.rejects(generateRpcBindings(root));assert.deepEqual(await snapshot(generated(root)),before);await writeFile(resolve(root,"src/services.ts"),source);
  await rm(generated(root),{recursive:true});await rm(markerPath(generated(root)));await mkdir(generated(root));await writeFile(resolve(generated(root),"sentinel.txt"),"user-owned");await assert.rejects(generateRpcBindings(root),/unmarked generated/);assert.equal(await readFile(resolve(generated(root),"sentinel.txt"),"utf8"),"user-owned");
  await config(root,value=>{value.services={module:"src.ts"};});await mkdir(resolve(root,"src.ts"));await assert.rejects(loadProject(root),/regular/);
  await symlink(resolve(root,"src"),resolve(root,"linked"),process.platform==="win32"?"junction":"dir");await config(root,value=>{value.services={module:"linked/services.ts"};});await assert.rejects(loadProject(root),/symbolic links/);
 }finally{await rm(root,{recursive:true,force:true});}
});
test("bounded descriptor/evidence reader rejects a file mutated during its immutable read",async()=>{
 const root=await project();const path=resolve(root,"evidence.json");await writeFile(path,'{"version":1}\n');let prototype:{read:FileHandle["read"]}|undefined;let original:FileHandle["read"]|undefined;
 try{const handle=await open(path,"r");const identity=await handle.stat();prototype=Object.getPrototypeOf(handle) as typeof prototype;original=prototype!.read;await handle.close();let changed=false;const read=original;
 prototype!.read=(async function(this:FileHandle,...args:unknown[]){const result=await Reflect.apply(read,this,args);const stat=await this.stat();if(!changed&&stat.ino===identity.ino&&stat.dev===identity.dev){changed=true;await writeFile(path,'{"version":2,"new":true}\n');}return result;}) as FileHandle["read"];
 await assert.rejects(readProjectDescriptorFile(root,path,"evidence"),/changed while it was being read/);assert.equal(changed,true);
 }finally{if(prototype&&original)prototype.read=original;await rm(root,{recursive:true,force:true});}
});
test("concurrent publishers serialize a complete generated set and leave an identical set untouched",async()=>{
 const root=await project();try{await Promise.all(Array.from({length:4},()=>generateRpcBindings(root)));const before=await snapshot(generated(root));const time=(await stat(resolve(generated(root),"api.js"))).mtimeMs;await Promise.all(Array.from({length:4},()=>generateRpcBindings(root)));assert.deepEqual(await snapshot(generated(root)),before);assert.equal((await stat(resolve(generated(root),"api.js"))).mtimeMs,time);assert.deepEqual((await readdir(resolve(root,".sunder/generated"))).sort(),["rpc","rpc.sunder-generated-output"]);}finally{await rm(root,{recursive:true,force:true});}
});
test("a suspended CLI publisher reads the new source after acquiring the output lease",{skip:process.platform==="win32"?"SIGSTOP/SIGCONT unavailable on Windows":false},async()=>{
 const root=await project(["old"]);let child:ChildProcessWithoutNullStreams|undefined;let release:(()=>void)|undefined;let task:Promise<void>|undefined;let output="";
 try{let ready!:()=>void;const acquired=new Promise<void>(done=>ready=done);task=withOutputLocks([generated(root)],async()=>{ready();await new Promise<void>(done=>release=done);});await acquired;
 child=spawn(process.execPath,[cli,"generate","--project",root],{stdio:"pipe"});child.stdout.on("data",data=>output+=data);child.stderr.on("data",data=>output+=data);await waitForProcessOutputLockAttempt(child.pid,()=>output);
 for(let i=0;i<10;i++){child.kill("SIGSTOP");await new Promise(done=>setTimeout(done,50));if(!await exists(generated(root)+".sunder-output.lock.transition/owner.json"))break;child.kill("SIGCONT");if(i===9)throw Error("Cannot suspend outside transition");}
 await configure(root,["current"]);release!();release=undefined;await task;task=undefined;await generateRpcBindings(root);child.kill("SIGCONT");assert.equal(await close(child),0,output);child=undefined;
 assert.equal(await exists(resolve(generated(root),"owned/old.ts")),false);assert.match(await readFile(resolve(generated(root),"owned/current.ts"),"utf8"),/example.rpc.current/);
 }finally{release?.();await task?.catch(()=>{});child?.kill("SIGCONT");child?.kill("SIGKILL");await rm(root,{recursive:true,force:true});}
});
test("CLI, development, production and Vite consume current generated bindings on cold roots",async()=>{
 const root=await project(["first"]);try{await web(root);await exec(process.execPath,[cli,"generate","--project",root]);await buildDevPackage({projectPath:root,watch:false});await rm(generated(root),{recursive:true});await rm(markerPath(generated(root)));await bundleProduction(await prepareProject(root),currentRid(),resolve(root,"production/worker.cjs"));assert.equal(await exists(resolve(generated(root),"owned/first.ts")),true);await buildWebTarget(root,currentRid());assert.match(await readFile(resolve(root,"production/worker.cjs"),"utf8"),/example.rpc.first/);}finally{await rm(root,{recursive:true,force:true});}
});
test("Vite and generated output share one fenced source snapshot; waiting generation cannot publish into an active build",async()=>{
 const root=await project(["first"]);let building:Promise<unknown>|undefined;let generating:Promise<void>|undefined;const started=resolve(root,"started"),release=resolve(root,"release");
 try{await web(root);const initial=await buildWebTarget(root,currentRid());const before=await snapshot(initial!);
 await writeFile(resolve(root,"app/vite.config.mjs"),`import {access,writeFile}from"node:fs/promises";export default {plugins:[{name:"barrier",async buildStart(){await writeFile(${JSON.stringify(started)},"ready");for(;;){try{await access(${JSON.stringify(release)});break;}catch{await new Promise(done=>setTimeout(done,20));}}}}]};`);
 building=buildWebTarget(root,currentRid());void building.catch(()=>{});await wait(()=>exists(started),()=>"Vite never reached barrier");const old=await readFile(resolve(generated(root),"owned/first.ts"));await config(root,value=>{value.version="1.0.1";});
 const contender=startOutputLockContender(generated(root),()=>generateRpcBindings(root),()=>"generation did not wait");generating=contender.completion;await contender.attempting;await assertPendingAfterTurn(generating,()=>"generation escaped Vite lease");assert.deepEqual(await readFile(resolve(generated(root),"owned/first.ts")),old);
 await writeFile(release,"release");await assert.rejects(building,/changed/);building=undefined;await generating;generating=undefined;assert.deepEqual(await snapshot(initial!),before);assert.match(await readFile(resolve(generated(root),"owned/first.ts"),"utf8"),/CONTRACT_VERSION = "1.0.1"/);
 }finally{await writeFile(release,"release").catch(()=>{});await building?.catch(()=>{});await generating?.catch(()=>{});await rm(root,{recursive:true,force:true});}
});
test("watch treats service identity and package versions as structural inputs",async()=>{
 const root=await project(["first"]);let child:ChildProcessWithoutNullStreams|undefined;let output="";
 try{child=spawn(process.execPath,[cli,"dev","--watch","--project",root],{stdio:"pipe"});child.stdout.on("data",data=>output+=data);child.stderr.on("data",data=>output+=data);const bundle=resolve(root,"dist/sunder-dev/payload/runtime/shared/bin/worker.cjs");
 await wait(()=>exists(bundle),()=>output);await config(root,value=>{value.version="1.0.1";});await wait(async()=>/CONTRACT_VERSION = "1.0.1"/.test(await readFile(resolve(generated(root),"owned/first.ts"),"utf8").catch(()=>"")),()=>output);
 child.kill("SIGINT");assert.equal(await close(child),0,output);child=undefined;
 }finally{child?.kill("SIGKILL");await rm(root,{recursive:true,force:true});}
});
