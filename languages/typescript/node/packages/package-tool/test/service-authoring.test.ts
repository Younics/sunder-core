import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, readFile, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { resolve } from "node:path";
import test, { after } from "node:test";
import { loadProject } from "../src/model";
import { generateRpcBindings } from "../src/rpc-bindings";
import { contractsCommand } from "../src/rpc-contracts";
import { exercisePackedScaffolds } from "./packed-scaffolds";
import { exercisePackedPublicOwner } from "./packed-public-owner";
import { createPackedServiceTools } from "./packed-service-tools";
import { build } from "esbuild";

async function project(source: string, extension: string) {
  const root = await mkdtemp(join(tmpdir(), "sunder-services-")); await mkdir(join(root, "src"));
  await writeFile(join(root, `src/services.${extension}`), source);
  await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.services", name: "Services", version: "2.0.0-beta.1+build.7", workerCapabilities: [], services: { module: `src/services.${extension}` } }));
  await writeFile(join(root, "sunder-contracts.lock.json"), JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2) + "\n");
  return root;
}
const typescript = `import {defineService} from "@sunder/sdk";
export const greeting = defineService({
 async greet(name: string): Promise<string> { return "Hello " + name; },
 async reset(): Promise<void> {},
 async *watch(name: string): AsyncIterable<string> { yield name; }
});`;
const javascript = `// @ts-check
import {defineService} from "@sunder/sdk";
export const greeting = defineService({
 /** @param {string} name @returns {Promise<string>} */
 async greet(name) { return "Hello " + name; },
 /** @returns {Promise<void>} */
 async reset() {},
 /** @param {string} name @returns {AsyncIterable<string>} */
 async *watch(name) { yield name; }
});`;

test("ordinary TS and genuine checked JS derive identical primitive/void/stream services and exact private package versions", async () => {
  let expected: Buffer | undefined;
  for (const [extension, source] of [["ts", typescript], ["js", javascript]] as const) {
    const root = await project(source, extension);
    try {
      const loaded = await loadProject(root); const contract = loaded.contracts[0]!;
      assert.equal(contract.descriptor.version, "2.0.0-beta.1+build.7");
      if (expected) assert.deepEqual(contract.canonicalBytes, expected); else expected = contract.canonicalBytes;
      assert.equal(contract.descriptor.services.greeting!.methods.watch!.kind, "server-stream");
      assert.deepEqual(JSON.parse(contract.canonical).$defs.GreetingGreetRequest, { type: "object", properties: { name: { type: "string" } }, required: ["name"], additionalProperties: false });
      await generateRpcBindings(root); const api = await readFile(join(root, ".sunder/generated/rpc/api.d.ts"), "utf8");
      assert.match(api, /greet\(name:.*\$options\?/u); assert.match(api, /reset\(\$options\?.*Promise<void>/u);
      assert.equal(await contractsCommand("check", root), false); assert.equal(await contractsCommand("freeze", root), false);
      await assert.rejects(stat(join(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("source inference rejects incomplete JS and unsupported values without executing source or replacing output", async () => {
  const root = await project(javascript, "js");
  try {
    await generateRpcBindings(root); const path = join(root, ".sunder/generated/rpc/authoring-receipt.v1.json"); const before = await readFile(path);
    await writeFile(join(root, "src/services.js"), javascript.replace("/** @param {string} name @returns {Promise<string>} */", ""));
    await assert.rejects(generateRpcBindings(root), /Incomplete\/nonportable|handler-incomplete/);
    assert.deepEqual(await readFile(path), before);
    await writeFile(join(root, "src/services.js"), `throw new Error("DO NOT EXECUTE");\n${javascript}`);
    await generateRpcBindings(root);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("ordinary public fixture source preserves its complete frozen descriptor and typed error details", async () => {
  const root = resolve(__dirname, "../../../../fixtures/runtime-backed-worker");
  const before = await readFile(join(root, "sunder-contracts.owned.json"));
  const loaded = await loadProject(root); const accepted = JSON.parse(before.toString()).contracts.messages.current;
  assert.equal(loaded.contracts[0]!.canonical, accepted.canonicalDescriptor);
  assert.equal(loaded.contracts[0]!.descriptorSha256, accepted.descriptorSha256);
  assert.equal(loaded.contracts[0]!.abiSha256, accepted.abiSha256);
  assert.deepEqual(loaded.contracts[0]!.appUse, {required:false,actions:["discover","invoke","subscribe"]});
  assert.deepEqual(loaded.config.usesContracts, [], "A Runtime-only fixture gets no App or implicit Runtime grant.");
  assert.doesNotMatch(loaded.ownedRpc!.files.get("api.d.ts")!, /["']@sunder\/sdk["']/u);
  assert.deepEqual(await readFile(join(root, "sunder-contracts.owned.json")), before);
});
test("ordinary record/list/map/nullable/recursive types reuse validating logical-value codecs", async () => {
  const source = `import {defineService} from "@sunder/sdk";
type Item = { label: string; next?: Item };
type Payload = { count: bigint; bytes: Uint8Array; enabled: boolean | null; note?: string | null; scores: Record<string, number>; items: readonly Item[] };
function roundTrip(request: Payload): Payload {
  if (typeof request.count !== "bigint" || !(request.bytes instanceof Uint8Array)) throw new Error("Missing logical codec");
  return request;
}
export const echo = defineService({roundTrip});`;
  const root = await project(source, "ts");
  try {
    await generateRpcBindings(root);
    const built = await build({ entryPoints: [join(root, ".sunder/generated/rpc/adapter.ts")], platform: "node", format: "cjs", bundle: true, write: false,
      alias: { "@sunder/sdk": require.resolve("@sunder/sdk"), "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser") } });
    const loaded = { exports: {} as { providers?: Array<{ handler: { invokeUnary(context: unknown, service: string, method: string, request: unknown): Promise<unknown> } }> } };
    new Function("require", "module", "exports", built.outputFiles[0]!.text)(require, loaded, loaded.exports);
    const invoke = (value: unknown) => loaded.exports.providers![0]!.handler.invokeUnary({ signal: new AbortController().signal }, "echo", "round-trip", value);
    const value = { count: "9007199254740993000", bytes: "AAEC/w==", enabled: null, scores: { first: 1.5 }, items: [{ label: "one", next: { label: "two" } }] };
    assert.deepEqual(await invoke(value), value);
    assert.deepEqual(await invoke({ ...value, note: null, enabled: false }), { ...value, note: null, enabled: false });
    await assert.rejects(async () => invoke({ ...value, count: 42 }));
    await assert.rejects(async () => invoke({ ...value, enabled: "false" }));
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("declaration-only services bind multiple checked implementations through the same frontend", async () => {
  for (const extension of ["ts", "js"]) {
    const declaration = extension === "ts"
      ? 'interface Greeting { greet(name: string): Promise<string> } export const greeting = defineService<Greeting>(null, {declarationOnly:true, exposure:"public"});'
      : '/** @typedef {{greet: (name: string) => Promise<string>}} Greeting */\n/** @type {import("@sunder/sdk").ServiceDefinition<Greeting>} */\nexport const greeting = defineService(null, {declarationOnly:true, exposure:"public"});';
    const source = `import {defineService} from "@sunder/sdk";\n${declaration}
export const first = defineService({async greet(name){return "first " + name;}}, {implements:greeting, providerId:"example.first"});
export const second = defineService({async greet(name){return "second " + name;}}, {implements:greeting, providerId:"example.second"});`;
    const root = await project(source, extension);
    try {
      await contractsCommand("accept", root);
      const loaded = await loadProject(root);
      assert.equal(loaded.contracts.length, 1); assert.equal(loaded.config.providers.length, 2);
      assert.deepEqual(loaded.config.providers.map(provider => provider.providerId), ["example.first", "example.second"]);
      await writeFile(join(root, `src/services.${extension}`), source.replace('providerId:"example.second"', 'providerId:"example.first"'));
      await assert.rejects(loadProject(root), /distinct explicit provider IDs/);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

const packedTools = createPackedServiceTools();
after(() => packedTools.dispose());

test("packed external service scaffolds run TS and independently authored JS in Node and React presets", { skip: process.env.SUNDER_TEST_PACKED_SERVICES !== "1", timeout: 1_800_000 }, async () => {
  const { archives, creator } = await packedTools.get();
  await exercisePackedScaffolds(archives, creator);
});
test("packed public owner UI consumes TS and genuine JS services without extra App policy", { skip: process.env.SUNDER_TEST_PACKED_SERVICES !== "1", timeout: 1_800_000 }, async () => {
  const { archives, creator } = await packedTools.get();
  await exercisePackedPublicOwner(archives, creator);
});
