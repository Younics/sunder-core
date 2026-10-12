import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { request } from "node:https";
import { dirname, join, resolve } from "node:path";
import { checkServerIdentity } from "node:tls";
import { packageApiCommand } from "../src/rpc-package-commands";
import { restoreContracts } from "../src/rpc-contract-restore";
import { parseContractLock } from "../src/rpc-contract-lock";
import { readPlatformCatalogs } from "../src/rpc-platform-catalogs";
import { currentRid, loadProjectWithOwnedFrame } from "../src/model";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { bundleProduction, writeTargetLeaf } from "../src/package";

// Private test companion pins its loopback TLS certificate and the selected Registry.
async function main(): Promise<void> {
  const [inputPath, phase] = process.argv.slice(2); assert.ok(inputPath); assert.ok(phase === "build" || phase === "revoked");
  const inputBytes = await readFile(inputPath); assert.ok(inputBytes.length <= 32_768);
  const input = JSON.parse(inputBytes.toString()) as { registryOrigin: string; certificatePem: string; certificateSha256: string; catalogSha256: string; csharpOutput: string };
  const origin = new URL(input.registryOrigin); assert.equal(origin.hostname, "localhost"); assert.equal(origin.protocol, "https:");
  const fixtures = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/conformance/valid");
  let offline = false;
  const fetchImplementation: typeof fetch = async (url, init) => {
    assert.equal(offline, false, "Offline source generation must not make Registry requests.");
    const target = new URL(String(url)); assert.equal(target.origin, origin.origin);
    return new Promise<Response>((resolveResponse, reject) => {
      const call = request(target, { method: "GET", ca: input.certificatePem, signal: init?.signal ?? undefined,
        checkServerIdentity: (host, certificate) => checkServerIdentity(host, certificate)
          ?? (certificate.fingerprint256.replaceAll(":", "").toLowerCase() === input.certificateSha256 ? undefined : new Error("Unexpected Registry test certificate")) }, response => {
        const chunks: Buffer[] = []; let size = 0;
        response.on("error", reject);
        response.on("data", (chunk: Buffer) => { size += chunk.length; if (size > 67_108_864) call.destroy(new Error("Test response exceeds its bound")); else chunks.push(chunk); });
        response.on("end", () => {
          const headers = new Headers();
          for (const [name, value] of Object.entries(response.headers)) if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(",") : value);
          const result = new Response(new Uint8Array(Buffer.concat(chunks)), { status: response.statusCode!, headers });
          Object.defineProperty(result, "url", { value: target.href }); resolveResponse(result);
        });
      });
      call.on("error", reject); call.end();
    });
  };
  const leaves: string[] = [];
  for (const [index, extension] of ["ts", "js"].entries()) {
    const project = join(dirname(inputPath), "node-" + extension); const cache = join(project, "cache"); const lockPath = join(project, "sunder-contracts.lock.json");
    if (phase === "build") {
      await mkdir(join(project, "src"), { recursive: true });
      await writeFile(lockPath, await readFile(join(fixtures, "minimal-contract-lock.json")));
      await writeFile(join(project, "sunder-contracts.owned.json"), await readFile(join(fixtures, "minimal-package-owned-baseline.json")));
      await writeFile(join(project, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Live Stack", version: `1.0.${index + 1}`,
        workerCapabilities: [], services: { module: `src/rpc.${extension}` } }));
      await writeFile(join(project, `src/rpc.${extension}`), "export {};");
      await packageApiCommand(project, { operation: "platform-implement", contractId: "sunder.stack.contributor", registryOrigin: input.registryOrigin, cacheRoot: cache, fetchImplementation });
    }
    const lockedBytes = await readFile(lockPath); const locked = parseContractLock(lockedBytes);
    assert.deepEqual(lockedBytes, await readFile(join(input.csharpOutput, "sunder-contracts.lock.json")));
    if (phase === "revoked") {
      await assert.rejects(packageApiCommand(project, { operation: "platform-update", cacheRoot: cache, fetchImplementation }), /platform-api.import-not-found/u);
      assert.deepEqual(await readFile(lockPath), lockedBytes);
    }
    assert.equal(await restoreContracts(project, { cacheRoot: cache, fetchImplementation }), 1);
    const catalog = [...(await readPlatformCatalogs(locked, cache)).values()][0]!;
    assert.equal(catalog.contentIdentity, input.catalogSha256);
    assert.deepEqual(Buffer.from(catalog.getCanonicalUtf8()), await readFile(join(input.csharpOutput, "catalog.json")));
    if (phase === "revoked") continue;
    await writeFile(join(project, `src/rpc.${extension}`), `
      import {defineService,RpcError} from '@sunder/sdk'; import {Implementations} from '#sunder/api';
      export const stack=defineService({
        metadata(request,context){context.signal.throwIfAborted();return {contributorId:'node-stack',displayName:'Node Stack',supportsExport:true,supportsImport:false,supportsImportApplied:false};},
        listExportItems(request,context){context.signal.throwIfAborted();return {items:[{itemId:'item',displayName:'Item',kind:'sample',defaultSelected:true}]};},
        export(){throw new RpcError({kind:'domain',code:'stack.method.unsupported',message:'Unsupported'});},
        previewImport(){throw new RpcError({kind:'domain',code:'stack.method.unsupported',message:'Unsupported'});},
        import(){throw new RpcError({kind:'domain',code:'stack.method.unsupported',message:'Unsupported'});},
        importApplied(){throw new RpcError({kind:'domain',code:'stack.method.unsupported',message:'Unsupported'});}
      },{implements:Implementations.stackContributor,providerId:'example.owner.stack'});`);
    offline = true;
    const options = { contractCacheRoot: cache };
    const loaded = await loadProjectWithOwnedFrame(project, options);
    assert.equal(loaded.contracts.length, 1); assert.deepEqual(loaded.config.usesContracts, []); assert.deepEqual(loaded.ownedRpc!.imports.dependencies, []);
    await generateRpcBindingsForLoadedProject(loaded);
    await mkdir(join(project, "work")); const modulePath = join(project, "work/worker.cjs");
    const notice = await bundleProduction(loaded, currentRid(), modulePath);
    leaves.push(await writeTargetLeaf({ project: loaded, rid: currentRid(), modulePath, workerNoticePath: notice, discoveryRoot: join(project, "dist/targets") }));
    assert.deepEqual(await readFile(lockPath), lockedBytes); offline = false;
  }
  if (phase === "build") await writeFile(join(dirname(inputPath), "node-platform-packages.json"), JSON.stringify(leaves));
}
void main().catch(error => { console.error(error); process.exitCode = 1; });
