import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtemp, mkdir, readFile, readdir, rm, stat, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { currentRid } from "../src/model";
import { emptyLock, fixtureSource } from "./service-fixtures";

const cli = resolve(__dirname, "../../dist/cli.js");
const nodeModules = resolve(__dirname, "../../../../node_modules");
const delay = async (ms: number): Promise<void> => new Promise(done => setTimeout(done, ms));
const text = async (path: string): Promise<string> => readFile(path, "utf8").catch(() => "");

function watch(root: string, progress = false) {
  const child = spawn(process.execPath, [cli, "dev", "--watch", "--project", root], {
    stdio: "pipe", env: { ...process.env, SUNDER_BUILDER_WATCH_PROGRESS: progress ? "1" : "0" },
  });
  let output = "";
  let standardOutput = "";
  child.stdout.on("data", data => { output += data; standardOutput += data; });
  child.stderr.on("data", data => { output += data; });
  const exited = new Promise<number | null>(done => child.once("exit", done));
  const count = () => [...output.matchAll(/Sunder dev package:/gu)].length;
  const webCount = () => [...output.matchAll(/WATCH_WEB_BUILD/gu)].length;
  const states = () => standardOutput.split(/\r?\n/u).slice(0, -1).filter(line => line.startsWith("sunder.package.watch:"))
    .map(line => JSON.parse(line.slice("sunder.package.watch:".length)) as { schemaVersion: number; state: string });
  const wait = async (condition: () => Promise<boolean> | boolean, label: string): Promise<void> => {
    const end = Date.now() + 90_000;
    while (!await condition()) {
      if (child.exitCode !== null || child.signalCode !== null || Date.now() > end) throw new Error(`${label}\n${output}`);
      await delay(20);
    }
  };
  return {
    child, count, webCount, wait, states, output: () => output,
    async edit(change: () => Promise<void>): Promise<void> {
      const next = count() + 1;
      await change();
      await wait(() => count() >= next, "watch publication");
    },
    async failed(change: () => Promise<void>): Promise<void> {
      const start = output.length;
      const before = count();
      await change();
      await wait(() => /(?:error|Error|ERROR|failed|rpc-authoring\.)/u.test(output.slice(start)), "watch build failure");
      assert.equal(count(), before, output);
      assert.equal(child.exitCode, null, output);
    },
    async close(): Promise<void> {
      child.kill("SIGINT");
      const timeout = setTimeout(() => child.kill("SIGKILL"), 90_000);
      try { assert.equal(await exited, 0, output); }
      finally { clearTimeout(timeout); }
    },
    async kill(): Promise<void> {
      if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
      await exited;
    },
  };
}

async function snapshot(root: string): Promise<Record<string, string>> {
  const files: Record<string, string> = {};
  for (const name of (await readdir(root, { recursive: true })).sort()) {
    const path = resolve(root, name);
    if ((await stat(path)).isFile()) files[name.replaceAll("\\", "/")] = createHash("sha256").update(await readFile(path)).digest("hex");
  }
  return files;
}

async function assertComplete(root: string): Promise<void> {
  const files = await snapshot(root);
  const index = JSON.parse(await readFile(resolve(root, "manifest/content-index.json"), "utf8")) as {
    files: { path: string; sha256: string; size: number }[];
  };
  assert.deepEqual(Object.keys(files).filter(path => path !== "manifest/content-index.json").sort(), index.files.map(file => file.path).sort());
  for (const file of index.files) {
    assert.equal(files[file.path], file.sha256, file.path);
    assert.equal((await stat(resolve(root, file.path))).size, file.size, file.path);
  }
}

// The private work module is the real esbuild output. Canonical trees are copied
// atomically, so their mtimes cannot establish that the Worker bundler was skipped.
async function workerBuild(root: string) {
  const stages = (await readdir(resolve(root, "dist"))).filter(name => name.startsWith(".sunder-dev-work.stage-"));
  assert.equal(stages.length, 1);
  const path = resolve(root, "dist", stages[0]!, "worker.cjs");
  const info = await stat(path, { bigint: true });
  return { path, mtime: info.mtimeNs, inode: info.ino, bytes: await readFile(path) };
}

async function webText(root: string): Promise<string> {
  const files = await readdir(root, { recursive: true });
  return (await Promise.all(files.filter(name => /\.(js|html)$/u.test(name)).map(name => text(resolve(root, name))))).join("\n");
}

async function fixture(extension: "ts" | "js"): Promise<string> {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-watch-"));
  await mkdir(resolve(root, "src"));
  await mkdir(resolve(root, "app/src"), { recursive: true });
  await symlink(nodeModules, resolve(root, "node_modules"), process.platform === "win32" ? "junction" : "dir");
  await writeFile(resolve(root, "package.json"), JSON.stringify({ name: "watch-fixture", type: "module" }));
  await writeFile(resolve(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({
    schemaVersion: 3, id: "test.watch", name: "Watch", version: "1.0.0", workerCapabilities: [], services: { module: `src/services.${extension}` },
    app: { root: "app", views: [{ viewId: "test.watch.main", displayName: "Watch", route: "/", defaultPlacement: "middle", showInHotbar: true }] },
  }));
  await writeFile(resolve(root, "app/shared-types.d.ts"), "export type Value = string;\n");
  await writeFile(resolve(root, `src/services.${extension}`), fixtureSource(`import {defineService} from "@sunder/sdk";
import {worker} from "./worker.js";import {shared} from "../shared.js";
export const first=defineService({
/** @param {import("../app/shared-types.js").Value} value */
invoke(value/*:import("../app/shared-types.js").Value*/)/*:string*/{return worker+shared+value;}},{contractId:"example.rpc.first",appUse:{}});`, extension));
  await writeFile(resolve(root, `src/worker.${extension}`), 'export const worker="WORKER_0";');
  await writeFile(resolve(root, `shared.${extension}`), 'export const shared="SHARED_0";');
  await writeFile(resolve(root, `app/src/View.${extension}x`), 'export function View(){return <span>UI_0</span>;}');
  await writeFile(resolve(root, `app/src/main.${extension}x`), `import React from "react";import {createRoot} from "react-dom/client";import {View} from "./View";
import {shared} from "../../shared";import {CONTRACT_ID} from "../../.sunder/generated/rpc/owned/first.js";
createRoot(document.getElementById("root")).render(<div><View/>{shared}{CONTRACT_ID}{__CONFIG_LABEL__}</div>);`);
  await writeFile(resolve(root, "app/index.html"), `<!doctype html><div id="root"></div><script type="module" src="./src/main.${extension}x"></script>`);
  await writeFile(resolve(root, "config-label.mjs"), 'export default "CONFIG_0";');
  await writeFile(resolve(root, "app/vite.config.mjs"), `import react from "@vitejs/plugin-react";import label from "../config-label.mjs";
import {access} from "node:fs/promises";
export default {define:{__CONFIG_LABEL__:JSON.stringify(label)},plugins:[react(),{name:"watch-test-observer",async buildStart(){
process.stdout.write("WATCH_WEB_BUILD\\n");
if(await access(${JSON.stringify(resolve(root, ".sunder/pause-web"))}).then(()=>true,()=>false)){
process.stdout.write("WATCH_WEB_PAUSED\\n");
while(await access(${JSON.stringify(resolve(root, ".sunder/pause-web"))}).then(()=>true,()=>false))await new Promise(done=>setTimeout(done,20));
}}}]};`);
  return root;
}

for (const extension of ["ts", "js"] as const) test(`watch routes React/${extension} roles, retains complete last-good output and recovers`, async () => {
  const root = await fixture(extension);
  const watching = watch(root);
  const dev = resolve(root, "dist/sunder-dev");
  const worker = resolve(dev, "payload/runtime/shared/bin/worker.cjs");
  const web = resolve(dev, "payload/app", currentRid());
  const putWorker = async (value: string) => writeFile(resolve(root, `src/worker.${extension}`), `export const worker=${JSON.stringify(value)};`);
  const putShared = async (value: string) => writeFile(resolve(root, `shared.${extension}`), `export const shared=${JSON.stringify(value)};`);
  const putUi = async (value: string) => writeFile(resolve(root, `app/src/View.${extension}x`), `export function View(){return <span>${value}</span>;}`);
  try {
    await watching.wait(() => watching.count() === 1, "initial React watch output");
    assert.deepEqual(watching.states(), [], "normal CLI output must not gain Builder diagnostics");
    await assertComplete(dev);
    let workerBefore = await workerBuild(root);
    const generatedBefore = await snapshot(resolve(root, ".sunder/generated/rpc"));
    await watching.edit(() => putUi("UI_ONLY"));
    assert.deepEqual(await workerBuild(root), workerBefore, "UI-only edit must not rerun the Worker bundler");
    assert.deepEqual(await snapshot(resolve(root, ".sunder/generated/rpc")), generatedBefore);
    assert.match(await webText(web), /UI_ONLY/u);

    let webBefore = await snapshot(web);
    let webBuilds = watching.webCount();
    await watching.edit(() => putWorker("WORKER_ONLY"));
    assert.equal(watching.webCount(), webBuilds, "Worker-only input must not run Vite");
    assert.deepEqual(await snapshot(web), webBefore);
    assert.match(await text(worker), /WORKER_ONLY/u);
    // The service module itself also has implementation-only edits.
    const services = resolve(root, `src/services.${extension}`);
    await watching.edit(async () => writeFile(services, (await text(services)).replace("+value;", '+value+"BODY_ONLY";')));
    assert.equal(watching.webCount(), webBuilds);
    assert.match(await text(worker), /BODY_ONLY/u);
    await watching.edit(() => putShared("SHARED_BOTH"));
    assert.ok(watching.webCount() > webBuilds);
    assert.match(await text(worker), /SHARED_BOTH/u);
    assert.match(await webText(web), /SHARED_BOTH/u);

    let before = await snapshot(dev);
    // This type-only App-root input is absent from both bundler graphs. Reusing
    // the RPC snapshot must still reject invalid authoring and refresh valid ABI.
    await watching.failed(() => writeFile(resolve(root, "app/shared-types.d.ts"), "export type Value = () => void;\n"));
    assert.deepEqual(await snapshot(dev), before);
    await watching.edit(() => writeFile(resolve(root, "app/shared-types.d.ts"), "export type Value = string | null;\n"));
    assert.match(await text(resolve(dev, "payload/shared/contracts/first.rpc.json")), /"null"/u);
    before = await snapshot(dev);
    await watching.failed(() => writeFile(resolve(root, `app/src/View.${extension}x`), "export function View( {"));
    assert.deepEqual(await snapshot(dev), before);
    await watching.failed(() => putWorker("WORKER_DURING_UI_ERROR"));
    assert.deepEqual(await snapshot(dev), before, "an unrelated edit cannot publish a partial generation");
    await watching.edit(() => putUi("UI_RECOVERED"));
    assert.match(await text(worker), /WORKER_DURING_UI_ERROR/u);
    assert.match(await webText(web), /UI_RECOVERED/u);

    before = await snapshot(dev);
    await watching.failed(() => writeFile(resolve(root, `src/worker.${extension}`), "export const worker = ;"));
    await watching.failed(() => putUi("UI_DURING_WORKER_ERROR"));
    assert.deepEqual(await snapshot(dev), before);
    await watching.edit(() => putWorker("WORKER_RECOVERED"));
    assert.match(await text(worker), /WORKER_RECOVERED/u);
    assert.match(await webText(web), /UI_DURING_WORKER_ERROR/u);

    await watching.edit(async () => {
      await writeFile(resolve(root, `app/src/added.${extension}`), 'export const added="ADDED_UI";');
      await writeFile(resolve(root, `app/src/View.${extension}x`), 'import {added} from "./added";export function View(){return <span>{added}</span>;}');
    });
    assert.match(await webText(web), /ADDED_UI/u);
    before = await snapshot(dev);
    await watching.failed(() => rm(resolve(root, `app/src/added.${extension}`)));
    assert.deepEqual(await snapshot(dev), before);
    await watching.edit(() => writeFile(resolve(root, `app/src/added.${extension}`), 'export const added="RESTORED_UI";'));
    assert.match(await webText(web), /RESTORED_UI/u);
    before = await snapshot(dev);
    await watching.failed(() => rm(resolve(root, `src/worker.${extension}`)));
    assert.deepEqual(await snapshot(dev), before);
    await watching.edit(() => putWorker("RESTORED_WORKER"));
    assert.match(await text(worker), /RESTORED_WORKER/u);

    // Rapid edits while a real Vite build is in flight must converge to both
    // latest roles, without an intermediate publication of the superseded UI.
    const publications = watching.count();
    const start = watching.output().length;
    await writeFile(resolve(root, ".sunder/pause-web"), "pause");
    await putUi("UI_SUPERSEDED");
    await watching.wait(() => watching.output().slice(start).includes("WATCH_WEB_PAUSED"), "Vite barrier");
    await putUi("UI_LATEST");
    await putWorker("WORKER_LATEST");
    await putShared("SHARED_LATEST");
    await rm(resolve(root, ".sunder/pause-web"));
    await watching.wait(() => watching.count() > publications, "latest coalesced publication");
    assert.equal(watching.count(), publications + 1);
    assert.match(await text(worker), /WORKER_LATEST/u);
    assert.match(await text(worker), /SHARED_LATEST/u);
    assert.match(await webText(web), /UI_LATEST/u);
    assert.match(await webText(web), /SHARED_LATEST/u);
    assert.doesNotMatch(await webText(web), /UI_SUPERSEDED/u);

    // Generated outputs stay excluded; the next isolated UI edit still reuses
    // the exact Worker. Vite config imports and service identity are structural.
    workerBefore = await workerBuild(root);
    await writeFile(resolve(root, "dist/watch-feedback.txt"), "ignored output");
    await writeFile(resolve(root, ".sunder/watch-feedback.txt"), "ignored tooling state");
    await watching.edit(() => putUi("UI_AFTER_OUTPUT"));
    assert.deepEqual(await workerBuild(root), workerBefore);
    await watching.edit(() => writeFile(resolve(root, "config-label.mjs"), 'export default "CONFIG_UPDATED";'));
    assert.match(await webText(web), /CONFIG_UPDATED/u);
    await watching.edit(async () => writeFile(services, (await text(services)).replace("example.rpc.first", "example.rpc.changed")));
    assert.match(await text(worker), /example\.rpc\.changed/u);
    assert.match(await webText(web), /example\.rpc\.changed/u);
    await assertComplete(dev);
    await watching.close();
  } finally {
    await watching.kill();
    await rm(root, { recursive: true, force: true });
  }
});

test("watch recovers raw Worker syntax errors and invalidates new resolution candidates", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-watch-raw-"));
  await mkdir(resolve(root, "app"));
  await writeFile(resolve(root, "app/value.js"), 'export const value="RESOLUTION_JS";');
  await writeFile(resolve(root, "app/index.html"), '<!doctype html><script type="module" src="./main.js"></script>');
  await writeFile(resolve(root, "app/main.js"), 'import {value} from "./value";document.body.textContent=value;');
  // With no node_modules, Vite creates its temporary bundled config next to
  // this file. Those generated writes must not feed back into package watch.
  await writeFile(resolve(root, "app/vite.config.mjs"), "export default {};\n");
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({
    schemaVersion: 3, id: "test.watch.raw", name: "Raw", version: "1.0.0", workerCapabilities: [], entry: "worker.js",
    app: { root: "app", views: [{ viewId: "test.watch.raw.main", displayName: "Raw", route: "/", defaultPlacement: "middle", showInHotbar: true }] },
  }));
  await writeFile(resolve(root, "worker.js"), "const value = ;");
  const watching = watch(root, true);
  try {
    await watching.wait(() => watching.output().includes("Watching package sources"), "watch ready after initial failure");
    assert.deepEqual(watching.states().map(value => value.state), ["building", "failed"]);
    assert.ok(watching.states().every(value => value.schemaVersion === 1));
    assert.equal(watching.count(), 0);
    await watching.edit(() => writeFile(resolve(root, "worker.js"), 'import {value} from "./app/value";console.log("RAW_FIRST",value);'));
    await watching.wait(() => watching.states().at(-1)?.state === "ready", "successful build status after complete publication");
    assert.ok(watching.states().some(value => value.state === "source-changed"));
    const first = await workerBuild(root);
    const dev = resolve(root, "dist/sunder-dev");
    const before = await snapshot(dev);
    await watching.failed(() => writeFile(resolve(root, "worker.js"), "const value = ;"));
    await watching.wait(() => watching.states().at(-1)?.state === "failed", "failed build status without publication");
    assert.deepEqual(await snapshot(dev), before);
    await watching.edit(() => writeFile(resolve(root, "worker.js"), 'import {value} from "./app/value";console.log("RAW_RECOVERED",value);'));
    await watching.wait(() => watching.states().at(-1)?.state === "ready", "recovered build status");
    assert.equal((await workerBuild(root)).path, first.path, "raw Worker should retain its esbuild context");
    assert.match(await text(resolve(dev, "payload/runtime/shared/bin/worker.cjs")), /RAW_RECOVERED/u);
    // This new App-root file was absent from both graphs. esbuild prefers .ts
    // over .js, so an unknown file cannot be assumed UI-only for a raw Worker.
    await watching.edit(() => writeFile(resolve(root, "app/value.ts"), 'export const value="RESOLUTION_TS";'));
    assert.match(await text(resolve(dev, "payload/runtime/shared/bin/worker.cjs")), /RESOLUTION_TS/u);
    await watching.edit(() => rm(resolve(root, "app/value.ts")));
    assert.match(await text(resolve(dev, "payload/runtime/shared/bin/worker.cjs")), /RESOLUTION_JS/u);
    await assertComplete(dev);
    await watching.close();
  } finally {
    await watching.kill();
    await rm(root, { recursive: true, force: true });
  }
});
