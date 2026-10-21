import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import type { PackageArtifact, ArtifactContract } from "../src/rpc-contract-lock";
import {liveCatalogWatch} from "./service-fixtures";

// The retained Registry two-contract fixture: both contracts have messages.send,
// an empty Request, and the same Response. Names and wire shapes are not repaired
// to accommodate consumer code generation. No private Registry code is executed.
export function retainedDescriptor(contractId: string) {
  return { descriptorVersion: 1, schemaProfile: "sunder.rpc.values.v1", contractId, version: "1.0.0", errors: {},
    services: { messages: { methods: { send: { kind: "unary", idempotency: "none", errors: [],
      requestSchema: { $ref: "#/$defs/Request" }, responseSchema: { $ref: "#/$defs/Response" } } } } },
    $defs: {
      Request: { type: "object", properties: {}, required: [], additionalProperties: false },
      Response: { type: "object", properties: { message: { type: "string" }, items: { type: "array", items: { type: "string" } },
        metadata: { type: "object", additionalProperties: { type: "string" }, propertyNames: { type: "string" } },
        payload: { type: "string", contentEncoding: "base64" } }, required: ["message"], additionalProperties: false },
    } };
}

/** Same fixture runs against checkout outputs and the production-only npm install.
 * All compiler, cache, generator, SDK and bundler implementations resolve from toolRoot. */
export async function serviceApiFixture(toolRoot: string, extension: "ts" | "js", ids = ["example.dependency.first", "example.dependency.second"]) {
  const fromTool = createRequire(join(toolRoot, "index.js"));
  const sdk: typeof import("@sunder/sdk") = fromTool("@sunder/sdk");
  const lockTools: typeof import("../src/rpc-contract-lock") = fromTool("./rpc-contract-lock.js");
  const cacheTools: typeof import("../src/rpc-contract-cache") = fromTool("./rpc-contract-cache.js");
  const projectionTools: typeof import("../src/rpc-model-projection") = fromTool("./rpc-model-projection.js");
  const projectionCache: typeof import("../src/rpc-model-projection-cache") = fromTool("./rpc-model-projection-cache.js");
  const model: typeof import("../src/model") = fromTool("./model.js");
  const bindings: typeof import("../src/rpc-bindings") = fromTool("./rpc-bindings.js");
  const root = await mkdtemp(join(tmpdir(), "sunder-service-scopes-")); const cache = join(root, "cache");
  try {
    const descriptors = new Map<string, Buffer>(); const records = new Map<string, ArtifactContract>();
    for (const id of [...ids].sort()) {
      const descriptor = sdk.parseRpcContractDescriptor(JSON.stringify(retainedDescriptor(id)));
      const bytes = Buffer.from(sdk.canonicalRpcDescriptorBytes(descriptor)); const hash = createHash("sha256").update(bytes).digest("hex");
      descriptors.set(id, bytes);
      records.set(id, { contractId: id, contractVersion: "1.0.0", descriptorBytes: bytes.length, descriptorSha256: hash,
        abiSha256: await sdk.computeRpcAbiSha256(descriptor), artifactSource: "owned", authorityKind: "package", authorityId: "example.dependency",
        selectedAuthorityRelease: "1.0.0", selectedAuthorityContentIdentity: "1".repeat(64), etag: `"${hash}"` });
    }
    // This wire-scope fixture has compiler Request/Response envelopes, no source
    // model declarations. Its current projection is explicitly empty; descriptor
    // bytes and retained service-call scopes remain the fixture's oracle.
    const projection = projectionTools.projectionDocumentBytes("example.dependency", "1.0.0", [...records.values()].map(record => ({ ...record, modelProjection: projectionTools.emptyModelProjection() })));
    const artifact: PackageArtifact = { packageId: "example.dependency", requestedVersionRange: ">=1.0.0 <2.0.0", includePrerelease: false,
      selectedPackageVersion: "1.0.0", selectedPackageContentIdentity: "1".repeat(64), registryOrigin: undefined, publicContractCount: records.size,
      publicInventorySha256: "", modelProjectionBytes: projection.length, modelProjectionSha256: createHash("sha256").update(projection).digest("hex"), contracts: records };
    const lockBytes = Buffer.from(JSON.stringify({ lockVersion: 1, packageArtifacts: { "example.dependency": {
      requestedVersionRange: artifact.requestedVersionRange, includePrerelease: false, selectedPackageVersion: artifact.selectedPackageVersion,
      selectedPackageContentIdentity: artifact.selectedPackageContentIdentity, publicContractCount: records.size,
      publicInventorySha256: createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(lockTools.inventoryBytes(artifact)).digest("hex"),
      modelProjectionBytes: artifact.modelProjectionBytes, modelProjectionSha256: artifact.modelProjectionSha256,
      contracts: Object.fromEntries([...records].map(([id, value]) => [id, { artifactSource: value.artifactSource, authorityKind: value.authorityKind,
        authorityId: value.authorityId, selectedAuthorityRelease: value.selectedAuthorityRelease, selectedAuthorityContentIdentity: value.selectedAuthorityContentIdentity,
        resolvedContractVersion: value.contractVersion, descriptorBytes: value.descriptorBytes, descriptorSha256: value.descriptorSha256, abiSha256: value.abiSha256, etag: value.etag }])) } },
      packageImports: { "example.dependency": { localName: "dependency", presence: "required", requiredContracts: [], consumerRoles: ["app", "runtime"] } },
      providerImports: {}, platformImports: {} }, null, 2) + "\n");
    const lock = lockTools.parseContractLock(lockBytes);
    await cacheTools.publishContractCache(cache, new Map([...records].map(([id, value]) => [value.descriptorSha256, descriptors.get(id)!])), lock.requirements);
    await projectionCache.publishModelProjectionCache(cache, new Map([[artifact.packageId, projection]]), lock.packageArtifacts, new Map([...records].map(([id, value]) => [value.descriptorSha256, descriptors.get(id)!])));
    await mkdir(join(root, "src")); await mkdir(join(root, "app"));
    await writeFile(join(root, "sunder-contracts.lock.json"), lockBytes);
    await writeFile(join(root, "package.json"), JSON.stringify({ name: "service-scopes", private: true, type: "module", imports: {
      "#sunder/api": { types: "./.sunder/generated/rpc/api.d.ts", default: "./.sunder/generated/rpc/api.js" } } }));
    await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Service scopes", version: "1.0.0",
      workerCapabilities: [], services: { module: `src/services.${extension}` }, app: { root: "app", entryPoint: "index.html",
        views: [{ viewId: "example.owner.main", displayName: "Scopes", route: "/", defaultPlacement: "middle", showInHotbar: true }] } }));
    await writeFile(join(root, "app/index.html"), `<!doctype html><script type="module" src="./consumer.${extension}"></script>`);
    const source = extension === "ts" ? `import {defineService, type WorkerContext} from "@sunder/sdk";
export const messages = defineService({send(value: string): string {return value;}}, {serviceId:"messages", runtimeUse:{}});
export const otherMessages = defineService({send(value: string): string {return value;}}, {serviceId:"messages", runtimeUse:{}});`
      : `// @ts-check
import {defineService} from "@sunder/sdk";
export const messages = defineService({
  /** @param {string} value @returns {string} */
  send(value) {return value;}
}, {serviceId:"messages", runtimeUse:{}});
export const otherMessages = defineService({
  /** @param {string} value @returns {string} */
  send(value) {return value;}
}, {serviceId:"messages", runtimeUse:{}});`;
    await writeFile(join(root, `src/services.${extension}`), source);
    return { root, cache, records, descriptors, lockBytes, fromTool, source,
      load: () => model.loadProjectWithOwnedFrame(root, { contractCacheRoot: cache }),
      generate: bindings.generateRpcBindingsForLoadedProject,
      dispose: () => rm(root, { recursive: true, force: true }) };
  } catch (error) { await rm(root, { recursive: true, force: true }); throw error; }
}

export async function exerciseServiceApiScopes(toolRoot: string, extension: "ts" | "js"): Promise<void> {
  const c = await serviceApiFixture(toolRoot, extension);
  try {
    // Both Worker source checking and ordinary browser tsc/checkJs consume tokens.
    await writeFile(join(c.root, `src/services.${extension}`), c.source + `
import {Self, Dependency, Services} from "#sunder/api";
/** @param {import("@sunder/sdk").WorkerContext} context */
export function configure(context${extension === "ts" ? ": WorkerContext" : ""}) {
  const own = context.api(Self); const selected = context.api(Dependency);
  return {async onActivated() {
    const first = await selected.example_Dependency_First.messages.send({});
    const second = await selected.example_Dependency_Second.messages.send({});
    if (Services.Dependency.example_Dependency_First.messages !== Services.Contracts.CExample_Dependency_First.messages) throw new Error("Different service token");
    if (Services.Self.messages !== Services.Contracts.CExample_Owner_Messages.messages) throw new Error("Different Self token");
    await own.messages.send(first.message); await own.otherMessages.send(second.message);
  }};
}
`);
    const loaded = await c.load(); await c.generate(loaded);
    const generated = join(c.root, ".sunder/generated/rpc");
    for (const [id, before] of c.descriptors) {
      const current = loaded.contracts.find(contract => contract.descriptor.contractId === id)!; const record = c.records.get(id)!;
      assert.deepEqual(current.canonicalBytes, before); assert.equal(current.descriptorSha256, record.descriptorSha256); assert.equal(current.abiSha256, record.abiSha256);
      assert.deepEqual(await readFile(join(generated, "contracts", `${id}.rpc.json`)), before);
      assert.deepEqual(await readFile(join(c.cache, `${record.descriptorSha256}.json`)), before);
      for (const receipt of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json"]) {
        const value = JSON.parse(await readFile(join(generated, receipt), "utf8"));
        const entry = value.contracts.find((item: {contractId: string}) => item.contractId === id);
        assert.equal(entry.canonicalDescriptor, before.toString()); assert.equal(entry.descriptorSha256, record.descriptorSha256); assert.equal(entry.abiSha256, record.abiSha256);
        assert.equal(value.uses.filter((use: {contractKey: string}) => use.contractKey === id).length, 1);
      }
    }
    assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), c.lockBytes);
    await writeFile(join(c.root, `app/consumer.${extension}`), `import {Self, Dependency, Services} from "#sunder/api";
import {createBrowserContext} from "@sunder/sdk/browser";
export function connect() {
  const owner = createBrowserContext(Self); const own = owner.api(Self); const selected = owner.api(Dependency);
  own.messages.send("one").then(value => value.toUpperCase()); own.otherMessages.send("two");
  selected.example_Dependency_First.messages.send({}).then(value => value.payload?.subarray(0));
  selected.example_Dependency_Second.messages.send({}).then(value => value.metadata?.key?.toUpperCase());
  const importedToken = Services.Dependency.example_Dependency_First.messages;
  const canonicalToken = Services.Contracts.CExample_Dependency_First.messages;
  const ownToken = Services.Self.messages;
  void [importedToken, canonicalToken, ownToken];
  // @ts-expect-error No flattened service alias may silently pick one contract.
  selected.messages.send({});
  // @ts-expect-error The descriptor supplies a Request, not guessed producer parameters/Unit omission.
  selected.example_Dependency_First.messages.send();
  // @ts-expect-error Self retains its ordinary source argument shape.
  own.messages.send({value:"one"});
  // @ts-expect-error Remote named Response is not an inferred primitive result.
  selected.example_Dependency_Second.messages.send({}).then(value => value.toUpperCase());
  // @ts-expect-error The canonical tree already supplies the contract scope.
  Services.Contracts.CExample_Dependency_First.example_Dependency_First.messages;
  return owner;
}
`);
    await checkServiceApiBrowser(c, extension);
    const esbuild: typeof import("esbuild") = c.fromTool("esbuild");
    const bundled = await esbuild.build({ stdin: { contents: `export {Self, Dependency, Services} from "#sunder/api";
export {createApiContext, createBrowserContext} from "@sunder/sdk/browser";
export {connect} from "./app/consumer.${extension}";`, resolveDir: c.root, loader: "ts" }, bundle: true, platform: "browser", format: "cjs", write: false, metafile: true,
      alias: { "@sunder/sdk/browser": c.fromTool.resolve("@sunder/sdk/browser") } });
    assert.ok(Object.keys(bundled.metafile!.inputs).every(path => !/worker|node:/u.test(path)));
    assert.doesNotMatch(bundled.outputFiles[0]!.text, /runWorker|node:fs/u);
    const result = { exports: {} as any }; new Function("module", "exports", bundled.outputFiles[0]!.text)(result, result.exports);
    const api = result.exports; const calls: Array<{id: string; request: unknown}> = []; let discoveries = 0; let closed = 0;
    const transport = {
      async discover(id: string) {
        discoveries++; const contract = loaded.contracts.find(value => value.descriptor.contractId === id)!; assert.ok(contract);
        return {revision:1,sequence:1, resetRequired: false, providers: [{providerId:id+".provider",activationId:"11223344556677889900aabbccddeeff",activationEpoch:1,sessionGeneration:1,catalogRevision:1, packageId: contract.imported ? "example.dependency" : "example.owner", packageVersion: "1.0.0", state: "active",
          contractId: id, contractVersion: contract.descriptor.version, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256,
          endpointReference: id, providerHandle: id }] };
      },
      async invoke(id: string, service: string, method: string, request: unknown) {
        assert.equal(service, "messages"); assert.equal(method, "send"); calls.push({id, request});
        if (c.records.has(id)) { assert.deepEqual(request, {}); return {message:id, items:[id], metadata:{key:id}, payload:"AAEC/w=="}; }
        assert.deepEqual(request, {value:id}); return id;
      },
      async *subscribe() { throw new Error("Unexpected stream"); },
      watch:liveCatalogWatch,
      async createCallScope() { return {...transport, async close() { closed++; }}; },
    };
    const owner = api.createApiContext("example.owner", transport);
    const own = owner.api(api.Self); const selected = owner.api(api.Dependency);
    assert.deepEqual(Object.keys(own), ["messages", "otherMessages"]);
    assert.deepEqual(Object.keys(selected), ["example_Dependency_First", "example_Dependency_Second"]);
    assert.deepEqual(Object.keys(api.Services.Dependency), Object.keys(selected));
    assert.deepEqual(Object.keys(api.Services.Self), Object.keys(own));
    assert.equal(api.Services.Dependency.example_Dependency_First.messages, api.Services.Contracts.CExample_Dependency_First.messages);
    assert.equal(api.Services.Self.messages, api.Services.Contracts.CExample_Owner_Messages.messages);
    assert.equal(discoveries, 0);
    for (const token of [api.Self, api.Dependency]) assert.throws(() => owner.api({...token}), /different package|token/i);
    assert.equal(discoveries, 0);
    assert.equal(await own.messages.send("example.owner.messages"), "example.owner.messages");
    assert.equal(await own.otherMessages.send("example.owner.other.messages"), "example.owner.other.messages");
    for (const [key, id] of [["example_Dependency_First", "example.dependency.first"], ["example_Dependency_Second", "example.dependency.second"]]) {
      assert.ok(Object.isFrozen(selected[key!])); assert.ok(Object.isFrozen(selected[key!].messages));
      const value = await selected[key!].messages.send({}); assert.equal(value.message, id); assert.deepEqual(value.payload, new Uint8Array([0,1,2,255]));
    }
    const child = owner.fork(); await child.api(api.Dependency).example_Dependency_First.messages.send({}); child.dispose();
    assert.equal(closed, 5); assert.equal(calls.length, 5);
    const host = new EventTarget(); const lifetime = new AbortController();
    Object.defineProperty(host, "sunder", { value: Object.freeze({protocol:"sunder.bridge.v1", version:1, signal:lifetime.signal, rpc:transport}), writable:false, configurable:false });
    const browser = api.createBrowserContext(api.Self, host);
    assert.equal((await browser.api(api.Dependency).example_Dependency_Second.messages.send({})).message, "example.dependency.second");
    assert.deepEqual(calls.map(call => call.id), ["example.owner.messages", "example.owner.other.messages", "example.dependency.first", "example.dependency.second", "example.dependency.first", "example.dependency.second"]);
    browser.dispose(); owner.dispose(); lifetime.abort();
  } finally { await c.dispose(); }
}

export async function checkServiceApiBrowser(c: Pick<Awaited<ReturnType<typeof serviceApiFixture>>, "root" | "fromTool">, extension: "ts" | "js"): Promise<void> {
  await writeFile(join(c.root, "tsconfig.browser.json"), JSON.stringify({ compilerOptions: {
    target: "ES2023", module: "ESNext", moduleResolution: "Bundler", lib: ["ES2023", "DOM", "DOM.Iterable"], types: [], strict: true,
    allowJs: true, checkJs: true, noEmit: true, skipLibCheck: false,
    paths: { "@sunder/sdk/browser": [c.fromTool.resolve("@sunder/sdk/browser").replace(/\.js$/u, ".d.ts")],
      "@sunder/sdk/core": [c.fromTool.resolve("@sunder/sdk/core").replace(/\.js$/u, ".d.ts")] },
  }, files: [`app/consumer.${extension}`] }));
  await promisify(execFile)(process.execPath, [c.fromTool.resolve("typescript/bin/tsc"), "-p", "tsconfig.browser.json"], { cwd: c.root });
}
