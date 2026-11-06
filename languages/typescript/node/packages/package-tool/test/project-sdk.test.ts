import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { appendFile, copyFile, cp, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { packageToolCompatibility, projectSdkCompatibility } from "../src/compatibility";
import { buildDevPackage, prepareProject } from "../src/package";
import { revalidateOwnedRpc } from "../src/rpc-authoring-plugin";
import { emptyLock } from "./service-fixtures";

async function sdkFixture(root: string, version: string): Promise<string> {
  const source = packageToolCompatibility().sdkPackageRoot;
  const destination = join(root, "sdk-" + version);
  await cp(join(source, "dist"), join(destination, "runtime"), { recursive: true });
  await mkdir(join(destination, "types"));
  for (const file of await readdir(join(destination, "runtime"))) if (file.endsWith(".d.ts")) {
    await copyFile(join(destination, "runtime", file), join(destination, "types", file));
    await rm(join(destination, "runtime", file));
  }
  const metadata = JSON.parse(await readFile(join(source, "package.json"), "utf8"));
  metadata.version = version;
  metadata.main = "runtime/index.js"; metadata.types = "types/index.d.ts";
  for (const entry of Object.values(metadata.exports) as Record<string, string>[]) {
    for (const [condition, path] of Object.entries(entry)) entry[condition] = path.replace("./dist/", condition === "types" ? "./types/" : "./runtime/");
  }
  await writeFile(join(destination, "package.json"), JSON.stringify(metadata));
  await copyFile(join(source, "LICENSE"), join(destination, "LICENSE"));
  await appendFile(join(destination, "runtime/index.js"), `\nexports.sdkFixtureVersion = ${JSON.stringify(version)};\n`);
  await appendFile(join(destination, "types/index.d.ts"), "\nexport declare const sdkFixtureVersion: string;\n");
  return destination;
}
async function selectSdk(project: string, sdk: string): Promise<void> {
  const selected = join(project, "node_modules/@younics/sunder-sdk");
  await mkdir(dirname(selected), { recursive: true });
  await rm(selected, { recursive: true, force: true });
  await symlink(sdk, selected, process.platform === "win32" ? "junction" : "dir");
}
async function projectFixture(root: string): Promise<void> {
  await mkdir(join(root, "src"), { recursive: true });
  await writeFile(join(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(join(root, "src/services.ts"), `import { defineService, sdkFixtureVersion } from "@younics/sunder-sdk";
    export const echo = defineService({ run(request: { value: string }): { value: string } { return { value: request.value + sdkFixtureVersion }; } });`);
  await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.sdk.selection", name: "SDK selection", version: "7.6.5", services: { module: "src/services.ts" }, workerCapabilities: [] }));
}
async function manifestVersion(root: string): Promise<string> {
  return JSON.parse(await readFile(join(root, "dist/sunder-dev/manifest/sunder-package.json"), "utf8")).targets[0].sdkVersion;
}

test("nested projects compile and bundle their installed SDK, including separate declaration exports", async () => {
  const root = await mkdtemp(join(tmpdir(), "sunder-project-sdk-"));
  try {
    const first = await sdkFixture(root, "1.3.4"), second = await sdkFixture(root, "1.8.9");
    const parent = join(root, "workspace"), nested = join(parent, "packages/nested");
    await projectFixture(parent); await projectFixture(nested);
    await selectSdk(parent, first);
    assert.equal(projectSdkCompatibility(nested).sdkVersion, "1.3.4");
    await selectSdk(nested, second);
    for (const [project, version] of [[parent, "1.3.4"], [nested, "1.8.9"]] as const) {
      await buildDevPackage({ projectPath: project, watch: false });
      assert.equal(await manifestVersion(project), version);
      const bundle = await readFile(join(project, "dist/sunder-dev/payload/runtime/shared/bin/worker.cjs"), "utf8");
      assert.ok(bundle.includes(version));
    }
    const loaded = await prepareProject(nested);
    await selectSdk(nested, first);
    await assert.rejects(revalidateOwnedRpc(loaded), /SDK selection changed/);
    assert.equal(projectSdkCompatibility(nested).sdkVersion, "1.3.4");
    const missing = join(root, "missing"); await projectFixture(missing);
    await assert.rejects(prepareProject(missing), /must install @younics\/sunder-sdk/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("watch reselects an SDK when npm replaces its dependency link", { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "sunder-watch-sdk-"));
  let child: ReturnType<typeof spawn> | undefined;
  try {
    const first = await sdkFixture(root, "1.3.4"), second = await sdkFixture(root, "1.8.9");
    const project = join(root, "project"); await projectFixture(project); await selectSdk(project, first);
    child = spawn(process.execPath, [resolve(__dirname, "../../dist/cli.js"), "dev", "--watch", "--project", project],
      { env: { ...process.env, SUNDER_BUILDER_WATCH_PROGRESS: "1" }, stdio: ["pipe", "pipe", "pipe"] });
    let output = "", errors = "";
    child.stdout!.on("data", chunk => { output += chunk.toString(); });
    child.stderr!.on("data", chunk => { errors += chunk.toString(); });
    const ready = async (count: number): Promise<void> => {
      while ((output.match(/"state":"ready"/gu) ?? []).length < count) {
        assert.equal(child!.exitCode, null, errors);
        try { await once(child!.stdout!, "data", { signal: AbortSignal.timeout(45_000) }); }
        catch (cause) { throw new Error(`Watch did not become ready.\n${output}\n${errors}`, { cause }); }
      }
    };
    await ready(1);
    assert.equal(await manifestVersion(project), "1.3.4");
    const count = (output.match(/"state":"ready"/gu) ?? []).length;
    await selectSdk(project, second);
    await ready(count + 1);
    assert.equal(await manifestVersion(project), "1.8.9");
  } finally {
    if (child && child.exitCode === null) { const exited = once(child, "exit"); child.kill("SIGTERM"); await exited; }
    await rm(root, { recursive: true, force: true });
  }
});
