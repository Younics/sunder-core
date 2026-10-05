import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { mkdtemp, mkdir, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { build } from "esbuild";
import { authoringBindingModel, canonicalRpcDescriptorBytes, parseRpcContractDescriptor } from "@sunder/sdk";
import { inventoryBytes, parseContractLock } from "../src/rpc-contract-lock";
import { publishContractCache } from "../src/rpc-contract-cache";
import { loadProjectWithOwnedFrame, currentRid } from "../src/model";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { packageApiCommand, type PackageApiRequest } from "../src/rpc-package-commands";
import { markerPath } from "../src/output";

const ROOT = resolve(__dirname, "../../../../../../../specs/rpc/v1");
const exec = promisify(execFile); const cli = resolve(__dirname, "../src/cli.js");
async function fixture(extension = "ts") {
  const root = await mkdtemp(join(tmpdir(), "sunder-package-imports-")); const cache = join(root, "cache"); await mkdir(join(root, "src"));
  const lockPath = join(root, "sunder-contracts.lock.json"); const original = await readFile(join(ROOT, "authoring/conformance/valid/package-api-contract-lock.json"));
  const locked = parseContractLock(original); const requirement = [...locked.requirements.values()][0]!;
  const descriptor = parseRpcContractDescriptor(await readFile(join(ROOT, "conformance/valid/minimal.rpc.json")));
  await publishContractCache(cache, new Map([[requirement.descriptorSha256, Buffer.from(canonicalRpcDescriptorBytes(descriptor))]]), locked.requirements);
  await writeFile(lockPath, original);
  await writeFile(join(root, "sunder-contracts.owned.json"), await readFile(join(ROOT, "authoring/conformance/valid/minimal-package-owned-baseline.json")));
  await writeFile(join(root, `src/rpc.${extension}`), 'import {rpc} from "@sunder/sdk"; export const packageRpc=rpc.router({contracts:{},providers:{}});');
  await writeFile(join(root, `src/worker.${extension}`), 'export {packageRpc} from "./rpc.js"; export {packageApis} from "#sunder/rpc";');
  const config = { schemaVersion: 3, id: "example.owner", name: "Import Fixture", version: "1.0.0", entry: `src/worker.${extension}`, workerCapabilities: [], rpc: { module: `src/rpc.${extension}`, export: "packageRpc" } };
  await writeFile(join(root, "sunder.package.json"), JSON.stringify(config));
  return { root, cache, lockPath, original, locked, requirement, descriptor, config,
    run: (...args: string[]) => exec(process.execPath, [cli, ...args, "--project", root], { env: { ...process.env, SUNDER_CONTRACT_CACHE: cache }, timeout: 180_000, maxBuffer: 4_194_304 }),
    dispose: () => rm(root, { recursive: true, force: true }) };
}

for (const extension of ["ts", "js"]) test(`offline ${extension} imports generate, build and pack from exact cached descriptors`, { timeout: 180_000 }, async () => {
  const c = await fixture(extension);
  try {
    const project = await loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache });
    assert.equal(project.ownedRpc!.contracts.length, 0); assert.equal(project.contracts.length, 1);
    assert.equal(project.config.usesContracts![0]!.required, true); assert.equal(project.config.providers.length, 0);
    await generateRpcBindingsForLoadedProject(project);
    const generated = join(c.root, ".sunder/generated/rpc");
    const before = await snapshot(generated); await generateRpcBindingsForLoadedProject(await loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }));
    assert.deepEqual(await snapshot(generated), before);
    await c.run("build"); await c.run("package", "--discovery-root", join(c.root, "dist/targets"));
    const manifest = JSON.parse(await readFile(join(c.root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.deepEqual(manifest.dependsOn, [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0", presence: "required" }]);
    assert.equal(manifest.contractBundles[0].source, "imported"); assert.equal(manifest.contractBundles[0].authorityId, "example.dependency");
    assert.equal(manifest.packageArtifactImports[0].publicInventorySha256, c.locked.packageArtifacts.get("example.dependency")!.publicInventorySha256);
    assert.equal(manifest.packageApiImports[0].useRefs[0].role, "runtime"); assert.equal(manifest.usesContracts[0].required, true);
    assert.deepEqual(await readFile(c.lockPath), c.original);
    const frozen = await snapshot(generated); await rm(join(c.cache, c.requirement.descriptorSha256 + ".json"));
    await assert.rejects(c.run("generate"), /contract-cache.entry-missing/u); assert.deepEqual(await snapshot(generated), frozen);
  } finally { await c.dispose(); }
});

test("generated discovery keeps exact tuples, compatible ranges, opaque endpoints and reset/lifecycle events", async () => {
  const c = await fixture();
  try {
    const project = await loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }); await generateRpcBindingsForLoadedProject(project);
    const bundled = join(c.root, "apis.cjs");
    await build({ entryPoints: [join(c.root, ".sunder/generated/rpc/imports.ts")], bundle: true, platform: "browser", format: "cjs", outfile: bundled,
      alias: { "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser") }, logLevel: "silent" });
    const { packageApis } = createRequire(__filename)(bundled);
    const raw = { packageId: "example.dependency", packageVersion: "1.9.9+new.content", providerId: "example.implementation", contractId: c.requirement.contractId,
      contractVersion: c.requirement.contractVersion, descriptorSha256: c.requirement.descriptorSha256, abiSha256: c.requirement.abiSha256,
      activationId: "activation", activationEpoch: 1, sessionGeneration: 1, endpointReference: "opaque", catalogRevision: 1, state: "active", faultCode: null };
    let providers = [raw]; let resetRequired = false; let replacement = raw;
    const rpc = { discover: async () => ({ revision: 1, sequence: 2, resetRequired, providers }), getProvider: async () => replacement,
      watch: async function* () { yield { revision: 2, sequence: 3, kind: "faulted", provider: { ...raw, state: "faulted" } }; yield { revision: 3, sequence: 4, kind: "reset-required", provider: null }; } };
    const api = packageApis.dependency(rpc).CExample_Rpc_V1_Minimal;
    const provider = await api.resolveSelectedPackage(); assert.ok(Object.isFrozen(provider)); assert.equal("endpointReference" in provider, false);
    assert.ok(await api.get(provider)); replacement = { ...raw, endpointReference: "replacement" }; assert.equal(await api.get(provider), null);
    let called = false; const scope = { invoke: async (target: string) => { assert.equal(target, "opaque"); called = true; return {}; } };
    const client = api.createClient(scope, provider); const model = authoringBindingModel(c.descriptor); const service = model.services[0]!;
    await client[service.clientPropertyName][service.methods[0]!.methodName]({}); assert.ok(called);
    assert.throws(() => api.createClient(scope, { ...provider }), (error: any) => error.error.code === "rpc.provider-invalid");
    providers = [{ ...raw, descriptorSha256: "a".repeat(64) }]; await assert.rejects(api.resolveSelectedPackage(), (error: any) => error.error.code === "rpc.provider-not-found");
    providers = [{ ...raw, packageVersion: "2.0.0" }]; await assert.rejects(api.resolveSelectedPackage(), (error: any) => error.error.code === "rpc.provider-not-found");
    providers = [raw, raw]; await assert.rejects(api.resolveSelectedPackage(), (error: any) => error.error.code === "rpc.provider-ambiguous");
    resetRequired = true; await assert.rejects(api.resolveSelectedPackage(), (error: any) => error.error.code === "rpc.catalog-reset-required");
    const events = []; for await (const event of api.watch(1, 2)) events.push(event);
    assert.equal(events[0].provider.state, "faulted"); assert.equal(events[1].kind, "reset-required");
  } finally { await c.dispose(); }
});

test("offline readiness changes and removal retain cache and defer removed-symbol errors to compilation", async () => {
  const c = await fixture();
  try {
    await writeFile(join(c.root, "src/worker.ts"), 'export {packageRpc} from "./rpc.js"; import {packageApis} from "#sunder/rpc"; export const dependency=packageApis.dependency;');
    await generateRpcBindingsForLoadedProject(await loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }));
    const generated = await snapshot(join(c.root, ".sunder/generated/rpc"));
    const command = (request: PackageApiRequest) => packageApiCommand(c.root, { ...request, cacheRoot: c.cache, fetchImplementation: async () => { throw new Error("Unexpected HTTP."); } });
    const before = await snapshot(c.cache);
    assert.equal((await command({ operation: "require", packageId: "example.dependency", contractId: c.requirement.contractId })).changed, false);
    await assert.rejects(command({ operation: "remove", packageId: "example.dependency" }), /remove-still-referenced/u);
    assert.equal((await command({ operation: "unrequire", packageId: "example.dependency", contractId: c.requirement.contractId })).changed, true);
    assert.equal((await command({ operation: "remove", packageId: "example.dependency" })).changed, true);
    assert.deepEqual(await snapshot(c.cache), before);
    assert.equal((await command({ operation: "list" })).lock.packageArtifacts.size, 0);
    await assert.rejects(loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }), /handler-incomplete/u);
    assert.deepEqual(await snapshot(join(c.root, ".sunder/generated/rpc")), generated);
  } finally { await c.dispose(); }
});

test("duplicate authored dependency and self authority fail before generated output changes", async () => {
  const c = await fixture();
  try {
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ ...c.config, dependencies: [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0" }] }));
    await assert.rejects(loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }), /dependency-source-duplicate/u);
    await assert.rejects(stat(join(c.root, ".sunder/generated/rpc")), { code: "ENOENT" });
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ ...c.config, id: "example.dependency" }));
    await assert.rejects(loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }), /cannot import its own/u);
  } finally { await c.dispose(); }
});

test("global update's last member failure and concurrent lock edits preserve the committed selection", async () => {
  const c = await fixture();
  try {
    const value = JSON.parse(c.original.toString()); const reexport = structuredClone(value.packageArtifacts["example.dependency"]);
    reexport.selectedPackageContentIdentity = "3".repeat(64); reexport.contracts[c.requirement.contractId].artifactSource = "imported";
    const inventory = JSON.parse(inventoryBytes(c.locked.packageArtifacts.get("example.dependency")!).toString());
    inventory.packageId = "example.zreexport"; inventory.packageContentIdentity = reexport.selectedPackageContentIdentity; inventory.contracts[0].artifactSource = "imported";
    reexport.publicInventorySha256 = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex");
    value.packageArtifacts["example.zreexport"] = reexport; value.packageImports["example.zreexport"] = { localName: "reexport", presence: "optional", requiredContracts: [] };
    const before = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(c.lockPath, before);
    const cache = await snapshot(c.cache); let calls = 0;
    const failure: typeof fetch = async (input, init) => {
      calls++; if (String(input).includes("example.zreexport")) return new Response(null, { status: 503 });
      return registryResponse(c, String(input), init);
    };
    await assert.rejects(packageApiCommand(c.root, { operation: "update", cacheRoot: c.cache, fetchImplementation: failure }));
    assert.equal(calls, 3); assert.deepEqual(await readFile(c.lockPath), before); assert.deepEqual(await snapshot(c.cache), cache);
    await writeFile(c.lockPath, c.original);
    const changed = c.original.toString().replace('"presence": "required"', '"presence": "optional"');
    const mutate: typeof fetch = async (input, init) => {
      const response = await registryResponse(c, String(input), init);
      if (String(input).endsWith("/descriptor")) await writeFile(c.lockPath, changed);
      return response;
    };
    await assert.rejects(packageApiCommand(c.root, { operation: "update", packageId: "example.dependency", cacheRoot: c.cache, fetchImplementation: mutate }), /lock changed/u);
    assert.equal(await readFile(c.lockPath, "utf8"), changed); assert.deepEqual(await snapshot(c.cache), cache);
  } finally { await c.dispose(); }
});

test("package API add/update uses stable selection, explicit presence and prerelease tri-state with atomic source checks", { timeout: 180_000 }, async () => {
  const c = await fixture();
  try {
    const empty = await readFile(join(ROOT, "authoring/conformance/valid/minimal-contract-lock.json")); await writeFile(c.lockPath, empty);
    await rm(join(c.root, "sunder-contracts.owned.json"));
    let requests = 0;
    const request: typeof fetch = async (input, init) => { requests++; return registryResponse(c, String(input), init); };
    const command = (value: PackageApiRequest) => packageApiCommand(c.root, { ...value, cacheRoot: c.cache, fetchImplementation: request });
    const add: PackageApiRequest = { operation: "add", packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0", registryOrigin: "https://registry.example/", presence: "optional" };
    const added = await command(add); assert.ok(added.changed);
    assert.equal(added.lock.packageArtifacts.get("example.dependency")!.selectedPackageVersion, "1.9.0");
    assert.equal(added.lock.packageImports.get("example.dependency")!.presence, "optional"); assert.deepEqual(added.lock.packageImports.get("example.dependency")!.requiredContracts, []);
    await assert.rejects(stat(join(c.root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await c.run("contracts", "accept");
    const before = await readFile(c.lockPath); const time = (await stat(c.lockPath)).mtimeMs; const calls = requests;
    assert.equal((await command(add)).changed, false); assert.equal(requests, calls); assert.equal((await stat(c.lockPath)).mtimeMs, time);
    const prerelease = await command({ operation: "update", packageId: "example.dependency", includePrerelease: true });
    assert.equal(prerelease.lock.packageArtifacts.get("example.dependency")!.selectedPackageVersion, "1.10.0-rc.1");
    assert.equal((await command({ operation: "update", packageId: "example.dependency" })).changed, false);
    const stable = await command({ operation: "update", packageId: "example.dependency", includePrerelease: false, presence: "required" });
    assert.equal(stable.lock.packageArtifacts.get("example.dependency")!.selectedPackageVersion, "1.9.0");
    const saved = await readFile(c.lockPath);
    await writeFile(join(c.root, "src/worker.ts"), 'import {packageApis} from "#sunder/rpc"; export const broken=packageApis.missing; export {packageRpc} from "./rpc.js";');
    await assert.rejects(command({ operation: "update", packageId: "example.dependency", presence: "optional" }), /rpc-authoring\./u);
    assert.deepEqual(await readFile(c.lockPath), saved); assert.notDeepEqual(saved, before);
    await assert.rejects(stat(join(c.root, ".sunder/generated/rpc")), { code: "ENOENT" });
  } finally { await c.dispose(); }
});

test("fresh API selection defers source diagnostics to Accept and refuses deleted baselines while preserving prior outputs", { timeout: 180_000 }, async () => {
  const c = await fixture();
  try {
    const empty = await readFile(join(ROOT, "authoring/conformance/valid/minimal-contract-lock.json"));
    await writeFile(c.lockPath, empty); await rm(join(c.root, "sunder-contracts.owned.json"));
    const command = () => packageApiCommand(c.root, { operation: "add", packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0",
      registryOrigin: "https://registry.example/", cacheRoot: c.cache, fetchImplementation: (input, init) => registryResponse(c, String(input), init) });
    await writeFile(join(c.root, "src/worker.ts"), 'import {packageApis} from "#sunder/rpc"; export const broken=packageApis.missing; export {packageRpc} from "./rpc.js";');
    assert.equal((await command()).changed, true);
    await assert.rejects(c.run("contracts", "accept"), /handler-incomplete/u);
    await writeFile(c.lockPath, empty);
    await assert.rejects(stat(join(c.root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await assert.rejects(stat(join(c.root, ".sunder/generated/rpc")), { code: "ENOENT" });
    await writeFile(join(c.root, "src/worker.ts"), 'export {packageRpc} from "./rpc.js";');
    await c.run("contracts", "accept");
    await generateRpcBindingsForLoadedProject(await loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }));
    const generated = join(c.root, ".sunder/generated/rpc");
    await rm(join(c.root, "sunder-contracts.owned.json"));
    // Each surviving evidence file independently blocks re-bootstrap, including
    // the case where an old baseline marker has also been deleted.
    const evidence = ["owned-baseline.sha256", "authoring-receipt.v1.json", "package-export.v1.json"];
    const retained = new Map(await Promise.all(evidence.slice(0, 2).map(async name => [name, await readFile(join(generated, name))] as const)));
    retained.set("package-export.v1.json", Buffer.from("retained export evidence"));
    for (const name of evidence) await rm(join(generated, name), { force: true });
    await rm(markerPath(generated));
    for (const name of evidence) {
      await writeFile(join(generated, name), retained.get(name)!); const before = await snapshot(generated);
      await assert.rejects(command(), /baseline-drift/u);
      await assert.rejects(c.run("contracts", "accept"), /baseline-drift/u);
      await assert.rejects(loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }), /baseline-drift/u);
      assert.deepEqual(await readFile(c.lockPath), empty); assert.deepEqual(await snapshot(generated), before);
      await rm(join(generated, name));
    }
  } finally { await c.dispose(); }
});

test("targeted local-to-Registry binding is explicit, source checked, and pins the new origin", async () => {
  const c = await fixture();
  try {
    const value = JSON.parse(c.original.toString()); delete value.packageArtifacts["example.dependency"].registryOrigin;
    const local = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(c.lockPath, local);
    let calls = 0;
    const command = (request: PackageApiRequest) => packageApiCommand(c.root, { ...request, cacheRoot: c.cache,
      fetchImplementation: (input, init) => { calls++; return registryResponse(c, String(input), init); } });
    await assert.rejects(command({ operation: "update", packageId: "example.dependency" }), /local-input-required/u);
    await assert.rejects(command({ operation: "update", registryOrigin: "https://registry.example/" }), /requires a specific package/u);
    assert.equal(calls, 0); assert.deepEqual(await readFile(c.lockPath), local);
    const selected = await command({ operation: "update", packageId: "example.dependency", registryOrigin: "https://registry.example/" });
    assert.equal(selected.lock.packageArtifacts.get("example.dependency")!.registryOrigin, "https://registry.example/");
    assert.deepEqual(selected.lock.packageImports.get("example.dependency"), c.locked.packageImports.get("example.dependency"));
    const pinned = await readFile(c.lockPath); const before = calls;
    for (const origin of ["https://registry.example/", "https://other.example/"])
      await assert.rejects(command({ operation: "update", packageId: "example.dependency", registryOrigin: origin }), /registry-origin-invalid/u);
    assert.equal(calls, before); assert.deepEqual(await readFile(c.lockPath), pinned);
  } finally { await c.dispose(); }
});

for (const invalid of [
  { operation: "update", presence: "optional" }, { operation: "update", includePrerelease: false }, { operation: "update", versionRange: ">=1.0.0" },
  { operation: "update", packageId: "example.dependency", registryOrigin: "https://registry.example/" },
  { operation: "add", packageId: "example.new", versionRange: ">=1.0.0" }, { operation: "require", packageId: "example.dependency", contractId: "example.absent" },
  { operation: "remove", packageId: "example.absent" },
] as const) test(`invalid package API intent stays offline: ${JSON.stringify(invalid)}`, async () => {
  const c = await fixture();
  try {
    let calls = 0;
    await assert.rejects(packageApiCommand(c.root, { ...invalid, cacheRoot: c.cache, fetchImplementation: async () => { calls++; throw new Error("Unexpected HTTP."); } }));
    assert.equal(calls, 0); assert.deepEqual(await readFile(c.lockPath), c.original);
  } finally { await c.dispose(); }
});

test("explicit update refuses removal of required inventory members before cache or lock publication", async () => {
  const c = await fixture();
  try {
    const request: typeof fetch = async (input, init) => {
      const response = await registryResponse(c, String(input), init);
      if (String(input).endsWith("/public-api")) {
        const body = await response.json() as any; body.inventory.contracts[0].contractId = "example.replacement";
        body.publicInventorySha256 = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(body.inventory)).digest("hex");
        return responseAt(String(input), JSON.stringify(body));
      }
      return response;
    };
    const cache = await snapshot(c.cache);
    await assert.rejects(packageApiCommand(c.root, { operation: "update", packageId: "example.dependency", cacheRoot: c.cache, fetchImplementation: request }), /required-contract-removed/u);
    assert.deepEqual(await readFile(c.lockPath), c.original); assert.deepEqual(await snapshot(c.cache), cache);
  } finally { await c.dispose(); }
});

test("imported runtime leaves and owned-only App leaves aggregate with exact role-scoped metadata", { timeout: 180_000 }, async () => {
  const c = await fixture();
  try {
    await mkdir(join(c.root, "app")); await writeFile(join(c.root, "app/index.html"), '<html><body>App</body></html>');
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ ...c.config, app: { root: "app", entryPoint: "index.html", views: [{ viewId: "example.owner.main", displayName: "App", route: "/", defaultPlacement: "middle", showInHotbar: true }] } }));
    await c.run("build"); await c.run("package", "--discovery-root", join(c.root, "dist/targets"));
    const app = JSON.parse(await readFile(join(c.root, "dist/targets", "app-" + currentRid(), "manifest/sunder-package.json"), "utf8"));
    assert.deepEqual(app.contractBundles, []); assert.deepEqual(app.packageApiImports, []); assert.deepEqual(app.usesContracts, []);
    const aggregate = JSON.parse(await readFile(join(c.root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.equal(aggregate.targets.length, 2); assert.equal(aggregate.contractBundles.length, 1); assert.equal(aggregate.usesContracts[0].role, "runtime");
    assert.deepEqual(await readFile(join(c.root, "dist/package/payload/shared/contracts", c.requirement.contractId + ".rpc.json")), Buffer.from(canonicalRpcDescriptorBytes(c.descriptor)));
  } finally { await c.dispose(); }
});

function responseAt(url: string, body: string | Uint8Array, headers: Record<string, string> = {}): Response {
  const response = new Response(typeof body === "string" ? body : new Uint8Array(body), { headers: { "content-type": "application/json", ...headers } });
  Object.defineProperty(response, "url", { value: url }); return response;
}
async function registryResponse(c: Awaited<ReturnType<typeof fixture>>, url: string, init?: RequestInit): Promise<Response> {
  assert.equal(init?.redirect, "error");
  if (new URL(url).pathname.endsWith("/versions")) return responseAt(url, JSON.stringify(["1.2.3", "1.9.0", "1.10.0-rc.1", "1.99.0", "2.0.0"].map(version => ({ version, isYanked: version === "1.99.0" }))),
    { "X-Total-Count": "5", "X-Skip": "0", "X-Take": "100" });
  if (url.endsWith("/public-api")) {
    const inventory = JSON.parse(inventoryBytes(c.locked.packageArtifacts.get("example.dependency")!).toString());
    const version = decodeURIComponent(new URL(url).pathname.split("/").at(-2)!);
    inventory.packageVersion = version; inventory.contracts[0].selectedAuthorityRelease = version;
    inventory.packageContentIdentity = "2".repeat(64); inventory.contracts[0].selectedAuthorityContentIdentity = inventory.packageContentIdentity;
    const hash = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex");
    return responseAt(url, JSON.stringify({ inventory, publicInventorySha256: hash }));
  }
  if (url.endsWith("/descriptor")) return responseAt(url, canonicalRpcDescriptorBytes(c.descriptor), { "content-type": "application/schema+json", etag: `"${c.requirement.descriptorSha256}"` });
  return responseAt(url, JSON.stringify({ contractId: c.requirement.contractId, version: c.requirement.contractVersion, descriptorSha256: c.requirement.descriptorSha256,
    abiSha256: c.requirement.abiSha256, size: c.requirement.descriptorBytes, etag: `"${c.requirement.descriptorSha256}"`, exposure: "public", firstPublisherPackageId: "example.dependency" }));
}

async function snapshot(path: string): Promise<string[]> {
  const output: string[] = [];
  for (const name of (await readdir(path)).sort()) {
    const file = join(path, name); const info = await stat(file);
    if (info.isDirectory()) output.push(...(await snapshot(file)).map(value => name + "/" + value));
    else output.push(name + ":" + (await readFile(file)).toString("base64") + ":" + info.mtimeMs);
  }
  return output;
}
