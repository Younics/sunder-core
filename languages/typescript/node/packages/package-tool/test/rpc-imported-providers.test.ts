import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { mkdtemp, mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { inventoryBytes, parseContractLock } from "../src/rpc-contract-lock";
import { publishContractCache } from "../src/rpc-contract-cache";
import { currentRid, loadProjectWithOwnedFrame } from "../src/model";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { packageApiCommand, type PackageApiRequest } from "../src/rpc-package-commands";

const ROOT = resolve(__dirname, "../../../../../../../specs/rpc/v1");
const exec = promisify(execFile); const cli = resolve(__dirname, "../src/cli.js");
const source = (providers = 'extension: imported.extension.provider({handlers:{example:{ping(request, context){context.signal.throwIfAborted(); return request;}}}})') =>
  `import {rpc} from "@sunder/sdk"; import {rpcImports as imported} from "#sunder/rpc";
export const packageRpc=rpc.router({contracts:{},providers:{${providers}}});`;
async function fixture(extension = "ts", consumer = false, stream = false) {
  const root = await mkdtemp(join(tmpdir(), "sunder-imported-providers-")); const cache = join(root, "cache"); await mkdir(join(root, "src"));
  const value = JSON.parse(await readFile(join(ROOT, "authoring/conformance/valid/package-api-contract-lock.json"), "utf8"));
  if (consumer) { value.packageImports["example.dependency"].presence = "optional"; value.packageImports["example.dependency"].requiredContracts = []; }
  else value.packageImports = {};
  value.providerImports = { extension: { packageId: "example.dependency", contractId: "example.rpc.v1.minimal" } };
  const descriptorValue = JSON.parse(await readFile(join(ROOT, "conformance/valid/minimal.rpc.json"), "utf8"));
  if (stream) descriptorValue.services.example.methods.watch = { kind: "server-stream", idempotency: "none", errors: [], requestSchema: { $ref: "#/$defs/Empty" }, eventSchema: { $ref: "#/$defs/Empty" } };
  const descriptor = parseRpcContractDescriptor(JSON.stringify(descriptorValue)); const bytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
  const hash = createHash("sha256").update(bytes).digest("hex"); const abi = await computeRpcAbiSha256(descriptor);
  if (stream) {
    const artifact = value.packageArtifacts["example.dependency"]; const contract = artifact.contracts[descriptor.contractId];
    Object.assign(contract, { descriptorBytes: bytes.length, descriptorSha256: hash, abiSha256: abi, etag: `"${hash}"` });
    const inventory = { schemaVersion: 1, packageId: "example.dependency", packageVersion: artifact.selectedPackageVersion, packageContentIdentity: artifact.selectedPackageContentIdentity,
      publicContractCount: 1, contracts: [{ contractId: descriptor.contractId, contractVersion: descriptor.version, descriptorBytes: bytes.length, descriptorSha256: hash, abiSha256: abi,
        artifactSource: contract.artifactSource, authorityKind: contract.authorityKind, authorityId: contract.authorityId,
        selectedAuthorityRelease: contract.selectedAuthorityRelease, selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity }] };
    artifact.publicInventorySha256 = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex");
  }
  const original = Buffer.from(JSON.stringify(value, null, 2) + "\n"); const locked = parseContractLock(original);
  await publishContractCache(cache, new Map([[hash, bytes]]), locked.requirements);
  const lockPath = join(root, "sunder-contracts.lock.json"); await writeFile(lockPath, original);
  await writeFile(join(root, "sunder-contracts.owned.json"), await readFile(join(ROOT, "authoring/conformance/valid/minimal-package-owned-baseline.json")));
  const sourcePath = join(root, `src/rpc.${extension}`);
  await writeFile(sourcePath, stream ? source().replace("return request;}", "return request;}, async *watch(request, context){context.signal.throwIfAborted(); yield request;}") : source());
  await writeFile(join(root, `src/worker.${extension}`), 'export {packageRpc} from "./rpc.js";');
  await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Imported Provider", version: "1.0.0",
    entry: `src/worker.${extension}`, workerCapabilities: [], rpc: { module: `src/rpc.${extension}`, export: "packageRpc" } }));
  const load = () => loadProjectWithOwnedFrame(root, { contractCacheRoot: cache });
  const run = (...args: string[]) => exec(process.execPath, [cli, ...args, "--project", root], { env: { ...process.env, SUNDER_CONTRACT_CACHE: cache }, timeout: 180_000, maxBuffer: 4_194_304 });
  const request: typeof fetch = async (input, init) => {
    const url = String(input); assert.equal(init?.redirect, "error"); const artifact = locked.packageArtifacts.get("example.dependency")!;
    let body: string | Uint8Array; let headers: Record<string, string> = { "content-type": "application/json" };
    if (url.endsWith("/public-api")) body = JSON.stringify({ inventory: JSON.parse(inventoryBytes(artifact).toString()), publicInventorySha256: artifact.publicInventorySha256 });
    else if (url.endsWith("/descriptor")) { body = bytes; headers = { "content-type": "application/schema+json", etag: `"${hash}"` }; }
    else body = JSON.stringify({ contractId: descriptor.contractId, version: descriptor.version, descriptorSha256: hash, abiSha256: abi, size: bytes.length, etag: `"${hash}"`, exposure: "public", firstPublisherPackageId: "example.dependency" });
    const response = new Response(typeof body === "string" ? body : new Uint8Array(body), { headers }); Object.defineProperty(response, "url", { value: url }); return response;
  };
  const command = (input: PackageApiRequest) => packageApiCommand(root, { ...input, cacheRoot: cache, fetchImplementation: request });
  return { root, cache, sourcePath, lockPath, original, locked, hash, descriptor, load, run, command, dispose: () => rm(root, { recursive: true, force: true }) };
}

for (const extension of ["ts", "js"]) test(`imported ${extension} providers compile from the cache and dispatch unary/stream values through generated codecs`, { timeout: 180_000 }, async () => {
  const c = await fixture(extension, false, true);
  try {
    const loaded = await c.load(); assert.equal(loaded.ownedRpc!.contracts.length, 0); assert.equal(loaded.config.providers.length, 1);
    assert.deepEqual(loaded.config.usesContracts, []); assert.equal(loaded.ownedRpc!.imports.dependencies[0]!.presence, "required");
    await generateRpcBindingsForLoadedProject(loaded);
    const receipt = JSON.parse(loaded.ownedRpc!.files.get("authoring-receipt.v1.json")!);
    assert.equal(receipt.providerImports[0].key, "extension"); assert.equal(receipt.providerImports[0].providerRefs[0].providerId, c.descriptor.contractId + ".provider");
    assert.equal(receipt.providers[0].contractKey, c.descriptor.contractId); assert.deepEqual(receipt.packageApiImports, []);
    await c.run("build"); await c.run("package", "--discovery-root", join(c.root, "dist/targets"));
    const manifest = JSON.parse(await readFile(join(c.root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.equal(manifest.providerImports[0].providerRefs.length, 1); assert.deepEqual(manifest.usesContracts, []); assert.equal(manifest.dependsOn[0].presence, "required");
    const worker = createRequire(__filename)(join(c.root, "dist/targets", currentRid(), "payload/runtime", currentRid(), "bin/worker.cjs"));
    const registration = worker.packageRpc.providerRegistrations[0]; assert.equal(registration.descriptorSha256, c.hash);
    const context = { signal: new AbortController().signal };
    assert.deepEqual(await registration.handler.invokeUnary(context, "example", "ping", {}), {});
    await assert.rejects(async () => registration.handler.invokeUnary(context, "example", "ping", { unknown: true }));
    const values = []; for await (const value of registration.handler.invokeServerStream(context, "example", "watch", {})) values.push(value);
    assert.deepEqual(values, [{}]); assert.deepEqual(await readFile(c.lockPath), c.original);
  } finally { await c.dispose(); }
});

test("provider intent requires a complete implementation and never borrows a consumer's optional presence", async () => {
  const c = await fixture("ts", true);
  try {
    const loaded = await c.load(); assert.equal(loaded.ownedRpc!.imports.dependencies[0]!.presence, "required");
    assert.equal(loaded.config.usesContracts![0]!.required, false); assert.equal(loaded.contracts.length, 1);
    await generateRpcBindingsForLoadedProject(loaded); const receiptPath = join(c.root, ".sunder/generated/rpc/authoring-receipt.v1.json"); const before = await readFile(receiptPath);
    await writeFile(c.sourcePath, source("")); await assert.rejects(c.load(), /handler-incomplete.*no local implementation/u);
    await rm(join(c.cache, c.hash + ".json")); await writeFile(c.sourcePath, "not even valid TypeScript");
    await assert.rejects(c.load(), /contract-cache.entry-missing/u); assert.deepEqual(await readFile(receiptPath), before);
  } finally { await c.dispose(); }
});

for (const extension of ["ts", "js"]) test(`imported ${extension} handlers retain contextual checking and reject source spoofing without executing source`, { timeout: 180_000 }, async () => {
  const c = await fixture(extension);
  try {
    const variants = [
      source().replace("return request", "return request.missing"), source().replace("ping(request, context)", "other(request, context)"),
      source().replace("imported.extension.provider", "imported.missing.provider"),
      source().replace('from "#sunder/rpc"', 'from "./fake.js"'),
      source().replace("imported.extension.provider", "builder").replace("export const", "const builder=imported.extension.provider; export const"),
      source().replace("imported.extension.provider", "imported['extension'].provider"),
      source().replace("{handlers:", '{contract:"example.rpc.v1.minimal",handlers:'),
      source().replace("imported.extension.provider({handlers:", 'rpc.provider({contract:"example.rpc.v1.minimal",handlers:'),
    ];
    await writeFile(join(c.root, "src/fake.js"), 'throw new Error("SOURCE WAS EXECUTED"); export const rpcImports={extension:{provider(value){return value;}}};');
    for (const [index, text] of variants.entries()) {
      await writeFile(c.sourcePath, text);
      await assert.rejects(c.load(), error => error instanceof Error && /rpc-authoring\./u.test(error.message) && !error.message.includes("SOURCE WAS EXECUTED"), `source variant ${index}`);
      assert.deepEqual(await readFile(c.lockPath), c.original);
    }
    await assert.rejects(stat(join(c.root, ".sunder/generated/rpc")), { code: "ENOENT" });
  } finally { await c.dispose(); }
});

test("multiple imported implementations require explicit distinct IDs and all retain their exact provider refs", async () => {
  const c = await fixture();
  try {
    const binding = 'imported.extension.provider({handlers:{example:{ping(request){return request;}}}})';
    await writeFile(c.sourcePath, source(`one:${binding},two:${binding}`)); await assert.rejects(c.load(), /explicit IDs/u);
    await writeFile(c.sourcePath, source(`one:${binding.replace("{handlers", '{id:"example.one",handlers')},two:${binding.replace("{handlers", '{id:"example.two",handlers')}`));
    const loaded = await c.load(); const receipt = JSON.parse(loaded.ownedRpc!.files.get("authoring-receipt.v1.json")!);
    assert.deepEqual(receipt.providerImports[0].providerRefs.map((value: { providerId: string }) => value.providerId), ["example.one", "example.two"]);
    await writeFile(c.sourcePath, (await readFile(c.sourcePath, "utf8")).replace("example.two", "example.one"));
    await assert.rejects(c.load(), /identity-collision/u);
  } finally { await c.dispose(); }
});

for (const extension of ["ts", "js"]) test(`${extension} implement/unimplement before first Accept stages only the selected missing handler and protects live bindings`, { timeout: 180_000 }, async () => {
  const c = await fixture(extension, true);
  try {
    await rm(join(c.root, "sunder-contracts.owned.json"));
    await writeFile(c.sourcePath, source(""));
    await c.run("api", "unimplement", "example.dependency", c.descriptor.contractId);
    assert.equal(parseContractLock(await readFile(c.lockPath)).packageImports.size, 1);
    assert.ok((await c.command({ operation: "implement", packageId: "example.dependency", contractId: c.descriptor.contractId, localName: "extension" })).changed);
    const before = await readFile(c.lockPath); const time = (await stat(c.lockPath)).mtimeMs;
    assert.equal((await c.command({ operation: "implement", packageId: "example.dependency", contractId: c.descriptor.contractId })).changed, false);
    assert.equal((await stat(c.lockPath)).mtimeMs, time);
    await assert.rejects(c.load(), /no local implementation/u);
    await writeFile(c.sourcePath, source()); await c.run("contracts", "accept"); await c.load();
    await assert.rejects(c.run("api", "unimplement", "example.dependency", c.descriptor.contractId), /remove-still-referenced/u);
    assert.deepEqual(await readFile(c.lockPath), before);
    await c.command({ operation: "remove", packageId: "example.dependency" });
    const onlyProvider = parseContractLock(await readFile(c.lockPath)); assert.equal(onlyProvider.packageArtifacts.size, 1); assert.equal(onlyProvider.providerImports.size, 1);
    await writeFile(c.sourcePath, source("")); await c.run("api", "unimplement", "example.dependency", c.descriptor.contractId);
    assert.equal(parseContractLock(await readFile(c.lockPath)).packageArtifacts.size, 0); assert.ok((await stat(join(c.cache, c.hash + ".json"))).size > 0);
    await assert.rejects(c.run("api", "unimplement", "example.dependency", c.descriptor.contractId), /import-not-found/u);
  } finally { await c.dispose(); }
});

test("provider commands reject inapplicable intent and changed tuple naming before network or lock edits", async () => {
  const c = await fixture();
  try {
    let calls = 0;
    for (const input of [
      { operation: "implement", packageId: "example.dependency", contractId: c.descriptor.contractId, presence: "optional" },
      { operation: "implement", packageId: "example.dependency", contractId: c.descriptor.contractId, localName: "different" },
      { operation: "unimplement", packageId: "example.dependency", contractId: c.descriptor.contractId, candidate: "archive.sunderpkg" },
      { operation: "implement", packageId: "example.dependency", contractId: "example.absent" },
    ] as const) {
      await assert.rejects(packageApiCommand(c.root, { ...input, cacheRoot: c.cache, fetchImplementation: async () => { calls++; assert.fail("Unexpected network"); } }));
      assert.equal(calls, 0);
      assert.deepEqual(await readFile(c.lockPath), c.original);
    }
  } finally { await c.dispose(); }
});

for (const extension of ["ts", "js"]) test(`mixed ${extension} owned/imported shared handlers keep every contextual constraint in both provider orders`, { timeout: 180_000 }, async () => {
  const c = await fixture(extension);
  try {
    const text = (valid: boolean, reverse: boolean) => {
      const providers = ['local:rpc.provider({contract:"local",handlers})', 'extension:imported.extension.provider({handlers})'];
      if (reverse) providers.reverse();
      return `import {rpc} from "@sunder/sdk"; import {rpcImports as imported} from "#sunder/rpc"; const s=rpc.schema;
const handlers={example:{ping(request,context){context.signal.throwIfAborted(); return ${valid ? "{}" : "42"};}}};
export const packageRpc=rpc.router({contracts:{local:rpc.contract({exposure:"package",errors:{},definitions:{Input:s.object({}),Output:s.json({maxProperties:5})},
services:{example:{ping:rpc.unary({request:s.ref("Input"),response:s.ref("Output")})}}})},providers:{${providers.join(",")}}});`;
    };
    for (const reverse of [false, true]) { await writeFile(c.sourcePath, text(false, reverse)); await assert.rejects(c.load(), /handler-incomplete/u); }
    await writeFile(c.sourcePath, text(true, false)); await c.run("contracts", "accept"); await c.run("contracts", "freeze");
    const owned = await readFile(join(c.root, "sunder-contracts.owned.json"));
    assert.deepEqual(Object.keys(JSON.parse(owned.toString()).contracts), ["local"]);
    for (const reverse of [false, true]) {
      await writeFile(c.sourcePath, text(true, reverse)); const loaded = await c.load();
      assert.equal(loaded.ownedRpc!.contracts.length, 1); assert.equal(loaded.contracts.length, 2); assert.equal(loaded.config.providers.length, 2);
    }
    await c.run("build"); await c.run("package", "--discovery-root", join(c.root, "dist/targets"));
    assert.deepEqual(await readFile(join(c.root, "sunder-contracts.owned.json")), owned);
  } finally { await c.dispose(); }
});

test("removing an authority selection cannot strand a still-referenced provider reexport", async () => {
  const c = await fixture();
  try {
    const value = JSON.parse(c.original.toString()); const artifact = structuredClone(value.packageArtifacts["example.dependency"]);
    artifact.selectedPackageContentIdentity = "3".repeat(64); artifact.contracts[c.descriptor.contractId].artifactSource = "imported";
    const inventory = JSON.parse(inventoryBytes(c.locked.packageArtifacts.get("example.dependency")!).toString());
    inventory.packageId = "example.reexport"; inventory.packageContentIdentity = artifact.selectedPackageContentIdentity; inventory.contracts[0].artifactSource = "imported";
    artifact.publicInventorySha256 = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex");
    value.packageArtifacts["example.reexport"] = artifact;
    value.providerImports.reexport = { packageId: "example.reexport", contractId: c.descriptor.contractId };
    const original = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(c.lockPath, original);
    await writeFile(c.sourcePath, source(""));
    await assert.rejects(c.command({ operation: "unimplement", packageId: "example.dependency", contractId: c.descriptor.contractId }), /authority-release-conflict/u);
    assert.deepEqual(await readFile(c.lockPath), original);
    value.packageImports["example.dependency"] = { localName: "dependency", presence: "optional", requiredContracts: [] };
    const retained = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(c.lockPath, retained);
    await assert.rejects(c.command({ operation: "unimplement", packageId: "example.dependency", contractId: c.descriptor.contractId }), /handler-incomplete.*reexport/u);
    assert.deepEqual(await readFile(c.lockPath), retained);
  } finally { await c.dispose(); }
});
