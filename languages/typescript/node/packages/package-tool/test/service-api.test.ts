import assert from "node:assert/strict";
import { cp, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";
import ts from "typescript";
import { serviceApiSource } from "../src/service-bindings";
import { preparePackageImports } from "../src/rpc-package-imports";
import { parseContractLock } from "../src/rpc-contract-lock";
import { checkServiceApiBrowser, exerciseServiceApiScopes, serviceApiFixture } from "./service-api-fixtures";

const toolRoot = resolve(__dirname, "../src");
for (const extension of ["ts", "js"] as const) test(`${extension} two-contract service scopes preserve Self, selected APIs, browser declarations and wire hashes`, async () => {
  await exerciseServiceApiScopes(toolRoot, extension);
});

test("contract scopes preserve segment separators and escape leading digits without false collisions", async () => {
  const c = await serviceApiFixture(toolRoot, "ts", ["example.dependency.foo.1", "example.dependency.foo1", "1.example"]);
  try {
    await writeFile(join(c.root, "src/services.ts"), c.source + `
import {Dependency, Services} from "#sunder/api";
export function configure(context: WorkerContext) {
  const api = context.api(Dependency);
  return {async onActivated() {
    await api.example_Dependency_Foo_1.messages.send({});
    await api.example_Dependency_Foo1.messages.send({});
    await api._1_Example.messages.send({});
    void [Services.Dependency.example_Dependency_Foo_1.messages, Services.Dependency.example_Dependency_Foo1.messages, Services.Dependency._1_Example.messages];
  }};
}`);
    const loaded = await c.load(); await c.generate(loaded);
    assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), c.lockBytes);
    for (const [id, bytes] of c.descriptors) assert.deepEqual(loaded.contracts.find(contract => contract.descriptor.contractId === id)!.canonicalBytes, bytes);
  } finally { await c.dispose(); }
});

test("single-contract imports have contract scopes and true service collisions remain rejected", async () => {
  const c = await serviceApiFixture(toolRoot, "ts", ["example.dependency.first"]);
  try {
    const loaded = await c.load(); await c.generate(loaded);
    const declaration = await readFile(join(c.root, ".sunder/generated/rpc/api.d.ts"), "utf8");
    assert.match(declaration, /\$createDependency[\s\S]*example_Dependency_First: Readonly<\{[\s\S]*messages: Readonly</);
    const imports = await preparePackageImports(parseContractLock(c.lockBytes), c.cache, "example.owner", [], new Set(["app", "runtime"]));
    const original = imports.contracts[0]!;
    const collision = {...original, descriptor: {...original.descriptor, services: {
      "message-items": original.descriptor.services.messages!, "message_items": original.descriptor.services.messages!,
    }}};
    assert.throws(() => serviceApiSource("example.owner", [collision], [], imports, "1.0.0"), /generated-name-collision/);
  } finally { await c.dispose(); }
});

for (const extension of ["ts", "js"] as const) test(`${extension} existing Worker and browser consumers compile unchanged when a selected package grows from one to two contracts`, async () => {
  const c = await serviceApiFixture(toolRoot, extension, ["example.dependency.first"]);
  const next = await serviceApiFixture(toolRoot, extension);
  try {
    const workerPath = join(c.root, `src/services.${extension}`); const browserPath = join(c.root, `app/consumer.${extension}`);
    const worker = c.source + `
import {Dependency, Services} from "#sunder/api";
/** @param {import("@sunder/sdk").WorkerContext} context */
export function configure(context${extension === "ts" ? ": WorkerContext" : ""}) {
  const api = context.api(Dependency);
  const token = Services.Dependency.example_Dependency_First.messages;
  if (token !== Services.Contracts.CExample_Dependency_First.messages) throw new Error("Different token");
  return {async onActivated() { const value = await api.example_Dependency_First.messages.send({}); value.message.toUpperCase(); }};
}`;
    const browser = `import {Self, Dependency, Services} from "#sunder/api";
import {createBrowserContext} from "@sunder/sdk/browser";
export function connect() {
  const owner = createBrowserContext(Self);
  const service = owner.api(Dependency).example_Dependency_First.messages;
  service.send({}).then(value => value.message.toUpperCase());
  const token = Services.Dependency.example_Dependency_First.messages;
  if (token !== Services.Contracts.CExample_Dependency_First.messages) throw new Error("Different token");
  // @ts-expect-error Single-contract selections have no flat compatibility alias.
  owner.api(Dependency).messages.send({});
  // @ts-expect-error Canonical tokens do not repeat their contract scope.
  Services.Contracts.CExample_Dependency_First.example_Dependency_First.messages;
  return owner;
}`;
    await writeFile(workerPath, worker); await writeFile(browserPath, browser);
    const before = await c.load(); await c.generate(before); await checkServiceApiBrowser(c, extension);
    // Replace only the test's reviewed selection/cache. Existing consumer source
    // and the first contract's descriptor, ABI and model projection stay exact.
    await cp(next.cache, c.cache, { recursive: true });
    await writeFile(join(c.root, "sunder-contracts.lock.json"), next.lockBytes);
    const after = await c.load(); await c.generate(after); await checkServiceApiBrowser(c, extension);
    assert.equal(after.contracts.filter(contract => contract.imported).length, 2);
    const first = (project: typeof before) => project.contracts.find(contract => contract.descriptor.contractId === "example.dependency.first")!;
    assert.deepEqual([first(after).canonicalBytes, first(after).abiSha256, first(after).modelProjection], [first(before).canonicalBytes, first(before).abiSha256, first(before).modelProjection]);
    assert.equal(await readFile(workerPath, "utf8"), worker); assert.equal(await readFile(browserPath, "utf8"), browser);
    assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), next.lockBytes);
  } finally { await next.dispose(); await c.dispose(); }
});

test("wire-compatible Self renaming changes the token generation without changing contract hashes", async () => {
  const c = await serviceApiFixture(toolRoot, "ts", ["example.dependency.first"]);
  try {
    const source = `import {defineService} from "@sunder/sdk";
type Message = {text:string};
export const messages=defineService({send(request:Message):Message{return request;}},{contractId:"example.owner.messages",serviceId:"messages"});`;
    const path = join(c.root, "src/services.ts"); await writeFile(path, source);
    const before = await c.load(); await writeFile(path, source.replace("const messages=", "const renamed=")); const after = await c.load();
    assert.deepEqual(before.contracts.map(contract => [contract.canonical, contract.descriptorSha256, contract.abiSha256]), after.contracts.map(contract => [contract.canonical, contract.descriptorSha256, contract.abiSha256]));
    const tokenGeneration = (api: string) => {
      const values:string[]=[];const visit=(node:ts.Node):void=>{
        if(ts.isCallExpression(node)&&ts.isIdentifier(node.expression)&&node.expression.text==="createApiToken"){
          const generation=node.arguments[2];assert.ok(generation&&ts.isStringLiteral(generation));assert.match(generation.text,/^[a-f0-9]{64}$/u);values.push(generation.text);
        }
        ts.forEachChild(node,visit);
      };visit(ts.createSourceFile("api.js",api,ts.ScriptTarget.Latest,true,ts.ScriptKind.JS));return values;
    };
    const oldGeneration = tokenGeneration(before.ownedRpc!.files.get("api.js")!); const newGeneration = tokenGeneration(after.ownedRpc!.files.get("api.js")!);
    assert.equal(oldGeneration.length, 2); assert.equal(newGeneration.length, 2);
    assert.equal(new Set(oldGeneration).size, 1); assert.equal(new Set(newGeneration).size, 1); assert.notEqual(oldGeneration[0], newGeneration[0]);
  } finally { await c.dispose(); }
});
