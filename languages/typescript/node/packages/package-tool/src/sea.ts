import { randomBytes } from "node:crypto";
import {
  access,
  chmod,
  copyFile,
  lstat,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import { homedir } from "node:os";
import { basename, dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { spawn } from "node:child_process";
import { FrameDecoder, encodeFrame, type JsonValue } from "@sunder/sdk";
import { downloadBoundedFile, fetchBoundedText } from "./download";
import { currentRid, type SunderRid } from "./model";
import { hashFile, verifyCachedNode, writeNodeCacheVerification } from "./node-cache";
import {
  assertReplaceableGeneratedDirectory,
  cleanupStagedOutputs,
  commitStagedOutputs,
  stageGeneratedDirectory,
  withOutputLocks,
} from "./output";
import {
  bundleProduction,
  maximumExecutableBytes,
  prepareProject,
  writeTargetLeaf,
  type PreparedProject,
} from "./package";

const SEA_FUSE = "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2";
const DOWNLOAD_TIMEOUT_MILLISECONDS = 120_000;
const MAXIMUM_SHASUMS_BYTES = 2 * 1024 * 1024;
const MAXIMUM_NODE_DISTRIBUTION_BYTES = 256 * 1024 * 1024;
const MAXIMUM_NOTICE_BYTES = 2 * 1024 * 1024;

interface DistributionFile {
  readonly file: string;
  readonly sha256: string;
  readonly archive: boolean;
  readonly executable: string;
}

interface DistributionMetadata {
  readonly nodeVersion: "24.18.1";
  readonly baseUrl: string;
  readonly notice: Readonly<{
    url: string;
    sha256: string;
  }>;
  readonly targets: Readonly<Record<SunderRid, DistributionFile>>;
}

interface PinnedNodeDistribution {
  readonly executablePath: string;
  readonly noticePath: string;
}

export interface SeaBuildOptions {
  readonly projectPath: string;
  readonly rid?: SunderRid;
  readonly allowUnsignedWindows: boolean;
}

export async function buildSeaTarget(options: SeaBuildOptions): Promise<{ leafPath: string; executablePath: string; bytes: number }> {
  const rid = options.rid ?? currentRid();
  if (rid !== currentRid()) {
    throw new Error(`Node SEA target '${rid}' must be built and smoke-tested on its exact native runner '${currentRid()}'.`);
  }
  const project = await prepareProject(options.projectPath);
  const metadata = await readDistributionMetadata();
  const distribution = await acquirePinnedNode(metadata, rid);
  const pinnedNode = distribution.executablePath;
  const workRoot = resolve(project.outputRoot, ".sunder-sea", rid);
  return await withOutputLocks([workRoot], async () => {
    await assertReplaceableGeneratedDirectory(workRoot, true);
    let leafPath = "";
    let bytes = 0;
    const staged = await stageGeneratedDirectory(
      workRoot,
      "Sunder Node SEA work output v1\n",
      async (stagingRoot) => {
        const bundlePath = resolve(stagingRoot, "worker.cjs");
        const workerNoticePath = await bundleProduction(project, rid, bundlePath);
        const blobPath = resolve(stagingRoot, "sea-prep.blob");
        const seaConfigPath = resolve(stagingRoot, "sea-config.json");
        await writeFile(seaConfigPath, `${JSON.stringify({
          main: bundlePath,
          output: blobPath,
          disableExperimentalSEAWarning: true,
          useSnapshot: false,
          useCodeCache: false,
          execArgvExtension: "none",
        }, null, 2)}\n`, "utf8");
        await runChecked(pinnedNode, ["--experimental-sea-config", seaConfigPath], stagingRoot);
        const stagedExecutablePath = resolve(stagingRoot, `${safeExecutableName(project.config.id)}${rid.startsWith("win-") ? ".exe" : ""}`);
        await copyFile(pinnedNode, stagedExecutablePath);
        if (!rid.startsWith("win-")) await chmod(stagedExecutablePath, 0o755);
        await removeSignature(stagedExecutablePath, rid, options.allowUnsignedWindows);
        await injectBlob(stagedExecutablePath, blobPath, rid);
        await signExecutable(stagedExecutablePath, rid, options.allowUnsignedWindows);
        bytes = (await stat(stagedExecutablePath)).size;
        const maximum = maximumExecutableBytes(project.config);
        if (bytes > maximum) {
          throw new Error(`SEA executable is ${bytes} bytes and exceeds maximumExecutableBytes ${maximum}.`);
        }
        leafPath = await writeTargetLeaf({
          project,
          rid,
          executablePath: stagedExecutablePath,
          nodeNoticePath: distribution.noticePath,
          workerNoticePath,
          discoveryRoot: resolve(project.outputRoot, "targets"),
        });
      },
    );
    const stagedOutputs = [staged.output, staged.marker];
    try {
      await commitStagedOutputs(stagedOutputs);
    } finally {
      await cleanupStagedOutputs(stagedOutputs);
    }
    const executablePath = resolve(workRoot, `${safeExecutableName(project.config.id)}${rid.startsWith("win-") ? ".exe" : ""}`);
    process.stdout.write(`Sunder SEA size (${rid}): ${bytes} bytes\n`);
    return { leafPath, executablePath, bytes };
  });
}

export async function smokeSeaTarget(projectPath: string, rid = currentRid()): Promise<void> {
  if (rid !== currentRid()) throw new Error(`SEA smoke test for '${rid}' requires its exact native runner.`);
  const project = await prepareProject(projectPath);
  const executableName = `${safeExecutableName(project.config.id)}${rid.startsWith("win-") ? ".exe" : ""}`;
  const leafRoot = resolve(project.outputRoot, "targets", rid);
  const executablePath = resolve(leafRoot, "payload", "runtime", rid, "bin", executableName);
  const smokeRoot = resolve(project.outputRoot, ".sunder-smoke", rid);
  await withOutputLocks([leafRoot, smokeRoot], async () => {
    await access(executablePath);
    const activationId = randomBytes(16).toString("hex");
    const sessionId = randomBytes(16).toString("hex");
    await rm(smokeRoot, { recursive: true, force: true });
    await mkdir(smokeRoot, { recursive: true });
    const child = spawn(executablePath, [], {
      cwd: dirname(executablePath),
      stdio: ["pipe", "pipe", "pipe"],
      env: {
        SUNDER_WORKER_PROTOCOL: "sunder.worker.v1",
        SUNDER_PACKAGE_ID: project.config.id,
        SUNDER_PACKAGE_VERSION: project.config.version,
        SUNDER_ACTIVATION_ID: activationId,
        SUNDER_SESSION_ID: sessionId,
        SUNDER_PACKAGE_CONTENT_PATH: dirname(executablePath),
        SUNDER_PACKAGE_DATA_PATH: smokeRoot,
        SUNDER_PACKAGE_STATE_PATH: resolve(smokeRoot, "state.json"),
        SUNDER_PACKAGE_WORKING_PATH: dirname(executablePath),
        TMPDIR: smokeRoot,
        TMP: smokeRoot,
        TEMP: smokeRoot,
        ...(process.platform === "win32" ? { SystemRoot: process.env.SystemRoot ?? "C:\\Windows" } : { LANG: "C.UTF-8", LC_ALL: "C.UTF-8" }),
      },
    });
    let stderr = "";
    child.stderr.on("data", (chunk: Buffer) => {
      if (stderr.length < 16 * 1024) stderr += chunk.toString("utf8");
    });
    const messages = new SmokeMessageQueue();
    child.stdout.on("data", (chunk: Buffer) => messages.push(chunk));
    const providers = project.config.providers.map((provider) => {
      const contract = project.contracts.find((item) => item.descriptor.contractId === provider.contractId && item.descriptor.version === provider.contractVersion);
      if (contract === undefined) throw new Error(`Smoke provider '${provider.providerId}' contract is missing.`);
      return { ...provider, contractSha256: contract.sha256 };
    });
    child.stdin.write(encodeFrame({
      type: "host.hello",
      protocol: "sunder.worker.v1",
      protocolVersion: 1,
      challenge: "native-sea-smoke",
      packageId: project.config.id,
      packageVersion: project.config.version,
      activationId,
      sessionId,
      providers,
    }));
    try {
      const ready = await messages.next(15_000);
      if (ready.type !== "worker.ready" || ready.challenge !== "native-sea-smoke") {
        throw new Error(`SEA worker returned invalid ready envelope: ${JSON.stringify(ready)}`);
      }
      child.stdin.write(encodeFrame({ type: "host.activate", sessionGeneration: 1 }));
      const activated = await messages.next(5_000);
      if (activated.type !== "worker.activated") throw new Error("SEA worker did not acknowledge activation.");
      child.stdin.write(encodeFrame({ type: "host.shutdown", shutdownId: "native-smoke-shutdown", reason: "smoke" }));
      const shutdown = await messages.next(5_000);
      if (shutdown.type !== "worker.shutdown-ack") throw new Error("SEA worker did not acknowledge shutdown.");
      const code = await waitForExit(child, 5_000);
      if (code !== 0) throw new Error(`SEA worker exited with ${code}: ${stderr}`);
      process.stdout.write(`Sunder native SEA protocol smoke passed: ${rid}\n`);
    } finally {
      if (child.exitCode === null) child.kill("SIGKILL");
    }
  });
}

async function readDistributionMetadata(): Promise<DistributionMetadata> {
  const path = resolve(__dirname, "..", "node-distributions.json");
  const metadata = JSON.parse(await readFile(path, "utf8")) as DistributionMetadata;
  if (metadata.nodeVersion !== "24.18.1") throw new Error("Node distribution metadata must remain pinned to 24.18.1.");
  if (metadata.baseUrl !== "https://nodejs.org/dist/v24.18.1") {
    throw new Error("Node distribution metadata must use the exact pinned HTTPS release origin.");
  }
  if (metadata.notice.url !== "https://raw.githubusercontent.com/nodejs/node/v24.18.1/LICENSE"
    || !/^[0-9a-f]{64}$/u.test(metadata.notice.sha256)) {
    throw new Error("Node distribution notice metadata must remain pinned to the exact Node release license.");
  }
  return metadata;
}

async function acquirePinnedNode(metadata: DistributionMetadata, rid: SunderRid): Promise<PinnedNodeDistribution> {
  const distribution = metadata.targets[rid];
  if (distribution === undefined) throw new Error(`No pinned Node distribution metadata exists for '${rid}'.`);
  const cacheRoot = resolve(process.env.SUNDER_NODE_CACHE ?? resolve(homedir(), ".cache", "sunder", "node"), `v${metadata.nodeVersion}`, rid);
  const executablePath = resolveCacheChild(cacheRoot, distribution.executable, "Node executable");
  const downloadPath = resolve(cacheRoot, basename(distribution.file));
  const verificationPath = resolve(cacheRoot, "verified.json");
  return await withOutputLocks([cacheRoot], async () => {
    if (await verifyCachedNode({
      verificationPath,
      archivePath: downloadPath,
      executablePath,
      expectedVersion: metadata.nodeVersion,
      expectedFile: distribution.file,
      expectedArchiveSha256: distribution.sha256,
      expectedExecutable: distribution.executable,
      verifyVersion: () => verifyNodeVersion(executablePath, metadata.nodeVersion),
    })) {
      return { executablePath, noticePath: await acquireNodeNotice(metadata, cacheRoot) };
    }
    await rm(cacheRoot, { recursive: true, force: true });
    await mkdir(cacheRoot, { recursive: true });
    const sums = await fetchBoundedText(`${metadata.baseUrl}/SHASUMS256.txt`, {
      label: "Node SHA metadata download",
      maximumBytes: MAXIMUM_SHASUMS_BYTES,
      timeoutMilliseconds: DOWNLOAD_TIMEOUT_MILLISECONDS,
    });
    const expectedLine = `${distribution.sha256}  ${distribution.file}`;
    if (!sums.split(/\r?\n/u).includes(expectedLine)) {
      throw new Error(`Pinned SHA metadata for '${distribution.file}' does not match upstream Node ${metadata.nodeVersion}.`);
    }
    const distributionUrl = new URL(distribution.file, `${metadata.baseUrl}/`);
    if (distributionUrl.origin !== "https://nodejs.org"
      || !distributionUrl.pathname.startsWith("/dist/v24.18.1/")
      || distributionUrl.search !== ""
      || distributionUrl.hash !== "") {
      throw new Error(`Pinned Node distribution path '${distribution.file}' escapes the exact release origin.`);
    }
    await downloadBoundedFile(distributionUrl.toString(), downloadPath, {
      label: "Pinned Node distribution download",
      maximumBytes: MAXIMUM_NODE_DISTRIBUTION_BYTES,
      timeoutMilliseconds: DOWNLOAD_TIMEOUT_MILLISECONDS,
    });
    const actual = await hashFile(downloadPath);
    if (actual !== distribution.sha256) {
      await rm(downloadPath, { force: true });
      throw new Error(`Downloaded Node binary SHA-256 '${actual}' does not match pinned metadata '${distribution.sha256}'.`);
    }
    if (distribution.archive) await runChecked("tar", ["-xzf", downloadPath, "-C", cacheRoot], cacheRoot);
    else if (downloadPath !== executablePath) {
      await mkdir(dirname(executablePath), { recursive: true });
      await copyFile(downloadPath, executablePath);
    }
    await requireRegularFile(executablePath, "Extracted Node executable");
    if (!rid.startsWith("win-")) await chmod(executablePath, 0o755);
    await verifyNodeVersion(executablePath, metadata.nodeVersion);
    await writeNodeCacheVerification(verificationPath, {
      schemaVersion: 1,
      version: metadata.nodeVersion,
      file: distribution.file,
      archiveSha256: distribution.sha256,
      executable: distribution.executable,
      executableSha256: await hashFile(executablePath),
    });
    return { executablePath, noticePath: await acquireNodeNotice(metadata, cacheRoot) };
  });
}

async function acquireNodeNotice(metadata: DistributionMetadata, cacheRoot: string): Promise<string> {
  const noticePath = resolve(cacheRoot, "Node.js-LICENSE.txt");
  if (await isRegularFileWithHash(noticePath, metadata.notice.sha256)) return noticePath;
  await downloadBoundedFile(metadata.notice.url, noticePath, {
    label: "Pinned Node distribution notice download",
    maximumBytes: MAXIMUM_NOTICE_BYTES,
    timeoutMilliseconds: DOWNLOAD_TIMEOUT_MILLISECONDS,
  });
  const actual = await hashFile(noticePath);
  if (actual !== metadata.notice.sha256) {
    await rm(noticePath, { force: true });
    throw new Error(`Downloaded Node distribution notice SHA-256 '${actual}' does not match pinned metadata '${metadata.notice.sha256}'.`);
  }
  return noticePath;
}

async function verifyNodeVersion(executablePath: string, expected: string): Promise<void> {
  const result = await runChecked(executablePath, ["--version"], dirname(executablePath));
  if (result.stdout.trim() !== `v${expected}`) {
    throw new Error(`Pinned Node executable reported '${result.stdout.trim()}', expected 'v${expected}'.`);
  }
}

async function removeSignature(executablePath: string, rid: SunderRid, allowUnsignedWindows: boolean): Promise<void> {
  if (rid.startsWith("osx-")) {
    await runChecked("codesign", ["--remove-signature", executablePath], dirname(executablePath));
  } else if (rid.startsWith("win-")) {
    if (!allowUnsignedWindows && process.env.SUNDER_WINDOWS_SIGN_SCRIPT === undefined) {
      throw new Error("Windows SEA output may be unsigned only with explicit --allow-unsigned-windows or SUNDER_WINDOWS_SIGN_SCRIPT.");
    }
    await runOptional("signtool", ["remove", "/s", executablePath], dirname(executablePath));
  }
}

async function injectBlob(executablePath: string, blobPath: string, rid: SunderRid): Promise<void> {
  const packageJson = require.resolve("postject/package.json");
  const cli = resolve(dirname(packageJson), "dist", "cli.js");
  const argumentsList = [cli, executablePath, "NODE_SEA_BLOB", blobPath, "--sentinel-fuse", SEA_FUSE];
  if (rid.startsWith("osx-")) argumentsList.push("--macho-segment-name", "NODE_SEA");
  await runChecked(process.execPath, argumentsList, dirname(executablePath));
}

async function signExecutable(executablePath: string, rid: SunderRid, allowUnsignedWindows: boolean): Promise<void> {
  if (rid.startsWith("osx-")) {
    const identity = process.env.SUNDER_MACOS_SIGN_IDENTITY ?? "-";
    const argumentsList = ["--force", "--sign", identity];
    const entitlements = process.env.SUNDER_MACOS_ENTITLEMENTS;
    if (entitlements !== undefined) argumentsList.push("--entitlements", resolve(entitlements));
    argumentsList.push(executablePath);
    await runChecked("codesign", argumentsList, dirname(executablePath));
    await runChecked("codesign", ["--verify", "--strict", executablePath], dirname(executablePath));
  } else if (rid.startsWith("win-")) {
    const script = process.env.SUNDER_WINDOWS_SIGN_SCRIPT;
    if (script !== undefined) await runChecked(resolve(script), [executablePath], dirname(executablePath));
    else if (!allowUnsignedWindows) throw new Error("A Windows release signing hook is required.");
  }
}

async function runChecked(file: string, argumentsList: readonly string[], cwd: string): Promise<ProcessOutput> {
  return await new Promise<ProcessOutput>((resolvePromise, reject) => {
    const child = spawn(file, [...argumentsList], { cwd, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk: Buffer) => {
      if (stdout.length < 1024 * 1024) stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", (chunk: Buffer) => {
      if (stderr.length < 1024 * 1024) stderr += chunk.toString("utf8");
    });
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (code === 0) resolvePromise({ stdout, stderr });
      else reject(new Error(`${file} ${argumentsList.join(" ")} exited with ${code ?? signal}: ${stderr || stdout}`));
    });
  });
}

async function runOptional(file: string, argumentsList: readonly string[], cwd: string): Promise<void> {
  try {
    await runChecked(file, argumentsList, cwd);
  } catch (error) {
    process.stderr.write(`Optional signature removal command was unavailable or failed: ${error instanceof Error ? error.message : String(error)}\n`);
  }
}

function resolveCacheChild(root: string, value: string, label: string): string {
  const path = resolve(root, value);
  const traversal = relative(root, path);
  if (traversal.length === 0 || traversal === ".." || traversal.startsWith(`..${sep}`) || isAbsolute(traversal)) {
    throw new Error(`${label} path '${value}' escapes the pinned Node cache.`);
  }
  return path;
}

async function isRegularFileWithHash(path: string, expectedSha256: string): Promise<boolean> {
  try {
    await requireRegularFile(path, "Cached Node notice");
    return await hashFile(path) === expectedSha256;
  } catch {
    return false;
  }
}

async function requireRegularFile(path: string, label: string): Promise<void> {
  const entry = await lstat(path);
  if (!entry.isFile() || entry.isSymbolicLink()) throw new Error(`${label} must be a regular file.`);
}

function safeExecutableName(packageId: string): string {
  return packageId.replace(/[^a-z0-9.-]/gu, "-");
}

async function waitForExit(child: ReturnType<typeof spawn>, timeoutMilliseconds: number): Promise<number | null> {
  if (child.exitCode !== null) return child.exitCode;
  return await new Promise<number | null>((resolvePromise, reject) => {
    const timer = setTimeout(() => reject(new Error("SEA worker exit timed out.")), timeoutMilliseconds);
    child.once("exit", (code) => {
      clearTimeout(timer);
      resolvePromise(code);
    });
  });
}

class SmokeMessageQueue {
  readonly #decoder = new FrameDecoder();
  readonly #values: Array<Readonly<Record<string, JsonValue>>> = [];
  readonly #waiters: Array<(value: Readonly<Record<string, JsonValue>>) => void> = [];

  public push(chunk: Buffer): void {
    for (const value of this.#decoder.push(chunk)) {
      if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("SEA smoke received a non-object envelope.");
      const root = value as Readonly<Record<string, JsonValue>>;
      const waiter = this.#waiters.shift();
      if (waiter === undefined) this.#values.push(root);
      else waiter(root);
    }
  }

  public async next(timeoutMilliseconds: number): Promise<Readonly<Record<string, JsonValue>>> {
    const value = this.#values.shift();
    if (value !== undefined) return value;
    return await new Promise((resolvePromise, reject) => {
      const timer = setTimeout(() => reject(new Error("SEA protocol smoke timed out waiting for a frame.")), timeoutMilliseconds);
      this.#waiters.push((item) => {
        clearTimeout(timer);
        resolvePromise(item);
      });
    });
  }
}

interface ProcessOutput {
  readonly stdout: string;
  readonly stderr: string;
}
