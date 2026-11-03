import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import test from "node:test";
import { pathToFileURL } from "node:url";
import { runOwnedTestProcess } from "./run-owned-test-process.mjs";

const leaf = `
  const fs = require('node:fs');
  fs.writeFileSync(process.argv[1], String(process.pid));
  process.on('SIGTERM', () => {});
  setInterval(() => fs.appendFileSync(process.argv[1], '.'), 20);
  process.send('ready');
`;
const tree = `
  const { spawn } = require('node:child_process');
  const path = process.argv[1];
  const leaf = spawn(process.execPath, ['-e', ${JSON.stringify(leaf)}, path], { stdio: ['ignore', 'inherit', 'inherit', 'ipc'] });
  leaf.on('message', () => {
    console.log('descendant-ready');
    if (process.argv[2] === 'exit') process.exit(0);
  });
  process.on('SIGTERM', () => {});
  setInterval(() => {}, 1000);
`;

async function fixture(action) {
  const diagnostics = process.env.SUNDER_TEST_PROCESS_DIAGNOSTICS_DIRECTORY;
  if (diagnostics !== undefined) await mkdir(diagnostics, { recursive: true });
  const root = await mkdtemp(resolve(diagnostics ?? tmpdir(), "sunder-owned-process-"));
  let failed = false;
  try { await action(root, (extra = {}) => ({ cwd: root, logStem: resolve(root, "command with spaces"),
    timeoutMilliseconds: 15_000, streamOutput: false, ...extra })); }
  catch (error) {
    failed = true;
    if (diagnostics !== undefined) console.error(`Failed process fixture retained at ${root}`);
    throw error;
  } finally {
    if (!failed || diagnostics === undefined) await rm(root, { recursive: true, force: true });
  }
}

async function unchanged(path) {
  const first = await readFile(path, "utf8");
  await delay(120);
  assert.equal(await readFile(path, "utf8"), first, "Owned descendant is still writing after cleanup.");
}

test("owned command preserves argument bytes, exit status and streamed diagnostics", async () => fixture(async (root, options) => {
  const args = ['space value', 'quote"value', 'trailing\\', ''];
  const result = await runOwnedTestProcess(process.execPath, ["-e",
    "console.log(JSON.stringify(process.argv.slice(1))); console.error('failure detail'); process.exit(7)", ...args], options());
  assert.equal(result.status, 7);
  assert.equal(result.error, null);
  assert.equal(result.cleanupComplete, true);
  assert.deepEqual(JSON.parse(await readFile(`${options().logStem}.stdout.log`, "utf8")), args);
  assert.match(await readFile(`${options().logStem}.stderr.log`, "utf8"), /failure detail/u);
  assert.deepEqual(JSON.parse(await readFile(`${options().logStem}.process.json`, "utf8")), result);
}));

test("project timeout kills a surviving descendant without touching an unrelated process", async () => fixture(async (root, options) => {
  const foreign = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
  const foreignClosed = new Promise((resolve) => foreign.once("close", resolve));
  try {
    const marker = resolve(root, "heartbeat");
    const result = await runOwnedTestProcess(process.execPath, ["-e", tree, marker, "wait"], options({ timeoutMilliseconds: 5_000 }));
    assert.equal(result.error?.code, "ETIMEDOUT", JSON.stringify(result));
    assert.equal(result.cleanupComplete, true, JSON.stringify(result));
    assert.match(await readFile(`${options().logStem}.stdout.log`, "utf8"), /descendant-ready/u);
    await unchanged(marker);
    assert.equal(foreign.exitCode, null);
    assert.equal(foreign.signalCode, null);
    process.kill(foreign.pid, 0);
  } finally {
    foreign.kill("SIGKILL");
    await foreignClosed;
  }
}));

test("normal launcher exit still drains and terminates its leftover descendant", async () => fixture(async (root, options) => {
  const marker = resolve(root, "heartbeat");
  const result = await runOwnedTestProcess(process.execPath, ["-e", tree, marker, "exit"], options());
  assert.equal(result.status, 0, JSON.stringify(result));
  assert.equal(result.error, null);
  assert.equal(result.cleanupComplete, true);
  await unchanged(marker);
}));

test("an owned test process can create its own nested containment without escaping the reporter", async () => fixture(async (root, options) => {
  const nested = `
    import { runOwnedTestProcess } from ${JSON.stringify(new URL("./run-owned-test-process.mjs", import.meta.url).href)};
    const result = await runOwnedTestProcess(process.execPath, ['-e', 'console.log("nested command")'], {
      cwd: ${JSON.stringify(root)}, logStem: ${JSON.stringify(resolve(root, "nested"))},
      timeoutMilliseconds: 10_000, streamOutput: false,
    });
    if (result.status !== 0 || result.error !== null || !result.cleanupComplete) throw new Error(JSON.stringify(result));
  `;
  const result = await runOwnedTestProcess(process.execPath, ["--input-type=module", "-e", nested], options({ timeoutMilliseconds: 30_000 }));
  assert.equal(result.status, 0, JSON.stringify(result));
  assert.equal(result.error, null);
  assert.equal(result.cleanupComplete, true);
  assert.match(await readFile(resolve(root, "nested.stdout.log"), "utf8"), /nested command/u);
}));

test("cancellation cleans the owned tree before returning", async () => fixture(async (root, options) => {
  const cancel = new AbortController();
  const marker = resolve(root, "heartbeat");
  const running = runOwnedTestProcess(process.execPath, ["-e", tree, marker, "wait"], options({ cancellationSignal: cancel.signal }));
  try {
    const deadline = Date.now() + 10_000;
    while (!existsSync(marker) && Date.now() < deadline) await delay(25);
    assert.ok(existsSync(marker), "Fixture descendant did not become ready.");
    const pending = JSON.parse(await readFile(`${options().logStem}.process.json`, "utf8"));
    assert.equal(pending.error.code, "EINCOMPLETE");
    assert.equal(pending.cleanupComplete, false);
  } finally { cancel.abort(); }
  const result = await running;
  assert.equal(result.error?.code, "ECANCELED", JSON.stringify(result));
  assert.equal(result.cleanupComplete, true);
  await unchanged(marker);
}));

test("output overflow and spawn failure retain explicit failures instead of looking like passing runs", async () => {
  await fixture(async (root, options) => {
    const result = await runOwnedTestProcess(process.execPath, ["-e", "setInterval(() => console.log('x'.repeat(4096)), 1)"],
      options({ maximumOutputBytes: 8192 }));
    assert.equal(result.error?.code, "ENOBUFS", JSON.stringify(result));
    assert.equal(result.cleanupComplete, true);
    assert.ok((await readFile(`${options().logStem}.stdout.log`)).length <= 8192);
  });
  await fixture(async (root, options) => {
    const result = await runOwnedTestProcess(resolve(root, "missing executable"), [], options());
    assert.notEqual(result.error, null);
    assert.notEqual(result.status, 0);
    assert.equal(result.cleanupComplete, true, JSON.stringify(result));
  });
});

test("a results log write failure still terminates and drains the owned tree", async () => fixture(async (root, options) => {
  const hook = resolve(root, "failed log writer #100%.mjs");
  await writeFile(hook, `
    import fs from 'node:fs';
    import { syncBuiltinESMExports } from 'node:module';
    const open = fs.openSync, write = fs.writeSync, close = fs.closeSync;
    const logs = new Set();
    fs.openSync = (path, ...args) => {
      const descriptor = open(path, ...args);
      if (String(path).endsWith('.stdout.log') || String(path).endsWith('.stderr.log')) logs.add(descriptor);
      return descriptor;
    };
    fs.closeSync = (descriptor) => { logs.delete(descriptor); return close(descriptor); };
    fs.writeSync = (descriptor, ...args) => {
      if (logs.has(descriptor)) throw Object.assign(new Error('seeded full results filesystem'), { code: 'ENOSPC' });
      return write(descriptor, ...args);
    };
    syncBuiltinESMExports();
  `);
  const marker = resolve(root, "heartbeat");
  const driver = `
    import { runOwnedTestProcess } from ${JSON.stringify(new URL("./run-owned-test-process.mjs", import.meta.url).href)};
    const result = await runOwnedTestProcess(process.execPath, ['-e', ${JSON.stringify(tree)}, ${JSON.stringify(marker)}, 'wait'],
      ${JSON.stringify(options())});
    if (result.error?.code !== 'ENOSPC' || !result.cleanupComplete) throw new Error(JSON.stringify(result));
  `;
  const result = spawnSync(process.execPath, ["--import", pathToFileURL(hook).href, "--input-type=module", "-e", driver], { encoding: "utf8", timeout: 45_000 });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const receipt = JSON.parse(await readFile(`${options().logStem}.process.json`, "utf8"));
  assert.equal(receipt.error.code, "ENOSPC");
  assert.equal(receipt.cleanupComplete, true);
  await unchanged(marker);
}));
