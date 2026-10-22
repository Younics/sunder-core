import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { currentRid } from "../src/model";
import { echoSource } from "./owned-service-sources";
import { emptyLock, fixtureSource } from "./service-fixtures";
import { constructorSource, sharedSource } from "./owned-rpc-review-fixtures";
import { exerciseOwnedWorker } from "./owned-rpc-worker";
import { exercisePackedImports } from "./packed-imports";
import { exercisePackedPlatformCatalog } from "./packed-platform-catalog";

export async function exercisePackedOwned(): Promise<void> {
  const exec=promisify(execFile);const nodeRoot=resolve(__dirname,"../../../..");const tarballs=await mkdtemp(resolve(tmpdir(),"sunder-packed-owned-"));
  try {
    for(const name of ["@sunder/sdk","@sunder/package-tool"]) await exec("npm",["pack","--workspace",name,"--pack-destination",tarballs,"--ignore-scripts"],{cwd:nodeRoot});
    const archives=(await readdir(tarballs)).filter(name=>name.endsWith(".tgz")).map(name=>resolve(tarballs,name));assert.equal(archives.length,2);
    for(const extension of ["ts","js"]) {
      const root=resolve(tarballs,extension);await mkdir(resolve(root,"src"),{recursive:true});
      const input=echoSource.replace('exposure:"package"','exposure:"public"');const sourcePath=resolve(root,`src/services.${extension}`);
      await writeFile(sourcePath,fixtureSource(input,extension));await writeFile(resolve(root,"sunder-contracts.lock.json"),emptyLock);
      await writeFile(resolve(root,"sunder.package.json"),JSON.stringify({schemaVersion:3,id:"example.owned",name:"Packed",version:"7.6.5",workerCapabilities:[],services:{module:`src/services.${extension}`}}));
      await writeFile(resolve(root,"package.json"),JSON.stringify({name:"packed-owned",version:"1.0.0",private:true,imports:{"#sunder/api":"./.sunder/generated/rpc/api.js"}}));
      await exec("npm",["install","--omit=dev","--ignore-scripts","--no-audit","--no-fund",...archives],{cwd:root});
      const tool=resolve(root,"node_modules/@sunder/package-tool/dist/cli.js");const run=(...args:string[])=>exec(process.execPath,[tool,...args,"--project",root],{maxBuffer:4*1024*1024});
      const sdk=JSON.parse(await readFile(resolve(root,"node_modules/@sunder/sdk/package.json"),"utf8"));assert.deepEqual(sdk.dependencies??{},{});
      assert.equal(JSON.parse(await readFile(resolve(root,"node_modules/typescript/package.json"),"utf8")).version,"6.0.3");
      assert.equal(JSON.parse(await readFile(resolve(root,"node_modules/@types/node/package.json"),"utf8")).version,"24.13.3");
      await assert.rejects(stat(resolve(root,"node_modules/react")),{code:"ENOENT"});
      await assert.rejects(stat(resolve(root,"node_modules/@sunder/sdk/dist/authoring.js")),{code:"ENOENT"});
      await exercisePackedImports(tool,extension,nodeRoot);if(extension==="ts")await exercisePackedPlatformCatalog(tool,nodeRoot);
      await run("contracts","accept","echo","--next-version","2.0.0");await run("contracts","check");
      const baselinePath=resolve(root,"sunder-contracts.owned.json");const draft=await readFile(baselinePath);
      assert.equal(JSON.parse(draft.toString()).contracts.echo.current.contractVersion,"2.0.0");await assert.rejects(stat(resolve(root,".sunder/generated/rpc")),{code:"ENOENT"});
      await run("build");await exerciseOwnedWorker(root,false,"2.0.0");assert.deepEqual(await readFile(baselinePath),draft);
      await assert.rejects(run("package","--discovery-root",resolve(root,"dist/targets")),/frozen/);
      await run("contracts","freeze");await run("package");assert.ok((await stat(resolve(root,"dist/example.owned.7.6.5.sunderpkg"))).size>0);
      await writeFile(sourcePath,fixtureSource(input.replace('"Payload.scores[]":','"Payload.count":{maxDigits:40},"Payload.scores[]":'),extension));
      await assert.rejects(run("contracts","accept"),/acknowledgement/);
      await run("contracts","accept","echo","--current-version","2.0.0","--next-version","3.0.0","--acknowledge-breaking-change");await run("contracts","freeze");await run("build");await exerciseOwnedWorker(root,false,"3.0.0");
      await run("package","--discovery-root",resolve(root,"dist/targets"),"--leaf",resolve(root,"dist/targets",currentRid()));
      const review=resolve(root,"review");await mkdir(resolve(review,"src"),{recursive:true});await writeFile(resolve(review,"sunder-contracts.lock.json"),emptyLock);
      await writeFile(resolve(review,"sunder.package.json"),JSON.stringify({schemaVersion:3,id:"example.review",name:"Review",version:"7.6.5",workerCapabilities:[],services:{module:`src/services.${extension}`}}));
      await writeFile(resolve(review,`src/services.${extension}`),constructorSource(true));
      const reviewRun=(...args:string[])=>exec(process.execPath,[tool,...args,"--project",review]);
      await reviewRun("contracts","accept");await reviewRun("contracts","freeze");const before=await readFile(resolve(review,"sunder-contracts.owned.json"));
      await writeFile(resolve(review,`src/services.${extension}`),constructorSource(false));await assert.rejects(reviewRun("contracts","accept"),/acknowledgement/);
      for(const reverse of [false,true]){await writeFile(resolve(review,`src/services.${extension}`),fixtureSource(sharedSource(extension,reverse,false),extension));await assert.rejects(reviewRun("contracts","accept"),/handler-incomplete/);}
      assert.deepEqual(await readFile(resolve(review,"sunder-contracts.owned.json")),before);
      if(extension==="ts") {
        for(const implementation of ["@sunder/package-tool/dist/generator.js","@sunder/sdk/dist/services.js","@sunder/sdk/dist/api.js","typescript/lib/typescript.js",
          ...["service-source","service-bindings","compatibility","rpc-compiler-inputs","rpc-baseline","rpc-baseline-json","rpc-baseline-store","rpc-evolution","rpc-contracts","rpc-contract-cache-gc","rpc-owned-app","rpc-owned-projection-review","rpc-package-imports","rpc-package-selection","rpc-package-commands","cli","output"].map(name=>`@sunder/package-tool/dist/${name}.js`)]) {
          const result=await exec(process.execPath,["-e",`const tool=require(process.argv[1]),fs=require("node:fs");(async()=>{const p=process.argv[3],before=fs.readFileSync(p);try{fs.writeFileSync(p,Buffer.concat([before,Buffer.from("\\n// mutation\\n")]));await require("node:assert/strict").rejects(tool.loadProject(process.argv[2]),/producer implementation changed.*restart/);console.log("guard");}finally{fs.writeFileSync(p,before);}})().catch(e=>{console.error(e);process.exitCode=1;});`,resolve(root,"node_modules/@sunder/package-tool/dist/index.js"),root,resolve(root,"node_modules",implementation)]);
          assert.equal(result.stdout.trim(),"guard");
        }
        const result=await exec(process.execPath,["-e",`const tool=require(process.argv[1]),fs=require("node:fs"),path=require("node:path"),o=require(path.join(process.argv[1],"../output.js"));const file=path.join(process.argv[1],"../rpc-evolution.js"),before=fs.readFileSync(file),original=o.withOutputLeases;let changed=false;o.withOutputLeases=async(paths,fn,wait)=>{if(paths.some(p=>p.endsWith("sunder-contracts.owned.json"))){changed=true;fs.writeFileSync(file,Buffer.concat([before,Buffer.from("\\n// race\\n")]));}return original(paths,fn,wait);};(async()=>{try{await require("node:assert/strict").rejects(tool.aggregateAndPack(process.argv[2],[{path:process.argv[3],discoveryRoot:path.dirname(process.argv[3])}]),/producer implementation changed.*restart/);if(!changed)throw Error("no commit fence");console.log("guard");}finally{fs.writeFileSync(file,before);}})().catch(e=>{console.error(e);process.exitCode=1;});`,resolve(root,"node_modules/@sunder/package-tool/dist/index.js"),root,resolve(root,"dist/targets",currentRid())]);
        assert.equal(result.stdout.trim(),"guard");
      }
    }
  }finally{await rm(tarballs,{recursive:true,force:true});}
}
