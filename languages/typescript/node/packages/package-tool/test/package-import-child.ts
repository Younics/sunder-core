import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { promisify } from "node:util";

async function main(): Promise<void> {
  const [parent, candidate, cache, app, provider] = process.argv.slice(2);
  if (!parent || !candidate || !cache) throw new Error("Expected parent, candidate and cache paths.");
  const root = join(parent, "node-import"); await mkdir(join(root, "src"), { recursive: true });
  await writeFile(join(root, "sunder-contracts.owned.json"), await readFile(join(parent, "sunder-contracts.owned.json")));
  await writeFile(join(root, "sunder-contracts.lock.json"), JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2) + "\n");
  await writeFile(join(root, "src/rpc.ts"), 'import {rpc} from "@sunder/sdk"; export const packageRpc=rpc.router({contracts:{},providers:{}});');
  await writeFile(join(root, "src/worker.ts"), 'export {packageRpc} from "./rpc.js"; export {packageApis} from "#sunder/rpc";');
  if (app === "app") { await mkdir(join(root, "app")); await writeFile(join(root, "app/index.html"), "<html><body>App</body></html>"); }
  await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Node Consumer", version: "1.0.0", entry: "src/worker.ts", workerCapabilities: [],
    rpc: { module: "src/rpc.ts", export: "packageRpc" }, ...(app === "app" ? { app: { root: "app", entryPoint: "index.html", views: [{ viewId: "example.owner.main", displayName: "App", route: "/", defaultPlacement: "middle", showInHotbar: true }] } } : {}) }));
  const cli = resolve(__dirname, "../src/cli.js"); const run = promisify(execFile);
  const command = (...args: string[]) => run(process.execPath, [cli, ...args, "--project", root], { env: { ...process.env, SUNDER_CONTRACT_CACHE: cache }, timeout: 180_000, maxBuffer: 4_194_304 });
  if (provider === "provider") {
    await command("api", "implement", "example.dependency", "example.rpc.v1.minimal", "--version", ">=1.0.0 <2.0.0", "--registry", "https://registry.example/", "--candidate", candidate, "--local-name", "extension", "--cache-root", cache);
    await assert.rejects(command("generate"), /no local implementation/);
    await writeFile(join(root, "src/rpc.ts"), 'import {rpc} from "@sunder/sdk"; import {rpcImports} from "#sunder/rpc"; export const packageRpc=rpc.router({contracts:{},providers:{implementation:rpcImports.extension.provider({handlers:{example:{ping(request,context){context.signal.throwIfAborted(); return request;}}}})}});');
  } else {
    await command("api", "add", "example.dependency", "--version", ">=1.0.0 <2.0.0", "--registry", "https://registry.example/", "--candidate", candidate, "--presence", "optional", "--cache-root", cache);
  }
  const lock = await readFile(join(root, "sunder-contracts.lock.json"));
  await command("generate"); await command("build"); await command("package", "--discovery-root", join(root, "dist/targets"));
  assert.deepEqual(await readFile(join(root, "sunder-contracts.lock.json")), lock);
  if (app === "app") {
    const appManifest = JSON.parse(await readFile(join(root, "dist/targets", "app-" + (process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : "linux") + "-" + process.arch, "manifest/sunder-package.json"), "utf8"));
    for (const group of ["usesContracts", "provides", "providerImports", "packageApiImports", "packageArtifactImports"]) assert.deepEqual(appManifest[group], []);
  }
  process.stdout.write("node-import-packed\n");
}
void main().catch(error => { process.stderr.write(String(error)); process.exitCode = 1; });
