import { createHash, randomUUID } from "node:crypto";
import { lstat, open, readFile, readdir, rename, unlink } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import ts from "typescript";
import { isPackageId } from "@sunder/sdk";
import { loadProject } from "./model";
import { readProjectDescriptorFile } from "./descriptor-files";
import { assertBaselineFreeEvidence, captureBaseline, exists, withBaselineLease } from "./rpc-baseline-store";
import { preparePrivateMetadataInvalidationLocked, withPrivateMetadataOutputLease } from "./rpc-bindings";
import { withOutputLeases } from "./output";

const journalName = ".sunder-builder-metadata.transaction.json";
interface Change { readonly path: string; readonly beforeBase64: string; readonly afterBase64: string }
export interface PrivateMetadataEdit {
  readonly schemaVersion: 1; readonly expectedPackageId: string;
  readonly expectedFiles: readonly { readonly path: string; readonly sha256: string }[];
  readonly edits: readonly Change[];
}
/** A narrowly scoped private-source metadata edit. Never runs npm, build scripts, or authored modules. */
export async function editPrivateProjectMetadata(root: string, request: PrivateMetadataEdit): Promise<void> {
  root = resolve(root);
  if (request.schemaVersion !== 1 || !isPackageId(request.expectedPackageId) || !Array.isArray(request.expectedFiles) || request.expectedFiles.length > 4096
    || !Array.isArray(request.edits) || request.edits.length < 1 || request.edits.length > 16) throw new Error("metadata.request: Invalid bounded metadata request.");
  const baseline = await captureBaseline(root);
  if (baseline.baseline) throw new Error("metadata.history: Retained owned baseline/history prevents automatic ID editing.");
  const paths = new Set<string>();
  for (const change of request.edits) {
    if (!["sunder.package.json", "src/services.ts", "src/services.js", "app/src/App.tsx", "app/src/App.jsx", "app/index.html"].includes(change.path) || paths.has(change.path))
      throw new Error("metadata.request: Only unique starter metadata files may be edited.");
    paths.add(change.path);
    for (const bytes of [change.beforeBase64, change.afterBase64]) if (typeof bytes !== "string" || bytes.length > 8_388_608 || Buffer.from(bytes, "base64").toString("base64") !== bytes) throw new Error("metadata.request: Invalid source snapshot.");
  }
  const configEdit = request.edits.find(change => change.path === "sunder.package.json");
  if (!configEdit) throw new Error("metadata.request: A package config edit is required.");
  await withPrivateMetadataOutputLease(root, async () => withBaselineLease(baseline, async () => withOutputLeases([resolve(root, journalName)], async () => {
    if (await exists(resolve(root, journalName))) throw new Error("metadata.recovery: An interrupted edit has recovery data. Recover the recorded source transaction before retrying.");
    const verify = async (includeGenerated: boolean): Promise<void> => {
      await baseline.revalidate();
      for (const file of request.expectedFiles) {
        if (typeof file.path !== "string" || file.path.includes("\\") || file.path.split("/").some(part => part === ".." || part === "" || part === ".") || !/^[0-9a-f]{64}$/u.test(file.sha256)) throw new Error("metadata.request: Invalid source fingerprint.");
        if (!includeGenerated && file.path.startsWith(".sunder/")) continue;
        if (createHash("sha256").update(await readProjectDescriptorFile(root, resolve(root, file.path), "Expected metadata source", 67_108_864)).digest("hex") !== file.sha256)
          throw new Error(`metadata.conflict: Source/evidence changed: ${file.path}`);
      }
      for (const edit of request.edits) if (!(await readProjectDescriptorFile(root, resolve(root, edit.path), "Metadata source")).equals(Buffer.from(edit.beforeBase64, "base64"))) throw new Error(`metadata.conflict: Source changed: ${edit.path}`);
    };
    await verify(true);
    const history = await captureHistory(root, request.expectedPackageId);
    const project = await loadProject(root);
    if (project.config.id !== request.expectedPackageId || project.config.services?.module !== "src/services.ts" && project.config.services?.module !== "src/services.js") throw new Error("metadata.identity: Select the original private starter source.");
    if (project.ownedRpc?.imports.contracts.length || project.ownedRpc?.importedProviders.length) throw new Error("metadata.identity: Imported identities require manual review; preserve the foreign import lock.");
    const before = JSON.parse(Buffer.from(configEdit.beforeBase64, "base64").toString()) as Record<string, unknown>;
    const after = parseConfigEdit(configEdit);
    const newId = after.id; const newName = after.name;
    if (typeof newId !== "string" || !isPackageId(newId) || !isPackageId(newId + ".messages.provider") || !isPackageId(newId + ".main") || typeof newName !== "string"
      || !newName.length || newName.length > 256 || newName.trim() !== newName || /\p{Cc}/u.test(newName)) throw new Error("metadata.identity: Invalid package or derived metadata.");
    const allowed = structuredClone(before); allowed.id = newId; allowed.name = newName;
    const app = allowed.app as { views: Array<{ viewId: string; displayName: string }> } | undefined;
    if (app) for (const view of app.views) {
      if (view.viewId !== project.config.id + ".main") throw new Error("metadata.identity: Custom view identities require manual review.");
      view.viewId = newId + ".main";
      if (view.displayName === project.config.name) {
        if (newName.length > 128) throw new Error("metadata.identity: Mirrored view names must fit the 128-character view limit.");
        view.displayName = newName;
      }
    }
    if (JSON.stringify(allowed) !== JSON.stringify(after)) throw new Error("metadata.request: Only config id/name and mirrored starter views may change.");
    for (const edit of request.edits.filter(change => change.path !== "sunder.package.json")) {
      const oldText = Buffer.from(edit.beforeBase64, "base64").toString(); const newText = Buffer.from(edit.afterBase64, "base64").toString();
      if (normalizeMirrors(edit.path, oldText, project.config.id, project.config.name) !== normalizeMirrors(edit.path, newText, newId, newName))
        throw new Error(`metadata.request: Unrecognized source edits: ${edit.path}`);
    }
    // The full compiler analysis proves current source is private. Explicit identity overrides are not starter metadata.
    const service = ts.createSourceFile(project.config.services.module, project.ownedRpc!.source, ts.ScriptTarget.Latest, true);
    let custom = false;
    const visit = (node: ts.Node): void => {
      if (ts.isPropertyAssignment(node) && ["contractId", "providerId", "serviceId", "exposure", "version"].includes(node.name.getText(service).replace(/["']/gu, ""))) custom = true;
      ts.forEachChild(node, visit);
    }; visit(service);
    if (custom || project.contracts.some(contract => !contract.imported && (contract.contractKey !== "messages" || contract.exposure !== "package" || contract.descriptor.contractId !== project.config.id + ".messages")))
      throw new Error("metadata.identity: Custom/public service identities require manual review.");
    const invalidate = await preparePrivateMetadataInvalidationLocked(project);
    await verify(true);
    if (await captureHistory(root, request.expectedPackageId) !== history) throw new Error("metadata.conflict: Retained identity history changed during planning.");
    const journal = Buffer.from(JSON.stringify({ schemaVersion: 1, entryPoint: "sunder.package.json", files: request.edits }));
    const journalPath = resolve(root, journalName);
    await durable(journalPath, journal);
    try {
      await invalidate(); await verify(false);
      for (const edit of request.edits) {
        const target = resolve(root, edit.path);
        if (!(await readProjectDescriptorFile(root, target, "Metadata source")).equals(Buffer.from(edit.beforeBase64, "base64"))) throw new Error(`metadata.conflict: Source changed: ${edit.path}`);
        await replace(root, target, Buffer.from(edit.afterBase64, "base64"));
      }
      for (const edit of request.edits) if (!(await readProjectDescriptorFile(root, resolve(root, edit.path), "Metadata publication")).equals(Buffer.from(edit.afterBase64, "base64")))
        throw new Error(`metadata.conflict: Source changed during publication: ${edit.path}`);
      if (!(await readFile(journalPath)).equals(journal)) throw new Error("metadata.conflict: Recovery journal changed.");
      await unlink(journalPath);
    } catch (error) {
      // Restore only bytes still belonging to this operation. A crash leaves the same bounded journal for Builder RecoverAsync.
      const current = await Promise.all(request.edits.map(async edit => ({ edit, bytes: await readProjectDescriptorFile(root, resolve(root, edit.path), "Recovery source") })));
      if (current.every(item => item.bytes.equals(Buffer.from(item.edit.beforeBase64, "base64")) || item.bytes.equals(Buffer.from(item.edit.afterBase64, "base64")))) {
        for (const item of current) if (!item.bytes.equals(Buffer.from(item.edit.beforeBase64, "base64"))) await replace(root, resolve(root, item.edit.path), Buffer.from(item.edit.beforeBase64, "base64"));
        if ((await readFile(journalPath)).equals(journal)) await unlink(journalPath);
      }
      throw error;
    }
  })));
}

function parseConfigEdit(edit: Change): Record<string, unknown> {
  const text = Buffer.from(edit.afterBase64, "base64").toString();
  const tree = ts.parseJsonText("sunder.package.json", text); const value = JSON.parse(text) as Record<string, unknown>;
  const visit = (node: ts.Node): void => {
    if (ts.isObjectLiteralExpression(node)) {
      const keys = node.properties.map(property => (property.name as ts.StringLiteral).text.toLowerCase());
      if (new Set(keys).size !== keys.length) throw new Error("metadata.request: Duplicate/colliding JSON members.");
    } ts.forEachChild(node, visit);
  }; visit(tree); return value;
}
function normalizeMirrors(path: string, text: string, id: string, name: string): string {
  if (path === "app/index.html") {
    const titles = [...text.matchAll(/<title>([^<]*)<\/title>/gu)];
    const decode = (value: string): string => value.replace(/&(#x[0-9a-f]+|#[0-9]+|amp|lt|gt|quot|apos|#39);/giu, (_, entity: string) => entity.startsWith("#x") ? String.fromCodePoint(parseInt(entity.slice(2), 16)) : entity.startsWith("#") ? String.fromCodePoint(Number(entity.slice(1))) : ({ amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" }[entity]!));
    if (titles.length === 1 && decode(titles[0]![1]!) === name) return text.replace(/<title>[^<]*<\/title>/u, "<title>@metadata-name</title>");
    return text;
  }
  const source = ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true);
  const edits: Array<{ start: number; end: number; text: string }> = [];
  for (const statement of source.statements) if (ts.isVariableStatement(statement) && statement.declarationList.flags & ts.NodeFlags.Const) {
    for (const declaration of statement.declarationList.declarations) {
      if (!ts.isIdentifier(declaration.name) || !declaration.initializer || !ts.isStringLiteral(declaration.initializer)) continue;
      const key = declaration.name.text;
      if (key === "packageName" && declaration.initializer.text === name || key === "packageId" && path.startsWith("app/") && declaration.initializer.text === id)
        edits.push({ start: declaration.initializer.getStart(source), end: declaration.initializer.end, text: JSON.stringify("@metadata-" + key) });
    }
  }
  for (const edit of edits.sort((a, b) => b.start - a.start)) text = text.slice(0, edit.start) + edit.text + text.slice(edit.end);
  return text;
}
async function durable(path: string, bytes: Buffer): Promise<void> {
  const handle = await open(path, "wx"); try { await handle.writeFile(bytes); await handle.sync(); } finally { await handle.close(); }
}
async function replace(root: string, path: string, bytes: Buffer): Promise<void> {
  await readProjectDescriptorFile(root, path, "Metadata replacement target");
  const stage = resolve(dirname(path), ".sunder-builder-stage-" + randomUUID());
  try { await durable(stage, bytes); await readProjectDescriptorFile(root, path, "Metadata replacement target"); await rename(stage, path); }
  finally { if (await exists(stage) && (await lstat(stage)).isFile() && (await readFile(stage)).equals(bytes)) await unlink(stage); }
}

/** Current receipt absence cannot erase history retained in another build frame. */
async function captureHistory(root: string, owner: string): Promise<string> {
  const pending = [{ path: root, depth: 0 }]; const files: Array<[string, string]> = []; let count = 0;
  while (pending.length) {
    const directory = pending.pop()!;
    if (directory.depth > 32) throw new Error("metadata.history: Evidence traversal exceeds 32 levels.");
    for (const entry of await readdir(directory.path, { withFileTypes: true })) {
      if (++count > 32768) throw new Error("metadata.history: Evidence traversal exceeds 32,768 entries.");
      if ([".git", "node_modules"].includes(entry.name) || entry.name.startsWith(".sunder-builder-")) continue;
      const path = resolve(directory.path, entry.name);
      if (entry.isSymbolicLink()) throw new Error("metadata.history: Linked source/evidence cannot qualify an identity edit.");
      if (entry.isDirectory()) { pending.push({ path, depth: directory.depth + 1 }); continue; }
      if (entry.name === "sunder-contracts.owned.json" || /identity-fork|publication/iu.test(entry.name)) throw new Error("metadata.history: Retained owned baseline/publication history prevents automatic ID editing.");
      if (!["authoring-receipt.v1.json", "app-authoring-receipt.v1.json", "package-export.v1.json", "owned-baseline.sha256"].includes(entry.name)) continue;
      const bytes = await readProjectDescriptorFile(root, path, "Retained metadata history", 67_108_864);
      if (entry.name === "owned-baseline.sha256") { if (bytes.toString() !== "absent\n") throw new Error("metadata.history: Restore the original public baseline."); }
      else assertBaselineFreeEvidence(bytes, path, owner, entry.name === "package-export.v1.json" ? "export" : "receipt");
      files.push([path, createHash("sha256").update(bytes).digest("hex")]);
    }
  }
  return JSON.stringify(files.sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
}
