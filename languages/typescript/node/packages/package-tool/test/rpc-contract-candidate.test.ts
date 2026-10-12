import assert from "node:assert/strict";
import childProcess from "node:child_process";
import { createHash } from "node:crypto";
import { appendFile, mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { crc32, deflateRawSync } from "node:zlib";
import test, { type TestContext } from "node:test";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { inspectContractCandidate } from "../src/rpc-contract-candidate";
import { MODEL_PROJECTION_FILE, projectionDocumentBytes } from "../src/rpc-model-projection";

const hash = (bytes: Uint8Array): string => createHash("sha256").update(bytes).digest("hex");
// Only metadata retrieval is under test here. The subprocess fixture stands in
// for the canonical CLI boundary; no second archive/schema validator is introduced.
function metadataZip(bytes: Buffer, deflated: boolean, zip64: boolean): Buffer {
  const name=Buffer.from("manifest/sunder-package.json"); const data=deflated?deflateRawSync(bytes):bytes;
  const extra=zip64?Buffer.alloc(28):Buffer.alloc(0);
  if(zip64){extra.writeUInt16LE(1);extra.writeUInt16LE(24,2);extra.writeBigUInt64LE(BigInt(bytes.length),4);extra.writeBigUInt64LE(BigInt(data.length),12);extra.writeBigUInt64LE(0n,20);}
  const local=Buffer.alloc(30);local.writeUInt32LE(0x04034b50);local.writeUInt16LE(20,4);local.writeUInt16LE(8,6);local.writeUInt16LE(deflated?8:0,8);local.writeUInt16LE(name.length,26);
  const descriptor=Buffer.alloc(16);descriptor.writeUInt32LE(0x08074b50);descriptor.writeUInt32LE(crc32(bytes),4);descriptor.writeUInt32LE(data.length,8);descriptor.writeUInt32LE(bytes.length,12);
  const central=Buffer.alloc(46);central.writeUInt32LE(0x02014b50);central.writeUInt16LE(zip64?45:20,6);central.writeUInt16LE(8,8);central.writeUInt16LE(deflated?8:0,10);
  central.writeUInt32LE(crc32(bytes),16);central.writeUInt32LE(zip64?0xffffffff:data.length,20);central.writeUInt32LE(zip64?0xffffffff:bytes.length,24);
  central.writeUInt16LE(name.length,28);central.writeUInt16LE(extra.length,30);central.writeUInt32LE(zip64?0xffffffff:0,42);
  const offset=local.length+name.length+data.length+descriptor.length; const size=central.length+name.length+extra.length;
  const end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50);end.writeUInt16LE(zip64?0xffff:1,8);end.writeUInt16LE(zip64?0xffff:1,10);end.writeUInt32LE(zip64?0xffffffff:size,12);end.writeUInt32LE(zip64?0xffffffff:offset,16);
  const wide=zip64?Buffer.alloc(76):Buffer.alloc(0);
  if(zip64){wide.writeUInt32LE(0x06064b50);wide.writeBigUInt64LE(44n,4);wide.writeUInt16LE(45,12);wide.writeUInt16LE(45,14);wide.writeBigUInt64LE(1n,24);wide.writeBigUInt64LE(1n,32);
    wide.writeBigUInt64LE(BigInt(size),40);wide.writeBigUInt64LE(BigInt(offset),48);wide.writeUInt32LE(0x07064b50,56);wide.writeBigUInt64LE(BigInt(offset+size),64);wide.writeUInt32LE(1,72);}
  return Buffer.concat([local,name,data,descriptor,central,name,extra,wide,end]);
}
async function fixture(t: TestContext) {
  const root=await realpath(await mkdtemp(resolve(tmpdir(),"sunder-inspector-handoff-")));const archive=resolve(root,"foreign-csharp.sunderpkg");const recipe=resolve(root,"recipe.json");const executable=resolve(root,"inspector.cjs");
  const descriptor=parseRpcContractDescriptor(await readFile(resolve(__dirname,"../../../../../../../specs/rpc/v1/conformance/valid/minimal.rpc.json")));
  const owned={contractId:descriptor.contractId,version:"2.0.0",descriptorSha256:hash(canonicalRpcDescriptorBytes(descriptor)),abiSha256:await computeRpcAbiSha256(descriptor),exposure:"package" as const};
  const manifest=Buffer.from(JSON.stringify({id:"example.owner",version:"2.0.0",contractBundles:[{...owned,source:"owned"}]}));
   await writeFile(executable,`const fs=require("node:fs"),path=require("node:path");const recipe=JSON.parse(fs.readFileSync(process.argv[2],"utf8"));if(recipe.reject)process.exit(1);const args=process.argv.slice(3);const output=args[args.indexOf("--api-output")+1];fs.mkdirSync(output);fs.writeFileSync(path.join(output,"inspection.json"),JSON.stringify(recipe.receipt));fs.writeFileSync(path.join(output,${JSON.stringify(MODEL_PROJECTION_FILE)}),recipe.projection);process.stdout.write(JSON.stringify({schemaVersion:1,success:true,data:{schemaVersion:1,publicApiExported:true}}));`);
  const spawn=childProcess.spawn;
  const inspector=t.mock.method(childProcess,"spawn",((command:string,args:readonly string[],options:childProcess.SpawnOptions)=>{
    assert.equal(command,process.env.SUNDER_CLI??"sunder");assert.deepEqual(args.slice(0,4),["dev","package","validate",archive]);assert.equal(args.at(-1),"--json");assert.equal(options.shell,false);
    return spawn(process.execPath,[executable,recipe,...args],options);
  }) as typeof childProcess.spawn);
  const prepare=async(deflated=true,zip64=false,wrongDigest=false,reject=false)=>{
    const bytes=metadataZip(manifest,deflated,zip64);await writeFile(archive,bytes);
    const manifestSha256=wrongDigest?"f".repeat(64):hash(manifest);const contentIndexSha256="c".repeat(64);
    const inventory={schemaVersion:1,packageId:"example.owner",packageVersion:"2.0.0",packageContentIdentity:hash(Buffer.concat([Buffer.from(manifestSha256,"hex"),Buffer.from(contentIndexSha256,"hex")])),publicContractCount:0,contracts:[]};
    const projection = projectionDocumentBytes("example.owner", "2.0.0", []);
    const receipt={schemaVersion:1,archiveSha256:hash(bytes),manifestSha256,contentIndexSha256,inventory,
      publicInventorySha256:createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex"),
      modelProjectionBytes:projection.length,modelProjectionSha256:hash(projection)};
    await writeFile(recipe,JSON.stringify({receipt,reject,projection:projection.toString()}));
  };
  return {root,archive,owned,prepare,inspector,dispose:()=>rm(root,{recursive:true,force:true})};
}

test("publication retrieves private tuples only from the canonical inspector-bound stored/deflated/ZIP64 manifest",async t=>{
  const c=await fixture(t);try{
    for(const [deflated,zip64] of [[false,false],[true,false],[true,true]]){
      await c.prepare(deflated,zip64);const result=await inspectContractCandidate(c.archive,undefined,true);
      assert.deepEqual(result.ownedContracts,[c.owned]);assert.equal(result.descriptors.size,0);assert.equal(result.inventory.publicContractCount,0);await result.revalidate();
    }
    assert.equal(c.inspector.mock.callCount(),3);
  }finally{await c.dispose();}
});
test("private metadata cannot bypass canonical validation, its manifest digest, or the retained archive fence",async t=>{
  const c=await fixture(t);try{
    await c.prepare(true,false,false,true);await assert.rejects(inspectContractCandidate(c.archive,undefined,true),/canonical.*inspector rejected/i);
    await c.prepare(true,false,true);await assert.rejects(inspectContractCandidate(c.archive,undefined,true),/manifest differs.*exact digest/i);
    await c.prepare();const result=await inspectContractCandidate(c.archive,undefined,true);await appendFile(c.archive,"tampered");await assert.rejects(result.revalidate(),/archive changed/);
  }finally{await c.dispose();}
});
