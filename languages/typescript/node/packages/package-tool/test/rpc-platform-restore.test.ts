import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, readdir, rm, stat, symlink, utimes, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { parseContractLock } from "../src/rpc-contract-lock";
import { publishContractCache, readContractCache } from "../src/rpc-contract-cache";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { createHash } from "node:crypto";
import { prepareInitialApiSelection } from "../src/rpc-authoring";
import { loadProjectConfiguration } from "../src/model";
import { captureBaseline } from "../src/rpc-baseline-store";
import { restoreContracts } from "../src/rpc-contract-restore";
import { catalogPath, platformRequirements, publishPlatformCatalogs, readPlatformCatalogs, selectPlatformContract } from "../src/rpc-platform-catalogs";
import { currentRid, loadProjectWithOwnedFrame } from "../src/model";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { bundleProduction } from "../src/package";
import { packageApiCommand, type PackageApiRequest } from "../src/rpc-package-commands";
import { collectContractCache } from "../src/rpc-contract-cache-gc";
import { generatedProviders, invocation } from "./service-fixtures";
import { publishFixtureModelProjections } from "./model-projection-fixtures";
import { preparePackageImports } from "../src/rpc-package-imports";

const ROOT = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/conformance/valid");
const compact = (value: unknown) => Buffer.from(JSON.stringify(value));
async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "sunder-platform-restore-")); const cache = join(root, "cache");
  const value = JSON.parse(await readFile(join(ROOT, "mixed-contract-lock.json"), "utf8")); value.packageArtifacts = {}; value.packageImports = {};
  const bytes = Buffer.from(JSON.stringify(value, null, 2) + "\n"); const locked = parseContractLock(bytes);
  const envelopeValue = JSON.parse(await readFile(join(ROOT, "platform-catalog.json"), "utf8")); const envelope = compact(envelopeValue);
  const path = join(root, "catalog.json"); const lockPath = join(root, "sunder-contracts.lock.json");
  await writeFile(path, envelope); await writeFile(lockPath, bytes);
  return { root, cache, bytes, locked, envelope, envelopeValue, path, lockPath,
    requirement: platformRequirements(locked)[0]!, dispose: () => rm(root, { recursive: true, force: true }) };
}
type Fixture = Awaited<ReturnType<typeof fixture>>;

for (const extension of ["ts", "js"]) test(`fresh complete ${extension} multi-provider source configures a local graph before first Accept`, { timeout: 180_000 }, async () => {
  const c = await fixture();
  try {
    const value = JSON.parse(c.envelopeValue.contracts[0].canonicalDescriptor); value.contractId = "sunder.extension";
    const descriptor = parseRpcContractDescriptor(JSON.stringify(value)); const bytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
    const extra = { contractId: descriptor.contractId, contractVersion: descriptor.version, descriptorBytes: bytes.length,
      descriptorSha256: createHash("sha256").update(bytes).digest("hex"), abiSha256: await computeRpcAbiSha256(descriptor), canonicalDescriptor: bytes.toString() };
    await writeFile(c.path, compact({ ...c.envelopeValue, contractCount: 2, contracts: [extra, ...c.envelopeValue.contracts] }));
    await writeFile(c.lockPath, await readFile(join(ROOT, "minimal-contract-lock.json"))); await mkdir(join(c.root, "src"));
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Multi Provider", version: "1.0.0",
      workerCapabilities: [], services: { module: `src/rpc.${extension}` } }));
    const source = 'import {defineService} from "@sunder/sdk"; import {Implementations} from "#sunder/api"; '
      + 'export const stack=defineService({ping(request){return request;}},{implements:Implementations.stackContributor});'
      + 'export const extension=defineService({ping(request){return request;}},{implements:Implementations.extension});';
    const sourcePath = join(c.root, `src/rpc.${extension}`); await writeFile(sourcePath, source);
    let requests = 0;
    const command = (request: PackageApiRequest) => packageApiCommand(c.root, { ...request, cacheRoot: c.cache, platformCatalog: c.path,
      fetchImplementation: async () => { requests++; assert.fail("Initial local configuration attempted HTTP"); } });
    const run = (...args: string[]) => promisify(execFile)(process.execPath, [resolve(__dirname, "../src/cli.js"), "contracts", ...args, "--project", c.root],
      { env: { ...process.env, SUNDER_CONTRACT_CACHE: c.cache } });
    assert.equal((await command({ operation: "platform-implement", contractId: "sunder.stack.contributor" })).changed, true);
    await assert.rejects(run("accept"), /rpc-authoring\./u);
    const first = await readFile(c.lockPath);
    await assert.rejects(command({ operation: "platform-implement", contractId: "sunder.extension", localName: "stackContributor" }), /local-name-collision/u);
    assert.deepEqual(await readFile(c.lockPath), first);
    // The dependency phase retains physical source/configuration fences even
    // though unresolved interfaces and handler errors are deferred to Accept.
    const frame = await loadProjectConfiguration(c.root);
    const verify = await prepareInitialApiSelection(frame.project, frame.configBytes, parseContractLock(first), c.cache, await captureBaseline(c.root));
    await writeFile(sourcePath, source + "\n// edited\n"); await assert.rejects(verify(), /changed/u); await writeFile(sourcePath, source);
    assert.equal((await command({ operation: "platform-implement", contractId: "sunder.extension" })).changed, true);
    const complete = await readFile(c.lockPath);
    await assert.rejects(stat(join(c.root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await assert.rejects(stat(join(c.root, ".sunder/generated/rpc")), { code: "ENOENT" });
    await writeFile(sourcePath, source.replaceAll("return request;", "return request.missing;"));
    await assert.rejects(run("accept"), /handler-incomplete/u); assert.deepEqual(await readFile(c.lockPath), complete);
    await writeFile(sourcePath, source); await run("accept"); await run("check");
    const loaded = await loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache });
    assert.equal(loaded.ownedRpc!.importedProviders.length, 2); await generateRpcBindingsForLoadedProject(loaded);
    const receiptPath = join(c.root, ".sunder/generated/rpc/authoring-receipt.v1.json"); const receipt = await readFile(receiptPath);
    await writeFile(sourcePath, source.replaceAll("return request;", "return request.missing;"));
    await assert.rejects(command({ operation: "platform-update" }), /handler-incomplete/u);
    await assert.rejects(stat(join(c.root, "sunder-contracts.owned.json")), {code:"ENOENT"});
    // Imported-only source needs no owned baseline, but still cannot bypass a
    // bad implementation through a lock update.
    await assert.rejects(command({ operation: "platform-update" }), /handler-incomplete/u);
    assert.deepEqual(await readFile(c.lockPath), complete); assert.deepEqual(await readFile(receiptPath), receipt); assert.equal(requests, 0);
  } finally { await c.dispose(); }
});
function metadata(c: Fixture, url: string) {
  return { authorityId: "sunder.core", releaseVersion: "1.1.0", catalogSha256: c.requirement.contentIdentity, catalogBytes: c.envelope.length,
    catalogDownloadUrl: new URL("api/v1/platform-contracts/sunder.core/releases/1.1.0/catalog", c.requirement.registryOrigin).href,
    lifecycleState: "Supported", publishedAtUtc: "2026-09-16T00:00:00Z", contracts: c.envelopeValue.contracts.map(({ canonicalDescriptor: _, ...value }: Record<string, unknown>) => value) };
}
function response(url: string, bytes: Buffer, headers: Record<string, string> = {}, status = 200): Response {
  const result = new Response(new Uint8Array(bytes), { status, headers: { "content-type": "application/json", "content-length": String(bytes.length), ...headers } });
  Object.defineProperty(result, "url", { value: url }); return result;
}

test("Registry catalog restore is offline, immutable, and needs no signing inputs", async () => {
  const c = await fixture();
  try {
    const restore = () => restoreContracts(c.root, { cacheRoot: c.cache, platformCatalog: c.path, fetchImplementation: async () => { throw new Error("Unexpected HTTP"); } });
    assert.equal(await restore(), 1);
    assert.equal((await readContractCache(c.cache, c.locked.requirements)).size, 1);
    assert.equal((await readPlatformCatalogs(c.locked, c.cache)).size, 1);
    const path = catalogPath(c.cache, c.requirement.contentIdentity); assert.deepEqual(await readFile(path), c.envelope);
    const time = (await stat(path)).mtimeMs; await restore(); assert.equal((await stat(path)).mtimeMs, time);
    assert.deepEqual(await readFile(c.lockPath), c.bytes);
  } finally { await c.dispose(); }
});

test("partial local restore requires remaining exact cache evidence before publishing a catalog", async () => {
  const c = await fixture();
  try {
    const value = JSON.parse(await readFile(join(ROOT, "package-api-contract-lock.json"), "utf8"));
    value.platformImports = JSON.parse(c.bytes.toString()).platformImports;
    delete value.packageArtifacts["example.dependency"].registryOrigin; delete value.platformImports.stackContributor.registryOrigin;
    const bytes = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(c.lockPath, bytes);
    const locked = parseContractLock(bytes); const offline: typeof fetch = async () => { assert.fail("Local restore attempted HTTP"); };
    const restore = () => restoreContracts(c.root, { cacheRoot: c.cache, platformCatalog: c.path, fetchImplementation: offline });
    await assert.rejects(restore(), /contract-cache.entry-missing.*local package candidate/u);
    await assert.rejects(stat(c.cache), { code: "ENOENT" }); assert.deepEqual(await readFile(c.lockPath), bytes);
    const contract = [...locked.packageArtifacts.get("example.dependency")!.contracts.values()][0]!;
    const descriptor = Buffer.from(canonicalRpcDescriptorBytes(parseRpcContractDescriptor(await readFile(resolve(ROOT, "../../../conformance/valid/minimal.rpc.json")))));
    await publishContractCache(c.cache, new Map([[contract.descriptorSha256, descriptor]]), locked.requirements);
    await publishFixtureModelProjections(c.cache,locked,new Map([[contract.descriptorSha256,descriptor]]));
    assert.equal(await restore(), 1); assert.equal((await readContractCache(c.cache, locked.requirements)).size, 2);
    assert.deepEqual(await readFile(c.lockPath), bytes);
  } finally { await c.dispose(); }
});

test("Development localhost catalog transport requires explicit origin mode, without signing configuration", async () => {
  const original = await fixture();
  try {
    const origin = "http://localhost:43127/"; const value = JSON.parse(original.bytes.toString()); value.platformImports.stackContributor.registryOrigin = origin;
    const bytes = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(original.lockPath, bytes);
    const locked = parseContractLock(bytes, true); const c = { ...original, bytes, locked, requirement: platformRequirements(locked)[0]! }; let requests = 0;
    const fetchImplementation: typeof fetch = async input => {
      requests++; const url = String(input); assert.ok(url.startsWith(origin));
      return url.endsWith("/catalog") ? response(url, c.envelope, { etag: `"${c.requirement.contentIdentity}"` }) : response(url, compact(metadata(c, url)));
    };
    const options = { cacheRoot: c.cache, allowDevelopmentRegistryOrigin: true, fetchImplementation };
    await assert.rejects(restoreContracts(c.root, { ...options, allowDevelopmentRegistryOrigin: false }), /registry-origin-invalid/u); assert.equal(requests, 0);
    assert.equal(await restoreContracts(c.root, options), 1);
    const selected = await selectPlatformContract("sunder.stack.contributor", origin, fetchImplementation, undefined, true);
    assert.equal(selected.registryOrigin, origin); assert.equal(selected.selectedAuthorityContentIdentity, c.requirement.contentIdentity);
    await assert.rejects(selectPlatformContract("sunder.stack.contributor", origin, fetchImplementation), /registry-origin-invalid/u);
    assert.deepEqual(await readFile(c.lockPath), bytes);
  } finally { await original.dispose(); }
});

test("online platform restoration uses original catalogs and matches exact offline cache bytes", async () => {
  const c = await fixture();
  try {
    let requests = 0;
    const fetchImplementation: typeof fetch = async (input, init) => {
      requests++; assert.equal(init?.redirect, "error"); const url = String(input);
      return url.endsWith("/catalog") ? response(url, c.envelope, { etag: `"${c.requirement.contentIdentity}"` }) : response(url, compact(metadata(c, url)));
    };
    assert.equal(await restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation }), 1);
    const offline = join(c.root, "offline"); await restoreContracts(c.root, { cacheRoot: offline, platformCatalog: c.path });
    for (const name of await readdir(c.cache, { recursive: true })) {
      if (!(await stat(join(c.cache, name))).isFile()) continue;
      assert.deepEqual(await readFile(join(c.cache, name)), await readFile(join(offline, name)));
    }
    assert.equal(requests, 2); assert.deepEqual(await readFile(c.lockPath), c.bytes);
  } finally { await c.dispose(); }
});

for (const failure of ["content", "inventory", "digest", "url", "etag", "length", "size-kind", "media", "redirect", "origin", "lock-edit"]) test(`platform restore rejects ${failure} before cache publication`, async () => {
  const c = await fixture();
  try {
    const fetchImplementation: typeof fetch = async input => {
      const url = String(input); const headers: Record<string, string> = {}; let bytes: Buffer; let status = 200;
      if (url.endsWith("/catalog")) {
        bytes = c.envelope; headers.etag = `"${c.requirement.contentIdentity}"`;
        if (failure === "content") bytes = compact({ ...c.envelopeValue, releaseVersion: "1.1.1" });
        if (failure === "etag") headers.etag = "W/" + headers.etag;
        if (failure === "length") headers["content-length"] = String(bytes.length - 1);
        if (failure === "media") headers["content-type"] = "application/schema+json";
        if (failure === "redirect") status = 302;
        if (failure === "origin") return response("https://other.example/catalog", bytes, headers);
        if (failure === "lock-edit") await writeFile(c.lockPath, "{}\n");
      } else {
        const value: Record<string, unknown> = metadata(c, url);
        if (failure === "inventory") value.contracts = [];
        if (failure === "digest") value.catalogSha256 = "f".repeat(64);
        if (failure === "url") value.catalogDownloadUrl = "https://other.example/catalog";
        if (failure === "size-kind") value.catalogBytes = "not-a-number";
        bytes = compact(value);
      }
      return response(url, bytes, headers, status);
    };
    await assert.rejects(restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation }));
    await assert.rejects(stat(c.cache), { code: "ENOENT" });
    assert.deepEqual(await readFile(c.lockPath), failure === "lock-edit" ? Buffer.from("{}\n") : c.bytes);
  } finally { await c.dispose(); }
});

test("metadata-only platform inventory verifies complete sidecar bytes without creating provider bindings or using HTTP", async () => {
  const c = await fixture();
  try {
    const locked = JSON.parse(await readFile(join(ROOT, "mixed-contract-lock.json"), "utf8")); locked.platformImports = {};
    const bytes = Buffer.from(JSON.stringify(locked, null, 2) + "\n"); await writeFile(c.lockPath, bytes);
    const selection = parseContractLock(bytes);
    const required = [...selection.requirements.values()][0]!;
    const descriptor = Buffer.from(canonicalRpcDescriptorBytes(parseRpcContractDescriptor(await readFile(resolve(ROOT, "../../../conformance/valid/minimal.rpc.json")))));
    await publishContractCache(c.cache, new Map([[required.descriptorSha256, descriptor]]), selection.requirements);
    await publishFixtureModelProjections(c.cache,selection,new Map([[required.descriptorSha256,descriptor],...c.envelopeValue.contracts.map((entry:{descriptorSha256:string;canonicalDescriptor:string})=>[entry.descriptorSha256,Buffer.from(entry.canonicalDescriptor)] as const)]));
    const count = await restoreContracts(c.root, { cacheRoot: c.cache, platformCatalog: c.path, fetchImplementation: async () => { assert.fail("Unexpected HTTP"); } });
    assert.equal(count, 1); assert.deepEqual((await readdir(c.cache)).sort(), [".sunder-contract-cache", required.descriptorSha256 + ".json", required.descriptorSha256 + ".receipt.json",
      c.requirement.descriptorSha256+".json",c.requirement.descriptorSha256+".receipt.json","model-projections", "platform-catalogs"].sort());
    const imports=await preparePackageImports(selection,c.cache,"example.owner",[]);
    assert.deepEqual(imports.contracts.map(contract=>contract.descriptor.contractId),[required.contractId]);assert.equal(imports.providerSurfaces.size,0);
    assert.equal((await readPlatformCatalogs(parseContractLock(bytes), c.cache)).size, 1);
  } finally { await c.dispose(); }
});

test("GC collects unreferenced owned catalog envelopes and descriptors", async () => {
  const c = await fixture();
  try {
    await restoreContracts(c.root, { cacheRoot: c.cache, platformCatalog: c.path });
    const old = new Date(Date.now() - 10 * 86_400_000);
    for (const name of await readdir(c.cache, { recursive: true })) if ((await stat(join(c.cache, name))).isFile()) await utimes(join(c.cache, name), old, old);
    await writeFile(c.lockPath, await readFile(join(ROOT, "minimal-contract-lock.json")));
    const result = await collectContractCache(c.cache, [c.lockPath]);
    assert.equal(result.removedEntries, 2); assert.equal(result.retainedUnprovenArtifacts, 0);
    assert.deepEqual(await readdir(join(c.cache, "platform-catalogs")), []);
  } finally { await c.dispose(); }
});

test("catalog repair preserves corrupt bytes, replays interruption, and refuses linked evidence", async () => {
  const c = await fixture();
  try {
    const restore = () => restoreContracts(c.root, { cacheRoot: c.cache, platformCatalog: c.path });
    await restore(); const path = catalogPath(c.cache, c.requirement.contentIdentity); await writeFile(path, "corrupt evidence");
    await assert.rejects(readPlatformCatalogs(c.locked, c.cache));
    await assert.rejects(publishPlatformCatalogs(c.cache, new Map([[c.requirement.contentIdentity, c.envelope]]), platformRequirements(c.locked),
      { checkpoint: point => { if (point === "catalog-quarantined") throw new Error("interrupted"); } }), /interrupted/u);
    await assert.rejects(stat(path), { code: "ENOENT" });
    const retained = (await readdir(join(c.cache, "platform-catalogs"))).filter(name => name.startsWith(".repair-")); assert.equal(retained.length, 1);
    assert.equal(await readFile(join(c.cache, "platform-catalogs", retained[0]!, c.requirement.contentIdentity + ".json"), "utf8"), "corrupt evidence");
    await restore(); assert.deepEqual(await readFile(path), c.envelope);
    if (process.platform !== "win32") {
      await rm(path); await symlink(c.path, path);
      await assert.rejects(readPlatformCatalogs(c.locked, c.cache)); await assert.rejects(restore());
      assert.deepEqual(await readFile(c.path), c.envelope);
    }
  } finally { await c.dispose(); }
});

for (const extension of ["ts", "js"]) test(`offline ${extension} platform providers compile and dispatch without consumer grants or dependencies`, { timeout: 120_000 }, async () => {
  const c = await fixture();
  try {
    const locked = JSON.parse(c.bytes.toString()); locked.platformImports.stackContributor.authorityReleaseRange = ">=1.1.0 <1.2.0";
    const lockBytes = Buffer.from(JSON.stringify(locked, null, 2) + "\n"); await writeFile(c.lockPath, lockBytes);
    await restoreContracts(c.root, { cacheRoot: c.cache, platformCatalog: c.path });
    await mkdir(join(c.root, "src"));
    await writeFile(join(c.root, "sunder-contracts.owned.json"), await readFile(join(ROOT, "minimal-package-owned-baseline.json")));
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Platform", version: "1.0.0",
      workerCapabilities: [], services: { module: `src/rpc.${extension}` } }));
    const source = 'import {defineService} from "@sunder/sdk"; import {Implementations} from "#sunder/api"; export const stack=defineService({ping(request,context){context.signal.throwIfAborted();return request;}},{implements:Implementations.stackContributor});';
    await writeFile(join(c.root, `src/rpc.${extension}`), source);
    const options = { contractCacheRoot: c.cache };
    const loaded = await loadProjectWithOwnedFrame(c.root, options); const analysis = loaded.ownedRpc!;
    assert.equal(loaded.contracts[0]!.imported!.authorityKind, "platform"); assert.deepEqual(loaded.config.usesContracts, []);
    assert.deepEqual(analysis.imports.dependencies, []);
    const receipt = JSON.parse(analysis.files.get("authoring-receipt.v1.json")!);
    assert.equal(receipt.platformImports[0].providerRefs.length, 1); assert.equal(receipt.platformImports[0].selectedAuthorityContentIdentity, c.requirement.contentIdentity);
    assert.deepEqual(receipt.packageArtifacts, []); assert.deepEqual(receipt.packageApiImports, []); assert.deepEqual(receipt.uses, []);
    await generateRpcBindingsForLoadedProject(loaded);
    const generated = join(c.root, ".sunder/generated/rpc/authoring-receipt.v1.json"); const before = await readFile(generated); const time = (await stat(generated)).mtimeMs;
    await generateRpcBindingsForLoadedProject(await loadProjectWithOwnedFrame(c.root, options));
    assert.equal((await stat(generated)).mtimeMs, time);
    const output = join(c.root, "provider.cjs");
    await bundleProduction(loaded, currentRid(), output);
    const registration = (await generatedProviders(c.root))[0]!;
    assert.equal(registration.contractId, "sunder.stack.contributor"); assert.equal(registration.descriptorSha256, c.requirement.descriptorSha256);
    assert.deepEqual(await registration.handler.invokeUnary(invocation(), "example", "ping", {}), {});
    await assert.rejects(async () => registration.handler.invokeUnary(invocation(), "example", "ping", { extra: true }));
    await writeFile(join(c.root, `src/rpc.${extension}`), 'export {};');
    await assert.rejects(loadProjectWithOwnedFrame(c.root, options), /handler-incomplete/u);
    await writeFile(join(c.root, `src/rpc.${extension}`), source);
    await rm(catalogPath(c.cache, c.requirement.contentIdentity));
    await assert.rejects(loadProjectWithOwnedFrame(c.root, options), /entry-missing/u);
    assert.deepEqual(await readFile(generated), before); assert.deepEqual(await readFile(c.lockPath), lockBytes);
  } finally { await c.dispose(); }
});

for (const local of [false, true]) test(`platform commands select from ${local ? "local catalog" : "Registry"}, check staged source, and protect live bindings`, { timeout: 120_000 }, async () => {
  const c = await fixture();
  try {
    await writeFile(c.lockPath, await readFile(join(ROOT, "minimal-contract-lock.json"))); await mkdir(join(c.root, "src"));
    if (!local) await writeFile(join(c.root, "sunder-contracts.owned.json"), await readFile(join(ROOT, "minimal-package-owned-baseline.json")));
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Platform", version: "1.0.0",
      workerCapabilities: [], services: { module: "src/rpc.ts" } }));
    const owned = local ? 'export const owned=defineService<{ping():Promise<void>}>(null,{declarationOnly:true});' : '';
    const empty = `import {defineService} from "@sunder/sdk"; ${owned}`;
    await writeFile(join(c.root, "src/rpc.ts"), empty);
    let requests = 0; let revoked = false;
    const fetchImplementation: typeof fetch = async input => {
      if (local) throw new Error("Local catalog authoring attempted HTTP.");
      requests++; const url = String(input);
      if (new URL(url).pathname.endsWith("/resolve")) assert.equal(new URL(url).searchParams.get("versionRange"), ">=1.1.0 <1.2.0");
      return url.endsWith("/catalog") ? response(url, c.envelope, { etag: `"${c.requirement.contentIdentity}"` })
        : response(url, compact({ ...metadata(c, url), lifecycleState: revoked ? "Revoked" : "Supported" }));
    };
    const command = (value: PackageApiRequest) => packageApiCommand(c.root, { ...value, cacheRoot: c.cache, fetchImplementation });
    const inputs = local ? { platformCatalog: c.path } : { registryOrigin: "https://registry.example/" };
    const added = await command({ operation: "platform-implement", contractId: "sunder.stack.contributor", ...inputs });
    assert.equal(added.changed, true); assert.equal(added.lock.platformImports.get("stackContributor")!.authorityReleaseRange, ">=1.1.0 <1.2.0");
    assert.equal(added.lock.platformImports.get("stackContributor")!.registryOrigin, local ? undefined : "https://registry.example/");
    assert.equal(added.lock.packageArtifacts.size, 0); const before = await readFile(c.lockPath); const time = (await stat(c.lockPath)).mtimeMs; const calls = requests;
    assert.equal((await command({ operation: "platform-implement", contractId: "sunder.stack.contributor" })).changed, false);
    assert.equal(requests, calls); assert.equal((await stat(c.lockPath)).mtimeMs, time);
    await assert.rejects(loadProjectWithOwnedFrame(c.root, { contractCacheRoot: c.cache }), /handler-incomplete/u);
    const source = empty + '\nimport {Implementations} from "#sunder/api"; export const stack=defineService({ping(request){return request;}},{implements:Implementations.stackContributor});';
    await writeFile(join(c.root, "src/rpc.ts"), source);
    if (local) {
      await assert.rejects(stat(join(c.root, "sunder-contracts.owned.json")), { code: "ENOENT" });
      await assert.rejects(stat(join(c.root, ".sunder/generated/rpc")), { code: "ENOENT" });
      const run = (...args: string[]) => promisify(execFile)(process.execPath, [resolve(__dirname, "../src/cli.js"), "contracts", ...args, "--project", c.root],
        { env: { ...process.env, SUNDER_CONTRACT_CACHE: c.cache } });
      await run("accept"); await run("check");
      await assert.rejects(stat(join(c.root, "sunder-contracts.owned.json")), {code:"ENOENT"});
      assert.deepEqual(await readFile(c.lockPath), before);
    }
    const restoreInput = local ? { platformCatalog: c.path } : {};
    assert.equal((await command({ operation: "platform-update", ...restoreInput })).changed, false);
    assert.equal((await command({ operation: "update", ...restoreInput })).changed, false);
    if (local) {
      await assert.rejects(command({ operation: "platform-update" }), /local-input-required/u);
      await assert.rejects(command({ operation: "platform-update", registryOrigin: "https://registry.example/" }), /requires a specific platform contract/u);
      const cold = join(c.root, "local-cold-cache");
      await restoreContracts(c.root, { cacheRoot: cold, platformCatalog: c.path, fetchImplementation });
      assert.equal((await readPlatformCatalogs(parseContractLock(before), cold)).size, 1);
      assert.equal(requests, 0);
    } else {
      revoked = true; await assert.rejects(command({ operation: "platform-update" }), /catalog-invalid/u); revoked = false;
    }
    await assert.rejects(command({ operation: "platform-unimplement", contractId: "sunder.stack.contributor" }), /remove-still-referenced/u);
    assert.deepEqual(await readFile(c.lockPath), before);
    await writeFile(join(c.root, "src/rpc.ts"), empty);
    assert.equal((await command({ operation: "platform-unimplement", contractId: "sunder.stack.contributor" })).changed, true);
    assert.ok((await stat(catalogPath(c.cache, c.requirement.contentIdentity))).isFile());
    const listed = await promisify(execFile)(process.execPath, [resolve(__dirname, "../src/cli.js"), "platform", "list", "--project", c.root]);
    assert.match(listed.stdout, /unchanged/u);
    for (const flag of ["--version", "--include-prerelease", "--presence", "--candidate", "--platform-trust-policy"])
      await assert.rejects(promisify(execFile)(process.execPath, [resolve(__dirname, "../src/cli.js"), "platform", "implement", "sunder.stack.contributor", flag, "forbidden", "--project", c.root]));
  } finally { await c.dispose(); }
});
