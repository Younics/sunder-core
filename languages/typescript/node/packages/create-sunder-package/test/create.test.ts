import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { lstat, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";

test("npm create entrypoint scaffolds a usable token-free Node Runtime package", async () => {
  const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
  try {
    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    await run(process.execPath, [cli, output, "--package-id", "sample.node", "--package-name", "Sample Node", "--yes"], process.cwd());
    const config = JSON.parse(await readFile(resolve(output, "sunder.package.json"), "utf8")) as ScaffoldPackageConfig;
    assert.equal(config.schemaVersion, 3);
    assert.equal(config.id, "sample.node");
    assert.equal(config.name, "Sample Node");
    assert.deepEqual(config.workerCapabilities, []);
    assert.deepEqual(config.services, { module: "src/services.ts" });
    for (const key of ["contracts", "providers", "usesContracts", "rpcBindings"]) assert.equal(key in config, false);
    const packageManifest = JSON.parse(await readFile(resolve(output, "package.json"), "utf8")) as {
      readonly engines: { readonly node: string };
      readonly allowScripts: Readonly<Record<string, boolean>>;
    };
    assert.equal(packageManifest.engines.node, ">=24.18.1 <25.0.0");
    assert.deepEqual(packageManifest.allowScripts, { "esbuild@0.28.1": true, fsevents: false });
    await assertOwnedSource(output, "sample.node");
    assert.equal(await exists(resolve(output, "src", "worker.ts")), false);
    assert.equal(await exists(resolve(output, "src", "sunder-contracts.d.ts")), false);
    for (const file of await files(output)) {
      if (file.includes("node_modules")) continue;
      assert.doesNotMatch(await readFile(file, "utf8"), /SUNDER_(?:PACKAGE|NPM|CONTRACT)|sunderpackageidscaffoldtoken/u);
    }
    assert.equal(await exists(resolve(output, ".gitignore")), true);
    assert.match(await readFile(resolve(output, ".gitignore"), "utf8"), /^\.sunder\/$/mu);
    assert.equal(await exists(resolve(output, "gitignore")), false);
    const tsconfig = JSON.parse(await readFile(resolve(output, "tsconfig.json"), "utf8")) as ScaffoldTypeScriptConfig;
    assert.equal(tsconfig.compilerOptions.exactOptionalPropertyTypes, true);
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
    const config = JSON.parse(await readFile(resolve(output, "sunder.package.json"), "utf8")) as ReactScaffoldPackageConfig;
    assert.equal(config.schemaVersion, 3);
    assert.deepEqual(config.workerCapabilities, []);
    assert.equal(config.app.views[0]?.viewId, "sample.react.main");
    assert.equal(config.app.views[0]?.route, "/");
    assert.equal("icon" in config.app.views[0]!, false);
    assert.deepEqual(config.services, { module: "src/services.ts" });
    for (const key of ["contracts", "providers", "usesContracts", "rpcBindings"]) assert.equal(key in config, false);
    const packageManifest = JSON.parse(await readFile(resolve(output, "package.json"), "utf8")) as {
      readonly engines: { readonly node: string };
      readonly allowScripts: Readonly<Record<string, boolean>>;
    };
    assert.equal(packageManifest.engines.node, ">=24.18.1 <25.0.0");
    assert.deepEqual(packageManifest.allowScripts, { "esbuild@0.28.1": true, fsevents: false });
    await assertOwnedSource(output, "sample.react");
    const app = await readFile(resolve(output, "app", "src", "App.tsx"), "utf8");
    assert.match(
      app,
      /from "@sunder\/sdk\/react"/u,
    );
    assert.match(app, /useApi\(Self\)/u);
    assert.match(app, /api\.messages\.send/u);
    assert.doesNotMatch(app, /rpc\.invoke<|providerHandle|contractId|\.discover\(|\.find\(/u);
    assert.equal(await exists(resolve(output, "src", "worker.ts")), false);
    assert.equal(await exists(resolve(output, "src", "sunder-contracts.d.ts")), false);
    assert.equal(await exists(resolve(output, "app", "vite.config.ts")), true);
    assert.equal(await exists(resolve(output, "app", "public", "icon.svg")), false);
    assert.equal(await exists(resolve(output, "tsconfig.worker.json")), true);
    assert.equal(await exists(resolve(output, "tsconfig.browser.json")), true);
    assert.equal(await exists(resolve(output, "tsconfig.json")), false);
    const workerConfig = JSON.parse(await readFile(resolve(output, "tsconfig.worker.json"), "utf8")) as ScaffoldTypeScriptConfig;
    const browserConfig = JSON.parse(await readFile(resolve(output, "tsconfig.browser.json"), "utf8")) as ScaffoldTypeScriptConfig;
    assert.equal(workerConfig.compilerOptions.exactOptionalPropertyTypes, true);
    assert.equal(browserConfig.compilerOptions.exactOptionalPropertyTypes, true);
    assert.match(await readFile(resolve(output, ".gitignore"), "utf8"), /^\.sunder\/$/mu);
    assert.match(await readFile(resolve(output, "app", "vite.config.ts"), "utf8"), /sourcemap: false/u);
    for (const file of await files(output)) {
      assert.doesNotMatch(await readFile(file, "utf8"), /SUNDER_(?:PACKAGE|NPM|CONTRACT)|sunderpackageidscaffoldtoken/u);
    }
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("scaffold substitutions preserve adversarial display names in JSON, TypeScript, HTML, and Markdown contexts", async () => {
  const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
  const packageName = "Name </title><script>alert(1)</script> `${value}` __SUNDER_PACKAGE_ID_JSON__";
  try {
    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    await run(process.execPath, [
      cli,
      output,
      "--preset",
      "react-node",
      "--package-id",
      "sample.adversarial",
      "--package-name",
      packageName,
      "--yes",
    ], process.cwd());
    const config = JSON.parse(await readFile(resolve(output, "sunder.package.json"), "utf8")) as { readonly name: string };
    assert.equal(config.name, packageName);
    const source = await readFile(resolve(output, "src", "services.ts"), "utf8");
    assert.ok(source.includes(JSON.stringify(packageName)));
    const html = await readFile(resolve(output, "app", "index.html"), "utf8");
    assert.doesNotMatch(html, /<script>/u);
    assert.match(html, /&lt;\/title&gt;&lt;script&gt;/u);
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("all presets exclude locally generated .sunder directories from scaffold output", async () => {
  const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
  const sources: Array<{ readonly generatedDirectory: string; readonly existed: boolean; readonly marker: string }> = [];
  const outputs: string[] = [];
  try {
    for (const [preset, templateDirectory] of [["node", "template"], ["react-node", "template-react-node"]] as const) {
      const generatedDirectory = resolve(__dirname, "..", "..", templateDirectory, ".sunder");
      const existed = await exists(generatedDirectory);
      const marker = resolve(generatedDirectory, `scaffold-copy-regression-${randomUUID()}.txt`);
      sources.push({ generatedDirectory, existed, marker });
      await mkdir(generatedDirectory, { recursive: true });
      await writeFile(marker, "must not be scaffolded\n", "utf8");

      const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
      outputs.push(output);
      await run(process.execPath, [cli, output, "--preset", preset, "--yes"], process.cwd());
      assert.equal(await exists(resolve(output, ".sunder")), false);
    }
  } finally {
    await Promise.all(outputs.map((output) => rm(output, { recursive: true, force: true })));
    for (const source of sources) {
      await rm(source.marker, { force: true });
      if (!source.existed) await rm(source.generatedDirectory, { recursive: true, force: true });
    }
  }
});

test("all presets accept the longest package id whose generated provider id fits package-tool limits", async () => {
  const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
  const packageId = "a".repeat(110);
  const outputs: string[] = [];
  try {
    for (const preset of ["node", "react-node"] as const) {
      const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
      outputs.push(output);
      await run(process.execPath, [cli, output, "--preset", preset, "--package-id", packageId, "--yes"], process.cwd());
      const config = JSON.parse(await readFile(resolve(output, "sunder.package.json"), "utf8")) as {
        readonly id: string;
        readonly app?: { readonly views: readonly { readonly viewId: string }[] };
      };
      assert.equal(config.id, packageId);
       assert.equal(`${packageId}.messages.provider`.length, 128);
      if (preset === "react-node") assert.equal(config.app?.views[0]?.viewId, `${packageId}.main`);
    }
  } finally {
    await Promise.all(outputs.map((output) => rm(output, { recursive: true, force: true })));
  }
});

test("package ids that overflow generated IDs fail clearly before scaffolding", async () => {
  const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
  for (const length of [111, 128]) {
    const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID());
    await assert.rejects(
      run(process.execPath, [cli, output, "--package-id", "a".repeat(length), "--yes"], process.cwd()),
      /--package-id must be at most 110 characters because generated provider and view IDs must fit the package-tool 128-character limit\./u,
    );
    assert.equal(await exists(output), false);
  }
});

test("an overlong package id derived from the output directory fails before scaffolding", async () => {
  const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
  const output = resolve(tmpdir(), "create-sunder-package-tests", randomUUID(), "a".repeat(120));
  await assert.rejects(
    run(process.execPath, [cli, output, "--yes"], process.cwd()),
    /--package-id must be at most 110 characters because generated provider and view IDs must fit the package-tool 128-character limit\./u,
  );
  assert.equal(await exists(output), false);
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

async function assertOwnedSource(root: string, packageId: string): Promise<void> {
  const source = await readFile(resolve(root, "src/services.ts"), "utf8");
  assert.match(source, /export const messages = defineService\(/u);
  assert.match(source, /async send\(message: string, context: ServiceContext\)/u);
  assert.match(source, /async \*watch\(message: string, context: ServiceContext\)/u);
  assert.doesNotMatch(source, /contractId|contractVersion|descriptorSha256|abiSha256|\.rpc\.json/u);
  assert.equal((await files(root)).some((path) => path.endsWith(".rpc.json")), false);
  assert.equal(await exists(resolve(root, "sunder-contracts.owned.json")), false);
  assert.equal(await readFile(resolve(root, "sunder-contracts.lock.json"), "utf8"),
    `${JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2)}\n`);
  const manifest = JSON.parse(await readFile(resolve(root, "package.json"), "utf8"));
  assert.equal(manifest.scripts["contracts:accept"], "sunder-package contracts accept");
  assert.equal(manifest.scripts["contracts:check"], "sunder-package contracts check");
  assert.equal(manifest.scripts["contracts:freeze"], "sunder-package contracts freeze");
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
    await lstat(path);
    return true;
  } catch {
    return false;
  }
}

interface ScaffoldTypeScriptConfig {
  readonly compilerOptions: {
    readonly exactOptionalPropertyTypes?: boolean;
  };
}

interface ScaffoldPackageConfig {
  readonly schemaVersion: number;
  readonly id: string;
  readonly name: string;
  readonly workerCapabilities: readonly string[];
  readonly services: { readonly module: string };
}

interface ReactScaffoldPackageConfig extends ScaffoldPackageConfig {
  readonly app: { readonly views: readonly { readonly viewId: string; readonly route: string }[] };
}
