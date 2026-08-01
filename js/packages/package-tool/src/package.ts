import { createHash, randomUUID } from "node:crypto";
import { createReadStream, watch as watchFiles } from "node:fs";
import {
  access,
  copyFile,
  lstat,
  mkdir,
  realpath,
  readdir,
  readFile,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import { basename, dirname, extname, isAbsolute, relative, resolve, sep } from "node:path";
import { canonicalizeDescriptor, type JsonValue, type RpcContractDescriptor } from "@sunder/sdk";
import type { BuildOptions } from "esbuild";
import { build as buildEsbuild, context } from "esbuild";
import {
  asJsonValue,
  currentRid,
  loadProject,
  resolveInside,
  SUPPORTED_RIDS,
  type AppConfig,
  type AppViewConfig,
  type LoadedContract,
  type LoadedProject,
  type SunderNodePackageConfig,
  type SunderRid,
} from "./model";

const MANIFEST_PATH = "manifest/sunder-package.json";
const INDEX_PATH = "manifest/content-index.json";
const MARKER_SUFFIX = ".sunder-generated-output";
const DEFAULT_MAX_EXECUTABLE_BYTES = 160 * 1024 * 1024;
const DEFAULT_MAX_PACKAGE_BYTES = 1024 * 1024 * 1024;

export interface PreparedProject extends LoadedProject {
  readonly contracts: readonly LoadedContract[];
}

export interface DevBuildOptions {
  readonly projectPath: string;
  readonly watch: boolean;
}

export interface LeafBuildInput {
  readonly project: PreparedProject;
  readonly rid: SunderRid;
  readonly executablePath: string;
}

export async function prepareProject(projectPath: string): Promise<PreparedProject> {
  const project = await loadProject(projectPath);
  const contracts: LoadedContract[] = [];
  const identities = new Set<string>();
  for (const configured of project.config.contracts) {
    const sourcePath = resolveInside(project.root, configured.path, "contract path");
    const descriptor = JSON.parse(await readFile(sourcePath, "utf8")) as RpcContractDescriptor;
    const canonical = canonicalizeDescriptor(descriptor as unknown as JsonValue);
    if (descriptor.descriptorVersion !== 1 || typeof descriptor.contractId !== "string" || typeof descriptor.version !== "string") {
      throw new Error(`Contract '${configured.path}' is not a Sunder RPC descriptorVersion 1 descriptor.`);
    }
    const identity = `${descriptor.contractId}\0${descriptor.version}`;
    if (!identities.add(identity)) throw new Error(`Contract '${descriptor.contractId}' version '${descriptor.version}' is configured more than once.`);
    const descriptorPath = normalizeLogicalPath(configured.descriptorPath ?? `contracts/${basename(configured.path)}`);
    contracts.push({
      sourcePath,
      descriptorPath,
      descriptor,
      canonical,
      sha256: sha256(Buffer.from(canonical, "utf8")),
    });
  }
  for (const provider of project.config.providers) {
    if (!contracts.some((contract) => contract.descriptor.contractId === provider.contractId && contract.descriptor.version === provider.contractVersion)) {
      throw new Error(`Provider '${provider.providerId}' has no exact configured local contract descriptor.`);
    }
  }
  for (const use of project.config.usesContracts ?? []) {
    if (!contracts.some((contract) => contract.descriptor.contractId === use.contractId)) {
      throw new Error(`Contract use '${use.contractId}' has no configured local contract descriptor.`);
    }
  }
  return { ...project, contracts };
}

export async function buildDevPackage(options: DevBuildOptions): Promise<void> {
  const project = await prepareProject(options.projectPath);
  const rid = currentRid();
  const workRoot = resolve(project.outputRoot, ".sunder-dev-work");
  await mkdir(workRoot, { recursive: true });
  const bundlePath = resolve(workRoot, "worker.cjs");
  let webRoot = project.config.app === undefined
    ? undefined
    : await buildWebAssets(project, resolve(workRoot, "web"));
  let emitChain = Promise.resolve();
  const emit = (rebuildWeb = false): Promise<void> => {
    const operation = emitChain.then(async () => {
      if (rebuildWeb) webRoot = await buildWebAssets(project, resolve(workRoot, "web"));
      await emitDevTree(project, rid, bundlePath, webRoot);
      process.stdout.write(`Sunder dev package: ${resolve(project.outputRoot, "sunder-dev")}\n`);
    });
    emitChain = operation.catch((error: unknown) => {
      process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    });
    return operation;
  };
  const buildOptions: BuildOptions = {
    entryPoints: [resolveInside(project.root, project.config.entry, "entry")],
    outfile: bundlePath,
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node24",
    sourcemap: true,
    logLevel: "info",
    alias: { "@sunder/sdk": require.resolve("@sunder/sdk") },
    plugins: options.watch ? [{
      name: "sunder-dev-output",
      setup(pluginBuild) {
        pluginBuild.onEnd(async (result) => {
          if (result.errors.length === 0) await emit();
        });
      },
    }] : [],
  };
  if (!options.watch) {
    await buildEsbuild(buildOptions);
    await emit();
    return;
  }
  const buildContext = await context(buildOptions);
  await buildContext.watch();
  const appRoot = project.config.app === undefined ? undefined : resolveAppRoot(project);
  let rebuildTimer: NodeJS.Timeout | undefined;
  const appWatcher = appRoot === undefined ? undefined : watchFiles(
    appRoot,
    { recursive: true },
    (_event, fileName) => {
      if (fileName !== null) {
        const relativeName = fileName.toString().replaceAll("\\", "/");
        if (relativeName.split("/").some((segment) => segment.startsWith(".sunder-vite-"))
          || isInside(project.outputRoot, resolve(appRoot, relativeName))) return;
      }
      if (rebuildTimer !== undefined) clearTimeout(rebuildTimer);
      rebuildTimer = setTimeout(() => {
        void emit(true).catch(() => undefined);
      }, 100);
    },
  );
  process.stdout.write("Watching package sources. Sunder will drain and restart the exact package activation after each canonical dev output change.\n");
  await new Promise<void>((resolvePromise) => {
    let stopping = false;
    const stop = (): void => {
      if (stopping) return;
      stopping = true;
      if (rebuildTimer !== undefined) clearTimeout(rebuildTimer);
      appWatcher?.close();
      void buildContext.dispose().then(() => emitChain).finally(resolvePromise);
    };
    process.once("SIGINT", stop);
    process.once("SIGTERM", stop);
  });
}

export async function bundleProduction(project: PreparedProject, rid: SunderRid, outputPath: string): Promise<void> {
  await mkdir(dirname(outputPath), { recursive: true });
  await buildEsbuild({
    entryPoints: [resolveInside(project.root, project.config.entry, "entry")],
    outfile: outputPath,
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node24.18",
    sourcemap: false,
    minify: true,
    legalComments: "none",
    logLevel: "info",
    alias: { "@sunder/sdk": require.resolve("@sunder/sdk") },
  });
}

export async function buildWebTarget(projectPath: string, configuredRid?: SunderRid): Promise<string | null> {
  const project = await prepareProject(projectPath);
  if (project.config.app === undefined) return null;
  const rid = configuredRid ?? currentRid();
  const webRoot = await buildWebAssets(project, resolve(project.outputRoot, ".sunder-web-work", rid));
  return writeWebTargetLeaf(project, rid, webRoot);
}

export async function writeTargetLeaf(input: LeafBuildInput): Promise<string> {
  const leafRoot = resolve(input.project.outputRoot, "targets", input.rid);
  await replaceGeneratedDirectory(leafRoot);
  const executableName = `${safeFileName(input.project.config.id)}${input.rid.startsWith("win-") ? ".exe" : ""}`;
  const logicalEntryPoint = `bin/${executableName}`;
  const targetExecutable = resolve(leafRoot, "payload", "runtime", input.rid, "bin", executableName);
  await mkdir(dirname(targetExecutable), { recursive: true });
  await copyFile(input.executablePath, targetExecutable);
  await copyContracts(input.project, leafRoot);
  const manifest = createManifest(input.project, [{
    role: "runtime",
    rid: input.rid,
    kind: "process",
    entryPoint: logicalEntryPoint,
    targetFramework: "node24",
    sdkVersion: "1.1.0",
    requiredHostCapabilities: ["rpc.v1", "sdk-baseline-1-1.v1"],
  }]);
  await writeCanonicalPackageMetadata(leafRoot, manifest);
  await writeFile(markerPath(leafRoot), "Sunder Node target leaf generated output v1\n", "utf8");
  return leafRoot;
}

async function writeWebTargetLeaf(
  project: PreparedProject,
  rid: SunderRid,
  webRoot: string,
): Promise<string> {
  const app = project.config.app;
  if (app === undefined) throw new Error("The package does not configure an App web target.");
  await validateWebOutput(app, webRoot);
  const leafRoot = resolve(project.outputRoot, "targets", `app-${rid}`);
  await replaceGeneratedDirectory(leafRoot);
  await copyDirectory(webRoot, resolve(leafRoot, "payload", "app", rid));
  await copyContracts(project, leafRoot);
  await writeCanonicalPackageMetadata(leafRoot, createManifest(project, [webTarget(app, rid)]));
  await writeFile(markerPath(leafRoot), "Sunder web target leaf generated output v1\n", "utf8");
  return leafRoot;
}

export async function aggregateAndPack(
  projectPath: string,
  configuredLeafPaths: readonly string[] = [],
): Promise<{ packageRoot: string; archivePath: string; bytes: number }> {
  const project = await prepareProject(projectPath);
  const leafRoots = configuredLeafPaths.length === 0
    ? await discoverLeaves(resolve(project.outputRoot, "targets"))
    : (await Promise.all(configuredLeafPaths.map(discoverLeaves))).flat();
  if (leafRoots.length === 0) throw new Error("No canonical package target leaves were found.");
  const leaves = await Promise.all(leafRoots.map(readLeaf));
  const targetKeys = new Set<string>();
  for (const leaf of leaves) {
    for (const target of leaf.manifest.targets) {
      const key = `${target.role}/${target.rid}`;
      if (targetKeys.has(key)) throw new Error(`Aggregate inputs declare exact target '${key}' more than once.`);
      targetKeys.add(key);
    }
  }
  const fingerprint = packageMetadataFingerprint(leaves[0]?.manifest);
  if (leaves.some((leaf) => packageMetadataFingerprint(leaf.manifest) !== fingerprint)) {
    throw new Error("Aggregate target leaves disagree on package-wide manifest metadata.");
  }
  if (fingerprint !== packageMetadataFingerprint(createManifest(project, []))) {
    throw new Error("Aggregate target leaves do not match the configured package identity and metadata.");
  }
  const packageRoot = resolve(project.outputRoot, "package");
  await replaceGeneratedDirectory(packageRoot);
  const targets = leaves.flatMap((leaf) => leaf.manifest.targets).sort(compareTargets);
  const logicalPaths = new Set(leaves.flatMap((leaf) => [...leaf.files.keys()]));
  for (const logicalPath of [...logicalPaths].sort(ordinal)) {
    const occurrences = leaves.filter((leaf) => leaf.files.has(logicalPath));
    if (occurrences.length === leaves.length && identical(occurrences, logicalPath)) {
      await copyLeafFile(occurrences[0]!, logicalPath, resolve(packageRoot, "payload", "shared", ...logicalPath.split("/")));
      continue;
    }
    for (const leaf of occurrences) {
      const target = single(leaf.manifest.targets, "Target leaf must contain one exact target for aggregation.");
      await copyLeafFile(leaf, logicalPath, resolve(packageRoot, "payload", target.role, target.rid, ...logicalPath.split("/")));
    }
  }
  await writeCanonicalPackageMetadata(packageRoot, createManifest(project, targets));
  await writeFile(markerPath(packageRoot), "Sunder package aggregate generated output v1\n", "utf8");
  const archivePath = resolve(project.outputRoot, `${project.config.id}.${project.config.version}.sunderpkg`);
  await writeDeterministicZip(packageRoot, archivePath);
  const bytes = (await stat(archivePath)).size;
  const maximum = project.config.maximumPackageBytes ?? DEFAULT_MAX_PACKAGE_BYTES;
  if (bytes > maximum) {
    await rm(archivePath, { force: true });
    throw new Error(`Package archive is ${bytes} bytes and exceeds maximumPackageBytes ${maximum}.`);
  }
  process.stdout.write(`Sunder package size: ${bytes} bytes (${targets.length} exact RID target${targets.length === 1 ? "" : "s"})\n`);
  return { packageRoot, archivePath, bytes };
}

export function maximumExecutableBytes(config: SunderNodePackageConfig): number {
  return config.maximumExecutableBytes ?? DEFAULT_MAX_EXECUTABLE_BYTES;
}

async function emitDevTree(
  project: PreparedProject,
  rid: SunderRid,
  bundlePath: string,
  webRoot?: string,
): Promise<void> {
  const root = resolve(project.outputRoot, "sunder-dev");
  await replaceGeneratedDirectory(root);
  const logicalEntryPoint = "worker.cjs";
  const destination = resolve(root, "payload", "runtime", rid, logicalEntryPoint);
  await mkdir(dirname(destination), { recursive: true });
  await copyFile(bundlePath, destination);
  const sourceMap = `${bundlePath}.map`;
  if (await exists(sourceMap)) await copyFile(sourceMap, `${destination}.map`);
  if (webRoot !== undefined) {
    await copyDirectory(webRoot, resolve(root, "payload", "app", rid));
  }
  await copyContracts(project, root);
  const targets: TargetManifest[] = [{
    role: "runtime",
    rid,
    kind: "process",
    entryPoint: logicalEntryPoint,
    targetFramework: `node${process.versions.node.split(".")[0]}`,
    sdkVersion: "1.1.0",
    requiredHostCapabilities: ["rpc.v1", "sdk-baseline-1-1.v1"],
  }];
  if (project.config.app !== undefined) targets.push(webTarget(project.config.app, rid));
  const manifest = createManifest(project, targets);
  const metadata = await writeCanonicalPackageMetadata(root, manifest);
  await writeFile(markerPath(root), "Sunder Node dev generated output v1\n", "utf8");
  await writeJson(`${root}.sunder-node-dev.json`, {
    schemaVersion: 1,
    kind: "node",
    packageId: project.config.id,
    packageVersion: project.config.version,
    rid,
    entryPoint: logicalEntryPoint,
    nodePath: resolve(process.execPath),
    nodeVersion: process.version,
    contentIdentity: metadata.contentIdentity,
  });
}

function createManifest(project: PreparedProject, targets: readonly TargetManifest[]): PackageManifest {
  const contracts = project.contracts.map((contract) => ({
    contractId: contract.descriptor.contractId,
    version: contract.descriptor.version,
    descriptorPath: contract.descriptorPath,
    sha256: contract.sha256,
  })).sort((left, right) => ordinal(`${left.contractId}\0${left.version}`, `${right.contractId}\0${right.version}`));
  const targetRoles = new Set(targets.map((target) => target.role));
  const providers = project.config.providers.filter(() => targetRoles.has("runtime")).map((provider) => {
    const contract = project.contracts.find((candidate) => candidate.descriptor.contractId === provider.contractId && candidate.descriptor.version === provider.contractVersion);
    if (contract === undefined) throw new Error(`Provider '${provider.providerId}' contract was not loaded.`);
    return {
      providerId: provider.providerId,
      contractId: provider.contractId,
      contractVersion: provider.contractVersion,
      contractSha256: contract.sha256,
      role: "runtime",
    };
  }).sort((left, right) => ordinal(left.providerId, right.providerId));
  return {
    archiveFormatVersion: 1,
    manifestVersion: 1,
    id: project.config.id,
    name: project.config.name,
    ...(project.config.summary === undefined ? {} : { summary: project.config.summary }),
    version: project.config.version,
    dependsOn: [...(project.config.dependencies ?? [])],
    targets: [...targets].sort(compareTargets),
    contractBundles: contracts,
    usesContracts: [...(project.config.usesContracts ?? [])].sort((left, right) => ordinal(left.contractId, right.contractId)),
    provides: providers,
  };
}

async function copyContracts(project: PreparedProject, packageRoot: string): Promise<void> {
  for (const contract of project.contracts) {
    const path = resolve(packageRoot, "payload", "shared", ...contract.descriptorPath.split("/"));
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, `${contract.canonical}\n`, "utf8");
  }
}

async function buildWebAssets(project: PreparedProject, outputRoot: string): Promise<string> {
  const app = project.config.app;
  if (app === undefined) throw new Error("The package does not configure an App web target.");
  if (!isInside(project.outputRoot, outputRoot) && resolve(project.outputRoot) !== resolve(outputRoot)) {
    throw new Error("The generated Vite output must remain inside outputDirectory.");
  }
  const appRoot = await realpath(resolveAppRoot(project));
  const stagingRoot = resolve(appRoot, `.sunder-vite-${process.pid}-${randomUUID()}`);
  const { build: buildVite } = await import("vite");
  try {
    await buildVite({
      root: appRoot,
      base: "./",
      build: {
        outDir: stagingRoot,
        emptyOutDir: true,
      },
      logLevel: "info",
    });
    await validateWebOutput(app, stagingRoot);
    await rm(outputRoot, { recursive: true, force: true });
    await copyDirectory(stagingRoot, outputRoot);
    return outputRoot;
  } finally {
    await rm(stagingRoot, { recursive: true, force: true });
  }
}

function resolveAppRoot(project: PreparedProject): string {
  const app = project.config.app;
  if (app === undefined) throw new Error("The package does not configure an App web target.");
  const root = resolve(project.root, app.root);
  if (root !== project.root && !isInside(project.root, root)) {
    throw new Error("app.root must resolve inside the package project.");
  }
  return root;
}

async function validateWebOutput(app: AppConfig, root: string): Promise<void> {
  const required = [app.entryPoint, ...app.views.flatMap((view) => view.icon === undefined ? [] : [view.icon])];
  for (const logicalPath of required) {
    const path = resolve(root, ...logicalPath.split("/"));
    if (!isInside(root, path)) throw new Error(`Vite output path '${logicalPath}' escapes its root.`);
    try {
      const entry = await lstat(path);
      if (!entry.isFile() || entry.isSymbolicLink()) throw new Error();
    } catch {
      throw new Error(`Vite output is missing regular file '${logicalPath}'.`);
    }
  }
}

async function copyDirectory(source: string, destination: string): Promise<void> {
  await mkdir(destination, { recursive: true });
  for (const entry of await readdir(source, { withFileTypes: true })) {
    const sourcePath = resolve(source, entry.name);
    const destinationPath = resolve(destination, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Vite output contains symbolic link '${sourcePath}'.`);
    if (entry.isDirectory()) await copyDirectory(sourcePath, destinationPath);
    else if (entry.isFile()) await copyFile(sourcePath, destinationPath);
    else throw new Error(`Vite output contains unsupported file type '${sourcePath}'.`);
  }
}

function webTarget(app: AppConfig, rid: SunderRid): TargetManifest {
  return {
    role: "app",
    rid,
    kind: "web",
    entryPoint: app.entryPoint,
    targetFramework: "web",
    sdkVersion: "1.1.0",
    requiredHostCapabilities: ["rpc.v1", "sdk-baseline-1-1.v1", "views.v1"],
    views: app.views.map((view) => ({ ...view })),
  };
}

async function writeCanonicalPackageMetadata(root: string, manifest: PackageManifest): Promise<{ contentIdentity: string }> {
  const manifestPath = resolve(root, ...MANIFEST_PATH.split("/"));
  await mkdir(dirname(manifestPath), { recursive: true });
  await writeJson(manifestPath, manifest);
  const entries = await enumerateCanonicalFiles(root);
  const files: ContentIndexEntry[] = [];
  for (const entry of entries.filter((entry) => entry !== INDEX_PATH)) {
    const path = resolve(root, ...entry.split("/"));
    files.push({ path: entry, sha256: await hashFile(path), size: (await stat(path)).size });
  }
  files.sort((left, right) => ordinal(left.path, right.path));
  const indexPath = resolve(root, ...INDEX_PATH.split("/"));
  await writeJson(indexPath, { schemaVersion: 1, files });
  const contentIdentity = sha256(Buffer.concat([
    Buffer.from(await hashFile(manifestPath), "hex"),
    Buffer.from(await hashFile(indexPath), "hex"),
  ]));
  return { contentIdentity };
}

async function enumerateCanonicalFiles(root: string): Promise<string[]> {
  const output: string[] = [];
  const visit = async (directory: string): Promise<void> => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      if (entry.isSymbolicLink()) throw new Error(`Canonical package output contains symbolic link '${path}'.`);
      if (entry.isDirectory()) await visit(path);
      else if (entry.isFile()) output.push(relative(root, path).split(sep).join("/"));
      else throw new Error(`Canonical package output contains unsupported file type '${path}'.`);
    }
  };
  await visit(root);
  return output.sort(ordinal);
}

async function readLeaf(root: string): Promise<Leaf> {
  const rootEntry = await lstat(root);
  if (!rootEntry.isDirectory() || rootEntry.isSymbolicLink()) throw new Error(`Target leaf '${root}' must be a regular directory.`);
  const manifestPath = resolve(root, ...MANIFEST_PATH.split("/"));
  const indexPath = resolve(root, ...INDEX_PATH.split("/"));
  await requireRegularFile(manifestPath, "target leaf manifest");
  await requireRegularFile(indexPath, "target leaf content index");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as PackageManifest;
  const index = JSON.parse(await readFile(indexPath, "utf8")) as ContentIndex;
  if (manifest.archiveFormatVersion !== 1 || manifest.manifestVersion !== 1 || !Array.isArray(manifest.targets) || manifest.targets.length !== 1 || index.schemaVersion !== 1 || !Array.isArray(index.files)) {
    throw new Error(`Target leaf '${root}' is not a canonical Sunder package leaf.`);
  }
  const target = manifest.targets[0]!;
  validateTarget(target, root);
  const files = new Map<string, LeafFile>();
  const indexedPaths = new Set<string>();
  for (const item of index.files) {
    const archivePath = canonicalArchivePath(item.path);
    if (indexedPaths.has(archivePath)) throw new Error(`Target leaf '${root}' indexes '${archivePath}' more than once.`);
    indexedPaths.add(archivePath);
    if (!/^[0-9a-f]{64}$/u.test(item.sha256) || !Number.isSafeInteger(item.size) || item.size < 0) {
      throw new Error(`Target leaf '${root}' has invalid content metadata for '${archivePath}'.`);
    }
    const fullPath = resolve(root, ...archivePath.split("/"));
    await requireRegularFile(fullPath, `target leaf file '${archivePath}'`);
    if (await hashFile(fullPath) !== item.sha256 || (await stat(fullPath)).size !== item.size) throw new Error(`Target leaf file '${item.path}' failed hash validation.`);
    if (archivePath === MANIFEST_PATH) continue;
    const rawLogical = physicalToLogical(archivePath, target);
    if (rawLogical === null) continue;
    const logical = normalizeLogicalPath(rawLogical);
    if (logical !== rawLogical) throw new Error(`Target leaf '${root}' contains non-canonical logical path '${rawLogical}'.`);
    if (files.has(logical)) throw new Error(`Target leaf '${root}' has a logical payload collision at '${logical}'.`);
    files.set(logical, { fullPath, sha256: item.sha256, size: item.size });
  }
  const actualPaths = (await enumerateCanonicalFiles(root)).filter((path) => path !== INDEX_PATH);
  if (actualPaths.length !== indexedPaths.size || actualPaths.some((path) => !indexedPaths.has(path))) {
    throw new Error(`Target leaf '${root}' content index does not exactly cover its canonical files.`);
  }
  if (!files.has(normalizeLogicalPath(target.entryPoint))) {
    throw new Error(`Target leaf '${root}' does not contain its declared entry point '${target.entryPoint}'.`);
  }
  return { root, manifest, files };
}

function validateTarget(target: TargetManifest, root: string): void {
  const common = SUPPORTED_RIDS.includes(target.rid as SunderRid)
    && target.sdkVersion === "1.1.0"
    && normalizeLogicalPath(target.entryPoint) === target.entryPoint;
  const processTarget = target.role === "runtime"
    && target.kind === "process"
    && target.targetFramework === "node24"
    && target.views === undefined;
  const webTarget = target.role === "app"
    && target.kind === "web"
    && target.targetFramework === "web"
    && Array.isArray(target.views)
    && target.views.length > 0;
  if (!common || !processTarget && !webTarget) {
    throw new Error(`Target leaf '${root}' does not declare one canonical supported process or web target.`);
  }
}

function canonicalArchivePath(value: unknown): string {
  if (typeof value !== "string"
    || value.length === 0
    || value.length > 240
    || value.includes("\\")
    || value.startsWith("/")
    || value.endsWith("/")) {
    throw new Error("Target leaf content index contains an invalid canonical archive path.");
  }
  const segments = value.split("/");
  if (segments.length > 32 || segments.some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    throw new Error(`Target leaf content index path '${value}' is not canonical.`);
  }
  if (!value.startsWith("manifest/") && !value.startsWith("payload/")) {
    throw new Error(`Target leaf content index path '${value}' is outside canonical package roots.`);
  }
  return value;
}

async function requireRegularFile(path: string, label: string): Promise<void> {
  const entry = await lstat(path);
  if (!entry.isFile() || entry.isSymbolicLink()) throw new Error(`${label} must be a regular file.`);
}

function physicalToLogical(path: string, target: TargetManifest): string | null {
  const shared = "payload/shared/";
  const role = `payload/${target.role}/shared/`;
  const exact = `payload/${target.role}/${target.rid}/`;
  if (path.startsWith(shared)) return path.slice(shared.length);
  if (path.startsWith(role)) return path.slice(role.length);
  if (path.startsWith(exact)) return path.slice(exact.length);
  return null;
}

async function copyLeafFile(leaf: Leaf, logicalPath: string, destination: string): Promise<void> {
  const file = leaf.files.get(logicalPath);
  if (file === undefined) throw new Error(`Leaf '${leaf.root}' is missing '${logicalPath}'.`);
  await mkdir(dirname(destination), { recursive: true });
  await copyFile(file.fullPath, destination);
}

function identical(leaves: readonly Leaf[], logicalPath: string): boolean {
  const expected = leaves[0]?.files.get(logicalPath);
  return expected !== undefined && leaves.every((leaf) => {
    const file = leaf.files.get(logicalPath);
    return file?.size === expected.size && file.sha256 === expected.sha256;
  });
}

async function discoverLeaves(root: string): Promise<string[]> {
  if (!await exists(root)) return [];
  if (await exists(resolve(root, ...MANIFEST_PATH.split("/"))) && await exists(resolve(root, ...INDEX_PATH.split("/")))) return [root];
  const output: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    if (entry.isDirectory()) output.push(...await discoverLeaves(resolve(root, entry.name)));
  }
  return output;
}

async function replaceGeneratedDirectory(path: string): Promise<void> {
  if (await exists(path)) {
    if (!await exists(markerPath(path))) throw new Error(`Refusing to replace unmarked generated output '${path}'.`);
    await rm(path, { recursive: true, force: true });
  }
  await rm(markerPath(path), { force: true });
  await mkdir(path, { recursive: true });
}

function markerPath(path: string): string {
  return `${path}${MARKER_SUFFIX}`;
}

async function writeJson(path: string, value: unknown): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

async function hashFile(path: string): Promise<string> {
  const hash = createHash("sha256");
  const stream = createReadStream(path);
  for await (const chunk of stream) hash.update(chunk as Buffer);
  return hash.digest("hex");
}

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
}

function normalizeLogicalPath(path: string): string {
  const normalized = path.replaceAll("\\", "/");
  const segments = normalized.split("/");
  if (normalized.length === 0 || normalized.length > 200 || normalized.startsWith("/") || normalized.endsWith("/") || segments.length > 24 || segments.some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    throw new Error(`Logical package path '${path}' is invalid.`);
  }
  return normalized;
}

function isInside(root: string, path: string): boolean {
  const traversal = relative(resolve(root), resolve(path));
  return traversal.length > 0 && !traversal.startsWith("..") && !isAbsolute(traversal);
}

function safeFileName(packageId: string): string {
  return packageId.replace(/[^a-z0-9.-]/gu, "-");
}

function packageMetadataFingerprint(manifest: PackageManifest | undefined): string {
  if (manifest === undefined) throw new Error("Aggregate input manifest is missing.");
  return canonicalizeDescriptor(asJsonValue({ ...manifest, targets: [], provides: [] }));
}

function compareTargets(left: TargetManifest, right: TargetManifest): number {
  const order = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
  return ordinal(left.role, right.role) || order.indexOf(left.rid) - order.indexOf(right.rid);
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function single<T>(items: readonly T[], message: string): T {
  if (items.length !== 1) throw new Error(message);
  return items[0]!;
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function writeDeterministicZip(root: string, outputPath: string): Promise<void> {
  const files = await enumerateCanonicalFiles(root);
  const localParts: Buffer[] = [];
  const centralParts: Buffer[] = [];
  let offset = 0;
  for (const path of files) {
    const data = await readFile(resolve(root, ...path.split("/")));
    const name = Buffer.from(path, "utf8");
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(0, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0x0021, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    localParts.push(local, name, data);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x031e, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(0, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0x0021, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE((0o100644 * 0x10000) >>> 0, 38);
    central.writeUInt32LE(offset, 42);
    centralParts.push(central, name);
    offset += local.length + name.length + data.length;
  }
  const centralSize = centralParts.reduce((sum, part) => sum + part.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(centralSize, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(0, 20);
  const archive = Buffer.concat([...localParts, ...centralParts, end]);
  await writeFile(outputPath, archive);
}

function crc32(data: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) === 0 ? 0 : 0xedb88320);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

interface TargetManifest {
  readonly role: string;
  readonly rid: string;
  readonly kind: string;
  readonly entryPoint: string;
  readonly targetFramework: string;
  readonly sdkVersion: string;
  readonly requiredHostCapabilities: readonly string[];
  readonly views?: readonly AppViewConfig[];
}

interface PackageManifest {
  readonly archiveFormatVersion: number;
  readonly manifestVersion: number;
  readonly id: string;
  readonly name: string;
  readonly summary?: string;
  readonly version: string;
  readonly dependsOn: readonly unknown[];
  readonly targets: readonly TargetManifest[];
  readonly contractBundles: readonly unknown[];
  readonly usesContracts: readonly unknown[];
  readonly provides: readonly unknown[];
}

interface ContentIndexEntry {
  readonly path: string;
  readonly sha256: string;
  readonly size: number;
}

interface ContentIndex {
  readonly schemaVersion: number;
  readonly files: readonly ContentIndexEntry[];
}

interface LeafFile {
  readonly fullPath: string;
  readonly sha256: string;
  readonly size: number;
}

interface Leaf {
  readonly root: string;
  readonly manifest: PackageManifest;
  readonly files: ReadonlyMap<string, LeafFile>;
}
