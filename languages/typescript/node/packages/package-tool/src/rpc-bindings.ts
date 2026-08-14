import { createHash } from "node:crypto";
import type { Stats } from "node:fs";
import { lstat, mkdir, readFile, readdir, rmdir, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import {
  SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  generateTypeScriptBindings,
  parseRpcContractDescriptor,
} from "@sunder/sdk";
import { readProjectDescriptorFile } from "./descriptor-files";
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
} from "./output";

const RPC_BINDINGS_MARKER = "Sunder TypeScript RPC bindings generated output v1\n";
const RPC_BINDINGS_OUTPUT_PATH = ".sunder/generated/rpc";

interface RpcBindingPlan {
  readonly outputFile: string;
  readonly source: string;
}

export async function generateRpcBindings(projectPath: string): Promise<void> {
  const root = resolve(projectPath);
  await withRpcBindingsOutputLock(root, async () => {
    await generateRpcBindingsForLoadedProjectLocked(await loadProject(root));
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

export function rpcBindingsOutputRoot(projectRoot: string): string {
  return resolve(projectRoot, ...RPC_BINDINGS_OUTPUT_PATH.split("/"));
}

export async function generateRpcBindingsForLoadedProjectLocked(project: LoadedProject): Promise<void> {
  const outputRoot = rpcBindingsOutputRoot(project.root);
  await assertSafeGeneratedOutputParents(project.root, outputRoot);
  const plans: RpcBindingPlan[] = [];
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
      const generatorOptions = descriptor.descriptorVersion === 2
        ? {
          descriptorSha256,
          abiSha256: contract?.abiSha256 ?? createHash("sha256")
            .update(SUNDER_RPC_V2_ABI_DOMAIN_SEPARATOR, "utf8")
            .update(canonicalRpcAbiProjectionBytes(descriptor))
            .digest("hex"),
        }
        : { descriptorSha256 };
      plans.push({
        outputFile: binding.outputFile,
        source: generateTypeScriptBindings(descriptor, generatorOptions),
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
  if (output !== undefined && await generatedSetMatches(outputRoot, plans)) return;

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
