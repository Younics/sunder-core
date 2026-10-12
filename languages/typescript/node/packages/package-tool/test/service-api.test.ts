import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";
import ts from "typescript";
import { serviceApiSource } from "../src/service-bindings";
import { preparePackageImports } from "../src/rpc-package-imports";
import { parseContractLock } from "../src/rpc-contract-lock";
import { exerciseServiceApiScopes, serviceApiFixture } from "./service-api-fixtures";

const toolRoot = resolve(__dirname, "../src");
for (const extension of ["ts", "js"] as const) test(`${extension} two-contract service scopes preserve Self, selected APIs, browser declarations and wire hashes`, async () => {
  await exerciseServiceApiScopes(toolRoot, extension);
});

test("contract scopes reject true normalized collisions before publishing generated output", async () => {
  const c = await serviceApiFixture(toolRoot, "ts", ["example.dependency.foo.1", "example.dependency.foo1"]);
  try {
    await assert.rejects(c.load(), /generated-name-collision.*example\.dependency\.foo\.1.*example\.dependency\.foo1.*exampleDependencyFoo1/);
    assert.deepEqual(await readFile(join(c.root, "sunder-contracts.lock.json")), c.lockBytes);
    await assert.rejects(readFile(join(c.root, ".sunder/generated/rpc/api.js")), {code:"ENOENT"});
  } finally { await c.dispose(); }
});

test("single-contract imports keep direct services and collisions stay within each descriptor scope", async () => {
  const c = await serviceApiFixture(toolRoot, "ts", ["example.dependency.first"]);
  try {
    const loaded = await c.load(); await c.generate(loaded);
    const declaration = await readFile(join(c.root, ".sunder/generated/rpc/api.d.ts"), "utf8");
    assert.match(declaration, /\$createDependency[\s\S]*messages: Readonly</); assert.doesNotMatch(declaration, /exampleDependencyFirst/);
    const imports = await preparePackageImports(parseContractLock(c.lockBytes), c.cache, "example.owner", [], new Set(["app", "runtime"]));
    const original = imports.contracts[0]!;
    const collision = {...original, descriptor: {...original.descriptor, services: {
      "message-items": original.descriptor.services.messages!, "message_items": original.descriptor.services.messages!,
    }}};
    assert.throws(() => serviceApiSource("example.owner", [collision], [], imports, "1.0.0"), /generated-name-collision/);
  } finally { await c.dispose(); }
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
