import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { exerciseOwnedWorker } from "./owned-rpc-worker";

const exec = promisify(execFile);

/** Exercise the actual npm entry point and installed template dependency graph outside the checkout. */
export async function exercisePackedScaffolds(archives: readonly string[], creator: string): Promise<void> {
  for (const preset of ["node", "react-node"]) for (const language of ["ts", "js"]) {
    const root = await mkdtemp(resolve(tmpdir(), "sunder-packed-scaffold-"));
    const id = `packed.${preset.replaceAll("-", ".")}`;
    const name = `Packed ${preset}`;
    try {
      await exec(process.execPath, [creator, root, "--preset", preset, "--language", language, "--package-id", id, "--package-name", name]);
      await assert.rejects(stat(resolve(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
      const config = await readFile(resolve(root, "sunder.package.json"));
      const source = await readFile(resolve(root, `src/services.${language}`));
      const lock = await readFile(resolve(root, "sunder-contracts.lock.json"));
      await exec("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", ...archives], { cwd: root });
      const sdkRoot = resolve(root, "node_modules/@sunder/sdk");
      const sdk = JSON.parse(await readFile(resolve(sdkRoot, "package.json"), "utf8"));
      assert.equal(sdk.peerDependenciesMeta.react.optional, true);
      if (preset === "node") await assert.rejects(stat(resolve(root, "node_modules/react")), { code: "ENOENT" });
      for (const retired of ["dist/authoring.js", "test/historical-authoring.ts", "dist-test/test/historical-authoring.js"])
        await assert.rejects(stat(resolve(sdkRoot, retired)), { code: "ENOENT" });
      const run = (script: string) => exec("npm", ["run", script], { cwd: root, maxBuffer: 4 * 1024 * 1024 });
      await run("contracts:check");
      const baselinePath = resolve(root, "sunder-contracts.owned.json");
      await assert.rejects(stat(baselinePath), { code: "ENOENT" });
      await exec("npm", ["test"], { cwd: root });
      // Check the emitted declaration closure with no Node ambient types and no
      // skipLibCheck; a browser facade must not leak the Worker SDK entry point.
      await writeFile(resolve(root, "browser-declarations.ts"), 'import {Self} from "#sunder/api"; import {createBrowserContext} from "@sunder/sdk/browser"; const owner=createBrowserContext(Self); const result:Promise<string>=owner.api(Self).messages.send("browser"); void result; owner.dispose();\n');
      await writeFile(resolve(root, "tsconfig.browser-declarations.json"), JSON.stringify({ compilerOptions: {
        target: "ES2023", module: "ESNext", moduleResolution: "Bundler", lib: ["ES2023", "DOM", "DOM.Iterable"], types: [], strict: true, noEmit: true, skipLibCheck: false,
      }, files: ["browser-declarations.ts"] }));
      await exec(process.execPath, [resolve(root, "node_modules/typescript/bin/tsc"), "-p", "tsconfig.browser-declarations.json"], { cwd: root });
      await run("sunder:dev:once");
      await exerciseOwnedWorker(root, true, "1.0.0", { packageName: name, app: preset === "react-node" });
      await run("build");
      await exerciseOwnedWorker(root, false, "1.0.0", { packageName: name, app: preset === "react-node" });
      await assert.rejects(stat(baselinePath), { code: "ENOENT" });
      await run("package");
      assert.ok((await stat(resolve(root, `dist/${id}.1.0.0.sunderpkg`))).size > 0);
      const manifest = JSON.parse(await readFile(resolve(root, "dist/package/manifest/sunder-package.json"), "utf8"));
      assert.equal(manifest.targets.length, preset === "react-node" ? 12 : 6);
      assert.equal(manifest.usesContracts.length, preset === "react-node" ? 1 : 0);
      assert.equal(manifest.provides.length, 1);
      assert.equal(manifest.provides[0].role, "runtime");
      assert.equal(manifest.provides[0].contractId, `${id}.messages`);
      assert.equal(manifest.provides[0].contractVersion, "1.0.0");
      if (preset === "react-node") {
        assert.equal(manifest.usesContracts[0].role, "app");
        assert.deepEqual(manifest.usesContracts[0].actions, ["discover", "invoke", "subscribe"]);
      }
      await assert.rejects(stat(baselinePath), { code: "ENOENT" });
      assert.deepEqual(await readFile(resolve(root, "sunder.package.json")), config);
      assert.deepEqual(await readFile(resolve(root, `src/services.${language}`)), source);
      assert.deepEqual(await readFile(resolve(root, "sunder-contracts.lock.json")), lock);
      assert.equal((await readdir(resolve(root, ".sunder/generated/rpc"))).includes("example-messages.g.ts"), false);
      // A strict project typecheck and the source prepass both reject invalid ordinary handlers.
      await writeFile(resolve(root, `src/services.${language}`), source.toString().replace("return `Hello from ${packageName}: ${message}`;", "return 42;"));
      await assert.rejects(run("build"), /handler-incomplete/);
      await assert.rejects(stat(baselinePath), { code: "ENOENT" });
    } finally { await rm(root, { recursive: true, force: true }); }
  }
}
