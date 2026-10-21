import { createHash } from "node:crypto";
import { canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, isPackageId, isSemanticVersion, parseRpcContractDescriptor, SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, type RpcContractDescriptor } from "@sunder/sdk";
import type { LoadedContract } from "./model";
import { classify, compareVersions, minimumOwnedVersion, ordinal, validateVersion, versionParts, type ChangeKind } from "./rpc-evolution";
import { fields, invalid, object, ordered, outerBytes, outerKeys, parseOuter, string, type Outer, type OuterObject } from "./rpc-baseline-json";
import { createProjectionReview, readProjectionReview, sameReviewedProjection, projectionChange, requireReviewedProjection, validateProjectionReviewHistory, type OwnedProjectionReview } from "./rpc-owned-projection-review";

export interface OwnedSnapshot {
  readonly state: "draft" | "release-frozen" | "published";
  readonly exposure: "package" | "public";
  readonly descriptor: RpcContractDescriptor;
  readonly json: OuterObject;
  readonly projectionReview?: OwnedProjectionReview;
}
export interface OwnedContract { readonly contractId: string; readonly current?: OwnedSnapshot; readonly history: readonly OwnedSnapshot[] }
export interface OwnedBaseline { readonly packageId: string; readonly contracts: ReadonlyMap<string, OwnedContract>; readonly retiredPrivateContracts?: ReadonlyMap<string, OwnedContract>; readonly bytes: Buffer; readonly sha256: string }
const snapshotFields = ["state", "exposure", "contractVersion", "descriptorBytes", "canonicalDescriptor", "descriptorSha256", "abiProjectionBytes", "canonicalAbiProjection", "abiSha256"];
export const sha256 = (bytes: string | Uint8Array): string => createHash("sha256").update(bytes).digest("hex");
const abiHash = (bytes: Uint8Array): string => createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8").update(bytes).digest("hex");
export function portableKey(value: string): boolean { return value.length <= 128 && /^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$/u.test(value); }
function packageId(value: Outer | undefined): string { const id = string(value); if (!isPackageId(id)) invalid("invalid package identity"); return id; }
function version(value: Outer | undefined): string { const text = string(value); if (!isSemanticVersion(text)) invalid("invalid strict SemVer"); return text; }
function hash(value: Outer | undefined): string { const text = string(value); if (!/^[0-9a-f]{64}$/u.test(text)) invalid("invalid lowercase SHA-256"); return text; }
function snapshot(value: Outer, id: string, owner: string, allowDraft: boolean): OwnedSnapshot {
  const json = fields(value, snapshotFields, ["packageRelease", "publication"]);
  const state = string(json.state); const exposure = string(json.exposure);
  if (!["draft", "release-frozen", "published"].includes(state) || !allowDraft && state === "draft") invalid("invalid snapshot state");
  if (exposure !== "package" && exposure !== "public") invalid("invalid package exposure");
  const contractVersion = version(json.contractVersion);
  const descriptorText = string(json.canonicalDescriptor); const bytes = Buffer.from(descriptorText);
  if (bytes.length < 2 || bytes.length > 4_194_304 || bytes.length !== json.descriptorBytes) invalid("descriptor byte count mismatch");
  const descriptor = parseRpcContractDescriptor(bytes);
  if (!bytes.equals(Buffer.from(canonicalRpcDescriptorBytes(descriptor))) || descriptor.contractId !== id || descriptor.version !== contractVersion || sha256(bytes) !== hash(json.descriptorSha256)) invalid("canonical descriptor identity mismatch");
  const abi = Buffer.from(string(json.canonicalAbiProjection));
  if (abi.length < 2 || abi.length > 4_194_304 || abi.length !== json.abiProjectionBytes || !abi.equals(Buffer.from(canonicalRpcAbiProjectionBytes(descriptor))) || abiHash(abi) !== hash(json.abiSha256)) invalid("canonical ABI identity mismatch");
  const hasRelease = Object.hasOwn(json, "packageRelease"); const hasPublication = Object.hasOwn(json, "publication");
  if (state === "draft" ? hasRelease || hasPublication : state === "release-frozen" ? !hasRelease || hasPublication : !hasRelease || !hasPublication) invalid("state/release/publication evidence mismatch");
  if (hasRelease) {
    const release = fields(json.packageRelease, ["packageId", "packageVersion"]);
    if (packageId(release.packageId) !== owner) invalid("release package identity mismatch");
    const releaseVersion = version(release.packageVersion);
    if (hasPublication) {
      const publication = fields(json.publication, ["registryOrigin", "packageId", "packageVersion", "packageContentIdentity", "publishedAtUtc", "etag"]);
      registryOrigin(string(publication.registryOrigin)); timestamp(string(publication.publishedAtUtc)); hash(publication.packageContentIdentity);
      if (packageId(publication.packageId) !== owner || version(publication.packageVersion) !== releaseVersion || string(publication.etag) !== `"${sha256(bytes)}"`) invalid("publication/release/descriptor evidence mismatch");
    }
  }
  return Object.freeze({ state: state as OwnedSnapshot["state"], exposure, descriptor, json });
}
export function parseBaseline(bytes: Buffer): OwnedBaseline {
  const root = fields(parseOuter(bytes), ["schemaVersion", "authorityKind", "authorityId", "packageId", "contracts"], ["retiredPrivateContracts"]);
  if (root.schemaVersion !== 1 || root.authorityKind !== "package") invalid("requires package authority (platform baselines are unsupported)");
  const owner = packageId(root.packageId); if (packageId(root.authorityId) !== owner) invalid("authority/package mismatch");
  if (outerKeys(object(root.contracts)).length + (Object.hasOwn(root, "retiredPrivateContracts") ? outerKeys(object(root.retiredPrivateContracts)).length : 0) > 128)
    throw new Error("rpc-authoring.baseline-combined-limit: Active and retired records together exceed 128 entries.");
  const readMap = (input: Outer | undefined, retired: boolean): ReadonlyMap<string, OwnedContract> => {
  const map = object(input); const keys = outerKeys(map);
  if (keys.length > 128 || JSON.stringify(keys) !== JSON.stringify([...keys].sort(ordinal))) invalid("contract map exceeds 128 entries or is not ordinally ordered");
  const contracts = new Map<string, OwnedContract>(); const ids = new Set<string>();
  for (const key of keys) {
    if (!portableKey(key)) invalid("invalid portable contract key");
    const value = fields(map[key], ["contractId", "history"], ["current", "projectionReviews"], ["contractId", "current", "history", "projectionReviews"]);
    const id = packageId(value.contractId); if (ids.has(id)) invalid("duplicate contract identity"); ids.add(id);
    if (!Array.isArray(value.history) || value.history.length > 256) invalid("history must be an array of at most 256 entries");
    const history = value.history.map((item) => snapshot(item, id, owner, false));
    let current = value.current === undefined ? undefined : snapshot(value.current, id, owner, true);
    const all = current ? [...history, current] : history;
    if (retired && all.some(item => item.exposure !== "package")) throw new Error(`rpc-authoring.private-retirement-public-evidence: ${key} contains public evidence; use the public lifecycle.`);
    if (!all.length) invalid("removed contract must retain nonempty immutable history");
    for (let i = 1; i < all.length; i++) if (compareVersions(all[i]!.descriptor.version, all[i - 1]!.descriptor.version) <= 0) invalid("current/history versions must be in strictly increasing SemVer precedence order");
    if (value.projectionReviews !== undefined) {
      if (!Array.isArray(value.projectionReviews) || !value.projectionReviews.length || value.projectionReviews.length > 257) invalid("projectionReviews must have 1..257 entries; omit when absent");
      let previous: string | undefined;
      for (const item of value.projectionReviews) {
        const reviewVersion = version(object(item).contractVersion);
        if (previous !== undefined && compareVersions(reviewVersion, previous) <= 0) invalid("projection reviews must have strictly increasing semantic versions");
        previous = reviewVersion;
        const index = history.findIndex(entry => entry.descriptor.version === reviewVersion);
        const retained = index >= 0 ? history[index] : current?.descriptor.version === reviewVersion ? current : undefined;
        if (!retained) invalid("projection review has no exact retained snapshot");
        const reviewed = Object.freeze({ ...retained, projectionReview: readProjectionReview(item, retained.descriptor) });
        if (index >= 0) history[index] = reviewed; else current = reviewed;
      }
    }
    contracts.set(key, Object.freeze({ contractId: id, ...(current ? { current } : {}), history: Object.freeze(history) }));
  }
  return contracts;
  };
  const contracts = readMap(root.contracts, false);
  const retiredPrivateContracts = Object.hasOwn(root, "retiredPrivateContracts") ? readMap(root.retiredPrivateContracts, true) : undefined;
  if (contracts.size + (retiredPrivateContracts?.size ?? 0) > 128) throw new Error("rpc-authoring.baseline-combined-limit: Active and retired records together exceed 128 entries.");
  return { packageId: owner, contracts, ...(retiredPrivateContracts === undefined ? {} : { retiredPrivateContracts }), bytes: Buffer.from(bytes), sha256: sha256(bytes) };
}
export function writeBaseline(owner: string, contracts: ReadonlyMap<string, OwnedContract>, retiredPrivateContracts?: ReadonlyMap<string, OwnedContract>): Buffer {
  const records = (values: ReadonlyMap<string, OwnedContract>): OuterObject => ordered([...values].sort(([a], [b]) => ordinal(a, b)).map(([key, contract]) => {
    const reviews = [...contract.history, ...contract.current ? [contract.current] : []].flatMap(entry => entry.projectionReview ? [entry.projectionReview.json] : []);
    return [key, ordered([
    ["contractId", contract.contractId], ...(contract.current ? [["current", contract.current.json] as const] : []), ["history", contract.history.map(entry => entry.json)],
    ...reviews.length ? [["projectionReviews", reviews] as const] : [],
  ])] as const; }));
  const bytes = outerBytes(ordered([
    ["schemaVersion", 1], ["authorityKind", "package"], ["authorityId", owner], ["packageId", owner],
    ["contracts", records(contracts)], ...(retiredPrivateContracts === undefined ? [] : [["retiredPrivateContracts", records(retiredPrivateContracts)] as const]),
  ]));
  const parsed = parseBaseline(bytes);
  for (const [map, parsedMap] of [[contracts, parsed.contracts], [retiredPrivateContracts ?? new Map<string, OwnedContract>(), parsed.retiredPrivateContracts ?? new Map<string, OwnedContract>()]] as const) for (const [key, contract] of map) {
    const actual = parsedMap.get(key)!;
    const matches = (a: OwnedSnapshot | undefined, b: OwnedSnapshot | undefined): boolean => !a ? !b : !!b && a.state === b.state && a.exposure === b.exposure
      && Buffer.from(canonicalRpcDescriptorBytes(a.descriptor)).equals(Buffer.from(canonicalRpcDescriptorBytes(b.descriptor)))
      && Buffer.from(canonicalRpcAbiProjectionBytes(a.descriptor)).equals(Buffer.from(canonicalRpcAbiProjectionBytes(b.descriptor)))
      && outerBytes(a.json).equals(outerBytes(b.json))
      && (a.projectionReview === undefined ? b.projectionReview === undefined : b.projectionReview !== undefined
        && a.projectionReview.reviewKind === b.projectionReview.reviewKind && sameReviewedProjection(a.projectionReview, b.projectionReview)
        && outerBytes(a.projectionReview.json).equals(outerBytes(b.projectionReview.json)));
    if (!matches(contract.current, actual.current) || contract.history.length !== actual.history.length || contract.history.some((item, i) => !matches(item, actual.history[i]))) invalid("model disagrees with retained lossless snapshot JSON");
  }
  return bytes;
}
export function emptyBaseline(owner: string): OwnedBaseline { return parseBaseline(writeBaseline(owner, new Map())); }
export function atVersion(descriptor: RpcContractDescriptor, version: string): RpcContractDescriptor { return parseRpcContractDescriptor(JSON.stringify({ ...descriptor, version })); }
function draft(exposure: OwnedSnapshot["exposure"], descriptor: RpcContractDescriptor): OwnedSnapshot {
  const bytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor)); const abi = Buffer.from(canonicalRpcAbiProjectionBytes(descriptor));
  return { state: "draft", exposure, descriptor, json: ordered([
    ["state", "draft"], ["exposure", exposure], ["contractVersion", descriptor.version], ["descriptorBytes", bytes.length],
    ["canonicalDescriptor", bytes.toString()], ["descriptorSha256", sha256(bytes)], ["abiProjectionBytes", abi.length], ["canonicalAbiProjection", abi.toString()], ["abiSha256", abiHash(abi)],
  ]) };
}
export function sourceMatches(snapshot: OwnedSnapshot, source: LoadedContract): boolean {
  return wireMatches(snapshot, source) && sameReviewedProjection(snapshot.projectionReview, source);
}
function wireMatches(snapshot: OwnedSnapshot, source: LoadedContract): boolean {
  return snapshot.exposure === source.exposure && Buffer.from(canonicalRpcDescriptorBytes(snapshot.descriptor)).equals(source.canonicalBytes)
    && abiHash(canonicalRpcAbiProjectionBytes(snapshot.descriptor)) === source.abiSha256;
}
function ownedChange(previous: OwnedSnapshot, candidate: LoadedContract): ChangeKind {
  if (previous.exposure !== candidate.exposure) return "Major";
  const wire = classify(previous.descriptor, candidate.descriptor).kind, source = projectionChange(previous, candidate);
  const kinds: readonly ChangeKind[] = ["Unchanged", "Patch", "Minor", "Major"];
  return kinds.indexOf(source) > kinds.indexOf(wire) ? source : wire;
}
function candidatesByKey(baseline: OwnedBaseline, candidates: readonly LoadedContract[], key?: string): Map<string, LoadedContract> {
  if (key !== undefined && !portableKey(key) || candidates.length > 128) invalid("invalid source/selected contract key or count");
  const source = new Map<string, LoadedContract>(); const folded = new Set<string>(); const ids = new Set<string>();
  for (const candidate of candidates) {
    const key = candidate.contractKey;
    if (!key || !portableKey(key) || folded.has(key.toLowerCase()) || ids.has(candidate.descriptor.contractId)) invalid("duplicate or invalid source identity");
    folded.add(key.toLowerCase()); ids.add(candidate.descriptor.contractId);
    const prior = baseline.contracts.get(key);
    if (prior && prior.contractId !== candidate.descriptor.contractId) invalid("source cannot change stable contract identity");
    if ([...baseline.contracts].some(([oldKey, entry]) => oldKey !== key && entry.contractId === candidate.descriptor.contractId)) invalid("source cannot alias or rename an existing key");
    source.set(key, candidate);
  }
  if (key !== undefined && !source.has(key) && !baseline.contracts.has(key)) invalid("selected contract key is absent");
  return source;
}
// Lifecycle is intentionally stricter than validateVersion's immutable reuse.
export function validateLifecycleHistory(candidate: RpcContractDescriptor, exposure: OwnedSnapshot["exposure"], history: readonly OwnedSnapshot[], preservePrerelease = false, review?: OwnedProjectionReview): void {
  if (!history.length) return;
  const version = versionParts(candidate.version); const highest = history.at(-1)!;
  const comparisons = history.filter((item) => item === highest || versionParts(item.descriptor.version).core[0] === version.core[0]);
  if (version.prerelease || comparisons.some((item) => versionParts(item.descriptor.version).prerelease)) {
    if (preservePrerelease) return;
    invalid("needs an undefined prerelease history progression policy");
  }
  if (compareVersions(candidate.version, highest.descriptor.version) <= 0) invalid("next version must follow all immutable history; issued versions cannot be reused");
  const errors = validateVersion(candidate, history.map((item) => item.descriptor)); if (errors.length) invalid(errors.join("; "));
  for (const item of comparisons) if (item.exposure !== exposure && compareVersions(candidate.version, minimumOwnedVersion(item.descriptor.version, "Major")) < 0) invalid("exposure change is Major relative to immutable history");
  validateProjectionReviewHistory(candidate, review, comparisons);
}
export function validateBaselineSource(baseline: OwnedBaseline, candidates: readonly LoadedContract[], release = false): void {
  const source = candidatesByKey(baseline, publicLifecycleCandidates(baseline, candidates));
  const active = [...baseline.contracts].filter(([, entry]) => entry.current);
  if (active.length !== source.size || active.some(([key, entry]) => !source.has(key) || !wireMatches(entry.current!, source.get(key)!))) invalid("source keys, exposure and exact descriptor/ABI must match the complete current baseline; run contracts accept");
  for (const [key, entry] of active) requireReviewedProjection(key, entry.current!, source.get(key)!);
  for (const [, entry] of active) {
    if (entry.current!.state === "draft") {
      if (release) invalid("release packing requires every current contract, including declaration-only contracts, to be release-frozen or published; run contracts freeze");
      validateLifecycleHistory(entry.current!.descriptor, entry.current!.exposure, entry.history, true, entry.current!.projectionReview);
    }
  }
}
export interface AcceptOptions { readonly key?: string; readonly currentVersion?: string; readonly nextVersion?: string; readonly acknowledgeBreakingChange?: boolean }
export function planAcceptance(baseline: OwnedBaseline, candidates: readonly LoadedContract[], options: AcceptOptions = {}): Buffer {
  if (Object.keys(options).some(key => !["key", "currentVersion", "nextVersion", "acknowledgeBreakingChange"].includes(key))) invalid("unknown contracts review option");
  writeBaseline(baseline.packageId, baseline.contracts, baseline.retiredPrivateContracts);
  const { key, currentVersion, nextVersion, acknowledgeBreakingChange = false } = options;
  const source = candidatesByKey(baseline, publicLifecycleCandidates(baseline, candidates), key);
  if (currentVersion !== undefined) version(currentVersion); if (nextVersion !== undefined) version(nextVersion);
  if (key === undefined && (currentVersion !== undefined || nextVersion !== undefined || acknowledgeBreakingChange)
    || acknowledgeBreakingChange && (currentVersion === undefined || nextVersion === undefined)) invalid("version selection requires one exact key; breaking acknowledgement requires a current/next version pair");
  if (key !== undefined) {
    const others = [...baseline.contracts].filter(([name, entry]) => name !== key && entry.current);
    if ([...source.keys()].filter((name) => name !== key).length !== others.length || others.some(([name, entry]) => !source.has(name) || !sourceMatches(entry.current!, source.get(name)!))) invalid("targeted acceptance requires complete unchanged snapshots outside the selected key");
  }
  const contracts = new Map(baseline.contracts);
  const acknowledge = (name: string): void => { if (key !== name || !currentVersion || !nextVersion || !acknowledgeBreakingChange) invalid(`breaking, unclassified, exposure or removal change '${name}' requires exact key, current/next versions and explicit breaking-change acknowledgement`); };
  for (const name of key === undefined ? [...new Set([...contracts.keys(), ...source.keys()])].sort(ordinal) : [key]) {
    const entry = contracts.get(name); const candidate = source.get(name); const current = entry?.current; const previous = current ?? entry?.history.at(-1);
    if (currentVersion !== undefined && currentVersion !== previous?.descriptor.version) invalid("does not have expected current version");
    if (previous && (currentVersion === undefined) !== (nextVersion === undefined)) invalid("existing contract version selection requires a current/next version pair");
    if (!candidate) {
      if (!current) continue;
      acknowledge(name); stableTransition(current.descriptor.version, nextVersion!);
      if (compareVersions(nextVersion!, minimumOwnedVersion(current.descriptor.version, "Major")) < 0) invalid("removal requires at least the next Major version");
      const history = current.state === "draft" ? entry!.history : [...entry!.history, current];
      if (!history.length) contracts.delete(name); else contracts.set(name, { contractId: entry!.contractId, history });
      continue;
    }
    if (current && sourceMatches(current, candidate) && (nextVersion === undefined || nextVersion === current.descriptor.version)) {
      if (current.state === "draft") validateLifecycleHistory(current.descriptor, current.exposure, entry!.history, true, current.projectionReview);
      continue;
    }
    let selectedVersion = nextVersion ?? current?.descriptor.version ?? "1.0.0";
    if (current && !current.projectionReview && wireMatches(current, candidate) && (nextVersion === undefined || nextVersion === current.descriptor.version)) requireReviewedProjection(name, current, candidate);
    stableTransition(previous?.descriptor.version, selectedVersion);
    const kind = previous ? ownedChange(previous, candidate) : "Unchanged";
    if (previous && (kind === "Major" || entry!.history.some((item) => versionParts(item.descriptor.version).core[0] === versionParts(previous.descriptor.version).core[0] && ownedChange(item, candidate) === "Major"))) acknowledge(name);
    const history = current && current.state !== "draft" ? [...entry!.history, current] : entry?.history ?? [];
    if (!nextVersion && previous) {
      let minimum = current?.state === "draft" ? current.descriptor.version : minimumOwnedVersion(previous.descriptor.version, kind === "Unchanged" ? "Patch" : kind);
      const major = versionParts(minimum).core[0];
      for (const item of history) {
        const version = versionParts(item.descriptor.version);
        if (version.core[0] !== major && item !== history.at(-1) || version.prerelease) continue;
        const change = ownedChange(item, candidate); const floor = minimumOwnedVersion(item.descriptor.version, change === "Unchanged" ? "Patch" : change);
        if (compareVersions(floor, minimum) > 0) minimum = floor;
      }
      selectedVersion = minimum;
    }
    const descriptor = atVersion(candidate.descriptor, selectedVersion);
    const projectionReview = createProjectionReview(descriptor, candidate);
    validateLifecycleHistory(descriptor, candidate.exposure, history, false, projectionReview);
    contracts.set(name, { contractId: descriptor.contractId, current: { ...draft(candidate.exposure, descriptor), projectionReview }, history });
  }
  return writeBaselineProposal(baseline, contracts);
}
export function planFreeze(baseline: OwnedBaseline, candidates: readonly LoadedContract[], packageVersion: string, key?: string): Buffer {
  version(packageVersion); candidatesByKey(baseline, candidates, key); validateBaselineSource(baseline, candidates);
  if (key !== undefined && !baseline.contracts.get(key)?.current) invalid("selected contract has no current snapshot to freeze");
  const contracts = new Map(baseline.contracts);
  for (const [name, entry] of contracts) {
    const current = entry.current;
    if (key !== undefined && key !== name || !current || current.state !== "draft") continue;
    validateLifecycleHistory(current.descriptor, current.exposure, entry.history, false, current.projectionReview);
    const json = ordered([...outerKeys(current.json).map((field): [string, Outer] => [field, field === "state" ? "release-frozen" : current.json[field]!]), ["packageRelease", ordered([["packageId", baseline.packageId], ["packageVersion", packageVersion]])]]);
    contracts.set(name, { ...entry, current: { ...current, state: "release-frozen", json } });
  }
  return writeBaselineProposal(baseline, contracts);
}
export function hasPublicEvidence(entry: OwnedContract): boolean {
  return entry.current?.exposure === "public" || entry.history.some(item => item.exposure === "public");
}
function writeBaselineProposal(baseline: OwnedBaseline, contracts: ReadonlyMap<string, OwnedContract>): Buffer {
  const bytes = writeBaseline(baseline.packageId, contracts, baseline.retiredPrivateContracts);
  validateImmutableProjectionReviews(baseline, parseBaseline(bytes));
  return bytes;
}
export function validateImmutableProjectionReviews(before: OwnedBaseline, after: OwnedBaseline): void {
  for (const [key, entry] of before.contracts) {
    if (!hasPublicEvidence(entry)) continue;
    for (const snapshot of [...entry.history, ...entry.current ? [entry.current] : []]) {
      if (snapshot.state === "draft") continue;
      const next = after.contracts.get(key);
      const retained = next?.contractId === entry.contractId ? [...next.history, ...next.current ? [next.current] : []].find(value => value.descriptor.version === snapshot.descriptor.version) : undefined;
      if (!retained) throw new Error(`rpc-authoring.immutable-evidence-mutated: Retain complete frozen history for '${key}'.`);
      const json = snapshot.state === "release-frozen" && retained.state === "published"
        ? ordered(outerKeys(retained.json).filter(name => name !== "publication").map(name => [name, name === "state" ? "release-frozen" : retained.json[name]!])) : retained.json;
      if (!outerBytes(snapshot.json).equals(outerBytes(json))) throw new Error(`rpc-authoring.immutable-evidence-mutated: Preserve complete snapshot/release/publication evidence for '${key}'.`);
      if (snapshot.projectionReview) {
        if (!retained.projectionReview || !outerBytes(snapshot.projectionReview.json).equals(outerBytes(retained.projectionReview.json)))
          throw new Error(`rpc-authoring.immutable-projection-review-mutated: Preserve exact frozen projection review for '${key}'.`);
      } else if (retained.projectionReview)
        throw new Error(`rpc-authoring.source-projection-history-unavailable: Cannot backfill historical or published projection evidence for '${key}' from current source.`);
    }
  }
}
/** Public identity matching happens before the baseline-free private fast path. */
export function publicLifecycleCandidates(baseline: OwnedBaseline, candidates: readonly LoadedContract[]): readonly LoadedContract[] {
  for (const [key, entry] of baseline.contracts) if (entry.current && !hasPublicEvidence(entry))
    throw new Error(`rpc-authoring.private-baseline-unsupported: '${key}' (${entry.contractId}) is an unsupported active private baseline. Private contracts use the package version; restore the current reviewed baseline.`);
  return candidates.filter(candidate => candidate.exposure === "public" || [...baseline.contracts].some(([key, entry]) =>
    hasPublicEvidence(entry) && (key === candidate.contractKey || entry.contractId === candidate.descriptor.contractId)));
}
function stableTransition(previous: string | undefined, next: string): void {
  if (versionParts(next).prerelease || previous && versionParts(previous).prerelease) invalid("cannot allocate a changed prerelease baseline; explicit prerelease progression policy is not defined");
}
function registryOrigin(text: string): void {
  const match = /^(https?):\/\/([a-z0-9.-]+)(?::([0-9]+))?\/$/u.exec(text);
  if (!match || text.length > 268) invalid("noncanonical registry origin");
  const [, scheme, host, port] = match;
  if (scheme === "http" ? host !== "localhost" : !/[a-z]/u.test(host!) || host!.length > 253 || host!.split(".").some((label) => label.startsWith("xn--") || label.length > 63 || !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/u.test(label))) invalid("noncanonical registry DNS origin");
  if (port !== undefined && (!/^[1-9][0-9]*$/u.test(port) || +port > 65535 || +port === (scheme === "https" ? 443 : 80))) invalid("noncanonical registry port");
}
function timestamp(text: string): void {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{0,8}[1-9]))?Z$/u.exec(text);
  if (!match) invalid("noncanonical UTC timestamp");
  const [, y, m, d, h, minute, second] = match; const year = +y!; const month = +m!;
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (!year || month < 1 || month > 12 || +d! < 1 || +d! > days[month - 1]! || +h! > 23 || +minute! > 59 || +second! > 59) invalid("noncanonical UTC timestamp calendar value");
}
