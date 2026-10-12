import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { canonicalRpcDescriptorBytes, parseRpcContractDescriptor } from "@sunder/sdk";
import { parseContractLock, inventoryBytes, parseContractWireJson } from "../src/rpc-contract-lock";
import { publishContractCache, readContractCache, receiptBytes } from "../src/rpc-contract-cache";
import { restoreContracts } from "../src/rpc-contract-restore";
import { canonicalCandidateFixture, installFixtureInspector } from "./model-projection-fixtures";
import { createHash } from "node:crypto";

const ROOT = resolve(__dirname, "../../../../../../../specs/rpc/v1");
const fixture = (name: string): Promise<Buffer> => readFile(join(ROOT, "authoring/conformance/valid", name));
async function context() {
  const root = await mkdtemp(join(tmpdir(), "sunder-contract-cache-node-"));
  const raw = JSON.parse((await fixture("package-api-contract-lock.json")).toString());
  const descriptor = Buffer.from(canonicalRpcDescriptorBytes(parseRpcContractDescriptor(await readFile(join(ROOT, "conformance/valid/minimal.rpc.json")))));
  const original = parseContractLock(Buffer.from(JSON.stringify(raw,null,2)+"\n")); const requirement = [...original.requirements.values()][0]!;
  const candidate=await canonicalCandidateFixture("example.dependency",raw.packageArtifacts["example.dependency"],await fixture("rpc-model-projection-nonempty.json"),new Map([[requirement.descriptorSha256,descriptor]]));
  raw.packageArtifacts["example.dependency"]=candidate.artifact;
  const lockBytes=Buffer.from(JSON.stringify(raw,null,2)+"\n");const lock=parseContractLock(lockBytes);
  const restoreInspector=await installFixtureInspector(root);
  await writeFile(join(root, "sunder-contracts.lock.json"), lockBytes);
  const cache = join(root, "cache");
  let requests = 0;
  const fetchImplementation: typeof fetch = async (input, init) => {
    requests++; assert.equal(init?.redirect, "error"); const url = String(input);
    assert.ok(url.endsWith("/canonical"));
    const body=candidate.bytes;const headers={"content-type":"application/octet-stream","content-length":String(body.length)};
    const response = new Response(new Uint8Array(body), { status: 200, headers }); Object.defineProperty(response, "url", { value: url, configurable: true }); return response;
  };
  return { root, cache, lock, lockBytes, descriptor, requirement, candidate, fetchImplementation, requests: () => requests, dispose: async () => {restoreInspector();await rm(root, { recursive: true, force: true });} };
}

test("Node lock reader consumes the existing C# lock and cache receipt oracles", async () => {
  const names = (await readdir(join(ROOT, "authoring/conformance/valid"))).filter(name => name.endsWith("contract-lock.json"));
  for (const required of ["minimal-contract-lock.json", "mixed-contract-lock.json", "package-api-contract-lock.json", "package-api-mixed-authority-contract-lock.json"]) assert.ok(names.includes(required));
  for (const name of names) { const bytes = await fixture(name); assert.deepEqual(parseContractLock(bytes).exactBytes, bytes, name); }
  const lock = parseContractLock(await fixture("package-api-contract-lock.json"));
  assert.deepEqual(receiptBytes([...lock.requirements.values()][0]!), await fixture("cache-entry-receipt.json"));
});

test("local lock origins must be absent and malformed present origins cannot trigger offline fallback", async () => {
  const c = await context();
  try {
    const original = JSON.parse((await fixture("mixed-contract-lock.json")).toString());
    const local = structuredClone(original);
    for (const record of [...Object.values(local.packageArtifacts), ...Object.values(local.platformImports)] as Record<string, unknown>[])
      delete record.registryOrigin;
    const parsed = parseContractLock(Buffer.from(JSON.stringify(local, null, 2) + "\n"));
    assert.ok([...parsed.packageArtifacts.values()].every(value => value.registryOrigin === undefined));
    assert.ok([...parsed.platformImports.values()].every(value => value.registryOrigin === undefined));
    for (const map of ["packageArtifacts", "platformImports"]) for (const origin of [null, "", "not-an-origin", "https://registry.example/path", "https://REGISTRY.example/", false]) {
      const value = structuredClone(original); value[map][Object.keys(value[map])[0]!].registryOrigin = origin;
      const bytes = Buffer.from(JSON.stringify(value, null, 2) + "\n"); await writeFile(join(c.root, "sunder-contracts.lock.json"), bytes);
      let requests = 0;
      await assert.rejects(restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation: async () => { requests++; assert.fail("Malformed origin attempted HTTP"); } }));
      assert.equal(requests, 0);
      assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), bytes);
      await assert.rejects(stat(c.cache), { code: "ENOENT" });
    }
  } finally { await c.dispose(); }
});

test("Node lock reader rejects frozen invalid and semantic-invalid lock fixtures", async () => {
  for (const directory of ["invalid", "semantic-invalid"]) for (const name of await readdir(join(ROOT, "authoring/conformance", directory))) {
    if (!name.startsWith("contract-lock-") && !name.startsWith("lock-")) continue;
    const bytes = await readFile(join(ROOT, "authoring/conformance", directory, name)); assert.throws(() => parseContractLock(bytes), Error, name);
  }
});

test("exact online restore preserves committed bytes and warm cache timestamps", async () => {
  const c = await context();
  try {
    assert.equal(await restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation: c.fetchImplementation }), 1);
    const before = await snapshot(c.cache);
    assert.equal(await restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation: c.fetchImplementation }), 0);
    assert.equal(c.requests(), 1); assert.deepEqual(await snapshot(c.cache), before);
    assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), c.lockBytes);
    const entries = await readContractCache(c.cache, c.lock.requirements);
    assert.deepEqual(entries.get(c.requirement.descriptorSha256)!.canonicalBytes, c.descriptor);
  } finally { await c.dispose(); }
});

for (const failure of ["inventory", "authority", "weak-etag", "media-type", "redirect", "origin", "oversize", "duplicate", "lock-edit"]) {
  test(`restore rejects ${failure} without publishing cache or rewriting the lock`, async () => {
    const c = await context();
    try {
      let expectedLock=c.lockBytes;
      if(failure==="weak-etag"){
        const raw=JSON.parse(expectedLock.toString());raw.packageArtifacts["example.dependency"].contracts[c.requirement.contractId].etag=`W/"${c.requirement.descriptorSha256}"`;
        expectedLock=Buffer.from(JSON.stringify(raw,null,2)+"\n");await writeFile(join(c.root,"sunder-contracts.lock.json"),expectedLock);
      }
      const fetchImplementation: typeof fetch = async (url, init) => {
        let response = await c.fetchImplementation(url, init); const path = String(url);
        if (["inventory","authority","duplicate"].includes(failure)) {
          const recipe=structuredClone(c.candidate.recipe) as any;
          if(failure==="inventory")recipe.inspection.inventory.packageVersion="9.0.0";
          if(failure==="authority")recipe.inspection.inventory.contracts[0].authorityId="example.other";
          recipe.inspection.publicInventorySha256=createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(recipe.inspection.inventory)).digest("hex");
          if(failure==="duplicate")recipe.receiptText='{"schemaVersion":1,"schemaVersion":1}';
          const body=JSON.stringify(recipe);response=new Response(body,{headers:{"content-type":"application/octet-stream","content-length":String(Buffer.byteLength(body))}});
        }
        if (failure === "media-type") response.headers.set("content-type", "application/json");
        if (failure === "redirect") response = new Response(null, { status: 302, headers: response.headers });
        if (failure === "oversize") response.headers.set("content-length","536870913");
        if (failure === "lock-edit") await writeFile(join(c.root, "sunder-contracts.lock.json"), c.lockBytes.toString().replace('"required"', '"optional"'));
        if (response.url === "" || failure === "origin") Object.defineProperty(response, "url", { value: failure === "origin" ? "https://other.example/" : path, configurable: true });
        return response;
      };
      await assert.rejects(restoreContracts(c.root, { cacheRoot: c.cache, fetchImplementation }), /(?:public-inventory-invalid|evidence-mismatch|entry-corrupt|model-projection-invalid)/u);
      await assert.rejects(stat(c.cache), { code: "ENOENT" });
      if (failure !== "lock-edit") assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), expectedLock);
    } finally { await c.dispose(); }
  });
}

for (const point of ["prepared", "quarantined", "published", "installed", "committed"]) {
  test(`Node publication recovers after ${point} and quarantines rather than deleting corrupt bytes`, async () => {
    const c = await context();
    try {
      const entries = new Map([[c.requirement.descriptorSha256, c.descriptor]]);
      await publishContractCache(c.cache, entries, c.lock.requirements);
      const descriptorPath = join(c.cache, c.requirement.descriptorSha256 + ".json"); await writeFile(descriptorPath, "preserve this corruption");
      await assert.rejects(publishContractCache(c.cache, entries, c.lock.requirements, { checkpoint: value => {
        if (value === point || value.startsWith(point + ":")) throw new Error("interrupted");
      } }), /interrupted/u);
      if (point !== "committed") await assert.rejects(readContractCache(c.cache, c.lock.requirements), /publication is incomplete/u);
      await publishContractCache(c.cache, entries, c.lock.requirements);
      const [quarantine] = await readdir(join(c.cache, ".quarantine"));
      assert.equal(await readFile(join(c.cache, ".quarantine", quarantine!, c.requirement.descriptorSha256 + ".json"), "utf8"), "preserve this corruption");
      assert.deepEqual((await readContractCache(c.cache, c.lock.requirements)).get(c.requirement.descriptorSha256)!.canonicalBytes, c.descriptor);
    } finally { await c.dispose(); }
  });
}

test("wire JSON rejects duplicate/case-colliding members, alternate numbers and exhausted budgets", () => {
  for (const text of ['{"x":1,"x":2}', '{"x":1,"X":2}', '{"size":1e0}', '{"size":9007199254740992}', '['.repeat(17) + ']'.repeat(17)])
    assert.throws(() => parseContractWireJson(Buffer.from(text), 65_536));
});

test("CLI restore supports empty exact locks and rejects empty/override/offline fallback inputs", async () => {
  const c = await context();
  try {
    await writeFile(join(c.root, "sunder-contracts.lock.json"), await fixture("minimal-contract-lock.json"));
    const cli = resolve(__dirname, "../src/cli.js"); const run = promisify(execFile);
    const { stdout } = await run(process.execPath, [cli, "contracts", "restore", "--project", c.root, "--cache-root", c.cache]);
    assert.match(stdout, /Restored 0/u); await assert.rejects(stat(c.cache), { code: "ENOENT" });
    for (const options of [["--candidate", ""], ["--registry", "https://registry.example/"], ["--candidate", "absent.sunderpkg"], ["--platform-catalog", "absent.json"]])
      await assert.rejects(run(process.execPath, [cli, "contracts", "restore", "--project", c.root, ...options]));
    await assert.rejects(stat(c.cache), { code: "ENOENT" });
  } finally { await c.dispose(); }
});

async function snapshot(root: string, prefix=""): Promise<unknown[]> {
  const values:unknown[]=[];
  for(const name of (await readdir(root)).sort()){
    const path=join(root,name);const info=await stat(path);
    if(info.isDirectory())values.push(...await snapshot(path,prefix+name+"/"));else values.push([prefix+name,await readFile(path),info.mtimeMs]);
  }
  return values;
}
