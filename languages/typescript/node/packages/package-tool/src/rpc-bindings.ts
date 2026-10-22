import { createHash } from "node:crypto";
import type { Stats } from "node:fs";
import { lstat, mkdir, readFile, readdir, rmdir, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import {
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  parseRpcContractDescriptor,
} from "@sunder/sdk";
import { generateTypeScriptBindings } from "./generator";
import { readProjectDescriptorFile } from "./descriptor-files";
import { assertBaselineFreeEvidence } from "./rpc-baseline-store";
import type { RegistryOriginOptions } from "./rpc-contract-lock";
import { MODEL_PROJECTION_PATH, projectionDocumentBytes } from "./rpc-model-projection";
import {
  loadProject,
  resolveInside,
  type LoadedContract,
  type LoadedProject,
} from "./model";
import {
  assertReplaceableGeneratedDirectory,
  cleanupStagedOutputs,
  commitStagedOutputs,
  markerPath,
  stageGeneratedDirectory,
  withOutputLocks,
  withOutputLeases,
} from "./output";

const RPC_BINDINGS_MARKER = "Sunder TypeScript RPC bindings generated output v1\n";
const RPC_BINDINGS_OUTPUT_PATH = ".sunder/generated/rpc";

interface RpcBindingPlan {
  readonly outputFile: string;
  readonly source: string;
}

export async function generateRpcBindings(projectPath: string, options: RegistryOriginOptions = {}): Promise<void> {
  const root = resolve(projectPath);
  await withRpcBindingsOutputLock(root, async () => {
    await generateRpcBindingsForLoadedProjectLocked(await loadProject(root, options));
  });
}

export async function generateRpcBindingsForLoadedProject(project: LoadedProject): Promise<void> {
  await withRpcBindingsOutputLock(project.root, async () => {
    await generateRpcBindingsForLoadedProjectLocked(project);
  });
}

export async function withRpcBindingsOutputLock<T>(projectRoot: string, operation: () => Promise<T>): Promise<T> {
  const root = resolve(projectRoot);
  const outputRoot = rpcBindingsOutputRoot(root);
  await assertSafeGeneratedOutputParents(root, outputRoot);
  const parents = [dirname(outputRoot), dirname(dirname(outputRoot))];
  const absentParents = new Set<string>();
  for (const parent of parents) if (await tryLstat(parent) === undefined) absentParents.add(parent);
  try {
    return await withOutputLocks([outputRoot], operation);
  } finally {
    for (const parent of parents) {
      if (absentParents.has(parent)) await rmdir(parent).catch(() => undefined);
    }
  }
}

/** Metadata qualification must precede recovery/cleanup: wildcard sweeps could otherwise erase unproven history. */
export async function withPrivateMetadataOutputLease<T>(projectRoot: string, operation: () => Promise<T>): Promise<T> {
  const root = resolve(projectRoot); const output = rpcBindingsOutputRoot(root);
  await assertSafeGeneratedOutputParents(root, output);
  return withOutputLeases([output], async () => {
    for (const entry of await readdir(dirname(output))) {
      if (entry.startsWith("rpc.stage-") || entry.startsWith("rpc.backup-") || entry.startsWith("rpc.sunder-output-transaction.json")
        || entry.startsWith("rpc.sunder-generated-output.stage-") || entry.startsWith("rpc.sunder-generated-output.backup-"))
        throw new Error("metadata.history: Interrupted or unproven generated output must be recovered under its original metadata before an ID edit.");
    }
    return operation();
  });
}

export function rpcBindingsOutputRoot(projectRoot: string): string {
  return resolve(projectRoot, ...RPC_BINDINGS_OUTPUT_PATH.split("/"));
}

/** Qualify the complete old private generated tree before a metadata edit. Caller holds the RPC output lock.
 * Old receipts are never relabeled. Only the verified generated tree is invalidated; authored baselines/locks stay outside the transaction.
 */
export async function preparePrivateMetadataInvalidationLocked(project: LoadedProject): Promise<() => Promise<void>> {
  const owned = project.ownedRpc;
  if (!owned || owned.baselineInput.baseline || owned.contracts.some(contract => !contract.imported && contract.exposure !== "package"))
    throw new Error("metadata.history: ID editing requires baseline-free private service source.");
  await owned.revalidate();
  const output = rpcBindingsOutputRoot(project.root);
  await assertSafeGeneratedOutputParents(project.root, output);
  const present = await tryLstat(output); const marker = await tryLstat(markerPath(output));
  if (!present && !marker) return async () => { await owned.revalidate(); };
  await assertReplaceableGeneratedDirectory(output);
  if (!present || !marker || (await readProjectDescriptorFile(project.root, markerPath(output), "RPC output marker")).toString() !== RPC_BINDINGS_MARKER)
    throw new Error("metadata.history: Unproven RPC output ownership; restore the original source/evidence.");
  const expected = new Map(owned.files);
  for (const name of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json", "package-export.v1.json"]) {
    if (!expected.has(name)) continue;
    const bytes = await readProjectDescriptorFile(project.root, resolve(output, name), "Prior metadata evidence", 67_108_864);
    assertBaselineFreeEvidence(bytes, name, project.config.id, name === "package-export.v1.json" ? "export" : "receipt");
    // Producer/source byte fingerprints can change between tool releases or a name-only edit.
    // All identity, ownership, descriptor, projection, import and role-use evidence must still match.
    const before = JSON.parse(bytes.toString()) as Record<string, unknown>;
    const after = JSON.parse(expected.get(name)!) as Record<string, unknown>;
    delete before.inputFingerprint; delete after.inputFingerprint;
    if (JSON.stringify(before) !== JSON.stringify(after)) throw new Error("metadata.history: Prior private generation differs from current service evidence. Build under the original ID before editing metadata.");
    expected.set(name, bytes.toString());
  }
  const plans = [...expected].map(([outputFile, source]) => ({ outputFile, source }));
  if (!await generatedSetMatches(output, plans)) throw new Error("metadata.history: Unknown or changed generated files cannot be discarded by a metadata edit. Build under the original ID first.");
  return async () => {
    await owned.revalidate(); await assertSafeGeneratedOutputParents(project.root, output);
    if (!await generatedSetMatches(output, plans)) throw new Error("metadata.conflict: Generated output changed after metadata planning.");
    const staged = await stageGeneratedDirectory(output, RPC_BINDINGS_MARKER, async directory => {
      // This is an absence marker, not a new-owner receipt. Generation must produce fresh authority evidence.
      await writeFile(resolve(directory, "owned-baseline.sha256"), "absent\n");
    });
    const outputs = [staged.output, staged.marker];
    try { await owned.revalidate(); await commitStagedOutputs(outputs); }
    finally { await cleanupStagedOutputs(outputs); }
  };
}

export async function generateRpcBindingsForLoadedProjectLocked(project: LoadedProject): Promise<void> {
  await project.ownedRpc?.revalidate();
  const outputRoot = rpcBindingsOutputRoot(project.root);
  await assertSafeGeneratedOutputParents(project.root, outputRoot);
  const plans: RpcBindingPlan[] = [];
  for (const [outputFile, source] of project.ownedRpc?.files ?? []) plans.push({ outputFile, source });
  if (!project.ownedRpc && project.contracts.length === 0) plans.push({ outputFile: MODEL_PROJECTION_PATH,
    source: projectionDocumentBytes(project.config.id, project.config.version, []).toString() });
  for (const [index, binding] of (project.config.rpcBindings ?? []).entries()) {
    const sourcePath = resolveInside(project.root, binding.path, `rpcBindings[${index}].path`);
    if (sourcePath === outputRoot || isInside(outputRoot, sourcePath) || isPortableOwnedOutputPath(binding.path)) {
      throw new Error(`rpcBindings[${index}].path '${binding.path}' must remain outside the owned RPC binding output root.`);
    }
    const contract = matchingContractSnapshot(project, sourcePath);
    const bytes = contract === undefined
      ? await readProjectDescriptorFile(project.root, sourcePath, `rpcBindings[${index}].path '${binding.path}'`)
      : contract.canonicalBytes;
    try {
      const descriptor = parseRpcContractDescriptor(bytes);
      const descriptorSha256 = contract?.descriptorSha256 ?? sha256(canonicalRpcDescriptorBytes(descriptor));
      plans.push({
        outputFile: binding.outputFile,
        source: generateTypeScriptBindings(descriptor, {
          descriptorSha256,
          abiSha256: contract?.abiSha256 ?? createHash("sha256")
            .update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8")
            .update(canonicalRpcAbiProjectionBytes(descriptor))
            .digest("hex"),
        }),
      });
    } catch (error) {
      throw new Error(`RPC binding descriptor '${binding.path}' could not be generated: ${error instanceof Error ? error.message : String(error)}`, { cause: error });
    }
  }
  plans.sort((left, right) => ordinal(left.outputFile, right.outputFile));

  const output = await tryLstat(outputRoot);
  const marker = await tryLstat(markerPath(outputRoot));
  if (plans.length === 0 && output === undefined && marker === undefined) return;

  await assertReplaceableGeneratedDirectory(outputRoot);
  if (output !== undefined && await generatedSetMatches(outputRoot, plans)) {
    await project.ownedRpc?.revalidate();
    return;
  }

  const staged = await stageGeneratedDirectory(
    outputRoot,
    RPC_BINDINGS_MARKER,
    async (stagingRoot) => {
      for (const plan of plans) {
        const path = resolve(stagingRoot, ...plan.outputFile.split("/"));
        await mkdir(dirname(path), { recursive: true });
        await writeFile(path, plan.source, "utf8");
      }
    },
  );
  const outputs = [staged.output, staged.marker];
  try {
    await project.ownedRpc?.revalidate();
    await commitStagedOutputs(outputs);
  } finally {
    await cleanupStagedOutputs(outputs);
  }
}

async function assertSafeGeneratedOutputParents(root: string, outputRoot: string): Promise<void> {
  const traversal = relative(resolve(root), dirname(outputRoot));
  let current = resolve(root);
  for (const segment of traversal.split(sep).filter((item) => item.length > 0)) {
    current = resolve(current, segment);
    const entry = await tryLstat(current);
    if (entry === undefined) return;
    if (!entry.isDirectory() || entry.isSymbolicLink()) {
      throw new Error(`RPC binding output parent '${current}' must be a regular directory without symbolic links or reparse points.`);
    }
  }
}

async function generatedSetMatches(root: string, plans: readonly RpcBindingPlan[]): Promise<boolean> {
  const expectedFiles = new Map(plans.map((plan) => [plan.outputFile, Buffer.from(plan.source, "utf8")]));
  const expectedDirectories = new Set<string>();
  for (const plan of plans) {
    const segments = plan.outputFile.split("/");
    for (let index = 1; index < segments.length; index++) {
      expectedDirectories.add(segments.slice(0, index).join("/"));
    }
  }

  let fileCount = 0;
  let directoryCount = 0;
  const visit = async (directory: string, logicalDirectory: string): Promise<boolean> => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const logicalPath = logicalDirectory.length === 0 ? entry.name : `${logicalDirectory}/${entry.name}`;
      const path = resolve(directory, entry.name);
      if (entry.isDirectory() && !entry.isSymbolicLink()) {
        if (!expectedDirectories.has(logicalPath)) return false;
        directoryCount++;
        if (!await visit(path, logicalPath)) return false;
      } else if (entry.isFile() && !entry.isSymbolicLink()) {
        const expected = expectedFiles.get(logicalPath);
        if (expected === undefined || !expected.equals(await readFile(path))) return false;
        fileCount++;
      } else {
        return false;
      }
    }
    return true;
  };
  return await visit(root, "")
    && fileCount === expectedFiles.size
    && directoryCount === expectedDirectories.size;
}

async function tryLstat(path: string): Promise<Stats | undefined> {
  try {
    return await lstat(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

function isInside(root: string, path: string): boolean {
  const traversal = relative(resolve(root), resolve(path));
  return traversal.length > 0 && traversal !== ".." && !traversal.startsWith(`..${sep}`) && !isAbsolute(traversal);
}

function matchingContractSnapshot(project: LoadedProject, sourcePath: string): LoadedContract | undefined {
  const identity = pathIdentity(sourcePath);
  return project.contracts.find((contract) => pathIdentity(contract.sourcePath) === identity);
}

function pathIdentity(path: string): string {
  const normalized = resolve(path);
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function isPortableOwnedOutputPath(path: string): boolean {
  const foldedPath = asciiCaseFold(path);
  const foldedRoot = asciiCaseFold(RPC_BINDINGS_OUTPUT_PATH);
  return foldedPath === foldedRoot || foldedPath.startsWith(`${foldedRoot}/`);
}

function asciiCaseFold(value: string): string {
  return value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 0x20));
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
}
