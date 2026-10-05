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
  for (const preset of ["node", "react-node"]) {
    const root = await mkdtemp(resolve(tmpdir(), "sunder-packed-scaffold-"));
    const id = `packed.${preset.replaceAll("-", ".")}`;
    const name = `Packed ${preset}`;
    try {
      await exec(process.execPath, [creator, root, "--preset", preset, "--package-id", id, "--package-name", name]);
      await assert.rejects(stat(resolve(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
      const config = await readFile(resolve(root, "sunder.package.json"));
      const source = await readFile(resolve(root, "src/rpc.ts"));
      const lock = await readFile(resolve(root, "sunder-contracts.lock.json"));
      await exec("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", ...archives], { cwd: root });
      const run = (script: string) => exec("npm", ["run", script], { cwd: root, maxBuffer: 4 * 1024 * 1024 });
      await run("contracts:accept"); await run("contracts:check");
      const baselinePath = resolve(root, "sunder-contracts.owned.json");
      const baseline = await readFile(baselinePath);
      assert.equal(JSON.parse(baseline.toString()).contracts.messages.current.contractVersion, "1.0.0");
      await exec("npm", ["test"], { cwd: root });
      await run("sunder:dev:once");
      await exerciseOwnedWorker(root, true, "1.0.0", { packageName: name, app: preset === "react-node" });
      await run("build");
      await exerciseOwnedWorker(root, false, "1.0.0", { packageName: name, app: preset === "react-node" });
      assert.deepEqual(await readFile(baselinePath), baseline);
      await assert.rejects(run("package"), /frozen|Freeze|freeze/);
      await run("contracts:freeze");
      const frozen = await readFile(baselinePath);
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
      assert.deepEqual(await readFile(baselinePath), frozen);
      assert.deepEqual(await readFile(resolve(root, "sunder.package.json")), config);
      assert.deepEqual(await readFile(resolve(root, "src/rpc.ts")), source);
      assert.deepEqual(await readFile(resolve(root, "sunder-contracts.lock.json")), lock);
      assert.equal((await readdir(resolve(root, ".sunder/generated/rpc"))).includes("example-messages.g.ts"), false);
      // A strict project typecheck and the source prepass both reject invalid ordinary handlers.
      await writeFile(resolve(root, "src/rpc.ts"), source.toString().replace("return { message: `Hello from ${packageName}: ${request.message}` };", "return { message: 42 };"));
      await assert.rejects(run("build"), /handler-incomplete/);
      assert.deepEqual(await readFile(baselinePath), frozen);
    } finally { await rm(root, { recursive: true, force: true }); }
  }
}
