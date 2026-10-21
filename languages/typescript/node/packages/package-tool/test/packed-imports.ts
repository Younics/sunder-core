import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { promisify } from "node:util";
import { exerciseServiceApiScopes } from "./service-api-fixtures";

const exec = promisify(execFile);

/** Only the installed npm tool/SDK execute the imported TS/JS build and cache path. */
export async function exercisePackedImports(tool: string, extension: string, nodeRoot: string): Promise<void> {
  assert.ok(extension === "ts" || extension === "js");
  await exerciseServiceApiScopes(dirname(tool), extension);
  const root = await mkdtemp(resolve(tmpdir(), "sunder-packed-imports-"));
  try {
    await mkdir(resolve(root, "src"));
    const corpus = resolve(nodeRoot, "../../../specs/rpc/v1");
    const value = JSON.parse(await readFile(resolve(corpus, "authoring/conformance/valid/package-api-contract-lock.json"), "utf8"));
    value.packageImports["example.dependency"].presence = "optional"; value.packageImports["example.dependency"].requiredContracts = [];
    value.providerImports = { extension: { packageId: "example.dependency", contractId: "example.rpc.v1.minimal" } };
    const locked = JSON.stringify(value, null, 2) + "\n"; await writeFile(resolve(root, "sunder-contracts.lock.json"), locked);
    await writeFile(resolve(root, "sunder-contracts.owned.json"), await readFile(resolve(corpus, "authoring/conformance/valid/minimal-package-owned-baseline.json")));
    await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Packed Imports", version: "1.0.0",
      workerCapabilities: [], services: { module: `src/rpc.${extension}` } }));
    await writeFile(resolve(root, `src/rpc.${extension}`), 'import {defineService} from "@sunder/sdk"; import {Implementations} from "#sunder/api"; export const implementation=defineService({examplePing(request,context){context.signal.throwIfAborted();return request;}},{implements:Implementations.extension});');
    const cache = resolve(root, "cache"); const toolRoot = dirname(tool);
    await exec(process.execPath, ["-e", `const fs=require("node:fs"),path=require("node:path");
      const {parseContractLock}=require(path.join(process.argv[1],"rpc-contract-lock.js"));
      const {publishContractCache}=require(path.join(process.argv[1],"rpc-contract-cache.js"));
      const {publishModelProjectionCache}=require(path.join(process.argv[1],"rpc-model-projection-cache.js"));
      const sdk=require(path.join(process.argv[1],"../../sdk/dist/index.js"));
      (async()=>{const lock=parseContractLock(fs.readFileSync(process.argv[2]));const hash=[...lock.requirements.keys()][0];
      const bytes=Buffer.from(sdk.canonicalRpcDescriptorBytes(sdk.parseRpcContractDescriptor(fs.readFileSync(process.argv[3]))));
      const descriptors=new Map([[hash,bytes]]);await publishContractCache(process.argv[4],descriptors,lock.requirements);
      await publishModelProjectionCache(process.argv[4],new Map([["example.dependency",fs.readFileSync(process.argv[5])]]),lock.packageArtifacts,descriptors);})().catch(error=>{console.error(error);process.exitCode=1;});`,
      toolRoot, resolve(root, "sunder-contracts.lock.json"), resolve(corpus, "conformance/valid/minimal.rpc.json"), cache,resolve(corpus,"authoring/conformance/valid/rpc-model-projection-nonempty.json")]);
    const run = (...args: string[]) => exec(process.execPath, [tool, ...args, "--project", root], { env: { ...process.env, SUNDER_CONTRACT_CACHE: cache }, timeout: 180_000, maxBuffer: 4_194_304 });
    await run("generate"); await run("build"); await run("package", "--discovery-root", resolve(root, "dist/targets"));
    const manifest = JSON.parse(await readFile(resolve(root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.equal(manifest.dependsOn[0].presence, "required"); assert.equal(manifest.packageApiImports[0].presence, "optional");
    assert.equal(manifest.usesContracts[0].required, false); assert.equal(manifest.providerImports[0].providerRefs.length, 1);
    const rid = `${process.platform === "darwin" ? "osx" : process.platform === "win32" ? "win" : "linux"}-${process.arch}`;
    const dispatched = await exec(process.execPath, ["-e", `const assert=require("node:assert/strict"),path=require("node:path"),{build}=require(path.join(process.argv[1],"../../../esbuild"));
      (async()=>{const result=await build({entryPoints:[path.join(process.argv[2],".sunder/generated/rpc/adapter.js")],bundle:true,platform:"node",format:"cjs",write:false,alias:{"@sunder/sdk":path.join(process.argv[1],"../../sdk/dist/index.js"),"@sunder/sdk/browser":path.join(process.argv[1],"../../sdk/dist/browser.js"),"#sunder/api":path.join(process.argv[2],".sunder/generated/rpc/api.js")}});const module={exports:{}};new Function("require","module","exports",result.outputFiles[0].text)(require,module,module.exports); const provider=module.exports.providers[0];assert.deepEqual(await provider.handler.invokeUnary({signal:new AbortController().signal},"example","ping",{}),{});console.log("packed-import-dispatched");})().catch(e=>{console.error(e);process.exitCode=1;});`, toolRoot, root]);
    assert.match(dispatched.stdout, /packed-import-dispatched/);
    assert.equal(await readFile(resolve(root, "sunder-contracts.lock.json"), "utf8"), locked);
    await assert.rejects(run("api", "unimplement", "example.dependency", "example.rpc.v1.minimal"), /remove-still-referenced/);
    await writeFile(resolve(root, `src/rpc.${extension}`), 'export {};');
    await run("api", "unimplement", "example.dependency", "example.rpc.v1.minimal"); await run("generate");
    const receipt = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"), "utf8"));
    assert.deepEqual(receipt.providerImports, []); assert.equal(receipt.packageApiImports.length, 1);
  } finally { await rm(root, { recursive: true, force: true }); }
}
