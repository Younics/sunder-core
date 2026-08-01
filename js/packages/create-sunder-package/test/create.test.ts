import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { readFile, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";

test("npm create entrypoint scaffolds a usable token-free Node Runtime package", async () => {
  const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
  try {
    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    await run(process.execPath, [cli, output, "--package-id", "sample.node", "--package-name", "Sample Node", "--yes"], process.cwd());
    const config = JSON.parse(await readFile(resolve(output, "sunder.package.json"), "utf8")) as Record<string, unknown>;
    assert.equal(config.id, "sample.node");
    assert.equal(config.name, "Sample Node");
    const worker = await readFile(resolve(output, "src", "worker.ts"), "utf8");
    assert.match(worker, /providerId: "sample\.node\.provider"/u);
    assert.match(worker, /contractSha256: "[0-9a-f]{64}"/u);
    for (const file of await files(output)) {
      if (file.includes("node_modules")) continue;
      assert.doesNotMatch(await readFile(file, "utf8"), /SUNDER_(?:PACKAGE|NPM|CONTRACT)/u);
    }
    assert.equal(await exists(resolve(output, ".gitignore")), true);
    assert.equal(await exists(resolve(output, "gitignore")), false);
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("react-node template scaffolds a namespaced web view with the browser-only SDK", async () => {
  const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
  try {
    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    await run(process.execPath, [
      cli,
      output,
      "--template",
      "react-node",
      "--package-id",
      "sample.react",
      "--package-name",
      "Sample React",
      "--yes",
    ], process.cwd());
    const config = JSON.parse(await readFile(resolve(output, "sunder.package.json"), "utf8")) as {
      readonly app: { readonly views: readonly { readonly viewId: string; readonly route: string }[] };
    };
    assert.equal(config.app.views[0]?.viewId, "sample.react.main");
    assert.equal(config.app.views[0]?.route, "/");
    assert.match(
      await readFile(resolve(output, "app", "src", "App.tsx"), "utf8"),
      /from "@sunder\/sdk\/browser"/u,
    );
    assert.equal(await exists(resolve(output, "app", "vite.config.ts")), true);
    for (const file of await files(output)) {
      assert.doesNotMatch(await readFile(file, "utf8"), /SUNDER_(?:PACKAGE|NPM|CONTRACT)/u);
    }
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

async function run(file: string, argumentsList: readonly string[], cwd: string): Promise<void> {
  await new Promise<void>((resolvePromise, reject) => {
    const child = spawn(file, [...argumentsList], { cwd, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    child.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    child.once("error", reject);
    child.once("exit", (code) => code === 0 ? resolvePromise() : reject(new Error(output)));
  });
}

async function files(root: string): Promise<string[]> {
  const output: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = resolve(root, entry.name);
    if (entry.isDirectory()) output.push(...await files(path));
    else if (entry.isFile()) output.push(path);
  }
  return output;
}

async function exists(path: string): Promise<boolean> {
  try {
    await readFile(path);
    return true;
  } catch {
    return false;
  }
}
