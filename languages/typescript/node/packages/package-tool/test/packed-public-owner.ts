import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { JSDOM } from "jsdom";
import type { JsonValue } from "@sunder/sdk";
import { currentRid } from "../src/model";
import { exerciseOwnedWorker } from "./owned-rpc-worker";

const exec = promisify(execFile);
type Use = {role:string;contractId:string;contractVersion:string;required:boolean;actions:string[]};

/** Actual packed creator/SDK/tool, checked TS or authored JS/JSX, Vite App build,
 * source-verified archive and unchanged React component calling the real Worker.
 * Only the native Host bridge is a test peer; its grants come from the manifest. */
export async function exercisePackedPublicOwner(archives: readonly string[], creator: string): Promise<void> {
  for (const language of ["ts", "js"] as const) {
    const root = await mkdtemp(resolve(tmpdir(), "sunder-packed-public-owner-")); const name = `Public ${language}`;
    try {
      await exec(process.execPath, [creator, root, "--preset", "react-node", "--language", language, "--package-id", `packed.public.${language}`, "--package-name", name]);
      await exec("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", ...archives], {cwd:root});
      const fromProject = createRequire(resolve(root, "package.json"));
      const tool = resolve(root, "node_modules/@sunder/package-tool/dist/cli.js");
      const run = (...args:string[]) => exec(process.execPath, [tool, ...args, "--project", root], {maxBuffer:4_194_304});
      const typecheck = () => exec("npm", ["run", "typecheck"], {cwd:root,maxBuffer:4_194_304});
      const appPath = resolve(root, `app/src/App.${language === "ts" ? "tsx" : "jsx"}`); const appSource = await readFile(appPath);
      const sourcePath = resolve(root, `src/services.${language}`); const source = await readFile(sourcePath, "utf8");
      assert.doesNotMatch(source, /appUse|runtimeUse/);
      await typecheck();
      const receiptPath = resolve(root, ".sunder/generated/rpc/app-authoring-receipt.v1.json");
      const previous = JSON.parse(await readFile(receiptPath, "utf8")); assert.equal(previous.uses[0].required, true);
      const publicSource = source.replace("}, {", '}, {\n  exposure: "public",'); assert.notEqual(publicSource, source);
      await writeFile(sourcePath, publicSource);
      await run("contracts", "accept"); await run("contracts", "freeze"); await typecheck();
      await run("build");
      const packageArgs = ["package", "--discovery-root", resolve(root, "dist/targets")]; await run(...packageArgs);
      const manifest = JSON.parse(await readFile(resolve(root, "dist/package/manifest/sunder-package.json"), "utf8"));
      const app = JSON.parse(await readFile(resolve(root, "dist/targets", `app-${currentRid()}`, "manifest/sunder-package.json"), "utf8"));
      const runtime = JSON.parse(await readFile(resolve(root, "dist/targets", currentRid(), "manifest/sunder-package.json"), "utf8"));
      assert.equal(manifest.targets.length, 2); assert.equal(manifest.provides[0].role, "runtime"); assert.equal(manifest.provides[0].audience, "public");
      const expected = {role:"app",contractId:`${manifest.id}.messages`,contractVersion:"1.0.0",abiSha256:previous.contracts[0].abiSha256,
        required:false,actions:["discover","invoke","subscribe"]};
      assert.deepEqual(manifest.usesContracts, [expected]); assert.deepEqual(app.usesContracts, [expected]); assert.deepEqual(runtime.usesContracts, []);
      const receipt = JSON.parse(await readFile(receiptPath, "utf8"));
      assert.deepEqual(receipt.contracts[0].appUse, {required:false,actions:expected.actions});
      for (const field of ["canonicalDescriptor", "descriptorSha256", "abiSha256"]) assert.equal(receipt.contracts[0][field], previous.contracts[0][field]);
      const descriptor = await readFile(resolve(root, "dist/package/payload/shared", manifest.contractBundles[0].descriptorPath));
      assert.equal(descriptor.toString(), previous.contracts[0].canonicalDescriptor);
      assert.deepEqual(await readFile(appPath), appSource, "Making the service public must not rewrite its UI.");
      await exerciseOwnedWorker(root, false, "1.0.0", {packageName:name,app:true}, undefined, async invokeUnary => {
        const {build}: typeof import("esbuild") = fromProject("esbuild");
        const bundled = await build({stdin:{contents:`
import {createElement, StrictMode} from "react";
import {createRoot} from "react-dom/client";
import {createBrowserContext} from "@sunder/sdk/browser";
import {ApiProvider} from "@sunder/sdk/react";
import {Self} from "#sunder/api";
import {App} from ${JSON.stringify(appPath)};
const context=createBrowserContext(Self),root=createRoot(document.getElementById("root"));
root.render(createElement(StrictMode,null,createElement(ApiProvider,{context},createElement(App))));
globalThis.teardown=()=>{root.unmount();context.dispose();};
`,resolveDir:root,loader:"js"},bundle:true,platform:"browser",format:"iife",jsx:"automatic",write:false,
          define:{"process.env.NODE_ENV":'"production"'},metafile:true});
        assert.ok(Object.keys(bundled.metafile!.inputs).every(path=>!path.includes("/src/services.")&&!path.includes("node:")));
        const dom = new JSDOM('<!doctype html><div id="root"></div>', {runScripts:"outside-only",url:"https://package.invalid/"});
        const lifetime = new dom.window.AbortController(); let dispatched = 0;
        const allow = (role:string,action:string) => {
          if (!(manifest.usesContracts as Use[]).some(use=>use.role===role&&use.contractId===expected.contractId&&use.actions.includes(action)))
            throw new Error("rpc.permission-denied");
        };
        const provider = {...manifest.provides[0],packageId:manifest.id,packageVersion:manifest.version,state:"active",providerHandle:"packed-owner",catalogRevision:1,faultCode:null};
        const rpc = {
          async discover(id:string) {allow("app","discover");assert.equal(id,expected.contractId);return {revision:1,sequence:1,resetRequired:false,providers:[provider]};},
          async invoke(target:string,service:string,method:string,request:JsonValue,options?:{signal?:AbortSignal}) {
            allow("app","invoke");options?.signal?.throwIfAborted();assert.equal(target,provider.providerHandle);assert.equal(service,"messages");dispatched++;
            return invokeUnary(method,request);
          },
          async *subscribe() {throw new Error("Unexpected UI stream");},
        };
        Object.defineProperty(dom.window,"sunder",{value:Object.freeze({protocol:"sunder.bridge.v1",version:1,signal:lifetime.signal,rpc}),writable:false,configurable:false});
        let observer: MutationObserver | undefined; let timer: ReturnType<typeof setTimeout> | undefined;
        try {
          const expectedText=`Hello from ${name}: Sunder App`;
          const rendered=new Promise<void>((done,reject)=>{
            observer=new dom.window.MutationObserver(()=>{if(dom.window.document.body.textContent?.includes(expectedText))done();});
            observer.observe(dom.window.document.body,{childList:true,subtree:true,characterData:true});
            timer=setTimeout(()=>reject(new Error(`Public owner UI did not receive its Worker response: ${dom.window.document.body.textContent}`)),10_000);
          });
          dom.window.eval(bundled.outputFiles[0]!.text); await rendered; assert.equal(dispatched,1);
          assert.throws(()=>allow("runtime","discover"),/permission-denied/);
        } finally {
          observer?.disconnect(); if(timer)clearTimeout(timer);
          (dom.window as unknown as {teardown?:()=>void}).teardown?.(); lifetime.abort(); dom.window.close();
        }
      });
      const archivePath = resolve(root, `dist/${manifest.id}.1.0.0.sunderpkg`); const archive = await readFile(archivePath);
      const baselinePath = resolve(root,"sunder-contracts.owned.json"); const frozen = await readFile(baselinePath);
      await writeFile(sourcePath, publicSource.replace('exposure: "public",','exposure: "public", appUse: {actions:["discover"]},'));
      await assert.rejects(run(...packageArgs), /match|source|leaf/i);
      assert.deepEqual(await readFile(archivePath),archive); assert.deepEqual(await readFile(baselinePath),frozen);
    } finally { await rm(root, {recursive:true,force:true}); }
  }
}
