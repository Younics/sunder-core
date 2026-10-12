import { randomUUID } from "node:crypto";
import { link, lstat, open, realpath, rename, unlink } from "node:fs/promises";
import { dirname, parse, resolve, sep } from "node:path";
import { readProjectDescriptorFile } from "./descriptor-files";
import { parseBaseline, writeBaseline, type OwnedBaseline } from "./rpc-baseline";
import { MAX_BASELINE_BYTES, object, parseOuter } from "./rpc-baseline-json";
import { isPackageId, isSemanticVersion } from "@sunder/sdk";
import { markerPath, withOutputLeases } from "./output";
import { validateRoleUseEvidence } from "./rpc-owned-uses";

export const BASELINE_FILE = "sunder-contracts.owned.json";
export interface BaselineInput {
  readonly path: string;
  readonly baseline?: OwnedBaseline;
  readonly fingerprint: string;
  revalidate(): Promise<void>;
  verifyParents(): Promise<void>;
}
export async function exists(path: string): Promise<boolean> {
  try { await lstat(path); return true; } catch (error) { if ((error as NodeJS.ErrnoException).code === "ENOENT") return false; throw error; }
}
/** Absence may continue only from a complete modern private/import-only receipt. */
export function assertBaselineFreeEvidence(bytes: Buffer, label: string, expectedPackageId?: string, kind: "receipt" | "export" = "receipt"): void {
  try {
    const value = object(parseOuter(bytes));
    validateRoleUseEvidence(value,kind);
    if (value.schemaVersion !== 1 || typeof value.packageId !== "string" || !isPackageId(value.packageId)
      || expectedPackageId !== undefined && value.packageId !== expectedPackageId || typeof value.packageVersion !== "string" || !isSemanticVersion(value.packageVersion)
      || !Array.isArray(value.contracts) || value.contracts.length > 128) throw new Error("Incomplete generation identity.");
    const bundles = value.bundles === undefined ? undefined : Array.isArray(value.bundles) ? value.bundles.map(object) : undefined;
    if (kind === "receipt" && (!bundles || bundles.length !== value.contracts.length)) throw new Error("Incomplete bundle evidence.");
    for (const item of value.contracts) {
      const contract = object(item);
      if (contract.exposure !== "package" && contract.exposure !== "public" || typeof contract.contractVersion !== "string" || !isSemanticVersion(contract.contractVersion)) throw new Error("Incomplete contract identity.");
      const source = contract.source ?? bundles?.find(bundle => bundle.contractKey === contract.contractKey)?.source;
      if (kind === "export" && source !== "owned") throw new Error("Project exports require owned source evidence.");
      if (source !== "owned" && source !== "imported") throw new Error("Incomplete ownership evidence.");
      if (source === "owned" && (contract.exposure === "public" || contract.contractVersion !== value.packageVersion)) throw new Error("Retained owned contract requires its original baseline.");
    }
  } catch (error) { throw new Error(`rpc-authoring.baseline-drift: ${label}: restore the original owned baseline; incomplete, public or legacy generation evidence cannot bootstrap private absence.`, { cause: error }); }
}
// Normalize only OS-owned aliases, never an arbitrary project symlink. Directory
// identity is retained across planning, lease acquisition and final publication.
async function parents(root: string): Promise<{ root: string; verify(): Promise<void> }> {
  root = resolve(root);
  if (process.platform === "darwin") for (const alias of ["/var", "/tmp"]) if (root === alias || root.startsWith(`${alias}/`)) {
    if (await realpath(alias) !== `/private${alias}`) throw new Error("Unexpected system path alias.");
    root = `/private${root}`;
  }
  const components = root.slice(parse(root).root.length).split(sep).filter(Boolean);
  const captured: Array<{ path: string; dev: bigint; ino: bigint; mode: bigint }> = [];
  let path = parse(root).root;
  for (const part of components) {
    path = resolve(path, part); const stat = await lstat(path, { bigint: true });
    if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Owned baseline parent must be a regular directory without symbolic links.");
    captured.push({ path, dev: stat.dev, ino: stat.ino, mode: stat.mode });
  }
  const verify = async (): Promise<void> => {
    for (const expected of captured) {
      const actual = await lstat(expected.path, { bigint: true });
      if (!actual.isDirectory() || actual.isSymbolicLink() || actual.dev !== expected.dev || actual.ino !== expected.ino || actual.mode !== expected.mode) throw new Error("Owned baseline parent directory changed after planning.");
    }
  };
  return { root, verify };
}
export async function captureBaseline(root: string): Promise<BaselineInput> {
  const parent = await parents(root); const path = resolve(parent.root, BASELINE_FILE);
  const bytes = await exists(path) ? await readProjectDescriptorFile(parent.root, path, "Owned baseline", MAX_BASELINE_BYTES) : undefined;
  const baseline = bytes === undefined ? undefined : parseBaseline(bytes);
  const revalidate = async (): Promise<void> => {
    await parent.verify(); const present = await exists(path);
    if (bytes === undefined ? present : !present || !(await readProjectDescriptorFile(parent.root, path, "Owned baseline", MAX_BASELINE_BYTES)).equals(bytes)) throw new Error("Owned baseline changed after planning; retry the command.");
    await parent.verify();
  };
  await revalidate();
  return { path, ...(baseline ? { baseline } : {}), fingerprint: baseline?.sha256 ?? "absent", revalidate, verifyParents: parent.verify };
}
/** Selection and first Accept must not reinterpret deleted baseline evidence as a new project. */
export async function verifyFreshOwnedSource(root: string, input: BaselineInput): Promise<void> {
  await input.revalidate();
  if (input.baseline) return;
  const marker = resolve(root, ".sunder/generated/rpc/owned-baseline.sha256");
  if (await exists(marker) && (await readProjectDescriptorFile(root, marker, "Baseline marker", 128)).toString() === "absent\n") {
    const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json");
    if (await exists(receiptPath)) {
      assertBaselineFreeEvidence(await readProjectDescriptorFile(root, receiptPath, "Service receipt", 67_108_864), receiptPath);
      return;
    }
  }
  for (const name of ["authoring-receipt.v1.json", "package-export.v1.json", "owned-baseline.sha256"]) {
    if (await exists(resolve(root, ".sunder/generated/rpc", name)))
      throw new Error("rpc-authoring.baseline-drift: Existing owned baseline is required; restore it before changing imports or accepting source with prior receipt/export evidence.");
  }
  if (await exists(markerPath(resolve(root, ".sunder/generated/rpc"))))
    throw new Error("rpc-authoring.baseline-drift: Existing generated output marker requires the original owned baseline.");
}
export async function withBaselineLease<T>(input: BaselineInput, operation: () => Promise<T>): Promise<T> {
  await input.verifyParents();
  return withOutputLeases([input.path], async () => { await input.revalidate(); return operation(); });
}
export async function commitBaseline(input: BaselineInput, proposal: Buffer, verifyInputs: () => Promise<void>): Promise<boolean> {
  const parsed = parseBaseline(proposal);
  if (input.baseline && parsed.packageId !== input.baseline.packageId) throw new Error("rpc-authoring.private-retirement-mutated: Baseline root authority cannot change.");
  for (const [key, entry] of input.baseline?.retiredPrivateContracts ?? []) {
    const after = parsed.retiredPrivateContracts?.get(key);
    if (!after || !writeBaseline(parsed.packageId, new Map([[key, entry]])).equals(writeBaseline(parsed.packageId, new Map([[key, after]]))))
      throw new Error(`rpc-authoring.private-retirement-mutated: ${key}: preserve complete original archived evidence.`);
  }
  for (const [key, entry] of parsed.retiredPrivateContracts ?? []) if (!input.baseline?.retiredPrivateContracts?.has(key)) {
    const original = input.baseline?.contracts.get(key);
    if (!original || parsed.contracts.has(key) || !writeBaseline(parsed.packageId, new Map([[key, original]])).equals(writeBaseline(parsed.packageId, new Map([[key, entry]]))))
      throw new Error(`rpc-authoring.private-retirement-mutated: ${key}: archive additions must equal removed active before-images.`);
  }
  return withBaselineLease(input, async () => {
    await verifyInputs(); await input.revalidate();
    if (input.baseline?.bytes.equals(proposal)) return false;
    const root = dirname(input.path); const stage = resolve(root, `.sunder-contract-stage-${randomUUID()}`);
    let created = false;
    const verifyStage = async (): Promise<void> => {
      await input.verifyParents();
      const bytes = await readProjectDescriptorFile(root, stage, "Owned baseline stage", proposal.length);
      if (!bytes.equals(proposal)) throw new Error("Staged owned baseline changed before publication.");
    };
    try {
      const handle = await open(stage, "wx", 0o644); created = true;
      try { await handle.writeFile(proposal); await handle.sync(); } finally { await handle.close(); }
      await verifyStage(); await verifyInputs(); await input.revalidate(); await verifyStage();
      if (input.baseline) { await rename(stage, input.path); created = false; }
      else {
        // Node has no portable rename-no-replace. An exclusive hard-link install
        // gives an atomic complete file and fails EEXIST instead of overwriting a
        // baseline that appeared after the final absence check.
        await link(stage, input.path);
        await verifyStage(); await unlink(stage); created = false;
      }
      return true;
    } finally {
      // No discovery, wildcard recovery, recursive removal, or deletion of a
      // changed stage is permitted in the authored tree.
      if (created) try { await verifyStage(); await unlink(stage); } catch { /* retain unproven stage */ }
    }
  });
}
