import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, writeFile, rm, stat, readdir, open, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020";
import { currentRid, loadProject } from "../src/model";
import { generateRpcBindings, generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { exerciseOwnedWorker } from "./owned-rpc-worker";
import { exercisePackedScaffolds } from "./packed-scaffolds";
import { exercisePackedImports } from "./packed-imports";
import { exercisePackedPlatformCatalog } from "./packed-platform-catalog";
import { withOutputLocks } from "../src/output";
import { rpcBindingsOutputRoot } from "../src/rpc-bindings";
import { build as esbuild } from "esbuild";
import { bundleProduction, prepareProject } from "../src/package";
import { workerResolutionOptions } from "../src/rpc-runtime-inputs";
import ts from "typescript";
import { constructorSource, sharedSource } from "./owned-rpc-review-fixtures";

const exec = promisify(execFile);
const corpusRoot = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/parity");
const cli = resolve(__dirname, "../../dist/cli.js");
const emptyLock = `${JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2)}\n`;
async function project(source: string, extension = "ts", id = "example.owned") {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-owned-"));
  await mkdir(resolve(root, "src"));
  await writeFile(resolve(root, "src/rpc." + extension), source);
  await writeFile(resolve(root, "src/worker." + extension), `import { runWorker } from "@sunder/sdk"; import { packageRpc } from "./rpc.js"; runWorker(() => ({ providers: packageRpc.providerRegistrations }));\n`);
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id, name: "Owned", version: "7.6.5", entry: "src/worker." + extension,
    workerCapabilities: [], rpc: { module: "src/rpc." + extension, export: "packageRpc" } }));
  await writeFile(resolve(root, "sunder-contracts.lock.json"), emptyLock);
  return root;
}
const source = `import { rpc, RpcDomainError } from "@sunder/sdk";
const s = rpc.schema;
export const packageRpc = rpc.router({
  contracts: { echo: rpc.contract({ exposure: "package", errors: { rejected: {} },
    definitions: { Payload: s.object({ count: s.required(s.bigInteger()), bytes: s.required(s.bytes()), scores: s.required(s.map(s.integer())),
      flag: s.required(s.boolean()), nullable: s.required(s.nullable(s.string())), note: s.optional(s.nullable(s.string())),
      chunks: s.optional(s.array(s.bytes())), choice: s.required(s.taggedUnion("kind", [s.ref("TextChoice"), s.ref("CountChoice")])) }),
      TextChoice: s.object({ kind: s.required(s.literal("text")), text: s.required(s.string()) }),
      CountChoice: s.object({ kind: s.required(s.literal("count")), number: s.required(s.bigInteger()) }) },
    services: { echo: { "round-trip": rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload") }),
      "async-trip": rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload") }),
      oversized: rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload") }),
      fail: rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload"), errors: ["rejected"] }),
      fault: rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload") }),
      wait: rpc.serverStream({ request: s.ref("Payload"), event: s.ref("Payload") }),
      watch: rpc.serverStream({ request: s.ref("Payload"), event: s.ref("Payload") }) } }
  }) },
  providers: { echo: rpc.provider({ contract: "echo", handlers: { echo: {
    "round-trip"(request, context) { context.signal.throwIfAborted(); if (typeof request.count !== "bigint" || !(request.bytes instanceof Uint8Array)) throw new Error("Missing value codec"); return request; },
    async "async-trip"(request, context) { context.signal.throwIfAborted(); return request; },
    oversized(request) { return { ...request, bytes: new Uint8Array(800000) }; },
    fail() { throw new RpcDomainError("rejected", "Expected domain failure"); },
    fault() { throw new Error("Private implementation failure"); },
    async *wait(request, context) { yield request; await new Promise((done) => context.signal.addEventListener("abort", done, { once: true })); context.signal.throwIfAborted(); },
    async *watch(request, context) { context.signal.throwIfAborted(); yield request; }
  } } }) }
});`;

const numericContract = `rpc.contract({ exposure: "package", errors: {}, definitions: {
  Payload: s.object({ choice: s.required(s.integerEnum([2, 1])), ratio: s.required(s.literal(0.5)), zero: s.required(s.literal(0)) })
}, services: { echo: { run: rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload") }) } } })`;
const numericHandlers = `{ echo: { run(request, context) { context.signal.throwIfAborted(); return { ...request }; } } }`;
const numericSource = `import { rpc } from "@sunder/sdk"; const s = rpc.schema;
const contract = ${numericContract};
export const packageRpc = rpc.router({ contracts: { echo: contract }, providers: { echo: rpc.provider({ contract: "echo", handlers: ${numericHandlers} }) } });`;

test("TS and JS preserve explicit canonical migration identities outside the default namespace", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(numericSource.replace('exposure: "package"', 'exposure: "package", contractId: "legacy.messages"'), extension);
    try {
      const contract = (await loadProject(root)).contracts[0]!;
      assert.equal(contract.descriptor.contractId, "legacy.messages");
      for (const invalid of ["Legacy.Messages", "legacy..messages", "legacy/messages"]) {
        await writeFile(resolve(root, `src/rpc.${extension}`), numericSource.replace('exposure: "package"', `exposure: "package", contractId: ${JSON.stringify(invalid)}`));
        await assert.rejects(loadProject(root), /rpc-authoring/u);
      }
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("TS and JS can preserve the fixed schema annotation without changing default descriptors or ABI", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(numericSource, extension);
    try {
      const ordinary = (await loadProject(root)).contracts[0]!;
      const sourcePath = resolve(root, `src/rpc.${extension}`);
      await writeFile(sourcePath, numericSource.replace('exposure: "package"', 'exposure: "package", includeSchemaDialect: false'));
      assert.deepEqual((await loadProject(root)).contracts[0]!.canonicalBytes, ordinary.canonicalBytes);
      await writeFile(sourcePath, numericSource.replace('exposure: "package"', 'exposure: "package", includeSchemaDialect: true'));
      const annotated = (await loadProject(root)).contracts[0]!;
      assert.deepEqual(JSON.parse(annotated.canonical), { ...JSON.parse(ordinary.canonical), $schema: "https://json-schema.org/draft/2020-12/schema" });
      assert.notEqual(annotated.descriptorSha256, ordinary.descriptorSha256);
      assert.equal(annotated.abiSha256, ordinary.abiSha256);
      await exportRouter(root, extension);
      await exec(process.execPath, [cli, "build", "--project", root]);
      assert.deepEqual(await invokeNumeric(root), { choice: 2, ratio: 0.5, zero: 0 });
      for (const invalid of ['"true"', '"https://json-schema.org/draft/2020-12/schema"', "0", "null"]) {
        await writeFile(sourcePath, numericSource.replace('exposure: "package"', `exposure: "package", includeSchemaDialect: ${invalid}`));
        await assert.rejects(loadProject(root), /includeSchemaDialect must be a literal boolean/);
      }
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

async function exportRouter(root: string, extension: string, extra = ""): Promise<void> {
  await writeFile(resolve(root, `src/worker.${extension}`), `import { packageRpc } from "./rpc.js";\n${extra}\nexport { packageRpc };\n`);
}

test("TS and JS raw JSON objects retain an unbounded object shape and validate generated calls", async () => {
  const contract = `rpc.contract({ exposure:"package", errors:{}, definitions:{Payload:s.json({objectOnly:true})}, services:{echo:{run:rpc.unary({request:s.ref("Payload"),response:s.ref("Payload")})}} })`;
  const input = numericSource.replace(numericContract, contract).replace(numericHandlers, `{echo:{run(request){return request;}}}`);
  for (const extension of ["ts", "js"]) {
    const root = await project(input, extension);
    try {
      const descriptor = JSON.parse((await loadProject(root)).contracts[0]!.canonical);
      assert.deepEqual(descriptor.$defs.Payload, { type: "object" });
      await exportRouter(root, extension);
      await exec(process.execPath, [cli, "build", "--project", root]);
      const value = { arbitrary: [1, null, { nested: false }], text: "漢字" };
      assert.deepEqual(await invokeNumeric(root, value), value);
      for (const invalid of [[], "text", 42, null]) await assert.rejects(invokeNumeric(root, invalid));
      for (const invalid of ['"true"', "1", "null"]) {
        await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("objectOnly:true", "objectOnly:" + invalid));
        await assert.rejects(loadProject(root), /objectOnly must be a literal boolean/);
      }
      await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("objectOnly:true", "objectOnly:false"));
      assert.deepEqual(JSON.parse((await loadProject(root)).contracts[0]!.canonical).$defs.Payload, {});
      await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("return request;", "return [];"));
      await assert.rejects(loadProject(root), /handler-incomplete/);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

async function invokeNumeric(root: string, request: unknown = { choice: 2, ratio: 0.5, zero: 0 }): Promise<unknown> {
  const module = resolve(root, "dist/targets", currentRid(), "payload/runtime", currentRid(), "bin/worker.cjs");
  const result = await exec(process.execPath, ["-e", `const {packageRpc}=require(process.argv[1]);
    Promise.resolve(packageRpc.providerRegistrations[0].handler.invokeUnary({signal:new AbortController().signal},"echo","run",JSON.parse(process.argv[2]))).then(value=>console.log(JSON.stringify(value)));`, module, JSON.stringify(request)]);
  return JSON.parse(result.stdout);
}

test("hoisted numeric and string enum constants preserve literal inference for TS and JS handlers", async () => {
  const inlineContract = numericContract.replace('zero: s.required(s.literal(0))', 'zero: s.required(s.literal(0)), status: s.required(s.stringEnum(["ready", "queued", "*/"]))');
  const hoistedContract = inlineContract.replace("s.integerEnum([2, 1])", "s.integerEnum(selection)").replace('s.stringEnum(["ready", "queued", "*/"])', "s.stringEnum(statuses)");
  const declarations = 'const two=2; const choices=[two,1]; const selection=choices; const statuses=["ready","queued","*/"];';
  const handlers = numericHandlers.replace("return { ...request };", 'if(unrelated()!==3) throw new Error("unrelated array changed"); return { ...request };');
  const unrelated = 'function unrelated(){const choices=[3,4]; choices.push(5); return choices.length;}';
  for (const extension of ["ts", "js"]) for (const shared of [false, true]) {
    const type = extension === "ts" ? ': import("@sunder/sdk").RpcHandlers<typeof contract>' : "";
    const jsdoc = extension === "js" ? '/** @type {import("@sunder/sdk").RpcHandlers<typeof contract>} */' : "";
    const prefix = '// @ts-check\nimport {rpc} from "@sunder/sdk"; const s=rpc.schema;';
    const router = shared
      ? `${jsdoc} const handlers${type}=${handlers}; export const packageRpc=rpc.router({contracts:{echo:contract},providers:{echo:rpc.provider({contract:"echo",handlers})}});`
      : `export const packageRpc=rpc.router({contracts:{echo:contract},providers:{echo:rpc.provider({contract:"echo",handlers:${handlers}})}});`;
    const input = `${prefix}\n${declarations}\nconst contract=${hoistedContract};\n${unrelated}\n${router}`;
    const root = await project(`${prefix}\nconst contract=${inlineContract};\n${unrelated}\n${router}`, extension);
    try {
      await exportRouter(root, extension);
      const baseline = (await loadProject(root)).contracts[0]!;
      await writeFile(resolve(root, `src/rpc.${extension}`), input);
      await exec(process.execPath, [cli, "build", "--project", root]);
      const loaded = await loadProject(root);
      assert.deepEqual(loaded.contracts[0]!.canonicalBytes, baseline.canonicalBytes);
      assert.equal(loaded.contracts[0]!.descriptorSha256, baseline.descriptorSha256);
      assert.equal(loaded.contracts[0]!.abiSha256, baseline.abiSha256);
      assert.equal(loaded.ownedRpc!.source, input);
      assert.equal(await readFile(resolve(root, `src/rpc.${extension}`), "utf8"), input);
      const request = { choice: 2, ratio: 0.5, zero: 0, status: "queued" };
      assert.deepEqual(await invokeNumeric(root, request), request);
      const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
      const prior = await readFile(receiptPath);
      for (const replacement of ['return { ...request, choice: 3 };', 'return { ...request, status: "wrong" };']) {
        await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("return { ...request };", replacement));
        await assert.rejects(generateRpcBindings(root), /handler-incomplete/);
        assert.deepEqual(await readFile(receiptPath), prior);
      }
      await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("const contract=", "selection.push(3); const contract="));
      await assert.rejects(generateRpcBindings(root), /metadata constant mutation or unsafe escape/);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("ordinary metadata property reads are still unsupported for hoisted literal values", async () => {
  const input = numericSource.replace("const contract =", "const options={ratio:0.5}; const contract =").replace("s.literal(0.5)", "s.literal(options.ratio)");
  const root = await project(input);
  try { await assert.rejects(loadProject(root), /Only direct members of SDK RPC constructors are metadata/); }
  finally { await rm(root, { recursive: true, force: true }); }
});

test("enum tuple checking retains explicit array constraints and existing const assertions", async () => {
  for (const extension of ["ts", "js"]) for (const asserted of [false, true]) {
    const values = asserted
      ? extension === "ts" ? "const choices=([2,1] as const); const selection=choices;" : "const choices=/** @type {const} */ ([2,1]); const selection=choices;"
      : extension === "ts" ? "const choices:number[]=[2,1]; const selection:readonly number[]=choices;" : "/** @type {number[]} */ const choices=[2,1]; /** @type {readonly number[]} */ const selection=choices;";
    const input = numericSource.replace("const contract =", `${values}\nconst contract =`).replace("s.integerEnum([2, 1])", "s.integerEnum(selection)");
    const root = await project(input, extension);
    try {
      await exportRouter(root, extension);
      await exec(process.execPath, [cli, "build", "--project", root]);
      assert.deepEqual(await invokeNumeric(root), { choice: 2, ratio: 0.5, zero: 0 });
      if (!asserted) {
        await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("number[]", "string[]"));
        await assert.rejects(generateRpcBindings(root), /handler-incomplete/);
      }
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("package-import aliases into generated modules work cold and after schema edits", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(`import { identity } from "./helper.js";\n${source.replace('context.signal.throwIfAborted(); if (typeof', 'context.signal.throwIfAborted(); if (identity() !== "example.owned.echo") throw new Error("wrong helper"); if (typeof')}`, extension);
    try {
      await writeFile(resolve(root, "package.json"), JSON.stringify({ type: "module", imports: {
        "#generated": { module: "./bad.js", node: `./.sunder/generated/rpc/imports.${extension}`, default: "./bad.js" },
      } }));
      await writeFile(resolve(root, `src/helper.${extension}`), '// @ts-check\nimport { Echo } from "#generated"; export function identity(){ return Echo.CONTRACT_ID; } export function currentHash(){ return Echo.CONTRACT_DESCRIPTOR_SHA256; }');
      await writeFile(resolve(root, `src/worker.${extension}`), `import {runWorker} from "@sunder/sdk"; import {packageRpc} from "./rpc.js"; import {currentHash} from "./helper.js";
if(currentHash() !== packageRpc.providerRegistrations[0].descriptorSha256) throw new Error("stale generated alias");
void runWorker(()=>({providers:packageRpc.providerRegistrations}));`);
      await exec(process.execPath, [cli, "build", "--project", root]);
      await exerciseOwnedWorker(root);
      const descriptor = resolve(root, ".sunder/generated/rpc/contracts/echo.rpc.json");
      const before = await readFile(descriptor);
      const sourcePath = resolve(root, `src/rpc.${extension}`);
      await writeFile(sourcePath, (await readFile(sourcePath, "utf8")).replace("s.bigInteger()", "s.bigInteger({ maxDigits: 40 })"));
      await exec(process.execPath, [cli, "build", "--project", root]);
      assert.notDeepEqual(await readFile(descriptor), before);
      await exerciseOwnedWorker(root);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("native package-export resolutions into warm generated output are reclassified", async () => {
  const root = await project(source);
  try {
    await generateRpcBindings(root);
    await writeFile(resolve(root, "package.json"), JSON.stringify({ name: "owned-export-alias", type: "module", exports: { "./generated": "./.sunder/generated/rpc/imports.ts" } }));
    await writeFile(resolve(root, "src/helper.ts"), 'import {Echo} from "owned-export-alias/generated"; export function currentHash(){return Echo.CONTRACT_DESCRIPTOR_SHA256;}');
    await writeFile(resolve(root, "src/worker.ts"), `import {runWorker} from "@sunder/sdk"; import {packageRpc} from "./rpc.js"; import {currentHash} from "./helper.js";
if(currentHash() !== packageRpc.providerRegistrations[0].descriptorSha256) throw new Error("stale native alias");
void runWorker(()=>({providers:packageRpc.providerRegistrations}));`);
    await writeFile(resolve(root, "src/rpc.ts"), source.replace("s.bigInteger()", "s.bigInteger({ maxDigits: 40 })"));
    await exec(process.execPath, [cli, "build", "--project", root]);
    await exerciseOwnedWorker(root);
  } finally { await rm(root, { recursive: true, force: true }); }
});

const rawMetadataSource = `import { rpc } from "@sunder/sdk";
const s=rpc.schema;
const facets={maxLength:5};
const services={echo:{run:rpc.unary({request:s.ref("Payload"),response:s.ref("Payload")})}};
/* before consumption */
const contract=rpc.contract({exposure:"package",errors:{},definitions:{Payload:s.object({text:s.required(s.string(facets))})},services});
export const packageRpc=rpc.router({contracts:{echo:contract},providers:{echo:rpc.provider({contract:"echo",handlers:{echo:{run(request){return request;}}}})}});`;

test("raw metadata mutation before constructor consumption is rejected", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(rawMetadataSource, extension);
    try {
      await generateRpcBindings(root);
      const receipt = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
      const before = await readFile(receipt);
      for (const mutation of [
        'Object.assign(services,{echo:{}});',
        'Object.assign(facets,{maxLength:10});',
        'facets.maxLength=10;',
        'facets["maxLength"]=10;',
        'const alias=facets; alias.maxLength=10;',
        'const alias=services.echo; Reflect.deleteProperty(alias,"run");',
        'const holder={facets}; Object.assign(holder.facets,{maxLength:10});',
        'const escape=(value)=>Object.assign(value,{maxLength:10}); escape(facets);',
      ]) {
        await writeFile(resolve(root, `src/rpc.${extension}`), rawMetadataSource.replace("/* before consumption */", mutation));
        await assert.rejects(generateRpcBindings(root), /metadata.*mutat|metadata.*escape/i);
        assert.deepEqual(await readFile(receipt), before);
      }
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("readonly metadata reuse remains valid and handler implementation state is not metadata", async () => {
  for (const extension of ["ts", "js"]) {
    const text = rawMetadataSource.replace("/* before consumption */", "const shared=facets; const first=s.string(shared); const second=s.string(facets); const state={calls:0};")
      .replace("text:s.required(s.string(facets))", "text:s.required(first),note:s.optional(second)")
      .replace("run(request){return request;}", 'run(request){Object.assign(state,{calls:state.calls+1}); if(facets.maxLength!==5) throw new Error("wrong facets"); return request;}');
    const root = await project(text, extension);
    try {
      await exportRouter(root, extension);
      await exec(process.execPath, [cli, "build", "--project", root]);
      const module = resolve(root, "dist/targets", currentRid(), "payload/runtime", currentRid(), "bin/worker.cjs");
      const result = await exec(process.execPath, ["-e", `const {packageRpc}=require(process.argv[1]); Promise.resolve(packageRpc.providerRegistrations[0].handler.invokeUnary({},"echo","run",{text:"hello",note:"ok"})).then(value=>console.log(JSON.stringify(value)));`, module]);
      assert.deepEqual(JSON.parse(result.stdout), { text: "hello", note: "ok" });
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("runtime binding checks actual contract service and method keys, not only handlers", async () => {
  for (const mutation of ['Object.assign(packageRpc.contracts.echo.services,{other:{}});', 'Object.assign(packageRpc.contracts.echo.services,{echo:{}});']) {
    const root = await project(`${numericSource}\n${mutation}`);
    try {
      await exportRouter(root, "ts");
      await exec(process.execPath, [cli, "build", "--project", root]);
      await assert.rejects(invokeNumeric(root), /RPC runtime provider projection mismatch/);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("compiler input limits reject sparse authored helpers before unbounded reads", async () => {
  for (const extension of ["ts", "d.ts"]) {
    const root = await project(`import { value } from "./huge-helper.js"; void value;\n${numericSource}`);
    const path = resolve(root, `src/huge-helper.${extension}`);
    const original = ts.sys.readFile;
    let unboundedReads = 0;
    try {
      const file = await open(path, "w");
      await file.truncate(100 * 1024 * 1024);
      await file.close();
      ts.sys.readFile = (fileName, encoding) => {
        if (resolve(fileName) === path) { unboundedReads++; throw new Error("unbounded compiler read reached"); }
        return original(fileName, encoding);
      };
      await assert.rejects(loadProject(root), /source-limit-exceeded/);
      assert.equal(unboundedReads, 0);
    } finally { ts.sys.readFile = original; await rm(root, { recursive: true, force: true }); }
  }
});

test("compiler source budget has an exact normalized 4 MiB aggregate boundary", async () => {
  const text = `import {a} from "./a.js"; import {b} from "./b.js"; void [a,b];\n${numericSource}`;
  const root = await project(text);
  try {
    const entry = await readFile(resolve(root, "src/worker.ts"), "utf8");
    const first = 'export const a=1;\n/*' + "x".repeat(1024 * 1024) + '*/\n';
    const prefix = 'export const b=2;\n/*';
    const suffix = '*/\n';
    const remaining = 4_194_304 - Buffer.byteLength(text + entry + first + prefix + suffix);
    const second = prefix + "y".repeat(remaining) + suffix;
    await writeFile(resolve(root, "src/a.ts"), first.replaceAll("\n", "\r\n"));
    await writeFile(resolve(root, "src/b.ts"), second.replaceAll("\n", "\r\n"));
    await generateRpcBindings(root);
    const receipt = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
    const previous = await readFile(receipt);
    await writeFile(resolve(root, "src/b.ts"), second + " ");
    await assert.rejects(generateRpcBindings(root), /source-limit-exceeded/);
    assert.deepEqual(await readFile(receipt), previous);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("compiler dependency declarations are bounded before reads and parsing", async () => {
  const root = await project(`import {value} from "large-declaration"; void value;\n${numericSource}`);
  const original = ts.sys.readFile;
  let unsafeReads = 0;
  try {
    const directory = resolve(root, "node_modules/large-declaration");
    await mkdir(directory, { recursive: true });
    await writeFile(resolve(directory, "package.json"), '{"name":"large-declaration","version":"1.0.0","types":"index.d.ts"}');
    const path = resolve(directory, "index.d.ts");
    const file = await open(path, "w"); await file.truncate(100 * 1024 * 1024); await file.close();
    ts.sys.readFile = (fileName, encoding) => {
      if (resolve(fileName) === path) { unsafeReads++; throw new Error("unbounded declaration read reached"); }
      return original(fileName, encoding);
    };
    await assert.rejects(loadProject(root), /source-limit-exceeded/);
    assert.equal(unsafeReads, 0);
  } finally { ts.sys.readFile = original; await rm(root, { recursive: true, force: true }); }
});

test("transitive authored compiler inputs reject file and ancestor symlinks", async () => {
  const root = await project(`import {value} from "./linked.js"; void value;\n${numericSource}`);
  const outside = await mkdtemp(resolve(tmpdir(), "sunder-compiler-link-"));
  try {
    await writeFile(resolve(outside, "helper.ts"), "export const value=1;");
    await symlink(resolve(outside, "helper.ts"), resolve(root, "src/linked.ts"), "file");
    await assert.rejects(loadProject(root), /symbolic links|non-link file/);
    await rm(resolve(root, "src/linked.ts"));
    await symlink(outside, resolve(root, "src/linked"), process.platform === "win32" ? "junction" : "dir");
    await writeFile(resolve(root, "src/rpc.ts"), `import {value} from "./linked/helper.js"; void value;\n${numericSource}`);
    await assert.rejects(loadProject(root), /symbolic links|non-link file/);
  } finally { await rm(root, { recursive: true, force: true }); await rm(outside, { recursive: true, force: true }); }
});

test("complete current generated overlay supports clean helper imports and schema edits in TS and JS", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(`import { identity } from "./helper.js";\n${source.replace('context.signal.throwIfAborted(); if (typeof', 'context.signal.throwIfAborted(); if (identity() !== "example.owned.echo") throw new Error("wrong helper"); if (typeof')}`, extension);
    try {
      await writeFile(resolve(root, `src/helper.${extension}`), `// @ts-check
import { Echo } from "../.sunder/generated/rpc/imports.js";
import * as Browser from "../.sunder/generated/rpc/browser.js";
/** @type {typeof import("../.sunder/generated/rpc/index").Echo.CONTRACT_ID} */
const expected = Browser.Echo.CONTRACT_ID;
export function identity() { if (expected !== Echo.CONTRACT_ID) throw new Error("identity mismatch"); return expected; }`);
      await exec(process.execPath, [cli, "build", "--project", root]);
      await exerciseOwnedWorker(root);
      const before = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"), "utf8"));
      const path = resolve(root, `src/rpc.${extension}`);
      await writeFile(path, (await readFile(path, "utf8")).replace("s.bigInteger()", "s.bigInteger({ maxDigits: 40 })"));
      await exec(process.execPath, [cli, "build", "--project", root]);
      const after = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"), "utf8"));
      assert.notEqual(after.contracts[0].descriptorSha256, before.contracts[0].descriptorSha256);
      await exerciseOwnedWorker(root);
      await writeFile(resolve(root, ".sunder/generated/rpc/stale.ts"), "export const stale = true;");
      await writeFile(resolve(root, `src/helper.${extension}`), 'import { stale } from "../.sunder/generated/rpc/stale.js"; export function identity(){ return stale; }');
      await assert.rejects(generateRpcBindings(root), /current generated RPC|Cannot find module/);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("numeric enums and literals remain narrow for real identity handlers and explicitly typed shared handlers", async () => {
  for (const extension of ["ts", "js"]) for (const shared of [false, true]) {
    const annotation = extension === "ts" ? ': import("@sunder/sdk").RpcHandlers<typeof contract>' : "";
    const jsdoc = extension === "js" ? '/** @type {import("@sunder/sdk").RpcHandlers<typeof contract>} */' : "";
    const input = shared ? `// @ts-check\nimport { rpc } from "@sunder/sdk"; const s=rpc.schema; const contract=${numericContract};
${jsdoc} const handlers${annotation}=${numericHandlers};
export const packageRpc=rpc.router({contracts:{echo:contract},providers:{echo:rpc.provider({contract:"echo",handlers})}});` : numericSource;
    const root = await project(input, extension);
    try {
      await exportRouter(root, extension);
      await exec(process.execPath, [cli, "build", "--project", root]);
      assert.deepEqual(await invokeNumeric(root), { choice: 2, ratio: 0.5, zero: 0 });
      const prior = await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"));
      await writeFile(resolve(root, `src/rpc.${extension}`), input.replace("return { ...request };", "return { ...request, ratio: 0.25 };"));
      await assert.rejects(generateRpcBindings(root), /handler-incomplete/);
      assert.deepEqual(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json")), prior);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("Node import conditions select the implementation that is actually checked and exclude module", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(`import { value } from "#impl";\n${numericSource.replace("return { ...request };", "return { ...request, choice: value() };")}`, extension);
    try {
      await exportRouter(root, extension);
      await writeFile(resolve(root, "package.json"), JSON.stringify({ type: "module", imports: { "#impl": {
        module: "./src/module.js", node: "./src/node.js", default: "./src/fallback.js",
      } } }));
      await writeFile(resolve(root, "src/module.js"), 'export function value(){return "wrong module condition";}');
      await writeFile(resolve(root, "src/node.js"), 'export function value(){return "wrong Node result";}');
      await writeFile(resolve(root, "src/fallback.js"), '/** @returns {1 | 2} */ export function value(){return 2;}');
      await assert.rejects(exec(process.execPath, [cli, "build", "--project", root]), /handler-incomplete/);
      await writeFile(resolve(root, "src/node.js"), '/** @returns {1 | 2} */ export function value(){return 2;}');
      await exec(process.execPath, [cli, "build", "--project", root]);
      assert.deepEqual(await invokeNumeric(root), { choice: 2, ratio: 0.5, zero: 0 });
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("runtime implementations shadowed by declarations are fenced and all bundle loads replay captured bytes", async () => {
  const root = await project(`import { value } from "./helper.js";\n${numericSource.replace("return { ...request };", "return { ...request, choice: value() };")}`);
  try {
    await exportRouter(root, "ts");
    await writeFile(resolve(root, "src/helper.d.ts"), "export declare function value(): 1 | 2;");
    await writeFile(resolve(root, "src/helper.js"), '/** @returns {1 | 2} */ export function value(){return 1;}');
    await generateRpcBindings(root);
    const loaded = await prepareProject(root);
    const prior = await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"));
    await writeFile(resolve(root, "src/helper.js"), '/** @returns {1 | 2} */ export function value(){return 2;}');
    await assert.rejects(generateRpcBindingsForLoadedProject(loaded), /runtime implementation input changed|compiler input changed/);
    await assert.rejects(bundleProduction(loaded, currentRid(), resolve(root, "worker.cjs")), /input changed/);
    assert.deepEqual(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json")), prior);
    // Isolate replay from its validation fence: even after a physical edit, the
    // bundler itself must consume captured implementation bytes, not new disk JS.
    const captured = await esbuild({ absWorkingDir: root, entryPoints: [resolve(root, "src/worker.ts")], bundle: true, platform: "node", format: "cjs", target: "node24",
      ...workerResolutionOptions, tsconfigRaw: {}, write: false, plugins: [loaded.ownedRpc!.runtime.plugin(async () => {})] });
    await writeFile(resolve(root, "captured.cjs"), captured.outputFiles[0]!.contents);
    const result = await exec(process.execPath, ["-e", `const {packageRpc}=require(process.argv[1]);Promise.resolve(packageRpc.providerRegistrations[0].handler.invokeUnary({signal:new AbortController().signal},"echo","run",{choice:2,ratio:0.5,zero:0})).then(value=>console.log(value.choice));`, resolve(root, "captured.cjs")]);
    assert.equal(result.stdout.trim(), "1");
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("runtime-only negative resolution candidates and package configuration remain fenced", async () => {
  const root = await project(`import { value } from "shadow";\n${numericSource.replace("return { ...request };", "return { ...request, choice: value() };")}`);
  try {
    await exportRouter(root, "ts");
    const dependency = resolve(root, "node_modules/shadow");
    await mkdir(dependency, { recursive: true });
    const config = JSON.stringify({ name: "shadow", version: "1.0.0", types: "index.d.ts", main: "index.js" });
    await writeFile(resolve(dependency, "package.json"), config);
    await writeFile(resolve(dependency, "index.d.ts"), "export declare function value(): 1 | 2;");
    await writeFile(resolve(dependency, "index.js"), 'const data = require("./choice"); exports.value = () => data.choice;');
    await writeFile(resolve(dependency, "choice.json"), '{"choice":1}');
    await generateRpcBindings(root);
    const loaded = await loadProject(root);
    const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
    const prior = await readFile(receiptPath);
    await writeFile(resolve(dependency, "choice.js"), "exports.choice = 2;");
    await assert.rejects(generateRpcBindingsForLoadedProject(loaded), /runtime resolution input set changed/);
    await rm(resolve(dependency, "choice.js"));
    await writeFile(resolve(dependency, "package.json"), config.replace("1.0.0", "1.0.1"));
    await assert.rejects(generateRpcBindingsForLoadedProject(loaded), /input changed|configuration changed/);
    assert.deepEqual(await readFile(receiptPath), prior);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("raw provider additions, removals and metadata edits now fail before publication", async () => {
  const edits = [
    'Object.assign(providers, { extra: providers.echo });',
    'Reflect.deleteProperty(providers, "echo");',
    'Object.assign(providers, { echo: { ...providers.echo, id: "example.other" } });',
    'Object.assign(providers, { echo: { ...providers.echo, contract: "other" } });',
    'Object.assign(providers, { echo: { ...providers.echo, audience: "public" } });',
  ];
  for (const extension of ["ts", "js"]) for (const edit of edits) {
    const root = await project(`import {rpc} from "@sunder/sdk"; const s=rpc.schema; const contract=${numericContract};
const providers={echo:rpc.provider({contract:"echo",handlers:${numericHandlers}})};
${edit}
export const packageRpc=rpc.router({contracts:{echo:contract},providers});`, extension);
    try {
      await assert.rejects(exec(process.execPath, [cli, "build", "--project", root]), /metadata constant mutation or unsafe escape/);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("frozen provider attributes reject late mutation and generated adapters retain stable handler references", async () => {
  const root = await project(`${numericSource}\nObject.assign(packageRpc.providers.echo, { id: "example.other" });`);
  try {
    await exportRouter(root, "ts");
    await exec(process.execPath, [cli, "build", "--project", root]);
    await assert.rejects(invokeNumeric(root), /read only|not extensible/);
    await writeFile(resolve(root, "src/rpc.ts"), numericSource);
    await exportRouter(root, "ts", 'Object.assign(packageRpc.providers.echo.handlers.echo, { run() { throw new Error("replaced handler"); } });');
    await exec(process.execPath, [cli, "build", "--project", root]);
    assert.deepEqual(await invokeNumeric(root), { choice: 2, ratio: 0.5, zero: 0 });
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("owned TS and independent JS frontends reproduce all seven frozen contract tuples", async () => {
  const corpus = JSON.parse(await readFile(resolve(corpusRoot, "expectations.json"), "utf8"));
  const validateReceipt = new Ajv2020({ strict: false, allErrors: true }).compile(JSON.parse(await readFile(resolve(corpusRoot, "../authoring-receipt.schema.json"), "utf8")));
  for (const fixture of corpus.validFixtures) for (const [language, extension] of [["typescript", "ts"], ["javascript", "js"]]) {
    const root = await project(await readFile(resolve(corpusRoot, fixture.sources[language!]), "utf8"), extension, fixture.packageId);
    try {
      const loaded = await loadProject(root);
      assert.equal(loaded.contracts.length, fixture.contracts.length);
      for (const [index, actual] of loaded.contracts.entries()) {
        const expected = fixture.contracts[index];
        assert.equal(actual.canonicalBytes.toString("base64"), expected.canonicalDescriptorBase64, `${fixture.id}/${language}`);
        assert.equal(actual.descriptorSha256, expected.descriptorSha256);
        assert.equal(actual.abiSha256, expected.abiSha256);
      }
      const receipt = JSON.parse(loaded.ownedRpc!.files.get("authoring-receipt.v1.json")!);
      assert.equal(validateReceipt(receipt), true, JSON.stringify(validateReceipt.errors));
      assert.deepEqual(receipt.providers, fixture.providers);
      assert.deepEqual(receipt.uses, []);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("actual CLI generates and builds clean owned TS and JS; stable output and negative handlers", async () => {
  for (const extension of ["ts", "js"]) {
    const root = await project(source, extension);
    try {
      await exec(process.execPath, [cli, "generate", "--project", root]);
      const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
      const before = await readFile(receiptPath);
      const modified = (await stat(receiptPath)).mtimeMs;
      await exec(process.execPath, [cli, "generate", "--project", root]);
      assert.equal((await stat(receiptPath)).mtimeMs, modified);
      const browser = await esbuild({ entryPoints: [resolve(root, `.sunder/generated/rpc/browser.${extension}`)], bundle: true, platform: "browser", write: false,
        alias: { "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser") } });
      assert.doesNotMatch(browser.outputFiles[0]!.text, /node:fs|runWorker|owned-provider-bind/);
      await exec(process.execPath, [cli, "build", "--project", root]);
      await exerciseOwnedWorker(root);
      await exec(process.execPath, [cli, "dev", "--project", root]);
      await exerciseOwnedWorker(root, true);
      await assert.rejects(exec(process.execPath, [cli, "package", "--project", root]), /release packing is denied/);
      await writeFile(resolve(root, "src/rpc." + extension), source.replace("return request;", "return { count: 'wrong', bytes: request.bytes, scores: request.scores };"));
      await assert.rejects(generateRpcBindings(root), /handler-incomplete/);
      assert.deepEqual(await readFile(receiptPath), before);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("source analysis is non-executing and follows resolved SDK aliases, not spelling", async () => {
  const aliased = source.replace('import { rpc, RpcDomainError }', 'import { rpc as api, RpcDomainError }').replaceAll("rpc.", "api.");
  const root = await project(`throw new Error("MUST NOT EXECUTE DURING GENERATION");\n${aliased}`);
  try {
    await generateRpcBindings(root);
    const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
    const prior = await readFile(receiptPath);
    const invalid = [
      source.replace('exposure: "package"', 'exposure: (() => { throw new Error("DO NOT RUN"); })()'),
      source.replace('exposure: "package"', '["exposure"]: "package"'),
      source.replace('exposure: "package"', 'get exposure() { return "package"; }'),
      source.replace('exposure: "package"', '...{ exposure: "package" }'),
      source.replace('exposure: "package"', 'exposure: true ? "package" : "public"'),
      source.replace('exposure: "package"', 'exposure: process.env.EXPOSURE'),
      source.replace('exposure: "package"', 'exposure: import("./side-effect.js")'),
      source.replace('const s = rpc.schema;', 'const s = s;'),
      source.replace('const s = rpc.schema;', 'const s = { bytes: () => ({}) };'),
      source.replace('const s = rpc.schema;', 'const s = rpc.schema; const f = s.string;').replace('s.bigInteger()', 'f({ maxDigits: 2 })'),
      source.replace('"@sunder/sdk"', '"./fake.js"'),
      source.replace('s.taggedUnion("kind"', 's.taggedUnion("wrong"'),
      source.replace('Payload: s.object', 'JsonValue: s.object'),
      source.replace('schema.string()', 'schema.notAConstructor()'),
    ];
    // Remove the unchanged replacement; all other vectors are genuinely invalid.
    for (const candidate of invalid.filter((item) => item !== source)) {
      await writeFile(resolve(root, "src/rpc.ts"), candidate);
      await assert.rejects(generateRpcBindings(root), /rpc-authoring\./);
      assert.deepEqual(await readFile(receiptPath), prior);
    }
    for (const collision of ["JsonValue", "IEchoProvider"]) {
      await writeFile(resolve(root, "src/rpc.ts"), source.replaceAll("Payload", collision));
      await assert.rejects(generateRpcBindings(root), /generated-name-collision/);
      assert.deepEqual(await readFile(receiptPath), prior);
    }
    await writeFile(resolve(root, "src/side-effect.js"), 'throw new Error("MUST NOT EXECUTE IMPORT"); export const value = 1;');
    await writeFile(resolve(root, "src/rpc.ts"), `import { value } from "./side-effect.js"; void value;\n${source}`);
    await generateRpcBindings(root);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("new resolution candidates invalidate the complete source input set", async () => {
  const root = await project(source);
  try {
    const workerPath = resolve(root, "src/worker.ts");
    await writeFile(workerPath, `import { value } from "./dependency"; void value;\n${await readFile(workerPath, "utf8")}`);
    await writeFile(resolve(root, "src/dependency.js"), "export const value = true;\n");
    await generateRpcBindings(root);
    const loaded = await loadProject(root);
    const path = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
    const previous = await readFile(path);
    await writeFile(resolve(root, "src/dependency.ts"), "export const value = true;\n");
    await assert.rejects(generateRpcBindingsForLoadedProject(loaded), /input set changed/);
    assert.deepEqual(await readFile(path), previous);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("non-RPC V3 retains ordinary configuration without requiring an RPC lock", async () => {
  const root = await project(source);
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8"));
    delete config.rpc;
    await writeFile(configPath, JSON.stringify(config));
    await rm(resolve(root, "sunder-contracts.lock.json"));
    assert.equal((await loadProject(root)).config.version, "7.6.5");
    await generateRpcBindings(root);
    await assert.rejects(stat(rpcBindingsOutputRoot(root)), { code: "ENOENT" });
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("owned diagnostic fixtures stay separate from C#-only naming controls and deferred evolution", async () => {
  const invalid = ["service-invalid", "method-invalid", "value-invalid", "provider-invalid", "handler-incomplete", "collision-ascii-case", "collision-namespace-duplicate", "collision-post-escape-reserved", "unsupported-source", "source-limit-contract-identity", "source-limit-provider-identity"];
  for (const extension of ["ts", "js"]) for (const id of [...invalid, "collision-suffix-service", "collision-suffix-async", "collision-synthetic-request"]) {
    const root = await project(await readFile(resolve(corpusRoot, "invalid", `${id}.${extension}`), "utf8"), extension, id.startsWith("source-limit") ? "example.boundary" : "example.collision");
    try {
      if (invalid.includes(id)) await assert.rejects(loadProject(root), /rpc-authoring\./, id);
      else await loadProject(root);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
});

test("changed source updates descriptor, manifest and live registrations rather than reusing snapshots", async () => {
  const root = await project(source);
  try {
    await exec(process.execPath, [cli, "build", "--project", root]);
    const first = await loadProject(root);
    await writeFile(resolve(root, "src/rpc.ts"), source.replace("s.bigInteger()", "s.bigInteger({ maxDigits: 40 })"));
    await exec(process.execPath, [cli, "build", "--project", root]);
    const second = await loadProject(root);
    assert.notEqual(first.contracts[0]!.descriptorSha256, second.contracts[0]!.descriptorSha256);
    assert.notEqual(first.ownedRpc!.inputFingerprint, second.ownedRpc!.inputFingerprint);
    await exerciseOwnedWorker(root);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("source, version and input set are checked after waiting for the existing output lease", async () => {
  const root = await project(source);
  try {
    await generateRpcBindings(root);
    const loaded = await loadProject(root);
    const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
    const prior = await readFile(receiptPath);
    let contender: Promise<void> | undefined;
    await withOutputLocks([rpcBindingsOutputRoot(root)], async () => {
      contender = generateRpcBindingsForLoadedProject(loaded);
      const configPath = resolve(root, "sunder.package.json");
      await writeFile(configPath, (await readFile(configPath, "utf8")).replace("7.6.5", "7.6.6"));
    });
    await assert.rejects(contender!, /changed/);
    assert.deepEqual(await readFile(receiptPath), prior);
    await writeFile(resolve(root, "sunder.package.json"), (await readFile(resolve(root, "sunder.package.json"), "utf8")).replace("7.6.6", "7.6.5"));
    const plan = await loadProject(root);
    const revalidate = plan.ownedRpc!.revalidate;
    let calls = 0;
    const modified = { ...plan, ownedRpc: { ...plan.ownedRpc!, revalidate: async () => {
      if (++calls === 2) await writeFile(resolve(root, "src/rpc.ts"), source + "\n// changed before publication\n");
      await revalidate();
    } } };
    // Force a new staged set, not merely the identical-output early return.
    await writeFile(resolve(root, ".sunder/generated/rpc/index.d.ts"), "// stale\n");
    await assert.rejects(generateRpcBindingsForLoadedProject(modified), /changed/);
    assert.deepEqual(await readFile(receiptPath), prior);
    assert.equal((await readdir(resolve(root, ".sunder/generated"))).some((name) => name.includes(".stage-") || name.includes(".backup-")), false);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("development lock/baseline/config gates never claim freeze or fall back to V2", async () => {
  const root = await project(source);
  try {
    for (const lock of ["{}\n", emptyLock.replace('"packageImports": {}', '"packageImports": { "foreign.package": {} }'), emptyLock.replace('"platformImports": {}', '"platformImports": null')]) {
      await writeFile(resolve(root, "sunder-contracts.lock.json"), lock);
      await assert.rejects(generateRpcBindings(root), /rpc-authoring\./);
    }
    await writeFile(resolve(root, "sunder-contracts.lock.json"), emptyLock);
    await writeFile(resolve(root, "sunder-contracts.owned.json"), '{"schemaVersion":1}\n');
    await assert.rejects(generateRpcBindings(root), /baseline-drift/);
    await rm(resolve(root, "sunder-contracts.owned.json"));
    await generateRpcBindings(root);
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8"));
    for (const name of ["contracts", "providers", "usesContracts", "rpcBindings", "platformImports"]) {
      await writeFile(configPath, JSON.stringify({ ...config, [name]: [] }));
      await assert.rejects(loadProject(root), /unknown property/);
    }
    delete config.rpc;
    await writeFile(configPath, JSON.stringify({ ...config, schemaVersion: 2, contracts: [], providers: [] }));
    await assert.rejects(loadProject(root), /fallback/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("bounded metadata graph rejects byte, node, reference, contract and provider overflow", async () => {
  const root = await project(source);
  try {
    const variants = [
      `${source}\n/*${"x".repeat(4_194_304)}*/`,
      source.replace('const s = rpc.schema;', Array.from({ length: 66 }, (_, index) => `const a${index} = ${index === 0 ? "rpc.schema" : `a${index - 1}`};`).join("\n") + "\nconst s = a65;"),
      source.replace('errors: { rejected: {} }', `errors: { rejected: {} }, definitions: { ${Array.from({ length: 24_000 }, (_, index) => `P${index}: s.string()`).join(",")} }`),
      `import {rpc} from "@sunder/sdk"; const c = rpc.contract({exposure:"package", errors:{}, definitions:{Unit:rpc.schema.object({})}, services:{echo:{run:rpc.unary({request:rpc.schema.ref("Unit"),response:rpc.schema.ref("Unit")})}}); export const packageRpc=rpc.router({contracts:{${Array.from({length:129}, (_, i) => `c${i}:c`).join(",")}},providers:{}});`,
      `import {rpc} from "@sunder/sdk"; export const packageRpc=rpc.router({contracts:{},providers:{${Array.from({length:129}, (_, i) => `p${i}:rpc.provider({contract:"missing",handlers:{}})`).join(",")}});`,
    ];
    for (const candidate of variants) {
      await writeFile(resolve(root, "src/rpc.ts"), candidate);
      await assert.rejects(generateRpcBindings(root), /source-limit-exceeded/);
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("packed SDK and package-tool production-only dependency closure accepts, freezes, releases and runs owned TS/JS", { skip: process.env.SUNDER_TEST_PACKED_CLOSURE !== "1", timeout: 1_200_000 }, async () => {
  const packedSource = source.replace("const s = rpc.schema;", 'const s = rpc.schema; const choices=[2,1]; const statuses=["ready","queued"];')
    .replace("flag: s.required(s.boolean())", "flag: s.required(s.boolean()), priority: s.optional(s.integerEnum(choices)), status: s.optional(s.stringEnum(statuses))");
  const tarballs = await mkdtemp(resolve(tmpdir(), "sunder-owned-tarballs-"));
  const nodeRoot = resolve(__dirname, "../../../..");
  try {
    await exec("npm", ["run", "build", "--workspace", "create-sunder-package"], { cwd: nodeRoot });
    for (const name of ["@sunder/sdk", "@sunder/package-tool", "create-sunder-package"]) await exec("npm", ["pack", "--workspace", name, "--pack-destination", tarballs, "--ignore-scripts"], { cwd: nodeRoot });
    const archives = (await readdir(tarballs)).filter((name) => name.endsWith(".tgz")).map((name) => resolve(tarballs, name));
    assert.equal(archives.length, 3);
    for (const extension of ["ts", "js"]) {
      const root = await project(packedSource, extension);
      try {
        await writeFile(resolve(root, "package.json"), JSON.stringify({ name: "owned-closure-test", version: "1.0.0", private: true }));
        await exec("npm", ["install", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund", ...archives], { cwd: root });
        const tool = resolve(root, "node_modules/@sunder/package-tool/dist/cli.js");
        if (extension === "ts") await exercisePackedPlatformCatalog(tool, nodeRoot);
        if (extension === "ts") {
          const contractCache = resolve(root, ".sunder/packed-contract-cache");
          await exec(process.execPath, ["-e", `const fs=require('node:fs'); const path=require('node:path');
            const {parseContractLock}=require(path.join(process.argv[1],'rpc-contract-lock.js'));
            const {publishContractCache}=require(path.join(process.argv[1],'rpc-contract-cache.js'));
            const sdk=require(path.join(process.argv[1],'../../sdk/dist/index.js'));
            (async()=>{const locked=parseContractLock(fs.readFileSync(process.argv[3]));const requirement=[...locked.requirements.values()][0];
              const bytes=Buffer.from(sdk.canonicalRpcDescriptorBytes(sdk.parseRpcContractDescriptor(fs.readFileSync(process.argv[4]))));
              await publishContractCache(process.argv[2],new Map([[requirement.descriptorSha256,bytes]]),locked.requirements);
              const old=new Date(Date.now()-10*86400000); for(const suffix of ['.json','.receipt.json']) fs.utimesSync(path.join(process.argv[2],requirement.descriptorSha256+suffix),old,old);
            })().catch(error=>{console.error(error);process.exitCode=1;});`, resolve(root, "node_modules/@sunder/package-tool/dist"), contractCache,
            resolve(nodeRoot, "../../../specs/rpc/v1/authoring/conformance/valid/package-api-contract-lock.json"),
            resolve(nodeRoot, "../../../specs/rpc/v1/conformance/valid/minimal.rpc.json")]);
          const collected = await exec(process.execPath, [tool, "contracts", "gc", "--cache-root", contractCache,
            "--lock", resolve(root, "sunder-contracts.lock.json")]);
          assert.match(collected.stdout, /Collected 1 contract entries/);
          assert.match(collected.stdout, /Pruned entries require explicit restore/);
        }
        const review = await project(constructorSource(true), extension, "example.review");
        try {
          for (const operation of ["accept", "freeze"]) await exec(process.execPath, [tool, "contracts", operation, "--project", review]);
          const frozen = await readFile(resolve(review, "sunder-contracts.owned.json"));
          await writeFile(resolve(review, `src/rpc.${extension}`), constructorSource(false));
          await assert.rejects(exec(process.execPath, [tool, "contracts", "accept", "--project", review]), /acknowledgement/);
          assert.deepEqual(await readFile(resolve(review, "sunder-contracts.owned.json")), frozen);
          for (const reverse of [false, true]) {
            await writeFile(resolve(review, `src/rpc.${extension}`), sharedSource(extension, reverse, false));
            await assert.rejects(exec(process.execPath, [tool, "contracts", "accept", "--project", review]), /handler-incomplete/);
            assert.deepEqual(await readFile(resolve(review, "sunder-contracts.owned.json")), frozen);
          }
        } finally { await rm(review, { recursive: true, force: true }); }
        const legacy = await project(packedSource, extension);
        try {
          await exec(process.execPath, [tool, "generate", "--project", legacy]);
          const receiptPath = resolve(legacy, ".sunder/generated/rpc/authoring-receipt.v1.json"); const receipt = await readFile(receiptPath);
          await assert.rejects(exec(process.execPath, [tool, "contracts", "accept", "echo", "--next-version", "2.0.0", "--project", legacy]), /Existing owned baseline is required/);
          assert.deepEqual(await readFile(receiptPath), receipt); await assert.rejects(stat(resolve(legacy, "sunder-contracts.owned.json")), { code: "ENOENT" });
        } finally { await rm(legacy, { recursive: true, force: true }); }
        const sdkPackage = JSON.parse(await readFile(resolve(root, "node_modules/@sunder/sdk/package.json"), "utf8"));
        assert.deepEqual(sdkPackage.dependencies ?? {}, {});
        assert.equal(JSON.parse(await readFile(resolve(root, "node_modules/typescript/package.json"), "utf8")).version, "6.0.3");
        assert.equal(JSON.parse(await readFile(resolve(root, "node_modules/@types/node/package.json"), "utf8")).version, "24.13.3");
        await exercisePackedImports(tool, extension, nodeRoot);
        if (extension === "ts") await exercisePackedScaffolds(archives, resolve(root, "node_modules/create-sunder-package/dist/cli.js"));
        const packageJson = JSON.parse(await readFile(resolve(root, "package.json"), "utf8"));
        await writeFile(resolve(root, "package.json"), JSON.stringify({ ...packageJson, type: "module", imports: { "#generated": `./.sunder/generated/rpc/imports.${extension}` } }));
        await writeFile(resolve(root, `src/helper.${extension}`), 'import {Echo} from "#generated"; export function currentHash(){return Echo.CONTRACT_DESCRIPTOR_SHA256;}');
        await writeFile(resolve(root, `src/worker.${extension}`), `import {runWorker} from "@sunder/sdk"; import {packageRpc} from "./rpc.js"; import {currentHash} from "./helper.js";
if(currentHash()!==packageRpc.providerRegistrations[0].descriptorSha256) throw new Error("stale packed alias");
void runWorker(()=>({providers:packageRpc.providerRegistrations}));`);
        await exec(process.execPath, [tool, "contracts", "accept", "echo", "--next-version", "2.0.0", "--project", root]);
        await exec(process.execPath, [tool, "contracts", "check", "--project", root]);
        await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
        const baselinePath = resolve(root, "sunder-contracts.owned.json"); const initialBaseline = await readFile(baselinePath);
        const initial = JSON.parse(initialBaseline.toString()).contracts.echo;
        assert.equal(initial.contractId, "example.owned.echo"); assert.equal(initial.current.state, "draft"); assert.equal(initial.current.contractVersion, "2.0.0");
        assert.equal(JSON.parse(initial.current.canonicalDescriptor).version, "2.0.0"); assert.deepEqual(initial.history, []); assert.equal(Object.hasOwn(initial.current, "packageRelease"), false);
        const invalidOperation = await exec(process.execPath, ["-e", `const assert=require("node:assert/strict"); const {contractsCommand}=require(process.argv[1]);
          (async()=>{for(const operation of ["publish","freeze ","constructor",null,undefined,{},42]) await assert.rejects(contractsCommand(operation,process.argv[2]),/Unknown contracts operation/);
          console.log("closed-runtime-operation");})().catch(error=>{console.error(error);process.exitCode=1;});`, resolve(root, "node_modules/@sunder/package-tool/dist/rpc-contracts.js"), root]);
        assert.equal(invalidOperation.stdout.trim(), "closed-runtime-operation");
        assert.deepEqual(await readFile(baselinePath), initialBaseline);
        await exec(process.execPath, [tool, "generate", "--project", root]);
        await exec(process.execPath, [tool, "build", "--project", root]);
        assert.match(await readFile(resolve(root, `.sunder/generated/rpc/echo.${extension}`), "utf8"), /CONTRACT_VERSION\s*=\s*"2\.0\.0"/);
        await exerciseOwnedWorker(root, false, "2.0.0");
        assert.deepEqual(await readFile(baselinePath), initialBaseline);
        const manifest = JSON.parse(await readFile(resolve(root, "dist/targets", currentRid(), "manifest/sunder-package.json"), "utf8"));
        assert.equal(manifest.version, "7.6.5"); assert.equal(manifest.provides[0].contractVersion, "2.0.0"); assert.equal(manifest.provides[0].descriptorSha256, initial.current.descriptorSha256);
        await exec(process.execPath, [tool, "contracts", "freeze", "--project", root]);
        const frozenInitial = JSON.parse((await readFile(baselinePath)).toString()).contracts.echo.current;
        assert.equal(frozenInitial.contractVersion, "2.0.0"); assert.equal(frozenInitial.packageRelease.packageVersion, "7.6.5");
        await exec(process.execPath, [tool, "package", "--project", root]);
        assert.ok((await stat(resolve(root, "dist/example.owned.7.6.5.sunderpkg"))).size > 0);
        await writeFile(resolve(root, `src/rpc.${extension}`), packedSource.replace("s.bigInteger()", "s.bigInteger({maxDigits:40})"));
        await exec(process.execPath, [tool, "contracts", "accept", "echo", "--current-version", "2.0.0", "--next-version", "3.0.0", "--acknowledge-breaking-change", "--project", root]);
        await exec(process.execPath, [tool, "contracts", "check", "--project", root]);
        await exec(process.execPath, [tool, "contracts", "freeze", "--project", root]);
        await exec(process.execPath, [tool, "build", "--project", root]);
        await exerciseOwnedWorker(root, false, "3.0.0");
        await exec(process.execPath, [tool, "package", "--discovery-root", resolve(root, "dist/targets"), "--leaf", resolve(root, "dist/targets", currentRid()), "--project", root]);
        if (extension === "ts") for (const implementation of ["@sunder/sdk/dist/generator.js", "@sunder/sdk/dist/authoring.js", "typescript/lib/typescript.js", "@sunder/package-tool/dist/compatibility.js", "@sunder/package-tool/dist/rpc-compiler-inputs.js",
          ...["rpc-baseline", "rpc-baseline-json", "rpc-baseline-store", "rpc-evolution", "rpc-contracts", "rpc-contract-cache-gc", "rpc-owned-app", "rpc-package-imports", "rpc-package-selection", "rpc-package-commands", "cli", "output"].map((name) => `@sunder/package-tool/dist/${name}.js`)]) {
          const result = await exec(process.execPath, ["-e", `const tool=require(process.argv[1]); const fs=require("node:fs");
            (async()=>{ const file=process.argv[3]; const before=fs.readFileSync(file); await Promise.resolve();
              try { fs.writeFileSync(file, Buffer.concat([before,Buffer.from("\\n// changed after producer load\\n")]));
                try { await tool.loadProject(process.argv[2]); throw new Error("missed producer mutation"); }
                catch(error) { if(!/producer implementation changed.*restart/.test(error.message)) throw error; console.log("producer-guard"); }
              } finally { fs.writeFileSync(file,before); }
            })().catch(error=>{ console.error(error);process.exitCode=1; });`, resolve(root, "node_modules/@sunder/package-tool/dist/index.js"), root, resolve(root, "node_modules", implementation)]);
          assert.equal(result.stdout.trim(), "producer-guard");
        }
        if (extension === "ts") {
          const result = await exec(process.execPath, ["-e", `const tool=require(process.argv[1]); const fs=require("node:fs"); const path=require("node:path");
            const output=require(path.join(process.argv[1],"../output.js")); const original=output.withOutputLeases;
            const file=path.join(process.argv[1],"../rpc-evolution.js"); const before=fs.readFileSync(file); let changed=false;
            output.withOutputLeases=async(paths,operation,wait)=>{ if(paths.some(p=>p.endsWith("sunder-contracts.owned.json"))){changed=true;fs.writeFileSync(file,Buffer.concat([before,Buffer.from("\\n// producer race\\n")]));} return original(paths,operation,wait); };
            (async()=>{try { await tool.aggregateAndPack(process.argv[2],[{path:process.argv[3],discoveryRoot:path.dirname(process.argv[3])}]); throw new Error("missed producer race"); }
              catch(error){if(!changed||!/producer implementation changed.*restart/.test(error.message)) throw error; console.log("postarchive-producer-guard");}
              finally{fs.writeFileSync(file,before);} })().catch(error=>{console.error(error);process.exitCode=1;});`, resolve(root, "node_modules/@sunder/package-tool/dist/index.js"), root, resolve(root, "dist/targets", currentRid())]);
          assert.equal(result.stdout.trim(), "postarchive-producer-guard");
        }
      } finally { await rm(root, { recursive: true, force: true }); }
    }
  } finally { await rm(tarballs, { recursive: true, force: true }); }
});

test("stale loaded source and input membership cannot replace previous output", async () => {
  const root = await project(source);
  try {
    await generateRpcBindings(root);
    const loaded = await loadProject(root);
    const prior = await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"));
    await writeFile(resolve(root, "src/rpc.ts"), source.replace("s.bigInteger()", "s.string()"));
    await assert.rejects(generateRpcBindingsForLoadedProject(loaded), /changed/);
    assert.deepEqual(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json")), prior);
  } finally { await rm(root, { recursive: true, force: true }); }
});
