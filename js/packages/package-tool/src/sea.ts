import { createHash, randomBytes } from "node:crypto";
import { createReadStream, createWriteStream } from "node:fs";
import {
  access,
  chmod,
  copyFile,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import { homedir } from "node:os";
import { basename, dirname, resolve } from "node:path";
import { pipeline } from "node:stream/promises";
import { spawn } from "node:child_process";
import { FrameDecoder, encodeFrame, type JsonValue } from "@sunder/sdk";
import { currentRid, type SunderRid } from "./model";
import {
  bundleProduction,
  maximumExecutableBytes,
  prepareProject,
  writeTargetLeaf,
  type PreparedProject,
} from "./package";

const SEA_FUSE = "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2";

interface DistributionFile {
  readonly file: string;
  readonly sha256: string;
  readonly archive: boolean;
  readonly executable: string;
}

interface DistributionMetadata {
  readonly nodeVersion: "24.18.1";
  readonly baseUrl: string;
  readonly targets: Readonly<Record<SunderRid, DistributionFile>>;
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
  const pinnedNode = await acquirePinnedNode(metadata, rid);
  const workRoot = resolve(project.outputRoot, ".sunder-sea", rid);
  await rm(workRoot, { recursive: true, force: true });
  await mkdir(workRoot, { recursive: true });
  const bundlePath = resolve(workRoot, "worker.cjs");
  await bundleProduction(project, rid, bundlePath);
  const blobPath = resolve(workRoot, "sea-prep.blob");
  const seaConfigPath = resolve(workRoot, "sea-config.json");
  await writeFile(seaConfigPath, `${JSON.stringify({
    main: bundlePath,
    output: blobPath,
    disableExperimentalSEAWarning: true,
    useSnapshot: false,
    useCodeCache: false,
    execArgvExtension: "none",
  }, null, 2)}\n`, "utf8");
  await runChecked(pinnedNode, ["--experimental-sea-config", seaConfigPath], workRoot);
  const executablePath = resolve(workRoot, `${safeExecutableName(project.config.id)}${rid.startsWith("win-") ? ".exe" : ""}`);
  await copyFile(pinnedNode, executablePath);
  if (!rid.startsWith("win-")) await chmod(executablePath, 0o755);
  await removeSignature(executablePath, rid, options.allowUnsignedWindows);
  await injectBlob(executablePath, blobPath, rid);
  await signExecutable(executablePath, rid, options.allowUnsignedWindows);
  const bytes = (await stat(executablePath)).size;
  const maximum = maximumExecutableBytes(project.config);
  if (bytes > maximum) {
    throw new Error(`SEA executable is ${bytes} bytes and exceeds maximumExecutableBytes ${maximum}.`);
  }
  const leafPath = await writeTargetLeaf({ project, rid, executablePath });
  process.stdout.write(`Sunder SEA size (${rid}): ${bytes} bytes\n`);
  return { leafPath, executablePath, bytes };
}

export async function smokeSeaTarget(projectPath: string, rid = currentRid()): Promise<void> {
  if (rid !== currentRid()) throw new Error(`SEA smoke test for '${rid}' requires its exact native runner.`);
  const project = await prepareProject(projectPath);
  const executableName = `${safeExecutableName(project.config.id)}${rid.startsWith("win-") ? ".exe" : ""}`;
  const executablePath = resolve(project.outputRoot, "targets", rid, "payload", "runtime", rid, "bin", executableName);
  await access(executablePath);
  const activationId = randomBytes(16).toString("hex");
  const sessionId = randomBytes(16).toString("hex");
  const smokeRoot = resolve(project.outputRoot, ".sunder-smoke", rid);
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
}

async function readDistributionMetadata(): Promise<DistributionMetadata> {
  const path = resolve(__dirname, "..", "node-distributions.json");
  const metadata = JSON.parse(await readFile(path, "utf8")) as DistributionMetadata;
  if (metadata.nodeVersion !== "24.18.1") throw new Error("Node distribution metadata must remain pinned to 24.18.1.");
  return metadata;
}

async function acquirePinnedNode(metadata: DistributionMetadata, rid: SunderRid): Promise<string> {
  const distribution = metadata.targets[rid];
  if (distribution === undefined) throw new Error(`No pinned Node distribution metadata exists for '${rid}'.`);
  const cacheRoot = resolve(process.env.SUNDER_NODE_CACHE ?? resolve(homedir(), ".cache", "sunder", "node"), `v${metadata.nodeVersion}`, rid);
  const executablePath = resolve(cacheRoot, distribution.executable);
  const downloadPath = resolve(cacheRoot, basename(distribution.file));
  if (await hasVerifiedCachedNode(downloadPath, executablePath, distribution.sha256, metadata.nodeVersion)) {
    return executablePath;
  }
  await rm(cacheRoot, { recursive: true, force: true });
  await mkdir(cacheRoot, { recursive: true });
  const sumsUrl = `${metadata.baseUrl}/SHASUMS256.txt`;
  const sumsResponse = await fetch(sumsUrl, { redirect: "error" });
  if (!sumsResponse.ok) throw new Error(`Could not download Node SHA metadata: HTTP ${sumsResponse.status}.`);
  const sums = await sumsResponse.text();
  const expectedLine = `${distribution.sha256}  ${distribution.file}`;
  if (!sums.split(/\r?\n/u).includes(expectedLine)) {
    throw new Error(`Pinned SHA metadata for '${distribution.file}' does not match upstream Node ${metadata.nodeVersion}.`);
  }
  const response = await fetch(`${metadata.baseUrl}/${distribution.file}`, { redirect: "error" });
  if (!response.ok || response.body === null) throw new Error(`Could not download pinned Node binary: HTTP ${response.status}.`);
  await pipeline(response.body, createWriteStream(downloadPath, { flags: "wx" }));
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
  if (!rid.startsWith("win-")) await chmod(executablePath, 0o755);
  await verifyNodeVersion(executablePath, metadata.nodeVersion);
  await writeFile(resolve(cacheRoot, "verified.json"), `${JSON.stringify({ version: metadata.nodeVersion, file: distribution.file, sha256: distribution.sha256 }, null, 2)}\n`, "utf8");
  return executablePath;
}

async function hasVerifiedCachedNode(
  downloadPath: string,
  executablePath: string,
  expectedSha256: string,
  expectedVersion: string,
): Promise<boolean> {
  if (!await exists(downloadPath) || !await exists(executablePath)) return false;
  try {
    if (await hashFile(downloadPath) !== expectedSha256) return false;
    await verifyNodeVersion(executablePath, expectedVersion);
    return true;
  } catch {
    return false;
  }
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

async function hashFile(path: string): Promise<string> {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) hash.update(chunk as Buffer);
  return hash.digest("hex");
}

function safeExecutableName(packageId: string): string {
  return packageId.replace(/[^a-z0-9.-]/gu, "-");
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
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
