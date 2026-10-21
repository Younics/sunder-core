import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, mkdir, readFile, rm, writeFile, readdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { editPrivateProjectMetadata, type PrivateMetadataEdit } from "../src/metadata-edit";
import { generateRpcBindings, rpcBindingsOutputRoot } from "../src/rpc-bindings";
import { contractsCommand } from "../src/rpc-contracts";
import { emptyLock } from "./service-fixtures";

const template = resolve(__dirname, "../../../create-sunder-package/template-react-node");
async function project(): Promise<string> {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-metadata-edit-"));
  for (const path of ["sunder.package.json", "src/services.ts", "app/src/App.tsx", "app/index.html"]) {
    const text = (await readFile(resolve(template, path), "utf8"))
      .replaceAll("sunderpackageidscaffoldtoken", "example.old")
      .replaceAll("__SUNDER_PACKAGE_ID_JSON__", "example.old")
      .replaceAll("__SUNDER_PACKAGE_NAME_JSON__", "Old display")
      .replaceAll("__SUNDER_PACKAGE_NAME_HTML__", "Old display");
    await mkdir(dirname(resolve(root, path)), { recursive: true }); await writeFile(resolve(root, path), text);
  }
  await writeFile(resolve(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(resolve(root, "package.json"), '{"name":"npm-name-stays","type":"module"}');
  return root;
}
async function request(root: string): Promise<PrivateMetadataEdit> {
  const paths = ["sunder.package.json", "src/services.ts", "app/src/App.tsx", "app/index.html"];
  const edits = await Promise.all(paths.map(async path => {
    const before = await readFile(resolve(root, path));
    let after = before.toString().replaceAll("Old display", "New display");
    if (path === "sunder.package.json" || path === "app/src/App.tsx") after = after.replaceAll("example.old", "example.new");
    return { path, beforeBase64: before.toString("base64"), afterBase64: Buffer.from(after).toString("base64") };
  }));
  return { schemaVersion: 1, expectedPackageId: "example.old", edits,
    expectedFiles: await Promise.all([...paths, "sunder-contracts.lock.json"].map(async path => ({ path, sha256: createHash("sha256").update(await readFile(resolve(root, path))).digest("hex") }))) };
}
async function files(root: string): Promise<Map<string, Buffer>> {
  const result = new Map<string, Buffer>();
  const walk = async (directory: string, prefix: string): Promise<void> => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name); const relative = prefix + entry.name;
      if (entry.isDirectory()) await walk(path, relative + "/"); else result.set(relative, await readFile(path));
    }
  }; await walk(root, ""); return result;
}

test("already-generated private React starter edits metadata, invalidates old receipts, and regenerates under the new owner", async () => {
  const root = await project();
  try {
    await generateRpcBindings(root);
    const old = JSON.parse((await readFile(resolve(rpcBindingsOutputRoot(root), "authoring-receipt.v1.json"))).toString());
    assert.equal(old.packageId, "example.old");
    const lock = await readFile(resolve(root, "sunder-contracts.lock.json"));
    await editPrivateProjectMetadata(root, await request(root));
    assert.deepEqual(await readdir(rpcBindingsOutputRoot(root)), ["owned-baseline.sha256"]);
    assert.equal(JSON.parse((await readFile(resolve(root, "sunder.package.json"))).toString()).id, "example.new");
    assert.match(await readFile(resolve(root, "app/src/App.tsx"), "utf8"), /const packageId = "example.new"/u);
    assert.match(await readFile(resolve(root, "src/services.ts"), "utf8"), /const packageName = "New display"/u);
    assert.deepEqual(await readFile(resolve(root, "sunder-contracts.lock.json")), lock);
    assert.match(await readFile(resolve(root, "package.json"), "utf8"), /npm-name-stays/u);
    await generateRpcBindings(root);
    const fresh = JSON.parse(await readFile(resolve(rpcBindingsOutputRoot(root), "authoring-receipt.v1.json"), "utf8"));
    assert.equal(fresh.packageId, "example.new");
    assert.equal(fresh.contracts[0].contractId, "example.new.messages");
    assert.equal(fresh.providers[0].providerId, "example.new.messages.provider");
    assert.equal((await files(root)).has(".sunder-builder-metadata.transaction.json"), false);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("unknown generated files, stale source snapshots, and unsupported source edits preserve the entire tree", async () => {
  const root = await project();
  try {
    await generateRpcBindings(root);
    await writeFile(resolve(rpcBindingsOutputRoot(root), "unowned.txt"), "retain unknown bytes");
    let before = await files(root);
    await assert.rejects(editPrivateProjectMetadata(root, await request(root)), /Unknown or changed generated files/u);
    assert.deepEqual(await files(root), before);
    await rm(resolve(rpcBindingsOutputRoot(root), "unowned.txt"));
    const orphan = rpcBindingsOutputRoot(root) + ".stage-unproven";
    await mkdir(orphan); await writeFile(resolve(orphan, "unknown-history.json"), "retain even unrecognized history");
    before = await files(root);
    await assert.rejects(editPrivateProjectMetadata(root, await request(root)), /Interrupted or unproven/u);
    assert.deepEqual(await files(root), before);
    await rm(orphan, { recursive: true });
    const stale = await request(root);
    await writeFile(resolve(root, "app/src/App.tsx"), "// concurrent user edit\n");
    before = await files(root);
    await assert.rejects(editPrivateProjectMetadata(root, stale), /metadata.conflict/u);
    assert.deepEqual(await files(root), before);
    const current = await request(root);
    const edit = current.edits.find(item => item.path === "src/services.ts")!;
    const unsupported = { ...current, edits: current.edits.map(item => item === edit ? { ...item, afterBase64: Buffer.from(Buffer.from(item.afterBase64, "base64").toString() + '\nthrow new Error("custom edit");').toString("base64") } : item) };
    await assert.rejects(editPrivateProjectMetadata(root, unsupported), /Unrecognized source edits/u);
    assert.deepEqual(await files(root), before);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("an interrupted metadata journal blocks generation rather than building mixed source", async () => {
  const root = await project();
  try {
    await generateRpcBindings(root);
    const evidence = await readFile(resolve(rpcBindingsOutputRoot(root), "authoring-receipt.v1.json"));
    await writeFile(resolve(root, ".sunder-builder-metadata.transaction.json"), '{"schemaVersion":1,"entryPoint":"sunder.package.json","files":[]}');
    await assert.rejects(generateRpcBindings(root), /metadata.recovery/u);
    assert.deepEqual(await readFile(resolve(rpcBindingsOutputRoot(root), "authoring-receipt.v1.json")), evidence);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("missing baseline never reinterprets prior public receipts as private identity permission", async () => {
  const root = await project();
  try {
    const service = resolve(root, "src/services.ts");
    const privateSource = await readFile(service, "utf8");
    await writeFile(service, privateSource.replace("methods:", 'exposure: "public", methods:'));
    await contractsCommand("accept", root, {});
    await generateRpcBindings(root);
    const baseline = await readFile(resolve(root, "sunder-contracts.owned.json"));
    let before = await files(root);
    await assert.rejects(editPrivateProjectMetadata(root, await request(root)), /Retained owned baseline/u);
    assert.deepEqual(await files(root), before);
    await rm(resolve(root, "sunder-contracts.owned.json"));
    await writeFile(service, privateSource);
    before = await files(root);
    await assert.rejects(editPrivateProjectMetadata(root, await request(root)), /baseline|history/u);
    assert.deepEqual(await files(root), before);
    assert.ok(baseline.length > 0);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("public history in another output frame also blocks a currently private project", async () => {
  const root = await project();
  try {
    await generateRpcBindings(root);
    const receipt = JSON.parse(await readFile(resolve(rpcBindingsOutputRoot(root), "authoring-receipt.v1.json"), "utf8"));
    receipt.contracts[0].exposure = "public";
    await mkdir(resolve(root, "dist/retained"), { recursive: true });
    await writeFile(resolve(root, "dist/retained/authoring-receipt.v1.json"), JSON.stringify(receipt));
    const before = await files(root);
    await assert.rejects(editPrivateProjectMetadata(root, await request(root)), /baseline|history/u);
    assert.deepEqual(await files(root), before);
  } finally { await rm(root, { recursive: true, force: true }); }
});
