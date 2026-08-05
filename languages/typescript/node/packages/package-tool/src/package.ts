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
  canonicalizeDescriptor,
  isArchiveRelativePath,
  isVersionInRange,
  parseRpcContractDescriptor,
  type JsonValue,
} from "@sunder/sdk";
import type { BuildOptions, Metafile, Plugin as EsbuildPlugin } from "esbuild";
import { build as buildEsbuild, context, type BuildContext } from "esbuild";
import type { Plugin as VitePlugin } from "vite" with { "resolution-mode": "import" };
import { packageToolCompatibility } from "./compatibility";
import {
  asJsonValue,
  currentRid,
  loadProject,
  MAXIMUM_COMPRESSED_PACKAGE_BYTES,
  resolveInside,
  SUPPORTED_RIDS,
  type AppConfig,
  type AppViewConfig,
  type LoadedContract,
  type LoadedProject,
  type SunderNodePackageConfig,
  type SunderRid,
} from "./model";
import { renderBundledDependencyNotice } from "./notices";
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
const DEFAULT_MAX_EXECUTABLE_BYTES = 160 * 1024 * 1024;
const DEFAULT_MAX_PACKAGE_BYTES = MAXIMUM_COMPRESSED_PACKAGE_BYTES;
const MAXIMUM_ARCHIVE_ENTRIES = 4096;
const MAXIMUM_ENTRY_BYTES = 256 * 1024 * 1024;
const MAXIMUM_TOTAL_BYTES = 1024 * 1024 * 1024;
const MAXIMUM_METADATA_BYTES = 1024 * 1024;
const MAXIMUM_COMPRESSION_RATIO = 200;
const NODE_NOTICE_LOGICAL_PATH = "THIRD-PARTY-NOTICES/Node.js-LICENSE.txt";
const WORKER_NOTICE_LOGICAL_PATH = "THIRD-PARTY-NOTICES/Bundled-Worker-Dependencies.txt";
const WEB_NOTICE_LOGICAL_PATH = "THIRD-PARTY-NOTICES/Bundled-Web-Dependencies.txt";

export interface PreparedProject extends LoadedProject {
  readonly contracts: readonly LoadedContract[];
}

export interface DevBuildOptions {
  readonly projectPath: string;
  readonly watch: boolean;
}

interface DevWatchSession {
  readonly project: PreparedProject;
  readonly workRoot: string;
  readonly bundlePath: string;
  readonly workerNoticePath: string;
  readonly watchers: FSWatcher[];
  webRoot?: string;
  buildContext?: BuildContext;
  superseded: boolean;
}

export interface LeafBuildInput {
  readonly project: PreparedProject;
  readonly rid: SunderRid;
  readonly executablePath: string;
  readonly nodeNoticePath: string;
  readonly workerNoticePath: string;
  readonly discoveryRoot: string;
}

export interface LeafDiscoverySource {
  readonly path: string;
  readonly discoveryRoot: string;
}

export async function prepareProject(projectPath: string): Promise<PreparedProject> {
  const project = await loadProject(projectPath);
  const contracts: LoadedContract[] = [];
  const identities = new Set<string>();
  const descriptorPaths = new Map<string, string>();
  for (const configured of project.config.contracts) {
    const sourcePath = resolveInside(project.root, configured.path, "contract path");
    const descriptor = parseRpcContractDescriptor(await readFile(sourcePath));
    const canonical = canonicalizeDescriptor(descriptor as unknown as JsonValue);
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
    if (!contracts.some((contract) => contract.descriptor.contractId === use.contractId
      && isVersionInRange(contract.descriptor.version, use.versionRange))) {
      throw new Error(`Contract use '${use.contractId}' range '${use.versionRange}' has no matching configured local contract descriptor.`);
    }
  }
  return { ...project, contracts };
}

export async function buildDevPackage(options: DevBuildOptions): Promise<void> {
  const project = await prepareProject(options.projectPath);
  const rid = currentRid();
  const workRoot = resolve(project.outputRoot, ".sunder-dev-work");
  if (options.watch) {
    await withOutputLocks([workRoot], async () => {
      const sessionRoot = `${workRoot}.stage-${randomUUID()}`;
      await rm(sessionRoot, { recursive: true, force: true });
      await mkdir(sessionRoot, { recursive: true });
      try {
        await watchDevPackage(project, rid, sessionRoot);
      } finally {
        await rm(sessionRoot, { recursive: true, force: true });
      }
    });
    return;
  }
  await withOutputLocks([workRoot], async () => {
    await assertReplaceableGeneratedDirectory(workRoot, true);
    const staged = await stageGeneratedDirectory(
      workRoot,
      "Sunder Node dev work output v1\n",
      async (stagingRoot) => {
        const bundlePath = resolve(stagingRoot, "worker.cjs");
        const webRoot = project.config.app === undefined
          ? undefined
          : await buildWebAssets(project, resolve(stagingRoot, "web"));
        const result = await buildEsbuild(devBuildOptions(project, bundlePath));
        const workerNoticePath = await writeWorkerNotice(project, bundlePath, result.metafile);
        await emitDevTree(project, rid, bundlePath, workerNoticePath, webRoot);
      },
    );
    const stagedOutputs = [staged.output, staged.marker];
    try {
      await commitStagedOutputs(stagedOutputs);
    } finally {
      await cleanupStagedOutputs(stagedOutputs);
    }
  });
  process.stdout.write(`Sunder dev package: ${resolve(project.outputRoot, "sunder-dev")}\n`);
}

function devBuildOptions(project: PreparedProject, bundlePath: string, plugins: readonly EsbuildPlugin[] = []): BuildOptions {
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
    alias: { "@sunder/sdk": require.resolve("@sunder/sdk") },
    plugins: [contractIdentityPlugin(project), ...plugins],
  };
}

async function watchDevPackage(initialProject: PreparedProject, rid: SunderRid, workRoot: string): Promise<void> {
  let activeSession: DevWatchSession | undefined;
  let operationTail = Promise.resolve();
  let restartTimer: NodeJS.Timeout | undefined;
  let stopping = false;

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
    for (const watcher of session.watchers) watcher.close();
    await session.buildContext?.dispose();
  };
  const emit = (session: DevWatchSession, rebuildWeb: boolean): Promise<void> => {
    if (session.superseded || stopping) return Promise.resolve();
    return enqueue(async () => {
      if (session.superseded || activeSession !== session || stopping) return;
      if (rebuildWeb && session.project.config.app !== undefined) {
        session.webRoot = await buildWebAssets(session.project, resolve(session.workRoot, "web"));
      }
      if (session.superseded || activeSession !== session || stopping) return;
      await emitDevTree(session.project, rid, session.bundlePath, session.workerNoticePath, session.webRoot);
      process.stdout.write(`Sunder dev package: ${resolve(session.project.outputRoot, "sunder-dev")}\n`);
    });
  };
  const scheduleRestart = (): void => {
    if (stopping) return;
    if (activeSession !== undefined) activeSession.superseded = true;
    if (restartTimer !== undefined) clearTimeout(restartTimer);
    restartTimer = setTimeout(() => {
      restartTimer = undefined;
      void enqueue(async () => {
        const previous = activeSession;
        const project = await prepareProject(initialProject.root);
        activeSession = undefined;
        await disposeSession(previous);
        activeSession = await createSession(project);
      }).catch(() => undefined);
    }, 100);
  };
  const createSession = async (project: PreparedProject): Promise<DevWatchSession> => {
    await mkdir(workRoot, { recursive: true });
    const session: DevWatchSession = {
      project,
      workRoot,
      bundlePath: resolve(workRoot, "worker.cjs"),
      workerNoticePath: resolve(workRoot, "worker.cjs.third-party-notices.txt"),
      webRoot: project.config.app === undefined
        ? undefined
        : await buildWebAssets(project, resolve(workRoot, "web")),
      watchers: [],
      superseded: false,
    };
    try {
      const contractPaths = new Map<string, Set<string>>();
      for (const contract of project.contracts) {
        const directory = dirname(contract.sourcePath);
        const names = contractPaths.get(directory) ?? new Set<string>();
        names.add(basename(contract.sourcePath));
        contractPaths.set(directory, names);
      }
      for (const [directory, names] of contractPaths) {
        const watcher = watchFiles(directory, (_event, fileName) => {
          if (fileName === null || names.has(fileName.toString())) scheduleRestart();
        });
        watcher.on("error", report);
        session.watchers.push(watcher);
      }
      if (project.config.app !== undefined) {
        const appRoot = resolveAppRoot(project);
        const structuralFiles = new Set(
          [project.configPath, ...project.contracts.map((contract) => contract.sourcePath)]
            .map((path) => resolve(path)),
        );
        let appTimer: NodeJS.Timeout | undefined;
        const watcher = watchFiles(appRoot, { recursive: true }, (_event, fileName) => {
          if (fileName !== null) {
            const relativeName = fileName.toString().replaceAll("\\", "/");
            const changedPath = resolve(appRoot, relativeName);
            if (relativeName.split("/").some((segment) => segment.startsWith(".sunder-vite-"))
              || structuralFiles.has(changedPath)
              || changedPath === project.outputRoot
              || isInside(project.outputRoot, changedPath)) return;
          }
          if (appTimer !== undefined) clearTimeout(appTimer);
          appTimer = setTimeout(() => void emit(session, true).catch(() => undefined), 100);
        });
        watcher.on("error", report);
        watcher.on("close", () => {
          if (appTimer !== undefined) clearTimeout(appTimer);
        });
        session.watchers.push(watcher);
      }
      const outputPlugin: EsbuildPlugin = {
        name: "sunder-dev-output",
        setup(pluginBuild) {
          pluginBuild.onEnd(async (result) => {
            if (result.errors.length !== 0) return;
            await writeWorkerNotice(project, session.bundlePath, result.metafile);
            await emit(session, false);
          });
        },
      };
      activeSession = session;
      session.buildContext = await context(devBuildOptions(project, session.bundlePath, [outputPlugin]));
      await session.buildContext.watch();
      return session;
    } catch (error) {
      await disposeSession(session);
      if (activeSession === session) activeSession = undefined;
      throw error;
    }
  };

  const configWatcher = watchFiles(initialProject.root, { recursive: true }, (_event, fileName) => {
    if (fileName === null) {
      if (activeSession === undefined || activeSession.superseded) scheduleRestart();
      return;
    }
    const relativeName = fileName.toString().replaceAll("\\", "/");
    if (relativeName === basename(initialProject.configPath)) {
      scheduleRestart();
      return;
    }
    if (activeSession !== undefined && !activeSession.superseded) return;
    const changedPath = resolve(initialProject.root, relativeName);
    if (relativeName.split("/").some((segment) => segment === "node_modules"
      || segment === ".git"
      || segment.startsWith(".sunder-"))
      || changedPath === initialProject.outputRoot
      || isInside(initialProject.outputRoot, changedPath)) return;
    scheduleRestart();
  });
  configWatcher.on("error", report);
  try {
    activeSession = await createSession(initialProject);
    process.stdout.write("Watching package sources. Sunder will drain and restart the exact package activation after each canonical dev output change.\n");
    await new Promise<void>((resolvePromise) => {
      const stop = (): void => {
        if (stopping) return;
        stopping = true;
        process.off("SIGINT", stop);
        process.off("SIGTERM", stop);
        if (restartTimer !== undefined) clearTimeout(restartTimer);
        configWatcher.close();
        const session = activeSession;
        activeSession = undefined;
        void enqueue(() => disposeSession(session)).finally(resolvePromise);
      };
      process.once("SIGINT", stop);
      process.once("SIGTERM", stop);
    });
  } finally {
    configWatcher.close();
    await disposeSession(activeSession);
    await operationTail;
  }
}

export async function bundleProduction(project: PreparedProject, rid: SunderRid, outputPath: string): Promise<string> {
  await mkdir(dirname(outputPath), { recursive: true });
  const result = await buildEsbuild({
    absWorkingDir: project.root,
    entryPoints: [resolveInside(project.root, project.config.entry, "entry")],
    outfile: outputPath,
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node24.18",
    sourcemap: false,
    metafile: true,
    minify: true,
    legalComments: "none",
    logLevel: "info",
    alias: { "@sunder/sdk": require.resolve("@sunder/sdk") },
    plugins: [contractIdentityPlugin(project)],
  });
  return await writeWorkerNotice(project, outputPath, result.metafile);
}

export async function buildWebTarget(projectPath: string, configuredRid?: SunderRid): Promise<string | null> {
  const project = await prepareProject(projectPath);
  if (project.config.app === undefined) return null;
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
}

export async function writeTargetLeaf(input: LeafBuildInput): Promise<string> {
  const leafRoot = resolve(input.project.outputRoot, "targets", input.rid);
  const discoveryRoot = requireDiscoveryRoot(input.discoveryRoot, leafRoot);
  const executableName = `${safeFileName(input.project.config.id)}${input.rid.startsWith("win-") ? ".exe" : ""}`;
  const logicalEntryPoint = `bin/${executableName}`;
  await requireRegularFile(input.nodeNoticePath, "Node.js distribution notice");
  await requireRegularFile(input.workerNoticePath, "Bundled worker dependency notice");
  const compatibility = packageToolCompatibility();
  const manifest = createManifest(input.project, [{
    role: "runtime",
    rid: input.rid,
    kind: "worker",
    entryPoint: logicalEntryPoint,
    targetFramework: "node24",
    sdkVersion: compatibility.sdkVersion,
    requiredHostCapabilities: ["rpc.v1", compatibility.sdkBaselineCapability],
  }]);
  await replaceGeneratedDirectory(
    leafRoot,
    "Sunder Node target leaf generated output v1\n",
    async (stagingRoot) => {
      const targetExecutable = resolve(stagingRoot, "payload", "runtime", input.rid, "bin", executableName);
      await mkdir(dirname(targetExecutable), { recursive: true });
      await copyFile(input.executablePath, targetExecutable);
      await copyContracts(input.project, stagingRoot);
      const noticePath = resolve(stagingRoot, "payload", "runtime", input.rid, ...NODE_NOTICE_LOGICAL_PATH.split("/"));
      await mkdir(dirname(noticePath), { recursive: true });
      await copyFile(input.nodeNoticePath, noticePath);
      const workerNoticePath = resolve(stagingRoot, "payload", "runtime", input.rid, ...WORKER_NOTICE_LOGICAL_PATH.split("/"));
      await mkdir(dirname(workerNoticePath), { recursive: true });
      await copyFile(input.workerNoticePath, workerNoticePath);
      await writeCanonicalPackageMetadata(stagingRoot, manifest);
    },
    false,
    [discoveryRoot],
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
      await copyContracts(project, stagingRoot);
      await writeCanonicalPackageMetadata(stagingRoot, createManifest(project, [webTarget(app, rid)]));
    },
  );
}

export async function aggregateAndPack(
  projectPath: string,
  configuredSources: readonly LeafDiscoverySource[] = [],
): Promise<{ packageRoot: string; archivePath: string; bytes: number }> {
  const project = await prepareProject(projectPath);
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
  return await withOutputLocks(discoveryRoots.map(targetLeafDiscoveryKey), async () => {
    const discovered = (await Promise.all(sources.map((source) => discoverLeaves(source.path)))).flat();
    const leafRoots = [...new Map(discovered.map((path) => [pathIdentity(path), path])).values()];
    if (leafRoots.length === 0) throw new Error("No canonical package target leaves were found.");
    return await withOutputLocks([...leafRoots, packageRoot, archivePath], async () => {
      const leaves = await Promise.all(leafRoots.map(readLeaf));
      if (leaves.length > 12) throw new Error("A universal Sunder package may contain at most 12 exact targets.");
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
      await assertReplaceableGeneratedDirectory(packageRoot);
      await assertReplaceableGeneratedFile(archivePath, "package archive");
      const targets = leaves.flatMap((leaf) => leaf.manifest.targets).sort(compareTargets);
      const stagedPackage = await stageGeneratedDirectory(
        packageRoot,
        "Sunder package aggregate generated output v1\n",
        async (stagingRoot) => {
          const logicalPaths = new Set(leaves.flatMap((leaf) => [...leaf.files.keys()]));
          for (const logicalPath of [...logicalPaths].sort(ordinal)) {
            const occurrences = leaves.filter((leaf) => leaf.files.has(logicalPath));
            if (occurrences.length === leaves.length && identical(occurrences, logicalPath)) {
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
            for (const leaf of occurrences) {
              const target = single(leaf.manifest.targets, "Target leaf must contain one exact target for aggregation.");
              await copyLeafFile(leaf, logicalPath, resolve(stagingRoot, "payload", target.role, target.rid, ...logicalPath.split("/")));
            }
          }
          await writeCanonicalPackageMetadata(stagingRoot, createManifest(project, targets));
        },
      );
      const stagedArchive: StagedOutput = {
        finalPath: archivePath,
        stagedPath: `${archivePath}.stage-${randomUUID()}`,
      };
      const stagedOutputs = [stagedPackage.output, stagedPackage.marker, stagedArchive];
      try {
        const maximum = project.config.maximumPackageBytes ?? DEFAULT_MAX_PACKAGE_BYTES;
        const bytes = await writeDeterministicZip(stagedPackage.output.stagedPath, stagedArchive.stagedPath, maximum);
        await commitStagedOutputs(stagedOutputs);
        process.stdout.write(`Sunder package size: ${bytes} bytes (${targets.length} exact RID target${targets.length === 1 ? "" : "s"})\n`);
        return { packageRoot, archivePath, bytes };
      } finally {
        await cleanupStagedOutputs(stagedOutputs);
      }
    });
  });
}

export function maximumExecutableBytes(config: SunderNodePackageConfig): number {
  return config.maximumExecutableBytes ?? DEFAULT_MAX_EXECUTABLE_BYTES;
}

async function emitDevTree(
  project: PreparedProject,
  rid: SunderRid,
  bundlePath: string,
  workerNoticeSourcePath: string,
  webRoot?: string,
): Promise<void> {
  const root = resolve(project.outputRoot, "sunder-dev");
  const sidecarPath = `${root}.sunder-node-dev.json`;
  const logicalEntryPoint = "worker.cjs";
  const compatibility = packageToolCompatibility();
  const targets: TargetManifest[] = [{
    role: "runtime",
    rid,
    kind: "worker",
    entryPoint: logicalEntryPoint,
    targetFramework: `node${process.versions.node.split(".")[0]}`,
    sdkVersion: compatibility.sdkVersion,
    requiredHostCapabilities: ["rpc.v1", compatibility.sdkBaselineCapability],
  }];
  if (project.config.app !== undefined) targets.push(webTarget(project.config.app, rid));
  const manifest = createManifest(project, targets);
  await withOutputLocks([root, sidecarPath], async () => {
    await assertReplaceableGeneratedDirectory(root);
    await assertReplaceableGeneratedFile(sidecarPath, "Node dev sidecar");
    let contentIdentity = "";
    const stagedDirectory = await stageGeneratedDirectory(
      root,
      "Sunder Node dev generated output v1\n",
      async (stagingRoot) => {
        const destination = resolve(stagingRoot, "payload", "runtime", rid, logicalEntryPoint);
        await mkdir(dirname(destination), { recursive: true });
        await copyFile(bundlePath, destination);
        const workerNoticePath = resolve(stagingRoot, "payload", "runtime", rid, ...WORKER_NOTICE_LOGICAL_PATH.split("/"));
        await mkdir(dirname(workerNoticePath), { recursive: true });
        await copyFile(workerNoticeSourcePath, workerNoticePath);
        if (webRoot !== undefined) {
          await copyDirectory(webRoot, resolve(stagingRoot, "payload", "app", rid));
        }
        await copyContracts(project, stagingRoot);
        contentIdentity = (await writeCanonicalPackageMetadata(stagingRoot, manifest)).contentIdentity;
      },
    );
    const stagedSidecar: StagedOutput = {
      finalPath: sidecarPath,
      stagedPath: `${sidecarPath}.stage-${randomUUID()}`,
    };
    const stagedOutputs = [stagedDirectory.output, stagedDirectory.marker, stagedSidecar];
    try {
      await writeJson(stagedSidecar.stagedPath, {
        schemaVersion: 1,
        kind: "node",
        packageId: project.config.id,
        packageVersion: project.config.version,
        rid,
        entryPoint: logicalEntryPoint,
        nodePath: resolve(process.execPath),
        nodeVersion: process.version,
        contentIdentity,
      });
      await commitStagedOutputs(stagedOutputs);
    } finally {
      await cleanupStagedOutputs(stagedOutputs);
    }
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
    await writeFile(path, contract.canonical, "utf8");
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

async function buildWebAssets(project: PreparedProject, outputRoot: string): Promise<string> {
  await withOutputLocks([outputRoot], async () => {
    await buildWebAssetsLocked(project, outputRoot);
  });
  return outputRoot;
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

function webTarget(app: AppConfig, rid: SunderRid): TargetManifest {
  const compatibility = packageToolCompatibility();
  return {
    role: "app",
    rid,
    kind: "web",
    entryPoint: app.entryPoint,
    targetFramework: "web",
    sdkVersion: compatibility.sdkVersion,
    requiredHostCapabilities: ["rpc.v1", compatibility.sdkBaselineCapability, "views.v1"],
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
  for (const entry of entries) canonicalArchivePath(entry);
  const files: ContentIndexEntry[] = [];
  for (const entry of entries.filter((entry) => entry !== INDEX_PATH)) {
    const path = resolve(root, ...entry.split("/"));
    files.push({ path: entry, sha256: await hashFile(path), size: (await stat(path)).size });
  }
  files.sort((left, right) => ordinal(left.path, right.path));
  const indexPath = resolve(root, ...INDEX_PATH.split("/"));
  await writeJson(indexPath, { schemaVersion: 1, files });
  if ((await stat(indexPath)).size > MAXIMUM_METADATA_BYTES) throw new Error("Package content index exceeds the 1 MiB metadata limit.");
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
  const paths = output.sort(ordinal);
  for (const path of paths) canonicalArchivePath(path);
  validatePortablePathCollisions(paths);
  return paths;
}

async function readLeaf(root: string): Promise<Leaf> {
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
  if (manifest.archiveFormatVersion !== 1 || manifest.manifestVersion !== 1 || !Array.isArray(manifest.targets) || manifest.targets.length !== 1 || index.schemaVersion !== 1 || !Array.isArray(index.files)) {
    throw new Error(`Target leaf '${root}' is not a canonical Sunder package leaf.`);
  }
  if (index.files.length > MAXIMUM_ARCHIVE_ENTRIES) throw new Error(`Target leaf '${root}' contains too many indexed files.`);
  const target = manifest.targets[0]!;
  validateTarget(target, root);
  const files = new Map<string, LeafFile>();
  const indexedPaths = new Set<string>();
  let indexedBytes = 0;
  for (const item of index.files) {
    const archivePath = canonicalArchivePath(item.path);
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
  const compatibility = packageToolCompatibility();
  const expectedCapabilities = target.role === "app"
    ? ["rpc.v1", compatibility.sdkBaselineCapability, "views.v1"]
    : ["rpc.v1", compatibility.sdkBaselineCapability];
  const common = SUPPORTED_RIDS.includes(target.rid as SunderRid)
    && target.sdkVersion === compatibility.sdkVersion
    && Array.isArray(target.requiredHostCapabilities)
    && target.requiredHostCapabilities.length === expectedCapabilities.length
    && target.requiredHostCapabilities.every((value, index) => value === expectedCapabilities[index])
    && normalizeLogicalPath(target.entryPoint) === target.entryPoint;
  const workerTarget = target.role === "runtime"
    && (target.kind === "worker" || target.kind === "process")
    && target.targetFramework === "node24"
    && target.views === undefined;
  const webTarget = target.role === "app"
    && target.kind === "web"
    && target.targetFramework === "web"
    && Array.isArray(target.views)
    && target.views.length > 0;
  if (!common || !workerTarget && !webTarget) {
    throw new Error(`Target leaf '${root}' does not declare one canonical supported worker or web target.`);
  }
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
): Promise<void> {
  const roots = discoveryRoots.map((root) => requireDiscoveryRoot(root, path));
  await withOutputLocks(roots.map(targetLeafDiscoveryKey), async () => {
    await withOutputLocks([path, ...readPaths], async () => {
      await replaceGeneratedDirectoryLocked(path, markerContent, populate, allowUnmarked);
    });
  });
}

async function replaceGeneratedDirectoryLocked(
  path: string,
  markerContent: string,
  populate: (stagingPath: string) => Promise<void>,
  allowUnmarked = false,
): Promise<void> {
  await assertReplaceableGeneratedDirectory(path, allowUnmarked);
  const staged = await stageGeneratedDirectory(path, markerContent, populate);
  const outputs = [staged.output, staged.marker];
  try {
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

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
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

function contractIdentityPlugin(project: PreparedProject): EsbuildPlugin {
  return {
    name: "sunder-contract-identities",
    setup(build) {
      build.onResolve({ filter: /^sunder:contracts$/ }, () => ({ path: "identities", namespace: "sunder-contracts" }));
      build.onLoad({ filter: /^identities$/, namespace: "sunder-contracts" }, () => {
        const identities = Object.fromEntries(project.contracts.map((contract) => [contract.configuredPath, {
          contractId: contract.descriptor.contractId,
          contractVersion: contract.descriptor.version,
          contractSha256: contract.sha256,
        }]));
        return {
          loader: "js",
          contents: `
          const identities = Object.freeze(Object.fromEntries(
            Object.entries(${JSON.stringify(identities)}).map(([path, identity]) => [path, Object.freeze(identity)]),
          ));
          export function contractIdentity(path) {
            const identity = identities[path];
            if (identity === undefined) throw new Error(\`Unknown configured Sunder contract descriptor '\${path}'.\`);
            return identity;
          }
          `,
        };
      });
    },
  };
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
