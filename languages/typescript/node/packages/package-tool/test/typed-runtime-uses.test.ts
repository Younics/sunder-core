import assert from "node:assert/strict";
import {mkdtemp,mkdir,readFile,rm,writeFile} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {createHash} from "node:crypto";
import test from "node:test";
import {build} from "esbuild";
import Ajv2020 from "ajv/dist/2020";
import {loadProjectWithOwnedFrame,currentRid} from "../src/model";
import {bundleProduction,writeTargetLeaf} from "../src/package";
import {generateRpcBindingsForLoadedProject} from "../src/rpc-bindings";
import {packageApiCommand} from "../src/rpc-package-commands";
import {parseContractLock} from "../src/rpc-contract-lock";
import {preparePackageImports} from "../src/rpc-package-imports";
import {MODEL_PROJECTION_PATH} from "../src/rpc-model-projection";
import {readRoleUses,roleUsesBytes,validateRoleUseEvidence} from "../src/rpc-owned-uses";
import {parseOuter} from "../src/rpc-baseline-json";
import {canonicalCandidateFixture,installFixtureInspector} from "./model-projection-fixtures";
import {emptyLock,fixtureSource} from "./service-fixtures";

const producerSource=`import {defineService} from "@sunder/sdk";
/*ts import type {ServiceReference} from "@sunder/sdk";*/
export const leaf=defineService({ready(){return true;}},{exposure:"public",runtimeUse:{actions:["discover"]}});
/** @typedef {{leaf:import("@sunder/sdk").ServiceReference<typeof leaf>,next?:TargetContext}} TargetContext */
/*ts type TargetContext={leaf:ServiceReference<typeof leaf>;next?:TargetContext};*/
export const target=defineService({
 /** @param {TargetContext} request */
 run(request/*:TargetContext*/){return true;}
},{exposure:"public",runtimeUse:{actions:["discover"]}});
/** @typedef {{target:import("@sunder/sdk").ServiceReference<typeof target>}} HolderContext */
/*ts type HolderContext={target:ServiceReference<typeof target>};*/
export const holder=defineService({
 /** @param {HolderContext} request */
 handle(request/*:HolderContext*/){return true;}
},{exposure:"public"});
export const unrelated=defineService({ping(){return "unused";}},{exposure:"public"});`;
const targetToken="Services.Contracts.CExample_Typed_Target.target";
const leafToken="Services.Contracts.CExample_Typed_Leaf.leaf";
const consumerSource=(intent:string)=>`import {defineService} from "@sunder/sdk";import {Implementations,Services} from "#sunder/api";
export const provider=defineService({holderHandle(request){return true;}},{implements:Implementations.holder,providerId:"example.consumer.holder"${intent}});`;

async function setup(language:"ts"|"js"){
  const root=await mkdtemp(join(tmpdir(),"sunder-typed-runtime-"));const producer=join(root,"producer"),consumer=join(root,"consumer"),cache=join(root,"cache");
  for(const path of [producer,consumer])await mkdir(join(path,"src"),{recursive:true});
  await mkdir(join(consumer,"app"));await writeFile(join(consumer,"app/index.html"),"<!doctype html><title>Unrelated App</title>");
  for(const [path,id,source] of [[producer,"example.typed",fixtureSource(producerSource,language)],[consumer,"example.consumer",consumerSource("")]] as const){
    await writeFile(join(path,`src/services.${language}`),source);await writeFile(join(path,"sunder-contracts.lock.json"),emptyLock);
    await writeFile(join(path,"package.json"),JSON.stringify({type:"module",imports:{"#sunder/api":{types:"./.sunder/generated/rpc/api.d.ts",default:"./.sunder/generated/rpc/api.js"}}}));
    await writeFile(join(path,"sunder.package.json"),JSON.stringify({schemaVersion:3,id,name:"Typed use",version:"1.0.0",workerCapabilities:[],services:{module:`src/services.${language}`},
      ...(path===consumer?{app:{root:"app",entryPoint:"index.html",views:[{viewId:"example.consumer.main",displayName:"Main",route:"/",defaultPlacement:"middle",showInHotbar:true}]}}:{})}));
  }
  const project=await loadProjectWithOwnedFrame(producer,{allowDrift:true});
  const descriptors=new Map(project.contracts.map(contract=>[contract.descriptorSha256,contract.canonicalBytes]));
  const contracts=Object.fromEntries([...project.contracts].sort((a,b)=>a.descriptor.contractId<b.descriptor.contractId?-1:1).map(contract=>[contract.descriptor.contractId,{artifactSource:"owned",authorityKind:"package",authorityId:project.config.id,selectedAuthorityRelease:"1.0.0",selectedAuthorityContentIdentity:"0".repeat(64),
    resolvedContractVersion:contract.descriptor.version,descriptorBytes:contract.canonicalBytes.length,descriptorSha256:contract.descriptorSha256,abiSha256:contract.abiSha256,etag:`"${contract.descriptorSha256}"`}]));
  const candidate=await canonicalCandidateFixture(project.config.id,{requestedVersionRange:">=1.0.0 <2.0.0",includePrerelease:false,selectedPackageVersion:"1.0.0",selectedPackageContentIdentity:"0".repeat(64),publicContractCount:4,publicInventorySha256:"0".repeat(64),modelProjectionBytes:1,modelProjectionSha256:"0".repeat(64),contracts},Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!),descriptors);
  const raw={lockVersion:1,packageArtifacts:{[project.config.id]:candidate.artifact},packageImports:{},providerImports:{holder:{packageId:project.config.id,contractId:"example.typed.holder"}},platformImports:{}};
  const modern=Buffer.from(JSON.stringify(raw,null,2)+"\n");await writeFile(join(consumer,"sunder-contracts.lock.json"),modern);
  const candidatePath=join(root,"candidate.sunderpkg");await writeFile(candidatePath,candidate.bytes);const restoreInspector=await installFixtureInspector(root);
  return {root,consumer,cache,project,descriptors,candidate,modern,
    write:(intent:string)=>writeFile(join(consumer,`src/services.${language}`),consumerSource(intent)),
    update:()=>packageApiCommand(consumer,{operation:"update",packageId:project.config.id,candidate:candidatePath,cacheRoot:cache}),
    load:()=>loadProjectWithOwnedFrame(consumer,{contractCacheRoot:cache}),
    dispose:async()=>{restoreInspector();await rm(root,{recursive:true,force:true});}};
}
const discover=`,runtimeUses:[{service:${targetToken},actions:["discover"]}]`;

for(const language of ["ts","js"] as const)test(`${language} provider-only Update requires explicit typed discovery, retains pinned recursive metadata and no App/package API grant`,async()=>{
  const c=await setup(language);try{
    await assert.rejects(c.update(),/service-reference-use-missing/);assert.deepEqual(await readFile(join(c.consumer,"sunder-contracts.lock.json")),c.modern);
    await c.write(discover);const selected=await c.update();assert.equal(selected.changed,false);assert.deepEqual(selected.lock.exactBytes,c.modern);assert.equal(selected.lock.packageImports.size,0);assert.equal(selected.lock.providerImports.size,1);
    const loaded=await c.load();await generateRpcBindingsForLoadedProject(loaded);
    assert.deepEqual(loaded.contracts.map(contract=>contract.descriptor.contractId),["example.typed.holder","example.typed.leaf","example.typed.target"]);
    assert.deepEqual(loaded.config.usesContracts,[{role:"runtime",contractId:"example.typed.target",contractVersion:"1.0.0",required:false,actions:["discover"]}]);
    const target=c.project.contracts.find(contract=>contract.descriptor.contractId==="example.typed.target")!;
    const expected=[{role:"runtime",contractId:target.descriptor.contractId,contractVersion:target.descriptor.version,descriptorSha256:target.descriptorSha256,abiSha256:target.abiSha256,serviceId:"target",serviceType:"Sunder.Generated.Packages.Example_Typed_Services.CExample_Typed_Target.ITargetService",required:false,actions:["discover"]}];
    const receipt=JSON.parse(loaded.ownedRpc!.files.get("authoring-receipt.v1.json")!),exported=JSON.parse(loaded.ownedRpc!.files.get("package-export.v1.json")!),app=JSON.parse(loaded.ownedRpc!.files.get("app-authoring-receipt.v1.json")!);
    assert.deepEqual(receipt.roleUses,expected);assert.deepEqual(exported.roleUses,expected);assert.equal(app.roleUses,undefined);assert.deepEqual(app.uses,[]);assert.deepEqual(app.contracts,[]);
    assert.deepEqual(validateRoleUseEvidence(parseOuter(Buffer.from(loaded.ownedRpc!.files.get("package-export.v1.json")!)),"export",loaded.ownedRpc!.roleUses),loaded.ownedRpc!.roleUses);
    assert.deepEqual(receipt.packageApiImports,[]);assert.deepEqual(receipt.generatedPackageFacades,[]);assert.deepEqual(receipt.uses,[{role:"runtime",required:false,actions:["discover"],contractKey:target.descriptor.contractId}]);
    assert.equal(Object.keys(receipt).at(-1),"roleUses");assert.equal(Object.keys(exported).at(-1),"roleUses");
    assert.deepEqual(roleUsesBytes(readRoleUses(parseOuter(Buffer.from(loaded.ownedRpc!.files.get("authoring-receipt.v1.json")!)).roleUses)),Buffer.from(JSON.stringify({roleUses:expected},null,2)+"\n"));
    const body=await readFile(join(c.cache,"model-projections/sha256",c.candidate.artifact.modelProjectionSha256+".json"));assert.deepEqual(body,c.candidate.projection);
    assert.equal((await readFile(join(c.cache,"model-projections/sha256",c.candidate.artifact.modelProjectionSha256+".receipt.json"))).toString(),JSON.stringify({schemaVersion:1,modelProjectionSha256:createHash("sha256").update(body).digest("hex"),modelProjectionBytes:body.length},null,2)+"\n");
    for(const [hash,bytes] of c.descriptors){assert.deepEqual(await readFile(join(c.cache,hash+".json")),bytes);
      const contract=c.project.contracts.find(value=>value.descriptorSha256===hash)!;
      assert.equal((await readFile(join(c.cache,hash+".receipt.json"))).toString(),JSON.stringify({schemaVersion:1,contractId:contract.descriptor.contractId,contractVersion:contract.descriptor.version,descriptorBytes:bytes.length,descriptorSha256:hash,abiSha256:contract.abiSha256},null,2)+"\n");
    }
    const bundle=await build({entryPoints:[join(c.consumer,".sunder/generated/rpc/adapter.js")],bundle:true,platform:"node",format:"cjs",write:false,alias:{"@sunder/sdk":require.resolve("@sunder/sdk"),"@sunder/sdk/browser":require.resolve("@sunder/sdk/browser"),"#sunder/api":join(c.consumer,".sunder/generated/rpc/api.js")}});
    const module={exports:{} as {providers:readonly unknown[]}};new Function("require","module","exports",bundle.outputFiles[0]!.text)(require,module,module.exports);assert.equal(module.exports.providers.length,1);
    if(language==="ts"){
      const modulePath=join(c.consumer,"dist/work/worker.cjs"),rid=currentRid();const notice=await bundleProduction(loaded,rid,modulePath);
      const leaf=await writeTargetLeaf({project:loaded,rid,modulePath,workerNoticePath:notice,discoveryRoot:join(c.consumer,"dist/targets")});
      const manifest=JSON.parse(await readFile(join(leaf,"manifest/sunder-package.json"),"utf8"));assert.equal(manifest.roleUses,undefined);
      assert.deepEqual(manifest.packageApiImports,[]);assert.equal(manifest.usesContracts.length,1);
      assert.deepEqual(manifest.usesContracts[0],{role:"runtime",contractId:target.descriptor.contractId,contractVersion:target.descriptor.version,abiSha256:target.abiSha256,required:false,actions:["discover"]});
    }
    for(const mutate of [
      (value:any)=>delete value.roleUses,(value:any)=>value.uses[0].actions.push("invoke"),(value:any)=>value.uses[0].required=true,
      (value:any)=>value.roleUses[0].descriptorSha256="f".repeat(64),(value:any)=>value.roleUses[0].serviceType="Lookalike.ITargetService",(value:any)=>value.uses=[],
      (value:any)=>value.roleUses[0].serviceType="Sunder.Generated.Packages.Example_Typed_Services.ITargetService",
      (value:any)=>value.roleUses[0].serviceType="Sunder.Generated.Packages.Example_Typed_Services.CExample_Typed_Leaf.ITargetService",
      (value:any)=>value.roleUses[0].extra=true,(value:any)=>value.roleUses[0].role="app",
    ]){const altered=structuredClone(receipt);mutate(altered);assert.throws(()=>validateRoleUseEvidence(parseOuter(Buffer.from(JSON.stringify(altered,null,2)+"\n")),"receipt"));}
    await c.write("");await assert.rejects(c.load(),/service-reference-use-missing/);assert.deepEqual(await readFile(join(c.consumer,"sunder-contracts.lock.json")),c.modern);
  }finally{await c.dispose();}
});

test("typed Runtime targets are canonical, policy-consistent and independently explicit for newly callable reference graphs",async()=>{
  const c=await setup("ts");try{await c.write(discover);await c.update();const first=await c.load();
    for(const entry of [
      `{service:${targetToken},actions:["invoke"]}`,`{service:${targetToken},actions:["discover","invoke"]}`,
    ]){await c.write(`,runtimeUses:[${entry}]`);await assert.rejects(c.load(),/service-reference-use-missing/);}
    await c.write(`,runtimeUses:[{service:${targetToken},actions:["invoke","discover"]},{service:${leafToken},actions:["discover"]}]`);
    const callable=await c.load();assert.equal(callable.ownedRpc!.roleUses.length,2);assert.notEqual(callable.ownedRpc!.inputFingerprint,first.ownedRpc!.inputFingerprint);
    assert.deepEqual(callable.contracts.map(value=>[value.descriptorSha256,value.abiSha256]),first.contracts.map(value=>[value.descriptorSha256,value.abiSha256]));
    for(const entry of [
      `{service:${targetToken},actions:[]}`,`{service:${targetToken},actions:["subscribe"]}`,`{service:${targetToken},actions:["discover","discover"]}`,
      `{service:${targetToken},actions:["publish"]}`,`{service:null,actions:["discover"]}`,`{service:{contractId:"example.typed.target",serviceId:"target"},actions:["discover"]}`,
      `{service:Services.Contracts.CExample_Typed_Unrelated.unrelated,actions:["discover"]}`,`{service:${targetToken},required:"yes",actions:["discover"]}`,`{service:${targetToken},role:"app",actions:["discover"]}`,
      `{service:${targetToken},actions:["discover"]},{service:${targetToken},actions:["discover"]}`,
    ]){await c.write(`,runtimeUses:[${entry}]`);await assert.rejects(c.load(),/runtime-use-invalid/);}
    const root=parseContractLock(c.modern),imports=await preparePackageImports(root,c.cache,"example.consumer",[],new Set(["runtime"]));assert.deepEqual(imports.uses,[]);assert.equal(imports.contracts.length,3);
    const original=consumerSource(discover),second=original.slice(original.indexOf("export const provider")).replace("const provider=","const other=").replace("example.consumer.holder","example.consumer.other");
    const path=join(c.consumer,"src/services.ts");await writeFile(path,original+"\n"+second);
    const identical=await c.load();assert.equal(identical.ownedRpc!.roleUses.length,1);assert.equal(identical.config.providers.length,2);
    await writeFile(path,original+"\n"+second.replace('["discover"]','["invoke"]'));await assert.rejects(c.load(),/runtime-use-invalid/);
    await writeFile(path,original.replace('import {defineService}', 'import {defineService,createServiceToken}').replace('export const provider=',`const counterfeit=createServiceToken({contractId:"example.typed.target",contractVersion:"1.0.0",descriptorSha256:"${"a".repeat(64)}",abiSha256:"${"b".repeat(64)}",serviceId:"target"},()=>({}));\nexport const provider=`).replace(`service:${targetToken}`,"service:counterfeit"));
    await assert.rejects(c.load(),/runtime-use-invalid/);
    await writeFile(path,original.replace("implements:Implementations.holder,",""));await assert.rejects(c.load(),/runtime-use-invalid/);
    await c.write(`,runtimeUses:[{service:${targetToken}},{service:${leafToken},actions:["discover"]}]`);
    const defaults=await c.load();assert.deepEqual(defaults.ownedRpc!.roleUses.find(use=>use.contractId==="example.typed.target")!.actions,["discover","invoke"]);
  }finally{await c.dispose();}
});

test("shared export and receipt schemas recognize exact typed role-use evidence",async()=>{
  const root=resolve(__dirname,"../../../../../../../specs/rpc/v1/authoring");
  const schemas=await Promise.all(["authoring-receipt","package-project-export","rpc-model-projection"].map(async name=>JSON.parse(await readFile(join(root,name+".schema.json"),"utf8"))));
  const ajv=new Ajv2020({strict:true,allErrors:true,schemas});const validate=ajv.getSchema(schemas[0].$id+"#/$defs/roleUse")!;
  const evidence={role:"runtime",contractId:"example.typed.target",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64),serviceId:"target",serviceType:"Sunder.Generated.Packages.Example_Typed_Services.CExample_Typed_Target.ITargetService",required:false,actions:["discover"]};
  assert.equal(validate(evidence),true,JSON.stringify(validate.errors));assert.equal(validate({...evidence,role:"app"}),false);
  assert.equal(validate({...evidence,unexpected:true}),false);
});
