import { createHash, randomUUID } from "node:crypto";
import { createReadStream, watch as watchFiles, type FSWatcher } from "node:fs";
import {
  access,
  copyFile,
  lstat,
  mkdir,
  open,
  realpath,
  readdir,
  readFile,
  rename,
  rm,
  stat,
  writeFile,
  type FileHandle,
} from "node:fs/promises";
import { basename, dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { Transform, Writable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { createDeflateRaw } from "node:zlib";
import {
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  canonicalizeDescriptor,
  isArchiveRelativePath,
  isPackageId,
  isSemanticVersion,
  parseRpcContractDescriptor,
} from "@sunder/sdk";
import type { BuildOptions, Metafile } from "esbuild";
import { build as buildEsbuild, context, type BuildContext } from "esbuild";
import type { Plugin as VitePlugin } from "vite" with { "resolution-mode": "import" };
import { packageToolCompatibility } from "./compatibility";
import type { RegistryOriginOptions } from "./rpc-contract-lock";
import { validateDependencyVersionRange } from "./dependency-range";
import { readProjectDescriptorFile } from "./descriptor-files";
import {
  detectRuntimeBinaryFingerprint,
  inspectNativeBinary,
} from "./native-binary";
import {
  asJsonValue,
  currentRid,
  loadProject,
  MAXIMUM_COMPRESSED_PACKAGE_BYTES,
  resolveInside,
  SUPPORTED_RIDS,
  type AppConfig,
  type AppViewConfig,
  type ContractExposure,
  type LoadedContract,
  type LoadedProject,
  type PackageRole,
  type ProviderAudience,
  type ResolvedNodePackageConfig as SunderNodePackageConfig,
  type SunderRid,
} from "./model";
import { renderBundledDependencyNotice } from "./notices";
import { ownedRpcPlugins, revalidateOwnedRpc } from "./rpc-authoring-plugin";
import { validateBaselineSource } from "./rpc-baseline";
import { authority, importProjection, importedContractsForRoles, importedDependenciesForRoles } from "./rpc-package-imports";
import { withBaselineLease } from "./rpc-baseline-store";
import { workerResolutionOptions } from "./rpc-runtime-inputs";
import { emptyModelProjection, MODEL_PROJECTION_ARCHIVE_PATH, MODEL_PROJECTION_PATH, projectionDocumentBytes, validateModelProjectionSet } from "./rpc-model-projection";
import {
  generateRpcBindingsForLoadedProjectLocked,
  withRpcBindingsOutputLock,
} from "./rpc-bindings";
import {
  assertReplaceableGeneratedDirectory,
  assertReplaceableGeneratedFile,
  cleanupStagedOutputs,
  commitStagedOutputs,
  stageGeneratedDirectory,
  targetLeafDiscoveryKey,
  withOutputLocks,
  type StagedOutput,
} from "./output";

const MANIFEST_PATH = "manifest/sunder-package.json";
const INDEX_PATH = "manifest/content-index.json";
const ARCHIVE_FORMAT_VERSION = 1;
const PACKAGE_MANIFEST_VERSION = 1;
const CONTENT_INDEX_SCHEMA_VERSION = 1;
const DEFAULT_MAX_PACKAGE_BYTES = MAXIMUM_COMPRESSED_PACKAGE_BYTES;
const MAXIMUM_ARCHIVE_ENTRIES = 4096;
const MAXIMUM_ENTRY_BYTES = 256 * 1024 * 1024;
const MAXIMUM_TOTAL_BYTES = 1024 * 1024 * 1024;
const MAXIMUM_METADATA_BYTES = 1024 * 1024;
const MAXIMUM_COMPRESSION_RATIO = 200;
const WORKER_NOTICE_LOGICAL_PATH = "THIRD-PARTY-NOTICES/Bundled-Worker-Dependencies.txt";
const WEB_NOTICE_LOGICAL_PATH = "THIRD-PARTY-NOTICES/Bundled-Web-Dependencies.txt";
const NODE_EXECUTABLE_HEADER_EVIDENCE_BYTES = 64 * 1024;
let activeNodeExecutableMetadataPromise: Promise<ActiveNodeExecutableMetadata> | undefined;
let activeNodeExecutableHashesPromise: Promise<ActiveNodeExecutableHashes> | undefined;
export const NODE_EXECUTION_RUNTIME = {
  id: "sunder.runtime.node",
  versionRange: ">=24.18.1 <25.0.0",
  moduleAbi: "sunder.node-worker-module.v1",
} as const;
const NODE_WORKER_ENTRY_POINT = "bin/worker.cjs";

export interface PreparedProject extends LoadedProject {
  readonly contracts: readonly LoadedContract[];
}

export interface DevBuildOptions extends RegistryOriginOptions {
  readonly projectPath: string;
  readonly watch: boolean;
}

interface DevWatchSession {
  readonly project: PreparedProject;
  readonly workRoot: string;
  readonly bundlePath: string;
  readonly workerNoticePath: string;
  webRoot?: string;
  readonly buildContext: BuildContext;
  superseded: boolean;
}

export interface LeafBuildInput {
  readonly project: PreparedProject;
  readonly rid: SunderRid;
  readonly modulePath: string;
  readonly workerNoticePath: string;
  readonly discoveryRoot: string;
}

export interface LeafDiscoverySource {
  readonly path: string;
  readonly discoveryRoot: string;
}

export async function prepareProject(projectPath: string, options: RegistryOriginOptions = {}): Promise<PreparedProject> {
  const project = await loadProject(projectPath, options);
  if (project.ownedRpc !== undefined) return project;
  const contracts: LoadedContract[] = [];
  const identities = new Set<string>();
  const descriptorPaths = new Map<string, string>();
  for (const configured of project.config.contracts) {
    const sourcePath = resolveInside(project.root, configured.path, "contract path");
    const descriptor = parseRpcContractDescriptor(await readProjectDescriptorFile(
      project.root,
      sourcePath,
      `Contract descriptor '${configured.path}'`,
    ));
    const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
    const canonical = canonicalBytes.toString("utf8");
    const identity = `${descriptor.contractId}\0${descriptor.version}`;
    if (!identities.add(identity)) throw new Error(`Contract '${descriptor.contractId}' version '${descriptor.version}' is configured more than once.`);
    const descriptorPath = normalizeLogicalPath(configured.descriptorPath ?? `contracts/${basename(configured.path)}`);
    const foldedDescriptorPath = descriptorPath.toLowerCase();
    const collidingPath = descriptorPaths.get(foldedDescriptorPath);
    if (collidingPath !== undefined) throw new Error(`Contract descriptor paths '${collidingPath}' and '${descriptorPath}' collide.`);
    descriptorPaths.set(foldedDescriptorPath, descriptorPath);
    contracts.push({
      configuredPath: configured.path,
      sourcePath,
      descriptorPath,
      descriptor,
      canonicalBytes,
      canonical,
      descriptorSha256: sha256(canonicalBytes),
      abiSha256: rpcAbiSha256(descriptor),
      modelProjection: emptyModelProjection(),
      exposure: configured.exposure,
    });
  }
  for (const provider of project.config.providers) {
    const contract = contracts.find((candidate) => candidate.descriptor.contractId === provider.contractId
      && candidate.descriptor.version === provider.contractVersion);
    if (contract === undefined) {
      throw new Error(`Provider '${provider.providerId}' has no exact configured local contract descriptor.`);
    }
    if (provider.audience === "public" && contract.exposure !== "public") {
      throw new Error(`Provider '${provider.providerId}' has audience 'public', but contract '${provider.contractId}' is exposed only to its package owner.`);
    }
  }
  for (const use of project.config.usesContracts ?? []) {
    if (!contracts.some((contract) => contract.descriptor.contractId === use.contractId
      && contract.descriptor.version === use.contractVersion)) {
      throw new Error(`Contract use '${use.role}/${use.contractId}' version '${use.contractVersion}' has no exact configured local contract descriptor.`);
    }
  }
  return { ...project, contracts };
}

export async function buildDevPackage(options: DevBuildOptions): Promise<void> {
  const rid = currentRid();
  if (options.watch) {
    await watchDevPackage(resolve(options.projectPath), rid, options);
    return;
  }
  await withRpcBindingsOutputLock(options.projectPath, async () => {
    const project = await prepareProject(options.projectPath, options);
    await generateRpcBindingsForLoadedProjectLocked(project);
    const workRoot = resolve(project.outputRoot, ".sunder-dev-work");
    await withOutputLocks([workRoot], async () => {
      await assertReplaceableGeneratedDirectory(workRoot, true);
      const staged = await stageGeneratedDirectory(
        workRoot,
        "Sunder Node dev work output v1\n",
        async (stagingRoot) => {
          const bundlePath = resolve(stagingRoot, "worker.cjs");
          let webRoot: string | undefined;
          if (project.config.app !== undefined) {
            webRoot = resolve(stagingRoot, "web");
            await buildWebAssetsLocked(project, webRoot);
          }
          const result = await buildEsbuild(devBuildOptions(project, bundlePath));
          const workerNoticePath = await writeWorkerNotice(project, bundlePath, result.metafile);
          await emitDevTree(project, rid, bundlePath, workerNoticePath, webRoot);
        },
      );
      const stagedOutputs = [staged.output, staged.marker];
      try {
        await revalidateOwnedRpc(project);
        await commitStagedOutputs(stagedOutputs);
      } finally {
        await cleanupStagedOutputs(stagedOutputs);
      }
    });
    process.stdout.write(`Sunder dev package: ${resolve(project.outputRoot, "sunder-dev")}\n`);
  });
}

function devBuildOptions(project: PreparedProject, bundlePath: string): BuildOptions {
  return {
    absWorkingDir: project.root,
    entryPoints: [resolveInside(project.root, project.config.entry, "entry")],
    outfile: bundlePath,
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node24",
    sourcemap: false,
    metafile: true,
    logLevel: "info",
    ...workerResolutionOptions,
    ...(project.ownedRpc === undefined ? {} : { tsconfigRaw: {} }),
    plugins: ownedRpcPlugins(project),
  };
}

async function watchDevPackage(projectRoot: string, rid: SunderRid, options: RegistryOriginOptions): Promise<void> {
  let activeSession: DevWatchSession | undefined;
  const producerWatchers = new Map<string, FSWatcher>();
  let operationTail = Promise.resolve();
  let operationTimer: NodeJS.Timeout | undefined;
  let pendingRestart = false;
  let stopping = false;
  const knownOutputRoots = new Set<string>();

  const report = (error: unknown): void => {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  };
  const enqueue = (operation: () => Promise<void>): Promise<void> => {
    const result = operationTail.then(operation);
    operationTail = result.catch(report);
    return result;
  };
  const disposeSession = async (session: DevWatchSession | undefined): Promise<void> => {
    if (session === undefined) return;
    session.superseded = true;
    try {
      await session.buildContext.dispose();
    } finally {
      await rm(session.workRoot, { recursive: true, force: true });
    }
  };
  const buildSessionLocked = async (session: DevWatchSession, rebuildWeb: boolean): Promise<void> => {
    if (session.superseded || activeSession !== session || stopping) return;
    const result = await session.buildContext.rebuild();
    await writeWorkerNotice(session.project, session.bundlePath, result.metafile);
    if (session.superseded || activeSession !== session || stopping) return;
    if (session.project.config.app !== undefined && (rebuildWeb || session.webRoot === undefined)) {
      session.webRoot = resolve(session.workRoot, "web");
      await buildWebAssetsLocked(session.project, session.webRoot);
    }
    if (session.superseded || activeSession !== session || stopping) return;
    await emitDevTree(session.project, rid, session.bundlePath, session.workerNoticePath, session.webRoot);
    process.stdout.write(`Sunder dev package: ${resolve(session.project.outputRoot, "sunder-dev")}\n`);
  };
  const rebuildSession = async (session: DevWatchSession): Promise<void> => {
    await withRpcBindingsOutputLock(projectRoot, async () => {
      if (session.superseded || activeSession !== session || stopping) return;
      await generateRpcBindingsForLoadedProjectLocked(session.project);
      await buildSessionLocked(session, true);
    });
  };
  const createSessionLocked = async (project: PreparedProject): Promise<DevWatchSession> => {
    const producerFiles = new Set(project.ownedRpc?.producerFiles.map(pathIdentity) ?? []);
    for (const directory of new Set((project.ownedRpc?.producerFiles ?? []).map(dirname))) if (!producerWatchers.has(directory)) {
      const watcher = watchFiles(directory, (_event, name) => {
        if (name === null || producerFiles.has(pathIdentity(resolve(directory, name.toString())))) scheduleOperation(true);
      });
      watcher.on("error", report);
      producerWatchers.set(directory, watcher);
    }
    const workRoot = resolve(project.outputRoot, `.sunder-dev-work.stage-${randomUUID()}`);
    await rm(workRoot, { recursive: true, force: true });
    await mkdir(workRoot, { recursive: true });
    const bundlePath = resolve(workRoot, "worker.cjs");
    let buildContext: BuildContext;
    try {
      buildContext = await context(devBuildOptions(project, bundlePath));
    } catch (error) {
      await rm(workRoot, { recursive: true, force: true });
      throw error;
    }
    const session: DevWatchSession = {
      project,
      workRoot,
      bundlePath,
      workerNoticePath: `${bundlePath}.third-party-notices.txt`,
      buildContext,
      superseded: false,
    };
    knownOutputRoots.add(pathIdentity(project.outputRoot));
    return session;
  };
  const restartSession = async (): Promise<void> => {
    const previous = activeSession;
    activeSession = undefined;
    await disposeSession(previous);
    if (stopping) return;
    await withRpcBindingsOutputLock(projectRoot, async () => {
      const project = await prepareProject(projectRoot, options);
      await generateRpcBindingsForLoadedProjectLocked(project);
      const session = await createSessionLocked(project);
      activeSession = session;
      try {
        await buildSessionLocked(session, true);
      } catch (error) {
        activeSession = undefined;
        await disposeSession(session);
        throw error;
      }
    });
  };
  const scheduleOperation = (restart: boolean): void => {
    if (stopping) return;
    if (restart) {
      pendingRestart = true;
      if (activeSession !== undefined) activeSession.superseded = true;
    }
    if (operationTimer !== undefined) clearTimeout(operationTimer);
    operationTimer = setTimeout(() => {
      operationTimer = undefined;
      const shouldRestart = pendingRestart;
      pendingRestart = false;
      void enqueue(async () => {
        if (stopping) return;
        const session = activeSession;
        if (shouldRestart || session === undefined || session.superseded) await restartSession();
        else await rebuildSession(session);
      }).catch(() => undefined);
    }, 100);
  };

  const projectWatcher: FSWatcher = watchFiles(projectRoot, { recursive: true }, (_event, fileName) => {
    if (fileName === null) {
      scheduleOperation(true);
      return;
    }
    const relativeName = fileName.toString().replaceAll("\\", "/");
    if (/sunder-contracts\.owned\.json\.sunder-output\.lock(?:\.|\/|$)/u.test(relativeName)) return;
    const segments = relativeName.split("/");
    if (segments.some((segment) => segment === "node_modules"
      || segment === ".git"
      || segment === ".sunder"
      || segment.startsWith(".sunder-"))) return;
    const changedPath = resolve(projectRoot, relativeName);
    const changedIdentity = pathIdentity(changedPath);
    if ([...knownOutputRoots].some((root) => root === changedIdentity || isInside(root, changedIdentity))) return;
    const session = activeSession;
    if (session === undefined || session.superseded) {
      scheduleOperation(true);
      return;
    }
    const structuralPaths = new Set([
      session.project.configPath,
      ...session.project.contracts.map((contract) => contract.sourcePath),
      ...(session.project.config.rpcBindings ?? []).map((binding, index) => (
        resolveInside(session.project.root, binding.path, `rpcBindings[${index}].path`)
      )),
      ...Object.values(session.project.config.nativeAssets ?? {}).flatMap((assets) => (
        assets.map((asset) => resolveInside(session.project.root, asset.sourcePath, "native asset source"))
      )),
    ].map(pathIdentity));
    scheduleOperation(session.project.ownedRpc !== undefined || [...structuralPaths].some((path) => path === changedIdentity || isInside(changedIdentity, path)));
  });
  projectWatcher.on("error", report);
  try {
    await enqueue(restartSession);
    process.stdout.write("Watching package sources. Sunder will drain and restart the exact package activation after each canonical dev output change.\n");
    await new Promise<void>((resolvePromise) => {
      const stop = (): void => {
        if (stopping) return;
        stopping = true;
        process.off("SIGINT", stop);
        process.off("SIGTERM", stop);
        if (operationTimer !== undefined) clearTimeout(operationTimer);
        projectWatcher.close();
        for (const watcher of producerWatchers.values()) watcher.close();
        const session = activeSession;
        activeSession = undefined;
        void enqueue(() => disposeSession(session)).finally(resolvePromise);
      };
      process.once("SIGINT", stop);
      process.once("SIGTERM", stop);
    });
  } finally {
    projectWatcher.close();
    for (const watcher of producerWatchers.values()) watcher.close();
    await disposeSession(activeSession);
    await operationTail;
  }
}

export async function bundleProduction(project: PreparedProject, rid: SunderRid, outputPath: string): Promise<string> {
  return await withRpcBindingsOutputLock(project.root, async () => {
    await generateRpcBindingsForLoadedProjectLocked(project);
    return await bundleProductionWithGeneratedRpcBindings(project, rid, outputPath);
  });
}

export async function bundleProductionWithGeneratedRpcBindings(project: PreparedProject, rid: SunderRid, outputPath: string): Promise<string> {
  if (!SUPPORTED_RIDS.includes(rid)) throw new Error(`Unsupported exact RID '${rid}'.`);
  await mkdir(dirname(outputPath), { recursive: true });
  const result = await buildEsbuild({
    absWorkingDir: project.root,
    entryPoints: [resolveInside(project.root, project.config.entry, "entry")],
    outfile: outputPath,
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node24",
    sourcemap: false,
    metafile: true,
    minify: true,
    legalComments: "none",
    logLevel: "info",
    ...workerResolutionOptions,
    ...(project.ownedRpc === undefined ? {} : { tsconfigRaw: {} }),
    plugins: ownedRpcPlugins(project),
  });
  return await writeWorkerNotice(project, outputPath, result.metafile);
}

export async function buildWebTarget(projectPath: string, configuredRid?: SunderRid, options: RegistryOriginOptions = {}): Promise<string | null> {
  return await withRpcBindingsOutputLock(projectPath, async () => {
    const project = await prepareProject(projectPath, options);
    if (project.config.app === undefined) return null;
    await generateRpcBindingsForLoadedProjectLocked(project);
    const rid = configuredRid ?? currentRid();
    const webRoot = resolve(project.outputRoot, ".sunder-web-work", rid);
    const leafRoot = resolve(project.outputRoot, "targets", `app-${rid}`);
    const discoveryRoot = requireDiscoveryRoot(resolve(project.outputRoot, "targets"), leafRoot);
    await withOutputLocks([targetLeafDiscoveryKey(discoveryRoot)], async () => {
      await withOutputLocks([webRoot, leafRoot], async () => {
        await buildWebAssetsLocked(project, webRoot);
        await writeWebTargetLeafLocked(project, rid, webRoot, leafRoot);
      });
    });
    return leafRoot;
  });
}

export async function writeTargetLeaf(input: LeafBuildInput): Promise<string> {
  const leafRoot = resolve(input.project.outputRoot, "targets", input.rid);
  const discoveryRoot = requireDiscoveryRoot(input.discoveryRoot, leafRoot);
  await requireRegularFile(input.modulePath, "Bundled Worker module");
  await requireRegularFile(input.workerNoticePath, "Bundled worker dependency notice");
  const compatibility = packageToolCompatibility();
  const manifest = createManifest(input.project, [{
    role: "runtime",
    rid: input.rid,
    kind: "worker",
    entryPoint: NODE_WORKER_ENTRY_POINT,
    targetFramework: "node24",
    sdkVersion: compatibility.sdkVersion,
    requiredHostCapabilities: workerTargetCapabilities(input.project.config),
    executionRuntime: NODE_EXECUTION_RUNTIME,
  }]);
  await replaceGeneratedDirectory(
    leafRoot,
    "Sunder Node target leaf generated output v1\n",
    async (stagingRoot) => {
      const targetModule = resolve(stagingRoot, "payload", "runtime", input.rid, ...NODE_WORKER_ENTRY_POINT.split("/"));
      await mkdir(dirname(targetModule), { recursive: true });
      await copyFile(input.modulePath, targetModule);
      await copyNativeAssets(input.project, input.rid, stagingRoot);
      await copyContracts(input.project, stagingRoot);
      const workerNoticePath = resolve(stagingRoot, "payload", "runtime", input.rid, ...WORKER_NOTICE_LOGICAL_PATH.split("/"));
      await mkdir(dirname(workerNoticePath), { recursive: true });
      await copyFile(input.workerNoticePath, workerNoticePath);
      await writeCanonicalPackageMetadata(stagingRoot, manifest);
    },
    false,
    [discoveryRoot],
    [],
    () => revalidateOwnedRpc(input.project),
  );
  return leafRoot;
}

export async function writeWebTargetLeaf(
  project: PreparedProject,
  rid: SunderRid,
  webRoot: string,
  configuredDiscoveryRoot: string,
): Promise<string> {
  const app = project.config.app;
  if (app === undefined) throw new Error("The package does not configure an App web target.");
  const leafRoot = resolve(project.outputRoot, "targets", `app-${rid}`);
  const discoveryRoot = requireDiscoveryRoot(configuredDiscoveryRoot, leafRoot);
  await withOutputLocks([targetLeafDiscoveryKey(discoveryRoot)], async () => {
    await withOutputLocks([leafRoot, webRoot], async () => {
      await writeWebTargetLeafLocked(project, rid, webRoot, leafRoot);
    });
  });
  return leafRoot;
}

async function writeWebTargetLeafLocked(
  project: PreparedProject,
  rid: SunderRid,
  webRoot: string,
  leafRoot: string,
): Promise<void> {
  const app = project.config.app;
  if (app === undefined) throw new Error("The package does not configure an App web target.");
  await replaceGeneratedDirectoryLocked(
    leafRoot,
    "Sunder web target leaf generated output v1\n",
    async (stagingRoot) => {
      await validateWebOutput(app, webRoot);
      await copyDirectory(webRoot, resolve(stagingRoot, "payload", "app", rid));
      await copyContracts(project, stagingRoot, new Set(["app"]));
      await writeCanonicalPackageMetadata(stagingRoot, createManifest(project, [webTarget(app, rid,project.config)]));
    },
    false,
    () => revalidateOwnedRpc(project),
  );
}

export function assertRpcReleaseSupported(project: LoadedProject): void {
  if (project.config.schemaVersion === 3 && project.config.services !== undefined) {
    if (!project.ownedRpc) throw new Error("Service release packing requires source analysis.");
    if (project.ownedRpc.baseline) validateBaselineSource(project.ownedRpc.baseline, project.ownedRpc.contracts, true);
    else if (project.ownedRpc.contracts.some(contract => contract.exposure === "public")) throw new Error("Public service release packing requires an accepted, frozen owned baseline.");
  }
}

export async function aggregateAndPack(
  projectPath: string,
  configuredSources: readonly LeafDiscoverySource[] = [],
  options: RegistryOriginOptions = {},
): Promise<{ packageRoot: string; archivePath: string; evidencePath: string; bytes: number }> {
  const project = await prepareProject(projectPath, options);
  assertRpcReleaseSupported(project);
  const targetsRoot = resolve(project.outputRoot, "targets");
  const sources = configuredSources.length === 0
    ? [{ path: targetsRoot, discoveryRoot: targetsRoot }]
    : configuredSources.map((source) => ({
      path: resolve(source.path),
      discoveryRoot: requireDiscoveryRoot(source.discoveryRoot, source.path),
    }));
  const discoveryRoots = [...new Map(sources.map((source) => [pathIdentity(source.discoveryRoot), source.discoveryRoot])).values()];
  const packageRoot = resolve(project.outputRoot, "package");
  const archivePath = resolve(project.outputRoot, `${project.config.id}.${project.config.version}.sunderpkg`);
  const evidencePath = resolve(project.outputRoot, `${project.config.id}.${project.config.version}.package-evidence.json`);
  return await withOutputLocks(discoveryRoots.map(targetLeafDiscoveryKey), async () => {
    const discovered = (await Promise.all(sources.map((source) => discoverLeaves(source.path)))).flat();
    const leafRoots = [...new Map(discovered.map((path) => [pathIdentity(path), path])).values()];
    if (leafRoots.length === 0) throw new Error("No canonical package target leaves were found.");
    return await withOutputLocks([...leafRoots, packageRoot, archivePath, evidencePath], async () => {
      const leaves = await Promise.all(leafRoots.map((root) => readLeaf(root, project.config, project)));
      if (leaves.length > 12) throw new Error("A universal Sunder package may contain at most 12 exact targets.");
      const targetKeys = new Set<string>();
      for (const leaf of leaves) {
        for (const target of leaf.manifest.targets) {
          const key = `${target.role}/${target.rid}`;
          if (targetKeys.has(key)) throw new Error(`Aggregate inputs declare exact target '${key}' more than once.`);
          targetKeys.add(key);
        }
      }
      validateConfiguredNativeAssetLeaves(leaves, project.config);
      if (project.ownedRpc && (project.ownedRpc.imports.lock.packageArtifacts.size || project.ownedRpc.imports.lock.platformImports.size)) {
        for (const leaf of leaves) if (packageMetadataFingerprint(leaf.manifest) !== packageMetadataFingerprint(createManifest(project, leaf.manifest.targets)))
          throw new Error("Aggregate target leaf import metadata differs from the fresh role-scoped source projection.");
      } else {
        const fingerprint = packageMetadataFingerprint(leaves[0]?.manifest);
        if (leaves.some((leaf) => packageMetadataFingerprint(leaf.manifest) !== fingerprint)) throw new Error("Aggregate target leaves disagree on package-wide manifest metadata.");
        if (fingerprint !== packageMetadataFingerprint(createManifest(project, []))) throw new Error("Aggregate target leaves do not match the configured package identity and metadata.");
      }
      await assertReplaceableGeneratedDirectory(packageRoot);
      await assertReplaceableGeneratedFile(archivePath, "package archive");
      await assertReplaceableGeneratedFile(evidencePath, "package ownership evidence");
      const targets = leaves.flatMap((leaf) => leaf.manifest.targets).sort(compareTargets);
      const declarations: RpcManifestDeclarations = {
        usesContracts: unionDeclarations(
          leaves.flatMap((leaf) => leaf.manifest.usesContracts),
          (use) => `${use.role}\0${use.contractId}`,
          "contract use",
          compareContractUses,
        ),
        provides: unionDeclarations(
          leaves.flatMap((leaf) => leaf.manifest.provides),
          (provider) => `${provider.role}\0${provider.providerId}`,
          "provider",
          compareProviders,
        ),
      };
      assertRoleScopedDeclarationSets(leaves);
      const aggregateManifest = createManifest(project, targets, declarations);
      const configuredManifest = createManifest(project, targets);
      if (rpcDeclarationsFingerprint(aggregateManifest) !== rpcDeclarationsFingerprint(configuredManifest)) {
        throw new Error("Aggregate target leaves do not exactly match the configured role-scoped RPC uses and providers.");
      }
      if (project.ownedRpc) {
        const completeRoles = new Set<PackageRole>(["runtime"]);
        if (targets.some((target) => target.role === "app")) completeRoles.add("app");
        const complete = configuredRpcDeclarations(project, completeRoles);
        if (canonicalizeDescriptor(asJsonValue(complete)) !== canonicalizeDescriptor(asJsonValue(declarations))) throw new Error("Owned release requires the complete source provider/use set, including every runtime provider.");
        // Prebuilt leaves are not source evidence. Compile fresh ordinary source
        // and compare the production module too, even for metadata-identical edits.
        const fresh = await buildEsbuild({ absWorkingDir: project.root, entryPoints: [resolveInside(project.root, project.config.entry, "entry")],
          bundle: true, platform: "node", format: "cjs", target: "node24", sourcemap: false, minify: true, legalComments: "none", logLevel: "silent",
          ...workerResolutionOptions, tsconfigRaw: {}, write: false, plugins: ownedRpcPlugins(project) });
        const expectedHash = sha256(fresh.outputFiles![0]!.contents);
        for (const leaf of leaves) if (leaf.manifest.targets[0]!.role === "runtime" && leaf.files.get(NODE_WORKER_ENTRY_POINT)?.sha256 !== expectedHash) throw new Error("Prebuilt Worker module differs from fresh full ordinary source; rebuild before release packing.");
      }
      const stagedPackage = await stageGeneratedDirectory(
        packageRoot,
        "Sunder package aggregate generated output v1\n",
        async (stagingRoot) => {
          const logicalPaths = new Set(leaves.flatMap((leaf) => [...leaf.files.keys()]));
          const packageSharedPaths = new Set(aggregateManifest.contractBundles.map((bundle) => bundle.descriptorPath));
          for (const logicalPath of [...logicalPaths].sort(ordinal)) {
            if (logicalPath === MODEL_PROJECTION_PATH) continue;
            const occurrences = leaves.filter((leaf) => leaf.files.has(logicalPath));
            if (isExactRidNativeAsset(logicalPath, occurrences, project.config)) {
              for (const leaf of occurrences) {
                const target = single(leaf.manifest.targets, "Target leaf must contain one exact target for aggregation.");
                await copyLeafFile(leaf, logicalPath, resolve(stagingRoot, "payload", target.role, target.rid, ...logicalPath.split("/")));
              }
              continue;
            }
            if (packageSharedPaths.has(logicalPath)
              && identical(occurrences, logicalPath)) {
              await copyLeafFile(occurrences[0]!, logicalPath, resolve(stagingRoot, "payload", "shared", ...logicalPath.split("/")));
              continue;
            }
            const occurrenceRoles = new Set(occurrences.map((leaf) => single(
              leaf.manifest.targets,
              "Target leaf must contain one exact target for aggregation.",
            ).role));
            if (occurrenceRoles.size === 1 && identical(occurrences, logicalPath)) {
              const role = [...occurrenceRoles][0]!;
              const roleLeaves = leaves.filter((leaf) => single(
                leaf.manifest.targets,
                "Target leaf must contain one exact target for aggregation.",
              ).role === role);
              if (occurrences.length === roleLeaves.length) {
                await copyLeafFile(occurrences[0]!, logicalPath, resolve(stagingRoot, "payload", role, "shared", ...logicalPath.split("/")));
                continue;
              }
            }
            if (occurrences.length === leaves.length && identical(occurrences, logicalPath)) {
              await copyLeafFile(occurrences[0]!, logicalPath, resolve(stagingRoot, "payload", "shared", ...logicalPath.split("/")));
              continue;
            }
            for (const leaf of occurrences) {
              const target = single(leaf.manifest.targets, "Target leaf must contain one exact target for aggregation.");
              await copyLeafFile(leaf, logicalPath, resolve(stagingRoot, "payload", target.role, target.rid, ...logicalPath.split("/")));
            }
          }
          const projectionPath = resolve(stagingRoot, ...MODEL_PROJECTION_ARCHIVE_PATH.split("/"));
          await mkdir(dirname(projectionPath), { recursive: true });
          await writeFile(projectionPath, packageProjectionBytes(project, aggregateManifest.contractBundles.map(bundle => bundle.contractId)));
          await writeCanonicalPackageMetadata(stagingRoot, aggregateManifest);
        },
      );
      const stagedArchive: StagedOutput = {
        finalPath: archivePath,
        stagedPath: `${archivePath}.stage-${randomUUID()}`,
      };
      const stagedEvidence: StagedOutput = {
        finalPath: evidencePath,
        stagedPath: `${evidencePath}.stage-${randomUUID()}`,
      };
      const stagedOutputs = [stagedPackage.output, stagedPackage.marker, stagedArchive, stagedEvidence];
      try {
        const maximum = project.config.maximumPackageBytes ?? DEFAULT_MAX_PACKAGE_BYTES;
        const bytes = await writeDeterministicZip(stagedPackage.output.stagedPath, stagedArchive.stagedPath, maximum);
        const evidence = await createPackageOwnershipEvidence(
          stagedPackage.output.stagedPath,
          aggregateManifest,
          project.config,
          stagedArchive.stagedPath,
          bytes,
        );
        await writeJson(stagedEvidence.stagedPath, evidence);
        const commit = async (): Promise<void> => {
          await revalidateOwnedRpc(project);
          assertRpcReleaseSupported(project);
          await commitStagedOutputs(stagedOutputs);
        };
        if (project.ownedRpc) await withBaselineLease(project.ownedRpc.baselineInput, commit);
        else await commit();
        process.stdout.write(`Sunder package size: ${bytes} bytes (${targets.length} exact RID target${targets.length === 1 ? "" : "s"})\n`);
        process.stdout.write(`Sunder package ownership evidence: ${evidencePath}\n`);
        return { packageRoot, archivePath, evidencePath, bytes };
      } finally {
        await cleanupStagedOutputs(stagedOutputs);
      }
    });
  });
}

function workerTargetCapabilities(config: SunderNodePackageConfig): readonly string[] {
  const compatibility = packageToolCompatibility();
  return [
    "core.v1",
    "execution-runtime.v1",
    "rpc.v1",
    compatibility.sdkBaselineCapability,
    "worker-protocol.v1",
    ...config.workerCapabilities,
  ].sort(ordinal);
}

async function emitDevTree(
  project: PreparedProject,
  rid: SunderRid,
  bundlePath: string,
  workerNoticeSourcePath: string,
  webRoot?: string,
): Promise<void> {
  const root = resolve(project.outputRoot, "sunder-dev");
  const obsoleteSidecarPath = `${root}.sunder-node-dev.json`;
  const compatibility = packageToolCompatibility();
  const targets: TargetManifest[] = [{
    role: "runtime",
    rid,
    kind: "worker",
    entryPoint: NODE_WORKER_ENTRY_POINT,
    targetFramework: "node24",
    sdkVersion: compatibility.sdkVersion,
    requiredHostCapabilities: workerTargetCapabilities(project.config),
    executionRuntime: NODE_EXECUTION_RUNTIME,
  }];
  if (project.config.app !== undefined) targets.push(webTarget(project.config.app, rid,project.config));
  const manifest = createManifest(project, targets);
  await withOutputLocks([root, obsoleteSidecarPath], async () => {
    await assertReplaceableGeneratedDirectory(root);
    await assertReplaceableGeneratedFile(obsoleteSidecarPath, "legacy Node dev sidecar");
    const stagedDirectory = await stageGeneratedDirectory(
      root,
      "Sunder Node dev generated output v1\n",
      async (stagingRoot) => {
        const destination = resolve(stagingRoot, "payload", "runtime", "shared", ...NODE_WORKER_ENTRY_POINT.split("/"));
        await mkdir(dirname(destination), { recursive: true });
        await copyFile(bundlePath, destination);
        await copyNativeAssets(project, rid, stagingRoot);
        const workerNoticePath = resolve(stagingRoot, "payload", "runtime", "shared", ...WORKER_NOTICE_LOGICAL_PATH.split("/"));
        await mkdir(dirname(workerNoticePath), { recursive: true });
        await copyFile(workerNoticeSourcePath, workerNoticePath);
        if (webRoot !== undefined) {
          await copyDirectory(webRoot, resolve(stagingRoot, "payload", "app", rid));
        }
        await copyContracts(project, stagingRoot, new Set(targets.map(target => target.role)));
        await writeCanonicalPackageMetadata(stagingRoot, manifest);
      },
    );
    const stagedOutputs = [stagedDirectory.output, stagedDirectory.marker];
    try {
      await revalidateOwnedRpc(project);
      await commitStagedOutputs(stagedOutputs);
      await rm(obsoleteSidecarPath, { force: true });
    } finally {
      await cleanupStagedOutputs(stagedOutputs);
    }
  });
}

function createManifest(
  project: PreparedProject,
  targets: readonly TargetManifest[],
  declarations?: RpcManifestDeclarations,
): PackageManifest {
  const targetRoles = new Set<PackageRole>(targets.map((target) => target.role));
  const imports = project.ownedRpc?.imports;
  const importedContracts = imports ? importedContractsForRoles(imports, targetRoles) : new Set<string>();
  const contractBundles = project.contracts.filter(contract => !contract.imported || importedContracts.has(contract.descriptor.contractId)).map((contract): ContractBundleManifest => ({
    contractId: contract.descriptor.contractId,
    version: contract.descriptor.version,
    descriptorPath: contract.descriptorPath,
    descriptorSha256: contract.descriptorSha256,
    abiSha256: contract.abiSha256,
    exposure: contract.exposure,
    ...(contract.imported ? { source: "imported" as const, ...authority(contract.imported) as Pick<ContractBundleManifest, "authorityKind" | "authorityId" | "selectedAuthorityRelease" | "selectedAuthorityContentIdentity"> }
      : { source: "owned" as const, authorityKind: "package" as const, authorityId: project.config.id }),
  })).sort((left, right) => ordinal(`${left.contractId}\0${left.version}`, `${right.contractId}\0${right.version}`));
  const configuredDeclarations = configuredRpcDeclarations(project, targetRoles);
  const selectedDeclarations = declarations ?? configuredDeclarations;
  const manifest: PackageManifest = {
    archiveFormatVersion: ARCHIVE_FORMAT_VERSION,
    manifestVersion: PACKAGE_MANIFEST_VERSION,
    id: project.config.id,
    name: project.config.name,
    ...(project.config.summary === undefined ? {} : { summary: project.config.summary }),
    version: project.config.version,
    dependsOn: [...(project.config.dependencies ?? []).map((dependency) => ({
      packageId: dependency.packageId,
      versionRange: dependency.versionRange,
      presence: "required" as const,
    })), ...(imports ? importedDependenciesForRoles(imports, targetRoles) : [])].sort((a, b) => ordinal(a.packageId, b.packageId)),
    targets: [...targets].sort(compareTargets),
    contractBundles,
    usesContracts: [...selectedDeclarations.usesContracts].sort(compareContractUses),
    provides: [...selectedDeclarations.provides].sort(compareProviders),
    ...(imports ? importProjection(imports, targetRoles, project.ownedRpc!.importedProviders) : { packageArtifactImports: [], packageApiImports: [], providerImports: [], platformImports: [] }),
  };
  validateManifestMetadata(manifest, targetRoles, "Generated package manifest", project.ownedRpc ? manifest : undefined);
  return manifest;
}

function configuredRpcDeclarations(
  project: PreparedProject,
  targetRoles: ReadonlySet<PackageRole>,
): RpcManifestDeclarations {
  const usesContracts = (project.config.usesContracts ?? [])
    .filter((use) => targetRoles.has(use.role))
    .map((use): ContractUseManifest => {
      const contract = exactContract(project, use.contractId, use.contractVersion);
      return {
        role: use.role,
        contractId: use.contractId,
        contractVersion: use.contractVersion,
        abiSha256: contract.abiSha256,
        required: use.required,
        actions: [...use.actions],
      };
    });
  const provides = project.config.providers
    .filter((provider) => targetRoles.has(provider.role))
    .map((provider): ProviderManifest => {
      const contract = exactContract(project, provider.contractId, provider.contractVersion);
      return {
        providerId: provider.providerId,
        role: provider.role,
        contractId: provider.contractId,
        contractVersion: provider.contractVersion,
        descriptorSha256: contract.descriptorSha256,
        abiSha256: contract.abiSha256,
        audience: provider.audience,
      };
    });
  return { usesContracts, provides };
}

function exactContract(project: PreparedProject, contractId: string, contractVersion: string): LoadedContract {
  const contract = project.contracts.find((candidate) => candidate.descriptor.contractId === contractId
    && candidate.descriptor.version === contractVersion);
  if (contract === undefined) throw new Error(`Contract '${contractId}' version '${contractVersion}' was not loaded.`);
  return contract;
}

async function copyContracts(project: PreparedProject, packageRoot: string, roles: ReadonlySet<PackageRole> = new Set(["runtime"])): Promise<void> {
  const imported = project.ownedRpc ? importedContractsForRoles(project.ownedRpc.imports, roles) : new Set<string>();
  const contracts = project.contracts.filter(contract => !contract.imported || imported.has(contract.descriptor.contractId));
  for (const contract of contracts) {
    if (contract.descriptorPath.toLowerCase() === MODEL_PROJECTION_PATH) throw new Error("Contract descriptor shadows the reserved model projection sidecar.");
    const path = resolve(packageRoot, "payload", "shared", ...contract.descriptorPath.split("/"));
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, contract.canonicalBytes);
  }
  const path = resolve(packageRoot, ...MODEL_PROJECTION_ARCHIVE_PATH.split("/"));
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, packageProjectionBytes(project, contracts.map(contract => contract.descriptor.contractId)));
}
function packageProjectionBytes(project: PreparedProject, ids: readonly string[]): Buffer {
  const evidence = project.contracts.filter(contract => contract.exposure === "public" && ids.includes(contract.descriptor.contractId)).map(contract => ({
    contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256,
    modelProjection: contract.modelProjection,methodPolicies:contract.methodPolicies, descriptor: contract.descriptor, authorityKind: contract.imported?.authorityKind ?? "package", authorityId: contract.imported?.authorityId ?? project.config.id,
  }));
  validateModelProjectionSet(evidence);
  return projectionDocumentBytes(project.config.id, project.config.version, evidence);
}

async function copyNativeAssets(project: PreparedProject, rid: SunderRid, packageRoot: string): Promise<void> {
  const projectRealPath = await realpath(project.root);
  for (const [index, asset] of (project.config.nativeAssets?.[rid] ?? []).entries()) {
    const sourcePath = resolveInside(project.root, asset.sourcePath, `nativeAssets.${rid}[${index}].sourcePath`);
    await requireRegularFile(sourcePath, `Native asset '${asset.sourcePath}'`);
    const sourceRealPath = await realpath(sourcePath);
    if (!isInside(projectRealPath, sourceRealPath)) {
      throw new Error(`Native asset source '${asset.sourcePath}' resolves outside the package project.`);
    }
    const destination = resolve(packageRoot, "payload", "runtime", rid, ...asset.targetPath.split("/"));
    await mkdir(dirname(destination), { recursive: true });
    await copyFile(sourcePath, destination);
    const classification = await classifyRuntimePayloadFile(
      asset.targetPath,
      destination,
      rid,
      project.config,
    );
    if (classification.runtimeOwned) {
      throw new Error(
        `Native asset '${asset.sourcePath}' contains forbidden execution-runtime closure at '${asset.targetPath}' (${classification.detail}).`,
      );
    }
  }
}

async function writeWorkerNotice(
  project: PreparedProject,
  bundlePath: string,
  metafile: Metafile | undefined,
): Promise<string> {
  if (metafile === undefined) throw new Error("esbuild did not return required bundled-module attribution metadata.");
  const moduleIds = new Set<string>();
  for (const output of Object.values(metafile.outputs)) {
    for (const [moduleId, contribution] of Object.entries(output.inputs)) {
      if (moduleId.startsWith("sunder-owned-rpc:") && project.ownedRpc?.files.has(moduleId.slice("sunder-owned-rpc:".length))) continue;
      if (contribution.bytesInOutput > 0) moduleIds.add(moduleId);
    }
  }
  const noticePath = `${bundlePath}.third-party-notices.txt`;
  await writeFile(
    noticePath,
    await renderBundledDependencyNotice(project.root, [...moduleIds], "worker"),
    "utf8",
  );
  return noticePath;
}

async function buildWebAssetsLocked(project: PreparedProject, outputRoot: string): Promise<void> {
  const app = project.config.app;
  if (app === undefined) throw new Error("The package does not configure an App web target.");
  if (!isInside(project.outputRoot, outputRoot) && resolve(project.outputRoot) !== resolve(outputRoot)) {
    throw new Error("The generated Vite output must remain inside outputDirectory.");
  }
  const appRoot = await realpath(resolveAppRoot(project));
  const { build: buildVite } = await import("vite");
  await assertReplaceableGeneratedDirectory(outputRoot, true);
  const staged = await stageGeneratedDirectory(
    outputRoot,
    "Sunder Vite work output v1\n",
    async (stagingRoot) => {
      const moduleIds = new Set<string>();
      const metadataPlugin: VitePlugin = {
        name: "sunder-bundled-dependency-metadata",
        enforce: "post",
        generateBundle(_options, bundle) {
          for (const output of Object.values(bundle)) {
            if (output.type === "chunk") {
              for (const [moduleId, metadata] of Object.entries(output.modules)) {
                if (!isVirtualBundlerModuleId(moduleId) || metadata.renderedLength > 0) moduleIds.add(moduleId);
              }
            }
          }
        },
      };
      await buildVite({
        root: appRoot,
        base: "./",
        plugins: [metadataPlugin],
        build: {
          outDir: stagingRoot,
          emptyOutDir: true,
        },
        logLevel: "info",
      });
      await validateWebOutput(app, stagingRoot);
      const noticePath = resolve(stagingRoot, ...WEB_NOTICE_LOGICAL_PATH.split("/"));
      await mkdir(dirname(noticePath), { recursive: true });
      await writeFile(noticePath, await renderBundledDependencyNotice(project.root, [...moduleIds], "web"), "utf8");
    },
  );
  const stagedOutputs = [staged.output, staged.marker];
  try {
    await revalidateOwnedRpc(project);
    await commitStagedOutputs(stagedOutputs);
  } finally {
    await cleanupStagedOutputs(stagedOutputs);
  }
}

function isVirtualBundlerModuleId(moduleId: string): boolean {
  return moduleId.startsWith("\0")
    || moduleId.startsWith("virtual:")
    || moduleId.startsWith("vite:")
    || moduleId.startsWith("rolldown:")
    || !moduleId.startsWith("file://") && !isAbsolute(moduleId) && /^[a-z][a-z0-9+.-]*:/iu.test(moduleId)
    || moduleId.startsWith("<");
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

function webTargetCapabilities(config:SunderNodePackageConfig):readonly string[]{
  return ["core.v1","rpc.v1",packageToolCompatibility().sdkBaselineCapability,"views.v1",...(config.workerCapabilities.includes("rpc.extended-deadline.v1")?["rpc.extended-deadline.v1"]:[])].sort(ordinal);
}
function webTarget(app: AppConfig, rid: SunderRid,config:SunderNodePackageConfig): TargetManifest {
  const compatibility = packageToolCompatibility();
  return {
    role: "app",
    rid,
    kind: "web",
    entryPoint: app.entryPoint,
    targetFramework: "web",
    sdkVersion: compatibility.sdkVersion,
    requiredHostCapabilities: webTargetCapabilities(config),
    views: app.views.map((view) => ({ ...view })),
  };
}

async function writeCanonicalPackageMetadata(root: string, manifest: PackageManifest): Promise<{ contentIdentity: string }> {
  const manifestPath = resolve(root, ...MANIFEST_PATH.split("/"));
  await mkdir(dirname(manifestPath), { recursive: true });
  await writeJson(manifestPath, manifest);
  if ((await stat(manifestPath)).size > MAXIMUM_METADATA_BYTES) throw new Error("Package manifest exceeds the 1 MiB metadata limit.");
  const entries = await enumerateCanonicalFiles(root);
  if (entries.length + 1 > MAXIMUM_ARCHIVE_ENTRIES) {
    throw new Error(`Canonical package output exceeds the ${MAXIMUM_ARCHIVE_ENTRIES}-file content-index limit.`);
  }
  for (const entry of entries) { canonicalArchivePath(entry); assertProjectionPath(entry); }
  const files: ContentIndexEntry[] = [];
  for (const entry of entries.filter((entry) => entry !== INDEX_PATH)) {
    const path = resolve(root, ...entry.split("/"));
    files.push({ path: entry, sha256: await hashFile(path), size: (await stat(path)).size });
  }
  files.sort((left, right) => ordinal(left.path, right.path));
  const indexPath = resolve(root, ...INDEX_PATH.split("/"));
  await writeJson(indexPath, { schemaVersion: CONTENT_INDEX_SCHEMA_VERSION, files });
  if ((await stat(indexPath)).size > MAXIMUM_METADATA_BYTES) throw new Error("Package content index exceeds the 1 MiB metadata limit.");
  const contentIdentity = sha256(Buffer.concat([
    Buffer.from(await hashFile(manifestPath), "hex"),
    Buffer.from(await hashFile(indexPath), "hex"),
  ]));
  return { contentIdentity };
}

async function createPackageOwnershipEvidence(
  packageRoot: string,
  manifest: PackageManifest,
  config: SunderNodePackageConfig,
  archivePath: string,
  archiveBytes: number,
): Promise<PackageOwnershipEvidence> {
  const contentIndex = JSON.parse(await readFile(resolve(packageRoot, ...INDEX_PATH.split("/")), "utf8")) as ContentIndex;
  const entries = [...contentIndex.files].sort((left, right) => ordinal(left.path, right.path));
  const targets: PackageTargetOwnershipEvidence[] = [];
  const runtimeClosure = new Map<string, { readonly entry: ContentIndexEntry; readonly detail: string }>();
  for (const target of manifest.targets) {
    const projected = new Map<string, { readonly entry: ContentIndexEntry; readonly layer: PayloadOwnershipLayer }>();
    for (const entry of entries) {
      const owned = targetPayloadOwnership(entry.path, target);
      if (owned === null) continue;
      if (projected.has(owned.logicalPath)) {
        throw new Error(`Package ownership evidence found projected collision '${owned.logicalPath}' for '${target.role}/${target.rid}'.`);
      }
      projected.set(owned.logicalPath, { entry, layer: owned.layer });
    }
    const nativeAssets: PackageNativeAssetEvidence[] = [];
    const targetRuntimeClosure = new Map<string, ContentIndexEntry>();
    const ownership = { packageSharedBytes: 0, roleSharedBytes: 0, exactRidBytes: 0 };
    for (const [logicalPath, item] of [...projected].sort(([left], [right]) => ordinal(left, right))) {
      ownership[item.layer] += item.entry.size;
      const fullPath = resolve(packageRoot, ...item.entry.path.split("/"));
      const classification = target.role === "runtime"
        ? await classifyRuntimePayloadFile(
          logicalPath,
          fullPath,
          target.rid as SunderRid,
          config,
          item.entry.sha256,
        )
        : packagePayloadClassification();
      if (classification.nativeAsset) {
        if (item.layer !== "exactRidBytes") {
          throw new Error(`Native asset '${logicalPath}' for '${target.role}/${target.rid}' was factored outside its exact RID layer.`);
        }
        nativeAssets.push({ path: logicalPath, bytes: item.entry.size, sha256: item.entry.sha256 });
      }
      if (classification.runtimeOwned) {
        targetRuntimeClosure.set(logicalPath, item.entry);
        runtimeClosure.set(item.entry.path, { entry: item.entry, detail: classification.detail });
      }
    }
    const targetRuntimeClosureEntries = [...targetRuntimeClosure.values()];
    targets.push({
      role: target.role,
      rid: target.rid,
      kind: target.kind,
      fileCount: projected.size,
      payloadBytes: ownership.packageSharedBytes + ownership.roleSharedBytes + ownership.exactRidBytes,
      ownership,
      nativeAssets,
      runtimeClosureFileCount: targetRuntimeClosureEntries.length,
      runtimeClosureBytes: targetRuntimeClosureEntries.reduce((total, entry) => total + entry.size, 0),
    });
  }
  const runtimeClosureEntries = [...runtimeClosure.entries()].sort(([left], [right]) => ordinal(left, right));
  const runtimeClosureFileCount = runtimeClosureEntries.length;
  const runtimeClosureBytes = runtimeClosureEntries.reduce((total, [, item]) => total + item.entry.size, 0);
  if (runtimeClosureFileCount > 0) {
    throw new Error(`Package contains a forbidden execution-runtime closure: ${runtimeClosureEntries
      .map(([path, item]) => `${path} (${item.detail})`)
      .join(", ")}.`);
  }
  const payloadEntries = entries.filter((entry) => entry.path.startsWith("payload/"));
  const metadataEntries = entries.filter((entry) => entry.path.startsWith("manifest/"));
  const contentIndexBytes = (await stat(resolve(packageRoot, ...INDEX_PATH.split("/")))).size;
  return {
    schemaVersion: 1,
    packageId: manifest.id,
    packageVersion: manifest.version,
    archive: basename(archivePath).replace(/\.stage-[0-9a-f-]+$/u, ""),
    archiveSha256: await hashFile(archivePath),
    archiveBytes,
    payloadBytes: payloadEntries.reduce((total, entry) => total + entry.size, 0),
    metadataBytes: metadataEntries.reduce((total, entry) => total + entry.size, contentIndexBytes),
    runtimeClosureFileCount,
    runtimeClosureBytes,
    targets,
  };
}

function targetPayloadOwnership(
  path: string,
  target: TargetManifest,
): { readonly logicalPath: string; readonly layer: PayloadOwnershipLayer } | null {
  for (const candidate of [
    { prefix: `payload/${target.role}/${target.rid}/`, layer: "exactRidBytes" as const },
    { prefix: `payload/${target.role}/shared/`, layer: "roleSharedBytes" as const },
    { prefix: "payload/shared/", layer: "packageSharedBytes" as const },
  ]) {
    if (path.startsWith(candidate.prefix)) {
      return { logicalPath: path.slice(candidate.prefix.length), layer: candidate.layer };
    }
  }
  return null;
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
  const paths = output.sort(ordinal);
  for (const path of paths) canonicalArchivePath(path);
  validatePortablePathCollisions(paths);
  return paths;
}

async function readLeaf(root: string, config: SunderNodePackageConfig, project: PreparedProject): Promise<Leaf> {
  const rootEntry = await lstat(root);
  if (!rootEntry.isDirectory() || rootEntry.isSymbolicLink()) throw new Error(`Target leaf '${root}' must be a regular directory.`);
  const manifestPath = resolve(root, ...MANIFEST_PATH.split("/"));
  const indexPath = resolve(root, ...INDEX_PATH.split("/"));
  await requireRegularFile(manifestPath, "target leaf manifest");
  await requireRegularFile(indexPath, "target leaf content index");
  if ((await stat(manifestPath)).size > MAXIMUM_METADATA_BYTES || (await stat(indexPath)).size > MAXIMUM_METADATA_BYTES) {
    throw new Error(`Target leaf '${root}' metadata exceeds the 1 MiB limit.`);
  }
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as PackageManifest;
  const index = JSON.parse(await readFile(indexPath, "utf8")) as ContentIndex;
  if (manifest.archiveFormatVersion !== ARCHIVE_FORMAT_VERSION) {
    throw new Error(`Target leaf '${root}' must declare archiveFormatVersion ${ARCHIVE_FORMAT_VERSION}.`);
  }
  if (manifest.manifestVersion !== PACKAGE_MANIFEST_VERSION) {
    throw new Error(`Target leaf '${root}' must declare manifestVersion ${PACKAGE_MANIFEST_VERSION}.`);
  }
  if (!Array.isArray(manifest.targets)
    || manifest.targets.length !== 1
    || index.schemaVersion !== CONTENT_INDEX_SCHEMA_VERSION
    || !Array.isArray(index.files)) {
    throw new Error(`Target leaf '${root}' is not a canonical Sunder package leaf.`);
  }
  if (index.files.length > MAXIMUM_ARCHIVE_ENTRIES) throw new Error(`Target leaf '${root}' contains too many indexed files.`);
  const target = manifest.targets[0]!;
  validateTarget(target, manifest.id, root, config);
  validateManifestMetadata(manifest, new Set([target.role]), `Target leaf '${root}'`, project.ownedRpc ? createManifest(project, manifest.targets) : undefined);
  const bundledDescriptorPaths = new Set(manifest.contractBundles.map((bundle) => bundle.descriptorPath));
  const files = new Map<string, LeafFile>();
  const indexedPaths = new Set<string>();
  let indexedBytes = 0;
  for (const item of index.files) {
    const archivePath = canonicalArchivePath(item.path);
    assertProjectionPath(archivePath);
    if (indexedPaths.has(archivePath)) throw new Error(`Target leaf '${root}' indexes '${archivePath}' more than once.`);
    indexedPaths.add(archivePath);
    if (!/^[0-9a-f]{64}$/u.test(item.sha256) || !Number.isSafeInteger(item.size) || item.size < 0 || item.size > MAXIMUM_ENTRY_BYTES) {
      throw new Error(`Target leaf '${root}' has invalid content metadata for '${archivePath}'.`);
    }
    if (item.size > MAXIMUM_TOTAL_BYTES - indexedBytes) throw new Error(`Target leaf '${root}' exceeds the total uncompressed size limit.`);
    indexedBytes += item.size;
    const fullPath = resolve(root, ...archivePath.split("/"));
    await requireRegularFile(fullPath, `target leaf file '${archivePath}'`);
    if (await hashFile(fullPath) !== item.sha256 || (await stat(fullPath)).size !== item.size) throw new Error(`Target leaf file '${item.path}' failed hash validation.`);
    if (archivePath === MANIFEST_PATH) continue;
    const rawLogical = physicalToLogical(archivePath, target);
    if (rawLogical === null) continue;
    const logical = normalizeLogicalPath(rawLogical);
    if (logical !== rawLogical) throw new Error(`Target leaf '${root}' contains non-canonical logical path '${rawLogical}'.`);
    const classification = target.role === "runtime"
      ? await classifyRuntimePayloadFile(
        logical,
        fullPath,
        target.rid as SunderRid,
        config,
        item.sha256,
      )
      : packagePayloadClassification();
    if (classification.runtimeOwned) {
      throw new Error(
        `Target leaf '${root}' contains forbidden execution-runtime closure '${logical}' (${classification.detail}).`,
      );
    }
    if (bundledDescriptorPaths.has(logical) && archivePath !== `payload/shared/${logical}`) {
      throw new Error(`Target leaf '${root}' must store bundled contract descriptor '${logical}' in payload/shared.`);
    }
    if (files.has(logical)) throw new Error(`Target leaf '${root}' has a logical payload collision at '${logical}'.`);
    files.set(logical, { fullPath, sha256: item.sha256, size: item.size, nativeAsset: classification.nativeAsset });
  }
  const actualPaths = (await enumerateCanonicalFiles(root)).filter((path) => path !== INDEX_PATH);
  if (actualPaths.length !== indexedPaths.size || actualPaths.some((path) => !indexedPaths.has(path))) {
    throw new Error(`Target leaf '${root}' content index does not exactly cover its canonical files.`);
  }
  if (!files.has(normalizeLogicalPath(target.entryPoint))) {
    throw new Error(`Target leaf '${root}' does not contain its declared entry point '${target.entryPoint}'.`);
  }
  await validateBundledDescriptorFiles(manifest.contractBundles, files, root);
  const projection = files.get(MODEL_PROJECTION_PATH);
  if (!projection || !(await readProjectDescriptorFile(root, projection.fullPath, "Leaf model projection", 4_194_304))
    .equals(packageProjectionBytes(project, manifest.contractBundles.map(bundle => bundle.contractId))))
    throw new Error("Target leaf model projection differs from the complete fresh role-scoped source/artifact projection.");
  return { root, manifest, files };
}
function assertProjectionPath(path: string): void {
  if (path.toLowerCase().endsWith("/" + MODEL_PROJECTION_PATH) && path !== MODEL_PROJECTION_ARCHIVE_PATH)
    throw new Error("Model projection sidecar is reserved to its exact package-shared path; role/RID and case shadows are forbidden.");
}

function validateManifestMetadata(
  manifest: PackageManifest,
  targetRoles: ReadonlySet<PackageRole>,
  label: string,
  expected?: PackageManifest,
): void {
  const root = manifestRecord(manifest, label);
  const importProperties = ["packageArtifactImports", "packageApiImports", "providerImports", "platformImports"];
  manifestOnly(root, label,
    "archiveFormatVersion", "manifestVersion", "id", "name", "version", "dependsOn", "targets",
    "contractBundles", "usesContracts", "provides",
    ...["summary", ...importProperties].filter((name) => Object.hasOwn(root, name)));
  for (const name of importProperties) {
    if (!Object.hasOwn(root, name)) continue;
    const imports = root[name];
    if (!Array.isArray(imports)) throw new Error(`${label} ${name} must be an array when present.`);
    if (imports.length > 0 && !expected) {
      throw new Error(`${label} nonempty ${name} is not supported by descriptor-first Node package tooling.`);
    }
  }

  if (!Array.isArray(manifest.dependsOn) || manifest.dependsOn.length > 128) {
    throw new Error(`${label} dependsOn must be an array with at most 128 entries.`);
  }
  const dependencies = new Set<string>();
  for (const [index, dependency] of manifest.dependsOn.entries()) {
    const dependencyLabel = `${label} dependsOn[${index}]`;
    const value = manifestRecord(dependency, dependencyLabel);
    manifestOnly(value, dependencyLabel, "packageId", "versionRange", "presence");
    if (typeof dependency.packageId !== "string" || !isPackageId(dependency.packageId)
      || typeof dependency.versionRange !== "string"
      || dependency.presence !== "required" && dependency.presence !== "optional") {
      throw new Error(`${dependencyLabel} must declare a canonical package id, version range, and presence 'required' or 'optional'.`);
    }
    validateDependencyVersionRange(dependency.versionRange, `${dependencyLabel}.versionRange`);
    if (dependency.presence === "optional" && !expected) {
      throw new Error(`${dependencyLabel} optional dependencies are not supported by descriptor-first Node package tooling.`);
    }
    if (dependencies.has(dependency.packageId)) {
      throw new Error(`${label} declares dependency package id '${dependency.packageId}' more than once.`);
    }
    dependencies.add(dependency.packageId);
  }

  if (!Array.isArray(manifest.contractBundles)
    || !Array.isArray(manifest.usesContracts)
    || !Array.isArray(manifest.provides)) {
    throw new Error(`${label} RPC metadata must contain contractBundles, usesContracts, and provides arrays.`);
  }
  if (manifest.contractBundles.length > 128 || manifest.usesContracts.length > 128 || manifest.provides.length > 128) {
    throw new Error(`${label} RPC metadata exceeds the 128-entry per-section limit.`);
  }

  const bundles = new Map<string, ContractBundleManifest>();
  const descriptorPaths = new Map<string, string>();
  for (const [index, item] of manifest.contractBundles.entries()) {
    const bundleLabel = `${label} contractBundles[${index}]`;
    const value = manifestRecord(item, bundleLabel);
    manifestOnly(value, bundleLabel,
      "contractId", "version", "descriptorPath", "descriptorSha256", "abiSha256", "exposure",
      "source", "authorityKind", "authorityId",
      ...(value.source === "imported" ? ["selectedAuthorityRelease", "selectedAuthorityContentIdentity"] : []));
    const bundle = item as ContractBundleManifest;
    if (bundle.source !== "owned" && bundle.source !== "imported"
      || bundle.authorityKind !== "package" && bundle.authorityKind !== "platform"
      || typeof bundle.authorityId !== "string" || !isPackageId(bundle.authorityId)) {
      throw new Error(`${bundleLabel} must declare canonical source, authorityKind, and authorityId provenance.`);
    }
    if (bundle.source === "owned") {
      if (bundle.authorityKind !== "package" || bundle.authorityId !== manifest.id) {
        throw new Error(`${bundleLabel} owned bundles require authorityKind 'package' and authorityId equal to the manifest package id '${manifest.id}'.`);
      }
    } else {
      if (typeof bundle.selectedAuthorityRelease !== "string" || !isSemanticVersion(bundle.selectedAuthorityRelease)
        || !isSha256(bundle.selectedAuthorityContentIdentity)) {
        throw new Error(`${bundleLabel} imported bundles require a canonical selectedAuthorityRelease and selectedAuthorityContentIdentity.`);
      }
      if (bundle.authorityKind === "platform" && bundle.authorityId !== "sunder.core") {
        throw new Error(`${bundleLabel} imported platform bundles require authority 'sunder.core'.`);
      }
      if (!expected) throw new Error(`${bundleLabel} imported bundles are not supported by descriptor-first Node package tooling.`);
    }
    if (typeof bundle.contractId !== "string" || !isPackageId(bundle.contractId)
      || typeof bundle.version !== "string" || !isSemanticVersion(bundle.version)
      || typeof bundle.descriptorPath !== "string" || normalizeLogicalPath(bundle.descriptorPath) !== bundle.descriptorPath
      || !isSha256(bundle.descriptorSha256)
      || !isSha256(bundle.abiSha256)
      || bundle.exposure !== "package" && bundle.exposure !== "public") {
      throw new Error(`${label} contractBundles[${index}] is not a canonical RPC V1 contract bundle.`);
    }
    const identity = `${bundle.contractId}\0${bundle.version}`;
    if (bundles.has(identity)) {
      throw new Error(`${label} declares contract bundle '${bundle.contractId}' version '${bundle.version}' more than once.`);
    }
    bundles.set(identity, bundle);
    const foldedPath = bundle.descriptorPath.toLowerCase();
    const existingPath = descriptorPaths.get(foldedPath);
    if (existingPath !== undefined) {
      throw new Error(`${label} contract descriptor paths '${existingPath}' and '${bundle.descriptorPath}' collide.`);
    }
    descriptorPaths.set(foldedPath, bundle.descriptorPath);
  }

  const uses = new Set<string>();
  for (const [index, item] of manifest.usesContracts.entries()) {
    const value = manifestRecord(item, `${label} usesContracts[${index}]`);
    manifestOnly(value, `${label} usesContracts[${index}]`, "role", "contractId", "contractVersion", "abiSha256", "required", "actions");
    const use = item as ContractUseManifest;
    if (use.role !== "app" && use.role !== "runtime"
      || typeof use.contractId !== "string" || !isPackageId(use.contractId)
      || typeof use.contractVersion !== "string" || !isSemanticVersion(use.contractVersion)
      || !isSha256(use.abiSha256)
      || typeof use.required !== "boolean"
      || !Array.isArray(use.actions)
      || use.actions.length === 0
      || use.actions.length > 3
      || new Set(use.actions).size !== use.actions.length
      || use.actions.some((action) => action !== "discover" && action !== "invoke" && action !== "subscribe")) {
      throw new Error(`${label} usesContracts[${index}] is not a canonical exact RPC V1 contract use.`);
    }
    if (!targetRoles.has(use.role)) {
      throw new Error(`${label} contract use '${use.role}/${use.contractId}' has no target for role '${use.role}'.`);
    }
    const identity = `${use.role}\0${use.contractId}`;
    if (uses.has(identity)) {
      throw new Error(`${label} declares contract use '${use.role}/${use.contractId}' more than once.`);
    }
    uses.add(identity);
    const bundle = bundles.get(`${use.contractId}\0${use.contractVersion}`);
    if (bundle === undefined || bundle.abiSha256 !== use.abiSha256) {
      throw new Error(`${label} contract use '${use.role}/${use.contractId}' does not exactly match one local bundle version and ABI hash.`);
    }
  }

  const providers = new Set<string>();
  for (const [index, item] of manifest.provides.entries()) {
    const value = manifestRecord(item, `${label} provides[${index}]`);
    manifestOnly(value, `${label} provides[${index}]`, "providerId", "role", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "audience");
    const provider = item as ProviderManifest;
    if (typeof provider.providerId !== "string" || !isPackageId(provider.providerId)
      || provider.role !== "app" && provider.role !== "runtime"
      || typeof provider.contractId !== "string" || !isPackageId(provider.contractId)
      || typeof provider.contractVersion !== "string" || !isSemanticVersion(provider.contractVersion)
      || !isSha256(provider.descriptorSha256)
      || !isSha256(provider.abiSha256)
      || provider.audience !== "owner" && provider.audience !== "public") {
      throw new Error(`${label} provides[${index}] is not a canonical RPC V1 provider.`);
    }
    if (provider.role === "app") {
      throw new Error(`${label} provider 'app/${provider.providerId}' is invalid. App-role providers are not supported because App provider registration and serving are not available.`);
    }
    if (!targetRoles.has(provider.role)) {
      throw new Error(`${label} provider '${provider.role}/${provider.providerId}' has no target for role '${provider.role}'.`);
    }
    const identity = `${provider.role}\0${provider.providerId}`;
    if (providers.has(identity)) {
      throw new Error(`${label} declares provider '${provider.role}/${provider.providerId}' more than once.`);
    }
    providers.add(identity);
    const bundle = bundles.get(`${provider.contractId}\0${provider.contractVersion}`);
    if (bundle === undefined
      || bundle.descriptorSha256 !== provider.descriptorSha256
      || bundle.abiSha256 !== provider.abiSha256) {
      throw new Error(`${label} provider '${provider.role}/${provider.providerId}' does not exactly match one local bundle version and both hashes.`);
    }
    if (provider.audience === "public" && bundle.exposure !== "public") {
      throw new Error(`${label} provider '${provider.role}/${provider.providerId}' cannot have public audience for package-only contract '${provider.contractId}'.`);
    }
  }
  // Structural/identity checks remain authoritative and actionable before the
  // final source equality fence; a malformed leaf must not mask its own defect.
  if (expected) {
    const projection = (value: PackageManifest) => ({ dependsOn: value.dependsOn, contractBundles: value.contractBundles, usesContracts: value.usesContracts,
      provides: value.provides, packageArtifactImports: value.packageArtifactImports ?? [], packageApiImports: value.packageApiImports ?? [],
      providerImports: value.providerImports ?? [], platformImports: value.platformImports ?? [] });
    if (canonicalizeDescriptor(asJsonValue(projection(manifest))) !== canonicalizeDescriptor(asJsonValue(projection(expected))))
      throw new Error(`${label} RPC/import metadata differs from the complete fresh source and lock projection.`);
  }
}

async function validateBundledDescriptorFiles(
  bundles: readonly ContractBundleManifest[],
  files: ReadonlyMap<string, LeafFile>,
  root: string,
): Promise<void> {
  for (const bundle of bundles) {
    const file = files.get(bundle.descriptorPath);
    if (file === undefined) {
      throw new Error(`Target leaf '${root}' is missing bundled contract descriptor '${bundle.descriptorPath}'.`);
    }
    const bytes = await readFile(file.fullPath);
    let descriptor;
    try {
      descriptor = parseRpcContractDescriptor(bytes);
    } catch (error) {
      throw new Error(`Target leaf '${root}' contract descriptor '${bundle.descriptorPath}' is invalid.`, { cause: error });
    }
    const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
    if (!bytes.equals(canonicalBytes)) {
      throw new Error(`Target leaf '${root}' contract descriptor '${bundle.descriptorPath}' is not the canonical immutable descriptor snapshot.`);
    }
    if (descriptor.contractId !== bundle.contractId
      || descriptor.version !== bundle.version
      || sha256(canonicalBytes) !== bundle.descriptorSha256
      || file.sha256 !== bundle.descriptorSha256
      || rpcAbiSha256(descriptor) !== bundle.abiSha256) {
      throw new Error(`Target leaf '${root}' contract descriptor '${bundle.descriptorPath}' does not match its exact V1 bundle identity and hashes.`);
    }
  }
}

function manifestRecord(value: unknown, label: string): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function manifestOnly(value: Readonly<Record<string, unknown>>, label: string, ...allowed: readonly string[]): void {
  const names = new Set(allowed);
  for (const name of Object.keys(value)) {
    if (!names.has(name)) throw new Error(`${label} contains unknown property '${name}'.`);
  }
  if (Object.keys(value).length !== allowed.length || allowed.some((name) => !(name in value))) {
    throw new Error(`${label} must contain exactly ${allowed.join(", ")}.`);
  }
}

function isSha256(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{64}$/u.test(value);
}

function validateTarget(
  target: TargetManifest,
  packageId: string,
  root: string,
  config: SunderNodePackageConfig,
): void {
  const value = manifestRecord(target, `Target leaf '${root}' target`);
  const commonProperties = [
    "role",
    "rid",
    "kind",
    "entryPoint",
    "targetFramework",
    "sdkVersion",
    "requiredHostCapabilities",
  ] as const;
  const allowedProperties = new Set([...commonProperties, "executionRuntime", "views"]);
  for (const name of Object.keys(value)) {
    if (!allowedProperties.has(name as typeof commonProperties[number] | "executionRuntime" | "views")) {
      throw new Error(`Target leaf '${root}' target contains unknown property '${name}'.`);
    }
  }
  if (target.role === "runtime" && target.kind !== "worker") {
    throw new Error(`Target leaf '${root}' Runtime target kind '${target.kind}' is invalid; Manifest V1 Runtime targets must use 'worker'.`);
  }
  const compatibility = packageToolCompatibility();
  const expectedCapabilities = target.role === "app"
    ? webTargetCapabilities(config)
    : workerTargetCapabilities(config);
  const capabilitiesMatch = Array.isArray(target.requiredHostCapabilities)
    && target.requiredHostCapabilities.length === expectedCapabilities.length
    && target.requiredHostCapabilities.every((capability, index) => capability === expectedCapabilities[index]);
  if (target.role === "runtime" && !capabilitiesMatch) {
    throw new Error(`Target leaf '${root}' Runtime target requiredHostCapabilities must exactly match [${expectedCapabilities.join(", ")}].`);
  }
  const common = SUPPORTED_RIDS.includes(target.rid as SunderRid)
    && target.sdkVersion === compatibility.sdkVersion
    && capabilitiesMatch
    && normalizeLogicalPath(target.entryPoint) === target.entryPoint;
  const workerTarget = target.role === "runtime"
    && target.kind === "worker"
    && target.entryPoint === NODE_WORKER_ENTRY_POINT
    && target.targetFramework === "node24"
    && target.views === undefined
    && executionRuntimeMatches(target.executionRuntime);
  const webTarget = target.role === "app"
    && target.kind === "web"
    && target.targetFramework === "web"
    && target.executionRuntime === undefined
    && Array.isArray(target.views)
    && target.views.length > 0;
  if (!common || !workerTarget && !webTarget) {
    throw new Error(`Target leaf '${root}' does not declare one canonical supported worker or web target.`);
  }
  if (workerTarget) {
    manifestOnly(value, `Target leaf '${root}' target`, ...commonProperties, "executionRuntime");
    const executionRuntime = manifestRecord(target.executionRuntime, `Target leaf '${root}' target executionRuntime`);
    manifestOnly(executionRuntime, `Target leaf '${root}' target executionRuntime`, "id", "versionRange", "moduleAbi");
  } else {
    manifestOnly(value, `Target leaf '${root}' target`, ...commonProperties, "views");
    validateTargetViews(target.views!, packageId, root);
  }
}

function executionRuntimeMatches(value: ExecutionRuntimeRequirement | undefined): boolean {
  return value?.id === NODE_EXECUTION_RUNTIME.id
    && value.versionRange === NODE_EXECUTION_RUNTIME.versionRange
    && value.moduleAbi === NODE_EXECUTION_RUNTIME.moduleAbi;
}

function validateTargetViews(views: readonly AppViewConfig[], packageId: string, root: string): void {
  if (views.length > 32 || !isPackageId(packageId)) {
    throw new Error(`Target leaf '${root}' does not declare canonical web views.`);
  }
  const ids = new Set<string>();
  const routes = new Set<string>();
  for (const [index, view] of views.entries()) {
    const label = `Target leaf '${root}' target views[${index}]`;
    const value = manifestRecord(view, label);
    const properties = ["viewId", "displayName", "route", "defaultPlacement", "showInHotbar"] as const;
    if (Object.hasOwn(value, "icon")) manifestOnly(value, label, ...properties, "icon");
    else manifestOnly(value, label, ...properties);
    if (typeof view.viewId !== "string"
      || !isPackageId(view.viewId)
      || !view.viewId.startsWith(`${packageId}.`)
      || typeof view.displayName !== "string"
      || view.displayName.length === 0
      || view.displayName.length > 128
      || view.displayName.trim() !== view.displayName
      || /[\u0000-\u001f\u007f-\u009f]/u.test(view.displayName)
      || typeof view.route !== "string"
      || !canonicalWebRoute(view.route)
      || view.icon !== undefined
        && (typeof view.icon !== "string" || normalizeLogicalPath(view.icon) !== view.icon)
      || !["leftTop", "middle", "rightTop", "leftBottom", "rightBottom"].includes(view.defaultPlacement)
      || typeof view.showInHotbar !== "boolean") {
      throw new Error(`${label} is not canonical.`);
    }
    const foldedId = view.viewId.toLowerCase();
    const foldedRoute = view.route.toLowerCase();
    if (!ids.add(foldedId)) throw new Error(`${label} has a duplicate or case-colliding viewId.`);
    if (!routes.add(foldedRoute)) throw new Error(`${label} has a duplicate or case-colliding route.`);
  }
}

function canonicalWebRoute(value: string): boolean {
  if (value === "/") return true;
  if (!value.startsWith("/") || value.endsWith("/") || value.includes("\\") || value.includes("?") || value.includes("#") || value.includes("%")) return false;
  return value.slice(1).split("/").every((segment) => segment.length > 0 && segment !== "." && segment !== ".." && /^[A-Za-z0-9._~-]+$/u.test(segment));
}

function canonicalArchivePath(value: unknown): string {
  if (typeof value !== "string" || !isArchiveRelativePath(value, 240, 32)) {
    throw new Error("Target leaf content index contains an invalid canonical archive path.");
  }
  if (!value.startsWith("manifest/") && !value.startsWith("payload/")) {
    throw new Error(`Target leaf content index path '${value}' is outside canonical package roots.`);
  }
  return value;
}

function validatePortablePathCollisions(paths: readonly string[]): void {
  const entries = new Map<string, { readonly path: string; readonly kind: "directory" | "file" }>();
  for (const path of paths) {
    const segments = path.split("/");
    for (let index = 1; index <= segments.length; index++) {
      const candidate = segments.slice(0, index).join("/");
      const kind = index === segments.length ? "file" : "directory";
      const folded = candidate.toLowerCase();
      const existing = entries.get(folded);
      if (existing !== undefined && (existing.path !== candidate || existing.kind !== kind)) {
        throw new Error(`Canonical package paths '${existing.path}' and '${candidate}' collide on portable filesystems.`);
      }
      entries.set(folded, { path: candidate, kind });
    }
  }
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

function validateConfiguredNativeAssetLeaves(
  leaves: readonly Leaf[],
  config: SunderNodePackageConfig,
): void {
  const configuredPaths = new Map<string, Set<SunderRid>>();
  for (const rid of SUPPORTED_RIDS) {
    for (const asset of config.nativeAssets?.[rid] ?? []) {
      const rids = configuredPaths.get(asset.targetPath) ?? new Set<SunderRid>();
      rids.add(rid);
      configuredPaths.set(asset.targetPath, rids);
    }
  }
  for (const leaf of leaves) {
    const target = single(leaf.manifest.targets, "Target leaf must contain one exact target for aggregation.");
    if (target.role !== "runtime") continue;
    const rid = target.rid as SunderRid;
    const expectedPaths = new Set((config.nativeAssets?.[rid] ?? []).map((asset) => asset.targetPath));
    for (const expectedPath of expectedPaths) {
      if (!leaf.files.has(expectedPath)) {
        throw new Error(`Target leaf '${leaf.root}' is missing configured exact-RID native asset '${expectedPath}'.`);
      }
    }
    for (const [path, configuredRids] of configuredPaths) {
      if (leaf.files.has(path) && !configuredRids.has(rid)) {
        throw new Error(`Target leaf '${leaf.root}' contains native asset '${path}' configured for a different exact RID.`);
      }
    }
  }
}

function isExactRidNativeAsset(
  logicalPath: string,
  occurrences: readonly Leaf[],
  config: SunderNodePackageConfig,
): boolean {
  if (SUPPORTED_RIDS.some((rid) => (
    config.nativeAssets?.[rid]?.some((asset) => asset.targetPath === logicalPath) ?? false
  ))) return true;
  return occurrences.some((leaf) => leaf.files.get(logicalPath)?.nativeAsset === true);
}

async function classifyRuntimePayloadFile(
  logicalPath: string,
  fullPath: string,
  rid: SunderRid,
  config: SunderNodePackageConfig,
  knownSha256?: string,
): Promise<RuntimePayloadClassification> {
  if (isForbiddenExecutionRuntimePath(logicalPath)) {
    return runtimePayloadClassification(`its path identifies a Node or .NET runtime/toolchain asset`);
  }

  const binary = await inspectNativeBinary(fullPath, rid);
  if (binary.kind === "executable") return runtimePayloadClassification(binary.detail);
  if (binary.kind === "managed-assembly") return runtimePayloadClassification(binary.detail);
  if (binary.kind === "toolchain") return runtimePayloadClassification(binary.detail);
  if (binary.kind === "invalid") return runtimePayloadClassification(`invalid native binary metadata: ${binary.detail}`);

  const activeNodeMatch = await matchActiveNodeExecutable(fullPath, knownSha256);
  if (activeNodeMatch !== null) return runtimePayloadClassification(activeNodeMatch);

  const configured = config.nativeAssets?.[rid]?.some((asset) => asset.targetPath === logicalPath) ?? false;
  const nativeAsset = configured || binary.kind === "shared-library" || isNativeAssetFileName(logicalPath);
  if (!nativeAsset) return packagePayloadClassification();

  const runtimeFingerprint = await detectRuntimeBinaryFingerprint(fullPath);
  if (runtimeFingerprint !== null) {
    return runtimePayloadClassification(`${runtimeFingerprint} content in a native asset`);
  }
  return { runtimeOwned: false, nativeAsset: true, detail: binary.detail };
}

function isNativeAssetFileName(logicalPath: string): boolean {
  const fileName = basename(logicalPath).toLowerCase();
  return fileName.endsWith(".dll")
    || fileName.endsWith(".dylib")
    || fileName.endsWith(".exe")
    || fileName.endsWith(".node")
    || fileName.endsWith(".so")
    || fileName.includes(".so.");
}

async function matchActiveNodeExecutable(path: string, knownSha256?: string): Promise<string | null> {
  const candidate = await stat(path);
  const active = await activeNodeExecutableMetadata();
  if (candidate.size !== active.size) return null;

  const activeHashes = await activeNodeExecutableHashes();
  const candidateSha256 = knownSha256 ?? await hashFile(path);
  if (candidateSha256 === activeHashes.sha256) return "the active Node executable's exact SHA-256 content identity";
  if (candidate.size <= NODE_EXECUTABLE_HEADER_EVIDENCE_BYTES) return null;
  if (await hashFileFrom(path, NODE_EXECUTABLE_HEADER_EVIDENCE_BYTES) === activeHashes.afterHeaderSha256) {
    return "the active Node executable's header-independent content identity";
  }
  return null;
}

function activeNodeExecutableMetadata(): Promise<ActiveNodeExecutableMetadata> {
  activeNodeExecutableMetadataPromise ??= (async () => {
    const path = await realpath(process.execPath);
    return { path, size: (await stat(path)).size };
  })();
  return activeNodeExecutableMetadataPromise;
}

function activeNodeExecutableHashes(): Promise<ActiveNodeExecutableHashes> {
  activeNodeExecutableHashesPromise ??= (async () => {
    const executable = await activeNodeExecutableMetadata();
    return {
      sha256: await hashFile(executable.path),
      afterHeaderSha256: executable.size > NODE_EXECUTABLE_HEADER_EVIDENCE_BYTES
        ? await hashFileFrom(executable.path, NODE_EXECUTABLE_HEADER_EVIDENCE_BYTES)
        : "",
    };
  })();
  return activeNodeExecutableHashesPromise;
}

function runtimePayloadClassification(detail: string): RuntimePayloadClassification {
  return { runtimeOwned: true, nativeAsset: false, detail };
}

function packagePayloadClassification(): RuntimePayloadClassification {
  return { runtimeOwned: false, nativeAsset: false, detail: "package-owned data" };
}

function isForbiddenExecutionRuntimePath(path: string): boolean {
  const segments = path.toLowerCase().split("/");
  const fileName = segments.at(-1)!;
  return fileName === "node"
    || fileName === "node.exe"
    || fileName === "node.dll"
    || fileName === "node.lib"
    || fileName === "libnode.dylib"
    || fileName === "node.js-license.txt"
    || fileName === "node.js-license"
    || fileName === "node_pdb"
    || fileName === "node_etw_provider.man"
    || fileName === "node.h"
    || fileName.startsWith("libnode.so")
    || [
      "apphost", "apphost.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
      "libcoreclr.dylib", "libcoreclr.so", "libhostfxr.dylib", "libhostfxr.so",
      "libhostpolicy.dylib", "libhostpolicy.so", "libclrjit.dylib", "libclrjit.so",
      "clrjit.dll", "mscordaccore.dll", "mscordbi.dll", "system.private.corelib.dll",
      "dotnet", "dotnet.exe",
    ].includes(fileName)
    || segments.some((segment) => /^node-v\d/iu.test(segment))
    || segments.some((segment, index) => segment === "node_modules"
      && (segments[index + 1] === "npm" || segments[index + 1] === "corepack"))
    || segments.some((segment, index) => segment === "shared"
      && segments[index + 1] === "microsoft.netcore.app")
    || segments.some((segment, index) => segment === "packs"
      && segments[index + 1]?.startsWith("microsoft.netcore.app.runtime.") === true);
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
    if (entry.isDirectory() && !/\.(?:stage|backup)-[0-9a-f-]+$/u.test(entry.name)) {
      output.push(...await discoverLeaves(resolve(root, entry.name)));
    }
  }
  return output;
}

async function replaceGeneratedDirectory(
  path: string,
  markerContent: string,
  populate: (stagingPath: string) => Promise<void>,
  allowUnmarked = false,
  discoveryRoots: readonly string[] = [],
  readPaths: readonly string[] = [],
  validate?: () => Promise<void>,
): Promise<void> {
  const roots = discoveryRoots.map((root) => requireDiscoveryRoot(root, path));
  await withOutputLocks(roots.map(targetLeafDiscoveryKey), async () => {
    await withOutputLocks([path, ...readPaths], async () => {
      await replaceGeneratedDirectoryLocked(path, markerContent, populate, allowUnmarked, validate);
    });
  });
}

async function replaceGeneratedDirectoryLocked(
  path: string,
  markerContent: string,
  populate: (stagingPath: string) => Promise<void>,
  allowUnmarked = false,
  validate?: () => Promise<void>,
): Promise<void> {
  await validate?.();
  await assertReplaceableGeneratedDirectory(path, allowUnmarked);
  const staged = await stageGeneratedDirectory(path, markerContent, populate);
  const outputs = [staged.output, staged.marker];
  try {
    await validate?.();
    await commitStagedOutputs(outputs);
  } finally {
    await cleanupStagedOutputs(outputs);
  }
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

async function hashFileFrom(path: string, start: number): Promise<string> {
  const hash = createHash("sha256");
  const stream = createReadStream(path, { start });
  for await (const chunk of stream) hash.update(chunk as Buffer);
  return hash.digest("hex");
}

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
}

function rpcAbiSha256(descriptor: LoadedContract["descriptor"]): string {
  return createHash("sha256")
    .update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8")
    .update(canonicalRpcAbiProjectionBytes(descriptor))
    .digest("hex");
}

function normalizeLogicalPath(path: string): string {
  if (!isArchiveRelativePath(path, 200, 24)) {
    throw new Error(`Logical package path '${path}' is invalid.`);
  }
  return path;
}

function isInside(root: string, path: string): boolean {
  const traversal = relative(resolve(root), resolve(path));
  return traversal.length > 0 && traversal !== ".." && !traversal.startsWith(`..${sep}`) && !isAbsolute(traversal);
}

function requireDiscoveryRoot(root: string, leaf: string): string {
  if (root.trim().length === 0) throw new Error("Target leaf discovery root must be explicit and nonempty.");
  if (leaf.trim().length === 0) throw new Error("Target leaf path must be nonempty.");
  const normalizedRoot = resolve(root);
  const normalizedLeaf = resolve(leaf);
  if (pathIdentity(normalizedRoot) !== pathIdentity(normalizedLeaf) && !isInside(normalizedRoot, normalizedLeaf)) {
    throw new Error(`Target leaf '${normalizedLeaf}' is outside configured discovery root '${normalizedRoot}'.`);
  }
  return normalizedRoot;
}

function pathIdentity(path: string): string {
  const normalized = resolve(path);
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function packageMetadataFingerprint(manifest: PackageManifest | undefined): string {
  if (manifest === undefined) throw new Error("Aggregate input manifest is missing.");
  return canonicalizeDescriptor(asJsonValue({ ...manifest, targets: [], usesContracts: [], provides: [] }));
}

function rpcDeclarationsFingerprint(manifest: PackageManifest): string {
  return canonicalizeDescriptor(asJsonValue({
    usesContracts: manifest.usesContracts,
    provides: manifest.provides,
  }));
}

function unionDeclarations<T>(
  declarations: readonly T[],
  key: (declaration: T) => string,
  label: string,
  compare: (left: T, right: T) => number,
): T[] {
  const values = new Map<string, { readonly declaration: T; readonly canonical: string }>();
  for (const declaration of declarations) {
    const identity = key(declaration);
    const canonical = canonicalizeDescriptor(asJsonValue(declaration));
    const existing = values.get(identity);
    if (existing !== undefined && existing.canonical !== canonical) {
      throw new Error(`Aggregate target leaves declare conflicting ${label} metadata for '${identity.replace("\0", "/")}'.`);
    }
    if (existing === undefined) values.set(identity, { declaration, canonical });
  }
  return [...values.values()].map((item) => item.declaration).sort(compare);
}

function assertRoleScopedDeclarationSets(leaves: readonly Leaf[]): void {
  for (const role of ["app", "runtime"] as const) {
    const roleLeaves = leaves.filter((leaf) => single(
      leaf.manifest.targets,
      "Target leaf must contain one exact target for aggregation.",
    ).role === role);
    if (roleLeaves.length < 2) continue;

    const expectedUses = roleLeaves[0]!.manifest.usesContracts.map((use) => use.contractId).sort(ordinal);
    const expectedProviders = roleLeaves[0]!.manifest.provides.map((provider) => provider.providerId).sort(ordinal);
    for (const leaf of roleLeaves.slice(1)) {
      const uses = leaf.manifest.usesContracts.map((use) => use.contractId).sort(ordinal);
      const providers = leaf.manifest.provides.map((provider) => provider.providerId).sort(ordinal);
      if (canonicalizeDescriptor(asJsonValue(uses)) !== canonicalizeDescriptor(asJsonValue(expectedUses))) {
        throw new Error(`Aggregate target leaves must declare identical contract use sets for role '${role}'; declarations cannot be omitted from individual RID leaves.`);
      }
      if (canonicalizeDescriptor(asJsonValue(providers)) !== canonicalizeDescriptor(asJsonValue(expectedProviders))) {
        throw new Error(`Aggregate target leaves must declare identical provider sets for role '${role}'; declarations cannot be omitted from individual RID leaves.`);
      }
    }
  }
}

function compareTargets(left: TargetManifest, right: TargetManifest): number {
  const order = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
  return ordinal(left.role, right.role) || order.indexOf(left.rid) - order.indexOf(right.rid);
}

function compareContractUses(left: ContractUseManifest, right: ContractUseManifest): number {
  return ordinal(left.role, right.role) || ordinal(left.contractId, right.contractId);
}

function compareProviders(left: ProviderManifest, right: ProviderManifest): number {
  return ordinal(left.role, right.role) || ordinal(left.providerId, right.providerId);
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

async function writeDeterministicZip(root: string, outputPath: string, maximumBytes: number): Promise<number> {
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes <= 0 || maximumBytes > MAXIMUM_COMPRESSED_PACKAGE_BYTES) {
    throw new Error(`maximumPackageBytes must be from 1 through ${MAXIMUM_COMPRESSED_PACKAGE_BYTES}.`);
  }
  const files = await enumerateCanonicalFiles(root);
  if (files.length > MAXIMUM_ARCHIVE_ENTRIES) throw new Error(`Package archive contains more than ${MAXIMUM_ARCHIVE_ENTRIES} files.`);
  const centralParts: Buffer[] = [];
  let totalUncompressedBytes = 0;
  const temporaryPath = `${outputPath}.write-${randomUUID()}`;
  const handle = await open(temporaryPath, "wx");
  const writer = new BoundedFileWriter(handle, maximumBytes);
  try {
    for (const path of files) {
      canonicalArchivePath(path);
      const fullPath = resolve(root, ...path.split("/"));
      const observed = await stat(fullPath);
      const observedBytes = observed.size;
      if (observedBytes > MAXIMUM_ENTRY_BYTES) throw new Error(`Package archive entry '${path}' exceeds ${MAXIMUM_ENTRY_BYTES} bytes.`);
      if (observedBytes > MAXIMUM_TOTAL_BYTES - totalUncompressedBytes) throw new Error(`Package archive exceeds ${MAXIMUM_TOTAL_BYTES} uncompressed bytes.`);
      totalUncompressedBytes += observedBytes;

      const name = Buffer.from(path, "utf8");
      const localOffset = writer.bytes;
      const local = Buffer.alloc(30);
      local.writeUInt32LE(0x04034b50, 0);
      local.writeUInt16LE(20, 4);
      local.writeUInt16LE(0x0808, 6);
      local.writeUInt16LE(8, 8);
      local.writeUInt16LE(0, 10);
      local.writeUInt16LE(0x0021, 12);
      local.writeUInt16LE(name.length, 26);
      await writer.write(local);
      await writer.write(name);

      let crc = 0xffffffff;
      let uncompressedBytes = 0;
      const meter = new Transform({
        transform(chunk: Buffer, _encoding, callback) {
          const data = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
          crc = updateCrc32(crc, data);
          uncompressedBytes += data.length;
          callback(null, data);
        },
      });
      const compressedStart = writer.bytes;
      const sink = new Writable({
        write(chunk: Buffer, _encoding, callback) {
          void writer.write(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)).then(
            () => callback(),
            (error: unknown) => callback(error instanceof Error ? error : new Error(String(error))),
          );
        },
      });
      await pipeline(createReadStream(fullPath), meter, createDeflateRaw({ level: 9 }), sink);
      const compressedBytes = writer.bytes - compressedStart;
      const afterRead = await stat(fullPath);
      if (uncompressedBytes !== observedBytes || afterRead.size !== observedBytes || afterRead.mtimeMs !== observed.mtimeMs) {
        throw new Error(`Package archive source '${path}' changed while it was read.`);
      }
      if (uncompressedBytes > 0 && (compressedBytes === 0 || uncompressedBytes / compressedBytes > MAXIMUM_COMPRESSION_RATIO)) {
        throw new Error(`Package archive entry '${path}' exceeds the ${MAXIMUM_COMPRESSION_RATIO}:1 compression-ratio limit.`);
      }
      const checksum = (crc ^ 0xffffffff) >>> 0;
      const descriptor = Buffer.alloc(16);
      descriptor.writeUInt32LE(0x08074b50, 0);
      descriptor.writeUInt32LE(checksum, 4);
      descriptor.writeUInt32LE(compressedBytes, 8);
      descriptor.writeUInt32LE(uncompressedBytes, 12);
      await writer.write(descriptor);

      const central = Buffer.alloc(46);
      central.writeUInt32LE(0x02014b50, 0);
      central.writeUInt16LE(0x031e, 4);
      central.writeUInt16LE(20, 6);
      central.writeUInt16LE(0x0808, 8);
      central.writeUInt16LE(8, 10);
      central.writeUInt16LE(0, 12);
      central.writeUInt16LE(0x0021, 14);
      central.writeUInt32LE(checksum, 16);
      central.writeUInt32LE(compressedBytes, 20);
      central.writeUInt32LE(uncompressedBytes, 24);
      central.writeUInt16LE(name.length, 28);
      central.writeUInt32LE((0o100644 * 0x10000) >>> 0, 38);
      central.writeUInt32LE(localOffset, 42);
      centralParts.push(central, name);
    }
    const centralOffset = writer.bytes;
    for (const part of centralParts) await writer.write(part);
    const centralSize = writer.bytes - centralOffset;
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(files.length, 8);
    end.writeUInt16LE(files.length, 10);
    end.writeUInt32LE(centralSize, 12);
    end.writeUInt32LE(centralOffset, 16);
    await writer.write(end);
    const bytes = writer.bytes;
    await handle.close();
    await rename(temporaryPath, outputPath);
    return bytes;
  } catch (error) {
    await handle.close().catch(() => undefined);
    await rm(temporaryPath, { force: true });
    throw error;
  }
}

function updateCrc32(crc: number, data: Uint8Array): number {
  for (const byte of data) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) === 0 ? 0 : 0xedb88320);
  }
  return crc;
}

class BoundedFileWriter {
  readonly #handle: FileHandle;
  readonly #maximumBytes: number;
  #bytes = 0;

  public constructor(handle: FileHandle, maximumBytes: number) {
    this.#handle = handle;
    this.#maximumBytes = maximumBytes;
  }

  public get bytes(): number {
    return this.#bytes;
  }

  public async write(data: Uint8Array): Promise<void> {
    if (data.byteLength > this.#maximumBytes - this.#bytes) {
      throw new Error(`Package archive exceeds maximumPackageBytes ${this.#maximumBytes}.`);
    }
    let offset = 0;
    while (offset < data.byteLength) {
      const result = await this.#handle.write(data, offset, data.byteLength - offset, null);
      if (result.bytesWritten === 0) throw new Error("Package archive write made no progress.");
      offset += result.bytesWritten;
      this.#bytes += result.bytesWritten;
    }
  }
}

interface TargetManifest {
  readonly role: PackageRole;
  readonly rid: string;
  readonly kind: string;
  readonly entryPoint: string;
  readonly targetFramework: string;
  readonly sdkVersion: string;
  readonly requiredHostCapabilities: readonly string[];
  readonly executionRuntime?: ExecutionRuntimeRequirement;
  readonly views?: readonly AppViewConfig[];
}

interface ExecutionRuntimeRequirement {
  readonly id: string;
  readonly versionRange: string;
  readonly moduleAbi: string;
}

interface ContractBundleManifest {
  readonly contractId: string;
  readonly version: string;
  readonly descriptorPath: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
  readonly exposure: ContractExposure;
  readonly source: "owned" | "imported";
  readonly authorityKind: "package" | "platform";
  readonly authorityId: string;
  readonly selectedAuthorityRelease?: string;
  readonly selectedAuthorityContentIdentity?: string;
}

interface ContractUseManifest {
  readonly role: PackageRole;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly abiSha256: string;
  readonly required: boolean;
  readonly actions: readonly ("discover" | "invoke" | "subscribe")[];
}

interface ProviderManifest {
  readonly providerId: string;
  readonly role: PackageRole;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
  readonly audience: ProviderAudience;
}

interface RpcManifestDeclarations {
  readonly usesContracts: readonly ContractUseManifest[];
  readonly provides: readonly ProviderManifest[];
}

interface PackageManifest {
  readonly archiveFormatVersion: number;
  readonly manifestVersion: number;
  readonly id: string;
  readonly name: string;
  readonly summary?: string;
  readonly version: string;
  readonly dependsOn: readonly {
    readonly packageId: string;
    readonly versionRange: string;
    readonly presence: "required" | "optional";
  }[];
  readonly targets: readonly TargetManifest[];
  readonly contractBundles: readonly ContractBundleManifest[];
  readonly usesContracts: readonly ContractUseManifest[];
  readonly provides: readonly ProviderManifest[];
  readonly packageArtifactImports?: readonly unknown[];
  readonly packageApiImports?: readonly unknown[];
  readonly providerImports?: readonly unknown[];
  readonly platformImports?: readonly unknown[];
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

type PayloadOwnershipLayer = "packageSharedBytes" | "roleSharedBytes" | "exactRidBytes";

interface PackageOwnershipEvidence {
  readonly schemaVersion: 1;
  readonly packageId: string;
  readonly packageVersion: string;
  readonly archive: string;
  readonly archiveSha256: string;
  readonly archiveBytes: number;
  readonly payloadBytes: number;
  readonly metadataBytes: number;
  readonly runtimeClosureFileCount: number;
  readonly runtimeClosureBytes: number;
  readonly targets: readonly PackageTargetOwnershipEvidence[];
}

interface PackageTargetOwnershipEvidence {
  readonly role: PackageRole;
  readonly rid: string;
  readonly kind: string;
  readonly fileCount: number;
  readonly payloadBytes: number;
  readonly ownership: Readonly<Record<PayloadOwnershipLayer, number>>;
  readonly nativeAssets: readonly PackageNativeAssetEvidence[];
  readonly runtimeClosureFileCount: number;
  readonly runtimeClosureBytes: number;
}

interface PackageNativeAssetEvidence {
  readonly path: string;
  readonly bytes: number;
  readonly sha256: string;
}

interface LeafFile {
  readonly fullPath: string;
  readonly sha256: string;
  readonly size: number;
  readonly nativeAsset: boolean;
}

interface RuntimePayloadClassification {
  readonly runtimeOwned: boolean;
  readonly nativeAsset: boolean;
  readonly detail: string;
}

interface ActiveNodeExecutableMetadata {
  readonly path: string;
  readonly size: number;
}

interface ActiveNodeExecutableHashes {
  readonly sha256: string;
  readonly afterHeaderSha256: string;
}

interface Leaf {
  readonly root: string;
  readonly manifest: PackageManifest;
  readonly files: ReadonlyMap<string, LeafFile>;
}
