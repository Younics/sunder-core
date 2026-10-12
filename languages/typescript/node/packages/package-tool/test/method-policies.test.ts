import assert from "node:assert/strict";
import test from "node:test";
import {mkdtemp,mkdir,writeFile,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {loadProjectWithOwnedFrame} from "../src/model";
import {emptyLock} from "./service-fixtures";
import {methodPoliciesOuter,readMethodPolicies,parseModelProjectionDocument,projectionDocumentBytes,MODEL_PROJECTION_PATH} from "../src/rpc-model-projection";
import {parseOuter,type Outer} from "../src/rpc-baseline-json";

for(const extension of ["ts","js"] as const)test(`${extension} semantic timeouts preserve descriptor/ABI bytes, omit defaults and bind every generated evidence image`,async()=>{
  const root=await mkdtemp(join(tmpdir(),"sunder-method-policy-"));try{
    await mkdir(join(root,"src"));await writeFile(join(root,"sunder.package.json"),JSON.stringify({schemaVersion:3,id:"example.policies",name:"Policies",version:"1.0.0",workerCapabilities:[],services:{module:`src/services.${extension}`}}));
    await writeFile(join(root,"sunder-contracts.lock.json"),emptyLock);
    const source=(timeout:string)=>`import {defineService} from "@sunder/sdk";export const tasks=defineService({run(){return "ok";}},{exposure:"public",runtimeUse:{},methods:{run:{name:"execute",timeoutSeconds:${timeout}}}});`;
    const path=join(root,`src/services.${extension}`);await writeFile(path,source("0"));
    const before=await loadProjectWithOwnedFrame(root,{allowDrift:true});assert.equal(before.contracts[0]!.methodPolicies,undefined);assert.equal(before.config.workerCapabilities.includes("rpc.extended-deadline.v1"),false);
    for(const text of before.ownedRpc!.files.values())if(text.startsWith("{\n")&&text.includes('"modelProjection"'))assert.doesNotMatch(text,/"methodPolicies"/);
    await writeFile(path,source("2100"));const after=await loadProjectWithOwnedFrame(root,{allowDrift:true});
    assert.deepEqual(after.contracts[0]!.canonicalBytes,before.contracts[0]!.canonicalBytes);assert.equal(after.contracts[0]!.abiSha256,before.contracts[0]!.abiSha256);assert.notEqual(after.ownedRpc!.inputFingerprint,before.ownedRpc!.inputFingerprint);
    assert.ok(after.config.workerCapabilities.includes("rpc.extended-deadline.v1"));
    for(const name of ["package-export.v1.json","authoring-receipt.v1.json",MODEL_PROJECTION_PATH]){const entry=JSON.parse(after.ownedRpc!.files.get(name)!).contracts[0];assert.deepEqual(entry.methodPolicies,{tasks:{execute:{timeoutSeconds:2100}}});assert.equal(Object.keys(entry)[Object.keys(entry).indexOf("modelProjection")+1],"methodPolicies");}
    assert.match(after.ownedRpc!.files.get("api.ts")!,/timeoutSeconds:2100/);
    const body=Buffer.from(after.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!);const document=parseModelProjectionDocument(body);assert.deepEqual(projectionDocumentBytes(document.packageId,document.packageVersion,document.contracts),body);
    assert.throws(()=>readMethodPolicies(methodPoliciesOuter({tasks:{missing:{timeoutSeconds:30}}}).methodPolicies,after.contracts[0]!.descriptor),/absent from the exact descriptor/);
    for(const value of ["-1","2101","1.5","null",'"30"']){await writeFile(path,source(value));await assert.rejects(loadProjectWithOwnedFrame(root,{allowDrift:true}),/timeoutSeconds must be an integer/);}
  }finally{await rm(root,{recursive:true,force:true});}
});
test("policy metadata uses exact ordinal nonempty maps, descriptor member IDs and the closed 1..2100 integer range",()=>{
  for(const timeoutSeconds of [1,2100])assert.equal(readMethodPolicies(methodPoliciesOuter({service:{method:{timeoutSeconds}}}).methodPolicies)!.service!.method!.timeoutSeconds,timeoutSeconds);
  for(const value of [{},null,{service:{}},{service:{method:{timeoutSeconds:0}}},{service:{method:{timeoutSeconds:2101}}},{service:{method:{timeoutSeconds:1,unknown:true}}},{Service:{method:{timeoutSeconds:1}}}])assert.throws(()=>readMethodPolicies(value as unknown as Outer),/model-projection-invalid/);
  assert.throws(()=>readMethodPolicies(parseOuter(Buffer.from(JSON.stringify({service:{z:{timeoutSeconds:1},a:{timeoutSeconds:1}}},null,2)+"\n"))),/ordinal/);
  assert.throws(()=>parseOuter(Buffer.from('{\n  "service": {\n    "method": {\n      "timeoutSeconds": 1.0\n    }\n  }\n}\n')),/canonical safe integers/);
});
