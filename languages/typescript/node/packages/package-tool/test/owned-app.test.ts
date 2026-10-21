import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { build as esbuild } from "esbuild";
import type { BrowserRpcProvider, SunderBrowserRpc } from "@sunder/sdk/browser";
import Ajv2020 from "ajv/dist/2020";
import { loadProject, currentRid } from "../src/model";
import { generateRpcBindings } from "../src/rpc-bindings";
import { assertServiceOptions } from "@sunder/sdk";
import { contractsCommand } from "../src/rpc-contracts";
import { liveCatalogWatch, writeSourceFile as writeFile } from "./service-fixtures";

const exec = promisify(execFile);
const cli = resolve(__dirname, "../../dist/cli.js");
async function receiptValidator() {
  const root = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring");
  const projection = JSON.parse(await readFile(resolve(root, "rpc-model-projection.schema.json"), "utf8"));
  const receipt = JSON.parse(await readFile(resolve(root, "authoring-receipt.schema.json"), "utf8"));
  return new Ajv2020({ strict: false }).addSchema(projection).compile(receipt);
}
const source = `import {defineService} from "@sunder/sdk";
/** @typedef {{message:string}} Message */
/*ts type Message = {message:string};*/
export const messages=defineService({
/** @param {Message} request @returns {Message} */
send(request/*: Message*/)/*: Message*/{return request;},
/** @param {Message} request @returns {AsyncIterable<Message>} */
async *watch(request/*: Message*/)/*: AsyncIterable<Message>*/{yield request;}
},{exposure:"package", constraints:{"Message.message":{minLength:1,maxLength:256}}});`;

async function project(extension: "ts" | "js", text = source): Promise<string> {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-owned-app-"));
  await mkdir(resolve(root, "src")); await mkdir(resolve(root, "app"));
  await writeFile(resolve(root, `src/rpc.${extension}`), text);
  await writeFile(resolve(root, "app/index.html"), '<!doctype html><html><head><title>Owned App</title></head><body>Owned App</body></html>');
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.app", name: "Owned App", version: "1.0.0",
    workerCapabilities: [], services: { module: `src/rpc.${extension}` },
    app: { root: "app", entryPoint: "index.html", views: [{ viewId: "example.app.main", displayName: "Owned App", route: "/", defaultPlacement: "middle", showInHotbar: true }] } }));
  await writeFile(resolve(root, "sunder-contracts.lock.json"), `${JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2)}\n`);
  if (text.includes('exposure:"public"')) await contractsCommand("accept", root);
  return root;
}

for (const extension of ["ts", "js"] as const) for (const exposure of ["package", "public"] as const)
test(`owned ${extension} ${exposure} App defaults and managed calls keep exact identity, ambiguity and role boundaries`, async () => {
  const root = await project(extension, source.replace('exposure:"package"', `exposure:"${exposure}"`));
  const owners: Array<{ disposeAsync(): Promise<void> }> = [];
  try {
    await generateRpcBindings(root);
    const loaded = await loadProject(root);
    const contract = loaded.contracts[0]!;
    const required = exposure === "package";
    const policy = { required, actions: ["discover", "invoke", "subscribe"] };
    assert.deepEqual(loaded.config.usesContracts, [{ role: "app", contractId: "example.app.messages", contractVersion: "1.0.0", ...policy }]);
    if (exposure === "public") assert.deepEqual(contract.appUse, policy);
    const runtime = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"), "utf8"));
    const app = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/app-authoring-receipt.v1.json"), "utf8"));
    assert.deepEqual(runtime.uses, []); assert.equal(runtime.providers.length, 1);
    assert.deepEqual(app.providers, []);
    assert.deepEqual(app.uses, [{ role: "app", ...policy, contractKey: "messages" }]);
    if (exposure === "public") for (const receipt of [runtime, app]) assert.deepEqual(receipt.contracts[0].appUse, policy);
    const validate = await receiptValidator();
    assert.equal(validate(app), true, JSON.stringify(validate.errors));
    assert.equal(validate(runtime), true, JSON.stringify(validate.errors));
    const bundle = resolve(root, "browser-test.cjs");
    await esbuild({ stdin: { contents: `export {Self} from ${JSON.stringify(resolve(root, ".sunder/generated/rpc/api.ts"))}; export {createApiContext,RpcError} from "@sunder/sdk/browser";`, resolveDir: root, loader: "ts" }, outfile: bundle, bundle: true, platform: "browser", format: "cjs",
      alias: { "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser") } });
    const browser = await readFile(bundle, "utf8");
    assert.doesNotMatch(browser, /node:fs|runWorker|owned-provider-bind/);
    const good: BrowserRpcProvider = { packageId: "example.app", packageVersion: "99.0.0", providerId: "example.app.messages.provider",
      contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version, descriptorSha256: contract.descriptorSha256,
      abiSha256: contract.abiSha256, providerHandle: "opaque-browser-handle", endpointReference: "exact-endpoint",
      activationId: "11223344556677889900aabbccddeeff", activationEpoch: 1, sessionGeneration: 1, catalogRevision: 1, state: "active", faultCode: null };
    let providers = [good]; let resetRequired = false;
    const calls: unknown[] = [];
    const rpc = {
      async discover(id: string) { assert.equal(id, contract.descriptor.contractId); return { revision: 1, sequence: 1, providers, resetRequired }; },
      async invoke(...args: unknown[]) { calls.push(args); return { message: "response" }; },
      async *subscribe(...args: unknown[]) { calls.push(args); yield { message: "event" }; },
      watch: liveCatalogWatch,
    } as SunderBrowserRpc;
    const { Self, createApiContext, RpcError } = require(bundle);
    const owner = createApiContext("example.app", rpc); owners.push(owner); const client = owner.api(Self);
    assert.deepEqual(await client.messages.send({ message: "request" }), { message: "response" });
    for await (const event of client.messages.watch({ message: "request" })) assert.deepEqual(event, { message: "event" });
    assert.equal((calls[0] as unknown[])[0], good.providerHandle);
    assert.equal((calls[1] as unknown[])[0], good.providerHandle);
    // Broker denial remains authoritative: owning a public provider/App facade
    // does not supply the Worker role with a consumer grant or bypass its error.
    const denial = new RpcError({kind:"permission-denied",code:"rpc.permission-denied",message:"Runtime use was not granted."});
    const workerOwner = createApiContext("example.app", { ...rpc, discover: async () => { throw denial; } });
    owners.push(workerOwner);
    await assert.rejects(workerOwner.api(Self).messages.send({ message: "request" }), error => error === denial);
    workerOwner.dispose(); assert.equal(calls.length, 2);
    for (const changed of [{ packageId: "other.app" }, { contractVersion: "9.9.9" }, { descriptorSha256: "f".repeat(64) }, { abiSha256: "f".repeat(64) }, { state: "inactive" as const }]) {
      providers = [{ ...good, ...changed }];
      await assert.rejects(client.messages.send({ message: "request" }), /No active provider/);
    }
    providers = [];
    await assert.rejects(client.messages.send({ message: "request" }), /No active provider/);
    providers = [good, { ...good, providerId: "example.app.other", providerHandle: "second-handle", endpointReference: "second-endpoint" }];
    await assert.rejects(client.messages.send({ message: "request" }), /ambiguous/);
    resetRequired = true; providers = [good];
    await assert.rejects(client.messages.send({ message: "request" }), error => {
      assert.ok(error instanceof RpcError);
      const response = (error as import("@sunder/sdk").RpcError).error;
      assert.equal(response.kind,"unavailable"); assert.equal(response.code,"rpc.catalog.disconnected");
      assert.equal(response.dispatchState,"not-dispatched"); assert.equal(response.retrySafety,"safe"); return true;
    });
    assert.equal(calls.length, 2); // Rejected discovery never invokes or retries a provider.
    owner.dispose();
  } finally { await Promise.all(owners.map(owner=>owner.disposeAsync())); await rm(root, { recursive: true, force: true }); }
});

test("owned App source projection packs role-isolated leaves and rejects stale prebuilt grants", { timeout: 180_000 }, async () => {
  const optional = source.replace('exposure:"package"', 'exposure:"public",appUse:{actions:["discover","invoke"]},runtimeUse:{required:true,actions:["discover"]}');
  const root = await project("ts", optional);
  const run = (...args: string[]) => exec(process.execPath, [cli, ...args, "--project", root]);
  try {
    await run("contracts", "accept"); await run("contracts", "freeze");
    const baseline = await readFile(resolve(root, "sunder-contracts.owned.json"));
    await run("build");
    const runtime = JSON.parse(await readFile(resolve(root, "dist/targets", currentRid(), "manifest/sunder-package.json"), "utf8"));
    const app = JSON.parse(await readFile(resolve(root, "dist/targets", `app-${currentRid()}`, "manifest/sunder-package.json"), "utf8"));
    assert.equal(runtime.provides.length, 1);
    assert.equal(runtime.usesContracts.length, 1);
    assert.equal(runtime.usesContracts[0].role, "runtime");
    assert.equal(runtime.usesContracts[0].required, true);
    assert.deepEqual(runtime.usesContracts[0].actions, ["discover"]);
    assert.deepEqual(app.provides, []); assert.equal(app.usesContracts.length, 1);
    assert.equal(app.usesContracts[0].required, false);
    assert.deepEqual(app.usesContracts[0].actions, ["discover", "invoke"]);
    const sources = ["--discovery-root", resolve(root, "dist/targets"), "--leaf", resolve(root, "dist/targets", currentRid()), "--leaf", resolve(root, "dist/targets", `app-${currentRid()}`)];
    await run("package", ...sources);
    const archivePath = resolve(root, "dist/example.app.1.0.0.sunderpkg");
    const original = await readFile(archivePath);
    const runtimeManifestPath = resolve(root, "dist/targets", currentRid(), "manifest/sunder-package.json");
    const runtimeManifest = await readFile(runtimeManifestPath);
    for (const mutation of ["missing", "required", "actions"]) {
      const changed = JSON.parse(runtimeManifest.toString("utf8"));
      if (mutation === "missing") changed.usesContracts = [];
      if (mutation === "required") changed.usesContracts[0].required = false;
      if (mutation === "actions") changed.usesContracts[0].actions.push("invoke");
      await writeFile(runtimeManifestPath, JSON.stringify(changed, null, 2) + "\n");
      await assert.rejects(run("package", ...sources), /match|source|leaf/i);
      assert.deepEqual(await readFile(archivePath), original);
    }
    await writeFile(runtimeManifestPath, runtimeManifest);
    await writeFile(resolve(root, "src/rpc.ts"), optional.replace('runtimeUse:{required:true', 'runtimeUse:{required:false'));
    await assert.rejects(run("package", ...sources), /match|source/i);
    assert.deepEqual(await readFile(archivePath), original);
    await writeFile(resolve(root, "src/rpc.ts"), optional);
    const appManifestPath = resolve(root, "dist/targets", `app-${currentRid()}`, "manifest/sunder-package.json");
    const appManifest = await readFile(appManifestPath);
    app.usesContracts = [];
    await writeFile(appManifestPath, JSON.stringify(app, null, 2) + "\n");
    await assert.rejects(run("package", ...sources), /match|App|app|source|leaf/i);
    assert.deepEqual(await readFile(archivePath), original);
    await writeFile(appManifestPath, appManifest);
    await writeFile(resolve(root, "src/rpc.ts"), optional.replace('appUse:{', 'appUse:{required:true,'));
    await assert.rejects(run("package", ...sources), /match|App|app|source/i);
    assert.deepEqual(await readFile(archivePath), original);
    await writeFile(resolve(root, "src/rpc.ts"), optional);
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")); delete config.app;
    await writeFile(configPath, JSON.stringify(config));
    await assert.rejects(run("package", ...sources), /match|App|app/);
    assert.deepEqual(await readFile(archivePath), original);
    assert.deepEqual(await readFile(resolve(root, "sunder-contracts.owned.json")), baseline);
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const extension of ["ts", "js"] as const) test(`${extension} public App defaults allow explicit readiness/narrowing overrides without changing wire identity`, async () => {
  const publicSource = source.replace('exposure:"package"', 'exposure:"public"');
  const root = await project(extension, publicSource);
  try {
    await generateRpcBindings(root);
    const original = await loadProject(root);
    assert.deepEqual(original.config.usesContracts, [{ role: "app", contractId: "example.app.messages", contractVersion: "1.0.0",
      required: false, actions: ["discover", "invoke", "subscribe"] }]);
    const validate = await receiptValidator();
    for (const [declaration, required, actions] of [
      ['{}', false, ["discover", "invoke", "subscribe"]],
      ['{required:true,actions:["invoke","discover"]}', true, ["discover", "invoke"]],
      ['{actions:["discover"]}', false, ["discover"]],
    ] as const) {
      await writeFile(resolve(root, `src/rpc.${extension}`), publicSource.replace('exposure:"public"', `exposure:"public",appUse:${declaration}`));
      await generateRpcBindings(root);
      const loaded = await loadProject(root);
      assert.equal(loaded.contracts[0]!.canonical, original.contracts[0]!.canonical);
      assert.equal(loaded.contracts[0]!.abiSha256, original.contracts[0]!.abiSha256);
      assert.deepEqual(loaded.config.usesContracts, [{ role: "app", contractId: "example.app.messages", contractVersion: "1.0.0", required, actions }]);
      for (const name of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json"]) {
        const receipt = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc", name), "utf8"));
        assert.equal(validate(receipt), true, JSON.stringify(validate.errors));
        assert.deepEqual(receipt.contracts[0].appUse, { required, actions });
        if (name.startsWith("app-")) {
          assert.equal(receipt.role, "app");
          assert.deepEqual(receipt.uses, [{ role: "app", contractKey: "messages", required, actions }]);
        } else assert.deepEqual(receipt.uses, []);
      }
    }
    // The bind-time check must reject removal of a narrowed policy's actions,
    // which would otherwise silently recover the broader default action set.
    const bundle = resolve(root, "policy-test.cjs");
    await esbuild({ entryPoints: [resolve(root, ".sunder/generated/rpc/adapter.js")], outfile: bundle,
      bundle: true, platform: "node", format: "cjs", alias: { "@sunder/sdk": require.resolve("@sunder/sdk"), "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser") } });
    let bound = 0;
    const router = (policy: unknown) => ({ options: { exposure: "public", appUse: policy } });
    const bind = (value: unknown) => { assertServiceOptions(value as never, {exposure:"public",appUse:{actions:["discover"]}}); bound++; };
    bind(router({ actions: ["discover"] }));
    for (const policy of [undefined, {}, { required: true, actions: ["discover"] }, { actions: ["discover", "invoke"] },
      { actions: ["discover", "discover"] }, { actions: ["discover"], extra: true }]) assert.throws(() => bind(router(policy)), /metadata differs/);
    assert.equal(bound, 1);
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const extension of ["ts", "js"] as const) test(`${extension} public declarations and multiple bindings add no implicit uses and retain declaration-owned policy`, async () => {
  const declaration = `import {defineService} from "@sunder/sdk";
import {Self} from "#sunder/api";
/** @typedef {{message:string}} Message */
/*ts type Message={message:string};*/
/** @typedef {{send:(request:Message)=>Message,watch:(request:Message)=>AsyncIterable<Message>}} Messaging */
/*ts interface Messaging {send(request:Message):Message;watch(request:Message):AsyncIterable<Message>}*/
/** @type {import("@sunder/sdk").ServiceDefinition<Messaging>} */
const messages=defineService/*<Messaging>*/(null,{declarationOnly:true,exposure:"public"});
const handlers={
  /** @param {Message} request @returns {Message} */
  send(request/*:Message*/)/*:Message*/{return request;},
  /** @param {Message} request @returns {AsyncIterable<Message>} */
  async *watch(request/*:Message*/)/*:AsyncIterable<Message>*/{yield request;}
};
/** @param {import("@sunder/sdk").WorkerContext} context */
export function lookup(context/*:import("@sunder/sdk").WorkerContext*/){return context.api(Self).messages;}
`;
  const bindings = `export const first=defineService(handlers,{implements:messages,providerId:"example.first"});
export const second=defineService(handlers,{implements:messages,providerId:"example.second"});
`;
  // Export providers first to prove contract policy cannot depend on source order.
  const complete = declaration + bindings + "export {messages};\n";
  const root = await project(extension, complete);
  try {
    const path = resolve(root, `src/rpc.${extension}`);
    const initial = await loadProject(root);
    for (const text of [declaration + "export {messages};\n", complete]) {
      await writeFile(path, text); const loaded = await loadProject(root);
      assert.equal(loaded.config.providers.length, text === complete ? 2 : 0);
      assert.deepEqual(loaded.config.usesContracts, []); assert.equal(loaded.contracts[0]!.appUse, undefined);
      for (const receipt of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json"])
        assert.deepEqual(JSON.parse(loaded.ownedRpc!.files.get(receipt)!).uses, []);
    }
    const explicit = complete.replace('exposure:"public"', 'exposure:"public",appUse:{required:true,actions:["invoke","discover"]},runtimeUse:{actions:["discover"]}');
    await writeFile(path, explicit); const loaded = await loadProject(root);
    assert.deepEqual(loaded.contracts[0]!.appUse, {required:true,actions:["discover","invoke"]});
    assert.deepEqual(loaded.contracts[0]!.runtimeUse, {required:false,actions:["discover"]});
    assert.deepEqual(loaded.config.usesContracts, [
      {role:"app",contractId:"example.app.messages",contractVersion:"1.0.0",required:true,actions:["discover","invoke"]},
      {role:"runtime",contractId:"example.app.messages",contractVersion:"1.0.0",required:false,actions:["discover"]},
    ]);
    assert.equal(loaded.contracts[0]!.canonical, initial.contracts[0]!.canonical);
    assert.equal(loaded.contracts[0]!.descriptorSha256, initial.contracts[0]!.descriptorSha256);
    assert.equal(loaded.contracts[0]!.abiSha256, initial.contracts[0]!.abiSha256);
    for (const field of ["appUse", "runtimeUse"]) {
      await writeFile(path, complete.replace('implements:messages,providerId:"example.first"', `implements:messages,${field}:{},providerId:"example.first"`));
      await assert.rejects(loadProject(root), /Implementations cannot redeclare.*consumer policy/);
    }
  } finally { await rm(root, {recursive:true,force:true}); }
});

for (const field of ["appUse", "runtimeUse"] as const) test(`${field} rejects invalid static actions, foreign fields and metadata mutation atomically`, async () => {
  const root = await project("ts");
  try {
    await generateRpcBindings(root);
    const receipt = resolve(root, ".sunder/generated/rpc/app-authoring-receipt.v1.json");
    const before = await readFile(receipt);
    for (const policy of ['null', '{required:"yes"}', '{extra:true}', '{actions:[]}', '{actions:null}',
      '{actions:["discover","discover"]}', '{actions:["write"]}', '{actions:[1]}']) {
      await writeFile(resolve(root, "src/rpc.ts"), source.replace('exposure:"package"', `exposure:"package",${field}:${policy}`));
      await assert.rejects(generateRpcBindings(root), /rpc-authoring\./);
      assert.deepEqual(await readFile(receipt), before);
    }
    const unaryOnly = `import {defineService} from "@sunder/sdk"; export const messages=defineService({send(){return "ok";}},{exposure:"package"});`;
    await writeFile(resolve(root, "src/rpc.ts"), unaryOnly.replace('exposure:"package"', `exposure:"package",${field}:{actions:["subscribe"]}`));
    await assert.rejects(generateRpcBindings(root), /actions must be a nonempty applicable/);
    await writeFile(resolve(root, "src/rpc.ts"), source.replace('export const messages=', 'const policy={required:false}; policy.required=true; export const messages=')
      .replace('exposure:"package"', `exposure:"package",${field}:policy`));
    await assert.rejects(generateRpcBindings(root), /metadata.*mutat|metadata.*escape/i);
    assert.deepEqual(await readFile(receipt), before);
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const extension of ["ts", "js"] as const) for (const exposure of ["package", "public"] as const)
  test(`${extension} ${exposure} Runtime policy is explicit and isolated from associated App grants`, async () => {
    const text = source.replace('exposure:"package"', `exposure:"${exposure}"`);
    const root = await project(extension, text);
    try {
      await generateRpcBindings(root);
      const original = await loadProject(root);
      assert.deepEqual(original.config.usesContracts!.filter(use => use.role === "runtime"), []);
      const originalReceipt = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"), "utf8"));
      assert.equal(originalReceipt.providers.length, 1);
      assert.deepEqual(originalReceipt.uses, []);
      const validate = await receiptValidator();
      for (const [policy, required, actions] of [
        ['{}', false, ["discover", "invoke", "subscribe"]],
        ['{required:true,actions:["invoke","discover"]}', true, ["discover", "invoke"]],
        ['{actions:["subscribe"]}', false, ["subscribe"]],
      ] as const) {
        await writeFile(resolve(root, `src/rpc.${extension}`), text.replace(`exposure:"${exposure}"`, `exposure:"${exposure}",runtimeUse:${policy}`));
        await generateRpcBindings(root);
        const loaded = await loadProject(root);
        assert.equal(loaded.contracts[0]!.canonical, original.contracts[0]!.canonical);
        assert.equal(loaded.contracts[0]!.abiSha256, original.contracts[0]!.abiSha256);
        assert.deepEqual(loaded.config.usesContracts!.filter(use => use.role === "runtime"), [{ role: "runtime",
          contractId: "example.app.messages", contractVersion: "1.0.0", required, actions }]);
        assert.deepEqual(loaded.config.usesContracts!.filter(use => use.role === "app"), original.config.usesContracts);
        const worker = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"), "utf8"));
        const app = JSON.parse(await readFile(resolve(root, ".sunder/generated/rpc/app-authoring-receipt.v1.json"), "utf8"));
        assert.equal(validate(worker), true, JSON.stringify(validate.errors));
        assert.equal(validate(app), true, JSON.stringify(validate.errors));
        assert.deepEqual(worker.contracts[0].runtimeUse, { required, actions });
        assert.deepEqual(worker.uses, [{ role: "runtime", required, actions, contractKey: "messages" }]);
        assert.equal(Object.hasOwn(app.contracts[0], "runtimeUse"), false);
        assert.ok(app.uses.every((use: { role: string }) => use.role === "app"));
      }
      const bundle = resolve(root, "runtime-policy-test.cjs");
      await esbuild({ entryPoints: [resolve(root, ".sunder/generated/rpc/adapter.js")], outfile: bundle, bundle: true,
        platform: "node", format: "cjs", alias: { "@sunder/sdk": require.resolve("@sunder/sdk"), "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser") } });
       let bound = 0;
       const router = (runtimeUse: unknown) => ({ options: { exposure, runtimeUse } });
       const bind = (value: unknown) => { assertServiceOptions(value as never, {exposure,runtimeUse:{actions:["subscribe"]}}); bound++; };
      bind(router({ actions: ["subscribe"] }));
      for (const policy of [undefined, {}, { required: true, actions: ["subscribe"] }, { actions: ["discover", "subscribe"] },
         { actions: ["subscribe", "subscribe"] }, { actions: ["subscribe"], extra: true }]) assert.throws(() => bind(router(policy)), /metadata differs/);
      assert.equal(bound, 1);
    } finally { await rm(root, { recursive: true, force: true }); }
  });
