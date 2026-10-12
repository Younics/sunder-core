import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { mkdtemp, readFile, writeFile, mkdir, rm, open, readdir, stat, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { atVersion, emptyBaseline, parseBaseline, planAcceptance, planFreeze, planPrivateRetirement, sha256, validateBaselineSource, writeBaseline } from "../src/rpc-baseline";
import { fields, MAX_BASELINE_BYTES, ordered, outerBytes, parseOuter } from "../src/rpc-baseline-json";
import { captureBaseline, commitBaseline } from "../src/rpc-baseline-store";
import { withOutputLeases } from "../src/output";
import { startOutputLockContender } from "./observable-barriers";
import type { LoadedContract } from "../src/model";
import { emptyModelProjection } from "../src/rpc-model-projection";

const corpus = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring");
const bytes = (value: unknown): Buffer => Buffer.from(`${JSON.stringify(value, null, 2)}\n`);
const sample = JSON.parse(readFileSync(resolve(corpus, "conformance/valid/package-owned-baseline.json"), "utf8"));
async function contract(version = "1.0.0", change: "none" | "annotation" | "minor" | "major" = "none"): Promise<LoadedContract> {
  const json = JSON.parse(sample.contracts.minimal.current.canonicalDescriptor);
  json.version = version;
  if (change === "annotation") json.$defs.Empty.title = "Reviewed λ <>& / annotation\n\t😀";
  if (change === "minor") json.services.example.methods.added = structuredClone(json.services.example.methods.ping);
  if (change === "major") json.services.example.methods.ping.idempotency = "none";
  const descriptor = parseRpcContractDescriptor(JSON.stringify(json)); const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
  return { modelProjection: emptyModelProjection(), contractKey: "minimal", configuredPath: "src/rpc.ts", sourcePath: "src/rpc.ts", descriptorPath: "contracts/minimal.rpc.json", descriptor,
    canonicalBytes, canonical: canonicalBytes.toString(), descriptorSha256: sha256(canonicalBytes), abiSha256: await computeRpcAbiSha256(descriptor), exposure: "public" };
}
test("new selected baseline keys accept a stable next-only version without changing source identity", async () => {
  const source = await contract();
  for (const nextVersion of ["0.0.0", "2.0.0", "2.0.0+reviewed"]) {
    const accepted = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [source], { key: "minimal", nextVersion }));
    const entry = accepted.contracts.get("minimal")!;
    assert.equal(entry.current!.descriptor.contractId, source.descriptor.contractId);
    assert.equal(entry.current!.descriptor.version, nextVersion); assert.equal(entry.current!.state, "draft"); assert.deepEqual(entry.history, []);
    assert.equal(Object.hasOwn(entry.current!.json, "packageRelease"), false);
  }
});
test("next-only selection applies to new keys in existing baselines, not current or removed historical keys", async () => {
  const source = await contract(); const initial = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [source]));
  const frozen = parseBaseline(planFreeze(initial, [source], "7.6.5"));
  const removed = parseBaseline(planAcceptance(frozen, [], { key: "minimal", currentVersion: "1.0.0", nextVersion: "2.0.0", acknowledgeBreakingChange: true }));
  for (const baseline of [initial, frozen, removed]) assert.throws(() => planAcceptance(baseline, [source], { key: "minimal", nextVersion: "2.0.0" }), /current\/next version pair/);
  const descriptor = parseRpcContractDescriptor(JSON.stringify({ ...source.descriptor, contractId: "example.owner.greeting" }));
  const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
  const addition: LoadedContract = { ...source, contractKey: "greeting", descriptor, canonicalBytes, canonical: canonicalBytes.toString(), descriptorSha256: sha256(canonicalBytes), abiSha256: await computeRpcAbiSha256(descriptor) };
  const accepted = parseBaseline(planAcceptance(frozen, [source, addition], { key: "greeting", nextVersion: "2.0.0" }));
  assert.equal(accepted.contracts.get("greeting")!.current!.descriptor.version, "2.0.0");
  assert.deepEqual(outerBytes(accepted.contracts.get("minimal")!.current!.json), outerBytes(frozen.contracts.get("minimal")!.current!.json));
});
test("new-key next-only selection still rejects missing keys, old versions, acknowledgement and invalid/prerelease versions", async () => {
  const source = await contract(); const baseline = emptyBaseline("example.owner");
  for (const options of [
    { nextVersion: "2.0.0" }, { key: "missing", nextVersion: "2.0.0" },
    { key: "minimal", currentVersion: "1.0.0", nextVersion: "2.0.0" }, { key: "minimal", nextVersion: "2.0.0", acknowledgeBreakingChange: true },
    ...["", "2.0", "02.0.0", "2.0.0-preview.1", `2.0.0+${"a".repeat(251)}`].map((nextVersion) => ({ key: "minimal", nextVersion })),
  ]) assert.throws(() => planAcceptance(baseline, [source], options));
});
for (const name of ["minimal-package-owned-baseline.json", "package-owned-baseline.json"]) test(`C# deterministic owned writer fixture roundtrip: ${name}`, () => {
  const original = readFileSync(resolve(corpus, "conformance/valid", name)); const baseline = parseBaseline(original);
  assert.deepEqual(writeBaseline(baseline.packageId, baseline.contracts), original); assert.equal(baseline.sha256, sha256(original));
});
test("C# source-built writer fixture roundtrips without interpreting package/platform authority", () => {
  // Original C# writer blob aa4b9986c9477bc6e14dcade4543dbfe812d63dd retained when the live private fixture became
  // baseline-free. Test evidence only; never a build input or published asset.
  const original = readFileSync(resolve(__dirname, "../../test/historical-csharp-owned.json"));
  const baseline = parseBaseline(original); assert.deepEqual(writeBaseline(baseline.packageId, baseline.contracts), original);
});
async function publishedFixture() {
  const source = await contract();
  const draft = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [source]));
  const frozen = JSON.parse(planFreeze(draft, [source], "3.4.5").toString());
  const current = frozen.contracts.minimal.current;
  current.state = "published";
  current.publication = { registryOrigin: "https://registry.example/", packageId: "example.owner", packageVersion: "3.4.5", packageContentIdentity: "a".repeat(64), publishedAtUtc: "2026-09-11T12:34:56.123456789Z", etag: `"${current.descriptorSha256}"` };
  return frozen;
}
test("lossless immutable publication and release fields survive acceptance, history and later package releases", async () => {
  const fixture = await publishedFixture(); const before = bytes(fixture); const baseline = parseBaseline(before);
  const source = await contract();
  assert.deepEqual(planAcceptance(baseline, [source]), before);
  assert.deepEqual(planFreeze(baseline, [source], "9.8.7"), before);
  const changed = parseBaseline(planAcceptance(baseline, [await contract("1.0.0", "annotation")]));
  assert.equal(changed.contracts.get("minimal")!.current!.descriptor.version, "1.0.1");
  assert.deepEqual(outerBytes(changed.contracts.get("minimal")!.history[0]!.json), bytes(fixture.contracts.minimal.current));
  const frozen = parseBaseline(planFreeze(changed, [await contract("1.0.1", "annotation")], "9.8.7"));
  const minor = parseBaseline(planAcceptance(frozen, [await contract("1.0.1", "minor")]));
  assert.equal(minor.contracts.get("minimal")!.current!.descriptor.version, "1.1.0");
  assert.deepEqual(outerBytes(minor.contracts.get("minimal")!.history[0]!.json), bytes(fixture.contracts.minimal.current));
  assert.deepEqual(writeBaseline(minor.packageId, minor.contracts), minor.bytes);
});
test("private retirement preserves complete draft/frozen/published/history records and no-op bytes", async () => {
  for (const state of ["draft", "release-frozen", "published", "history-only"]) {
    const input = await publishedFixture(); const snapshot = input.contracts.minimal.current;
    snapshot.exposure = "package";
    if (state === "draft") { snapshot.state = state; delete snapshot.packageRelease; delete snapshot.publication; }
    if (state === "release-frozen") { snapshot.state = state; delete snapshot.publication; }
    if (state === "history-only") { input.contracts.minimal.history = [snapshot]; delete input.contracts.minimal.current; }
    const before = parseBaseline(bytes(input));
    const proposal = planPrivateRetirement(before, []); const after = parseBaseline(proposal);
    assert.equal(after.contracts.size, 0);
    assert.deepEqual(JSON.parse(proposal.toString()).retiredPrivateContracts.minimal, input.contracts.minimal);
    assert.deepEqual(writeBaseline(after.packageId, after.contracts, after.retiredPrivateContracts), proposal);
    assert.deepEqual(planPrivateRetirement(after, []), proposal);
    validateBaselineSource(after, [{ ...await contract("9.0.0+Private"), exposure: "package" }], true);
  }
});
test("private retirement cannot hide public history, demotion, removal, or current public source", async () => {
  const source = await contract(); const published = await publishedFixture(); const publicBaseline = parseBaseline(bytes(published));
  assert.throws(() => planPrivateRetirement(publicBaseline, [source], "minimal"), /private-retirement-public-evidence/);
  assert.throws(() => planPrivateRetirement(publicBaseline, []), /baseline/);
  assert.throws(() => validateBaselineSource(publicBaseline, [{ ...source, exposure: "package" }]), /baseline/);
  const demoted = parseBaseline(planAcceptance(publicBaseline, [{ ...source, exposure: "package" }], { key: "minimal", currentVersion: "1.0.0", nextVersion: "2.0.0", acknowledgeBreakingChange: true }));
  assert.throws(() => planPrivateRetirement(demoted, [], "minimal"), /private-retirement-public-evidence/);
  published.contracts.minimal.current.exposure = "package";
  const privateBaseline = parseBaseline(bytes(published));
  assert.throws(() => planPrivateRetirement(privateBaseline, [source]), /private-retirement-public-evidence/);
  assert.throws(() => validateBaselineSource(privateBaseline, []), /private-baseline-migration-required/);
});
test("archived private evidence cannot be enriched/removed by another baseline writer", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-retired-private-"));
  try {
    const fixture = await publishedFixture(); fixture.contracts.minimal.current.exposure = "package";
    const retired = planPrivateRetirement(parseBaseline(bytes(fixture)), []);
    await writeFile(resolve(root, "sunder-contracts.owned.json"), retired);
    const input = await captureBaseline(root); const originalTime = (await stat(input.path)).mtimeMs;
    assert.equal(await commitBaseline(input, retired, async () => {}), false);
    assert.equal((await stat(input.path)).mtimeMs, originalTime);
    await assert.rejects(commitBaseline(input, writeBaseline("example.owner", new Map()), async () => {}), /private-retirement-mutated/);
    assert.deepEqual(await readFile(input.path), retired);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("portable baseline keys preserve ordinal numeric keys, broader than the lower-kebab frontend", async () => {
  const first = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [await contract()]));
  const entry = first.contracts.get("minimal")!;
  const make = (key: string) => {
    const json = JSON.parse(bytes(sample).toString()); json.contracts = { [key]: structuredClone(sample.contracts.minimal) };
    return parseBaseline(bytes(json)).contracts.get(key)!;
  };
  for (const key of ["Upper_Key", "Mixed.Case-9", "0", "10", "constructor", "toString", "hasOwnProperty", "valueOf"]) assert.ok(make(key));
  const current = entry.current!;
  const secondDescriptor = parseRpcContractDescriptor(JSON.stringify({ ...current.descriptor, contractId: "example.second" }));
  const second = await contract();
  const secondBaseline = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [{ ...second, contractKey: "2", descriptor: secondDescriptor,
    canonicalBytes: Buffer.from(canonicalRpcDescriptorBytes(secondDescriptor)), canonical: Buffer.from(canonicalRpcDescriptorBytes(secondDescriptor)).toString(), abiSha256: await computeRpcAbiSha256(secondDescriptor) }]));
  const output = writeBaseline(first.packageId, new Map([["2", secondBaseline.contracts.get("2")!], ["10", entry]]));
  assert.ok(output.indexOf('"10"') < output.indexOf('"2"')); assert.deepEqual([...parseBaseline(output).contracts.keys()], ["10", "2"]);
});
test("frozen baseline and canonical outer maps use own fields without prototype or ordinal-key loss", async () => {
  for (const key of ["constructor", "toString", "hasOwnProperty", "valueOf"]) {
    const source = { ...await contract(), contractKey: key };
    const draft = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [source]));
    const frozen = parseBaseline(planFreeze(draft, [source], "7.6.5"));
    assert.equal(frozen.contracts.get(key)!.current!.state, "release-frozen");
    assert.deepEqual(planAcceptance(frozen, [source]), frozen.bytes);
    assert.deepEqual(writeBaseline(frozen.packageId, frozen.contracts), frozen.bytes);
  }
  const value = ordered([["10", 10], ["2", 2], ["__proto__", ordered([["constructor", "data"]])], ["constructor", "own"], ["hasOwnProperty", true]]);
  const bytes = outerBytes(value); const parsed = parseOuter(bytes);
  assert.equal(Object.getPrototypeOf(parsed), null); assert.equal(Object.hasOwn(parsed, "__proto__"), true);
  assert.equal(Object.hasOwn(parsed, "constructor"), true); assert.deepEqual(outerBytes(parsed), bytes);
  assert.ok(bytes.indexOf('"10"') < bytes.indexOf('"2"'));
  assert.throws(() => fields(Object.create({ state: "draft" }), ["state"]), /missing/);
  assert.equal(Object.hasOwn(Object.prototype, "state"), false);
});
test("strict outer form rejects Unicode, order, null, BOM, duplicate/colliding and closed fields", async () => {
  const fixture = await publishedFixture(); const original = bytes(fixture); const text = original.toString();
  const invalids = [Buffer.concat([Buffer.from([239, 187, 191]), original]), Buffer.concat([original, Buffer.from("\n")]), Buffer.from(text.replaceAll("\n", "\r\n")), Buffer.from(JSON.stringify(fixture) + "\n"),
    Buffer.from(text.replace('"schemaVersion": 1', '"schemaVersion": 1.0')), Buffer.from(text.replace('"schemaVersion": 1', '"schemaVersion": 1,\n  "SchemaVersion": 1')),
    Buffer.from(text.replace('"history": []', '"history": null')), Buffer.from(text.replace('"authorityKind": "package"', '"authorityKind": "platform"')),
    Buffer.from(text.replace('"schemaVersion": 1', '"schemaVersion": -0')), Buffer.from(text.replace('"registryOrigin": "https://registry.example/"', '"registryOrigin": "\\ud800"')),
    Buffer.from(text.replace('"publishedAtUtc": "2026-09-11T12:34:56.123456789Z"', '"publishedAtUtc": "2026-02-30T12:34:56Z"'))];
  for (const content of invalids) assert.throws(() => parseBaseline(content));
  for (const field of ["selectedAuthorityRelease", "selectedAuthorityContentIdentity", "catalogSigningKeyId", "catalogTrustGeneration", "catalogTrustFingerprint", "storeAuthority", "immutablePayload"]) {
    const value = structuredClone(fixture); value.contracts.minimal.current[field] = "forged"; assert.throws(() => parseBaseline(bytes(value)), /unsupported/);
  }
  const swap = structuredClone(fixture); swap.contracts.minimal.current.publication = { ...swap.contracts.minimal.current.publication, unknown: true }; assert.throws(() => parseBaseline(bytes(swap)), /unsupported/);
  assert.throws(() => parseBaseline(readFileSync(resolve(corpus, "conformance/valid/platform-owned-baseline.json"))), /package authority|unsupported/);
  assert.throws(() => parseBaseline(Buffer.from(text.replace("example.owner", "example.other"))), /mismatch/);
  assert.throws(() => parseBaseline(Buffer.from(text.replace('"state": "published",\n        "exposure": "public"', '"exposure": "public",\n        "state": "published"'))), /order/);
  assert.throws(() => parseBaseline(Buffer.from(text.replace('"minimal": {', '"minimal": {},\n    "Minimal": {'))), /colliding/);
});
test("tampered published history fails exact canonical/count/hash/state/evidence validation", async () => {
  const fixture = await publishedFixture(); const published = fixture.contracts.minimal.current;
  const current = JSON.parse(planAcceptance(parseBaseline(bytes(fixture)), [await contract("1.0.0", "annotation")]).toString());
  const mutations = [
    (value: typeof published) => { value.canonicalDescriptor = value.canonicalDescriptor.replace("Empty value", "Bad value"); },
    (value: typeof published) => { value.descriptorBytes++; }, (value: typeof published) => { value.descriptorSha256 = "b".repeat(64); },
    (value: typeof published) => { value.abiProjectionBytes++; }, (value: typeof published) => { value.canonicalAbiProjection += " "; },
    (value: typeof published) => { value.abiSha256 = "c".repeat(64); }, (value: typeof published) => { value.contractVersion = "9.0.0"; },
    (value: typeof published) => { value.publication.etag = `"${"0".repeat(64)}"`; }, (value: typeof published) => { value.publication.packageVersion = "3.4.6"; },
    (value: typeof published) => { value.packageRelease.packageId = "other.owner"; }, (value: typeof published) => { value.state = "release-frozen"; },
    (value: typeof published) => { delete value.publication; }, (value: typeof published) => { value.publication.registryOrigin = "https://EXAMPLE.com/"; },
    (value: typeof published) => { value.publication.registryOrigin = "https://example.com:443/"; }, (value: typeof published) => { value.publication.packageContentIdentity = "bad"; },
  ];
  for (const mutate of mutations) { const value = structuredClone(current); mutate(value.contracts.minimal.history[0]); assert.throws(() => parseBaseline(bytes(value))); }
  const invalidModel = parseBaseline(bytes(fixture)); const entry = invalidModel.contracts.get("minimal")!;
  assert.throws(() => writeBaseline(invalidModel.packageId, new Map([["minimal", { ...entry, current: { ...entry.current!, state: "draft" } }]])), /model disagrees/);
});
test("all frozen package baseline semantic-invalid fixtures are rejected by the production reader", () => {
  for (const name of ["baseline-authority-mismatch", "baseline-evidence-mismatch", "baseline-hash-mismatch", "baseline-order-mismatch", "baseline-root-mismatch"]) {
    assert.throws(() => parseBaseline(readFileSync(resolve(corpus, "conformance/semantic-invalid", `${name}.json`))), name);
  }
});
test("inclusive 256 immutable-history limit and 128 contract entries never silently truncate", async () => {
  const fixture = await publishedFixture(); const json = fixture.contracts.minimal.current; delete json.publication; json.state = "release-frozen";
  const original = parseRpcContractDescriptor(json.canonicalDescriptor);
  const at = (patch: number) => { const descriptor = Buffer.from(canonicalRpcDescriptorBytes(atVersion(original, `1.0.${patch}`))); return { ...json, contractVersion: `1.0.${patch}`, descriptorBytes: descriptor.length, canonicalDescriptor: descriptor.toString(), descriptorSha256: sha256(descriptor) }; };
  fixture.contracts.minimal.history = Array.from({ length: 256 }, (_, i) => at(i)); fixture.contracts.minimal.current = at(256);
  const baseline = parseBaseline(bytes(fixture)); assert.equal(baseline.contracts.get("minimal")!.history.length, 256);
  assert.deepEqual(writeBaseline(baseline.packageId, baseline.contracts), baseline.bytes);
  assert.throws(() => planAcceptance(baseline, [awaitCandidate()]), /256/);
  function awaitCandidate(): LoadedContract {
    const value = JSON.parse(fixture.contracts.minimal.current.canonicalDescriptor); value.$defs.Empty.title = "Changed";
    const descriptor = parseRpcContractDescriptor(JSON.stringify(value)); const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
    return { modelProjection: emptyModelProjection(), contractKey: "minimal", configuredPath: "rpc.ts", sourcePath: "rpc.ts", descriptorPath: "contracts/minimal.rpc.json", descriptor, canonicalBytes,
      canonical: canonicalBytes.toString(), descriptorSha256: sha256(canonicalBytes), abiSha256: json.abiSha256, exposure: "public" };
  }
  const overflow = structuredClone(sample); overflow.contracts = Object.fromEntries(Array.from({ length: 129 }, (_, i) => [`c${String(i).padStart(3, "0")}`, sample.contracts.minimal]));
  assert.throws(() => parseBaseline(bytes(overflow)), /128/);
});
test("baseline history is stricter than immutable tuple reuse and enforces draft history floors", async () => {
  const source = await contract(); const draft = parseBaseline(planAcceptance(emptyBaseline("example.owner"), [source]));
  const frozen = parseBaseline(planFreeze(draft, [source], "3.4.5"));
  const changed = await contract("1.0.0", "major");
  assert.throws(() => planAcceptance(frozen, [changed]), /acknowledgement/);
  assert.throws(() => planAcceptance(frozen, [changed], { key: "minimal", currentVersion: "1.0.0", nextVersion: "1.1.0", acknowledgeBreakingChange: true }), /bump-required/);
  const major = parseBaseline(planAcceptance(frozen, [changed], { key: "minimal", currentVersion: "1.0.0", nextVersion: "2.0.0", acknowledgeBreakingChange: true }));
  const majorSource = await contract("2.0.0", "major");
  assert.deepEqual(planAcceptance(major, [majorSource]), major.bytes);
  const bad = JSON.parse(major.bytes.toString()); const snapshot = bad.contracts.minimal.current; const descriptor = atVersion(majorSource.descriptor, "1.0.1");
  snapshot.contractVersion = descriptor.version; snapshot.canonicalDescriptor = Buffer.from(canonicalRpcDescriptorBytes(descriptor)).toString(); snapshot.descriptorBytes = Buffer.byteLength(snapshot.canonicalDescriptor); snapshot.descriptorSha256 = sha256(snapshot.canonicalDescriptor);
  assert.throws(() => validateBaselineSource(parseBaseline(bytes(bad)), [{ ...majorSource, descriptor, canonicalBytes: Buffer.from(canonicalRpcDescriptorBytes(descriptor)) }]), /bump-required/);
  const reuse = JSON.parse(major.bytes.toString()); reuse.contracts.minimal.current = structuredClone(reuse.contracts.minimal.history[0]);
  assert.throws(() => parseBaseline(bytes(reuse)), /precedence/);
});
test("streamed baseline read rejects sparse oversized files before allocation and all symbolic-link paths", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-baseline-read-"));
  try {
    const path = resolve(root, "sunder-contracts.owned.json"); const handle = await open(path, "wx"); await handle.truncate(MAX_BASELINE_BYTES + 1); await handle.close();
    await assert.rejects(captureBaseline(root), /source-limit-exceeded/);
    await rm(path); const target = resolve(root, "target.json"); await writeFile(target, emptyBaseline("example.owner").bytes); await symlink(target, path);
    await assert.rejects(captureBaseline(root), /regular file/);
    await rm(path); const alias = resolve(root, "alias"); await symlink(root, alias); await assert.rejects(captureBaseline(alias), /symbolic links/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("outer depth and preparse budget bound malformed input without underbudgeting valid schema maximum", () => {
  assert.throws(() => parseOuter(Buffer.from("[".repeat(17) + "]".repeat(17) + "\n")), /depth/);
  assert.throws(() => parseOuter(Buffer.from("[" + "true,".repeat(2_097_152) + "true]\n")), /structural budget/);
  // Full valid maximum structural shape, using opaque small strings to keep this
  // resource-budget test independent of descriptor validation and hashing cost.
  const snapshot = Object.fromEntries(["state", "exposure", "contractVersion", "descriptorBytes", "canonicalDescriptor", "descriptorSha256", "abiProjectionBytes", "canonicalAbiProjection", "abiSha256"].map((name) => [name, "x"]));
  Object.assign(snapshot, { packageRelease: { packageId: "x", packageVersion: "x" }, publication: Object.fromEntries(["registryOrigin", "packageId", "packageVersion", "packageContentIdentity", "publishedAtUtc", "etag"].map((key) => [key, "x"])) });
  const value = { schemaVersion: 1, authorityKind: "package", authorityId: "example.owner", packageId: "example.owner", contracts: Object.fromEntries(Array.from({ length: 128 }, (_, i) => [`c${i}`, { contractId: "x", current: snapshot, history: Array(256).fill(snapshot) }])) };
  assert.doesNotThrow(() => parseOuter(bytes(value)));
  const invalid = structuredClone(sample); invalid.contracts.minimal.history = Array(257).fill(sample.contracts.minimal.current); assert.throws(() => parseBaseline(bytes(invalid)), /256/);
});
test("baseline lease never sweeps tracked neighbors, preserves no-op mtime and rejects a waiting stale writer", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-baseline-write-")); const proposal = emptyBaseline("example.owner").bytes;
  try {
    const input = await captureBaseline(root); const neighbor = `${input.path}.stage-user-work`; const backup = `${input.path}.backup-user-work`;
    await mkdir(neighbor); await writeFile(resolve(neighbor, "keep"), "user work"); await writeFile(backup, "user backup");
    assert.equal(await commitBaseline(input, proposal, async () => {}), true);
    const written = await captureBaseline(root); const before = await stat(input.path);
    assert.equal(await commitBaseline(written, proposal, async () => {}), false); assert.equal((await stat(input.path)).mtimeMs, before.mtimeMs);
    let completion!: Promise<boolean>;
    await withOutputLeases([input.path], async () => {
      const contender = startOutputLockContender(input.path, () => commitBaseline(written, proposal, async () => {}), () => "baseline stale writer");
      completion = contender.completion; void completion.catch(() => undefined); await contender.attempting;
      await writeFile(input.path, emptyBaseline("example.changed").bytes);
    });
    await assert.rejects(completion, /changed after planning/);
    assert.equal(await readFile(resolve(neighbor, "keep"), "utf8"), "user work"); assert.equal(await readFile(backup, "utf8"), "user backup");
    assert.equal((await readdir(root)).filter((name) => name.startsWith(".sunder-contract-stage-")).length, 0);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("writer rechecks final stage bytes and cleans only a current byte-proven stage", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-baseline-stage-"));
  try {
    const input = await captureBaseline(root); let calls = 0; let stage = "";
    await assert.rejects(commitBaseline(input, emptyBaseline("example.owner").bytes, async () => {
      if (++calls !== 2) return;
      stage = resolve(root, (await readdir(root)).find((name) => name.startsWith(".sunder-contract-stage-"))!);
      await writeFile(stage, "changed stage must survive");
    }), /stage|Staged/);
    assert.equal(await readFile(stage, "utf8"), "changed stage must survive"); await assert.rejects(readFile(input.path), { code: "ENOENT" });
  } finally { await rm(root, { recursive: true, force: true }); }
});
