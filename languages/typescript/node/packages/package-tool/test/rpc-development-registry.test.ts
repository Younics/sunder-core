import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { canonicalRpcDescriptorBytes, parseRpcContractDescriptor } from "@sunder/sdk";
import { inventoryBytes, parseContractLock, registryOrigin } from "../src/rpc-contract-lock";
import { restoreContracts } from "../src/rpc-contract-restore";
import { selectPackageArtifact } from "../src/rpc-package-selection";

const ROOT = resolve(__dirname, "../../../../../../../specs/rpc/v1");
const fixtures = join(ROOT, "authoring/conformance/valid");
const outer = (value: unknown) => Buffer.from(JSON.stringify(value, null, 2) + "\n");
const exec = promisify(execFile); const cli = resolve(__dirname, "../src/cli.js");
async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "sunder-development-registry-")); const cache = join(root, "cache"); const path = join(root, "sunder-contracts.lock.json");
  const original = await readFile(join(fixtures, "package-api-contract-lock.json")); const locked = parseContractLock(original);
  const artifact = locked.packageArtifacts.get("example.dependency")!; const requirement = [...locked.requirements.values()][0]!;
  const descriptor = Buffer.from(canonicalRpcDescriptorBytes(parseRpcContractDescriptor(await readFile(join(ROOT, "conformance/valid/minimal.rpc.json")))));
  const setOrigin = async (origin: string): Promise<Buffer> => {
    const value = JSON.parse(original.toString()); value.packageArtifacts[artifact.packageId].registryOrigin = origin;
    const bytes = outer(value); await writeFile(path, bytes); return bytes;
  };
  await writeFile(path, original);
  return { root, cache, path, original, locked, artifact, requirement, descriptor, setOrigin,
    run: (...args: string[]) => exec(process.execPath, [cli, ...args, "--project", root], { env: { ...process.env, SUNDER_CONTRACT_CACHE: cache }, timeout: 180_000, maxBuffer: 4_194_304 }),
    dispose: () => rm(root, { recursive: true, force: true }) };
}

for (const origin of ["http://localhost/", "http://localhost:43127/"]) test(`Development origin ${origin} requires explicit Boolean opt-in`, async () => {
  const c = await fixture();
  try {
    const bytes = await c.setOrigin(origin);
    assert.throws(() => parseContractLock(bytes)); assert.throws(() => registryOrigin(origin));
    assert.equal(registryOrigin(origin, true), origin); assert.deepEqual(parseContractLock(bytes, true).exactBytes, bytes);
    for (const mode of ["true", 1, null]) assert.throws(() => parseContractLock(bytes, mode as unknown as boolean));
    let requests = 0;
    await assert.rejects(restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation: async () => { requests++; throw new Error("Unexpected HTTP"); } }));
    assert.equal(requests, 0); await assert.rejects(stat(c.cache), { code: "ENOENT" });
  } finally { await c.dispose(); }
});

for (const origin of ["http://127.0.0.1:43127/", "http://[::1]:43127/", "http://localhost.example/", "http://LOCALHOST:43127/",
  "http://localhost:80/", "http://localhost:043127/", "http://localhost:0/", "http://localhost:65536/", "http://user@localhost/",
  "http://localhost/path/", "http://localhost/?query=1", "http://localhost/#fragment"]) test(`Development mode rejects noncanonical ${origin} without HTTP or cache writes`, async () => {
  const c = await fixture();
  try {
    const before = await c.setOrigin(origin); let requests = 0;
    const fetchImplementation: typeof fetch = async () => { requests++; throw new Error("Unexpected HTTP"); };
    await assert.rejects(restoreContracts(c.root, { cacheRoot: c.cache, allowDevelopmentRegistryOrigin: true, fetchImplementation }));
    await assert.rejects(selectPackageArtifact(c.artifact.packageId, { registryOrigin: origin, versionRange: ">=1.0.0 <2.0.0", includePrerelease: false,
      allowDevelopmentRegistryOrigin: true, fetchImplementation }));
    assert.equal(requests, 0); assert.deepEqual(await readFile(c.path), before); await assert.rejects(stat(c.cache), { code: "ENOENT" });
  } finally { await c.dispose(); }
});

test("actual localhost CLI selection/restore preserve exact provenance and offline build/pack require the mode again", { timeout: 180_000 }, async () => {
  const c = await fixture(); let requests = 0;
  const server = createServer((request, response) => {
    requests++; let body: Buffer; const headers: Record<string, string> = { "content-type": "application/json" };
    if (request.url!.includes("/versions?")) {
      body = Buffer.from(JSON.stringify([{ version: c.artifact.selectedPackageVersion, isYanked: false }]));
      Object.assign(headers, { "X-Total-Count": "1", "X-Skip": "0", "X-Take": "100" });
    } else if (request.url!.endsWith("/public-api")) body = Buffer.from(JSON.stringify({ inventory: JSON.parse(inventoryBytes(c.artifact).toString()), publicInventorySha256: c.artifact.publicInventorySha256 }));
    else if (request.url!.endsWith("/descriptor")) { body = c.descriptor; headers["content-type"] = "application/schema+json"; headers.etag = `"${c.requirement.descriptorSha256}"`; }
    else body = Buffer.from(JSON.stringify({ contractId: c.requirement.contractId, version: c.requirement.contractVersion,
      descriptorSha256: c.requirement.descriptorSha256, abiSha256: c.requirement.abiSha256, size: c.requirement.descriptorBytes,
      etag: `"${c.requirement.descriptorSha256}"`, exposure: "public", firstPublisherPackageId: c.artifact.packageId }));
    response.writeHead(200, { ...headers, "content-length": String(body.length) }); response.end(body);
  });
  try {
    await new Promise<void>((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); });
    const address = server.address(); assert.ok(address && typeof address === "object"); const origin = `http://localhost:${address.port}/`;
    await mkdir(join(c.root, "src"));
    await writeFile(join(c.root, "sunder-contracts.owned.json"), await readFile(join(fixtures, "minimal-package-owned-baseline.json")));
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Local Registry", version: "1.0.0",
      entry: "src/worker.ts", workerCapabilities: [], rpc: { module: "src/rpc.ts", export: "packageRpc" } }));
    await writeFile(join(c.root, "src/rpc.ts"), 'import {rpc} from "@sunder/sdk"; export const packageRpc=rpc.router({contracts:{},providers:{}});');
    await writeFile(join(c.root, "src/worker.ts"), 'export {packageRpc} from "./rpc.js"; export {packageApis} from "#sunder/rpc";');
    await writeFile(c.path, await readFile(join(fixtures, "minimal-contract-lock.json")));
    const add = ["api", "add", c.artifact.packageId, "--version", c.artifact.requestedVersionRange, "--registry", origin, "--cache-root", c.cache];
    await assert.rejects(c.run(...add), /registry-origin-invalid/u); assert.equal(requests, 0);
    await c.run(...add, "--development-registry"); const selected = await readFile(c.path);
    assert.equal(parseContractLock(selected, true).packageArtifacts.get(c.artifact.packageId)!.registryOrigin, origin);
    const count = requests; const time = (await stat(c.path)).mtimeMs;
    await c.run(...add, "--development-registry"); assert.equal(requests, count); assert.equal((await stat(c.path)).mtimeMs, time);
    await c.run("api", "update", "--development-registry", "--cache-root", c.cache); assert.deepEqual(await readFile(c.path), selected);
    await assert.rejects(c.run("api", "update", "--registry", origin, "--development-registry"), /package-api.registry-origin-invalid/u);
    await c.run("contracts", "restore", "--cache-root", c.cache, "--development-registry");
    await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    const networkCount = requests;
    await assert.rejects(c.run("generate"), /registry-origin-invalid/u);
    await c.run("generate", "--development-registry");
    await c.run("contracts", "check", "--development-registry"); await c.run("contracts", "freeze", "--development-registry");
    await c.run("build", "--development-registry");
    await assert.rejects(c.run("package", "--discovery-root", join(c.root, "dist/targets")), /registry-origin-invalid/u);
    await c.run("package", "--discovery-root", join(c.root, "dist/targets"), "--development-registry");
    const manifest = JSON.parse(await readFile(join(c.root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.equal(manifest.packageArtifactImports[0].selectedPackageContentIdentity, c.artifact.selectedPackageContentIdentity);
    assert.deepEqual(await readFile(c.path), selected); assert.equal(requests, networkCount);
    await assert.rejects(c.run("api", "list"), /registry-origin-invalid/u); await c.run("api", "list", "--development-registry");
    await assert.rejects(c.run("generate", "--development-registry", "--development-registry"), /Duplicate Development/u);
  } finally { if (server.listening) await new Promise<void>(resolve => server.close(() => resolve())); await c.dispose(); }
});

test("explicit Development transport still rejects redirects and response-origin changes", async () => {
  const c = await fixture();
  try {
    const before = await c.setOrigin("http://localhost:43127/");
    for (const changedOrigin of [false, true]) {
      let requests = 0;
      await assert.rejects(restoreContracts(c.root, { cacheRoot: c.cache, allowDevelopmentRegistryOrigin: true, fetchImplementation: async input => {
        requests++; const response = new Response("{}", { status: changedOrigin ? 200 : 302, headers: { "content-type": "application/json" } });
        Object.defineProperty(response, "url", { value: changedOrigin ? "http://localhost:43128/" : String(input) }); return response;
      } }));
      assert.equal(requests, 1); assert.deepEqual(await readFile(c.path), before); await assert.rejects(stat(c.cache), { code: "ENOENT" });
    }
  } finally { await c.dispose(); }
});
