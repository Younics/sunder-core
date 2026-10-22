import { build } from "esbuild";
import type { RpcProviderRegistration } from "@sunder/sdk";
import { resolve } from "node:path";
import { writeFile } from "node:fs/promises";
import { workerResolutionOptions } from "../src/rpc-runtime-inputs";

/** Shared bodies have independently checked JSDoc plus explicit TS-only annotation
 * markers. JS is authored JS, not emitted/transpiled TS or a schema translator. */
export function fixtureSource(source: string, extension: string): string {
  return extension === "ts" ? source.replace(/\/\*(:[\s\S]*?|<[\s\S]*?>)\*\//gu, "$1").replace(/\/\*ts ([\s\S]*?)\*\//gu, "$1") : source;
}
export async function writeSourceFile(path: string, value: Parameters<typeof writeFile>[1], options?: Parameters<typeof writeFile>[2]): Promise<void> {
  return writeFile(path, typeof value === "string" && path.endsWith(".ts") ? fixtureSource(value, "ts") : value, options);
}

/** Test-only execution of the same generated adapter used by Worker bootstrap.
 * Process/protocol behavior remains covered by owned-rpc-worker and packed cases. */
export async function generatedProviders(root: string): Promise<readonly RpcProviderRegistration[]> {
  const result = await build({ entryPoints: [resolve(root, ".sunder/generated/rpc/adapter.ts")], bundle: true,
    platform: "node", format: "cjs", write: false, logLevel: "silent",
    ...workerResolutionOptions,
    alias: { ...workerResolutionOptions.alias, "#sunder/api": resolve(root, ".sunder/generated/rpc/api.js") } });
  const module = { exports: {} as { providers: readonly RpcProviderRegistration[] } };
  new Function("require", "module", "exports", result.outputFiles[0]!.text)(require, module, module.exports);
  return module.exports.providers;
}
export const invocation = () => ({ signal: new AbortController().signal }) as import("@sunder/sdk").RpcInvocationContext;
export const emptyLock = JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2) + "\n";
export async function* liveCatalogWatch(_revision:number,_sequence:number,options?:{signal?:AbortSignal}):AsyncIterable<never>{
  const signal=options?.signal;if(!signal)throw new Error("Catalog test watches require cancellation.");
  await new Promise<void>(resolve=>{if(signal.aborted)resolve();else signal.addEventListener("abort",()=>resolve(),{once:true});});
}
export function catalogFeed(signal?:AbortSignal){
  const events:any[]=[];let waiting:((value:IteratorResult<any>)=>void)|undefined;let ended=false;
  const emit=(value:any)=>{if(waiting){const resolve=waiting;waiting=undefined;resolve({done:false,value});}else events.push(value);};
  const end=()=>{ended=true;waiting?.({done:true,value:undefined});waiting=undefined;};signal?.addEventListener("abort",end,{once:true});if(signal?.aborted)end();
  return {emit,stream:{[Symbol.asyncIterator](){return this;},next():Promise<IteratorResult<any>>{return ended?Promise.resolve({done:true,value:undefined}):events.length?Promise.resolve({done:false,value:events.shift()}):new Promise(resolve=>{waiting=resolve;});},async return(){end();signal?.removeEventListener("abort",end);return {done:true as const,value:undefined};}}};
}
