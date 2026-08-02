import { randomUUID } from "node:crypto";
import { execFile } from "node:child_process";
import { lstatSync, readFileSync, unlinkSync, type Stats } from "node:fs";
import {
  lstat,
  mkdir,
  open,
  readFile,
  readlink,
  readdir,
  rename,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import { hostname, tmpdir } from "node:os";
import { basename, dirname, resolve } from "node:path";
import { promisify } from "node:util";

export const GENERATED_OUTPUT_MARKER_SUFFIX = ".sunder-generated-output";

const AGGREGATE_GENERATED_OUTPUT_MARKER_SUFFIX = ".sunder-aggregate-generated-output";
const LOCK_SUFFIX = ".sunder-output.lock";
const LOCK_OWNER_FILE = "owner.json";
const LOCK_TRANSITION_SUFFIX = ".transition";
const TARGET_LEAF_DISCOVERY_SUFFIX = ".sunder-target-leaf-container";
const PROCESS_MARKER_DIRECTORY = "sunder-generated-output/processes";
const DEFAULT_LOCK_WAIT_MILLISECONDS = 5 * 60_000;
const TRANSACTION_SUFFIX = ".sunder-output-transaction.json";
const INSTALLED_SUFFIX = ".installed";
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
const GUID_PATTERN = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu;

export interface StagedOutput {
  readonly finalPath: string;
  readonly stagedPath: string;
}

export interface StagedGeneratedDirectory {
  readonly output: StagedOutput;
  readonly marker: StagedOutput;
}

interface LockOwner {
  readonly schemaVersion: 2;
  readonly token: string;
  readonly pid: number;
  readonly hostname: string;
  readonly processStartedAt: string;
  readonly platform: "linux" | "win32" | "darwin";
  readonly processMarkerPath: string;
  readonly processMarkerToken: string;
  readonly linuxBootId: string | null;
  readonly linuxPidNamespace: string | null;
  readonly acquiredAt: string;
}

interface ProcessMarker {
  readonly schemaVersion: 1;
  readonly token: string;
  readonly pid: number;
  readonly hostname: string;
  readonly processStartedAt: string;
  readonly platform: "linux" | "win32" | "darwin";
  readonly linuxBootId: string | null;
  readonly linuxPidNamespace: string | null;
  readonly createdAt: string;
}

interface ProcessMarkerRegistration {
  readonly path: string;
  readonly marker: ProcessMarker;
  ownedDirectoryCount: number;
}

interface OwnedDirectory {
  readonly path: string;
  readonly token: string;
  readonly processMarker: ProcessMarkerRegistration;
  markerReleased: boolean;
}

interface TransactionOutput {
  readonly finalPath: string;
  readonly stagedPath: string;
  readonly backupPath: string;
  readonly existed: boolean;
}

interface OutputTransaction {
  readonly schemaVersion: 1;
  readonly token: string;
  readonly coordinatorPath: string;
  readonly outputs: readonly TransactionOutput[];
}

export async function withOutputLocks<T>(
  paths: readonly string[],
  operation: () => Promise<T>,
  waitMilliseconds = DEFAULT_LOCK_WAIT_MILLISECONDS,
): Promise<T> {
  if (!Number.isSafeInteger(waitMilliseconds) || waitMilliseconds <= 0) {
    throw new Error("Generated output lock waitMilliseconds must be a positive safe integer.");
  }
  const unique = new Map<string, string>();
  for (const path of paths.map((item) => resolve(item))) {
    unique.set(process.platform === "win32" ? path.toLowerCase() : path, path);
  }
  const keys = [...unique.values()].sort(pathOrder);
  const locks: OwnedDirectory[] = [];
  const deadline = Date.now() + waitMilliseconds;
  let operationFailed = false;
  let operationError: unknown;
  try {
    for (const key of keys) locks.push(await acquireOutputLock(key, deadline, waitMilliseconds));
    await recoverOutputTransactions(keys);
    await cleanupOrphanedOutputArtifacts(keys);
    return await operation();
  } catch (error) {
    operationFailed = true;
    operationError = error;
    throw error;
  } finally {
    const releaseErrors: unknown[] = [];
    for (const lock of locks.reverse()) {
      try {
        await releaseOutputLock(lock, waitMilliseconds);
      } catch (error) {
        releaseErrors.push(error);
      }
    }
    if (releaseErrors.length > 0) {
      throw new AggregateError(
        operationFailed ? [operationError, ...releaseErrors] : releaseErrors,
        "One or more generated output locks could not be released safely.",
      );
    }
  }
}

export function targetLeafDiscoveryKey(directory: string): string {
  return `${resolve(directory)}${TARGET_LEAF_DISCOVERY_SUFFIX}`;
}

export async function assertReplaceableGeneratedDirectory(path: string, allowUnmarked = false): Promise<void> {
  const output = await tryLstat(path);
  const marker = await tryLstat(markerPath(path));
  if (marker !== undefined && (!marker.isFile() || marker.isSymbolicLink())) {
    throw new Error(`Generated output marker '${markerPath(path)}' must be a regular file.`);
  }
  if (output === undefined) return;
  if (!output.isDirectory() || output.isSymbolicLink()) {
    throw new Error(`Refusing to replace generated output '${path}' because it is not a regular directory.`);
  }
  if (allowUnmarked) return;
  if (marker === undefined) {
    throw new Error(`Refusing to replace unmarked generated output '${path}'.`);
  }
}

export async function assertReplaceableGeneratedFile(path: string, label = "generated output"): Promise<void> {
  const output = await tryLstat(path);
  if (output !== undefined && (!output.isFile() || output.isSymbolicLink())) {
    throw new Error(`Refusing to replace ${label} '${path}' because it is not a regular file.`);
  }
}

export async function stageGeneratedDirectory(
  path: string,
  markerContent: string,
  populate: (stagingPath: string) => Promise<void>,
): Promise<StagedGeneratedDirectory> {
  await mkdir(dirname(path), { recursive: true });
  const stagedPath = `${path}.stage-${randomUUID()}`;
  const stagedMarkerPath = markerPath(stagedPath);
  await mkdir(stagedPath);
  try {
    await populate(stagedPath);
    await writeFile(stagedMarkerPath, markerContent, "utf8");
    return {
      output: { finalPath: path, stagedPath },
      marker: { finalPath: markerPath(path), stagedPath: stagedMarkerPath },
    };
  } catch (error) {
    await rm(stagedPath, { recursive: true, force: true });
    await rm(stagedMarkerPath, { force: true });
    throw error;
  }
}

export async function commitStagedOutputs(outputs: readonly StagedOutput[]): Promise<void> {
  if (outputs.length === 0) return;
  const token = randomUUID();
  const coordinatorPath = resolve(outputs[0]!.finalPath);
  const journalPath = transactionPath(coordinatorPath);
  const installedPath = `${journalPath}${INSTALLED_SUFFIX}`;
  const transactionOutputs: TransactionOutput[] = [];
  for (const output of outputs) {
    const finalPath = resolve(output.finalPath);
    const stagedPath = resolve(output.stagedPath);
    const stagedEntry = await tryLstat(stagedPath);
    if (stagedEntry === undefined) {
      throw new Error(`Staged generated output '${stagedPath}' is missing.`);
    }
    const markerSidecar = isMarkerSidecarPath(finalPath);
    if (markerSidecar && (!stagedEntry.isFile() || stagedEntry.isSymbolicLink())) {
      throw new Error(`Staged generated output marker '${stagedPath}' must be a regular file.`);
    }
    const finalEntry = await tryLstat(finalPath);
    if (markerSidecar && finalEntry !== undefined && (!finalEntry.isFile() || finalEntry.isSymbolicLink())) {
      throw new Error(`Generated output marker '${finalPath}' must be a regular file.`);
    }
    await mkdir(dirname(finalPath), { recursive: true });
    transactionOutputs.push({
      finalPath,
      stagedPath,
      backupPath: `${finalPath}.backup-${token}`,
      existed: finalEntry !== undefined,
    });
  }
  const transaction: OutputTransaction = {
    schemaVersion: 1,
    token,
    coordinatorPath,
    outputs: transactionOutputs,
  };
  const journalStagingPath = `${journalPath}.write-${randomUUID()}`;
  try {
    await writeDurableFile(journalStagingPath, `${JSON.stringify(transaction, null, 2)}\n`);
    await rename(journalStagingPath, journalPath);
    for (const output of transactionOutputs) {
      if (output.existed) await rename(output.finalPath, output.backupPath);
    }
    for (const output of transactionOutputs) await rename(output.stagedPath, output.finalPath);
    await writeDurableFile(installedPath, `${token}\n`);
    await finalizeTransaction(transaction, journalPath, installedPath);
  } catch (error) {
    await rm(journalStagingPath, { force: true }).catch(() => undefined);
    const rollbackErrors: unknown[] = [];
    try {
      if (await tryLstat(journalPath) !== undefined) {
        const installedToken = await readTextIfPresent(installedPath);
        if (installedToken?.trim() !== token) {
          await rollbackTransaction(transaction, journalPath, installedPath);
        }
      }
    } catch (rollbackError) {
      rollbackErrors.push(rollbackError);
    }
    if (rollbackErrors.length > 0) {
      throw new AggregateError([error, ...rollbackErrors], "Generated output replacement failed and could not be fully rolled back.");
    }
    throw error;
  }
}

export async function cleanupStagedOutputs(outputs: readonly StagedOutput[]): Promise<void> {
  await Promise.all(outputs.map(async (output) => {
    try {
      if (isMarkerSidecarPath(resolve(output.finalPath))) {
        await assertOptionalRegularFile(output.stagedPath, "staged generated output marker");
        await rm(output.stagedPath, { force: true });
      } else {
        await rm(output.stagedPath, { recursive: true, force: true });
      }
    } catch {
      // Preserve the primary output result or failure.
    }
  }));
}

export function markerPath(path: string): string {
  return `${path}${GENERATED_OUTPUT_MARKER_SUFFIX}`;
}

async function acquireOutputLock(key: string, deadline: number, waitMilliseconds: number): Promise<OwnedDirectory> {
  const lockPath = `${key}${LOCK_SUFFIX}`;
  await mkdir(dirname(lockPath), { recursive: true });
  while (true) {
    let acquired: OwnedDirectory | undefined;
    let quarantinePath: string | undefined;
    const transition = await acquireTransitionGuard(lockPath, deadline, waitMilliseconds);
    try {
      const lockEntry = await tryLstat(lockPath);
      if (lockEntry === undefined) {
        acquired = await tryPublishOwnedDirectory(lockPath);
        if (acquired === undefined) {
          throw new Error(`Generated output lock '${lockPath}' changed while its transition guard was held.`);
        }
      } else {
        await ensureRegularDirectory(lockPath, "generated output lock");
        const candidate = await readDirectoryOwner(lockPath);
        if (candidate !== undefined && await getOwnerState(candidate) === "dead") {
          const current = await readDirectoryOwner(lockPath);
          if (current?.token === candidate.token) {
            const candidateQuarantine = `${lockPath}.stale-${candidate.token}-${randomUUID()}`;
            if (await tryRenameDirectory(lockPath, candidateQuarantine)) quarantinePath = candidateQuarantine;
          }
        }
      }
    } finally {
      await releaseTransitionGuard(transition, deadline, waitMilliseconds);
    }
    if (quarantinePath !== undefined) {
      await rm(quarantinePath, { recursive: true, force: true }).catch(() => undefined);
    }
    if (acquired !== undefined) return acquired;
    throwIfLockTimedOut(deadline, waitMilliseconds, lockPath);
    await delay(25 + Math.floor(Math.random() * 75));
  }
}

async function releaseOutputLock(lock: OwnedDirectory, waitMilliseconds: number): Promise<void> {
  const deadline = Date.now() + waitMilliseconds;
  while (true) {
    let quarantinePath: string | undefined;
    let ownerUnreadable = false;
    const transition = await acquireTransitionGuard(lock.path, deadline, waitMilliseconds);
    try {
      const current = await readDirectoryOwner(lock.path);
      if (current === undefined) {
        if (await tryLstat(lock.path) === undefined) {
          releaseProcessMarkerOwnership(lock);
          return;
        }
        ownerUnreadable = true;
      } else if (current.token !== lock.token) {
        releaseProcessMarkerOwnership(lock);
        return;
      }
      if (!ownerUnreadable) {
        const verified = await readDirectoryOwner(lock.path);
        if (verified === undefined) {
          if (await tryLstat(lock.path) === undefined) {
            releaseProcessMarkerOwnership(lock);
            return;
          }
          ownerUnreadable = true;
        } else if (verified.token !== lock.token) {
          releaseProcessMarkerOwnership(lock);
          return;
        }
      }
      if (!ownerUnreadable) {
        const candidateQuarantine = `${lock.path}.release-${lock.token}-${randomUUID()}`;
        if (await tryRenameDirectory(lock.path, candidateQuarantine)) quarantinePath = candidateQuarantine;
      }
    } finally {
      await releaseTransitionGuard(transition, deadline, waitMilliseconds);
    }
    if (quarantinePath !== undefined) {
      await rm(quarantinePath, { recursive: true, force: true }).catch(() => undefined);
      releaseProcessMarkerOwnership(lock);
      return;
    }
    throwIfLockTimedOut(deadline, waitMilliseconds, lock.path);
    await delay(25 + Math.floor(Math.random() * 75));
  }
}

async function acquireTransitionGuard(
  lockPath: string,
  deadline: number,
  waitMilliseconds: number,
): Promise<OwnedDirectory> {
  const guardPath = `${lockPath}${LOCK_TRANSITION_SUFFIX}`;
  while (true) {
    const owned = await tryPublishOwnedDirectory(guardPath);
    if (owned !== undefined) return owned;
    const guardEntry = await tryLstat(guardPath);
    if (guardEntry === undefined) {
      throwIfLockTimedOut(deadline, waitMilliseconds, guardPath);
      continue;
    }
    if (!guardEntry.isDirectory() || guardEntry.isSymbolicLink()) {
      throw new Error(`The generated output transition guard '${guardPath}' is not a regular directory.`);
    }
    const candidate = await readDirectoryOwner(guardPath);
    if (candidate !== undefined && await getOwnerState(candidate) === "dead") {
      const current = await readDirectoryOwner(guardPath);
      if (current?.token === candidate.token) {
        // The nonempty token tombstone permanently fences contenders that paused
        // after reading this dead guard but before attempting the same rename.
        const tombstonePath = `${guardPath}.stale-${candidate.token}`;
        if (await tryLstat(tombstonePath) === undefined) {
          await tryRenameDirectory(guardPath, tombstonePath);
        }
      }
    }
    throwIfLockTimedOut(deadline, waitMilliseconds, guardPath);
    await delay(25 + Math.floor(Math.random() * 75));
  }
}

async function releaseTransitionGuard(
  guard: OwnedDirectory,
  deadline: number,
  waitMilliseconds: number,
): Promise<void> {
  while (true) {
    const current = await readDirectoryOwner(guard.path);
    if (current === undefined) {
      if (await tryLstat(guard.path) === undefined) {
        releaseProcessMarkerOwnership(guard);
        return;
      }
      throwIfLockTimedOut(deadline, waitMilliseconds, guard.path);
      await delay(25 + Math.floor(Math.random() * 75));
      continue;
    }
    if (current.token !== guard.token) {
      releaseProcessMarkerOwnership(guard);
      return;
    }
    const verified = await readDirectoryOwner(guard.path);
    if (verified === undefined) {
      if (await tryLstat(guard.path) === undefined) {
        releaseProcessMarkerOwnership(guard);
        return;
      }
      throwIfLockTimedOut(deadline, waitMilliseconds, guard.path);
      await delay(25 + Math.floor(Math.random() * 75));
      continue;
    }
    if (verified.token !== guard.token) {
      releaseProcessMarkerOwnership(guard);
      return;
    }
    const releasePath = `${guard.path}.guard-release-${guard.token}`;
    if (await tryRenameDirectory(guard.path, releasePath)) {
      await rm(releasePath, { recursive: true, force: true }).catch(() => undefined);
      releaseProcessMarkerOwnership(guard);
      return;
    }
    throwIfLockTimedOut(deadline, waitMilliseconds, guard.path);
    await delay(25 + Math.floor(Math.random() * 75));
  }
}

async function tryPublishOwnedDirectory(path: string): Promise<OwnedDirectory | undefined> {
  const processMarker = await currentProcessMarker();
  const owner = createLockOwner(processMarker);
  const candidatePath = `${path}.candidate-${owner.token}`;
  await mkdir(dirname(path), { recursive: true });
  await mkdir(candidatePath);
  let retained = false;
  let published = false;
  try {
    await writeDurableFile(resolve(candidatePath, LOCK_OWNER_FILE), `${JSON.stringify(owner, null, 2)}\n`);
    retainProcessMarkerOwnership(processMarker);
    retained = true;
    try {
      await rename(candidatePath, path);
    } catch (error) {
      if (["EEXIST", "ENOTEMPTY"].includes((error as NodeJS.ErrnoException).code ?? "")) return undefined;
      if (await tryLstat(path) !== undefined) return undefined;
      throw error;
    }
    published = true;
    return { path, token: owner.token, processMarker, markerReleased: false };
  } finally {
    if (retained && !published) decrementProcessMarkerOwnership(processMarker);
    await rm(candidatePath, { recursive: true, force: true }).catch(() => undefined);
  }
}

function createLockOwner(processMarker: ProcessMarkerRegistration): LockOwner {
  const marker = processMarker.marker;
  return {
    schemaVersion: 2,
    token: randomUUID(),
    pid: marker.pid,
    hostname: marker.hostname,
    processStartedAt: marker.processStartedAt,
    platform: marker.platform,
    processMarkerPath: processMarker.path,
    processMarkerToken: marker.token,
    linuxBootId: marker.linuxBootId,
    linuxPidNamespace: marker.linuxPidNamespace,
    acquiredAt: new Date().toISOString(),
  };
}

async function readDirectoryOwner(directory: string): Promise<LockOwner | undefined> {
  return await readLockOwner(resolve(directory, LOCK_OWNER_FILE));
}

async function readLockOwner(path: string): Promise<LockOwner | undefined> {
  try {
    const entry = await tryLstat(path);
    if (entry === undefined || !entry.isFile() || entry.isSymbolicLink()) return undefined;
    const value = JSON.parse(await readFile(path, "utf8")) as Partial<LockOwner>;
    if (value.schemaVersion !== 2
      || typeof value.token !== "string"
      || !UUID_PATTERN.test(value.token)
      || !Number.isSafeInteger(value.pid)
      || value.pid! <= 0
      || typeof value.hostname !== "string"
      || value.hostname.length === 0
      || typeof value.processStartedAt !== "string"
      || value.processStartedAt.length === 0
      || !isSupportedLockPlatform(value.platform)
      || typeof value.processMarkerPath !== "string"
      || value.processMarkerPath.length === 0
      || typeof value.processMarkerToken !== "string"
      || !UUID_PATTERN.test(value.processMarkerToken)
      || !validLinuxProof(value.platform, value.linuxBootId, value.linuxPidNamespace)
      || typeof value.acquiredAt !== "string") return undefined;
    return value as LockOwner;
  } catch (error) {
    if (["ENOENT", "EISDIR", "SyntaxError"].includes((error as NodeJS.ErrnoException).code ?? (error as Error).name)) return undefined;
    return undefined;
  }
}

async function getOwnerState(owner: LockOwner): Promise<"alive" | "dead" | "unknown"> {
  if (!await hasValidLocalProcessMarker(owner)) return "unknown";
  const actualStartedAt = await processStartedAt(owner.pid);
  if (actualStartedAt === null) return "dead";
  if (actualStartedAt === undefined) return "unknown";
  if (actualStartedAt !== owner.processStartedAt) return "dead";
  return owner.platform === "darwin" ? "unknown" : "alive";
}

async function ensureRegularDirectory(path: string, label: string): Promise<void> {
  const entry = await tryLstat(path);
  if (entry === undefined || !entry.isDirectory() || entry.isSymbolicLink()) {
    throw new Error(`The ${label} '${path}' is not a regular directory.`);
  }
}

async function tryRenameDirectory(source: string, destination: string): Promise<boolean> {
  try {
    await rename(source, destination);
    return true;
  } catch (error) {
    if (["ENOENT", "EACCES", "EPERM", "EEXIST", "ENOTEMPTY"].includes((error as NodeJS.ErrnoException).code ?? "")) return false;
    throw error;
  }
}

function throwIfLockTimedOut(deadline: number, waitMilliseconds: number, path: string): void {
  if (Date.now() >= deadline) {
    throw new Error(`Timed out after ${waitMilliseconds} ms waiting for generated output lock '${path}'.`);
  }
}

const execFileAsync = promisify(execFile);
let currentProcessStartedAtPromise: Promise<string> | undefined;
let currentHostIdentityPromise: Promise<Pick<ProcessMarker, "hostname" | "platform" | "linuxBootId" | "linuxPidNamespace">> | undefined;
let currentProcessMarkerPromise: Promise<ProcessMarkerRegistration> | undefined;

function currentProcessMarker(): Promise<ProcessMarkerRegistration> {
  currentProcessMarkerPromise ??= createProcessMarker();
  return currentProcessMarkerPromise;
}

async function createProcessMarker(): Promise<ProcessMarkerRegistration> {
  const [processStartedAt, hostIdentity] = await Promise.all([
    currentProcessStartedAt(),
    currentHostIdentity(),
  ]);
  const token = randomUUID();
  const marker: ProcessMarker = {
    schemaVersion: 1,
    token,
    pid: process.pid,
    hostname: hostIdentity.hostname,
    processStartedAt,
    platform: hostIdentity.platform,
    linuxBootId: hostIdentity.linuxBootId,
    linuxPidNamespace: hostIdentity.linuxPidNamespace,
    createdAt: new Date().toISOString(),
  };
  const root = processMarkerRoot();
  const path = resolve(root, `${token}.json`);
  await mkdir(root, { recursive: true, mode: 0o700 });
  await ensureRegularDirectory(root, "generated output process marker root");
  await writeDurableFile(path, `${JSON.stringify(marker, null, 2)}\n`, 0o600);
  const registration: ProcessMarkerRegistration = { path, marker, ownedDirectoryCount: 0 };
  process.once("exit", () => cleanupProcessMarkerAtExit(registration));
  return registration;
}

function currentHostIdentity(): Promise<Pick<ProcessMarker, "hostname" | "platform" | "linuxBootId" | "linuxPidNamespace">> {
  currentHostIdentityPromise ??= readCurrentHostIdentity();
  return currentHostIdentityPromise;
}

async function readCurrentHostIdentity(): Promise<Pick<ProcessMarker, "hostname" | "platform" | "linuxBootId" | "linuxPidNamespace">> {
  if (!isSupportedLockPlatform(process.platform)) {
    throw new Error("Generated output process identity is unavailable on this operating system.");
  }
  const currentHostname = canonicalHostname(hostname());
  if (currentHostname.length === 0) throw new Error("Could not establish the current host identity for generated output locking.");
  if (process.platform !== "linux") {
    return { hostname: currentHostname, platform: process.platform, linuxBootId: null, linuxPidNamespace: null };
  }
  const [bootIdText, pidNamespace] = await Promise.all([
    readFile("/proc/sys/kernel/random/boot_id", "utf8"),
    readlink("/proc/self/ns/pid"),
  ]);
  const linuxBootId = bootIdText.trim().toLowerCase();
  if (!GUID_PATTERN.test(linuxBootId) || !/^pid:\[\d+\]$/u.test(pidNamespace)) {
    throw new Error("Could not establish the current Linux boot and PID namespace identity for generated output locking.");
  }
  return { hostname: currentHostname, platform: process.platform, linuxBootId, linuxPidNamespace: pidNamespace };
}

async function hasValidLocalProcessMarker(owner: LockOwner): Promise<boolean> {
  try {
    const markerRoot = processMarkerRoot();
    const rootEntry = await tryLstat(markerRoot);
    if (rootEntry === undefined || !rootEntry.isDirectory() || rootEntry.isSymbolicLink()) return false;
    const expectedMarkerPath = resolve(markerRoot, `${owner.processMarkerToken}.json`);
    if (pathOrder(resolve(owner.processMarkerPath), expectedMarkerPath) !== 0) return false;
    const entry = await tryLstat(expectedMarkerPath);
    if (entry === undefined || !entry.isFile() || entry.isSymbolicLink()) return false;
    const marker = JSON.parse(await readFile(expectedMarkerPath, "utf8")) as Partial<ProcessMarker>;
    if (marker.schemaVersion !== 1
      || marker.token !== owner.processMarkerToken
      || marker.pid !== owner.pid
      || marker.hostname !== owner.hostname
      || marker.processStartedAt !== owner.processStartedAt
      || marker.platform !== owner.platform
      || marker.linuxBootId !== owner.linuxBootId
      || marker.linuxPidNamespace !== owner.linuxPidNamespace
      || typeof marker.createdAt !== "string") return false;
    const local = await currentHostIdentity();
    return local.platform === owner.platform
      && canonicalHostname(local.hostname) === canonicalHostname(owner.hostname)
      && local.linuxBootId === owner.linuxBootId
      && local.linuxPidNamespace === owner.linuxPidNamespace;
  } catch {
    return false;
  }
}

function isSupportedLockPlatform(value: unknown): value is ProcessMarker["platform"] {
  return value === "linux" || value === "win32" || value === "darwin";
}

function canonicalHostname(value: string): string {
  const normalized = value.trim().replace(/\.$/u, "").toLowerCase();
  return normalized.endsWith(".local") ? normalized.slice(0, -".local".length) : normalized;
}

function validLinuxProof(
  platform: ProcessMarker["platform"],
  bootId: string | null | undefined,
  pidNamespace: string | null | undefined,
): boolean {
  return platform === "linux"
    ? typeof bootId === "string" && GUID_PATTERN.test(bootId) && typeof pidNamespace === "string" && /^pid:\[\d+\]$/u.test(pidNamespace)
    : bootId === null && pidNamespace === null;
}

function processMarkerRoot(): string {
  return resolve(tmpdir(), ...PROCESS_MARKER_DIRECTORY.split("/"));
}

function retainProcessMarkerOwnership(registration: ProcessMarkerRegistration): void {
  registration.ownedDirectoryCount++;
}

function decrementProcessMarkerOwnership(registration: ProcessMarkerRegistration): void {
  if (registration.ownedDirectoryCount <= 0) throw new Error("Generated output process marker ownership underflowed.");
  registration.ownedDirectoryCount--;
}

function releaseProcessMarkerOwnership(owned: OwnedDirectory): void {
  if (owned.markerReleased) return;
  owned.markerReleased = true;
  decrementProcessMarkerOwnership(owned.processMarker);
}

function cleanupProcessMarkerAtExit(registration: ProcessMarkerRegistration): void {
  if (registration.ownedDirectoryCount !== 0) return;
  try {
    const entry = lstatSync(registration.path);
    if (!entry.isFile() || entry.isSymbolicLink()) return;
    const marker = JSON.parse(readFileSync(registration.path, "utf8")) as Partial<ProcessMarker>;
    if (marker.schemaVersion === 1 && marker.token === registration.marker.token) unlinkSync(registration.path);
  } catch {
    // A missing or replaced marker must not be modified during process shutdown.
  }
}

function currentProcessStartedAt(): Promise<string> {
  currentProcessStartedAtPromise ??= processStartedAt(process.pid).then((value) => {
    if (value === null || value === undefined) {
      throw new Error("Could not establish the current process start identity for generated output locking.");
    }
    return value;
  });
  return currentProcessStartedAtPromise;
}

async function processStartedAt(pid: number): Promise<string | null | undefined> {
  try {
    if (process.platform === "linux") {
      const [statText, bootIdText] = await Promise.all([
        readFile(`/proc/${pid}/stat`, "utf8"),
        readFile("/proc/sys/kernel/random/boot_id", "utf8"),
      ]);
      const closingParenthesis = statText.lastIndexOf(")");
      const fields = closingParenthesis < 0 ? [] : statText.slice(closingParenthesis + 1).trim().split(/\s+/u);
      const startTicks = fields[19];
      const bootId = bootIdText.trim().toLowerCase();
      if (startTicks === undefined || !/^\d+$/u.test(startTicks) || !GUID_PATTERN.test(bootId)) return undefined;
      return `linux:${bootId}:${startTicks}`;
    }
    if (process.platform === "win32") {
      const script = [
        "$ErrorActionPreference = 'Stop'",
        `$p = [System.Diagnostics.Process]::GetProcessById(${pid})`,
        "$p.StartTime.ToUniversalTime().Ticks.ToString([System.Globalization.CultureInfo]::InvariantCulture)",
      ].join("; ");
      const { stdout } = await execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
        encoding: "utf8",
        timeout: 5_000,
        windowsHide: true,
      });
      const ticks = stdout.trim();
      return /^\d+$/u.test(ticks) ? `windows:${ticks}` : undefined;
    }
    if (process.platform === "darwin") {
      const { stdout } = await execFileAsync("/bin/ps", ["-p", String(pid), "-o", "lstart="], {
        encoding: "utf8",
        env: { ...process.env, LANG: "C", LC_ALL: "C", TZ: "UTC" },
        timeout: 5_000,
      });
      const value = Date.parse(`${stdout.trim()} UTC`);
      if (!Number.isFinite(value)) return undefined;
      return `darwin:${new Date(Math.floor(value / 1_000) * 1_000).toISOString()}`;
    }
    return undefined;
  } catch {
    const exists = processExists(pid);
    return exists === false ? null : undefined;
  }
}

function processExists(pid: number): boolean | undefined {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    return code === "ESRCH" ? false : code === "EPERM" ? true : undefined;
  }
}

async function recoverOutputTransactions(protectedPaths: readonly string[]): Promise<void> {
  for (const coordinatorPath of protectedPaths) {
    const journalPath = transactionPath(coordinatorPath);
    if (await tryLstat(journalPath) === undefined) continue;
    const transaction = await readTransaction(journalPath, protectedPaths);
    const installedPath = `${journalPath}${INSTALLED_SUFFIX}`;
    const installedToken = await readTextIfPresent(installedPath);
    if (installedToken?.trim() === transaction.token) {
      await finalizeTransaction(transaction, journalPath, installedPath);
    } else {
      await rollbackTransaction(transaction, journalPath, installedPath);
    }
  }
}

async function readTransaction(path: string, protectedPaths: readonly string[]): Promise<OutputTransaction> {
  let value: Partial<OutputTransaction>;
  try {
    value = JSON.parse(await readFile(path, "utf8")) as Partial<OutputTransaction>;
  } catch (error) {
    throw new Error(`Generated output transaction journal '${path}' is unreadable; refusing unsafe recovery.`, { cause: error });
  }
  if (value.schemaVersion !== 1
    || typeof value.token !== "string"
    || !UUID_PATTERN.test(value.token)
    || typeof value.coordinatorPath !== "string"
    || transactionPath(resolve(value.coordinatorPath)) !== path
    || !Array.isArray(value.outputs)
    || value.outputs.length === 0) {
    throw new Error(`Generated output transaction journal '${path}' is invalid; refusing unsafe recovery.`);
  }
  const allowed = new Set(protectedPaths.flatMap((item) => [
    resolve(item),
    resolve(markerPath(item)),
    resolve(`${item}${AGGREGATE_GENERATED_OUTPUT_MARKER_SUFFIX}`),
  ]));
  for (const raw of value.outputs) {
    const output = raw as Partial<TransactionOutput>;
    const finalPath = typeof output.finalPath === "string" ? resolve(output.finalPath) : "";
    const validStagedPath = typeof output.stagedPath === "string" && (
      output.stagedPath.startsWith(`${finalPath}.stage-`)
      || finalPath.endsWith(GENERATED_OUTPUT_MARKER_SUFFIX)
      && output.stagedPath.startsWith(`${finalPath.slice(0, -GENERATED_OUTPUT_MARKER_SUFFIX.length)}.stage-`)
      && output.stagedPath.endsWith(GENERATED_OUTPUT_MARKER_SUFFIX)
    );
    if (typeof output.finalPath !== "string"
      || typeof output.stagedPath !== "string"
      || typeof output.backupPath !== "string"
      || typeof output.existed !== "boolean"
      || !allowed.has(finalPath)
      || resolve(output.stagedPath) !== output.stagedPath
      || !validStagedPath
      || resolve(output.backupPath) !== `${finalPath}.backup-${value.token}`) {
      throw new Error(`Generated output transaction journal '${path}' contains unsafe paths; refusing recovery.`);
    }
    if (isMarkerSidecarPath(finalPath)) {
      await assertOptionalRegularFile(finalPath, "generated output marker");
      await assertOptionalRegularFile(output.stagedPath, "staged generated output marker");
      await assertOptionalRegularFile(output.backupPath, "backed-up generated output marker");
    }
  }
  return value as OutputTransaction;
}

async function rollbackTransaction(
  transaction: OutputTransaction,
  journalPath: string,
  installedPath: string,
): Promise<void> {
  for (const output of [...transaction.outputs].reverse()) {
    const backupExists = await tryLstat(output.backupPath) !== undefined;
    if (output.existed && backupExists) {
      await deleteTransactionPath(output, output.finalPath);
      await rename(output.backupPath, output.finalPath);
    } else if (!output.existed) {
      await deleteTransactionPath(output, output.finalPath);
    }
    await deleteTransactionPath(output, output.stagedPath);
  }
  await rm(journalPath, { force: true });
  await rm(installedPath, { force: true }).catch(() => undefined);
}

async function finalizeTransaction(
  transaction: OutputTransaction,
  journalPath: string,
  installedPath: string,
): Promise<void> {
  for (const output of transaction.outputs) {
    const finalEntry = await tryLstat(output.finalPath);
    if (finalEntry === undefined) {
      throw new Error(`Committed generated output '${output.finalPath}' is missing during crash recovery.`);
    }
    if (isMarkerSidecarPath(output.finalPath) && (!finalEntry.isFile() || finalEntry.isSymbolicLink())) {
      throw new Error(`Committed generated output marker '${output.finalPath}' is not a regular file.`);
    }
  }
  for (const output of transaction.outputs) {
    await deleteTransactionPath(output, output.backupPath);
    await deleteTransactionPath(output, output.stagedPath);
  }
  await rm(journalPath, { force: true });
  await rm(installedPath, { force: true }).catch(() => undefined);
}

async function cleanupOrphanedOutputArtifacts(keys: readonly string[]): Promise<void> {
  const finals = [...new Set(keys.flatMap((key) => [
    resolve(key),
    resolve(markerPath(key)),
    resolve(`${key}${AGGREGATE_GENERATED_OUTPUT_MARKER_SUFFIX}`),
  ]))];
  for (const finalPath of finals) {
    const markerSidecar = isMarkerSidecarPath(finalPath);
    if (markerSidecar) await assertOptionalRegularFile(finalPath, "generated output marker");
    const parent = dirname(finalPath);
    let entries;
    try {
      entries = await readdir(parent, { withFileTypes: true });
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") continue;
      throw error;
    }
    const name = basename(finalPath);
    const stages = entries.filter((entry) => entry.name.startsWith(`${name}.stage-`)
      || entry.name.startsWith(`${name}${TRANSACTION_SUFFIX}.write-`)
      || entry.name === `${name}${TRANSACTION_SUFFIX}${INSTALLED_SUFFIX}`);
    for (const entry of stages) {
      const path = resolve(parent, entry.name);
      if (isMarkerSidecarPath(path) || markerSidecar && entry.name.startsWith(`${name}.stage-`)) {
        await assertOptionalRegularFile(path, "orphaned staged generated output marker");
        await rm(path, { force: true });
      } else {
        await rm(path, { recursive: true, force: true });
      }
    }
    const backups = entries.filter((entry) => entry.name.startsWith(`${name}.backup-`));
    if (backups.length === 0) continue;
    const candidates = await Promise.all(backups.map(async (entry) => {
      const path = resolve(parent, entry.name);
      if (markerSidecar) await assertOptionalRegularFile(path, "orphaned backed-up generated output marker");
      return { path, mtimeMs: (await stat(path)).mtimeMs };
    }));
    candidates.sort((left, right) => right.mtimeMs - left.mtimeMs || pathOrder(left.path, right.path));
    if (await tryLstat(finalPath) === undefined) await rename(candidates[0]!.path, finalPath);
    for (const candidate of candidates) {
      if (candidate.path !== candidates[0]!.path || await tryLstat(candidate.path) !== undefined) {
        await rm(candidate.path, markerSidecar ? { force: true } : { recursive: true, force: true });
      }
    }
  }
}

function isMarkerSidecarPath(path: string): boolean {
  return path.endsWith(GENERATED_OUTPUT_MARKER_SUFFIX)
    || path.endsWith(AGGREGATE_GENERATED_OUTPUT_MARKER_SUFFIX);
}

async function assertOptionalRegularFile(path: string, label: string): Promise<void> {
  const entry = await tryLstat(path);
  if (entry !== undefined && (!entry.isFile() || entry.isSymbolicLink())) {
    throw new Error(`The ${label} '${path}' must be a regular file.`);
  }
}

async function deleteTransactionPath(output: TransactionOutput, path: string): Promise<void> {
  if (isMarkerSidecarPath(output.finalPath)) {
    await assertOptionalRegularFile(path, "generated output marker transaction artifact");
    await rm(path, { force: true });
  } else {
    await rm(path, { recursive: true, force: true });
  }
}

async function writeDurableFile(path: string, content: string, mode?: number): Promise<void> {
  const handle = await open(path, "wx", mode);
  try {
    await handle.writeFile(content, "utf8");
    await handle.sync();
  } finally {
    await handle.close();
  }
}

function transactionPath(coordinatorPath: string): string {
  return `${resolve(coordinatorPath)}${TRANSACTION_SUFFIX}`;
}

async function readTextIfPresent(path: string): Promise<string | undefined> {
  try {
    return await readFile(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

async function tryLstat(path: string): Promise<Stats | undefined> {
  try {
    return await lstat(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

function pathOrder(left: string, right: string): number {
  const normalizedLeft = process.platform === "win32" ? left.toLowerCase() : left;
  const normalizedRight = process.platform === "win32" ? right.toLowerCase() : right;
  return normalizedLeft < normalizedRight ? -1 : normalizedLeft > normalizedRight ? 1 : 0;
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}
